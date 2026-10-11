using System.Runtime.InteropServices;

namespace Packet.SoundModem.Audio;

/// <summary>
/// Thin ALSA PCM wrapper over <c>libasound.so.2</c> P/Invoke - capture and playback of
/// interleaved S16_LE. Linux-only by design (headless Pi is the primary deployment; a
/// cross-platform backend can join behind the same shape later). Buffer and period are asked
/// for separately through hw_params, because a capture stream wants a deep buffer and short
/// periods at the same time and <c>snd_pcm_set_params</c> cannot say that.
/// </summary>
public sealed class AlsaPcm : IDisposable, IPcmTransfer
{
    /// <summary>PCM direction.</summary>
    public enum Direction
    {
        /// <summary>Audio out.</summary>
        Playback = 0,

        /// <summary>Audio in.</summary>
        Capture = 1,
    }

    /// <summary>Default buffer, in microseconds. What the daemon ran on before the capture buffer
    /// was deepened, and still what playback uses.</summary>
    public const int DefaultBufferMicroseconds = 120_000;

    /// <summary>Default period, in microseconds: how often the card wakes the reader, and the
    /// granularity of a blocking read. Kept short whatever the buffer is, because it is receive
    /// latency and the buffer is not.</summary>
    public const int DefaultPeriodMicroseconds = 30_000;

    private const string Lib = "libasound.so.2";
    private const int FormatS16Le = 2;
    private const int AccessRwInterleaved = 3;
    private const int Einval = 22;

    private IntPtr _pcm;
    private int _xruns;

    // Only meaningful inside Read/Write, where it points at the caller's pinned span. A field
    // rather than an argument so the transfer loop can be shared without a per-call closure:
    // Read is on the receive hot path and must not allocate. One AlsaPcm is one direction and
    // one thread, which the wrappers above it already assume (they reuse one scratch buffer).
    private IntPtr _transferBuffer;

    private AlsaPcm(
        IntPtr pcm, Direction direction, int channels, int sampleRate,
        int bufferFrames, int periodFrames, string? configurationWarning)
    {
        _pcm = pcm;
        Dir = direction;
        Channels = channels;
        SampleRate = sampleRate;
        BufferFrames = bufferFrames;
        PeriodFrames = periodFrames;
        ConfigurationWarning = configurationWarning;
    }

    /// <summary>The direction this PCM was opened for.</summary>
    public Direction Dir { get; }

    /// <summary>Interleaved channel count.</summary>
    public int Channels { get; }

    /// <summary>Configured sample rate (the device may resample via the plug layer).</summary>
    public int SampleRate { get; }

    /// <summary>The buffer the card actually gave us, in frames (0 if it would not say).</summary>
    public int BufferFrames { get; }

    /// <summary>The period the card actually gave us, in frames (0 if it would not say).</summary>
    public int PeriodFrames { get; }

    /// <summary>The buffer in milliseconds - how long the reader can be away before audio is
    /// lost, which is the number that matters at start-up.</summary>
    public int BufferMilliseconds => Milliseconds(BufferFrames);

    /// <summary>The period in milliseconds.</summary>
    public int PeriodMilliseconds => Milliseconds(PeriodFrames);

    /// <summary>Why the buffer and period asked for were not the ones used, naming the errno, or
    /// null when they were. Worth a journal line: a card that will not take the deep buffer is
    /// otherwise invisible short of an strace.</summary>
    public string? ConfigurationWarning { get; }

    /// <summary>Number of xruns recovered so far - capture overruns or playback
    /// underruns. Recovery is lossy: the stream restarts, so every xrun is a
    /// discontinuity in the sample stream and can corrupt a frame in flight. Non-zero
    /// here means the machine is not keeping up, and long frames (300 baud runs for
    /// seconds) are hit hardest.</summary>
    public int Xruns => _xruns;

    /// <inheritdoc />
    bool IPcmTransfer.IsCapture => Dir == Direction.Capture;

