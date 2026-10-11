using System.Buffers.Binary;
using System.Collections.Specialized;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Waterfall;

/// <summary>
/// The local channel audio stream (issue #584): loopback only, no key, refused from anything
/// that declares an <c>Origin</c> or came through a proxy, the channel's own rate in 100 ms
/// blocks, keyed blocks marked rather than missing, lost audio marked with a gap, and several
/// readers at once.
/// </summary>
public class ChannelAudioStreamTests : IAsyncLifetime
{
    private const int SampleRate = 12000;
    private const int Block = SampleRate / 10;

    private readonly SoundModemChannel _channel;
    private readonly WaterfallWebServer _server;
    private readonly int _port;
    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(60));

    public ChannelAudioStreamTests()
    {
        _channel = new SoundModemChannel(SampleRate, randomSeed: 11);
        _channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        _port = FreePorts.Next();
        _server = new WaterfallWebServer(_channel, _port);
    }

    public ValueTask InitializeAsync()
    {
        _server.Start();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        _cancellation.Dispose();
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("10.0.0.5", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("8.8.8.8", false)]
    public void Is_Loopback_Address_Accepts_Only_Loopback(string address, bool expected) =>
        ChannelAudioStream.IsLoopbackAddress(IPAddress.Parse(address)).Should().Be(expected);

    [Fact]
    public void Is_Loopback_Address_Refuses_No_Address_At_All() =>
        ChannelAudioStream.IsLoopbackAddress(null).Should().BeFalse("no remote address is not trusted as loopback");

    [Theory]
    [InlineData("X-Forwarded-For", "203.0.113.9")]
    [InlineData("Forwarded", "for=203.0.113.9")]
    [InlineData("X-Real-IP", "203.0.113.9")]
    [InlineData("Origin", "http://example.com")]
    public void The_Local_Program_Check_Refuses_A_Proxied_Or_Browser_Request_From_Loopback(string header, string value)
    {
        var headers = new NameValueCollection { [header] = value };
        ChannelAudioStream.LocalProgramRefusal(IPAddress.Loopback, headers).Should().NotBeNull(
            "a reverse proxy on this machine makes everything it relays arrive from loopback");
    }

    [Fact]
    public void The_Local_Program_Check_Passes_A_Plain_Loopback_Request() =>
        ChannelAudioStream.LocalProgramRefusal(IPAddress.Loopback, []).Should().BeNull();

    [Fact]
    public async Task Refuses_A_Websocket_Upgrade_That_Declares_An_Origin()
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", "http://example.com");

        Func<Task> connect = () => socket.ConnectAsync(StreamUri(_port), _cancellation.Token);

        await connect.Should().ThrowAsync<WebSocketException>(
            "a browser sets Origin itself and script cannot remove it, so this is exactly "
            + "the one thing that must never be served");
    }

    [Theory]
    [InlineData("X-Forwarded-For", "203.0.113.9")]
    [InlineData("Forwarded", "for=203.0.113.9")]
    [InlineData("X-Real-IP", "203.0.113.9")]
    public async Task Refuses_A_Websocket_Upgrade_Relayed_By_A_Proxy(string header, string value)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader(header, value);

        Func<Task> connect = () => socket.ConnectAsync(StreamUri(_port), _cancellation.Token);

        await connect.Should().ThrowAsync<WebSocketException>(
            "through a reverse proxy anything on the LAN would arrive from 127.0.0.1 with no Origin");
    }

    [Fact]
    public async Task Refuses_A_Websocket_Upgrade_From_An_Address_That_Is_Not_Loopback()
    {
        IPAddress? lan = NetworkInterface.GetAllNetworkInterfaces()
            .Where(i => i.OperationalStatus == OperationalStatus.Up
                && i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(i => i.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        Assert.SkipWhen(lan is null, "this machine has no address other than loopback to connect from");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 17);
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port, bind: "*");
        server.Start();

        // Reachable over the LAN address: the page itself is served there.
        using (var http = new HttpClient())
        {
            HttpResponseMessage page = await http.GetAsync(new Uri($"http://{lan}:{port}/"), _cancellation.Token);
            page.IsSuccessStatusCode.Should().BeTrue("the server does listen on that address");
        }

        using var socket = new ClientWebSocket();
        Func<Task> connect = () => socket.ConnectAsync(
            new Uri($"ws://{lan}:{port}{ChannelAudioStream.Path}"), _cancellation.Token);

        await connect.Should().ThrowAsync<WebSocketException>(
            "the stream is for a program on this machine, whatever the station's bind is");
    }

    [Fact]
    public async Task Accepts_A_Loopback_Connection_With_No_Origin_And_Says_The_Rate()
    {
        using var socket = await ConnectAsync();

        (WebSocketMessageType kind, byte[] payload) = await Receive(socket);
        kind.Should().Be(WebSocketMessageType.Text);
        using JsonDocument hello = JsonDocument.Parse(payload);
        hello.RootElement.GetProperty("type").GetString().Should().Be("hello");
        hello.RootElement.GetProperty("rateHz").GetInt32().Should().Be(SampleRate);
        hello.RootElement.GetProperty("dialHz").ValueKind.Should().Be(
            JsonValueKind.Null, "this station was never given a dial");
    }

    [Fact]
    public async Task The_Hello_Is_The_First_Message_Even_While_Audio_Is_Flowing()
    {
        using var feeding = new CancellationTokenSource();
        Task feeder = Task.Run(
            () =>
            {
                float[] block = new float[240];
                while (!feeding.IsCancellationRequested)
                {
                    _channel.ProcessReceive(block);
                }
            },
            CancellationToken.None);

        try
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                using var socket = await ConnectAsync();
                (WebSocketMessageType kind, byte[] payload) = await Receive(socket);
                kind.Should().Be(WebSocketMessageType.Text, "the hello must come before any audio");
                Encoding.UTF8.GetString(payload).Should().Contain("\"hello\"");
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, _cancellation.Token);
            }
        }
        finally
        {
            await feeding.CancelAsync();
            await feeder;
        }
    }

    [Fact]
    public async Task Says_The_Dial_In_The_Hello_When_The_Station_Knows_One()
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 13);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(
            channel, port, new WaterfallOptions { DialFrequencyHz = 7052000 });
        server.Start();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(StreamUri(port), _cancellation.Token);
        (_, byte[] payload) = await Receive(socket);
        using JsonDocument hello = JsonDocument.Parse(payload);
        hello.RootElement.GetProperty("dialHz").GetDouble().Should().Be(7052000);
    }

    [Fact]
    public async Task Streams_Received_Audio_As_Float32_Blocks_With_A_Sample_Counter()
    {
        using var socket = await ConnectAsync();
        await Receive(socket); // hello

        float[] tone = Tone(Block, 1800);
        _channel.ProcessReceive(tone);

        (WebSocketMessageType kind, byte[] payload) = await Receive(socket);
        kind.Should().Be(WebSocketMessageType.Binary);
        payload[0].Should().Be((byte)0x01, "the audio-block message kind");
        payload[1].Should().Be((byte)0, "neither transmitted nor gap on a first, ordinary block");
        payload[2].Should().Be((byte)0);
        payload[3].Should().Be((byte)0);
        AudioBlock block = Parse(payload);
        block.Samples.Length.Should().Be(tone.Length);
        payload.Length.Should().Be(16 + (tone.Length * 4));
        block.Samples.Should().Equal(tone, "every sample comes through unchanged");
    }

    [Fact]
    public async Task Short_Channel_Blocks_Are_Gathered_Into_100_Ms_Blocks()
    {
        using var socket = await ConnectAsync();
        await Receive(socket); // hello

        // An ARDOP station reads 20 ms at a time.
        float[] tone = Tone(Block * 3, 1500);
        for (int offset = 0; offset < tone.Length; offset += SampleRate / 50)
        {
            _channel.ProcessReceive(tone.AsSpan(offset, SampleRate / 50));
        }

        var received = new List<float>();
        ulong? first = null;
        for (int i = 0; i < 3; i++)
        {
            AudioBlock block = Parse((await Receive(socket)).Payload);
            block.Samples.Length.Should().Be(Block, "a block is a tenth of a second whatever the channel reads");
            block.Flags.Should().Be(0);
            first ??= block.Index;
            block.Index.Should().Be(first.Value + (ulong)(i * Block));
            received.AddRange(block.Samples);
        }

        received.Should().Equal(tone);
    }

    [Fact]
    public async Task Marks_A_Block_Skipped_For_A_Keyup_As_Transmitted_Silence_With_A_Continuous_Index()
    {
        using var socket = await ConnectAsync();
        await Receive(socket); // hello

        _channel.ProcessReceive(Tone(600, 1200));
        await KeyAndFeedAsync(keyedBlocks: 5, keyedLength: 240); // 1200 samples while keyed
        _channel.ProcessReceive(Tone(Block, 1200));

        // 600 heard, then 1200 keyed, then 1200 heard: the gathered blocks split exactly where
        // the transmitted flag changes, and nowhere else.
        List<AudioBlock> blocks = await ReceiveSamples(socket, 600 + Block + Block);
        blocks.Select(b => (b.Samples.Length, b.Flags)).Should().Equal(
            (600, (byte)0), (Block, (byte)1), (Block, (byte)0));
        blocks[1].Samples.Should().OnlyContain(s => s == 0f, "a keyed block carries silence, never our own audio");
        for (int i = 1; i < blocks.Count; i++)
        {
            blocks[i].Index.Should().Be(
                blocks[i - 1].Index + (ulong)blocks[i - 1].Samples.Length,
                "the sample index runs straight through a keyup");
        }
    }

    [Fact]
    public async Task Audio_The_Input_Lost_Marks_The_Next_Block_On_Every_Connection_Without_Moving_The_Index()
    {
        using var first = await ConnectAsync();
        using var second = await ConnectAsync();
        await Receive(first);  // hello
        await Receive(second); // hello

        _channel.ProcessReceive(Tone(600, 1000));
        _channel.NoteReceiveAudioLost();
        _channel.ProcessReceive(Tone(Block, 1000));
        _channel.ProcessReceive(Tone(Block, 1000));

        foreach (ClientWebSocket socket in new[] { first, second })
        {
            List<AudioBlock> blocks = await ReceiveSamples(socket, 600 + Block + Block);
            blocks.Select(b => (b.Samples.Length, b.Flags)).Should().Equal(
                [(600, (byte)0), (Block, (byte)2), (Block, (byte)0)],
                "what was gathered goes out before the hole, and the block after it is marked once");
            blocks[1].Index.Should().Be(
                blocks[0].Index + 600,
                "nobody knows how much the input lost, so the index counts what was delivered");
        }
    }

    [Fact]
    public async Task Several_Readers_Each_Get_The_Same_Audio()
    {
        using var first = await ConnectAsync();
        using var second = await ConnectAsync();
        await Receive(first);  // hello
        await Receive(second); // hello

        float[] tone = Tone(Block, 1200);
        _channel.ProcessReceive(tone);

        (_, byte[] a) = await Receive(first);
        (_, byte[] b) = await Receive(second);
        a.Should().Equal(b, "every connected reader hears the same block");
    }

    [Fact]
    public async Task A_Slow_Reader_Gets_A_Gap_While_A_Second_Reader_On_The_Same_Station_Loses_Nothing()
    {
        // 48 kHz and no modem: big blocks, cheap to make, so the slow reader's socket buffers and
        // then its queue fill in a few seconds.
        const int rate = 48000;
        const int block = rate / 10;
        const int blocks = 1500; // about 29 MB, well past what loopback buffers hold
        var channel = new SoundModemChannel(rate, randomSeed: 19);
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        using var slow = new ClientWebSocket();
        using var fast = new ClientWebSocket();
        await slow.ConnectAsync(StreamUri(port), _cancellation.Token);
        await fast.ConnectAsync(StreamUri(port), _cancellation.Token);
        await Receive(slow); // hello; then it reads nothing until the end
        await Receive(fast); // hello

        int fastBlocks = 0;
        bool fastSawGap = false;
        ulong expected = ulong.MaxValue;
        bool fastContinuous = true;
        Task fastReader = Task.Run(async () =>
        {
            var buffer = new byte[64 * 1024];
            while (Volatile.Read(ref fastBlocks) < blocks)
            {
                (_, byte[] payload) = await Receive(fast, buffer);
                AudioBlock got = Parse(payload);
                fastSawGap |= (got.Flags & 2) != 0;
                if (expected != ulong.MaxValue && got.Index != expected)
                {
                    fastContinuous = false;
                }

                expected = got.Index + (ulong)got.Samples.Length;
                Interlocked.Increment(ref fastBlocks);
            }
        });

        float[] audio = new float[block];
        for (int i = 0; i < blocks; i++)
        {
            audio[0] = i;
            channel.ProcessReceive(audio);

            // Paced by the fast reader, never by the slow one: it is never more than a few
            // blocks behind, so its own queue never overflows.
            while (i - Volatile.Read(ref fastBlocks) > 16)
            {
                await Task.Delay(1, _cancellation.Token);
            }
        }

        await fastReader.WaitAsync(_cancellation.Token);
        fastSawGap.Should().BeFalse("a reader that keeps up loses nothing, whatever another reader does");
        fastContinuous.Should().BeTrue("and its sample index never jumps");

        // Now drain the slow one: somewhere it lost blocks, and the first block after the hole
        // says so, with the index jumping by exactly what it lost.
        var slowBuffer = new byte[64 * 1024];
        ulong previousEnd = ulong.MaxValue;
        bool sawGap = false;
        while (!sawGap)
        {
            AudioBlock got = Parse((await Receive(slow, slowBuffer)).Payload);
            if ((got.Flags & 2) != 0)
            {
                sawGap = true;
                got.Index.Should().BeGreaterThan(previousEnd, "whole blocks were dropped before this one");
                ((got.Index - previousEnd) % block).Should().Be(0UL, "only whole blocks are ever dropped");
            }
            else if (previousEnd != ulong.MaxValue)
            {
                got.Index.Should().Be(previousEnd, "nothing is missing before the marked block");
            }

            previousEnd = got.Index + (ulong)got.Samples.Length;
        }
    }

    [Fact]
    public async Task A_Disconnecting_Client_Is_Dropped_From_The_Fan_Out()
    {
        var socket = await ConnectAsync();
        await Receive(socket); // hello
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, _cancellation.Token);
        socket.Dispose();

        await WaitUntil(() => Stream().ClientCount == 0, "the dropped client must be removed, not leaked");
    }

    [Fact]
    public async Task A_Reader_That_Names_Itself_Is_Listed_With_Its_Page_Port_Until_It_Goes()
    {
        var receiver = await ConnectAsync();
        await Receive(receiver); // hello
        using var recorder = await ConnectAsync();
        await Receive(recorder); // hello
        using var anonymous = await ConnectAsync();
        await Receive(anonymous); // hello, and nothing more: a reader need not say who it is

        await SendText(receiver, """{"name":"pdn-mailcast-receiver","pagePort":8130}""");
        await SendText(recorder, """{"name":"recorder"}""");

        await WaitUntil(() => Stream().Listeners.Count == 2, "both named readers are listed");
        Stream().Listeners.Should().BeEquivalentTo(
            [new Listener("pdn-mailcast-receiver", 8130), new Listener("recorder", null)],
            "a reader that gave no page port is listed without one, and one that gave no name not at all");

        await receiver.CloseAsync(WebSocketCloseStatus.NormalClosure, null, _cancellation.Token);
        receiver.Dispose();

        await WaitUntil(() => Stream().Listeners.Count == 1, "a reader that goes is taken off the list");
        Stream().Listeners.Should().Equal(new Listener("recorder", null));
    }

    [Theory]
    [InlineData("  pdn-mailcast-receiver  ", "pdn-mailcast-receiver")]
    [InlineData("bell\u0007 and\nnewline", "bell andnewline")]
    [InlineData("caf\u00e9", "caf")]
    [InlineData("\u00e9\u00e9", null)]
    [InlineData("   ", null)]
    public void A_Name_Is_Kept_To_Printable_Ascii(string escaped, string? expected) =>
        ChannelAudioStream.CleanName(System.Text.RegularExpressions.Regex.Unescape(escaped)).Should().Be(expected);

    [Fact]
    public void A_Long_Name_Is_Cut_To_The_Cap() =>
        ChannelAudioStream.CleanName(new string('x', 200)).Should().HaveLength(ChannelAudioStream.MaxNameLength);

    [Fact]
    public async Task Asking_For_A_Band_Fires_The_Hook_And_Clearing_It_Fires_Null()
    {
        var seen = new List<(int LowHz, int HighHz)?>();
        _server.ReceiveBandRequested = requested => { lock (seen) { seen.Add(requested); } };

        var socket = await ConnectAsync();
        await Receive(socket); // hello

        await SendText(socket, """{"name":"test","band":{"lowHz":1000,"highHz":2000}}""");

        await WaitUntil(() => Count(seen) >= 1, "the identify message must trigger the band hook");
        Last(seen).Should().Be((1000, 2000));

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, _cancellation.Token);
        socket.Dispose();

        await WaitUntil(() => Count(seen) >= 2 && Last(seen) is null,
            "no connection asking for a band any more must fire null");
    }

    [Fact]
    public async Task A_Band_Asked_For_Before_The_Hook_Is_Set_Reaches_It_When_It_Is()
    {
        // The page server starts before the device opens, so a program can connect and ask
        // before the Flex installs its handler.
        using var socket = await ConnectAsync();
        await Receive(socket); // hello
        await SendText(socket, """{"band":{"lowHz":500,"highHz":5000}}""");
        await WaitUntil(() => FirstClientBand() is not null, "the band is recorded");

        var seen = new List<(int LowHz, int HighHz)?>();
        _server.ReceiveBandRequested = requested => { lock (seen) { seen.Add(requested); } };

        Count(seen).Should().Be(1, "installing the handler hands it the band already asked for");
        Last(seen).Should().Be((500, 5000));
    }

    [Fact]
    public async Task A_Band_Is_Clamped_To_What_The_Channel_Can_Carry()
    {
        var seen = new List<(int LowHz, int HighHz)?>();
        _server.ReceiveBandRequested = requested => { lock (seen) { seen.Add(requested); } };
        using var socket = await ConnectAsync();
        await Receive(socket); // hello

        await SendText(socket, """{"band":{"lowHz":-300,"highHz":20000}}""");

        await WaitUntil(() => Count(seen) >= 1, "a band partly outside the channel is still a band");
        Last(seen).Should().Be((0, SampleRate / 2));
    }

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("\"band\"")]
    [InlineData("42")]
    [InlineData("""{"name":7,"pagePort":"x","band":{"lowHz":"1000","highHz":2000}}""")]
    [InlineData("""{"band":"wide"}""")]
    [InlineData("""{"band":{"lowHz":3000,"highHz":1000}}""")]
    [InlineData("{not json")]
    public async Task An_Identify_Message_It_Does_Not_Understand_Is_Ignored_And_The_Audio_Keeps_Coming(string message)
    {
        var seen = new List<(int LowHz, int HighHz)?>();
        _server.ReceiveBandRequested = requested => { lock (seen) { seen.Add(requested); } };
        using var socket = await ConnectAsync();
        await Receive(socket); // hello

        await SendText(socket, message);
        await SendText(socket, """{"band":{"lowHz":1000,"highHz":2000}}""");
        await WaitUntil(() => Count(seen) >= 1, "a good message after a bad one is still read");
        Last(seen).Should().Be((1000, 2000));
        Count(seen).Should().Be(1, "the bad message asked for nothing");

        _channel.ProcessReceive(Tone(Block, 900));
        (WebSocketMessageType kind, _) = await Receive(socket);
        kind.Should().Be(WebSocketMessageType.Binary, "the connection is still open and streaming");
    }

    [Fact]
    public async Task A_Fragmented_Identify_Message_Is_Read_Whole()
    {
        var seen = new List<(int LowHz, int HighHz)?>();
        _server.ReceiveBandRequested = requested => { lock (seen) { seen.Add(requested); } };
        using var socket = await ConnectAsync();
        await Receive(socket); // hello

        byte[] whole = Encoding.UTF8.GetBytes("""{"name":"split","band":{"lowHz":700,"highHz":2300}}""");
        await socket.SendAsync(whole.AsMemory(0, 10), WebSocketMessageType.Text, false, _cancellation.Token);
        await socket.SendAsync(whole.AsMemory(10, 20), WebSocketMessageType.Text, false, _cancellation.Token);
        await socket.SendAsync(whole.AsMemory(30), WebSocketMessageType.Text, true, _cancellation.Token);

        await WaitUntil(() => Count(seen) >= 1, "the pieces make one message");
        Last(seen).Should().Be((700, 2300));
    }

    [Fact]
    public async Task An_Identify_Message_Over_The_Cap_Is_Ignored_Whole()
    {
        var seen = new List<(int LowHz, int HighHz)?>();
        _server.ReceiveBandRequested = requested => { lock (seen) { seen.Add(requested); } };
        using var socket = await ConnectAsync();
        await Receive(socket); // hello

        string padding = new(' ', ChannelAudioStream.MaxIdentifyBytes);
        await SendText(socket, "{\"band\":{\"lowHz\":100,\"highHz\":200}," + padding + "\"name\":\"long\"}");
        await SendText(socket, """{"band":{"lowHz":1000,"highHz":2000}}""");

        await WaitUntil(() => Count(seen) >= 1, "the message after the long one is read");
        Count(seen).Should().Be(1);
        Last(seen).Should().Be((1000, 2000), "nothing from the over-long message was applied");
    }

    [Fact]
    public void A_Queue_Marks_The_Next_Block_With_A_Gap_After_Dropping_The_Oldest()
    {
        var queue = new ChannelAudioStream.AudioQueue(2, 1);
        queue.Enqueue(0, [1f], 0);
        queue.Enqueue(1, [2f], 0);
        queue.Enqueue(2, [3f], 0); // queue full: drops index 0

        byte[] message = new byte[20];
        queue.TryDequeue(message).Should().Be(20);
        AudioBlock first = Parse(message);
        first.Index.Should().Be(1UL, "the oldest block was dropped to make room");
        first.Flags.Should().Be(2, "the block right after a drop carries the gap flag");

        queue.TryDequeue(message).Should().Be(20);
        AudioBlock second = Parse(message);
        second.Index.Should().Be(2UL);
        second.Flags.Should().Be(0, "only the block right after the drop is marked, not every block after it");
        queue.TryDequeue(message).Should().Be(0, "nothing is left");
    }

    [Fact]
    public async Task The_Receive_Path_Allocates_Nothing_Once_A_Reader_Is_Connected()
    {
        using var socket = await ConnectAsync();
        await Receive(socket); // hello
        ChannelAudioStream stream = Stream();
        float[] audio = Tone(240, 1000);

        // Warm up: the first pass through anything may allocate once.
        for (int i = 0; i < 20; i++)
        {
            stream.OnReceive(audio);
            stream.OnKeyedBlock(240);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
        {
            stream.OnReceive(audio);
            stream.OnKeyedBlock(240);
            stream.OnInputLost();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        allocated.Should().Be(0, "the receive thread copies into buffers it already has");
    }

    private async Task KeyAndFeedAsync(int keyedBlocks, int keyedLength)
    {
        _channel.Csma.Persistence = 255; // never defer: the keyup below is the test's own
        var recorder = new RecordingOutput(SampleRate);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool fed = false;
        _channel.TransmittingChanged += transmitting =>
        {
            if (transmitting && !fed)
            {
                // ProcessReceive is still called while keyed in the real daemon (the capture loop
                // never stops because PTT is up), so these are exactly the blocks the channel's
                // half-duplex gate would otherwise drop with nothing to show for them.
                fed = true;
                for (int i = 0; i < keyedBlocks; i++)
                {
                    _channel.ProcessReceive(new float[keyedLength]);
                }
            }
            else if (!transmitting)
            {
                ended.TrySetResult();
            }
        };

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task completion = _channel.EnqueueTransmit(0, Frame());
        Task transmitter = _channel.RunTransmitterAsync(recorder, recorder, cancellation.Token);
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await completion.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<ClientWebSocket> ConnectAsync()
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(StreamUri(_port), _cancellation.Token);
        return socket;
    }

    private static Uri StreamUri(int port) => new($"ws://127.0.0.1:{port}{ChannelAudioStream.Path}");

    private Task SendText(ClientWebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, _cancellation.Token);

    private ChannelAudioStream Stream() =>
        (ChannelAudioStream)typeof(WaterfallWebServer)
            .GetField("_channelAudioStream", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(_server)!;

    private (int LowHz, int HighHz)? FirstClientBand()
    {
        ChannelAudioStream stream = Stream();
        var clients = (ChannelAudioStream.Client[])typeof(ChannelAudioStream)
            .GetField("_clients", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(stream)!;
        return clients.Length == 0 ? null : clients[0].Band;
    }

    private static int Count(List<(int LowHz, int HighHz)?> seen)
    {
        lock (seen)
        {
            return seen.Count;
        }
    }

    private static (int LowHz, int HighHz)? Last(List<(int LowHz, int HighHz)?> seen)
    {
        lock (seen)
        {
            return seen[^1];
        }
    }

    private static float[] Tone(int samples, double hz)
    {
        var tone = new float[samples];
        for (int n = 0; n < samples; n++)
        {
            tone[n] = 0.25f * MathF.Sin(2 * MathF.PI * (float)hz * n / SampleRate);
        }

        return tone;
    }

    private static byte[] Frame()
    {
        byte[] frame = new byte[24];
        byte[] header = [0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0];
        header.CopyTo(frame, 0);
        return frame;
    }

    private static async Task WaitUntil(Func<bool> condition, string because)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }

        condition().Should().BeTrue(because);
    }

    private readonly record struct AudioBlock(byte Flags, ulong Index, float[] Samples);

    private static AudioBlock Parse(byte[] payload)
    {
        int count = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(12, 4));
        var samples = new float[count];
        for (int i = 0; i < count; i++)
        {
            samples[i] = BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(16 + (i * 4), 4));
        }

        return new AudioBlock(payload[1], BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(4, 8)), samples);
    }

    /// <summary>Reads audio blocks until they hold this many samples between them.</summary>
    private async Task<List<AudioBlock>> ReceiveSamples(ClientWebSocket socket, int samples)
    {
        var blocks = new List<AudioBlock>();
        int total = 0;
        while (total < samples)
        {
            (WebSocketMessageType kind, byte[] payload) = await Receive(socket);
            kind.Should().Be(WebSocketMessageType.Binary);
            AudioBlock block = Parse(payload);
            blocks.Add(block);
            total += block.Samples.Length;
        }

        return blocks;
    }

    private Task<(WebSocketMessageType Kind, byte[] Payload)> Receive(ClientWebSocket socket) =>
        Receive(socket, new byte[64 * 1024]);

    private async Task<(WebSocketMessageType Kind, byte[] Payload)> Receive(ClientWebSocket socket, byte[] buffer)
    {
        int filled = 0;
        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(
                new ArraySegment<byte>(buffer, filled, buffer.Length - filled), _cancellation.Token);
            filled += result.Count;
            if (result.EndOfMessage)
            {
                return (result.MessageType, buffer[..filled]);
            }
        }
    }

    private sealed class RecordingOutput(int sampleRate) : IAudioOutput, IPttControl
    {
        public int SampleRate { get; } = sampleRate;

        public void Write(ReadOnlySpan<float> samples)
        {
        }

        public void Drain()
        {
        }

        public void Key()
        {
        }

        public void Unkey()
        {
        }
    }
}
