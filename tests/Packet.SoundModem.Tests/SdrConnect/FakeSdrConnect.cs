using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Packet.SoundModem.SdrConnect;

namespace Packet.SoundModem.Tests.SdrConnect;

/// <summary>
/// Hands out the client end of a loopback WebSocket pair for each connect and runs a
/// <see cref="FakeSdrConnect"/> on the server end, so the client is tested against a real socket.
/// </summary>
internal sealed class FakeSdrConnectHost : IDisposable
{
    private readonly System.Threading.Channels.Channel<FakeSdrConnect> _accepted =
        System.Threading.Channels.Channel.CreateUnbounded<FakeSdrConnect>();
    private readonly List<IDisposable> _owned = [];
    private readonly Action<FakeSdrConnect>? _configure;
    private int _connects;

    public FakeSdrConnectHost(Action<FakeSdrConnect>? configure = null) => _configure = configure;

    public static readonly SdrConnectEndpoint Endpoint = new("shack-pc.example", 5454);

    /// <summary>Connects made, refused ones included.</summary>
    public int Connects => Volatile.Read(ref _connects);

    /// <summary>When set, connects fail as if nothing were listening.</summary>
    public volatile bool Refusing;

    public async Task<WebSocket> ConnectAsync(CancellationToken cancellation)
    {
        Interlocked.Increment(ref _connects);
        if (Refusing)
        {
            throw new WebSocketException("connection refused");
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        Task connecting = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, cancellation).AsTask();
        TcpClient server = await listener.AcceptTcpClientAsync(cancellation);
        await connecting;
        lock (_owned)
        {
            _owned.Add(client);
            _owned.Add(server);
        }

        WebSocket serverSocket = WebSocket.CreateFromStream(server.GetStream(), new WebSocketCreationOptions { IsServer = true });
        var fake = new FakeSdrConnect(serverSocket);
        _configure?.Invoke(fake);
        fake.Run();
        await _accepted.Writer.WriteAsync(fake, cancellation);
        return WebSocket.CreateFromStream(client.GetStream(), new WebSocketCreationOptions { IsServer = false });
    }

    public async Task<FakeSdrConnect> NextAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await _accepted.Reader.ReadAsync(timeout.Token);
    }

    public void Dispose()
    {
        lock (_owned)
        {
            foreach (IDisposable owned in _owned)
            {
                owned.Dispose();
            }
        }
    }
}

/// <summary>
/// One connection's worth of SDRconnect as its API document describes it: a property table that
/// answers <c>get_property</c>, takes <c>set_property</c> and reports each change, the stream
/// switches, and binary IQ and audio messages on demand. Every message the client sends is kept.
/// </summary>
internal sealed class FakeSdrConnect
{
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly object _gate = new();

    public FakeSdrConnect(WebSocket socket) => _socket = socket;

    public Dictionary<string, string> Properties { get; } = new(StringComparer.Ordinal)
    {
        ["api_version"] = "1.0.3",
        ["active_device"] = "RSP1A",
        ["valid_devices"] = "RSP1A",
        ["can_control"] = "true",
        ["started"] = "true",
        ["device_sample_rate"] = "2000000",
        ["device_center_frequency"] = "7000000",
        ["device_vfo_frequency"] = "7000000",
        ["valid_antennas"] = "Antenna A,Antenna B",
        ["active_antenna"] = "Antenna A",
        ["lna_state"] = "3",
        ["lna_state_min"] = "0",
        ["lna_state_max"] = "9",
        ["demodulator"] = "AM",
        ["demod_max_bandwidth"] = "12000",
        ["audio_volume_percent"] = "50",
    };

    /// <summary>Properties that get no answer to a <c>get_property</c>.</summary>
    public HashSet<string> Silent { get; } = new(StringComparer.Ordinal);

    /// <summary>What a set value becomes, by property: SDRconnect choosing a rate of its own.</summary>
    public Dictionary<string, Func<string, string>> Coerce { get; } = new(StringComparer.Ordinal);

    /// <summary>Whether a set is reported back to the client that made it, which the document does
    /// not say; false stands for an SDRconnect that reports only other clients' changes.</summary>
    public bool EchoSets { get; set; } = true;

    /// <summary>Every message the client sent, in order.</summary>
    public ConcurrentQueue<SdrConnectMessage> Received { get; } = new();

    public bool IqOn { get; private set; }

    public bool AudioOn { get; private set; }

