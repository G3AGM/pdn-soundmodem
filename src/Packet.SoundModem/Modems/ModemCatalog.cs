using Packet.SoundModem.CarrierSense;
using Packet.SoundModem.Fx25;
using Packet.SoundModem.Modems.OfdmFm;
using Packet.SoundModem.Ms110d;

namespace Packet.SoundModem.Modems;

/// <summary>
/// The single source of truth mapping a mode name to a concrete <see cref="IModem"/>, its DSP
/// sample rate, and whether it accepts an audio-centre frequency. The daemon and every in-process
/// consumer (e.g. the PDN node) build modems through here, so the accepted mode set, the rate
/// derivation, and the frequency-gating rule can never drift between them.
/// </summary>
/// <remarks>
/// <para>The mode strings are the same ones accepted by the daemon's <c>--modem N:MODE[:FREQ]</c>
/// and the node's <c>soundmodem</c> port <c>mode</c> field.</para>
/// <para>Everything the catalogue knows about a mode lives in that mode's one
/// <see cref="ModeDescriptor"/> row below. It used to be smeared across half a dozen parallel
/// name-pattern chains (rate, centre semantics, default centre, IL2P+CRC, the factory switch)
/// plus satellite tables in other files, so adding a mode meant editing them all and missing one
/// produced quiet drift - an operator message here contradicting the catalogue's own, a new PSK
/// mode whose NinoTNC idents nothing could read. Now a mode is one row, and every question asked
/// of the catalogue reads from it.</para>
/// </remarks>
public static class ModemCatalog
{
    /// <summary>Where a mode's audio centre comes from - the fact the frequency-override
    /// rules hang off.</summary>
    private enum CentreSemantics
    {
        /// <summary>DC-to-Nyquist baseband (fsk*/c4fsk*): no centre to speak of; a
        /// frequency override is refused (issue #39).</summary>
        Baseband,

        /// <summary>The mode's own DSP takes the centre natively (AFSK tone pairs, the
        /// PSK carrier).</summary>
        Native,

        /// <summary>Spec-fixed centre (ms110d-*/freedv-*): the validated DSP stays on its
        /// native centre and <see cref="FrequencyShiftedModem"/> translates the audio
        /// either side, which is what lets a band plan place these anywhere in a wide
        /// passband instead of letting them dictate the dial. On air nothing changes -
        /// interop is set by the RF centre.</summary>
        Shifted,
    }

    /// <summary>What a modem factory gets to work with: the caller's resolved knobs.</summary>
    /// <param name="DspRate">The channel DSP rate the modem runs at.</param>
    /// <param name="FrameReceived">The decoded-frame sink.</param>
    /// <param name="CentreHz">The resolved audio centre (override or the row's default);
    /// null for the baseband family.</param>
    /// <param name="OffsetPairs">Diversity-bank width override, where the mode runs one.</param>
    /// <param name="OffsetStepHz">Diversity-bank step override, where the mode runs one.</param>
    /// <param name="Detector">The PSK detector, resolved against the catalogue default.</param>
    /// <param name="AcceptPlainIl2p">Plain-IL2P tolerance (validated against the row).</param>
    /// <param name="SecondDetector">Ensemble second detector for the bpsk banks (validated
    /// against the mode in <see cref="Create"/>).</param>
    /// <param name="TxAmplitude">MS110D transmit level (validated against the mode in
    /// <see cref="Create"/>); null for the modem's default.</param>
    private readonly record struct ModemBuild(
        int DspRate,
        Action<byte[]> FrameReceived,
        double? CentreHz,
        int? OffsetPairs,
        double? OffsetStepHz,
        PskDetector Detector,
        bool AcceptPlainIl2p,
        PskDetector? SecondDetector,
        double? TxAmplitude);

