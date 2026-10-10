using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Survey;

/// <summary>One reading of a capture: a frame, and how it was read.</summary>
/// <param name="Mode">The catalogue mode that read it.</param>
/// <param name="CentreHz">The audio centre it was read at.</param>
/// <param name="Frame">The frame bytes.</param>
/// <param name="Quality">The receiver's own diagnostics.</param>
/// <param name="Source">Source callsign, where the frame yields one.</param>
/// <param name="Destination">Destination callsign, same.</param>
public sealed record CaptureReading(
    string Mode,
    double CentreHz,
    byte[] Frame,
    FrameQuality Quality,
    string? Source,
    string? Destination);

/// <summary>
/// Reads one survey capture with every mode that could plausibly have carried it, pointed at
/// the centre the survey measured.
/// </summary>
/// <remarks>
/// <para>
/// The narrow, station-side cousin of the <c>pdn-decode</c> tool. Where that sweeps the whole
/// catalogue over an unlabelled file and is allowed to take twenty seconds about it, this runs
/// unattended beside a live receiver and has to be cheap: one centre (the survey measured it),
/// one DSP rate (the station's own), and only the modes that could be what the capture holds.
/// </para>
/// <para>
/// <b>Only the modes the station could run.</b> A 12 kHz station's captures are 12 kHz, and this
/// deliberately does not resample them to try the 48 kHz modes: a station that cannot run a mode
/// gains nothing from being told it heard one. A 48 kHz station can run every 12 kHz built-in
/// mode as well as its own, so its captures are tried with both - the 12 kHz ones at 12 kHz
/// behind the rate bridge, as its channel would run them. Until 2026-10-10 this took the
/// station's rate alone, and GB7RDG's survey went blind on 40 m the day an ms110d modem moved
/// its channel to 48 kHz: 118 of 4950 captures readable before, 5 of 2100 after, because only
/// the FM 9600 family was being tried against HF packet bursts.
/// </para>
/// <para>
/// <b>Not the HF data waveforms.</b> They are most of the running time of a full sweep and they
/// are not what an unread packet burst on a packet channel turns out to be. A station wanting
/// that answer has <c>pdn-decode</c> and no deadline.
/// </para>
/// </remarks>
public static class CaptureSweep
{
    /// <summary>Modes worth trying against a capture at <paramref name="dspRate"/>.</summary>
    /// <remarks>
    /// Stated as an exclusion so a mode added to the catalogue joins automatically - the safe
    /// direction, since the failure mode here is a station never being told about traffic it
    /// could read. The baseband <c>fsk*</c>/<c>c4fsk*</c> family is included and simply ignores
    /// the centre, occupying DC upwards.
    /// </remarks>
    public static IReadOnlyList<string> ModesFor(int dspRate)
    {
        var modes = new List<string>();
        foreach (string mode in ModemCatalog.KnownModes)
        {
            if (RunsAt(mode, dspRate) && IsPacketMode(mode))
            {
                modes.Add(mode);
            }
        }

        return modes;
    }

    /// <summary>
    /// Whether a station whose channel runs at <paramref name="dspRate"/> could run
    /// <paramref name="mode"/>: at its own rate, or - a built-in mode only - at a rate that divides
    /// the channel's, which the rate bridge runs (<see cref="ModemCatalog.CreateForChannel"/>). A
    /// plugin mode is built at its declared rate or not at all.
    /// </summary>
    public static bool RunsAt(string mode, int dspRate)
    {
        int native = ModemCatalog.DspRateFor(mode);
        return native == dspRate
            || (!ModemPluginRegistry.IsRegistered(mode) && native < dspRate && dspRate % native == 0);
    }

    /// <summary>Whether a mode is packet-radio lineage rather than an HF data waveform.</summary>
    public static bool IsPacketMode(string mode) =>
        !mode.StartsWith("freedv-", StringComparison.Ordinal)
        && !mode.StartsWith("ms110d-", StringComparison.Ordinal);

    /// <summary>
    /// Runs <paramref name="modes"/> over <paramref name="audio"/> at <paramref name="centreHz"/>
    /// and returns everything any of them read.
    /// </summary>
    /// <param name="audio">The capture, at <paramref name="dspRate"/>.</param>
    /// <param name="dspRate">Its sample rate, which must be a rate the modes run at.</param>
    /// <param name="centreHz">Where the survey measured the signal.</param>
    /// <param name="modes">What to try; <see cref="ModesFor"/> by default.</param>
    /// <param name="shouldStop">Polled between modes so a shutting-down station stops promptly
    /// rather than finishing a sweep nobody will read.</param>
    public static IReadOnlyList<CaptureReading> Run(
        float[] audio,
        int dspRate,
        double centreHz,
        IReadOnlyList<string> modes,
        Func<bool>? shouldStop = null)
    {
        ArgumentNullException.ThrowIfNull(modes);

        var readings = new List<CaptureReading>();
        foreach (string mode in modes)
        {
            if (shouldStop?.Invoke() == true)
            {
                break;
            }

            try
            {
                ModeReader.Run(
                    mode,
                    audio,
                    dspRate,
                    ModeReader.At(mode, centreHz),
                    (frame, quality) =>
                    {
                        bool addressed = Waterfall.Ax25AddressParser.TryParse(
                            frame, out string source, out string destination);
                        readings.Add(new CaptureReading(
                            mode,
                            centreHz,
                            frame,
                            quality,
                            addressed ? source : null,
                            addressed && destination.Length > 0 ? destination : null));
                    });
            }
            catch (ArgumentException e) when (e.ParamName == "centreHz")
            {
                // Too wide to sit where it was pointed. Arithmetic, not a fault.
                //
                // ArgumentException, not ArgumentOutOfRangeException: the catalogue has two
                // guards on a centre and they do not throw the same type. The narrower catch
                // caught one of them and let the other escape.
            }
#pragma warning disable CA1031 // one broken mode must not take the sweep down with it
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }

        return readings;
    }
}
