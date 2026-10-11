using System.Net;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Updates;

/// <summary>What the last check that worked found.</summary>
internal sealed record UpdateFinding(string Latest, bool Newer, DateTimeOffset CheckedAt, bool FromAptRepo);

/// <summary>
/// Looks in packet-net's apt repository for a newer pdn-soundmodem (issue #583): once soon after
/// start-up, then every <see cref="Every"/>. One small GET of the repository's <c>Packages</c>
/// index; nothing is downloaded and nothing is installed.
/// </summary>
/// <remarks>
/// <para>Tom's decisions on #583: always on, no setting to turn it off; one plain sentence for
/// every release, with no flag for one that matters more than another; and a check that cannot
/// reach the repository (an offline station, most often) stays quiet rather than logging a
/// failure - it tries again at the next interval and nothing in between says anything went
/// wrong.</para>
/// <para>Unlike a one-shot announcement, a line is written on <b>every</b> check that finds a
/// newer version, not only the first: an operator who has left the old version running for a
/// week is reminded every day, not told once and then left to scroll back for it.</para>
/// </remarks>
public sealed class UpdateCheck : IDisposable
{
    /// <summary>The package this is.</summary>
    internal const string Package = "pdn-soundmodem";

    /// <summary>The repository's index.</summary>
    public const string PackagesUrl = "https://packet-net.github.io/apt/Packages";

    /// <summary>Where each release's notes are.</summary>
    public const string ReleasesUrl = "https://github.com/packet-net/pdn-soundmodem/releases";

    /// <summary>
    /// Where the apt repository is set up, for a station that was not installed from it: reading
    /// this and running the two lines there makes every later upgrade the one command below.
    /// </summary>
    public const string SetupDocsUrl =
        "https://github.com/packet-net/pdn-soundmodem/blob/main/docs/01-install.md#add-the-repository";

    /// <summary>The command that upgrades a station installed from the apt repository.</summary>
    public const string AptCommand = "sudo apt update && sudo apt install --only-upgrade " + Package;

    /// <summary>How often it looks, once the first look (after <see cref="FirstDelay"/>) is done.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromDays(1);

    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly Func<CancellationToken, Task<string>> _fetch;
    private readonly Func<bool> _fromAptRepo;
    private ITimer? _timer;
    private volatile UpdateFinding? _found;

    /// <summary>
    /// A check for this machine. <paramref name="fetch"/> and <paramref name="fromAptRepo"/> are
    /// for tests; production uses <see cref="UpdateCheck(TimeProvider, Action{string})"/>, which
    /// reads the real repository and the real machine.
    /// </summary>
    internal UpdateCheck(
        TimeProvider time,
        Action<string> log,
        string current,
        string? architecture,
        Func<CancellationToken, Task<string>>? fetch = null,
        Func<bool>? fromAptRepo = null)
    {
        _time = time;
        _log = log;
        Current = current;
        Architecture = architecture;
        _fetch = fetch ?? FetchAsync;
        _fromAptRepo = fromAptRepo ?? AptSources.ThisMachine;
    }

    /// <summary>A check for this station, on this machine, against the real repository.</summary>
    public UpdateCheck(TimeProvider time, Action<string> log)
        : this(time, log, DaemonVersion.Version, AptPackages.ThisArchitecture())
    {
    }

    // The index is small (tens of packages, well under 100 kB as of 2026-10) and this GETs it
    // once a day; a shared client with a bounded timeout and response size is plenty, the same
    // caution UberSdrDirectory's own client takes for a document it does not control.
    private static readonly HttpClient Http = new(
        new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromSeconds(20),
        MaxResponseContentBufferSize = 8 * 1024 * 1024,
    };

    /// <summary>This station's version, without a local build's <c>+commit</c> suffix.</summary>
    public string Current { get; }

    /// <summary>Debian's name for this machine's architecture, or null when packet-net builds none for it (then nothing is checked).</summary>
    public string? Architecture { get; }

    /// <summary>
    /// How long after <see cref="Start"/> the first look is, so that a fleet of stations started
    /// together (a reboot, a provisioning script) does not all ask at once. 20 s to 2 min by
    /// default; settable for tests.
    /// </summary>
    internal TimeSpan FirstDelay { get; init; } = TimeSpan.FromSeconds(Random.Shared.Next(20, 121));

    /// <summary>Raised after each look, whether it found anything newer or not, for tests.</summary>
    internal event Action? Checked;

