using System.Text;
using AwesomeAssertions;
using Packet.SoundModem.SdrConnect;

namespace Packet.SoundModem.Tests.SdrConnect;

/// <summary>The SDRconnect WebSocket API's wire format, byte for byte, and the device string.</summary>
public class SdrConnectProtocolTests
{
    [Fact]
    public void A_Set_Is_The_Envelope_The_Document_Shows()
    {
        string json = Encoding.UTF8.GetString(SdrConnectProtocol.Set("device_center_frequency", "101000000"));

        json.Should().Be("{\"event_type\":\"set_property\",\"property\":\"device_center_frequency\",\"value\":\"101000000\"}");
    }

    [Fact]
    public void A_Stream_Switch_Has_An_Empty_Property_And_A_Boolean_String()
    {
        Encoding.UTF8.GetString(SdrConnectProtocol.Enable("iq_stream_enable", true))
            .Should().Be("{\"event_type\":\"iq_stream_enable\",\"property\":\"\",\"value\":\"true\"}");
        Encoding.UTF8.GetString(SdrConnectProtocol.Get("api_version"))
            .Should().Be("{\"event_type\":\"get_property\",\"property\":\"api_version\",\"value\":\"\"}");
    }

    [Fact]
    public void A_Push_From_SDRconnect_Is_Read_With_Its_Device()
    {
        byte[] json = Encoding.UTF8.GetBytes(
            "{ \"event_type\": \"property_changed\", \"property\": \"device_center_frequency\", \"device\": \"primary\", \"value\": \"100000000\" }");

        SdrConnectProtocol.TryParse(json, out SdrConnectMessage message).Should().BeTrue();

        message.Should().Be(new SdrConnectMessage("property_changed", "device_center_frequency", "100000000", "primary"));
        message.IsPrimary.Should().BeTrue();
    }

    [Theory]
    [InlineData("{\"event_type\":\"get_property_response\",\"property\":\"device_sample_rate\",\"value\":2000000.0}", "2000000.0")]
    [InlineData("{\"event_type\":\"get_property_response\",\"property\":\"started\",\"value\":true}", "true")]
    [InlineData("{\"event_type\":\"get_property_response\",\"property\":\"x\",\"value\":{\"a\":[1]},\"extra\":1}", "")]
    public void A_Value_That_Is_Not_A_String_Is_Taken_As_Its_Text(string json, string value)
    {
        SdrConnectProtocol.TryParse(Encoding.UTF8.GetBytes(json), out SdrConnectMessage message).Should().BeTrue();

        message.Value.Should().Be(value);
        message.Device.Should().BeNull();
        message.IsPrimary.Should().BeTrue("an absent device is the primary");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"property\":\"x\",\"value\":\"y\"}")]
    public void Anything_That_Is_Not_An_Api_Message_Is_Not_Read(string json)
    {
        SdrConnectProtocol.TryParse(Encoding.UTF8.GetBytes(json), out _).Should().BeFalse();
    }

    [Fact]
    public void A_Secondary_Device_Message_Is_Not_The_Primary()
    {
        new SdrConnectMessage("property_changed", "p", "v", "secondary").IsPrimary.Should().BeFalse();
    }

    [Fact]
    public void Iq_Is_Signed_Sixteen_Bit_Little_Endian_I_Then_Q()
    {
        byte[] frames = [0x00, 0x40, 0x00, 0xC0, 0xFF, 0x7F, 0x00, 0x80];
        var output = new float[4];

        SdrConnectProtocol.ConvertIq(frames, output);

        output.Should().Equal(0.5f, -0.5f, 32767f / 32768, -1f);
    }

    [Fact]
    public void Audio_Keeps_The_Left_Channel_Of_Each_Stereo_Frame()
    {
        byte[] frames = [0x00, 0x40, 0x11, 0x11, 0x00, 0xC0, 0x22, 0x22];
        var output = new float[2];

        SdrConnectProtocol.ConvertLeft(frames, output);

        output.Should().Equal(0.5f, -0.5f);
    }

    [Theory]
    [InlineData("2000000", 2_000_000d)]
    [InlineData("2000000.0", 2_000_000d)]
    [InlineData("1e6", 1_000_000d)]
    [InlineData("", null)]
    [InlineData("fast", null)]
    public void A_Number_Is_Read_Whether_Or_Not_It_Has_A_Fraction(string text, double? expected)
    {
        SdrConnectProtocol.ParseNumber(text).Should().Be(expected);
    }

    [Fact]
    public void Text_From_SDRconnect_Is_Made_Printable()
    {
        SdrConnectProtocol.Printable("RSP1A été\n").Should().Be("RSP1A ?t??");
        SdrConnectProtocol.Printable("Antenna A").Should().Be("Antenna A");
    }

    [Theory]
    [InlineData("sdrconnect:shack-pc", "shack-pc", 5454)]
    [InlineData("SDRCONNECT:192.168.1.20:5455", "192.168.1.20", 5455)]
    [InlineData("sdrconnect:[fd00::5]:5454", "fd00::5", 5454)]
    [InlineData("sdrconnect:[fd00::5]", "fd00::5", 5454)]
    public void A_Device_String_Names_A_Host_And_A_Port(string device, string host, int port)
    {
        SdrConnectDevice.IsSdrConnect(device).Should().BeTrue();
        SdrConnectDevice.Parse(device).Should().Be(new SdrConnectEndpoint(host, port));
    }

    [Theory]
    [InlineData("sdrconnect:", "*names no SDRconnect*")]
    [InlineData("sdrconnect:host:port", "*not a host and a TCP port*")]
    [InlineData("sdrconnect:host:70000", "*not a host and a TCP port*")]
    [InlineData("sdrconnect:fd00::5", "*IPv6 address in brackets*")]
    [InlineData("sdrconnect:ws://host/", "*not a host and a TCP port*")]
    public void A_Malformed_Device_String_Is_Refused_In_Words(string device, string message)
    {
        Action parse = () => SdrConnectDevice.Parse(device);

        parse.Should().Throw<InvalidDataException>().WithMessage(message);
    }

    [Fact]
    public void The_WebSocket_Is_At_The_Root_Of_The_Port()
    {
        new SdrConnectEndpoint("shack-pc", 5454).WebSocketUri.Should().Be(new Uri("ws://shack-pc:5454/"));
        new SdrConnectEndpoint("fd00::5", 5454).WebSocketUri.Should().Be(new Uri("ws://[fd00::5]:5454/"));
    }
}
