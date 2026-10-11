using Packet.SoundModem.Daemon;
using Packet.SoundModem.Iq;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The <c>iqcard:</c> device kind (issue #596): what it is matched by, what it refuses, and the
/// channel it asks the front end for. Opening it needs a sound card, so what is pinned here is
/// everything start-up decides before that.
/// </summary>
public class IqCardDeviceTests
{
    private static readonly DeviceSettings NoSettings = new(UberSdr: null, HasWaterfall: false);

    [Theory]
    [InlineData("iqcard:plughw:CARD=Device,DEV=0", "plughw:CARD=Device,DEV=0")]
    [InlineData("IQCard:hw:1,0", "hw:1,0")]
    [InlineData("iqcard: default ", "default")]
    public void An_Iqcard_Device_Names_The_Alsa_Card_After_Its_Prefix(string device, string card)
    {
        DeviceKinds.Of(device).Should().BeSameAs(DeviceKinds.IqCard);
        StationDevice station = DeviceKinds.Resolve(device, wavLoopPath: null, NoSettings);

        station.Should().BeOfType<IqCardStationDevice>().Which.Card.Should().Be(card);
    }

    [Fact]
    public void An_Iqcard_Device_With_No_Card_Is_Refused_When_It_Is_Resolved()
    {
        FluentActions.Invoking(() => DeviceKinds.Resolve("iqcard:", wavLoopPath: null, NoSettings))
            .Should().Throw<InvalidDataException>().WithMessage("*names no sound card*arecord -L*");
    }

    [Fact]
    public void A_Sound_Card_Whose_Name_Merely_Contains_Iq_Is_Still_A_Sound_Card()
    {
        DeviceKinds.Of("plughw:CARD=iqcard,DEV=0").Should().BeSameAs(DeviceKinds.Alsa);
    }

    [Fact]
    public void It_Receives_Only_Tunes_Itself_And_Brings_Its_Own_Clock()
    {
        StationDevice station = DeviceKinds.Resolve("iqcard:hw:G90", wavLoopPath: null, NoSettings);

        station.CaptureRateApplies.Should().BeFalse("the front end resamples any rate the card runs at");
        station.SelfTunes.Should().BeTrue("the channel is placed in the card's band by the station");
        station.OwnsTheRadio.Should().BeFalse();
        station.ReceiveOnlyReason.Should().Contain("hw:G90").And.Contain("receives only");
        station.PttRefusal.Should().Contain("Remove \"ptt\"");
        station.NoReceiveDialRefusal.Should().Contain("dialFrequency");
        station.Kind.TransmitTestRefusal.Should().NotBeNull();
        station.Kind.HasMixer.Should().BeFalse("its mixer is left alone, and the page has none to show");
        station.Kind.RigRefusal("iqcard:hw:G90").Should().BeNull("a rig is where the IQ centre can come from");
        station.Kind.PublishRefusal("iqcard:hw:G90").Should().BeNull("it is the station's own radio");
    }

    public static TheoryData<IqCardConfig, string> BadSections => new()
    {
        { new IqCardConfig { SampleRate = 4000 }, "\"sampleRate\" (4000)" },
        { new IqCardConfig { CentreFrequency = -1 }, "\"centreFrequency\"" },
        { new IqCardConfig { CentreOffsetHz = double.NaN }, "\"centreOffsetHz\"" },
        { new IqCardConfig { GainImbalanceDb = 9 }, "gain imbalance" },
        { new IqCardConfig { PhaseImbalanceDegrees = -45 }, "phase imbalance" },
        { new IqCardConfig { SsbLowHz = 3000, SsbHighHz = 300 }, "\"ssbLowHz\"" },
        { new IqCardConfig { Gain = 0 }, "\"gain\"" },
    };

    [Theory]
    [MemberData(nameof(BadSections))]
    public void An_Iqcard_Section_Is_Checked_Before_Anything_Is_Opened(IqCardConfig config, string named)
    {
        StationDevice station = DeviceKinds.Resolve(
            "iqcard:hw:G90", wavLoopPath: null, NoSettings with { IqCard = config });

        station.SettingsProblem.Should().Contain("\"iqCard\"").And.Contain(named);
    }

    [Fact]
    public void A_Good_Section_Has_No_Problem()
    {
        IqCardStationDevice.Problem(new IqCardConfig
        {
            CentreFrequency = 7_040_000,
            SampleRate = 96000,
            SwapIq = true,
            GainImbalanceDb = 0.4,
            PhaseImbalanceDegrees = -2,
            Gain = 2,
        }).Should().BeNull();
    }

    [Theory]
    [InlineData("usb", IqDemodulation.Usb)]
    [InlineData("LSB", IqDemodulation.Lsb)]
    [InlineData("fm", IqDemodulation.Fm)]
    public void The_Channel_Is_At_The_Dials_Offset_From_The_Centre_In_The_Plans_Sideband(
        string sideband, IqDemodulation demodulation)
    {
        IqChannelSpec spec = IqCardStationDevice.Channel(
            new IqCardConfig { SsbLowHz = 300, SsbHighHz = 2700, Gain = 2 }, sideband, 6000);

        spec.OffsetHz.Should().Be(6000);
        spec.Demodulation.Should().Be(demodulation);
        spec.SsbLowHz.Should().Be(300);
        spec.SsbHighHz.Should().Be(2700);
        spec.Gain.Should().Be(2f);
    }

    [Theory]
    [InlineData("usb", -1000, true)]   // the centre 1 kHz above a USB dial: in its audio
    [InlineData("usb", 1000, false)]   // below it: clear
    [InlineData("usb", 6000, false)]
    [InlineData("lsb", 1000, true)]    // the centre 1 kHz below an LSB dial: in its audio
    [InlineData("lsb", -1000, false)]
    [InlineData("fm", 3000, true)]
    [InlineData("fm", 12000, false)]
    public void A_Centre_Inside_The_Channels_Audio_Is_Noticed(string sideband, double offsetHz, bool inside)
    {
        IqCardStationDevice.CentreInPassband(IqCardStationDevice.Channel(null, sideband, offsetHz))
            .Should().Be(inside);
    }

    [Fact]
    public void The_Source_Is_Given_The_Sections_Wiring_And_Balance()
    {
        SoundCardIqOptions options = IqCardStationDevice.Options(
            new IqCardConfig { SwapIq = true, GainImbalanceDb = 0.3, PhaseImbalanceDegrees = 1.5 }, 7_040_000);

        options.CentreFrequencyHz.Should().Be(7_040_000);
        options.SwapIq.Should().BeTrue();
        options.GainImbalanceDb.Should().Be(0.3);
        options.PhaseImbalanceDegrees.Should().Be(1.5);
        options.MaxReadFrames.Should().Be(IqCardStationDevice.ReadBlockFrames);
    }

    [Fact]
    public void An_Iqcard_Section_Is_Read_From_The_File_And_A_Misspelt_Key_Is_Reported()
    {
        string path = Path.Combine(Path.GetTempPath(), $"iqcard-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {
              "device": "iqcard:plughw:CARD=Device,DEV=0",
              "dialFrequency": 10147300,
              "iqCard": { "centreFrequency": 10141300, "swapIq": true, "gainImbalanceDb": 0.5, "phaseImbalanceDeg": 3 }
            }
            """);
        try
        {
            DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

            config.Should().NotBeNull(error);
            config!.IqCard!.CentreFrequency.Should().Be(10_141_300);
            config.IqCard.SwapIq.Should().BeTrue();
            config.IqCard.GainImbalanceDb.Should().Be(0.5);
            config.Warnings.Should().Contain(w => w.Contains("iqCard: \"phaseImbalanceDeg\""));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