    /// <summary>Opens and configures a PCM device (e.g. "default", "plughw:0,0").</summary>
    /// <param name="device">ALSA device name.</param>
    /// <param name="direction">Capture or playback.</param>
    /// <param name="channels">Interleaved channels (1 or 2).</param>
    /// <param name="sampleRate">Requested rate; capture at the card-native 48000 and
    /// decimate in the DSP rather than letting the plug layer's linear resampler run.</param>
    /// <param name="bufferMicroseconds">How much audio the card holds for us. On capture this is
    /// how long the receive loop may be away before an overrun; on playback it is also how long
    /// after the first write the transmission starts, so the two are not set alike.</param>
    /// <param name="periodMicroseconds">How often the card wakes us, and the granularity of a
    /// blocking transfer. 0 takes the default, or a quarter of the buffer if that is shorter.</param>
    /// <exception cref="InvalidOperationException">The device could not be opened or
    /// configured.</exception>
    public static AlsaPcm Open(
        string device, Direction direction, int channels, int sampleRate,
        int bufferMicroseconds = DefaultBufferMicroseconds,
        int periodMicroseconds = 0)
    {
        int period = periodMicroseconds > 0
            ? periodMicroseconds
            : Math.Min(DefaultPeriodMicroseconds, Math.Max(1, bufferMicroseconds / 4));

        int err = snd_pcm_open(out IntPtr pcm, device, (int)direction, 0);
        Throw(err, $"snd_pcm_open({device})");

        // hw_params by hand, because the buffer and the period have to be asked for separately.
        // snd_pcm_set_params takes one latency figure and derives the period from it (a quarter),
        // so buying a 500 ms capture buffer through it would also make every read block for
        // 125 ms, and receive latency is not what we are trying to spend here.
        int bufferFrames = 0;
        int periodFrames = 0;
        int refused = ConfigureHardware(pcm, channels, sampleRate, bufferMicroseconds, period);
        bool startsOnFirstRead = false;
        if (refused >= 0)
        {
            ReadParams(pcm, out bufferFrames, out periodFrames);
            refused = ConfigureSoftware(pcm, direction, bufferFrames, periodFrames);
            startsOnFirstRead = refused >= 0 && direction == Direction.Capture;
        }

        // Exactly the configuration this daemon shipped with, which means the latency that yields
        // the period asked for and NOT the deep buffer: snd_pcm_set_params derives one from the
        // other, and handing it 500 ms here would buy the buffer back at the price of a 125 ms
        // period, which is the thing the hand-rolled path exists to avoid.
        int fallbackLatency = Math.Min(bufferMicroseconds, period * 4);

        // Playback cannot be left half-configured. alsa-lib's default after a hand-rolled
        // snd_pcm_hw_params is a start threshold of 1, which on playback means transmitting from
        // the first frame in the buffer and underrunning at once, so a playback stream whose
        // sw_params would not take is configured from scratch instead. Capture is safe either
        // way: at 1 it is what we asked for, and at the library default the explicit start below
        // covers it, so capture keeps the deep buffer it did get.
        bool fellBack = refused < 0 && (direction == Direction.Playback || bufferFrames <= 0);
        if (fellBack)
        {
            err = snd_pcm_set_params(
                pcm, FormatS16Le, AccessRwInterleaved, (uint)channels, (uint)sampleRate,
                softResample: 1, latency: (uint)fallbackLatency);
            if (err < 0)
            {
                snd_pcm_close(pcm);
                Throw(err, $"snd_pcm_set_params({device}, {sampleRate} Hz, {channels} ch)");
            }

            ReadParams(pcm, out bufferFrames, out periodFrames);
            startsOnFirstRead = false;
        }

        // Named, with the errno, because a card that will not take the deep buffer still runs
        // and is otherwise invisible: it is a station one stalled start-up away from the bug all
        // of this is about, and the next person should not have to strace it to find that out.
        string? refusal = refused >= 0
            ? null
            : $"{device} would not take a {Round(bufferMicroseconds)} ms buffer with a "
              + $"{Round(period)} ms period ({Message(refused)}) - "
              + (fellBack
                  ? $"running on the {Round(fallbackLatency)} ms default configuration instead"
                  : "keeping it, and starting the capture stream at open instead");

        var opened = new AlsaPcm(
            pcm, direction, channels, sampleRate, bufferFrames, periodFrames, refusal);

        if (direction == Direction.Capture && !startsOnFirstRead)
        {
            // Only when the start threshold is not ours. With it set to 1 the stream starts on
            // the first read, which is when the receive loop is actually ready for audio - better
            // than starting it here and letting it fill while the first pass through the modem is
            // still being JIT-compiled. Otherwise it is either the library default after a
            // hand-rolled hw_params, or the buffer size that snd_pcm_set_params leaves, which no
            // read here is ever big enough to reach; some USB codecs answer the first read with
            // -EIO in that state and an explicit start is the only thing that makes them stream.
            // Harmless where it is not needed: the stream is PREPARED at this point.
            err = snd_pcm_start(pcm);
            if (err < 0)
            {
                opened.Dispose();
                Throw(err, $"snd_pcm_start({device})");
            }
        }

        return opened;
    }

