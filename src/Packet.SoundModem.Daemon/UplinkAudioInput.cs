using System.Buffers;
using System.Buffers.Binary;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Audio;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// A relayed station's audio, arriving over its uplink socket and read by an ordinary
/// <see cref="Station"/> receive loop as if it came off a sound card.
/// </summary>
/// <remarks>
/// <para>This is the whole of the monitor's side of the seam for audio: a relayed station is an
/// ordinary station over a socket-fed input, with no modems, so the waterfall, the band overlays,
/// the browser audio and the page all come out of code that is already in production. See
/// <c>docs/dev/archive/uplink-plan.md</c> 3.2 and 4.1.</para>
/// <para><b>The buffer is bounded and drops the oldest.</b> Everything here arrives from another
/// machine over a socket this process does not control the pace of, and an unbounded accumulator
/// on such a path is the one thing the plan names as the pattern to avoid. When the buffer
/// overruns, the oldest audio goes: a late block is worth less than a live one to somebody
/// watching a waterfall, and a buffer that grew instead would turn a slow reader into a memory
/// leak with a display minutes behind the band.</para>
/// <para><b>A block is never half transmitted and half received.</b> The uplink labels each block
/// as heard or as the station's own transmission, and <see cref="Read"/> stops at a change of
/// kind rather than returning a mixture, so the flag it sets on the way out describes every
/// sample it returned. Reading and processing are the same thread
/// (<c>Station</c>'s receive loop), so the flag is exact rather than nearly right.</para>
/// </remarks>
internal sealed class UplinkAudioInput : IAudioInput, IDisposable
{
    /// <summary>
    /// How long <see cref="Read"/> waits for audio before returning nothing.
    /// </summary>
    /// <remarks>
    /// The same shape every network input in this tree has: wait inside <c>Read</c> and return 0,
    /// so the receive loop needs no backoff of its own and never spins. A station nobody is
    /// watching sends no audio at all, so this is the ordinary case rather than a failure.
    /// </remarks>
    internal static readonly TimeSpan ReadWait = TimeSpan.FromMilliseconds(100);

    private readonly Lock _gate = new();
    private readonly Queue<Block> _blocks = new();
    private readonly ManualResetEventSlim _arrived = new(false);
    private readonly Action<bool> _setTransmit;
    private readonly int _capacitySamples;

    private long _accepted;
    private long _consumed;
    private long _dropped;
    private long _breaks;
    private long _lossAtRead;
    private bool _disposed;

    /// <param name="sampleRate">The rate the station said it is relaying at, in its hello.</param>
    /// <param name="setTransmit">
    /// Told, immediately before each block is returned, whether that block is the station's own
    /// transmission. Wired to <c>WaterfallWebServer.IncomingIsTransmit</c>, which is what marks
    /// the line the block produces as ours.
    /// </param>
    /// <param name="bufferSeconds">
    /// How much audio the jitter buffer holds before it starts dropping the oldest. Two seconds
    /// is far more than the wire jitter of a healthy link and far less than a display anybody
    /// would call live.
    /// </param>
    internal UplinkAudioInput(int sampleRate, Action<bool> setTransmit, double bufferSeconds = 2.0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        ArgumentNullException.ThrowIfNull(setTransmit);
        SampleRate = sampleRate;
        _setTransmit = setTransmit;
        _capacitySamples = Math.Max(1, (int)(sampleRate * bufferSeconds));
    }

    /// <inheritdoc />
    public int SampleRate { get; }

    /// <summary>
    /// Whether a station is connected on this input right now. False is not a fault: it is a
    /// station that is off the air, or between reconnects, and its page goes on serving its
    /// history either way.
    /// </summary>
    internal bool Connected { get; set; }

    /// <summary>
    /// Called at the top of every <see cref="Read"/>, before it waits. What rides here is the
    /// frame hold: a frame is released once the audio that carried it has been read, and the only
    /// thing that knows the audio has been read is this loop.
    /// </summary>
    internal Action? BeforeRead { get; set; }

