using Packet.SoundModem.Audio;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Survey;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Survey;

/// <summary>
/// Turning the survey's captures into "this station should be listening to X".
/// </summary>
/// <remarks>
/// The survey answers "something went past that I could not read" and stops, which is a
/// diagnosis with no prescription: the live 40 m station produced 14,267 of them in three weeks
/// and two opened by hand on 2026-08-24 turned out to be one station beaconing every twenty
/// minutes in a mode the station could read and was not configured for. These tests run against
/// that exact capture (`samples/offair/2026-08-24/`), because a prospector that works on
/// synthesised audio and not on the file that motivated it has proved nothing.
/// </remarks>
public class ModemProspectorTests
{
    private const string Capture =
        "samples/offair/2026-08-24/20260824-152242-1134hz-unclaimed.wav";

    /// <summary>PD4R-12, the beacon in that capture.</summary>
    private const string Station = "PD4R-12";

    [Fact]
    public void A_Capture_Is_Read_By_Whatever_Mode_Could_Have_Carried_It()
    {
        (float[] audio, int rate) = Load();

        IReadOnlyList<CaptureReading> readings = CaptureSweep.Run(
            audio, rate, 1133.98, CaptureSweep.ModesFor(rate));

        CaptureReading reading = readings.Should().ContainSingle(
            r => r.Source == Station).Subject;
        reading.Mode.Should().Be("afsk300", "300 baud AFSK carrying plain AX.25");
        reading.Quality.CrcValid.Should().BeNull("plain AX.25 has an FCS, not an IL2P CRC");
        reading.Quality.MonitorOnly.Should().BeFalse("a real link would have delivered it");
    }

    [Fact]
    public void A_48_kHz_Stations_Capture_Is_Still_Read_By_The_12_kHz_Mode_That_Carried_It()
    {
        // GB7RDG from 2026-10-05: an ms110d modem moved its channel to 48 kHz, its captures came
        // out at 48 kHz, and a sweep of 48 kHz modes only read 5 of 2100 of them. The same off-air
        // beacon, at the rate that station now records at.
        (float[] native, int nativeRate) = Load();
        var upsampler = new M0LTE.Dsp.Upsampler(48000, 4);
        var audio = new float[upsampler.OutputLength(native.Length)];
        upsampler.Process(native, audio);

        IReadOnlyList<CaptureReading> readings = CaptureSweep.Run(
            audio, nativeRate * 4, 1133.98, CaptureSweep.ModesFor(48000));

        readings.Should().Contain(r => r.Source == Station && r.Mode == "afsk300");
    }

    [Fact]
    public void A_48_kHz_Station_Tries_Every_Packet_Mode_It_Could_Run()
    {
        IReadOnlyList<string> modes = CaptureSweep.ModesFor(48000);

        modes.Should().Contain(["afsk300", "afsk300-il2pc", "bpsk300", "qpsk2400", "afsk1200"],
            "a 48 kHz channel runs the 12 kHz modes too, behind the rate bridge");
        modes.Should().Contain(["fsk9600-il2p", "c4fsk9600"]);
        modes.Should().NotContain(m => m.StartsWith("freedv-", StringComparison.Ordinal));
        modes.Should().NotContain(m => m.StartsWith("ms110d-", StringComparison.Ordinal));
    }

    [Fact]
    public void The_Stations_Own_Modes_Are_Tried_And_The_Hf_Waveforms_Are_Not()
    {
        // The sweep runs beside a live receiver, so it buys only what it can use: a 12 kHz
        // station gains nothing from being told it heard a 48 kHz mode, and the HF data
        // waveforms are most of a full sweep's running time and are not what an unread packet
        // burst turns out to be. A station wanting that answer has pdn-decode and no deadline.
        IReadOnlyList<string> modes = CaptureSweep.ModesFor(12000);

        modes.Should().Contain(["afsk300", "afsk300-il2pc", "bpsk300", "qpsk2400"]);
        modes.Should().NotContain(m => m.StartsWith("freedv-", StringComparison.Ordinal));
        modes.Should().NotContain(m => m.StartsWith("ms110d-", StringComparison.Ordinal));
        modes.Should().OnlyContain(m => ModemCatalog.DspRateFor(m) == 12000);
    }

    [Fact]
    public void A_Station_Heard_Repeatedly_Becomes_A_Proposal()
    {
        // The capture replayed as what it is: one beacon heard on several occasions.
        var prospector = new ModemProspector(Options(), [], dialFrequencyHz: 7049450);
        var proposed = new List<ModemProposal>();
        prospector.Proposed += proposed.Add;

        (float[] audio, int rate) = Load();
        for (int i = 0; i < 3; i++)
        {
            prospector.Examine(CaptureAt(TimeSpan.FromMinutes(20 * i)), audio, Modes(rate));
        }

        ModemProposal proposal = proposed.Should().ContainSingle(
            "the third capture is what crosses the threshold, and it crosses it once").Subject;
        proposal.Mode.Should().Be("afsk300");
        proposal.Kind.Should().Be(ProposalKind.NewModem, "nothing is configured at all here");
        proposal.Captures.Should().Be(3);
        proposal.Stations.Should().Equal([Station]);
        proposal.RfFrequencyHz.Should().BeApproximately(7050584, 2, "dial plus the audio centre");
        proposal.FirstHeard.Should().BeBefore(proposal.LastHeard);
        prospector.Proposals().Should().ContainSingle();
    }

