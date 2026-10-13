using AwesomeAssertions;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The <c>rtltcp:</c> kind behind the device seam: what it answers start-up, and the
/// <c>"rtltcp"</c> section's checks.
/// </summary>
public class RtlTcpDeviceKindTests
{
    private static DeviceSettings With(RtlTcpConfig? config) => new(UberSdr: null, HasWaterfall: false) { RtlTcp = config };

    [Theory]
    [InlineData("rtltcp:pi.local")]
    [InlineData("RTLTCP:192.168.1.20:1234")]
    [InlineData("rtltcp:[fd00::5]:1234")]
    public void An_Rtltcp_Device_Is_A_Self_Tuning_Receive_Only_Station_With_Its_Own_Clock(string device)
    {
        DeviceKinds.Of(device).Should().BeSameAs(DeviceKinds.RtlTcp);
        StationDevice station = DeviceKinds.Resolve(device, wavLoopPath: null, With(null));

        station.Kind.Name.Should().Be("rtltcp");
        station.CaptureRateApplies.Should().BeFalse();
        station.SelfTunes.Should().BeTrue();
        station.OwnsTheRadio.Should().BeFalse();
        station.Kind.OwnsTheRadio(device).Should().BeFalse();
        station.ReportsTransmitFilter.Should().BeFalse();
        station.ClosesItsOwnStreams.Should().BeFalse();
        station.ReceiveOnlyReason.Should().Contain("rtl_tcp server");
        station.PttRefusal.Should().StartWith("--device rtltcp: is a receive-only station");
        station.NoReceiveDialRefusal.Should().Contain("has to be told where to listen");
        station.Kind.TransmitTestRefusal.Should().StartWith("tx test: refused");
        station.Kind.RigRefusal(device).Should().Contain("remove \"rig\"");
        station.Kind.PublishRefusal(device).Should().BeNull("an rtl_tcp server is the operator's own receiver");
        station.MailcastKind.Should().Be(MailcastRadioKind.SoundCard);
        station.DeadFeedKind.Should().Be(DeadFeedDevice.UberSdr);
        station.SettingsProblem.Should().BeNull();
        DaemonConfig.IsSoundCard(device).Should().BeFalse();
    }

    [Fact]
    public void A_Malformed_Rtltcp_Device_Is_Refused_When_It_Is_Resolved()
    {
        Action resolve = () => DeviceKinds.Resolve("rtltcp:", wavLoopPath: null, With(null));

        resolve.Should().Throw<InvalidDataException>().WithMessage("*names no server*");
    }

    [Fact]
    public void It_Is_Tried_Before_The_Sound_Card()
    {
        DeviceKinds.All.Should().Contain(DeviceKinds.RtlTcp);
        DeviceKinds.All[^1].Should().BeSameAs(DeviceKinds.Alsa);
    }

    public static TheoryData<RtlTcpConfig, string?> Sections => new()
    {
        { new RtlTcpConfig(), null },
        { new RtlTcpConfig { SampleRate = 2_048_000, GainDb = 29.7, Ppm = -3, RtlAgc = false }, null },
        { new RtlTcpConfig { LfoOffsetHz = 66_021_528, IqSwap = true }, null },
        { new RtlTcpConfig { LfoOffsetHz = double.PositiveInfinity }, "*\"lfoOffsetHz\" has to be a frequency*" },
        { new RtlTcpConfig { IqSwap = true }, "*\"iqSwap\" is for a converter*" },
        { new RtlTcpConfig { IqSwap = false }, null },
        { new RtlTcpConfig { ReadOnly = true, CentreFrequency = 144_900_000, SampleRate = 2_400_000, LfoOffsetHz = 66_021_528 }, "*\"lfoOffsetHz\" and \"iqSwap\"*" },
        { new RtlTcpConfig { ReadOnly = true, CentreFrequency = 144_900_000, SampleRate = 2_400_000, IqSwap = true }, "*\"lfoOffsetHz\" and \"iqSwap\"*" },
        { new RtlTcpConfig { ReadOnly = true, CentreFrequency = 144_900_000, SampleRate = 2_400_000 }, null },
        { new RtlTcpConfig { ReadOnly = true, SampleRate = 2_400_000 }, "*needs \"centreFrequency\"*" },
        { new RtlTcpConfig { ReadOnly = true, CentreFrequency = 144_900_000 }, "*needs \"sampleRate\"*" },
        { new RtlTcpConfig { ReadOnly = true, CentreFrequency = 144_900_000, SampleRate = 2_400_000, GainDb = 20 }, "*\"readOnly\" sends nothing*" },
        { new RtlTcpConfig { CentreFrequency = 144_900_000 }, "*is for a \"readOnly\" server*" },
        { new RtlTcpConfig { SampleRate = 600_000 }, "*not a rate an RTL dongle runs at*" },
        { new RtlTcpConfig { GainDb = 70 }, "*\"gainDb\" (70)*" },
        { new RtlTcpConfig { Ppm = 5000 }, "*\"ppm\" (5000)*" },
        { new RtlTcpConfig { NoDataSeconds = 0 }, "*\"noDataSeconds\" has to be above 0" },
        { new RtlTcpConfig { Gain = 0 }, "*\"gain\" has to be above 0" },
        { new RtlTcpConfig { SsbLowHz = 2700, SsbHighHz = 300 }, "*(2700 and 300)*" },
        { new RtlTcpConfig { FmIfBandwidthHz = 0 }, "*\"fmIfBandwidthHz\" has to be above 0" },
        { new RtlTcpConfig { FmDeviationHz = -1 }, "*\"fmDeviationHz\" has to be above 0" },
    };

    [Theory]
    [MemberData(nameof(Sections))]
    public void An_Rtltcp_Section_Is_Checked_Before_Anything_Is_Opened(RtlTcpConfig config, string? problem)
    {
        string? said = DeviceKinds.Resolve("rtltcp:pi.local", null, With(config)).SettingsProblem;

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
    public void An_Unreachable_Server_Is_Explained_With_Where_The_Setting_Lives()
    {
        string text = RtlTcpStationDevice.Diagnostic("rtltcp:pi.local", "/etc/pdn-soundmodem/soundmodem.json",
            new IOException("no answer from pi.local:1234 in 10 s"));

        text.Should().Contain("cannot listen through \"rtltcp:pi.local\"");
        text.Should().Contain("Set by \"device\" in /etc/pdn-soundmodem/soundmodem.json");
        text.Should().Contain("-a");
        text.Should().MatchRegex("^[\\x00-\\x7F]*$", "printable strings are ASCII");
    }
}
