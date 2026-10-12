using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Packet.SoundModem.SdrConnectProbe;

/// <summary>
/// One WebSocket to SDRconnect, read on a loop of its own: text messages are logged and kept as
/// the latest value of each property, binary messages are counted and measured per payload type,
/// and a capture can take the first bytes of one type to a file.
/// </summary>
/// <remarks>Written from SDRplay's published SDRconnect WebSocket API 1.0.3 document; no SDRplay
/// code is used.</remarks>
internal sealed class SdrConnectLink : IAsyncDisposable
{
    private const int MaxLoggedMessages = 20_000;

    private readonly ClientWebSocket _socket = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, string> _properties = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _waiters = new(StringComparer.Ordinal);
    private readonly ManualResetEventSlim _running = new(true);
    private readonly StreamWriter _log;
    private readonly long _start = Stopwatch.GetTimestamp();
    private readonly Dictionary<int, TypeStats> _stats = [];
    private int _logged;
    private Task? _reader;
    private int _captureType = -1;
    private MemoryStream? _capture;
    private long _captureLimit;

    public SdrConnectLink(StreamWriter log) => _log = log;

    /// <summary>Why the reader stopped, or null while it runs.</summary>
    public string? Ended { get; private set; }

    /// <summary>When the reader stopped, seconds since connecting.</summary>
    public double EndedAt { get; private set; }

    public async Task ConnectAsync(Uri uri, CancellationToken cancellation)
    {
        _socket.Options.SetRequestHeader("User-Agent", "sdrconnect-probe (pdn-soundmodem #699)");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await _socket.ConnectAsync(uri, timeout.Token);
        Log($"connected to {uri}");
        _reader = Task.Run(ReadLoopAsync);
    }

    public double Elapsed => Stopwatch.GetElapsedTime(_start).TotalSeconds;

    public bool Open => _socket.State == WebSocketState.Open && Ended is null;

    /// <summary>The latest value of <paramref name="property"/> SDRconnect has said, or null.</summary>
    public string? Property(string property) => _properties.TryGetValue(property, out string? v) ? v : null;

    /// <summary>Stops reading the socket, so SDRconnect's sends back up, until <see cref="Resume"/>.</summary>
    public void Pause()
    {
        Log("probe: reader paused");
        _running.Reset();
    }

    public void Resume()
    {
        _running.Set();
        Log("probe: reader resumed");
    }

