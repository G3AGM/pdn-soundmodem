using AwesomeAssertions;
using Packet.SoundModem.RtlTcp;

namespace Packet.SoundModem.Tests.RtlTcp;

/// <summary>The rtl_tcp wire format, byte for byte, and the <c>rtltcp:</c> device string.</summary>
public class RtlTcpProtocolTests
{
    [Fact]
    public void A_Dongle_Header_Gives_Its_Tuner_And_Gain_Steps()
    {
        // "RTL0", tuner 5 (R820T), 29 gain steps, big-endian: what an R820T dongle's rtl_tcp sends.
        byte[] header = [0x52, 0x54, 0x4C, 0x30, 0, 0, 0, 5, 0, 0, 0, 29];

        RtlTcpHeader? parsed = RtlTcpProtocol.ParseHeader(header);

        parsed.Should().Be(new RtlTcpHeader(RtlTcpTuner.R820T, 29));
        parsed!.Value.TunerName.Should().Be("R820T");
    }

    [Fact]
    public void Bytes_That_Do_Not_Start_With_The_Magic_Are_No_Header()
    {
        byte[] samples = [127, 128, 126, 129, 127, 128, 126, 129, 127, 128, 126, 129];

        RtlTcpProtocol.ParseHeader(samples).Should().BeNull();
        RtlTcpProtocol.ParseHeader("RTL0"u8).Should().BeNull("four bytes are not a whole header");
    }

    [Fact]
    public void A_Header_Written_Reads_Back()
    {
        var bytes = new byte[RtlTcpProtocol.HeaderLength];
        RtlTcpProtocol.WriteHeader(bytes, new RtlTcpHeader(RtlTcpTuner.E4000, 14));

        RtlTcpProtocol.ParseHeader(bytes).Should().Be(new RtlTcpHeader(RtlTcpTuner.E4000, 14));
    }

    [Theory]
    [InlineData(RtlTcpCommand.SetFrequency, 144_800_000u, new byte[] { 0x01, 0x08, 0xA1, 0x79, 0x00 })]
    [InlineData(RtlTcpCommand.SetSampleRate, 1_024_000u, new byte[] { 0x02, 0x00, 0x0F, 0xA0, 0x00 })]
    [InlineData(RtlTcpCommand.SetGainMode, 1u, new byte[] { 0x03, 0x00, 0x00, 0x00, 0x01 })]
    [InlineData(RtlTcpCommand.SetGain, 296u, new byte[] { 0x04, 0x00, 0x00, 0x01, 0x28 })]
    [InlineData(RtlTcpCommand.SetAgcMode, 0u, new byte[] { 0x08, 0x00, 0x00, 0x00, 0x00 })]
    public void A_Command_Is_One_Byte_Of_Number_And_A_Big_Endian_Parameter(
        RtlTcpCommand command, uint parameter, byte[] expected)
    {
        var bytes = new byte[RtlTcpProtocol.CommandLength];
        RtlTcpProtocol.Encode(bytes, command, parameter);

        bytes.Should().Equal(expected);
        RtlTcpProtocol.Decode(bytes).Should().Be((command, parameter));
    }

    [Fact]
    public void A_Negative_Frequency_Correction_Goes_As_Twos_Complement()
    {
        var bytes = new byte[RtlTcpProtocol.CommandLength];
        RtlTcpProtocol.Encode(bytes, RtlTcpCommand.SetFrequencyCorrection, -12);

        bytes.Should().Equal(0x05, 0xFF, 0xFF, 0xFF, 0xF4);
    }

    [Fact]
    public void Unsigned_Samples_Are_Centred_On_127_And_A_Half()
    {
        var floats = new float[4];
        RtlTcpProtocol.Convert([0, 255, 127, 128], floats);

        floats[0].Should().Be(-1f);
        floats[1].Should().Be(1f);
        floats[2].Should().BeApproximately(-0.5f / 127.5f, 1e-7f);
        floats[3].Should().BeApproximately(0.5f / 127.5f, 1e-7f);
    }

    [Theory]
    [InlineData(225_000, false)]
    [InlineData(250_000, true)]
    [InlineData(300_000, true)]
    [InlineData(600_000, false)]
    [InlineData(1_024_000, true)]
    [InlineData(2_400_000, true)]
    [InlineData(3_200_000, true)]
    [InlineData(3_200_001, false)]
    public void Only_The_Rates_An_Rtl2832_Runs_At_Are_Dongle_Rates(int rate, bool dongle)
    {
        RtlTcpProtocol.IsDongleRate(rate).Should().Be(dongle);
    }

    [Theory]
    [InlineData("rtltcp:pi.local", "pi.local", 1234)]
    [InlineData("rtltcp:192.168.1.20:1234", "192.168.1.20", 1234)]
    [InlineData("RTLTCP:sdr:7373", "sdr", 7373)]
    [InlineData("rtltcp:[fd00::5]:1235", "fd00::5", 1235)]
    [InlineData("rtltcp:[fd00::5]", "fd00::5", 1234)]
    public void Every_Way_Of_Writing_A_Server_Names_Its_Host_And_Port(string device, string host, int port)
    {
        RtlTcpDevice.IsRtlTcp(device).Should().BeTrue();
        RtlTcpDevice.Parse(device).Should().Be(new RtlTcpEndpoint(host, port));
    }

    [Theory]
    [InlineData("rtltcp:", "*names no server*")]
    [InlineData("rtltcp:pi.local:0", "*not a host and a TCP port*")]
    [InlineData("rtltcp:pi.local:http", "*not a host and a TCP port*")]
    [InlineData("rtltcp:http://pi.local/", "*not a host and a TCP port*")]
    [InlineData("rtltcp:fd00::5", "*IPv6 address in brackets*")]
    [InlineData("rtltcp:[fd00::5]x", "*not a host and a TCP port*")]
    public void A_Malformed_Server_Is_Refused_In_Words_For_The_Operator(string device, string message)
    {
        Action parse = () => RtlTcpDevice.Parse(device);

        parse.Should().Throw<InvalidDataException>().WithMessage(message);
    }

    [Fact]
    public void An_IPv6_Server_Is_Written_Back_In_Brackets()
    {
        new RtlTcpEndpoint("fd00::5", 1234).ToString().Should().Be("[fd00::5]:1234");
        new RtlTcpEndpoint("pi.local", 1234).ToString().Should().Be("pi.local:1234");
    }
}