    /// <summary>What the last look that worked found, or null before one has, or if every look so far has failed.</summary>
    internal UpdateFinding? Found => _found;

    /// <summary>The User-Agent it sends: the package and its version, and where it comes from.</summary>
    internal string UserAgent => $"{Package}/{Current} (+https://github.com/packet-net/pdn-soundmodem)";

    /// <summary>
    /// Starts looking: once after <see cref="FirstDelay"/>, then every <see cref="Every"/>, until
    /// <paramref name="cancellation"/> is cancelled or this is disposed. A station whose
    /// architecture packet-net does not build for is never checked at all, which is quiet by
    /// construction rather than a repeated failure.
    /// </summary>
    public void Start(CancellationToken cancellation)
    {
        if (Architecture is null)
        {
            return;
        }

        _timer = _time.CreateTimer(_ => _ = CheckOnceAsync(cancellation), null, FirstDelay, Every);
    }

    /// <summary>One look. Never throws but for <paramref name="cancellation"/>, and never logs a failure.</summary>
    internal async Task CheckOnceAsync(CancellationToken cancellation)
    {
        if (Architecture is null)
        {
            // Nothing packet-net builds for this machine: there is nothing in the index that
            // could ever answer this station, so there is nothing to check. Quiet by
            // construction, the same as Start() never scheduling one.
            Checked?.Invoke();
            return;
        }

        try
        {
            string text = await _fetch(cancellation).ConfigureAwait(false);
            AptPackage? newest = AptPackages.Newest(AptPackages.Parse(text), Package, Architecture!);
            if (newest is not null)
            {
                bool newer = DebianVersion.Compare(newest.Version, Current) > 0;
                var finding = new UpdateFinding(newest.Version, newer, _time.GetUtcNow(), _fromAptRepo());
                _found = finding;
                if (newer)
                {
                    _log($"update: version {newest.Version} is available (you have {Current}); "
                        + (finding.FromAptRepo ? $"run: {AptCommand}" : $"see {SetupDocsUrl} for the apt repository")
                        + $" (notes: {ReleasesUrl}/tag/v{newest.Version})");
                }
            }

            // No build for this architecture in the index: nothing to compare against. Quiet,
            // like every other way this can fail to answer - it tries again at the next interval.
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // observe only: an offline station, a bad gateway, a malformed
        // index or anything else that goes wrong here is never an error (Tom, #583) - it tries
        // again at the next interval and says nothing about the attempt that failed.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        finally
        {
            Checked?.Invoke();
        }
    }

    /// <summary>The fetch for the real repository, with this station's User-Agent on it.</summary>
    private async Task<string> FetchAsync(CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, PackagesUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var response = await Http.SendAsync(request, cancellation).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException($"{AptSources.RepoHost} answered {(int)response.StatusCode}");
        }

        return await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// What the station page shows (issue #583): installed and available versions, a link to the
    /// release's notes, and the command to upgrade - or null everywhere once nothing is known yet
    /// or the installed version is current.
    /// </summary>
    internal UpdateView? View()
    {
        if (_found is not { Newer: true } f)
        {
            return null;
        }

        return new UpdateView(
            Current,
            f.Latest,
            f.CheckedAt,
            $"{ReleasesUrl}/tag/v{f.Latest}",
            f.FromAptRepo ? AptCommand : null,
            f.FromAptRepo ? null : SetupDocsUrl);
    }

    /// <summary>Stops <see cref="Start"/>.</summary>
    public void Dispose()
    {
        _timer?.Dispose();
    }
}

/// <summary>
/// What the station page shows for issue #583, carried over the config message and the
/// dedicated <c>update</c> broadcast (see <c>WaterfallWebServer.SetUpdateAvailable</c>).
/// Null fields are left out of the JSON.
/// </summary>
/// <param name="Installed">The version this station is running.</param>
/// <param name="Available">The newer version the apt repository offers.</param>
/// <param name="CheckedAt">When this was last confirmed against the repository.</param>
/// <param name="Release">A link to that release's notes.</param>
/// <param name="Command">The command that upgrades a station installed from the apt repository; null when it was not.</param>
/// <param name="SetupDocs">Where to read the apt repository setup, for a station that was not installed from it; null when it was.</param>
internal sealed record UpdateView(
    string Installed,
    string Available,
    DateTimeOffset CheckedAt,
    string Release,
    string? Command,
    string? SetupDocs);