    /// <summary>One mode, wholly: every fact the catalogue can be asked, next to the
    /// factory that must agree with it.</summary>
    /// <param name="Name">The mode string.</param>
    /// <param name="DspRate">DSP rate the mode's chain runs at (12000 or 48000).</param>
    /// <param name="Path">The kind of radio the mode is meant for: the "Radio path" column of
    /// <c>docs/05-modes.md</c>, which <c>ReferenceDocsTests</c> holds to this.</param>
    /// <param name="Centre">Where its audio centre comes from.</param>
    /// <param name="DefaultCentreHz">The centre used when no override is given - the
    /// tone-pair midpoint, the PSK carrier, or the spec-pinned centre; null for baseband.
    /// Declared rather than measured because it chooses dial frequencies, where a spectrum
    /// estimate's few-Hz error would land in the answer; <c>ModemCentreFrequencyTests</c>
    /// checks every declaration against the modem's own measured spectrum.</param>
    /// <param name="RunsIl2pCrc">Whether the receiver runs IL2P+CRC, and so has something
    /// for <see cref="ModemOptions.AcceptPlainIl2p"/> to switch on. Spelled per-row because
    /// the names lie: <c>fsk9600-il2p</c>/<c>fsk4800-il2p</c> run IL2P+<em>CRC</em>
    /// (NinoTNC modes 2 and 4) while <c>afsk300-il2p</c> and <c>afsk1200-il2p-nocrc</c>
    /// really are CRC-less. <c>ModemCatalogTests</c> checks each answer against the modem
    /// this catalogue actually builds.</param>
    /// <param name="NinoPskIdBeacon">Whether a NinoTNC running this mode sends its station
    /// identifications as 300 baud AFSK AX.25 alongside the data (see
    /// <see cref="IdBeaconGhost"/>, which asks this). <c>qpsk3600</c> is excluded
    /// deliberately and is not an oversight: it is an FM mode
    /// (<c>docs/dev/mode-modulation-reference.md</c>), and Nino identifies the FM modes in
    /// 1200 AFSK rather than 300 - a different ghost.</param>
    /// <param name="Factory">Builds the modem from the resolved knobs.</param>
    private sealed record ModeDescriptor(
        string Name,
        int DspRate,
        RadioPath Path,
        CentreSemantics Centre,
        double? DefaultCentreHz,
        bool RunsIl2pCrc,
        bool NinoPskIdBeacon,
        Func<ModemBuild, IModem> Factory);

    private static ModeDescriptor Ms110d(int waveform) => new(
        $"ms110d-wn{waveform}", 48000, RadioPath.Ssb, CentreSemantics.Shifted,
        // MIL-STD-188-110D Table D-I: a 2400 Bd single carrier on an 1800 Hz sub-carrier.
        DefaultCentreHz: 1800, RunsIl2pCrc: true, NinoPskIdBeacon: false,
        b => new Ms110dModem(
            b.DspRate, b.FrameReceived,
            b.TxAmplitude is double amplitude
                ? new Ms110dTxSettings { WaveformNumber = waveform, Amplitude = (float)amplitude }
                : new Ms110dTxSettings { WaveformNumber = waveform },
            acceptPlainIl2p: b.AcceptPlainIl2p));

    private static ModeDescriptor FreeDv(
        string name, double centreHz, Func<ModemBuild, IModem> factory) => new(
        name, 48000, RadioPath.Ssb, CentreSemantics.Shifted,
        DefaultCentreHz: centreHz, RunsIl2pCrc: true, NinoPskIdBeacon: false, factory);

    /// <summary>
    /// An OFDM-FM row. Baseband, because the waveform occupies the audio band from just above DC
    /// upwards and there is no centre to move. No IL2P: the burst already supplies sync, a length,
    /// a CRC and forward error correction, so a second framing layer would spend rate twice for
    /// guarantees this waveform already holds, and its Reed-Solomon stage cannot use the soft
    /// information the equalised constellation produces. The payload is one opaque AX.25 frame.
    /// </summary>
    /// <remarks>The layout comes from <see cref="OfdmFmPresets.ByMode"/> and the acquisition table
    /// from <see cref="OfdmFmPresets.Table"/>, which is also what the docs and the tests read, so a
    /// mode cannot drift from the geometry it is documented as having.</remarks>
    private static ModeDescriptor OfdmFmMode(string name) => new(
        name, 48000, RadioPath.Fm, CentreSemantics.Baseband,
        DefaultCentreHz: null, RunsIl2pCrc: false, NinoPskIdBeacon: false,
        b => new OfdmFmModem(
            name,
            OfdmFmPresets.ByMode[name],
            b.FrameReceived,
            // One table shared by every preset, so a receiver on any of them decodes a burst sent
            // on any other. Passing null here would silently reduce every mode to its own layout.
            geometryTable: OfdmFmPresets.Table));

