using System.Text.Json.Nodes;
using AwesomeAssertions;
using Packet.SoundModem.OpenWebRx;

namespace Packet.SoundModem.Tests.OpenWebRx;

/// <summary>
/// One session's conversation with an OpenWebRX server, without the socket: what the receiver
/// says, and what the client answers.
/// </summary>
public class OpenWebRxConversationTests
{
    private static readonly OpenWebRxTuning Usb = OpenWebRxTuning.For(7_048_800, "usb", 150, 3450, 5000);

    /// <summary>A config message as the server sends it, with only the keys given.</summary>
    private static string Config(long? centre = null, long? rate = null, string? compression = null,
        string? sdr = null, string? profile = null)
    {
        var value = new JsonObject();
        if (centre is not null)
        {
            value["center_freq"] = centre;
        }

        if (rate is not null)
        {
            value["samp_rate"] = rate;
        }

        if (compression is not null)
        {
            value["audio_compression"] = compression;
        }

        if (sdr is not null)
        {
            value["sdr_id"] = sdr;
        }

        if (profile is not null)
        {
            value["profile_id"] = profile;
        }

        return new JsonObject { ["type"] = "config", ["value"] = value }.ToJsonString();
    }

    private const string Profiles =
        """{"type":"profiles","value":[{"id":"rtl|40m","name":"40m"},{"id":"rtl|2m","name":"2m APRS"}]}""";

    private static JsonObject Json(string message) => (JsonObject)JsonNode.Parse(message)!;

    [Fact]
    public void A_Session_Opens_As_The_Browser_Client_Opens_One()
    {
        OpenWebRxConversation.Opening().Should().Equal(
            "SERVER DE CLIENT client=pdn-soundmodem type=receiver",
            """{"type":"connectionproperties","params":{"output_rate":12000,"hd_output_rate":48000}}""");
    }

    [Fact]
    public void The_Servers_Answer_Names_It_And_Its_Version()
    {
        var conversation = new OpenWebRxConversation(Usb);

        conversation.OnText("CLIENT DE SERVER server=openwebrx version=v1.2.2").Send.Should().BeEmpty();

        conversation.Greeted.Should().BeTrue();
        conversation.Server.Should().Be("openwebrx v1.2.2");
    }

    [Fact]
    public void Once_The_Receiver_Says_Where_It_Is_The_Demodulator_Is_Placed_And_Started()
    {
        var conversation = new OpenWebRxConversation(Usb);
        conversation.OnText(Config(compression: "adpcm")).Send.Should().BeEmpty("the centre is not known yet");
        conversation.Adpcm.Should().BeTrue();

        OpenWebRxReaction reaction = conversation.OnText(Config(centre: 7_100_000, rate: 2_400_000));

        reaction.Send.Should().HaveCount(2);
        JsonObject dsp = Json(reaction.Send[0]);
        dsp["type"]!.GetValue<string>().Should().Be("dspcontrol");
        dsp["params"]!.ToJsonString().Should().Be(
            """{"low_cut":150,"high_cut":3450,"offset_freq":-51200,"mod":"usb","squelch_level":-150}""");
        reaction.Send[1].Should().Be("""{"type":"dspcontrol","action":"start"}""");
        conversation.Listening.Should().BeTrue();
    }

    [Theory]
    [InlineData("lsb", "lsb", -3450, -150)]
    [InlineData("LSB", "lsb", -3450, -150)]
    [InlineData("usb", "usb", 150, 3450)]
    [InlineData("fm", "nfm", -5000, 5000)]
    public void The_Passband_Is_Asked_For_The_Way_The_Receiver_Takes_It(
        string sideband, string modulation, int low, int high)
    {
        OpenWebRxTuning tuning = OpenWebRxTuning.For(144_800_000, sideband, 150, 3450, 5000);

        tuning.Modulation.Should().Be(modulation);
        tuning.LowCutHz.Should().Be(low);
        tuning.HighCutHz.Should().Be(high);
    }

