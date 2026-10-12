namespace Packet.SoundModem.SdrConnect;

/// <summary>An SDRconnect instance's WebSocket API address.</summary>
/// <param name="Host">Hostname or address (an IPv6 address without its brackets).</param>
/// <param name="Port">TCP port.</param>
public readonly record struct SdrConnectEndpoint(string Host, int Port)
{
    /// <summary>The address as an operator would write it.</summary>
    public override string ToString() =>
        Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]:{Port}" : $"{Host}:{Port}";

    /// <summary>The WebSocket to connect to. The API document names a port and no path, so the
    /// root is asked for.</summary>
    public Uri WebSocketUri => new($"ws://{this}/");
}

/// <summary>Parses the <c>--device sdrconnect:HOST[:PORT]</c> device string.</summary>
/// <remarks>
/// A bare host takes SDRconnect's own WebSocket port, 5454. An IPv6 address goes in brackets, as
/// in a URL: <c>sdrconnect:[fd00::5]:5454</c>.
/// </remarks>
public static class SdrConnectDevice
{
    private const string Prefix = "sdrconnect:";

    /// <summary>True when <paramref name="device"/> selects an SDRconnect instance.</summary>
    public static bool IsSdrConnect(string device) =>
        device.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Splits an <c>sdrconnect:</c> device string into its endpoint.</summary>
    /// <exception cref="ArgumentException">The string is not an <c>sdrconnect:</c> device at all.</exception>
    /// <exception cref="InvalidDataException">It is, but names no SDRconnect, in words for the
    /// operator.</exception>
    public static SdrConnectEndpoint Parse(string device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!IsSdrConnect(device))
        {
            throw new ArgumentException($"'{device}' is not an sdrconnect: device string", nameof(device));
        }

        string rest = device[Prefix.Length..].Trim();
        string Bad() =>
            $"\"device\": \"{device}\" - '{rest}' is not a host and a TCP port. Write the PC running "
            + $"SDRconnect as host or host:port (SDRconnect's WebSocket port, {SdrConnectProtocol.DefaultPort}, "
            + "is the default), and an IPv6 address in brackets, e.g. \"sdrconnect:[fd00::5]:5454\".";
        if (rest.Length == 0)
        {
            throw new InvalidDataException(
                "\"device\": \"sdrconnect:\" names no SDRconnect. Give the host SDRconnect runs on, "
                + "e.g. \"sdrconnect:shack-pc.local\" or \"sdrconnect:192.168.1.20:5454\".");
        }

        if (rest.Contains('/', StringComparison.Ordinal))
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

        int number = SdrConnectProtocol.DefaultPort;
        if (port is not null && (!int.TryParse(port, out number) || number is < 1 or > 65535))
        {
            throw new InvalidDataException(Bad());
        }

        return new SdrConnectEndpoint(host, number);
    }
}