    private static readonly ModeDescriptor[] Modes =
    [
        new("afsk1200", 12000, RadioPath.Fm, CentreSemantics.Native, 1700, RunsIl2pCrc: false, NinoPskIdBeacon: false,
            b => new Afsk1200Modem(b.DspRate, b.FrameReceived, b.CentreHz!.Value)),
        new("afsk1200-fx25", 12000, RadioPath.Fm, CentreSemantics.Native, 1700, RunsIl2pCrc: false, NinoPskIdBeacon: false,
            b => new Afsk1200Modem(b.DspRate, b.FrameReceived, b.CentreHz!.Value, Fx25Mode.TransmitReceive)),
        new("afsk1200-fx25rx", 12000, RadioPath.Fm, CentreSemantics.Native, 1700, RunsIl2pCrc: false, NinoPskIdBeacon: false,
            b => new Afsk1200Modem(b.DspRate, b.FrameReceived, b.CentreHz!.Value, Fx25Mode.Receive)),
        new("afsk1200-multi", 12000, RadioPath.Fm, CentreSemantics.Native, 1700, RunsIl2pCrc: false, NinoPskIdBeacon: false,
            b => new Afsk1200MultiModem(b.DspRate, b.FrameReceived, offsetPairs: 3, centerFrequency: b.CentreHz!.Value)),
        new("afsk1200-il2p", 12000, RadioPath.Fm, CentreSemantics.Native, 1700, RunsIl2pCrc: true, NinoPskIdBeacon: false,
            b => new Afsk1200Il2pModem(b.DspRate, b.FrameReceived, crc: true, b.CentreHz!.Value, b.AcceptPlainIl2p)),
        new("afsk1200-il2p-nocrc", 12000, RadioPath.Fm, CentreSemantics.Native, 1700, RunsIl2pCrc: false, NinoPskIdBeacon: false,
            b => new Afsk1200Il2pModem(b.DspRate, b.FrameReceived, crc: false, b.CentreHz!.Value)),

        // The 300 baud HF AFSK family defaults to the narrow-branch frequency-diversity
        // bank - on the live 40 m slot the single wide modem lost most frames to a
        // neighbouring QSO inside its passband (see Afsk300MultiModem). offsetPairs/
        // offsetStepHz tune it (offsetPairs:0 gives a single tight-filtered modem).
        new("afsk300", 12000, RadioPath.Ssb, CentreSemantics.Native, 1700, RunsIl2pCrc: false, NinoPskIdBeacon: false,
            b => new Afsk300MultiModem(b.DspRate, b.FrameReceived, Afsk300Framing.Ax25,
                b.CentreHz!.Value, b.OffsetPairs ?? 5, b.OffsetStepHz)),
        new("afsk300-il2p", 12000, RadioPath.Ssb, CentreSemantics.Native, 1700, RunsIl2pCrc: false, NinoPskIdBeacon: false,
            b => new Afsk300MultiModem(b.DspRate, b.FrameReceived, Afsk300Framing.Il2p,
                b.CentreHz!.Value, b.OffsetPairs ?? 5, b.OffsetStepHz)),
        new("afsk300-il2pc", 12000, RadioPath.Ssb, CentreSemantics.Native, 1700, RunsIl2pCrc: true, NinoPskIdBeacon: false,
            b => new Afsk300MultiModem(b.DspRate, b.FrameReceived, Afsk300Framing.Il2pCrc,
                b.CentreHz!.Value, b.OffsetPairs ?? 5, b.OffsetStepHz, b.AcceptPlainIl2p)),

        // BPSK defaults to the differential frequency-diversity bank - offsetPairs/
        // offsetStepHz tune it (offsetPairs:0 gives a plain single modem).
        new("bpsk300", 12000, RadioPath.Ssb, CentreSemantics.Native, 1500, RunsIl2pCrc: true, NinoPskIdBeacon: true,
            b => new BpskMultiModem(b.DspRate, b.FrameReceived, crc: true, b.CentreHz!.Value,
                baud: 300, offsetPairs: b.OffsetPairs ?? 4, offsetHz: b.OffsetStepHz,
                detector: b.Detector, acceptPlainIl2p: b.AcceptPlainIl2p,
                secondDetector: b.SecondDetector)),
        new("bpsk300-multi", 12000, RadioPath.Ssb, CentreSemantics.Native, 1500, RunsIl2pCrc: true, NinoPskIdBeacon: true,
            b => new BpskMultiModem(b.DspRate, b.FrameReceived, crc: true, b.CentreHz!.Value,
                baud: 300, offsetPairs: b.OffsetPairs ?? 4, offsetHz: b.OffsetStepHz,
                detector: b.Detector, acceptPlainIl2p: b.AcceptPlainIl2p,
                secondDetector: b.SecondDetector)),
        new("bpsk300-nocrc", 12000, RadioPath.Ssb, CentreSemantics.Native, 1500, RunsIl2pCrc: false, NinoPskIdBeacon: true,
            b => new BpskMultiModem(b.DspRate, b.FrameReceived, crc: false, b.CentreHz!.Value,
                baud: 300, offsetPairs: b.OffsetPairs ?? 4, offsetHz: b.OffsetStepHz,
                detector: b.Detector, secondDetector: b.SecondDetector)),
        new("bpsk1200", 12000, RadioPath.Ssb, CentreSemantics.Native, 1500, RunsIl2pCrc: true, NinoPskIdBeacon: true,
            b => new BpskMultiModem(b.DspRate, b.FrameReceived, crc: true, b.CentreHz!.Value,
                baud: 1200, offsetPairs: b.OffsetPairs ?? 4, offsetHz: b.OffsetStepHz,
                detector: b.Detector, acceptPlainIl2p: b.AcceptPlainIl2p,
                secondDetector: b.SecondDetector)),
        new("bpsk1200-multi", 12000, RadioPath.Ssb, CentreSemantics.Native, 1500, RunsIl2pCrc: true, NinoPskIdBeacon: true,
            b => new BpskMultiModem(b.DspRate, b.FrameReceived, crc: true, b.CentreHz!.Value,
                baud: 1200, offsetPairs: b.OffsetPairs ?? 4, offsetHz: b.OffsetStepHz,
                detector: b.Detector, acceptPlainIl2p: b.AcceptPlainIl2p,
                secondDetector: b.SecondDetector)),

        // QPSK runs the same differential frequency-diversity bank as BPSK since issue #326;
        // offsetPairs/offsetStepHz tune it (offsetPairs:0 gives a plain single modem).
        new("qpsk600", 12000, RadioPath.Ssb, CentreSemantics.Native, 1500, RunsIl2pCrc: true, NinoPskIdBeacon: true,
            b => new QpskMultiModem(b.DspRate, b.FrameReceived, crc: true, b.CentreHz!.Value,
                baud: 300, offsetPairs: b.OffsetPairs ?? 4, offsetHz: b.OffsetStepHz,
                detector: b.Detector, acceptPlainIl2p: b.AcceptPlainIl2p,
                secondDetector: b.SecondDetector)),
        new("qpsk2400", 12000, RadioPath.Ssb, CentreSemantics.Native, 1500, RunsIl2pCrc: true, NinoPskIdBeacon: true,
            b => new QpskMultiModem(b.DspRate, b.FrameReceived, crc: true, b.CentreHz!.Value,
                baud: 1200, offsetPairs: b.OffsetPairs ?? 4, offsetHz: b.OffsetStepHz,
                detector: b.Detector, acceptPlainIl2p: b.AcceptPlainIl2p,
                secondDetector: b.SecondDetector)),
        // qpsk3600 arrives through FM, where the audio tones land on frequency whatever the RF
        // offset, and its decode chain runs threefold upsampled: no offset pairs unless asked.
        new("qpsk3600", 12000, RadioPath.Fm, CentreSemantics.Native, 1650, RunsIl2pCrc: true, NinoPskIdBeacon: false,
            b => new QpskMultiModem(b.DspRate, b.FrameReceived, crc: true, b.CentreHz!.Value,
                baud: 1800, offsetPairs: b.OffsetPairs ?? 0, offsetHz: b.OffsetStepHz,
                detector: b.Detector, acceptPlainIl2p: b.AcceptPlainIl2p,
                secondDetector: b.SecondDetector)),

        new("fsk9600", 48000, RadioPath.Fm, CentreSemantics.Baseband, null, RunsIl2pCrc: false, NinoPskIdBeacon: false,
            b => FskModem.Fsk9600(b.DspRate, b.FrameReceived, FskFraming.ClassicHdlc)),
        new("fsk9600-il2p", 48000, RadioPath.Fm, CentreSemantics.Baseband, null, RunsIl2pCrc: true, NinoPskIdBeacon: false,
            b => new FskModem(b.DspRate, b.FrameReceived, FskFraming.Il2pCrc,
                acceptPlainIl2p: b.AcceptPlainIl2p)),
        new("fsk4800-il2p", 48000, RadioPath.Fm, CentreSemantics.Baseband, null, RunsIl2pCrc: true, NinoPskIdBeacon: false,
            b => FskModem.Fsk4800(b.DspRate, b.FrameReceived, acceptPlainIl2p: b.AcceptPlainIl2p)),
        new("c4fsk9600", 48000, RadioPath.Fm, CentreSemantics.Baseband, null, RunsIl2pCrc: true, NinoPskIdBeacon: false,
            b => C4fskModem.C4fsk9600(b.DspRate, b.FrameReceived, b.AcceptPlainIl2p)),
        new("c4fsk19200", 48000, RadioPath.Fm, CentreSemantics.Baseband, null, RunsIl2pCrc: true, NinoPskIdBeacon: false,
            b => C4fskModem.C4fsk19200(b.DspRate, b.FrameReceived, b.AcceptPlainIl2p)),

        // The datac modes nominally centre on 1500 Hz; the even-carrier-count ones land
        // half a carrier below it by construction (M0LTE.Ofdm's per-mode CarrierCentreHz).
        FreeDv("freedv-datac0", 1500, b => FreeDvDatacModem.Datac0(b.DspRate, b.FrameReceived, b.AcceptPlainIl2p)),
        FreeDv("freedv-datac1", 1500, b => FreeDvDatacModem.Datac1(b.DspRate, b.FrameReceived, b.AcceptPlainIl2p)),
        FreeDv("freedv-datac3", 1500, b => FreeDvDatacModem.Datac3(b.DspRate, b.FrameReceived, b.AcceptPlainIl2p)),
        FreeDv("freedv-datac4", 1468.75, b => FreeDvDatacModem.Datac4(b.DspRate, b.FrameReceived, b.AcceptPlainIl2p)),
        FreeDv("freedv-datac13", 1500, b => FreeDvDatacModem.Datac13(b.DspRate, b.FrameReceived, b.AcceptPlainIl2p)),
        FreeDv("freedv-datac14", 1472.2222222222222, b => FreeDvDatacModem.Datac14(b.DspRate, b.FrameReceived, b.AcceptPlainIl2p)),

        Ms110d(0), Ms110d(1), Ms110d(2), Ms110d(3), Ms110d(4),
        Ms110d(5), Ms110d(6), Ms110d(7), Ms110d(8), Ms110d(13),

        // OFDM-FM. All eight share one geometry table, so a receiver on any of them can decode a
        // burst sent on any other and a link can change bandwidth without a handshake.
        // ofdm-fm-8k is the one to reach for, and the only one measured across the full range of
        // payload sizes in both directions.
        OfdmFmMode("ofdm-fm-narrow"),
        OfdmFmMode("ofdm-fm-6k"),
        OfdmFmMode("ofdm-fm-6k-fast"),
        OfdmFmMode("ofdm-fm-8k"),
        OfdmFmMode("ofdm-fm-8k-r56"),
        OfdmFmMode("ofdm-fm-8k-r78"),
        OfdmFmMode("ofdm-fm-8k-follow"),
        OfdmFmMode("ofdm-fm-8k-adaptive"),
    ];

