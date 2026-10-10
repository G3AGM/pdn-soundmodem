namespace Packet.SoundModem.Modems;

/// <summary>
/// Reads a recording with one mode, offline: build the receiver, feed it the whole thing, collect
/// what it decoded.
/// </summary>
/// <remarks>
/// <para>
/// The primitive under every "what is in this audio" question the tree asks - the
/// <c>pdn-decode</c> sweep across the catalogue, and the station's own
/// <see cref="Survey.CaptureSweep"/> over a survey capture. Both used to carry their own copy,
/// and the three decisions below are exactly the ones that must not be allowed to differ between
/// a tool's answer and a station's.
/// </para>
/// <para>
/// <b>Driven off <see cref="IModem.FrameDecoded"/>, not the constructor's frame sink.</b> The
/// event is a superset: every frame that reaches the sink also raises it, and a frame the
/// receiver read but would not hand to a host - plain IL2P heard by an IL2P+CRC link, marked
/// <see cref="FrameQuality.MonitorOnly"/> - raises it and never reaches the sink at all. Asking
/// what is in a recording means wanting exactly that frame, and wanting to be told it was held
/// back.
/// </para>
/// <para>
/// <b>Fed in blocks, not one span.</b> A diversity bank holds per-chunk candidate state and
/// compares its branches at chunk boundaries, so it behaves as it does on the air only when fed
/// something like air-sized pieces.
/// </para>
/// <para>
/// <b>Flushed with silence.</b> A recording that ends flush with its last closing flag still has
/// that frame inside the demodulator's FIR pipeline. A live stream never ends, so no modem does
/// this for itself - and a survey capture ends by construction.
/// </para>
/// </remarks>
public static class ModeReader
{
    /// <summary>Audio handed to a modem at a time. Air-sized, so a bank's branch comparison
    /// happens where it would on a live channel.</summary>
    private const int BlockSamples = 4096;

    /// <summary>
    /// Runs <paramref name="mode"/> over <paramref name="audio"/>, calling
    /// <paramref name="decoded"/> for every frame it reads.
    /// </summary>
    /// <param name="mode">A <see cref="ModemCatalog"/> mode.</param>
    /// <param name="audio">The recording, at <paramref name="dspRate"/>.</param>
    /// <param name="dspRate">Its rate, which must be one the mode runs at.</param>
    /// <param name="options">Per-mode knobs - a centre frequency, a detector - or default.</param>
    /// <param name="decoded">Called per frame, with the receiver's own diagnostics.</param>
    /// <param name="flushSilence">Whether to append half a second of silence to flush the
    /// pipeline. True for a recording; false when the caller has already done it.</param>
    /// <exception cref="ArgumentException">The mode cannot sit where it was pointed: its band
    /// would run off the end of the passband. <c>ParamName</c> is <c>centreHz</c>, which is what
    /// a caller should match on - the catalogue's two guards on a centre throw
    /// <see cref="ArgumentException"/> and <see cref="ArgumentOutOfRangeException"/> respectively,
    /// so catching the narrower type catches only one of them.</exception>
    public static void Run(
        string mode,
        ReadOnlySpan<float> audio,
        int dspRate,
        ModemOptions options,
        Action<byte[], FrameQuality> decoded,
        bool flushSilence = true)
    {
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(decoded);

        float[] samples;
        if (flushSilence)
        {
            samples = new float[audio.Length + (dspRate / 2)];
            audio.CopyTo(samples);
        }
        else
        {
            samples = audio.ToArray();
        }

        // The sink is required by the catalogue and deliberately ignored: see the type remarks
        // for why FrameDecoded is the honest source here. Built as a station's channel builds
        // it, so a 12 kHz mode reading 48 kHz audio runs at 12 kHz behind the rate bridge
        // (issue #648) - the same receiver the station itself would have used.
        IModem modem = ModemCatalog.CreateForChannel(mode, dspRate, _ => { }, options);
        try
        {
            modem.FrameDecoded += (frame, quality) => decoded(frame, quality);
            for (int offset = 0; offset < samples.Length; offset += BlockSamples)
            {
                modem.Process(samples.AsSpan(offset, Math.Min(BlockSamples, samples.Length - offset)));
            }
        }
        finally
        {
            (modem as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// The options to run <paramref name="mode"/> at <paramref name="centreHz"/>, or
    /// <c>default</c> where the mode has no centre to point.
    /// </summary>
    /// <remarks>
    /// The baseband <c>fsk*</c>/<c>c4fsk*</c> family occupies DC upwards and
    /// <see cref="ModemCatalog.Create"/> refuses a centre for it outright (issue #39), so asking
    /// is the caller's job and getting it wrong is an exception rather than a worse answer.
    /// </remarks>
    public static ModemOptions At(string mode, double centreHz) =>
        ModemCatalog.AcceptsCentreFrequency(mode)
            ? new ModemOptions(CentreFrequencyHz: centreHz)
            : default;
}

/// <summary>
/// How good a reading of a transmission is, for choosing between several modes' copies of one
/// burst.
/// </summary>
/// <remarks>
/// Several modes reading one burst is the normal case rather than a problem - a diversity bank
/// and its single-branch sibling both read plain AX.25 - so something has to say which of them to
/// name, and it must not be "whichever ran first". Shared between the <c>pdn-decode</c> report
/// and the station's own <see cref="Survey.ModemProspector"/>, because a tool telling an operator
/// one thing while the station acts on another is worse than either answer alone.
/// </remarks>
public static class DecodeConfidence
{
    /// <summary>Lowest is best: a frame the receiver would have handed to its host beats one it
    /// held back, and a verified check sequence beats Reed-Solomon standing alone.</summary>
    public static int Rank(FrameQuality quality) => quality switch
    {
        { MonitorOnly: true } => 3,
        { CrcValid: true } => 0,
        { PlainIl2p: true, TrailerNearBits: not null } => 1,
        { PlainIl2p: true } => 2,
        { CrcValid: false } => 2,
        _ => 0, // HDLC or FX.25: the FCS passed, which is the whole guarantee framing has
    };

    /// <summary>
    /// Whether a reading is evidence that somebody transmitted, as opposed to a receiver finding
    /// structure in noise.
    /// </summary>
    /// <remarks>
    /// Running thirty receivers over one recording is thirty chances to be wrong, and a
    /// Reed-Solomon-only decode with no verified check sequence is a real thing such a sweep
    /// produces - a sample run over the live 40 m station's captures turned one up on its first
    /// afternoon, 15 bytes of "qpsk2400" at 3044 Hz with no readable callsigns. A verified FCS or
    /// CRC is a different kind of statement: the bytes carry their own proof.
    /// </remarks>
    public static bool IsEvidence(FrameQuality quality) =>
        quality is { MonitorOnly: false }
            and ({ CrcValid: true } or { PlainIl2p: false, CrcValid: null });
}
