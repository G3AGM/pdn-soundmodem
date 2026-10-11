using System.Buffers.Binary;
using System.Collections.Specialized;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Packet.SoundModem.Channel;

namespace Packet.SoundModem.Waterfall;

/// <summary>
/// The local channel audio stream (issue #584): a WebSocket on the station page server that
/// hands a program on the same machine the channel's receive audio, nothing else. One receive
/// tap fans out to every connection, each with its own small bounded queue; a slow reader loses
/// blocks, marked with a gap, rather than slowing the modem or any other reader.
/// </summary>
/// <remarks>
/// <para><b>Loopback only, no key.</b> The server checks the request with
/// <see cref="LocalProgramRefusal"/> before accepting the upgrade; this class assumes that has
/// already happened and serves anything handed to it.</para>
/// <para><b>100 ms blocks.</b> The channel's own blocks are whatever the station reads (100 ms,
/// or 20 ms with an ARDOP modem), so the audio is gathered here into blocks of a tenth of a
/// second, once for every connection. A block is shorter than that only where the transmitted
/// flag changes, or where the input lost audio: each block is wholly one or the other, and a gap
/// always starts a new one.</para>
/// <para><b>Keyed blocks.</b> <see cref="SoundModemChannel.ProcessReceive"/> skips every receive
/// tap while the channel is transmitting (half duplex), so an ordinary tap simply sees nothing
/// for the length of a keyup. <see cref="OnKeyedBlock"/> is wired to
/// <see cref="SoundModemChannel.KeyedReceiveBlock"/> instead, which fires from exactly that gate
/// with the length of the block that was skipped - silence of the same length, marked
/// transmitted, takes its place, so a reader's sample clock never stops and it can tell a keyup
/// from a gap.</para>
/// <para><b>Gaps.</b> Two kinds of loss are marked the same way. A slow reader's own queue
/// overflowing drops whole blocks, and the sample index jumps by exactly what was dropped. Audio
/// the input lost (<see cref="OnInputLost"/>, from <see cref="SoundModemChannel.ReceiveAudioLost"/>:
/// an overrun, a lost radio packet, a stalled input) never reached the channel at all and nobody
/// knows how much it was, so the index does not move for it; the gap flag alone says the two
/// sides of it are not one signal.</para>
/// <para><b>The requested band.</b> A connection may name a band it needs to hear
/// (<see cref="Client.Band"/>, set from the identify message); <see cref="BandRequested"/> fires
/// with the union of every connected band whenever that union changes, null when none is asked
/// for. What that is used for, widening a headless Flex's slice filter, is the caller's, not
/// this class's: it knows nothing about Flex.</para>
/// <para><b>Allocation.</b> Nothing on the receive thread allocates once a connection is open:
/// the block being gathered is one buffer, and each connection's queue is a ring of
/// preallocated blocks it copies into.</para>
/// </remarks>
internal sealed class ChannelAudioStream
{
    /// <summary>The path this stream is served under, under whatever base the station page is.</summary>
    internal const string Path = "/channel-audio";

    /// <summary>How long a whole block is.</summary>
    internal const int BlockMilliseconds = 100;

    /// <summary>
    /// Blocks held per connection before the oldest is dropped: 6.4 seconds at 100 ms a block,
    /// generous for a reader on the same machine.
    /// </summary>
    internal const int QueueCapacity = 64;

    /// <summary>The longest identify message read; anything longer is ignored whole.</summary>
    internal const int MaxIdentifyBytes = 4096;

    private const byte AudioKind = 0x01;
    private const byte FlagTransmitted = 0x01;
    private const byte FlagGap = 0x02;
    private const int HeaderBytes = 16;

    /// <summary>The proxy headers that say a request was relayed rather than made here.</summary>
    private static readonly string[] ForwardingHeaders = ["X-Forwarded-For", "Forwarded", "X-Real-IP"];

    private readonly int _sampleRate;
    private readonly int _blockSamples;
    private readonly Action<string>? _log;

    // Clients and bands, changed only under this lock. The receive thread reads _clients without
    // it: the array is replaced, never changed in place.
    private readonly object _gate = new();
    private volatile Client[] _clients = [];
    private (int LowHz, int HighHz)? _lastReportedBand;
    private Action<(int LowHz, int HighHz)?>? _bandRequested;
    private Listener[] _lastReportedListeners = [];
    private Action<IReadOnlyList<Listener>>? _listenersChanged;

