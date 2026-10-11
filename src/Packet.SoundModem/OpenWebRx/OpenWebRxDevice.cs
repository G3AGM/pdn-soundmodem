namespace Packet.SoundModem.OpenWebRx;

/// <summary>One OpenWebRX receiver's address: where its page is, and so where its WebSocket is.</summary>
/// <param name="Host">Hostname or address.</param>
/// <param name="Port">TCP port: 8073 is OpenWebRX's own, 443 or 80 behind a web server.</param>
/// <param name="Ssl">True for <c>https</c>/<c>wss</c>.</param>
/// <param name="Path">The path the receiver's page is served under, starting and ending with
/// <c>/</c>: <c>/</c> for one at the root, longer behind a reverse proxy.</param>
public readonly record struct OpenWebRxEndpoint(string Host, int Port, bool Ssl, string Path = "/")
{
    /// <summary>The receiver's WebSocket, which the browser client finds as <c>ws/</c> beside its
    /// page.</summary>
    public Uri WebSocketUri => new($"{(Ssl ? "wss" : "ws")}://{HostAndPort}{Path}ws/");

    /// <summary>The receiver's own page, as a link for a visitor.</summary>
    public string PublicUrl => $"{(Ssl ? "https" : "http")}://{HostAndPort}{Path}";

    private string HostAndPort => Port == (Ssl ? 443 : 80) ? Host : $"{Host}:{Port}";

    /// <summary>The receiver as an operator would write it, with the default port and an empty
    /// path left off.</summary>
    public override string ToString() => Path == "/" ? HostAndPort : HostAndPort + Path.TrimEnd('/');
}

/// <summary>
/// Parses the <c>--device openwebrx:&lt;receiver&gt;</c> device string.
/// </summary>
/// <remarks>
/// <para>The receiver may be written as the URL out of a browser's address bar
/// (<c>openwebrx:https://sdr.example.org/</c>, <c>openwebrx:http://sdr.example.org:8073/</c>),
/// which is what is to hand and the only form that can carry a path; or as a bare host or
/// <c>host:port</c>, taken as plain HTTP on OpenWebRX's own port 8073 unless a port says
/// otherwise, because that is how the software installs.</para>
/// <para>A fragment or a query, which the browser adds when a frequency is picked
/// (<c>#freq=7050000</c>), is ignored: where to listen comes from the band plan.</para>
/// </remarks>
public static class OpenWebRxDevice
{
    /// <summary>OpenWebRX's own HTTP port.</summary>
    public const int DefaultPort = 8073;

    private const string Prefix = "openwebrx:";

    /// <summary>True when <paramref name="device"/> selects an OpenWebRX receiver.</summary>
    public static bool IsOpenWebRx(string device) =>
        device.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Splits an <c>openwebrx:</c> device string into its endpoint.</summary>
    /// <exception cref="ArgumentException">The string is not an <c>openwebrx:</c> device at all.</exception>
    /// <exception cref="InvalidDataException">It is, but names no receiver - an operator's typo,
    /// phrased for them to act on.</exception>
    public static OpenWebRxEndpoint Parse(string device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!IsOpenWebRx(device))
        {
            throw new ArgumentException($"'{device}' is not an openwebrx: device string", nameof(device));
        }

        string rest = device[Prefix.Length..].Trim();
        if (rest.Length == 0)
        {
            throw new InvalidDataException(
                "\"device\": \"openwebrx:\" names no receiver. Give it the URL you would open in a "
                + "browser, e.g. \"openwebrx:http://sdr.example.org:8073/\".");
        }

        if (rest.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(rest, UriKind.Absolute, out Uri? url)
                || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
                || url.Host.Length == 0)
            {
                throw new InvalidDataException(
                    $"\"device\": \"{device}\" - '{rest}' is not an http:// or https:// URL. Give the "
                    + "URL you would open in a browser to listen to the receiver.");
            }

            // The browser client puts its WebSocket beside its page, so a page at /sdr/index.html
            // has its socket at /sdr/ws/: everything up to the last slash.
            string path = url.AbsolutePath;
            path = path[..(path.LastIndexOf('/') + 1)];
            return new OpenWebRxEndpoint(url.Host, url.Port, url.Scheme == Uri.UriSchemeHttps, path);
        }

        if (rest.Contains('/', StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"\"device\": \"{device}\" - a receiver with a path needs its whole URL, scheme and "
                + "all, e.g. \"openwebrx:https://sdr.example.org/owrx/\".");
        }

        int colon = rest.LastIndexOf(':');
        if (colon < 0)
        {
            return new OpenWebRxEndpoint(rest, DefaultPort, Ssl: false);
        }

        string host = rest[..colon];
        if (host.Length == 0
            || !int.TryParse(rest[(colon + 1)..], out int port) || port is < 1 or > 65535)
        {
            throw new InvalidDataException(
                $"\"device\": \"{device}\" - '{rest}' is not a host and a TCP port. Write the "
                + "receiver as host, host:port, or the URL you would open in a browser.");
        }

        return new OpenWebRxEndpoint(host, port, Ssl: port == 443);
    }
}
