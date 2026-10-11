using AwesomeAssertions;
using Packet.SoundModem.OpenWebRx;

namespace Packet.SoundModem.Tests.OpenWebRx;

/// <summary>The <c>openwebrx:</c> device string, written however the operator has the address.</summary>
public class OpenWebRxDeviceTests
{
    [Theory]
    [InlineData("openwebrx:http://sdr.example.org:8073/", "sdr.example.org", 8073, false, "/", "ws://sdr.example.org:8073/ws/")]
    [InlineData("openwebrx:https://sdr.example.org/", "sdr.example.org", 443, true, "/", "wss://sdr.example.org/ws/")]
    [InlineData("openwebrx:https://sdr.example.org", "sdr.example.org", 443, true, "/", "wss://sdr.example.org/ws/")]
    [InlineData("OpenWebRX:https://example.org/owrx/", "example.org", 443, true, "/owrx/", "wss://example.org/owrx/ws/")]
    [InlineData("openwebrx:https://example.org/owrx/index.html", "example.org", 443, true, "/owrx/", "wss://example.org/owrx/ws/")]
    [InlineData("openwebrx:http://sdr.example.org:8073/#freq=7050000,mod=usb", "sdr.example.org", 8073, false, "/", "ws://sdr.example.org:8073/ws/")]
    [InlineData("openwebrx:sdr.example.org", "sdr.example.org", 8073, false, "/", "ws://sdr.example.org:8073/ws/")]
    [InlineData("openwebrx:sdr.example.org:8080", "sdr.example.org", 8080, false, "/", "ws://sdr.example.org:8080/ws/")]
    [InlineData("openwebrx:sdr.example.org:443", "sdr.example.org", 443, true, "/", "wss://sdr.example.org/ws/")]
    public void Every_Way_Of_Writing_A_Receiver_Names_Its_WebSocket(
        string device, string host, int port, bool ssl, string path, string webSocket)
    {
        OpenWebRxDevice.IsOpenWebRx(device).Should().BeTrue();
        OpenWebRxEndpoint endpoint = OpenWebRxDevice.Parse(device);

        endpoint.Should().Be(new OpenWebRxEndpoint(host, port, ssl, path));
        endpoint.WebSocketUri.ToString().Should().Be(webSocket);
    }

    [Theory]
    [InlineData("openwebrx:", "*names no receiver*")]
    [InlineData("openwebrx:ftp://sdr.example.org/", "*not an http:// or https:// URL*")]
    [InlineData("openwebrx:sdr.example.org/owrx/", "*needs its whole URL*")]
    [InlineData("openwebrx:sdr.example.org:http", "*not a host and a TCP port*")]
    [InlineData("openwebrx::8073", "*not a host and a TCP port*")]
    public void A_String_That_Names_No_Receiver_Is_Refused_In_Words(string device, string message)
    {
        Action parse = () => OpenWebRxDevice.Parse(device);

        parse.Should().Throw<InvalidDataException>().WithMessage(message);
    }

    [Fact]
    public void The_Receiver_Reads_Back_The_Way_It_Was_Written()
    {
        OpenWebRxDevice.Parse("openwebrx:https://example.org/owrx/").ToString().Should().Be("example.org/owrx");
        OpenWebRxDevice.Parse("openwebrx:sdr.example.org").ToString().Should().Be("sdr.example.org:8073");
        OpenWebRxDevice.Parse("openwebrx:https://example.org/owrx/").PublicUrl.Should().Be("https://example.org/owrx/");
    }
}