    private static readonly Dictionary<string, ModeDescriptor> ByName =
        Modes.ToDictionary(m => m.Name, StringComparer.Ordinal);

    /// <summary>
    /// Every mode <em>built into this library</em>, in catalogue order. Plugin modes are
    /// deliberately not here: this is the set the repository is answerable for, the one the
    /// whole-catalogue test sweeps iterate, and the one whose count is pinned. Use
    /// <see cref="AllModes"/> to list what a running station can actually do.
    /// </summary>
    public static IReadOnlyList<string> KnownModes { get; } =
        [.. Modes.Select(m => m.Name)];

    /// <summary>
    /// Every mode this process can build: the built-ins followed by whatever
    /// <see cref="ModemPluginRegistry"/> holds. What to show an operator, and what changes under
    /// your feet when a plugin loads.
    /// </summary>
    public static IReadOnlyList<string> AllModes =>
        ModemPluginRegistry.RegisteredModes.Count == 0
            ? KnownModes
            : [.. KnownModes, .. ModemPluginRegistry.RegisteredModes];

    /// <summary>True if <paramref name="mode"/> is a recognised mode string - built in or
    /// registered by a plugin (ordinal, case-sensitive).</summary>
    public static bool IsKnown(string mode) =>
        ByName.ContainsKey(mode) || ModemPluginRegistry.IsRegistered(mode);