    /// <summary>Total samples ever accepted into the buffer, dropped ones included.</summary>
    /// <remarks>
    /// Read when a frame arrives, to say how much audio has to leave the buffer before that
    /// frame is tagged onto a line. See <see cref="Consumed"/>.
    /// </remarks>
    internal long Accepted => Interlocked.Read(ref _accepted);

    /// <summary>
    /// Total samples ever taken out of the buffer, whether they were read or dropped.
    /// </summary>
    /// <remarks>
    /// Dropped samples count, and that is the point: a frame waiting on audio that was dropped
    /// for overrunning would otherwise wait for ever. This way the hold is self-correcting across
    /// an overrun and across a reconnect.
    /// </remarks>
    internal long Consumed => Interlocked.Read(ref _consumed);

    /// <summary>Samples thrown away because the buffer overran, for the journal.</summary>
    internal long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>
    /// A running count that moves whenever the audio this input hands out has a hole in it: the
    /// samples the jitter buffer had no room for, plus one for each <see cref="MarkBreak"/> (the
    /// station reconnected, or started sending again after nobody was watching). As of the last
    /// <see cref="Read"/> that delivered audio, so the count the receive loop reads after a Read
    /// describes exactly the samples that Read handed out. A Read that returns nothing is not a
    /// hole: the socket delivers late audio whole.
    /// </summary>
    internal long Discontinuities => Interlocked.Read(ref _lossAtRead);

    /// <summary>
    /// Says the audio after this point does not follow on from the audio before it: the station
    /// reconnected, or is sending again after a spell with nobody watching. Called before any of
    /// the new audio arrives. Nothing is marked for an input that has never had audio, because
    /// there is nothing before it to be discontinuous with.
    /// </summary>
    internal void MarkBreak()
    {
        lock (_gate)
        {
            if (_accepted > 0)
            {
                _breaks++;
            }
        }
    }

    /// <summary>Samples waiting to be read.</summary>
    internal long Buffered => Accepted - Consumed;

    /// <summary>
    /// Takes one block of relayed audio off the wire.
    /// </summary>
    /// <param name="pcm">The block, 16-bit PCM at <see cref="SampleRate"/>.</param>
    /// <param name="transmitted">
    /// False for audio the station heard, true for its own transmission.
    /// </param>
    internal void Push(ReadOnlySpan<byte> pcm, bool transmitted)
    {
        int count = pcm.Length / 2;
        if (count == 0)
        {
            return;
        }

        // Rented, not allocated: this runs 25 times a second per watched station, and a float
        // array per block is the sort of steady-state garbage CLAUDE.md's DSP rule exists to
        // keep out of a receive path. Returned when the block is read or dropped.
        float[] samples = ArrayPool<float>.Shared.Rent(count);
        for (int i = 0; i < count; i++)
        {
            samples[i] = Pcm16.ToFloat(BinaryPrimitives.ReadInt16LittleEndian(pcm[(2 * i)..]));
        }

        lock (_gate)
        {
            if (_disposed)
            {
                ArrayPool<float>.Shared.Return(samples);
                return;
            }

            _blocks.Enqueue(new Block(samples, count, transmitted));
            _accepted += count;

            // Oldest first, and one whole block at a time: half a block would leave a fragment
            // whose start nothing lines up with, and the display is being drawn from a stream
            // that has already lost continuity by the time this fires.
            while (_accepted - _consumed > _capacitySamples && _blocks.Count > 1)
            {
                Block oldest = _blocks.Dequeue();
                int lost = oldest.Length - oldest.Offset;
                _consumed += lost;
                _dropped += lost;
                ArrayPool<float>.Shared.Return(oldest.Samples);
            }

            _arrived.Set();
        }
    }

