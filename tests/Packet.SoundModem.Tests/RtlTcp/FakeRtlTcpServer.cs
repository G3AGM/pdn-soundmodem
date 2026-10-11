using System.Net;
using System.Net.Sockets;
using Packet.SoundModem.RtlTcp;

namespace Packet.SoundModem.Tests.RtlTcp;

/// <summary>
/// An rtl_tcp server on loopback for the tests: one client at a time, as rtl_tcp serves; sends
/// the header (or not) as each client connects, records every command it is sent, and streams
/// whatever bytes the test gives it. It can drop the client, go silent, or stop listening.
/// </summary>
internal sealed class FakeRtlTcpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly object _gate = new();
    private readonly List<(int Connection, RtlTcpCommand Command, uint Parameter)> _commands = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _accepting;
    private TcpClient? _client;
    private int _connections;
    private long _bytesFromClient;

    public FakeRtlTcpServer(RtlTcpHeader? header = null)
    {
        Header = header ?? new RtlTcpHeader(RtlTcpTuner.R820T, 29);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accepting = Task.Run(AcceptLoop);
    }

    public int Port { get; }

    public RtlTcpEndpoint Endpoint => new("127.0.0.1", Port);

    /// <summary>The header sent to each client; null sends none (samples from the first byte).</summary>
    public RtlTcpHeader? Header { get; set; }

    public int Connections => Volatile.Read(ref _connections);

    public long BytesFromClient => Interlocked.Read(ref _bytesFromClient);

    public IReadOnlyList<(int Connection, RtlTcpCommand Command, uint Parameter)> Commands
    {
        get
        {
            lock (_gate)
            {
                return [.. _commands];
            }
        }
    }

    /// <summary>Sends <paramref name="bytes"/> to the connected client.</summary>
    public void Send(ReadOnlySpan<byte> bytes)
    {
        TcpClient client;
        lock (_gate)
        {
            client = _client ?? throw new InvalidOperationException("no client is connected");
        }

        client.GetStream().Write(bytes);
    }

    /// <summary>Closes the connection to the current client; the listener stays up.</summary>
    public void DropClient()
    {
        lock (_gate)
        {
            _client?.Dispose();
            _client = null;
        }
    }

    /// <summary>Stops listening and drops the client: the server is gone.</summary>
    public void Stop()
    {
        _stopping.Cancel();
        _listener.Stop();
        DropClient();
    }

    public void WaitForConnections(int count, int timeoutMs = 10_000)
    {
        if (!SpinWait.SpinUntil(() => Connections >= count && HasClient(), timeoutMs))
        {
            throw new TimeoutException($"waited for connection {count}; have {Connections}");
        }
    }

    public void WaitForCommands(int count, int timeoutMs = 10_000)
    {
        if (!SpinWait.SpinUntil(() => Commands.Count >= count, timeoutMs))
        {
            throw new TimeoutException($"waited for {count} commands; have {Commands.Count}");
        }
    }

    public void Dispose()
    {
        Stop();
        try
        {
            _accepting.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
    }

    private bool HasClient()
    {
        lock (_gate)
        {
            return _client is not null;
        }
    }

    private async Task AcceptLoop()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            client.NoDelay = true;
            if (Header is RtlTcpHeader header)
            {
                var bytes = new byte[RtlTcpProtocol.HeaderLength];
                RtlTcpProtocol.WriteHeader(bytes, header);
                await client.GetStream().WriteAsync(bytes);
            }

            int connection;
            lock (_gate)
            {
                _client?.Dispose();
                _client = client;
                connection = ++_connections;
            }

            _ = Task.Run(() => ReadCommands(client, connection));
        }
    }

    private async Task ReadCommands(TcpClient client, int connection)
    {
        var buffer = new byte[RtlTcpProtocol.CommandLength];
        try
        {
            NetworkStream stream = client.GetStream();
            while (true)
            {
                await stream.ReadExactlyAsync(buffer);
                Interlocked.Add(ref _bytesFromClient, buffer.Length);
                (RtlTcpCommand command, uint parameter) = RtlTcpProtocol.Decode(buffer);
                lock (_gate)
                {
                    _commands.Add((connection, command, parameter));
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or EndOfStreamException or SocketException or InvalidOperationException)
        {
        }
    }
}
