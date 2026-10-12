using AwesomeAssertions;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The <c>sdrconnect:</c> kind behind the device seam: what it answers start-up, and the
/// <c>"sdrconnect"</c> section's checks.
/// </summary>
public class SdrConnectDeviceKindTests
{
    private static DeviceSettings With(SdrConnectConfig? config) => new(UberSdr: null, HasWaterfall: false) { SdrConnect = config };

    [Theory]
    [InlineData("sdrconnect:shack-pc")]
    [InlineData("SDRCONNECT:192.168.1.20:5454")]
    [InlineData("sdrconnect:[fd00::5]:5454")]
    public void An_Sdrconnect_Device_Is_A_Self_Tuning_Receive_Only_Station_With_Its_Own_Clock(string device)
    {
        DeviceKinds.Of(device).Should().BeSameAs(DeviceKinds.SdrConnect);
        StationDevice station = DeviceKinds.Resolve(device, wavLoopPath: null, With(null));

        station.Kind.Name.Should().Be("sdrconnect");
        station.CaptureRateApplies.Should().BeFalse();
        station.SelfTunes.Should().BeTrue();
        station.OwnsTheRadio.Should().BeFalse();
        station.Kind.OwnsTheRadio(device).Should().BeFalse();
        station.ReportsTransmitFilter.Should().BeFalse();
        station.ClosesItsOwnStreams.Should().BeFalse();
        station.ReceiveOnlyReason.Should().Contain("SDRconnect");
        station.PttRefusal.Should().StartWith("--device sdrconnect: is a receive-only station");
        station.NoReceiveDialRefusal.Should().Contain("has to be told where to listen");
        station.Kind.TransmitTestRefusal.Should().StartWith("tx test: refused");
        station.Kind.RigRefusal(device).Should().Contain("remove \"rig\"");
        station.Kind.PublishRefusal(device).Should().BeNull("SDRconnect is the operator's own receiver");
        station.MailcastKind.Should().Be(MailcastRadioKind.SoundCard);
        station.DeadFeedKind.Should().Be(DeadFeedDevice.UberSdr);
        station.SettingsProblem.Should().BeNull();
        DaemonConfig.IsSoundCard(device).Should().BeFalse();
    }

    [Fact]
    public void A_Malformed_Sdrconnect_Device_Is_Refused_When_It_Is_Resolved()
    {
        Action resolve = () => DeviceKinds.Resolve("sdrconnect:", wavLoopPath: null, With(null));

        resolve.Should().Throw<InvalidDataException>().WithMessage("*names no SDRconnect*");
    }

    [Fact]
    public void It_Is_Tried_Before_The_Sound_Card()
    {
        DeviceKinds.All.Should().Contain(DeviceKinds.SdrConnect);
        DeviceKinds.All[^1].Should().BeSameAs(DeviceKinds.Alsa);
    }

    public static TheoryData<SdrConnectConfig, string?> Sections => new()
    {
        { new SdrConnectConfig(), null },
        { new SdrConnectConfig { Mode = "iq", SampleRate = 2_000_000, LnaState = 4, Antenna = "Antenna B" }, null },
        { new SdrConnectConfig { Mode = "AUDIO", SsbLowHz = 300, SsbHighHz = 2700, Gain = 2 }, null },
        { new SdrConnectConfig { DeviceSerial = "2305012345" }, null },
        { new SdrConnectConfig { Mode = "both" }, "*\"mode\" (\"both\") has to be \"iq\"*" },
        { new SdrConnectConfig { SampleRate = 1000 }, "*\"sampleRate\" (1000) is not a rate an RSP runs at*" },
        { new SdrConnectConfig { LnaState = -1 }, "*\"lnaState\" cannot be negative*" },
        { new SdrConnectConfig { DeviceSerial = "1", DeviceName = "RSP1A" }, "*both \"deviceSerial\" and \"deviceName\"*" },
        { new SdrConnectConfig { DeviceName = " " }, "*\"deviceName\" is empty*" },
        { new SdrConnectConfig { Antenna = "" }, "*\"antenna\" is empty*" },
        { new SdrConnectConfig { NoDataSeconds = 0 }, "*\"noDataSeconds\" has to be above 0" },
        { new SdrConnectConfig { Gain = 0 }, "*\"gain\" has to be above 0" },
        { new SdrConnectConfig { SsbLowHz = 2700, SsbHighHz = 300 }, "*(2700 and 300)*" },
        { new SdrConnectConfig { Mode = "audio", SsbHighHz = 30000 }, "*above 24000 Hz*" },
        { new SdrConnectConfig { FmIfBandwidthHz = 0 }, "*\"fmIfBandwidthHz\" has to be above 0" },
        { new SdrConnectConfig { FmDeviationHz = -1 }, "*\"fmDeviationHz\" has to be above 0" },
        { new SdrConnectConfig { Mode = "audio", FmDeviationHz = 3000 }, "*uses SDRconnect's; remove it" },
    };

    [Theory]
    [MemberData(nameof(Sections))]
    public void An_Sdrconnect_Section_Is_Checked_Before_Anything_Is_Opened(SdrConnectConfig config, string? problem)
    {
        string? said = DeviceKinds.Resolve("sdrconnect:shack-pc", null, With(config)).SettingsProblem;

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
    public void Unreachable_SDRconnect_Is_Explained_With_Where_The_Setting_Lives()
    {
        string text = SdrConnectStationDevice.Diagnostic("sdrconnect:shack-pc", "/etc/pdn-soundmodem/soundmodem.json",
            new IOException("cannot reach SDRconnect at ws://shack-pc:5454/: connection refused"));

        text.Should().Contain("cannot listen through \"sdrconnect:shack-pc\"");
        text.Should().Contain("Set by \"device\" in /etc/pdn-soundmodem/soundmodem.json");
        text.Should().Contain("firewall");
        text.Should().MatchRegex("^[\\x00-\\x7F]*$", "printable strings are ASCII");
    }

    [Fact]
    public void An_Sdrconnect_Section_Is_Read_From_The_Configuration_File()
    {
        DaemonConfig config;
        string dir = Directory.CreateTempSubdirectory("pdnsm-sdrc").FullName;
        try
        {
            string path = Path.Combine(dir, "soundmodem.json");
            File.WriteAllText(path, """
                {
                  "device": "sdrconnect:shack-pc",
                  "sdrconnect": { "mode": "audio", "lnaState": 5, "antenna": "Antenna B", "colour": "red" }
                }
                """);
            DaemonConfig? loaded = DaemonConfig.TryLoad(path, out string error);
            error.Should().BeEmpty();
            config = loaded!;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        config.SdrConnect.Should().NotBeNull();
        config.SdrConnect!.Mode.Should().Be("audio");
        config.SdrConnect.LnaState.Should().Be(5);
        config.SdrConnect.Antenna.Should().Be("Antenna B");
        config.SdrConnect.UnknownSettings.Should().ContainKey("colour");
    }
}