    // The block being gathered: the receive thread's alone.
    private readonly float[] _block;
    private int _blockFill;
    private ulong _blockStart;
    private bool _blockTransmitted;
    private bool _blockGap;
    private bool _inputGapPending;
    private ulong _sampleCounter;

    public ChannelAudioStream(int sampleRate, Action<string>? log)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 10);
        _sampleRate = sampleRate;
        _blockSamples = sampleRate * BlockMilliseconds / 1000;
        _block = new float[_blockSamples];
        _log = log;
    }

    /// <summary>
    /// Called with the union of every connected client's requested band (see the identify
    /// message, <see cref="ApplyIdentify"/>) whenever it changes; null when no connected client
    /// has asked for one. Setting it while a band is already asked for calls it straight away
    /// with that band, so a handler installed after the first connection still hears it. Called
    /// under this stream's own lock, in order, so it must return promptly.
    /// </summary>
    public Action<(int LowHz, int HighHz)?>? BandRequested
    {
        get
        {
            lock (_gate)
            {
                return _bandRequested;
            }
        }

        set
        {
            lock (_gate)
            {
                _bandRequested = value;
                if (_lastReportedBand is not null)
                {
                    value?.Invoke(_lastReportedBand);
                }
            }
        }
    }

    /// <summary>
    /// Called with every connected reader that has said its name (issue #586), whenever that list
    /// changes: a named reader joins, renames itself, gives or drops its page port, or goes.
    /// Setting it calls it straight away with the list as it stands. Called under this stream's
    /// own lock, in order, so it must return promptly.
    /// </summary>
    public Action<IReadOnlyList<Listener>>? ListenersChanged
    {
        get
        {
            lock (_gate)
            {
                return _listenersChanged;
            }
        }

        set
        {
            lock (_gate)
            {
                _listenersChanged = value;
                value?.Invoke(_lastReportedListeners);
            }
        }
    }

    /// <summary>The named readers as last reported to <see cref="ListenersChanged"/>.</summary>
    internal IReadOnlyList<Listener> Listeners
    {
        get
        {
            lock (_gate)
            {
                return _lastReportedListeners;
            }
        }
    }

    /// <summary>The longest name a reader may give itself; anything longer is cut.</summary>
    internal const int MaxNameLength = 64;

    /// <summary>How many samples a whole block holds.</summary>
    internal int BlockSamples => _blockSamples;

    /// <summary>How many clients are connected right now, for the journal and for tests.</summary>
    internal int ClientCount => _clients.Length;

    /// <summary>
    /// True for a loopback address (<c>127.0.0.1</c>, <c>::1</c>, and anything else
    /// <see cref="IPAddress.IsLoopback"/> calls loopback); false for null (no remote address at
    /// all, which is refused rather than trusted) or anything else.
    /// </summary>
    public static bool IsLoopbackAddress(IPAddress? address) => address is not null && IPAddress.IsLoopback(address);

    /// <summary>
    /// Why a request may not use something meant only for a program on this machine, or null
    /// when it may. Refused: anything not from a loopback address; anything carrying an
    /// <c>Origin</c> header (a browser sets it and page script cannot remove it); and anything
    /// carrying a proxy's forwarding header, because a reverse proxy on this machine makes every
    /// request it relays arrive from loopback with no <c>Origin</c>.
    /// </summary>
    /// <param name="remote">The request's remote address.</param>
    /// <param name="headers">The request's headers.</param>
    public static string? LocalProgramRefusal(IPAddress? remote, NameValueCollection headers)
    {
        if (!IsLoopbackAddress(remote))
        {
            return "it is not loopback";
        }

        if (!string.IsNullOrEmpty(headers["Origin"]))
        {
            return "it declared an Origin (a browser)";
        }

        foreach (string header in ForwardingHeaders)
        {
            if (headers[header] is not null)
            {
                return $"it carries {header} (relayed by a proxy)";
            }
        }

        return null;
    }

    /// <summary>The channel's own receive tap: every block of audio it hears, in order.</summary>
    public void OnReceive(ReadOnlySpan<float> samples) => Append(samples, samples.Length, transmitted: false);

    /// <summary>
    /// The channel's keyed-block callback: a block of this many samples was skipped because the
    /// station was transmitting. Fed to every client as silence, marked transmitted, so its
    /// sample clock keeps pace with the channel's own.
    /// </summary>
    public void OnKeyedBlock(int length) => Append(default, Math.Max(0, length), transmitted: true);

    /// <summary>
    /// The input lost audio before the next block: whatever has been gathered so far goes out as
    /// it is, and the next block on every connection is marked as following a gap.
    /// </summary>
    public void OnInputLost()
    {
        Client[] clients = _clients;
        if (clients.Length == 0)
        {
            return;
        }

        Flush(clients);
        _inputGapPending = true;
    }

    /// <summary>Serves one already-accepted, already-checked WebSocket until it closes.</summary>
    /// <param name="socket">The accepted socket.</param>
    /// <param name="remoteDescription">For the journal: who connected, e.g. an address and port.</param>
    /// <param name="dialHz">The rig's dial frequency if the station knows one (rig or Flex
    /// slice), for the hello message; 0 for "not known", the same sentinel the page's own
    /// config message uses.</param>
    /// <param name="serverStopping">Cancelled when the whole server is going down.</param>
    public async Task ServeAsync(WebSocket socket, string remoteDescription, double dialHz, CancellationToken serverStopping)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(serverStopping);
        var client = new Client(socket, new AudioQueue(QueueCapacity, _blockSamples));
        Task? send = null;
        bool added = false;
        try
        {
            // The connection joins the fan-out first, so no audio is lost while the hello goes
            // out: blocks wait in its queue. Its send loop starts only once the hello is sent, so
            // the hello is still always the first message.
            AddClient(client);
            added = true;
            byte[] hello = JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "hello",
                rateHz = _sampleRate,
                dialHz = dialHz > 0 ? dialHz : (double?)null,
            });
            await socket.SendAsync(hello, WebSocketMessageType.Text, true, stop.Token).ConfigureAwait(false);

            Journal($"channel-audio: {remoteDescription} connected - {ClientCount} client{Plural(ClientCount)}");
            send = SendLoopAsync(client, stop.Token);

            // An identify message may arrive in fragments; it is gathered up to its end, and one
            // longer than the cap is read to its end and ignored whole.
            var buffer = new byte[MaxIdentifyBytes];
            int filled = 0;
            bool tooLong = false;
            while (socket.State == WebSocketState.Open && !stop.IsCancellationRequested)
            {
                if (!tooLong && filled == buffer.Length)
                {
                    tooLong = true;
                }

                Memory<byte> into = tooLong ? buffer : buffer.AsMemory(filled);
                ValueWebSocketReceiveResult received =
                    await socket.ReceiveAsync(into, stop.Token).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (!tooLong)
                {
                    filled += received.Count;
                }

                if (!received.EndOfMessage)
                {
                    continue;
                }

                if (received.MessageType == WebSocketMessageType.Text && !tooLong && filled > 0)
                {
                    ApplyIdentify(client, buffer.AsMemory(0, filled));
                }

                filled = 0;
                tooLong = false;
            }
        }
        catch (Exception)
        {
            // A vanished client is normal shutdown for its connection, nothing more.
        }
        finally
        {
            client.Queue.Complete();
            await stop.CancelAsync().ConfigureAwait(false);
            if (send is not null)
            {
                try
                {
                    await send.ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            if (added)
            {
                RemoveClient(client);
                Journal($"channel-audio: {remoteDescription} disconnected - {ClientCount} client{Plural(ClientCount)} left");
            }

            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
            }

            socket.Dispose();
        }
    }

    /// <summary>Stops every connection and drops them, for the server's own shutdown.</summary>
    public void Shutdown()
    {
        Client[] clients;
        lock (_gate)
        {
            clients = _clients;
            _clients = [];
        }

        foreach (Client client in clients)
        {
            client.Queue.Complete();
        }
    }

    /// <summary>
    /// Reads one identify message. Anything it does not understand is ignored rather than
    /// ending the connection: a root that is not an object, a field of the wrong type, a band
    /// that is not a band.
    /// </summary>
    internal void ApplyIdentify(Client client, ReadOnlyMemory<byte> payload)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(payload);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (root.TryGetProperty("name", out JsonElement nameElement)
                && nameElement.ValueKind == JsonValueKind.String)
            {
                client.Name = CleanName(nameElement.GetString());
            }

            if (root.TryGetProperty("pagePort", out JsonElement portElement)
                && portElement.ValueKind == JsonValueKind.Number
                && portElement.TryGetInt32(out int pagePort)
                && pagePort is > 0 and <= 65535)
            {
                client.PagePort = pagePort;
            }

            RecomputeListeners();
            if (!root.TryGetProperty("band", out JsonElement bandElement))
            {
                return;
            }

            if (bandElement.ValueKind == JsonValueKind.Null)
            {
                SetBand(client, null);
            }
            else if (bandElement.ValueKind == JsonValueKind.Object
                && TryReadHz(bandElement, "lowHz", out int lowHz)
                && TryReadHz(bandElement, "highHz", out int highHz))
            {
                // Clamped to what the channel can carry at all, before it goes anywhere near a
                // radio: the band is audio Hz on this channel, from 0 to half its rate.
                int nyquist = _sampleRate / 2;
                lowHz = Math.Clamp(lowHz, 0, nyquist);
                highHz = Math.Clamp(highHz, 0, nyquist);
                if (lowHz < highHz)
                {
                    SetBand(client, (lowHz, highHz));
                }
            }
        }
        catch (JsonException)
        {
            // A malformed identify message changes nothing: the audio keeps flowing regardless,
            // the same as a browser that sent us an odd message on the page's own socket would.
        }
    }

    private static bool TryReadHz(JsonElement band, string name, out int hz)
    {
        hz = 0;
        return band.TryGetProperty(name, out JsonElement element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out hz);
    }

    private void Append(ReadOnlySpan<float> samples, int length, bool transmitted)
    {
        Client[] clients = _clients;
        if (clients.Length == 0)
        {
            // Nobody to tell. The clock still runs, and a half-gathered block or a pending gap
            // means nothing to a connection that arrives later.
            _sampleCounter += (ulong)length;
            _blockFill = 0;
            _inputGapPending = false;
            return;
        }

        int offset = 0;
        while (offset < length)
        {
            if (_blockFill > 0 && _blockTransmitted != transmitted)
            {
                Flush(clients);
            }

            if (_blockFill == 0)
            {
                _blockStart = _sampleCounter;
                _blockTransmitted = transmitted;
                _blockGap = _inputGapPending;
                _inputGapPending = false;
            }

            int take = Math.Min(_blockSamples - _blockFill, length - offset);
            Span<float> into = _block.AsSpan(_blockFill, take);
            if (transmitted)
            {
                into.Clear();
            }
            else
            {
                samples.Slice(offset, take).CopyTo(into);
            }

            _blockFill += take;
            _sampleCounter += (ulong)take;
            offset += take;
            if (_blockFill == _blockSamples)
            {
                Flush(clients);
            }
        }
    }

    private void Flush(Client[] clients)
    {
        if (_blockFill == 0)
        {
            return;
        }

        byte flags = (byte)((_blockTransmitted ? FlagTransmitted : 0) | (_blockGap ? FlagGap : 0));
        ReadOnlySpan<float> block = _block.AsSpan(0, _blockFill);
        foreach (Client client in clients)
        {
            client.Queue.Enqueue(_blockStart, block, flags);
        }

        _blockFill = 0;
    }

    private void AddClient(Client client)
    {
        lock (_gate)
        {
            _clients = [.. _clients, client];
        }
    }

    private void RemoveClient(Client client)
    {
        lock (_gate)
        {
            _clients = Array.FindAll(_clients, c => !ReferenceEquals(c, client));
            client.Band = null;
            RecomputeBandLocked();
            RecomputeListenersLocked();
        }
    }

    /// <summary>
    /// A reader's name as the station page shows it: printable ASCII only, trimmed, at most
    /// <see cref="MaxNameLength"/> characters; null when nothing is left. The page escapes it as
    /// well, but a name is another program's words and has no business carrying anything else.
    /// </summary>
    internal static string? CleanName(string? name)
    {
        if (name is null)
        {
            return null;
        }

        var kept = new StringBuilder(Math.Min(name.Length, MaxNameLength));
        foreach (char c in name)
        {
            if (c is >= ' ' and <= '~')
            {
                kept.Append(c);
            }
        }

        string cleaned = kept.ToString().Trim();
        if (cleaned.Length > MaxNameLength)
        {
            cleaned = cleaned[..MaxNameLength].TrimEnd();
        }

        return cleaned.Length == 0 ? null : cleaned;
    }

    private void RecomputeListeners()
    {
        lock (_gate)
        {
            RecomputeListenersLocked();
        }
    }

    /// <summary>Under <see cref="_gate"/>, so each change is reported in order.</summary>
    private void RecomputeListenersLocked()
    {
        var named = new List<Listener>();
        foreach (Client client in _clients)
        {
            if (client.Name is string name)
            {
                named.Add(new Listener(name, client.PagePort));
            }
        }

        if (named.SequenceEqual(_lastReportedListeners))
        {
            return;
        }

        _lastReportedListeners = [.. named];
        try
        {
            _listenersChanged?.Invoke(_lastReportedListeners);
        }
        catch (Exception e)
        {
            Journal($"channel-audio: the listeners handler failed: {e.Message}");
        }
    }

    private void SetBand(Client client, (int LowHz, int HighHz)? band)
    {
        lock (_gate)
        {
            // A client already removed (its connection went while this message was being read)
            // must not bring its band back.
            if (Array.IndexOf(_clients, client) < 0)
            {
                return;
            }

            client.Band = band;
            RecomputeBandLocked();
        }
    }

    /// <summary>Under <see cref="_gate"/>, so each change is computed and reported in order.</summary>
    private void RecomputeBandLocked()
    {
        (int LowHz, int HighHz)? union = null;
        foreach (Client client in _clients)
        {
            if (client.Band is (int lowHz, int highHz))
            {
                union = union is (int unionLow, int unionHigh)
                    ? (Math.Min(unionLow, lowHz), Math.Max(unionHigh, highHz))
                    : (lowHz, highHz);
            }
        }

        if (!Nullable.Equals(union, _lastReportedBand))
        {
            _lastReportedBand = union;
            try
            {
                _bandRequested?.Invoke(union);
            }
            catch (Exception e)
            {
                Journal($"channel-audio: the band handler failed: {e.Message}");
            }
        }
    }

    private void Journal(string line)
    {
        try
        {
            _log?.Invoke(line);
        }
        catch (Exception)
        {
            // A journal that will not take a line is not this connection's problem.
        }
    }

    private static string Plural(int count) => count == 1 ? "" : "s";

    private static async Task SendLoopAsync(Client client, CancellationToken cancellation)
    {
        try
        {
            while (true)
            {
                int length = await client.Queue.DequeueAsync(client.Message, cancellation).ConfigureAwait(false);
                if (length < 0)
                {
                    return;
                }

                await client.Socket.SendAsync(
                        client.Message.AsMemory(0, length), WebSocketMessageType.Binary, true, cancellation)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// <c>[kind 1][flags 1][reserved 2][sampleIndex u64 LE][count i32 LE][samples f32 LE ...]</c>.
    /// Flags: bit 0 transmitted (silence standing in for a keyup), bit 1 gap (audio was lost
    /// before this block, by this reader's own queue or by the input).
    /// </summary>
    /// <returns>The message length.</returns>
    internal static int Encode(Span<byte> message, ulong sampleIndex, ReadOnlySpan<float> samples, byte flags)
    {
        message[0] = AudioKind;
        message[1] = flags;
        message[2] = 0;
        message[3] = 0;
        BinaryPrimitives.WriteUInt64LittleEndian(message.Slice(4, 8), sampleIndex);
        BinaryPrimitives.WriteInt32LittleEndian(message.Slice(12, 4), samples.Length);
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(message.Slice(HeaderBytes + (i * 4), 4), samples[i]);
        }

        return HeaderBytes + (samples.Length * 4);
    }

    /// <summary>One connection: its socket, its queue, and what it said about itself.</summary>
    internal sealed class Client(WebSocket socket, AudioQueue queue)
    {
        public WebSocket Socket { get; } = socket;

        public AudioQueue Queue { get; } = queue;

        /// <summary>The message being sent, reused for every block.</summary>
        public byte[] Message { get; } = new byte[HeaderBytes + (queue.BlockSamples * 4)];

        public string? Name { get; set; }

        public int? PagePort { get; set; }

        public (int LowHz, int HighHz)? Band { get; set; }
    }

    /// <summary>
    /// A fixed ring of preallocated blocks for one connection: a block is copied in on the
    /// receive thread and encoded straight out of its slot by the sender, so neither side
    /// allocates. The oldest block is dropped to make room for a new one, and whichever block is
    /// actually handed to the reader next carries the gap flag, so a reader finds out audio was
    /// lost exactly once, on the first block it sees after the hole. The flag is decided when a
    /// block leaves the queue, not when it enters it: a drop can be followed by several more
    /// blocks arriving before the reader catches up, and it is whichever of those the reader
    /// actually sees first that must carry the mark.
    /// </summary>
    internal sealed class AudioQueue
    {
        private readonly object _lock = new();
        private readonly SemaphoreSlim _signal = new(0);
        private readonly float[][] _slots;
        private readonly int[] _counts;
        private readonly ulong[] _indices;
        private readonly byte[] _flags;
        private int _head;
        private int _size;
        private bool _gapPending;
        private bool _completed;

        public AudioQueue(int capacity, int blockSamples)
        {
            BlockSamples = blockSamples;
            _slots = new float[capacity][];
            for (int i = 0; i < capacity; i++)
            {
                _slots[i] = new float[blockSamples];
            }

            _counts = new int[capacity];
            _indices = new ulong[capacity];
            _flags = new byte[capacity];
        }

        /// <summary>The most samples one block can hold.</summary>
        public int BlockSamples { get; }

        public void Enqueue(ulong sampleIndex, ReadOnlySpan<float> samples, byte flags)
        {
            lock (_lock)
            {
                if (_completed)
                {
                    return;
                }

                if (_size == _slots.Length)
                {
                    _head = (_head + 1) % _slots.Length;
                    _size--;
                    _gapPending = true;
                }

                int slot = (_head + _size) % _slots.Length;
                samples.CopyTo(_slots[slot]);
                _counts[slot] = samples.Length;
                _indices[slot] = sampleIndex;
                _flags[slot] = flags;
                _size++;
            }

            _signal.Release();
        }

        /// <summary>
        /// Waits for the next block and encodes it into <paramref name="message"/>.
        /// </summary>
        /// <returns>The encoded length, or -1 once the queue is completed.</returns>
        public async ValueTask<int> DequeueAsync(byte[] message, CancellationToken cancellation)
        {
            while (true)
            {
                int length = TryDequeue(message);
                if (length != 0)
                {
                    return length;
                }

                await _signal.WaitAsync(cancellation).ConfigureAwait(false);
            }
        }

        /// <summary>Encodes the next block if there is one.</summary>
        /// <returns>The encoded length; 0 for nothing waiting; -1 once completed.</returns>
        public int TryDequeue(Span<byte> message)
        {
            lock (_lock)
            {
                if (_completed)
                {
                    return -1;
                }

                if (_size == 0)
                {
                    return 0;
                }

                int slot = _head;
                byte flags = _flags[slot];
                if (_gapPending)
                {
                    flags |= FlagGap;
                    _gapPending = false;
                }

                int length = Encode(message, _indices[slot], _slots[slot].AsSpan(0, _counts[slot]), flags);
                _head = (_head + 1) % _slots.Length;
                _size--;
                return length;
            }
        }

        public void Complete()
        {
            lock (_lock)
            {
                _completed = true;
            }

            _signal.Release();
        }
    }
}

/// <summary>A program reading the channel audio stream that said who it is (issue #586).</summary>
/// <param name="Name">Its name, cleaned by <see cref="ChannelAudioStream.CleanName"/>.</param>
/// <param name="PagePort">Its own page's port on this machine, when it gave one.</param>
public readonly record struct Listener(string Name, int? PagePort);