    /// <summary>Reads interleaved frames (capture PCM). Blocks until the span is filled.
    /// Recovers from overruns transparently.</summary>
    /// <returns>Frames read (normally the full span).</returns>
    public int Read(Span<short> interleaved)
    {
        ObjectDisposedException.ThrowIf(_pcm == IntPtr.Zero, this);
        return Transfer(interleaved, "snd_pcm_readi");
    }

    // A fault ReadUntilOverrun met after it had frames to return, left for its next call.
    private int _deferredHole;

    /// <summary>Reads interleaved frames (capture PCM), as <see cref="Read"/> does, except that an
    /// overrun ends the read: the frames returned never span one. The overrun is counted in
    /// <see cref="Xruns"/>, and recovered, by the call that returns the first frames after it, so
    /// an increase seen after a call means the hole lies immediately before what it returned.</summary>
    /// <returns>Frames read: the full span, or fewer when an overrun stopped it.</returns>
    public int ReadUntilOverrun(Span<short> interleaved)
    {
        ObjectDisposedException.ThrowIf(_pcm == IntPtr.Zero, this);
        return Transfer(interleaved, "snd_pcm_readi", stopAtXrun: true);
    }

    /// <summary>Writes interleaved frames (playback PCM). Blocks until consumed -
    /// device-paced, which is exactly what sample-accurate PTT timing needs.
    /// Recovers from underruns transparently.</summary>
    public void Write(ReadOnlySpan<short> interleaved)
    {
        ObjectDisposedException.ThrowIf(_pcm == IntPtr.Zero, this);
        _ = Transfer(interleaved, "snd_pcm_writei");
    }

