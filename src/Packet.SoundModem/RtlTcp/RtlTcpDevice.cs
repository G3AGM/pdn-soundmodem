namespace Packet.SoundModem.RtlTcp;

/// <summary>An rtl_tcp server's address.</summary>
/// <param name="Host">Hostname or address (an IPv6 address without its brackets).</param>
/// <param name="Port">TCP port.</param>
public readonly record struct RtlTcpEndpoint(string Host, int Port)
{
    /// <summary>The server as an operator would write it.</summary>
    public override string ToString() =>
        Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
}

/// <summary>Parses the <c>--device rtltcp:HOST[:PORT]</c> device string.</summary>
/// <remarks>
/// A bare host takes rtl_tcp's own port, 1234. An IPv6 address goes in brackets, as in a URL:
/// <c>rtltcp:[fd00::5]:1234</c>.
/// </remarks>
public static class RtlTcpDevice
{
    private const string Prefix = "rtltcp:";

    /// <summary>True when <paramref name="device"/> selects an rtl_tcp server.</summary>
    public static bool IsRtlTcp(string device) =>
        device.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Splits an <c>rtltcp:</c> device string into its endpoint.</summary>
    /// <exception cref="ArgumentException">The string is not an <c>rtltcp:</c> device at all.</exception>
    /// <exception cref="InvalidDataException">It is, but names no server, in words for the
    /// operator.</exception>
    public static RtlTcpEndpoint Parse(string device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!IsRtlTcp(device))
        {
            throw new ArgumentException($"'{device}' is not an rtltcp: device string", nameof(device));
        }

        string rest = device[Prefix.Length..].Trim();
        string Bad() =>
            $"\"device\": \"{device}\" - '{rest}' is not a host and a TCP port. Write the rtl_tcp "
            + $"server as host or host:port (rtl_tcp's own port, {RtlTcpProtocol.DefaultPort}, is the "
            + "default), and an IPv6 address in brackets, e.g. \"rtltcp:[fd00::5]:1234\".";
        if (rest.Length == 0)
        {
            throw new InvalidDataException(
                "\"device\": \"rtltcp:\" names no server. Give the host the rtl_tcp server runs on, "
                + "e.g. \"rtltcp:pi.local\" or \"rtltcp:192.168.1.20:1234\".");
        }

        if (rest.Contains('/', StringComparison.Ordinal) || rest.Contains("://", StringComparison.Ordinal))
        {
            throw new InvalidDataException(Bad());
        }

        string host;
        string? port = null;
        if (rest.StartsWith('['))
        {
            int close = rest.IndexOf(']', StringComparison.Ordinal);
            if (close < 2)
            {
                throw new InvalidDataException(Bad());
            }

            host = rest[1..close];
            string after = rest[(close + 1)..];
            if (after.Length > 0)
            {
                if (after[0] != ':')
                {
                    throw new InvalidDataException(Bad());
                }

                port = after[1..];
            }
        }
        else
        {
            int colon = rest.IndexOf(':', StringComparison.Ordinal);
            if (colon >= 0 && rest.IndexOf(':', colon + 1) >= 0)
            {
                // More than one colon and no brackets: an IPv6 address that cannot carry a port.
                throw new InvalidDataException(Bad());
            }

            host = colon < 0 ? rest : rest[..colon];
            port = colon < 0 ? null : rest[(colon + 1)..];
        }

        if (host.Length == 0)
        {
            throw new InvalidDataException(Bad());
        }

        int number = RtlTcpProtocol.DefaultPort;
        if (port is not null && (!int.TryParse(port, out number) || number is < 1 or > 65535))
        {
            throw new InvalidDataException(Bad());
        }

        return new RtlTcpEndpoint(host, number);
    }
}