    [Fact]
    public void A_Dial_The_Band_Does_Not_Reach_Is_Waited_For_And_Taken_Up_When_It_Does()
    {
        var conversation = new OpenWebRxConversation(Usb);
        OpenWebRxReaction away = conversation.OnText(Config(centre: 14_100_000, rate: 2_400_000, sdr: "rtl", profile: "20m"));

        away.Send.Should().BeEmpty();
        away.Lines.Should().ContainSingle().Which.Should().Be(
            "the receiver is on 12.900000 to 15.300000 MHz (profile rtl|20m), which does not reach "
            + "7.048800 MHz; listening there resumes when it does");
        conversation.OutOfBand.Should().BeTrue();
        conversation.Listening.Should().BeFalse();

        conversation.OnText(Config(centre: 14_200_000)).Lines.Should().ContainSingle(
            "a move that still does not reach is said again, since it is news");

        OpenWebRxReaction back = conversation.OnText(Config(centre: 7_100_000, profile: "40m"));
        back.Lines.Should().ContainSingle().Which.Should().Contain("reaches 7.048800 MHz again");
        back.Send.Should().HaveCount(2);
        conversation.Listening.Should().BeTrue();
    }

    [Fact]
    public void A_Passband_That_Would_Hang_Over_The_Band_Edge_Does_Not_Fit()
    {
        // 7.0488 MHz with 3450 Hz above it is 7.05225 MHz, a little past a band that stops at 7.052.
        OpenWebRxProtocol.OffsetFor(7_040_000, 24_000, Usb).Should().BeNull();
        OpenWebRxProtocol.OffsetFor(7_040_000, 25_000, Usb).Should().Be(8_800);
    }

    [Fact]
    public void A_Profile_Named_In_The_Config_Is_Asked_For_Once_And_Then_Listened_On()
    {
        var conversation = new OpenWebRxConversation(Usb with { Profile = "40M" });
        conversation.OnText(Config(centre: 145_000_000, rate: 2_400_000, sdr: "rtl", profile: "2m"))
            .Send.Should().BeEmpty("a name can only be matched once the list arrives");

        OpenWebRxReaction ask = conversation.OnText(Profiles);
        ask.Send.Should().Equal("""{"type":"selectprofile","params":{"profile":"rtl|40m"}}""");
        ask.Lines.Should().ContainSingle().Which.Should().Contain("which moves it for everyone listening");

        // The server's next word may still carry the old band; nothing is placed on it.
        conversation.OnText(Config(centre: 145_000_000)).Send.Should().BeEmpty();
        conversation.OutOfBand.Should().BeFalse();

        OpenWebRxReaction placed = conversation.OnText(Config(centre: 7_100_000, profile: "40m"));
        placed.Send.Should().HaveCount(2);
        conversation.Listening.Should().BeTrue();
    }

    [Fact]
    public void A_Profile_Is_Not_Asked_For_Until_The_Input_Says_It_May_Be()
    {
        var conversation = new OpenWebRxConversation(Usb with { Profile = "rtl|40m" });

        conversation.OnText(Config(centre: 145_000_000, rate: 2_400_000, sdr: "rtl", profile: "2m"), mayAskProfile: false)
            .Send.Should().BeEmpty();
        conversation.OutOfBand.Should().BeFalse("the band it is on now is not the one it will be asked onto");

        conversation.OnText("""{"type":"smeter","value":-90.0}""", mayAskProfile: true).Send.Should().Equal(
            ["""{"type":"selectprofile","params":{"profile":"rtl|40m"}}"""],
            "whatever the receiver says next carries the ask that was held back");
    }

    [Fact]
    public void A_Profile_The_Receiver_Keeps_Is_A_Fault_Rather_Than_A_Wait()
    {
        var conversation = new OpenWebRxConversation(Usb with { Profile = "rtl|40m" });
        conversation.OnText(Config(centre: 145_000_000, rate: 2_400_000, sdr: "rtl", profile: "2m"));

        conversation.OnText(Config(centre: 145_000_000, rate: 2_400_000, sdr: "rtl", profile: "2m"));

        conversation.Fault.Should().Be(
            "the receiver kept profile rtl|2m rather than moving to rtl|40m; it may be locked by its operator");
    }

    [Fact]
    public void A_Demodulator_On_A_New_Sdr_Is_Started_Again()
    {
        var conversation = new OpenWebRxConversation(Usb);
        conversation.OnText(Config(centre: 7_100_000, rate: 2_400_000, sdr: "rtl", profile: "40m"));

        OpenWebRxReaction reaction = conversation.OnText(
            Config(centre: 7_100_000, rate: 2_400_000, sdr: "airspy", profile: "40m"));

        reaction.Send.Should().HaveCount(2);
        reaction.Send[1].Should().Be("""{"type":"dspcontrol","action":"start"}""");
    }