    /// <summary>Blocks until everything written has actually played (playback only) -
    /// the sample-domain part of releasing PTT.</summary>
    public void Drain()
    {
        ObjectDisposedException.ThrowIf(_pcm == IntPtr.Zero, this);
        _ = snd_pcm_drain(_pcm);

        // Draining leaves the PCM in SETUP state; without a re-prepare the next
        // writei fails with EBADFD. Re-arm so the handle stays reusable across
        // transmissions.
        _ = snd_pcm_prepare(_pcm);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_pcm != IntPtr.Zero)
        {
            snd_pcm_close(_pcm);
            _pcm = IntPtr.Zero;
        }
    }

    /// <inheritdoc />
    long IPcmTransfer.Transfer(int frameOffset, int frames)
    {
        IntPtr at = _transferBuffer + (frameOffset * Channels * sizeof(short));
        return Dir == Direction.Capture
            ? snd_pcm_readi(_pcm, at, (ulong)frames)
            : snd_pcm_writei(_pcm, at, (ulong)frames);
    }

    /// <inheritdoc />
    int IPcmTransfer.Recover(int error) => snd_pcm_recover(_pcm, error, 1);

    /// <inheritdoc />
    int IPcmTransfer.Prepare() => snd_pcm_prepare(_pcm);

    /// <inheritdoc />
    int IPcmTransfer.Start() => snd_pcm_start(_pcm);

    /// <inheritdoc />
    void IPcmTransfer.CountXrun() => Interlocked.Increment(ref _xruns);

    /// <inheritdoc />
    void IPcmTransfer.Pause(int milliseconds) => Thread.Sleep(milliseconds);

    private static void Throw(int err, string operation)
    {
        if (err < 0)
        {
            throw new InvalidOperationException($"{operation}: {Message(err)}");
        }
    }

    /// <summary>alsa-lib's own words for an errno.</summary>
    private static string Message(int err) =>
        Marshal.PtrToStringAnsi(snd_strerror(err)) ?? $"error {err}";

    private static int Round(int microseconds) => (int)Math.Round(microseconds / 1000.0);

    /// <summary>What the card settled on, in frames. Zero for a device that will not say.</summary>
    private static void ReadParams(IntPtr pcm, out int bufferFrames, out int periodFrames)
    {
        bufferFrames = 0;
        periodFrames = 0;
        if (snd_pcm_get_params(pcm, out CULong buffer, out CULong period) >= 0)
        {
            bufferFrames = (int)buffer.Value;
            periodFrames = (int)period.Value;
        }
    }

    /// <summary>Format, rate and the buffer/period pair, asked for explicitly.</summary>
    /// <returns>0, or the negative errno of the step that would not take.</returns>
    private static int ConfigureHardware(
        IntPtr pcm, int channels, int sampleRate, int bufferMicroseconds, int periodMicroseconds)
    {
        int err = snd_pcm_hw_params_malloc(out IntPtr hw);
        if (err < 0)
        {
            return err;
        }

        try
        {
            err = snd_pcm_hw_params_any(pcm, hw);
            if (err < 0)
            {
                return err;
            }

            // Same as snd_pcm_set_params' softResample: 1 - let the plug layer convert a rate the
            // card does not have rather than failing to open.
            err = snd_pcm_hw_params_set_rate_resample(pcm, hw, 1);
            if (err < 0)
            {
                return err;
            }

            err = snd_pcm_hw_params_set_access(pcm, hw, AccessRwInterleaved);
            if (err < 0)
            {
                return err;
            }

            err = snd_pcm_hw_params_set_format(pcm, hw, FormatS16Le);
            if (err < 0)
            {
                return err;
            }

            err = snd_pcm_hw_params_set_channels(pcm, hw, (uint)channels);
            if (err < 0)
            {
                return err;
            }

            uint rate = (uint)sampleRate;
            int dir = 0;
            err = snd_pcm_hw_params_set_rate_near(pcm, hw, ref rate, ref dir);
            if (err < 0)
            {
                return err;
            }

            if (rate != (uint)sampleRate)
            {
                // What set_params does, and for the same reason: the DSP downstream is built for
                // the rate it asked for, and a stream at another one is nonsense at both ends.
                return -Einval;
            }

            // Period first, then the buffer around it: asked for the other way round, a card that
            // rounds the buffer down can leave no room for the period we wanted.
            uint periodTime = (uint)periodMicroseconds;
            dir = 0;
            err = snd_pcm_hw_params_set_period_time_near(pcm, hw, ref periodTime, ref dir);
            if (err < 0)
            {
                return err;
            }

            uint bufferTime = (uint)bufferMicroseconds;
            dir = 0;
            err = snd_pcm_hw_params_set_buffer_time_near(pcm, hw, ref bufferTime, ref dir);
            if (err < 0)
            {
                return err;
            }

            return snd_pcm_hw_params(pcm, hw);
        }
        finally
        {
            snd_pcm_hw_params_free(hw);
        }
    }

    /// <summary>When the stream starts, and how much has to be there before a transfer returns.</summary>
    /// <returns>0, or the negative errno of the step that would not take.</returns>
    private static int ConfigureSoftware(
        IntPtr pcm, Direction direction, int bufferFrames, int periodFrames)
    {
        if (bufferFrames <= 0 || periodFrames <= 0)
        {
            return -Einval;
        }

        int err = snd_pcm_sw_params_malloc(out IntPtr sw);
        if (err < 0)
        {
            return err;
        }

        try
        {
            err = snd_pcm_sw_params_current(pcm, sw);
            if (err < 0)
            {
                return err;
            }

            err = snd_pcm_sw_params_set_avail_min(pcm, sw, new CULong((nuint)periodFrames));
            if (err < 0)
            {
                return err;
            }

            // Capture starts on the first read of any size; playback when the buffer is full.
            //
            // The capture threshold is the point. snd_pcm_set_params leaves it at the buffer
            // size, and a reader that asks for less than a whole buffer at a time - which is
            // every reader here - then never trips it, so the stream has to be started by hand
            // before the loop is ready to read it, and free-runs until it is. At 1 the stream
            // starts when the audio is wanted.
            //
            // Playback is the opposite case and keeps set_params' answer: starting on the first
            // frame written would underrun on the next one.
            err = snd_pcm_sw_params_set_start_threshold(
                pcm, sw, new CULong((nuint)(direction == Direction.Capture ? 1 : bufferFrames)));
            if (err < 0)
            {
                return err;
            }

            err = snd_pcm_sw_params_set_stop_threshold(pcm, sw, new CULong((nuint)bufferFrames));
            if (err < 0)
            {
                return err;
            }

            return snd_pcm_sw_params(pcm, sw);
        }
        finally
        {
            snd_pcm_sw_params_free(sw);
        }
    }

    private int Milliseconds(int frames) =>
        SampleRate > 0 && frames > 0 ? (int)Math.Round(frames * 1000.0 / SampleRate) : 0;

    /// <summary>Pins the caller's span and turns the shared transfer loop over it.</summary>
    private int Transfer(ReadOnlySpan<short> interleaved, string operation, bool stopAtXrun = false)
    {
        int frameCount = interleaved.Length / Channels;
        int moved;
        int failure;
        unsafe
        {
            fixed (short* p = interleaved)
            {
                _transferBuffer = (IntPtr)p;
                moved = PcmTransfer.Run(this, frameCount, out failure, stopAtXrun, ref _deferredHole);
                _transferBuffer = IntPtr.Zero;
            }
        }

        Throw(failure, operation);
        return moved;
    }

    [DllImport(Lib)]
    private static extern int snd_pcm_open(out IntPtr pcm, string name, int stream, int mode);

    [DllImport(Lib)]
    private static extern int snd_pcm_set_params(
        IntPtr pcm, int format, int access, uint channels, uint rate, int softResample, uint latency);

    // snd_pcm_uframes_t is C unsigned long, so CULong: 32-bit on armhf, 64-bit everywhere else.
    // The frame counts on readi/writei below have the same type and are declared as ulong - wrong
    // on armhf, right everywhere else, left alone deliberately (see the comment there and issue
    // #417). New bindings do not add to that debt.
    [DllImport(Lib)]
    private static extern int snd_pcm_get_params(
        IntPtr pcm, out CULong bufferSize, out CULong periodSize);

    [DllImport(Lib)]
    private static extern int snd_pcm_hw_params_malloc(out IntPtr parameters);

    [DllImport(Lib)]
    private static extern void snd_pcm_hw_params_free(IntPtr parameters);

    [DllImport(Lib)]
    private static extern int snd_pcm_hw_params_any(IntPtr pcm, IntPtr parameters);

    [DllImport(Lib)]
    private static extern int snd_pcm_hw_params_set_rate_resample(
        IntPtr pcm, IntPtr parameters, uint value);

    [DllImport(Lib)]
    private static extern int snd_pcm_hw_params_set_access(
        IntPtr pcm, IntPtr parameters, int access);

    [DllImport(Lib)]
    private static extern int snd_pcm_hw_params_set_format(
        IntPtr pcm, IntPtr parameters, int format);

    [DllImport(Lib)]
    private static extern int snd_pcm_hw_params_set_channels(
        IntPtr pcm, IntPtr parameters, uint channels);

    [DllImport(Lib)]
    private static extern int snd_pcm_hw_params_set_rate_near(
        IntPtr pcm, IntPtr parameters, ref uint rate, ref int dir);

    [DllImport(Lib)]
    private static extern int snd_pcm_hw_params_set_period_time_near(
        IntPtr pcm, IntPtr parameters, ref uint microseconds, ref int dir);

    [DllImport(Lib)]
    private static extern int snd_pcm_hw_params_set_buffer_time_near(
        IntPtr pcm, IntPtr parameters, ref uint microseconds, ref int dir);

    [DllImport(Lib)]
    private static extern int snd_pcm_hw_params(IntPtr pcm, IntPtr parameters);

    [DllImport(Lib)]
    private static extern int snd_pcm_sw_params_malloc(out IntPtr parameters);

    [DllImport(Lib)]
    private static extern void snd_pcm_sw_params_free(IntPtr parameters);

    [DllImport(Lib)]
    private static extern int snd_pcm_sw_params_current(IntPtr pcm, IntPtr parameters);

    [DllImport(Lib)]
    private static extern int snd_pcm_sw_params_set_avail_min(
        IntPtr pcm, IntPtr parameters, CULong frames);

    [DllImport(Lib)]
    private static extern int snd_pcm_sw_params_set_start_threshold(
        IntPtr pcm, IntPtr parameters, CULong frames);

    [DllImport(Lib)]
    private static extern int snd_pcm_sw_params_set_stop_threshold(
        IntPtr pcm, IntPtr parameters, CULong frames);

    [DllImport(Lib)]
    private static extern int snd_pcm_sw_params(IntPtr pcm, IntPtr parameters);

    // snd_pcm_sframes_t and snd_pcm_uframes_t are C long, which is 32-bit on armhf and so is not
    // the C# long and ulong declared here. Wrong on the armhf .deb the release builds; correct
    // everywhere else. Left as it is on purpose rather than by oversight - see AlsaMixer's
    // comment above its own volume bindings for the same fault fixed with CLong, and issue #417
    // for why this one changes every call site and wants a 32-bit bench run of its own.
    [DllImport(Lib)]
    private static extern long snd_pcm_readi(IntPtr pcm, IntPtr buffer, ulong frames);

    [DllImport(Lib)]
    private static extern long snd_pcm_writei(IntPtr pcm, IntPtr buffer, ulong frames);

    [DllImport(Lib)]
    private static extern int snd_pcm_recover(IntPtr pcm, int err, int silent);

    [DllImport(Lib)]
    private static extern int snd_pcm_start(IntPtr pcm);

    [DllImport(Lib)]
    private static extern int snd_pcm_prepare(IntPtr pcm);

    [DllImport(Lib)]
    private static extern int snd_pcm_drain(IntPtr pcm);

    [DllImport(Lib)]
    private static extern int snd_pcm_close(IntPtr pcm);

    [DllImport(Lib)]
    private static extern IntPtr snd_strerror(int error);
}