    [Fact]
    public void On_Fm_A_Proposal_Is_In_Audio_Terms_Only()
    {
        // The same beacon on a channel radio. A proposal carrying "rfFrequency" here would be
        // proposing a config line that means something else entirely, so it carries the audio
        // centre alone and the operator writes "frequency" (#413).
        var prospector = new ModemProspector(
            Options(), [], dialFrequencyHz: 145_300_000, sideband: "fm");
        var proposed = new List<ModemProposal>();
        prospector.Proposed += proposed.Add;

        (float[] audio, int rate) = Load();
        for (int i = 0; i < 3; i++)
        {
            prospector.Examine(CaptureAt(TimeSpan.FromMinutes(20 * i)), audio, Modes(rate));
        }

        ModemProposal proposal = proposed.Should().ContainSingle().Subject;
        proposal.AudioCentreHz.Should().BeApproximately(1134, 5, "where it sits in the audio");
        proposal.RfFrequencyHz.Should().BeNull("a tone on a channel has no RF of its own");
        proposal.Summary().Should().NotContain("MHz",
            "and the line the operator reads must not offer one either");
    }

    [Fact]
    public void One_Beacon_Sent_Again_And_Again_Is_Evidence_Rather_Than_A_Duplicate()
    {
        // The gate that had to be got right. The obvious one is "how many DIFFERENT frames",
        // and it is exactly wrong for this traffic: a beacon is the same bytes every twenty
        // minutes for ever, so PD4R-12 - the station that prompted the whole feature - would
        // never have been proposed under it, having sent one frame's worth of bytes several
        // hundred times. What a repeated identical beacon evidences is a station reliably
        // there, which is what a modem slot commits to.
        var prospector = new ModemProspector(Options(), []);
        (float[] audio, int rate) = Load();
        var frames = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < 3; i++)
        {
            foreach (CaptureReading reading in
                     prospector.Examine(CaptureAt(TimeSpan.FromMinutes(20 * i)), audio, Modes(rate)))
            {
                frames.Add(Convert.ToHexString(reading.Frame));
            }
        }

        frames.Should().ContainSingle("every capture holds byte-identical bytes");
        prospector.Proposals().Should().ContainSingle().Which.Captures.Should().Be(3);
    }

    [Fact]
    public void Traffic_Inside_A_Configured_Modems_Band_Is_A_Framing_Problem_Not_A_Frequency_One()
    {
        // The finding this exists for. PD4R-12 sat inside an afsk300-il2pc modem's passband for
        // a month: audible, detected, unreadable, because that modem reads IL2P+CRC and this
        // station sends plain AX.25. Moving anything would have been moving the wrong thing, and
        // a proposal that said "add a modem at 1134 Hz" without saying why the one already there
        // could not hear it would be a worse answer than none.
        ModemBand[] configured =
        [
            new(0, "afsk300-il2pc-multi11", 1133.98 - 175, 1133.98 + 175, 1133.98),
        ];
        var prospector = new ModemProspector(Options(), configured, dialFrequencyHz: 7049450);

        (float[] audio, int rate) = Load();
        for (int i = 0; i < 3; i++)
        {
            prospector.Examine(CaptureAt(TimeSpan.FromMinutes(20 * i)), audio, Modes(rate));
        }

        ModemProposal proposal = prospector.Proposals().Should().ContainSingle().Subject;
        proposal.Kind.Should().Be(ProposalKind.FramingChange);
        proposal.Conflicts.Should().Equal([0], "modem 0 is the one that cannot read it");
        proposal.Summary().Should().Contain("already covers").And.Contain("afsk300");
    }

    [Fact]
    public void Nothing_Is_Proposed_On_Too_Little_Evidence()
    {
        var prospector = new ModemProspector(Options(), []);
        (float[] audio, int rate) = Load();

        prospector.Examine(CaptureAt(TimeSpan.Zero), audio, Modes(rate));
        prospector.Examine(CaptureAt(TimeSpan.FromMinutes(20)), audio, Modes(rate));

        prospector.Examined.Should().Be(2);
        prospector.Read.Should().Be(2);
        prospector.Proposals().Should().BeEmpty("two occasions is not yet a standing commitment");
    }

    [Fact]
    public void Silence_Proposes_Nothing_And_Is_Counted_As_Examined()
    {
        var prospector = new ModemProspector(Options(), []);

        prospector.Examine(CaptureAt(TimeSpan.Zero), new float[12000 * 2], Modes(12000));

        prospector.Examined.Should().Be(1);
        prospector.Read.Should().Be(0, "nothing was read out of it");
        prospector.Proposals().Should().BeEmpty();
    }

    private static ModemProspectorOptions Options() => new() { MinCaptures = 3 };

    /// <summary>The two modes this capture can be: the one that reads it and one that does not.
    /// Running the whole catalogue per capture would make each of these tests a sweep.</summary>
    private static IReadOnlyList<string> Modes(int rate) => ["afsk300", "afsk300-il2pc"];

    private static BurstCapture CaptureAt(TimeSpan after) => new(
        new DateTimeOffset(2026, 8, 24, 15, 22, 42, TimeSpan.Zero) + after,
        SurveyVerdict.Unclaimed,
        AudioCentreHz: 1133.98,
        AudioLowHz: 1018.9,
        AudioHighHz: 1249.0,
        WidthHz: 230.1,
        DurationSeconds: 4.433,
        PeakSnrDb: 21.7,
        MeanSnrDb: 19.5,
        RfCentreHz: 7050583.98,
        DialHz: 7049450,
        Sideband: "usb",
        SampleRate: 12000,
        Modems: ["0:afsk300-il2pc-multi11"]);

    private static (float[] Audio, int Rate) Load()
    {
        var (samples, rate) = WavFile.ReadMono(Path.Combine(FindRepoRoot(), Capture));
        rate.Should().Be(12000);
        return (samples, rate);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pdn-soundmodem.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }
}
