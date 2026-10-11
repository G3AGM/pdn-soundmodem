using System.Text.RegularExpressions;
using AwesomeAssertions;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.FlexRadio;
using Packet.SoundModem.OpenWebRx;
using Packet.SoundModem.UberSdr;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The device seam (#595): one kind per device, each answering what start-up used to work out by
/// testing the device string.
/// </summary>
/// <remarks>
/// The seam was a refactor that changed no behaviour, so most of what is pinned here is the old
/// answer: each capability is checked against the expression Program.cs used to compute it from
/// <see cref="FlexDevice.IsFlex"/>, <see cref="UberSdrDevice.IsUberSdr"/> and
/// <see cref="PipeAudio.IsPipe"/>, over every spelling of every kind.
/// </remarks>
public class DeviceKindTests
{
    private static readonly DeviceSettings NoSettings = new(UberSdr: null, HasWaterfall: false);

    /// <summary>Every kind, in more than one spelling where its prefix is case-blind.</summary>
    public static TheoryData<string, string> Devices => new()
    {
        { "default", "alsa" },
        { "plughw:CARD=Device,DEV=0", "alsa" },
        { "hw:1,0", "alsa" },
        // The pipe prefix is case-sensitive, so this one has always been taken as a card name.
        { "PIPE:/tmp/a,/tmp/b", "alsa" },
        { "pipe:/tmp/a,/tmp/b", "pipe" },
        { "pipe:/tmp/a,/tmp/b,12000", "pipe" },
        { "flex:mock", "flex" },
        { "flex:mock@SmartSDR", "flex" },
        { "flex:discover:B", "flex" },
        { "FLEX:192.168.1.5", "flex" },
        { "flex:serial=1234-5678@Station", "flex" },
        { "ubersdr:m9psy-1.instance.ubersdr.org", "ubersdr" },
        { "UberSDR:https://example.org:8073/", "ubersdr" },
        { "openwebrx:http://sdr.example.org:8073/", "openwebrx" },
        { "OpenWebRX:sdr.example.org", "openwebrx" },
    };

    [Theory]
    [MemberData(nameof(Devices))]
    public void A_Device_String_Is_Matched_To_The_Kind_That_Owns_Its_Prefix(string device, string kind)
    {
        DeviceKinds.Of(device).Name.Should().Be(kind);
        DeviceKinds.Resolve(device, wavLoopPath: null, NoSettings).Kind.Name.Should().Be(kind);
    }

    [Theory]
    [MemberData(nameof(Devices))]
    public void Every_Capability_Answers_What_Program_Cs_Used_To_Work_Out_From_The_String(
        string device, string kind)
    {
        _ = kind;
        bool deviceIsFlex = FlexDevice.IsFlex(device);
        bool flexIsHeadless = deviceIsFlex && FlexDevice.Parse(device).Headless;
        bool deviceIsUberSdr = UberSdrDevice.IsUberSdr(device);
        // A web receiver of the second kind (#599) answers as the first does, except where the
        // two differ by name: its own dead-feed family and its own mailcast kind.
        bool deviceIsOpenWebRx = OpenWebRxDevice.IsOpenWebRx(device);
        bool webReceiver = deviceIsUberSdr || deviceIsOpenWebRx;
        bool flexIsMock = deviceIsFlex
            && FlexDevice.Parse(device).RadioSpec.Equals("mock", StringComparison.OrdinalIgnoreCase);

        StationDevice station = DeviceKinds.Resolve(device, wavLoopPath: null, NoSettings);

        station.CaptureRateApplies.Should().Be(!deviceIsFlex && !webReceiver);
        station.OwnsTheRadio.Should().Be(flexIsHeadless);
        station.Kind.OwnsTheRadio(device).Should().Be(flexIsHeadless);
        station.SelfTunes.Should().Be(flexIsHeadless || webReceiver);
        station.ReportsTransmitFilter.Should().Be(deviceIsFlex);
        station.ClosesItsOwnStreams.Should().Be(deviceIsFlex);
        (station.PttRefusal is not null).Should().Be(deviceIsFlex || webReceiver);
        (station.ReceiveOnlyReason is not null).Should().Be(webReceiver);
        (station.NoReceiveDialRefusal is not null).Should().Be(webReceiver);
        (station.Kind.TransmitTestRefusal is not null).Should().Be(webReceiver);
        (station.Kind.RigRefusal(device) is not null).Should().Be(deviceIsFlex || webReceiver);
        (station.Kind.PublishRefusal(device) is not null).Should().Be(webReceiver);
        station.SettingsProblem.Should().BeNull();

        station.MailcastKind.Should().Be(
            flexIsHeadless ? MailcastRadioKind.FlexHeadless
            : deviceIsUberSdr ? MailcastRadioKind.UberSdr
            : deviceIsOpenWebRx ? MailcastRadioKind.OpenWebRx
            : deviceIsFlex ? MailcastRadioKind.FlexAttach
            : MailcastRadioKind.SoundCard);

        station.DeadFeedKind.Should().Be(
            deviceIsUberSdr ? DeadFeedDevice.UberSdr
            : deviceIsOpenWebRx ? DeadFeedDevice.OpenWebRx
            : deviceIsFlex ? (flexIsMock ? DeadFeedDevice.WavLoop : DeadFeedDevice.Flex)
            : DeadFeedDevice.Alsa);

        DaemonConfig.IsSoundCard(device).Should().Be(
            !deviceIsFlex && !webReceiver && !PipeAudio.IsPipe(device));
    }

    [Theory]
    [MemberData(nameof(Devices))]
    public void A_Wav_Loop_Replaces_The_Audio_And_Nothing_Else_About_The_Named_Device(
        string device, string kind)
    {
        _ = kind;
        StationDevice named = DeviceKinds.Resolve(device, wavLoopPath: null, NoSettings);
        StationDevice looped = DeviceKinds.Resolve(device, "/tmp/recording.wav", NoSettings);

        looped.Should().BeOfType<WavLoopStationDevice>();
        looped.Kind.Should().BeSameAs(named.Kind);
        looped.Spec.Should().Be(device);
        looped.CaptureRateApplies.Should().Be(named.CaptureRateApplies);
        looped.OwnsTheRadio.Should().Be(named.OwnsTheRadio);
        looped.SelfTunes.Should().Be(named.SelfTunes);
        looped.ReportsTransmitFilter.Should().Be(named.ReportsTransmitFilter);
        looped.ClosesItsOwnStreams.Should().Be(named.ClosesItsOwnStreams);
        looped.PttRefusal.Should().Be(named.PttRefusal);
        looped.ReceiveOnlyReason.Should().Be(named.ReceiveOnlyReason);
        looped.NoReceiveDialRefusal.Should().Be(named.NoReceiveDialRefusal);
        looped.MailcastKind.Should().Be(named.MailcastKind);
        looped.DeadFeedKind.Should().Be(
            DeadFeedDevice.WavLoop, "a recording is a bench input whatever device it stands in for");
    }

    [Fact]
    public void A_Malformed_UberSdr_Device_Is_Refused_When_It_Is_Resolved()
    {
        Action resolve = () => DeviceKinds.Resolve("ubersdr:", wavLoopPath: null, NoSettings);

        resolve.Should().Throw<InvalidDataException>().WithMessage("*names no instance*");
    }

    [Fact]
    public void An_UberSdr_Says_Which_Instance_It_Cannot_Transmit_Through()
    {
        StationDevice station = DeviceKinds.Resolve(
            "ubersdr:m9psy-1.instance.ubersdr.org", wavLoopPath: null, NoSettings);

        station.ReceiveOnlyReason.Should().Be(
            "this station receives only: its audio comes from the UberSDR instance at "
            + "m9psy-1.instance.ubersdr.org, which is a receiver and has no transmitter.");
        station.PttRefusal.Should().StartWith("--device ubersdr: is a receive-only station");
    }

    [Fact]
    public void An_On_Demand_UberSdr_Needs_A_Waterfall_And_A_Linger_That_Is_Not_Negative()
    {
        const string device = "ubersdr:m9psy-1.instance.ubersdr.org";

        DeviceKinds.Resolve(device, null, new DeviceSettings(
                new UberSdrConfig { OnDemand = true, LingerSeconds = -1 }, HasWaterfall: true))
            .SettingsProblem.Should().Be("\"ubersdr\".\"lingerSeconds\" cannot be negative");

        DeviceKinds.Resolve(device, null, new DeviceSettings(
                new UberSdrConfig { OnDemand = true }, HasWaterfall: false))
            .SettingsProblem.Should().StartWith("\"ubersdr\".\"onDemand\" needs a \"waterfall\" section");

        DeviceKinds.Resolve(device, null, new DeviceSettings(
                new UberSdrConfig { OnDemand = true }, HasWaterfall: true))
            .SettingsProblem.Should().BeNull();

        DeviceKinds.Resolve(device, null, new DeviceSettings(
                new UberSdrConfig { OnDemand = false, LingerSeconds = -1 }, HasWaterfall: false))
            .SettingsProblem.Should().BeNull("the linger means nothing to an always-on session");
    }

    [Fact]
    public void An_OpenWebRx_Receiver_Says_Which_Receiver_It_Cannot_Transmit_Through()
    {
        StationDevice station = DeviceKinds.Resolve(
            "openwebrx:https://sdr.example.org/owrx/", wavLoopPath: null, NoSettings);

        station.ReceiveOnlyReason.Should().Be(
            "this station receives only: its audio comes from the OpenWebRX receiver at "
            + "sdr.example.org/owrx, which is a receiver and has no transmitter.");
        station.PttRefusal.Should().StartWith("--device openwebrx: is a receive-only station");
        station.Kind.PublishRefusal("openwebrx:https://sdr.example.org/owrx/").Should().Contain(
            "somebody else's public web receiver");
    }

    [Fact]
    public void A_Malformed_OpenWebRx_Device_Is_Refused_When_It_Is_Resolved()
    {
        Action resolve = () => DeviceKinds.Resolve("openwebrx:", wavLoopPath: null, NoSettings);

        resolve.Should().Throw<InvalidDataException>().WithMessage("*names no receiver*");
    }

    [Theory]
    [InlineData(null, null, null, null, null, null)]
    [InlineData(300, 2700, null, null, null, null)]
    [InlineData(0, 6000, 6000, 0, 2.0, null)]
    [InlineData(2700, 300, null, null, null, "*\"ssbLowHz\" and \"ssbHighHz\" (2700 and 300)*")]
    [InlineData(150, 6001, null, null, null, "*at most 6000 Hz*")]
    [InlineData(-1, 3000, null, null, null, "*\"ssbLowHz\"*")]
    [InlineData(null, null, 0, null, null, "*\"fmHalfWidthHz\" (0)*")]
    [InlineData(null, null, 7000, null, null, "*\"fmHalfWidthHz\" (7000)*")]
    [InlineData(null, null, null, -5, null, "*\"startupGuardMs\" cannot be negative")]
    [InlineData(null, null, null, null, 0.0, "*\"gain\" has to be above 0")]
    public void An_OpenWebRx_Section_Is_Checked_Before_Anything_Is_Opened(
        int? low, int? high, int? fmHalfWidth, int? guard, double? gain, string? problem)
    {
        var config = new OpenWebRxConfig
        {
            SsbLowHz = low,
            SsbHighHz = high,
            FmHalfWidthHz = fmHalfWidth,
            StartupGuardMs = guard,
            Gain = gain,
        };

        string? said = DeviceKinds.Resolve(
            "openwebrx:sdr.example.org", null, new DeviceSettings(null, HasWaterfall: false, config)).SettingsProblem;

        if (problem is null)
        {
            said.Should().BeNull();
        }
        else
        {
            said.Should().Match(problem);
        }
    }

    [Fact]
    public void The_Sound_Card_Is_The_Default_And_Is_Tried_Last()
    {
        DeviceKinds.All[^1].Should().BeSameAs(DeviceKinds.Alsa);
        DeviceKinds.All.Should().OnlyHaveUniqueItems();
        DeviceKinds.All.Select(k => k.Name).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void The_Device_Help_Names_Every_Kind_And_Only_The_Kinds_There_Are()
    {
        // The --device paragraph of --help: its own line and the continuation lines under it.
        string[] lines = Usage.Text.Split('\n');
        int start = Array.FindIndex(lines, l => l.StartsWith("  --device ", StringComparison.Ordinal));
        start.Should().BeGreaterThan(-1, "--help describes --device");
        int end = Array.FindIndex(lines, start + 1, l => l.StartsWith("  --", StringComparison.Ordinal));
        string paragraph = Regex.Replace(
            string.Join(' ', lines[start..end]), @"\s+", " ");

        foreach (DeviceKind kind in DeviceKinds.All)
        {
            paragraph.Should().Contain(
                kind.Spelling,
                $"the {kind.Name} kind is registered, so --help has to say how to spell it");
        }

        // And the other way: every PREFIX:WORD spelling the paragraph offers is one a kind owns.
        foreach (Match spelling in Regex.Matches(paragraph, @"\b([a-z]+):[A-Z]"))
        {
            string offered = paragraph[spelling.Index..].Split(' ')[0].TrimEnd(',', '.');
            DeviceKinds.All.Should().Contain(
                k => k.Spelling == offered,
                $"--help offers {offered}, which no registered kind spells that way");
        }
    }
}