    [Fact]
    public void A_Profile_The_Receiver_Is_Already_On_Is_Not_Asked_For()
    {
        var conversation = new OpenWebRxConversation(Usb with { Profile = "rtl|40m" });

        OpenWebRxReaction reaction = conversation.OnText(
            Config(centre: 7_100_000, rate: 2_400_000, sdr: "rtl", profile: "40m"));

        reaction.Send.Should().HaveCount(2);
        reaction.Send.Should().NotContain(m => m.Contains("selectprofile", StringComparison.Ordinal));
    }

    [Fact]
    public void Somebody_Else_Moving_The_Receiver_Away_Is_Never_Fought()
    {
        var conversation = new OpenWebRxConversation(Usb with { Profile = "rtl|40m" });
        conversation.OnText(Config(centre: 7_100_000, rate: 2_400_000, sdr: "rtl", profile: "40m"));

        OpenWebRxReaction moved = conversation.OnText(Config(centre: 145_000_000, profile: "2m"));

        moved.Send.Should().BeEmpty("profiles are everybody's, and this session has had its one ask");
        conversation.OutOfBand.Should().BeTrue();
    }

    [Fact]
    public void A_Profile_The_Receiver_Does_Not_Offer_Is_A_Fault_That_Lists_What_It_Does()
    {
        var conversation = new OpenWebRxConversation(Usb with { Profile = "80m" });
        conversation.OnText(Config(centre: 145_000_000, rate: 2_400_000, sdr: "rtl", profile: "2m"));

        conversation.OnText(Profiles).Send.Should().BeEmpty();

        conversation.Fault.Should().Be(
            "the receiver offers no profile \"80m\". It offers \"40m\" (rtl|40m), \"2m APRS\" (rtl|2m).");
    }

    [Fact]
    public void A_Retune_Moves_The_Demodulator_Without_Starting_It_Again()
    {
        var conversation = new OpenWebRxConversation(Usb);
        conversation.OnText(Config(centre: 7_100_000, rate: 2_400_000));

        OpenWebRxReaction reaction = conversation.Retune(Usb with { FrequencyHz = 7_101_000 });

        reaction.Send.Should().ContainSingle();
        Json(reaction.Send[0])["params"]!["offset_freq"]!.GetValue<long>().Should().Be(1_000);
        conversation.Retune(Usb with { FrequencyHz = 7_101_000 }).Send.Should().BeEmpty("nothing moved");
    }

    [Fact]
    public void A_Retune_Off_The_Band_Says_So_And_Sends_Nothing()
    {
        var conversation = new OpenWebRxConversation(Usb);
        conversation.OnText(Config(centre: 7_100_000, rate: 2_400_000));

        OpenWebRxReaction reaction = conversation.Retune(Usb with { FrequencyHz = 10_140_000 });

        reaction.Send.Should().BeEmpty();
        conversation.OutOfBand.Should().BeTrue();
        reaction.Lines.Should().ContainSingle().Which.Should().Contain("does not reach 10.140000 MHz");
    }

    [Fact]
    public void A_Busy_Receiver_And_A_Broken_One_Are_Told_Apart()
    {
        var busy = new OpenWebRxConversation(Usb);
        busy.OnText("""{"type":"backoff","reason":"Too many clients"}""");
        busy.Refusal.Should().Be("Too many clients");
        busy.Fault.Should().BeNull();

        var broken = new OpenWebRxConversation(Usb);
        broken.OnText("""{"type":"sdr_error","value":"No SDR Devices available"}""");
        broken.Fault.Should().Be("the receiver reports no working SDR (No SDR Devices available)");
        broken.Refusal.Should().BeNull();
    }

    [Fact]
    public void The_Receivers_Own_Words_Reach_The_Journal_As_Ascii()
    {
        var conversation = new OpenWebRxConversation(Usb);

        conversation.OnText(
            """{"type":"receiver_details","value":{"receiver_name":"Schöne SDR","receiver_location":"Berlin","receiver_asl":40}}""");
        conversation.Description.Should().Be("Sch?ne SDR, Berlin");

        conversation.OnText("""{"type":"log_message","value":"This profile is locked, keeping current profile."}""")
            .Lines.Should().Equal("the receiver says: This profile is locked, keeping current profile.");
    }

    [Theory]
    [InlineData("""{"type":"smeter","value":-93.5}""")]
    [InlineData("""{"type":"features","value":{}}""")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    public void Everything_Else_The_Receiver_Says_Is_Read_Past(string text)
    {
        var conversation = new OpenWebRxConversation(Usb);

        conversation.OnText(text).Should().Be(OpenWebRxReaction.None);
    }
}
