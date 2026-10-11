namespace Packet.SoundModem.OpenWebRx;

/// <summary>
/// How to listen through an OpenWebRX receiver: where, with which of its demodulators, through
/// what passband, and what to deliver.
/// </summary>
/// <remarks>
/// The receiver demodulates, so unlike an UberSDR's IQ the filter, the AGC and the demodulator
/// are all the receiver's. What this chooses is which of them, and the passband it asks for,
/// which the server applies as it stands.
/// </remarks>
public sealed record OpenWebRxTuning
{
    /// <summary>The dial, in Hz: the suppressed carrier for SSB, the channel for FM.</summary>
    public required long FrequencyHz { get; init; }

    /// <summary>The receiver's demodulator: <c>usb</c>, <c>lsb</c> or <c>nfm</c>.</summary>
    public string Modulation { get; init; } = "usb";

    /// <summary>
    /// Lower edge of the passband, Hz from the dial, as OpenWebRX takes it: positive for USB,
    /// negative for LSB, and minus the half-width for FM. See <see cref="For"/>.
    /// </summary>
    public int LowCutHz { get; init; } = 150;

    /// <summary>Upper edge of the passband, Hz from the dial. See <see cref="LowCutHz"/>.</summary>
    public int HighCutHz { get; init; } = 3450;

    /// <summary>The audio rate to deliver, Hz: the channel's DSP rate. A whole multiple of the
    /// receiver's 12 kHz.</summary>
    public int OutputRate { get; init; } = OpenWebRxProtocol.AudioRate;

    /// <summary>
    /// The receiver profile to ask for, by its id (<c>sdr|profile</c>) or its name as the page's
    /// list shows it; null to listen on whatever band the receiver is on.
    /// </summary>
    public string? Profile { get; init; }

    /// <summary>Audio discarded after each connect, in milliseconds, while the receiver's AGC
    /// settles on the new stream.</summary>
    public int StartupGuardMs { get; init; } = 1000;

    /// <summary>Linear gain on the delivered audio.</summary>
    public float Gain { get; init; } = 1.0f;

    /// <summary>
    /// The tuning for a station's sideband: <paramref name="sideband"/> is <c>usb</c>,
    /// <c>lsb</c> or <c>fm</c>, and the passband is given the way the station thinks of it.
    /// </summary>
    /// <param name="dialHz">The dial.</param>
    /// <param name="sideband">The station's sideband.</param>
    /// <param name="audioLowHz">For SSB, the bottom of the audio passband, Hz.</param>
    /// <param name="audioHighHz">For SSB, the top of the audio passband, Hz.</param>
    /// <param name="fmHalfWidthHz">For FM, half the channel filter's width, Hz.</param>
    public static OpenWebRxTuning For(
        long dialHz, string sideband, int audioLowHz, int audioHighHz, int fmHalfWidthHz)
    {
        ArgumentNullException.ThrowIfNull(sideband);
        if (sideband.Equals("fm", StringComparison.OrdinalIgnoreCase))
        {
            return new OpenWebRxTuning
            {
                FrequencyHz = dialHz,
                Modulation = "nfm",
                LowCutHz = -fmHalfWidthHz,
                HighCutHz = fmHalfWidthHz,
            };
        }

        bool lower = sideband.Equals("lsb", StringComparison.OrdinalIgnoreCase);
        return new OpenWebRxTuning
        {
            FrequencyHz = dialHz,
            Modulation = lower ? "lsb" : "usb",
            LowCutHz = lower ? -audioHighHz : audioLowHz,
            HighCutHz = lower ? -audioLowHz : audioHighHz,
        };
    }
}