    public Task SendAsync(string eventType, string property, string value)
    {
        string json = $"{{\"event_type\":{Quote(eventType)},\"property\":{Quote(property)},\"value\":{Quote(value)}}}";
        Log("sent: " + json);
        return _socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, _stopping.Token);
    }

    /// <summary>Asks for a property and waits for its get_property_response.</summary>
    public async Task<string?> GetAsync(string property, TimeSpan timeout)
    {
        TaskCompletionSource<string> waiter = Wait("get:" + property);
        await SendAsync("get_property", property, "");
        return await Within(waiter.Task, timeout);
    }

    /// <summary>Sets a property, waits for SDRconnect to say it changed, then reads it back.</summary>
    /// <returns>What SDRconnect says the property is afterwards, and whether a property_changed came.</returns>
    public async Task<(string? Value, bool Changed)> SetAsync(string property, string value, TimeSpan timeout)
    {
        TaskCompletionSource<string> changed = Wait("changed:" + property);
        await SendAsync("set_property", property, value);
        bool sawChange = await Within(changed.Task, timeout) is not null;
        string? now = await GetAsync(property, TimeSpan.FromSeconds(2));
        return (now, sawChange);
    }

    /// <summary>Clears the binary counters, starting a measurement window.</summary>
    public void ResetStats()
    {
        lock (_gate)
        {
            _stats.Clear();
        }
    }

    /// <summary>The counters for one binary type since the last <see cref="ResetStats"/>.</summary>
    public TypeStats Stats(int type)
    {
        lock (_gate)
        {
            return _stats.TryGetValue(type, out TypeStats? s) ? s.Copy() : new TypeStats();
        }
    }

    /// <summary>Every binary type seen since the last <see cref="ResetStats"/>.</summary>
    public int[] TypesSeen()
    {
        lock (_gate)
        {
            return [.. _stats.Keys.Order()];
        }
    }

    /// <summary>Starts keeping the payloads of <paramref name="type"/>, up to
    /// <paramref name="limitBytes"/>.</summary>
    public void Capture(int type, long limitBytes)
    {
        lock (_gate)
        {
            _captureType = type;
            _capture = new MemoryStream();
            _captureLimit = limitBytes;
        }
    }

    /// <summary>Stops a capture and hands back what it kept.</summary>
    public byte[] EndCapture()
    {
        lock (_gate)
        {
            byte[] kept = _capture?.ToArray() ?? [];
            _capture = null;
            _captureType = -1;
            return kept;
        }
    }

    public void Log(string line)
    {
        lock (_log)
        {
            if (_logged++ < MaxLoggedMessages)
            {
                _log.WriteLine($"{Elapsed,9:F3} {line}");
            }
            else if (_logged == MaxLoggedMessages + 1)
            {
                _log.WriteLine($"{Elapsed,9:F3} (log full; later lines dropped)");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _running.Set();
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "probe finished", timeout.Token);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
        }

        _stopping.Cancel();
        if (_reader is not null)
        {
            await Task.WhenAny(_reader, Task.Delay(2000));
        }

        _socket.Dispose();
    }

    private TaskCompletionSource<string> Wait(string key)
    {
        var waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters[key] = waiter;
        return waiter;
    }

    private static async Task<string?> Within(Task<string> task, TimeSpan timeout) =>
        await Task.WhenAny(task, Task.Delay(timeout)) == task ? await task : null;

    private async Task ReadLoopAsync()
    {
        byte[] buffer = new byte[1 << 16];
        var message = new MemoryStream();
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                _running.Wait(_stopping.Token);
                ValueWebSocketReceiveResult result = await _socket.ReceiveAsync(buffer.AsMemory(), _stopping.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Stop($"SDRconnect closed the socket ({_socket.CloseStatus} {_socket.CloseStatusDescription})");
                    return;
                }

                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    OnText(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                }
                else
                {
                    OnBinary(message.GetBuffer().AsSpan(0, (int)message.Length));
                }

                message.SetLength(0);
            }
        }
        catch (OperationCanceledException)
        {
            Stop("probe stopped reading");
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException)
        {
            Stop($"the socket failed: {e.Message}");
        }
    }

    private void Stop(string why)
    {
        Ended ??= why;
        EndedAt = Elapsed;
        Log("reader ended: " + why);
        foreach (TaskCompletionSource<string> waiter in _waiters.Values)
        {
            waiter.TrySetCanceled();
        }
    }

    private void OnText(string text)
    {
        // signal_power and the like may be pushed many times a second; they are kept but only
        // the property name is logged after the first few hundred lines of them.
        string? eventType = null;
        string? property = null;
        string? value = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            eventType = Field(root, "event_type");
            property = Field(root, "property");
            value = Field(root, "value");
        }
        catch (JsonException)
        {
        }

        Log("recv: " + (text.Length > 2000 ? text[..2000] + "...(truncated)" : text));
        if (property is null || value is null)
        {
            return;
        }

        _properties[property] = value;
        string key = eventType switch
        {
            "get_property_response" => "get:" + property,
            "property_changed" => "changed:" + property,
            _ => "",
        };
        if (key.Length > 0 && _waiters.TryRemove(key, out TaskCompletionSource<string>? waiter))
        {
            waiter.TrySetResult(value);
        }
    }

    private static string? Field(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement e)
            ? (e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText())
            : null;

    private void OnBinary(ReadOnlySpan<byte> message)
    {
        int type = message.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(message) : -1;
        ReadOnlySpan<byte> payload = message.Length >= 2 ? message[2..] : message;
        lock (_gate)
        {
            if (!_stats.TryGetValue(type, out TypeStats? stats))
            {
                stats = new TypeStats();
                _stats[type] = stats;
                Log($"binary: first message of type {type} in this window, {message.Length} bytes");
            }

            stats.Add(payload, Elapsed, type is 1 or 2 or 4 or 5);
            if (type == _captureType && _capture is not null && _capture.Length < _captureLimit)
            {
                _capture.Write(payload[..(int)Math.Min(payload.Length, _captureLimit - _capture.Length)]);
            }
        }
    }

    private static string Quote(string s)
    {
        var b = new StringBuilder("\"");
        foreach (char c in s)
        {
            b.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                _ when c < 0x20 => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            });
        }

        return b.Append('"').ToString();
    }
}

/// <summary>Counters for one binary payload type over a window.</summary>
internal sealed class TypeStats
{
    public long Messages { get; private set; }
    public long Bytes { get; private set; }
    public int MinBytes { get; private set; } = int.MaxValue;
    public int MaxBytes { get; private set; }
    public double First { get; private set; } = -1;
    public double Last { get; private set; }

    // For 16-bit payloads, as pairs (I,Q or L,R): sums for RMS and DC, peaks, and how often the
    // two halves are equal (a mono signal sent as stereo).
    public long Pairs { get; private set; }
    public double SumA { get; private set; }
    public double SumB { get; private set; }
    public double SumSqA { get; private set; }
    public double SumSqB { get; private set; }
    public int PeakA { get; private set; }
    public int PeakB { get; private set; }
    public long EqualPairs { get; private set; }
    public long OddLengths { get; private set; }

    public void Add(ReadOnlySpan<byte> payload, double at, bool sixteenBitPairs)
    {
        Messages++;
        Bytes += payload.Length;
        MinBytes = Math.Min(MinBytes, payload.Length);
        MaxBytes = Math.Max(MaxBytes, payload.Length);
        if (First < 0)
        {
            First = at;
        }

        Last = at;
        if (!sixteenBitPairs)
        {
            return;
        }

        if (payload.Length % 4 != 0)
        {
            OddLengths++;
        }

        for (int i = 0; i + 3 < payload.Length; i += 4)
        {
            int a = BinaryPrimitives.ReadInt16LittleEndian(payload[i..]);
            int b = BinaryPrimitives.ReadInt16LittleEndian(payload[(i + 2)..]);
            Pairs++;
            SumA += a;
            SumB += b;
            SumSqA += (double)a * a;
            SumSqB += (double)b * b;
            PeakA = Math.Max(PeakA, Math.Abs(a));
            PeakB = Math.Max(PeakB, Math.Abs(b));
            if (a == b)
            {
                EqualPairs++;
            }
        }
    }

    public TypeStats Copy() => (TypeStats)MemberwiseClone();
}