    /// <summary>
    /// Throws away everything waiting, for a station that has just reconnected.
    /// </summary>
    /// <remarks>
    /// Audio from before a disconnect is audio from before a gap of unknown length, and painting
    /// it after the reconnect would put a burst on the display minutes after it happened. The
    /// dropped samples count as consumed, exactly as an overrun's do, so any frame held against
    /// them is released rather than waiting for audio that is never coming.
    /// </remarks>
    internal void Flush()
    {
        lock (_gate)
        {
            while (_blocks.Count > 0)
            {
                Block block = _blocks.Dequeue();
                _consumed += block.Length - block.Offset;
                ArrayPool<float>.Shared.Return(block.Samples);
            }

            _arrived.Reset();
        }
    }

    /// <inheritdoc />
    public int Read(Span<float> destination)
    {
        // Before the wait, not after it: a frame that arrived with the buffer already empty has
        // nothing to wait for and should be listed now, and this is the only place that runs
        // often enough to notice.
        BeforeRead?.Invoke();

        if (destination.IsEmpty)
        {
            return 0;
        }

        int got = TryTake(destination);
        if (got > 0)
        {
            return got;
        }

        // Waits inside Read and returns 0, like every other network input here, so the receive
        // loop needs no backoff of its own and a station nobody is watching costs one wait per
        // hundred milliseconds.
        _arrived.Wait(ReadWait);
        return TryTake(destination);
    }

    private int TryTake(Span<float> destination)
    {
        bool transmitted;
        int written;

        lock (_gate)
        {
            if (_blocks.Count == 0)
            {
                _arrived.Reset();
                return 0;
            }

            transmitted = _blocks.Peek().Transmitted;
            written = 0;

            // Up to a change of kind, and no further. A mixed block would paint a keyup and the
            // band it interrupted onto one line and mark the whole thing as one or the other.
            while (written < destination.Length
                   && _blocks.Count > 0
                   && _blocks.Peek().Transmitted == transmitted)
            {
                Block block = _blocks.Peek();
                int available = block.Length - block.Offset;
                int take = Math.Min(available, destination.Length - written);
                block.Samples.AsSpan(block.Offset, take).CopyTo(destination[written..]);
                written += take;
                block.Offset += take;
                if (block.Offset == block.Length)
                {
                    _blocks.Dequeue();
                    ArrayPool<float>.Shared.Return(block.Samples);
                }
            }

            _consumed += written;
            Interlocked.Exchange(ref _lossAtRead, _dropped + _breaks);
            if (_blocks.Count == 0)
            {
                _arrived.Reset();
            }
        }

        // Outside the lock but before returning, which is the whole contract: the caller is about
        // to hand these samples to ProcessReceive on this same thread.
        _setTransmit(transmitted);
        return written;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            while (_blocks.Count > 0)
            {
                ArrayPool<float>.Shared.Return(_blocks.Dequeue().Samples);
            }
        }

        // Set rather than reset: a loop sitting in the wait has to come out of it, find nothing
        // and return 0, which is what lets it notice it has been cancelled. Deliberately not
        // disposed: a receive loop may be inside Wait at this moment - that is the normal way
        // this input is taken down - and disposing the event under it turns an orderly stop into
        // an ObjectDisposedException on a thread with nowhere to report it. One lazily-created
        // wait handle per station, reclaimed by its own finaliser, is the cheaper mistake.
        _arrived.Set();
    }

    /// <summary>
    /// One block as it arrived, with how much of it has been read.
    /// </summary>
    /// <remarks>
    /// <see cref="Samples"/> is rented from the pool and is longer than the block, which is why
    /// <see cref="Length"/> exists: the array's own length is whatever the pool had to hand and
    /// says nothing about how much audio is in it.
    /// </remarks>
    private sealed class Block(float[] samples, int length, bool transmitted)
    {
        internal float[] Samples { get; } = samples;

        internal int Length { get; } = length;

        internal bool Transmitted { get; } = transmitted;

        internal int Offset { get; set; }
    }
}
