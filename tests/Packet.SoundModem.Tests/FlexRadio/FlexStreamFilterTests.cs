using System.Net.WebSockets;
using System.Text;
using M0LTE.Flex;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.FlexRadio;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.FlexRadio;

/// <summary>
/// The channel audio stream's band on a Flex (issue #584), through the real
/// <see cref="FlexStationDevice"/> against <c>flex:mock</c>: a headless slice's receive filter is
/// widened to cover what a connection asks for and put back to exactly what bring-up set when
/// the last asking connection goes, however it goes; a band already inside the filter writes
/// nothing; and attach mode never touches the filter at all.
/// </summary>
public sealed class FlexStreamFilterTests : IAsyncDisposable
{
    private const int DspRate = 12000;

    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(60));
    private readonly SoundModemChannel _channel = new(DspRate, randomSeed: 5);
    private readonly WaterfallWebServer _server;
    private readonly int _port = FreePorts.Next();
    private DeviceOpening? _opening;

    public FlexStreamFilterTests()
    {
        _server = new WaterfallWebServer(_channel, _port);
        _server.Start();
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        _opening?.FlexMeters?.Dispose();
        if (_opening?.Flex is FlexRuntime flex)
        {
            await flex.DisposeAsync();
        }

        _cancellation.Dispose();
    }

    [Fact]
    public async Task A_Headless_Slice_Is_Widened_For_A_Band_And_Put_Back_When_The_Reader_Closes()
    {
        MockFlexRadio mock = await OpenAsync("flex:mock", baseline: (300, 2700));
        mock.SliceFilter.Should().Be((300, 2700), "bring-up set the baseline");

        using var reader = await ConnectAsync();
        await AskForBand(reader, 1000, 5000);
        await Until(() => mock.SliceFilter == (300, 5200), "the slice must open to cover the band, with margin");
        Filts(mock).Should().Contain($"filt {Slice()} 300 5200");

        await reader.CloseAsync(WebSocketCloseStatus.NormalClosure, null, _cancellation.Token);
        await Until(() => mock.SliceFilter == (300, 2700), "the last asking reader went, so the filter goes back");
        Filts(mock)[^1].Should().Be($"filt {Slice()} 300 2700", "back to exactly what bring-up set");
    }

    [Fact]
    public async Task The_Filter_Is_Put_Back_When_The_Reader_Drops_Abruptly()
    {
        MockFlexRadio mock = await OpenAsync("flex:mock", baseline: (300, 2700));

        var reader = await ConnectAsync();
        await AskForBand(reader, 500, 4000);
        await Until(() => mock.SliceFilter == (300, 4200), "widened");

        reader.Abort(); // no close handshake: the process on the other end died
        reader.Dispose();

        await Until(() => mock.SliceFilter == (300, 2700), "a reader that vanished is not asking any more");
    }

    [Fact]
    public async Task The_Filter_Stays_Open_Until_The_Last_Asking_Reader_Goes()
    {
        MockFlexRadio mock = await OpenAsync("flex:mock", baseline: (300, 2700));

        using var wide = await ConnectAsync();
        using var narrow = await ConnectAsync();
        using var silent = await ConnectAsync(); // asks for nothing, never counts
        await AskForBand(wide, 1000, 5500);
        await AskForBand(narrow, 200, 3500);
        await Until(() => mock.SliceFilter == (0, 5700), "the union of both bands");

        await wide.CloseAsync(WebSocketCloseStatus.NormalClosure, null, _cancellation.Token);
        await Until(() => mock.SliceFilter == (0, 3700), "narrowed to the reader still asking");

        await narrow.CloseAsync(WebSocketCloseStatus.NormalClosure, null, _cancellation.Token);
        await Until(() => mock.SliceFilter == (300, 2700), "the reader left asks for no band");
    }

    [Fact]
    public async Task A_Band_Already_Inside_The_Filter_Writes_Nothing()
    {
        // GB7RDG's node: bring-up sets 150-5700 Hz, and a reader asking for a band already
        // inside it must leave the radio alone.
        MockFlexRadio mock = await OpenAsync("flex:mock", baseline: (150, 5700));
        int before = Filts(mock).Count;

        using var reader = await ConnectAsync();
        await AskForBand(reader, 1000, 2000);
        await Until(() => BandsAsked() == (1000, 2000), "the band was read");
        await reader.CloseAsync(WebSocketCloseStatus.NormalClosure, null, _cancellation.Token);
        await Until(() => _server.ReceiveBandRequested is not null && BandsAsked() is null, "and withdrawn");
        await Task.Delay(300);

        Filts(mock).Count.Should().Be(before, "nothing to widen, and so nothing to put back");
        mock.SliceFilter.Should().Be((150, 5700));
    }

    [Fact]
    public async Task A_Band_Asked_For_Before_The_Radio_Opens_Is_Applied_When_It_Does()
    {
        // The page server starts before the device opens, so a reader can be first.
        using var reader = await ConnectAsync();
        await AskForBand(reader, 1000, 5000);
        await Until(() => BandsAsked() == (1000, 5000), "the band was read");

        MockFlexRadio mock = await OpenAsync("flex:mock", baseline: (300, 2700));

        await Until(() => mock.SliceFilter == (300, 5200), "the waiting band is applied as soon as the radio is up");
    }

    [Fact]
    public async Task Attach_Mode_Never_Sends_A_Filter_Command()
    {
        MockFlexRadio mock = await OpenAsync("flex:mock@pdn", baseline: null);
        _server.ReceiveBandRequested.Should().BeNull("in attach mode the slice and its filter are SmartSDR's");
        int before = Filts(mock).Count;

        using var reader = await ConnectAsync();
        await AskForBand(reader, 1000, 5000);
        await Until(() => BandsAsked() == (1000, 5000), "the band was read");
        await Task.Delay(300);

        Filts(mock).Count.Should().Be(before, "attach mode leaves the operator's filter alone");
    }

    [Fact]
    public async Task Attach_Mode_Installs_Nothing_Even_With_A_Filter_It_Could_Widen()
    {
        // The mock's attach bring-up reads back no filter, so the test above would pass on that
        // alone; this one hands the attach station a baseline and checks the mode itself is what
        // says no.
        await OpenAsync("flex:mock@pdn", baseline: null);
        FlexDevice.FlexSpec attach = FlexDevice.Parse("flex:mock@pdn");
        attach.Headless.Should().BeFalse();

        FlexStationDevice.StreamFilterFor(
                attach, _opening!.Flex!.Station, (300, 2700), _server, _cancellation.Token)
            .Should().BeNull("attach mode leaves the filter to SmartSDR");
        _server.ReceiveBandRequested.Should().BeNull();

        FlexStationDevice.StreamFilterFor(
                FlexDevice.Parse("flex:mock"), _opening.Flex.Station, (300, 2700), _server, _cancellation.Token)
            .Should().NotBeNull("the same call for a headless spec does install one");
    }

    [Fact]
    public async Task Requests_Are_Applied_In_Order_And_Only_The_Latest_Counts()
    {
        // A radio that takes its time: each command waits for the test to let it through.
        var sent = new List<string>();
        var pending = new Queue<TaskCompletionSource>();
        Task Send(string command)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (sent)
            {
                sent.Add(command);
                pending.Enqueue(done);
            }

            return done.Task;
        }

        void Release()
        {
            lock (sent)
            {
                pending.Dequeue().SetResult();
            }
        }

        int Sent()
        {
            lock (sent)
            {
                return sent.Count;
            }
        }

        var filter = new FlexStreamFilter((300, 2700), () => "0", Send, _ => { }, _ => { });

        filter.Request((1000, 5000));
        await Until(() => Sent() == 1, "the first widening goes out");

        // While it is in flight: put back, then widen again. Only the newest is still wanted.
        filter.Request(null);
        filter.Request((1000, 4000));
        await Task.Delay(100);
        Sent().Should().Be(1, "nothing is sent over the top of a command still in flight");

        Release();
        await Until(() => Sent() == 2, "then the latest target, and only that");
        Release();
        await Until(() => !filter.Busy, "done");

        sent.Should().Equal("filt 0 300 5200", "filt 0 300 4200");
        filter.Applied.Should().Be((300, 4200));

        filter.Request((1000, 4000));
        await Task.Delay(100);
        Sent().Should().Be(2, "asking for what is already applied sends nothing");
    }

    [Fact]
    public async Task A_Rebuilt_Slice_Gets_A_Standing_Widening_Back()
    {
        var sent = new List<string>();
        Task Send(string command)
        {
            lock (sent)
            {
                sent.Add(command);
            }

            return Task.CompletedTask;
        }

        string slice = "0";
        var filter = new FlexStreamFilter((300, 2700), () => slice, Send, _ => { }, _ => { });
        filter.Request((1000, 5000));
        await Until(() => !filter.Busy && filter.Applied == (300, 5200), "widened");

        slice = "1";
        filter.SliceRebuilt();
        await Until(() => !filter.Busy && sent.Count == 2, "widened again on the new slice");
        sent[^1].Should().Be("filt 1 300 5200", "the slice is read when the command is sent");
    }

    [Fact]
    public async Task A_Slice_Rebuilt_While_A_Widening_Is_In_Flight_Is_Widened_Again()
    {
        var sent = new List<string>();
        var pending = new Queue<TaskCompletionSource>();
        Task Send(string command)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (sent)
            {
                sent.Add(command);
                pending.Enqueue(done);
            }

            return done.Task;
        }

        int Sent()
        {
            lock (sent)
            {
                return sent.Count;
            }
        }

        string slice = "0";
        var filter = new FlexStreamFilter((300, 2700), () => slice, Send, _ => { }, _ => { });
        filter.Request((1000, 5000));
        await Until(() => Sent() == 1, "the widening goes out to the old slice");

        // The radio rebuilds the slice while that command is still in flight; the new slice comes
        // up on the baseline, and the old command's answer must not count for it.
        slice = "1";
        filter.SliceRebuilt();
        lock (sent)
        {
            pending.Dequeue().SetResult();
        }

        await Until(() => Sent() == 2, "the new slice is widened too");
        lock (sent)
        {
            pending.Dequeue().SetResult();
        }

        await Until(() => !filter.Busy, "done");
        sent.Should().Equal("filt 0 300 5200", "filt 1 300 5200");
        filter.Applied.Should().Be((300, 5200));
    }

    private async Task<MockFlexRadio> OpenAsync(string device, (int Low, int High)? baseline)
    {
        var kind = new FlexDeviceKind();
        var stationDevice = (FlexStationDevice)kind.Create(device, new DeviceSettings(null, HasWaterfall: true));
        _opening = await stationDevice.OpenAsync(new DeviceOpenContext
        {
            DspRate = DspRate,
            ConfigPath = null,
            Journal = new StationJournal("", _ => { }, _ => { }),
            Channel = _channel,
            Waterfall = _server,
            RadioLost = () => { },
            Cancellation = _cancellation.Token,
            Sideband = "usb",
            ReceiveDialHz = null,
            FlexPacketBuffer = 3,
            FlexTuning = new FlexTuning
            {
                ReceiveFilterLowHz = baseline?.Low,
                ReceiveFilterHighHz = baseline?.High,
            },
            TransmitBands = [],
            CaptureRate = DspRate,
            CaptureDeviceKey = null,
            PlaybackDeviceKey = null,
            Alsa = null,
            Ptt = null,
            Rig = null,
        });

        _opening.ExitCode.Should().BeNull("the mock radio opens");
        return _opening.Flex!.Mock!;
    }

    private string Slice() => _opening!.Flex!.Station.SliceIndex;

    private static List<string> Filts(MockFlexRadio mock) =>
        [.. mock.CommandLog.Where(c => c.StartsWith("filt ", StringComparison.Ordinal))];

    private (int LowHz, int HighHz)? BandsAsked()
    {
        var stream = (ChannelAudioStream)typeof(WaterfallWebServer)
            .GetField("_channelAudioStream", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(_server)!;
        return ((int LowHz, int HighHz)?)typeof(ChannelAudioStream)
            .GetField("_lastReportedBand", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(stream);
    }

    private async Task<ClientWebSocket> ConnectAsync()
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}{ChannelAudioStream.Path}"), _cancellation.Token);
        var buffer = new byte[4096];
        WebSocketReceiveResult hello = await socket.ReceiveAsync(buffer, _cancellation.Token);
        hello.MessageType.Should().Be(WebSocketMessageType.Text);
        return socket;
    }

    private Task AskForBand(ClientWebSocket socket, int lowHz, int highHz) =>
        socket.SendAsync(
            Encoding.UTF8.GetBytes(
                "{\"name\":\"test\",\"band\":{\"lowHz\":" + lowHz + ",\"highHz\":" + highHz + "}}"),
            WebSocketMessageType.Text,
            true,
            _cancellation.Token);

    private static async Task Until(Func<bool> condition, string because)
    {
        for (int i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(25);
        }

        condition().Should().BeTrue(because);
    }
}