    /// <summary>
    /// Mode names closest to <paramref name="mode"/>, for a "did you mean" on a typo. A
    /// case-insensitive hit wins outright - the catalogue is ordinal, so <c>AFSK1200</c> is a
    /// miss and naming the cased spelling is the whole answer. Otherwise the nearest by edit
    /// distance, closest first. Empty when nothing is near enough to be a useful guess.
    /// </summary>
    /// <remarks>Start-up path only (the daemon rejecting a bad config), never per-frame.</remarks>
    public static string[] NearestModes(string mode, int max = 3)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return [];
        }

        // Over AllModes, not KnownModes: a station running a plugin should be offered its modes
        // on a typo like any other, and an operator who has loaded one does not think of it as a
        // different kind of mode.
        IReadOnlyList<string> candidates = AllModes;
        string[] cased = candidates
            .Where(m => string.Equals(m, mode, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (cased.Length > 0)
        {
            return cased;
        }

        // Scale the budget with the name so "ms110d-wn13" tolerates more than "afsk300" does,
        // without a short typo matching half the catalogue.
        int budget = Math.Clamp(mode.Length / 3, 2, 4);
        return candidates
            .Select(m => (Mode: m, Distance: EditDistance(m, mode)))
            .Where(x => x.Distance <= budget)
            .OrderBy(x => x.Distance)
            .ThenBy(x => x.Mode, StringComparer.Ordinal)
            .Take(max)
            .Select(x => x.Mode)
            .ToArray();
    }

    /// <summary>Case-insensitive Levenshtein distance, two rows rather than a full matrix.</summary>
    private static int EditDistance(string a, string b)
    {
        int[] previous = new int[b.Length + 1];
        int[] current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    /// <summary>
    /// The DSP sample rate a mode's demod/mod chain runs at: 48000 for the wideband baseband
    /// families and the OFDM/MS110D waveforms, 12000 otherwise, and whatever a plugin mode
    /// declares. A soundcard capture rate must be an integer multiple of this. Unknown mode
    /// strings answer 12000 - callers validate names against <see cref="IsKnown"/> before the
    /// answer matters.
    /// </summary>
    public static int DspRateFor(string mode) =>
        ByName.TryGetValue(mode, out ModeDescriptor? found) ? found.DspRate
        : ModemPluginRegistry.DescriptorFor(mode)?.DspRate ?? 12000;

    /// <summary>
    /// The kind of radio a built-in mode is meant for (the mode table's "Radio path" column), or
    /// null for a plugin mode or an unknown name, which declare none.
    /// </summary>
    public static RadioPath? RadioPathFor(string mode) =>
        ByName.TryGetValue(mode, out ModeDescriptor? found) ? found.Path : null;

    /// <summary>
    /// Whether a mode has a settable audio-centre frequency. The variable-centre families - the
    /// AFSK tone-pair and the BPSK/QPSK carrier - take one natively; the spec-fixed families
    /// (<c>ms110d-*</c>, <c>freedv-*</c>) take one by decoration (see
    /// <see cref="CentreSemantics.Shifted"/>). Only the baseband fsk*/c4fsk* families
    /// (DC-to-Nyquist, no centre to speak of) refuse one: passing a centre frequency to
    /// <see cref="Create"/> for those throws (issue #39).
    /// </summary>
    public static bool AcceptsCentreFrequency(string mode) =>
        ByName.TryGetValue(mode, out ModeDescriptor? found)
            ? found.Centre != CentreSemantics.Baseband
            : ModemPluginRegistry.DescriptorFor(mode)?.AcceptsCentre ?? false;

    /// <summary>
    /// The audio centre a mode sits on when no override is given: the tone-pair midpoint, the PSK
    /// carrier, or - for the spec-fixed families - the centre their standard pins them to. Null for
    /// the baseband families (<c>fsk*</c>/<c>c4fsk*</c>), which occupy DC upwards and so have no
    /// centre to speak of.
    /// </summary>
    public static double? DefaultCentreFrequencyFor(string mode) =>
        ByName.TryGetValue(mode, out ModeDescriptor? found) ? found.DefaultCentreHz
        : ModemPluginRegistry.DescriptorFor(mode)?.DefaultCentreHz;

    /// <summary>
    /// Whether a mode's receiver runs IL2P+CRC, and so has something for
    /// <see cref="ModemOptions.AcceptPlainIl2p"/> to switch on. False for the AX.25/FX.25 modes
    /// (no IL2P at all) and for the modes that already read plain IL2P, where the tolerance is
    /// what they do anyway.
    /// </summary>
    public static bool RunsIl2pCrc(string mode) =>
        ByName.TryGetValue(mode, out ModeDescriptor? found) ? found.RunsIl2pCrc
        : ModemPluginRegistry.DescriptorFor(mode)?.RunsIl2pCrc ?? false;

    /// <summary>
    /// Whether a NinoTNC running this mode sends its station identifications as 300 baud AFSK
    /// AX.25 alongside the data - what decides whether an <see cref="IdBeaconGhost"/> listens
    /// beside the modem. See the descriptor row notes for why <c>qpsk3600</c> says no.
    /// </summary>
    public static bool HasNinoPskIdBeacon(string mode) =>
        ByName.TryGetValue(mode, out ModeDescriptor? found) && found.NinoPskIdBeacon;

    /// <summary>
    /// The default PSK detector for a mode when the caller does not override it: differential for
    /// every PSK family. BPSK reversed to differential 2026-07-18 (issues #40/#42 - coherent's
    /// narrow Costas loop cannot acquire real carriers); QPSK followed 2026-07-31 on the studybox
    /// NinoTNC corpus: coherent copied 0-2 of 3 real NinoTNC frames per capture even on a wired
    /// audio loop (knife-edge timing over multi-second bursts, ±5-8 Hz CFO walls - issues #11/#116/
    /// #144), while differential - V.26A is differentially encoded by construction - copies 9/9
    /// QPSK corpus files and widens the CFO half-widths ×4-7, for 0.4-3.5 dB at the AWGN knee
    /// (`docs/dev/archive/bench/ninotnc-corpus-2026-07-31.md`, `docs/dev/archive/cfo/evidence/`). Coherent remains
    /// selectable via <see cref="ModemOptions.Detector"/>.
    /// </summary>
    public static PskDetector DefaultDetectorFor(string mode) => PskDetector.Differential;

    /// <summary>
    /// Builds the modem for <paramref name="mode"/> at <paramref name="dspRate"/>, delivering decoded
    /// frames to <paramref name="frameReceived"/>. <paramref name="options"/> supplies the optional
    /// per-mode knobs (centre frequency, BPSK bank width/step, PSK detector); omit it for defaults.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="mode"/> is not a known mode, a centre frequency was supplied for a
    /// fixed-centre mode (see <see cref="AcceptsCentreFrequency"/>), or plain-IL2P tolerance was
    /// asked of a mode that does not run IL2P+CRC (see <see cref="RunsIl2pCrc"/>).
    /// </exception>
    public static IModem Create(string mode, int dspRate, Action<byte[]> frameReceived, ModemOptions options = default)
    {
        if (!ByName.TryGetValue(mode, out ModeDescriptor? descriptor))
        {
            // One lookup, not IsRegistered-then-DescriptorFor: registration is process-global and
            // a plugin unregistered between the two would turn a clean "unknown mode" into a
            // NullReferenceException from a null-forgiving dereference.
            return ModemPluginRegistry.DescriptorFor(mode) is ModemDescriptor registered
                ? CreateRegistered(mode, registered, dspRate, frameReceived, options)
                : throw new ArgumentException(
                    $"unknown mode '{mode}'{MissingPluginHint(mode)}", nameof(mode));
        }

        double? frequency = options.CentreFrequencyHz;
        if (frequency is not null && descriptor.Centre == CentreSemantics.Baseband)
        {
            throw new ArgumentException(
                $"mode '{mode}' occupies the audio band from DC upwards and has no centre " +
                "frequency to move - drop the frequency override (the afsk*/bpsk*/qpsk* and " +
                "spec-fixed ms110d-*/freedv-* modes accept one)",
                nameof(options));
        }

        // Refused rather than ignored, on the same reasoning as the centre frequency above: the
        // operator who set it believes their modem got more tolerant, and on a mode with no
        // IL2P+CRC check to relax it never will be.
        bool acceptPlainIl2p = options.AcceptPlainIl2p ?? false;
        if (acceptPlainIl2p && !descriptor.RunsIl2pCrc)
        {
            throw new ArgumentException(
                $"mode '{mode}' does not run IL2P+CRC, so it has no CRC check to relax - drop " +
                "the plain-IL2P tolerance (the modes that accept it are the IL2P+CRC ones)",
                nameof(options));
        }

        // The spec-fixed families take the override by decoration: the modem is built on its
        // native centre exactly as ever, and the wrapper translates the audio either side. The
        // factory therefore never sees the override for those modes.
        double? shiftedCentre = null;
        if (frequency is double asked && descriptor.Centre == CentreSemantics.Shifted)
        {
            shiftedCentre = asked;
            frequency = null;
        }

        if (options.SecondDetector is not null
            && !mode.StartsWith("bpsk", StringComparison.Ordinal)
            && !mode.StartsWith("qpsk", StringComparison.Ordinal))
        {
            // Only the PSK banks carry the ensemble machinery; a silent no-op here would
            // hand a measurement the single-detector bank while it believes it asked for
            // the union - the instrument lie the MS110D programme taught us to refuse.
            throw new ArgumentException(
                $"mode '{mode}' does not support an ensemble second detector (bpsk/qpsk banks only)",
                nameof(options));
        }

        if (options.TxAmplitude is double txAmplitude)
        {
            if (!mode.StartsWith("ms110d-", StringComparison.Ordinal))
            {
                throw new ArgumentException($"a transmit amplitude applies to the ms110d-* modes only, not '{mode}'", nameof(options));
            }
            if (!double.IsFinite(txAmplitude) || txAmplitude <= 0 || txAmplitude > 1)
            {
                throw new ArgumentException($"transmit amplitude {txAmplitude} is not above 0 and at most 1", nameof(options));
            }
        }

        IModem modem = descriptor.Factory(new ModemBuild(
            dspRate,
            frameReceived,
            frequency ?? descriptor.DefaultCentreHz,
            options.OffsetPairs,
            options.OffsetStepHz,
            options.Detector ?? DefaultDetectorFor(mode),
            acceptPlainIl2p,
            options.SecondDetector,
            options.TxAmplitude));

        // Asked-for centre equal to the native one included: Wrap returns the bare modem then,
        // so a config that spells out the default is bit-identical to one that omits it.
        return shiftedCentre is double centre
            ? FrequencyShiftedModem.Wrap(modem, dspRate, centre, descriptor.DefaultCentreHz!.Value)
            : modem;
    }

    /// <summary>
    /// Builds a mode for a shared channel running at <paramref name="channelRate"/>: at its own
    /// DSP rate behind a <see cref="RateBridgedModem"/> when the channel runs faster, otherwise
    /// exactly as <see cref="Create"/> does.
    /// </summary>
    /// <remarks>
    /// <para>A built-in 12 kHz mode is rate-parameterised and works at 48 kHz too, which is how a
    /// channel carrying any 48 kHz mode used to build every modem on it - at four times the cost
    /// (issue #648). Here it is built at 12 kHz and bridged, unless its band will not fit under
    /// the 12 kHz Nyquist with the decimator's transition (see <see cref="RateBridgedModem.TryWrap"/>),
    /// in which case it is built at the channel rate as before.</para>
    /// <para><see cref="Create"/> keeps its contract of building at the rate it is asked for:
    /// the band planner's probes, the mode reader and the tests ask for a rate and mean it. A
    /// plugin mode always goes through <see cref="Create"/>, which builds it at its declared rate
    /// or not at all.</para>
    /// </remarks>
    public static IModem CreateForChannel(
        string mode, int channelRate, Action<byte[]> frameReceived, ModemOptions options = default)
    {
        if (ByName.TryGetValue(mode, out ModeDescriptor? descriptor)
            && descriptor.DspRate < channelRate
            && channelRate % descriptor.DspRate == 0)
        {
            IModem? native = null;
            try
            {
                native = Create(mode, descriptor.DspRate, frameReceived, options);
            }
            catch (ArgumentException)
            {
                // A setting the native rate cannot carry (a centre above its Nyquist) and the
                // channel rate can: build it at the channel rate below, which also gives the
                // operator the channel-rate refusal if it is wrong there too.
            }

            if (native is not null
                && RateBridgedModem.TryWrap(native, descriptor.DspRate, channelRate, out IModem? bridged))
            {
                return bridged!;
            }
        }

        return Create(mode, channelRate, frameReceived, options);
    }

    /// <summary>
    /// Builds a plugin mode: the same option rules as a built-in, applied against the descriptor
    /// the plugin declared, then the plugin asked to construct it.
    /// </summary>
    /// <remarks>
    /// The messages are word-for-word the built-in ones deliberately. An operator hitting the
    /// centre-frequency rule on <c>sample:loopback</c> is hitting the same rule as on <c>fsk9600</c>,
    /// and a different wording would read as a different rule.
    /// </remarks>
    private static IModem CreateRegistered(
        string mode,
        ModemDescriptor descriptor,
        int dspRate,
        Action<byte[]> frameReceived,
        ModemOptions options)
    {
        // A built-in mode's factory reads the rate from the caller and is written to work at the
        // one the catalogue declares; nothing checks, because nothing has to - the same table
        // states both. A plugin's descriptor and its caller are two different parties, so a
        // mismatch is possible and has to be refused rather than handed over. A host whose channel
        // runs at 48000 building a mode that said 12000 would otherwise get a modem quietly
        // demodulating at four times the rate its DSP was designed for, which decodes nothing and
        // looks like a modem that does not work.
        if (dspRate != descriptor.DspRate)
        {
            throw new ArgumentException(
                $"mode '{mode}' runs at {descriptor.DspRate} Hz and was asked for at {dspRate} Hz "
                + "- a plugin mode is built at the rate its descriptor declares or not at all",
                nameof(dspRate));
        }

        double? frequency = options.CentreFrequencyHz;
        if (frequency is not null && !descriptor.AcceptsCentre)
        {
            throw new ArgumentException(
                $"mode '{mode}' occupies the audio band from DC upwards and has no centre " +
                "frequency to move - drop the frequency override (the afsk*/bpsk*/qpsk* and " +
                "spec-fixed ms110d-*/freedv-* modes accept one)",
                nameof(options));
        }

        if ((options.AcceptPlainIl2p ?? false) && !descriptor.RunsIl2pCrc)
        {
            throw new ArgumentException(
                $"mode '{mode}' does not run IL2P+CRC, so it has no CRC check to relax - drop " +
                "the plain-IL2P tolerance (the modes that accept it are the IL2P+CRC ones)",
                nameof(options));
        }

        // Refused unconditionally rather than by the built-in path's name test: only the PSK
        // banks carry the ensemble machinery, none of them is a plugin, and a plugin id of "bpsk"
        // would otherwise sneak past a StartsWith and be handed a detector it cannot use.
        if (options.SecondDetector is not null)
        {
            throw new ArgumentException(
                $"mode '{mode}' does not support an ensemble second detector (bpsk/qpsk banks only)",
                nameof(options));
        }

        // The centre is resolved here, so a plugin is told which centre to use rather than having
        // to ask the catalogue about itself. Everything else crosses as the caller wrote it.
        return ModemPluginRegistry.Create(
            mode,
            dspRate,
            frameReceived,
            options with { CentreFrequencyHz = frequency ?? descriptor.DefaultCentreHz });
    }

    /// <summary>
    /// The second sentence of an unknown-mode complaint, when the name looks like a plugin mode
    /// whose plugin is not loaded. "No plugin registered for 'ofdm-fm'" is a thing an operator can
    /// act on; a mode that merely does not exist is not, and the config they are reading says the
    /// mode plainly.
    /// </summary>
    private static string MissingPluginHint(string mode)
    {
        int separator = mode.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return "";
        }

        string id = mode[..separator];
        return ModemPluginRegistry.IsPluginRegistered(id)
            ? $" - modem plugin '{id}' is loaded and does not provide it"
            : $" - no modem plugin registered for '{id}'";
    }
}