    public TaskCompletionSource Streaming { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Run() => _ = Task.Run(LoopAsync);

    public IEnumerable<SdrConnectMessage> Sets(string property) =>
        Received.Where(m => m.EventType == "set_property" && m.Property == property);

    public string? Value(string property)
    {
        lock (_gate)
        {
            return Properties.GetValueOrDefault(property);
        }
    }

    /// <summary>A change made by someone else (the SDRconnect window): reported, as SDRconnect would.</summary>
    public Task ChangeAsync(string property, string value)
    {
        lock (_gate)
        {
            Properties[property] = value;
        }

        return SendJsonAsync("property_changed", property, value);
    }

    /// <summary>IQ samples (I, Q, I, Q) as one or more binary type-2 messages.</summary>
    public Task SendIqAsync(ReadOnlySpan<short> interleaved, int? splitAt = null) =>
        SendBinaryAsync(SdrConnectProtocol.PrimaryIq, Bytes(interleaved), splitAt);

    /// <summary>Stereo audio (L, R, L, R) as one or more binary type-1 messages.</summary>
    public Task SendAudioAsync(ReadOnlySpan<short> interleaved, int? splitAt = null) =>
        SendBinaryAsync(SdrConnectProtocol.PrimaryAudio, Bytes(interleaved), splitAt);

    /// <summary>One binary message, sent in two WebSocket frames when <paramref name="splitAt"/>
    /// says where (counted from the start of the type word).</summary>
    public async Task SendBinaryAsync(ushort type, byte[] payload, int? splitAt = null)
    {
        var message = new byte[2 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(message, type);
        payload.CopyTo(message, 2);
        await _send.WaitAsync();
        try
        {
            if (splitAt is int split && split > 0 && split < message.Length)
            {
                await _socket.SendAsync(message.AsMemory(0, split), WebSocketMessageType.Binary, false, CancellationToken.None);
                await _socket.SendAsync(message.AsMemory(split), WebSocketMessageType.Binary, true, CancellationToken.None);
            }
            else
            {
                await _socket.SendAsync(message, WebSocketMessageType.Binary, true, CancellationToken.None);
            }
        }
        finally
        {
            _send.Release();
        }
    }

    /// <summary>A whole binary message, type word included, sent as it is: for a test that must not
    /// allocate on the fake's side.</summary>
    public async Task SendRawAsync(byte[] message)
    {
        await _send.WaitAsync();
        try
        {
            await _socket.SendAsync(message.AsMemory(), WebSocketMessageType.Binary, true, CancellationToken.None);
        }
        finally
        {
            _send.Release();
        }
    }

    /// <summary>A whole text message as it is, for the same test.</summary>
    public async Task SendRawTextAsync(byte[] message)
    {
        await _send.WaitAsync();
        try
        {
            await _socket.SendAsync(message.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            _send.Release();
        }
    }

    /// <summary>Ends the connection abruptly, as a crash or a pulled cable would.</summary>
    public void Drop() => _socket.Abort();

    public async Task WaitForAsync(Func<FakeSdrConnect, bool> condition, string what)
    {
        DateTime giveUp = DateTime.UtcNow.AddSeconds(10);
        while (!condition(this))
        {
            if (DateTime.UtcNow > giveUp)
            {
                throw new TimeoutException($"the fake SDRconnect never saw {what}");
            }

            await Task.Delay(10);
        }
    }

    private static byte[] Bytes(ReadOnlySpan<short> samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int k = 0; k < samples.Length; k++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(2 * k), samples[k]);
        }

        return bytes;
    }

    private async Task LoopAsync()
    {
        var buffer = new byte[65536];
        try
        {
            while (true)
            {
                int length = 0;
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), CancellationToken.None);
                    length += result.Count;
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (!SdrConnectProtocol.TryParse(buffer.AsSpan(0, length), out SdrConnectMessage message))
                {
                    continue;
                }

                Received.Enqueue(message);
                await HandleAsync(message);
            }
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
        }

        Closed.TrySetResult();
    }

    private async Task HandleAsync(SdrConnectMessage message)
    {
        switch (message.EventType)
        {
            case "get_property":
                string? value = Silent.Contains(message.Property) ? null : Value(message.Property);
                if (value is not null)
                {
                    await SendJsonAsync("get_property_response", message.Property, value);
                }

                break;
            case "set_property":
                string set = Coerce.TryGetValue(message.Property, out Func<string, string>? coerce) ? coerce(message.Value) : message.Value;
                lock (_gate)
                {
                    Properties[message.Property] = set;
                }

                if (EchoSets)
                {
                    await SendJsonAsync("property_changed", message.Property, set);
                }

                break;
            case "iq_stream_enable":
                IqOn = message.Value == "true";
                if (IqOn)
                {
                    Streaming.TrySetResult();
                }

                break;
            case "audio_stream_enable":
                AudioOn = message.Value == "true";
                if (AudioOn)
                {
                    Streaming.TrySetResult();
                }

                break;
        }
    }

    private async Task SendJsonAsync(string eventType, string property, string value)
    {
        string json = string.Create(CultureInfo.InvariantCulture,
            $"{{\"event_type\":\"{eventType}\",\"property\":\"{property}\",\"device\":\"primary\",\"value\":\"{value}\"}}");
        await _send.WaitAsync();
        try
        {
            await _socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException)
        {
        }
        finally
        {
            _send.Release();
        }
    }
}
