using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Updates;

namespace Packet.SoundModem.Tests.Updates;

/// <summary>
/// The apt repository update check (issue #583): one journal line for every check that finds a
/// newer version, nothing at all when it cannot reach the repository or the index makes no
/// sense, and what the station page is told.
/// </summary>
public class UpdateCheckTests
{
    private const string Current = "0.88.0";

    private static string Index(string version, string architecture = "amd64", string package = "pdn-soundmodem") =>
        $"""
        Package: {package}
        Version: {version}
        Architecture: {architecture}
        Filename: pool/{package}_{version}_{architecture}.deb

        """;

    private sealed class Harness
    {
        private readonly List<string> _lines = [];
        private Func<CancellationToken, Task<string>> _fetch;
        private bool _fromAptRepo = true;

        internal Harness()
        {
            _fetch = _ => Task.FromResult(Index(Current));
            Time = new FakeTimeProvider();
        }

        internal FakeTimeProvider Time { get; }

        internal IReadOnlyList<string> Lines => _lines;

        internal void Answers(string body) => _fetch = _ => Task.FromResult(body);

        internal void Fails(Exception e) => _fetch = _ => Task.FromException<string>(e);

        internal void NotFromAptRepo() => _fromAptRepo = false;

        internal UpdateCheck Check => _check ??= new UpdateCheck(
            Time, _lines.Add, Current, "amd64", token => _fetch(token), () => _fromAptRepo);

        private UpdateCheck? _check;

        internal Task RunOnceAsync() => Check.CheckOnceAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_Newer_Version_In_The_Index_Logs_One_Plain_Line()
    {
        var h = new Harness();
        h.Answers(Index("0.89.0"));

        await h.RunOnceAsync();

        h.Lines.Should().ContainSingle();
        string line = h.Lines[0];
        line.Should().Contain("0.89.0").And.Contain("0.88.0")
            .And.NotContain("ERROR", "a newer version is news, not a fault");
        line.Should().MatchRegex("^[\\x20-\\x7e]*$", "journalctl's pager renders anything above ASCII as hex escapes");
    }

    [Fact]
    public async Task The_Same_Version_In_The_Index_Logs_Nothing()
    {
        var h = new Harness();
        h.Answers(Index(Current));

        await h.RunOnceAsync();

        h.Lines.Should().BeEmpty();
        h.Check.View().Should().BeNull();
    }

    [Fact]
    public async Task An_Older_Version_In_The_Index_Logs_Nothing()
    {
        var h = new Harness();
        h.Answers(Index("0.80.0"));

        await h.RunOnceAsync();

        h.Lines.Should().BeEmpty();
        h.Check.View().Should().BeNull();
    }

    [Fact]
    public async Task An_Offline_Station_Logs_Nothing_And_Is_Never_An_Error()
    {
        var h = new Harness();
        h.Fails(new HttpRequestException("Name or service not known"));

        await h.RunOnceAsync();

        h.Lines.Should().BeEmpty();
        h.Check.View().Should().BeNull();
    }

    [Fact]
    public async Task A_Timeout_Reaching_The_Repository_Logs_Nothing()
    {
        var h = new Harness();
        h.Fails(new TaskCanceledException("the operation was cancelled"));

        await h.RunOnceAsync();

        h.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task A_Malformed_Index_Logs_Nothing()
    {
        var h = new Harness();
        h.Answers("<html>not found</html>");

        await h.RunOnceAsync();

        h.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task An_Index_With_No_Build_For_This_Architecture_Logs_Nothing()
    {
        var h = new Harness();
        h.Answers(Index("0.89.0", architecture: "arm64"));

        await h.RunOnceAsync();

        h.Lines.Should().BeEmpty();
        h.Check.View().Should().BeNull();
    }

    [Fact]
    public async Task A_Station_Packet_Net_Does_Not_Build_For_Is_Never_Checked()
    {
        var lines = new List<string>();
        var fetched = false;
        var check = new UpdateCheck(
            new FakeTimeProvider(), lines.Add, Current, null,
            token =>
            {
                fetched = true;
                return Task.FromResult(Index("0.89.0"));
            });

        await check.CheckOnceAsync(CancellationToken.None);

        fetched.Should().BeFalse("there is no package in the index that could ever answer this machine");
        lines.Should().BeEmpty();
    }

    [Fact]
    public async Task Every_Check_That_Still_Finds_A_Newer_Version_Logs_Again()
    {
        var h = new Harness();
        h.Answers(Index("0.89.0"));

        await h.RunOnceAsync();
        await h.RunOnceAsync();
        await h.RunOnceAsync();

        h.Lines.Should().HaveCount(3,
            "an operator who left the old version running for days is reminded every check, not told once");
        h.Lines.Should().OnlyContain(l => l == h.Lines[0]);
    }

    [Fact]
    public async Task Installed_From_The_Apt_Repo_Gets_The_Apt_Upgrade_Command()
    {
        var h = new Harness();
        h.Answers(Index("0.89.0"));

        await h.RunOnceAsync();

        h.Lines[0].Should().Contain("sudo apt update && sudo apt install --only-upgrade pdn-soundmodem");
        var view = h.Check.View();
        view.Should().NotBeNull();
        view!.Command.Should().Be("sudo apt update && sudo apt install --only-upgrade pdn-soundmodem");
        view.SetupDocs.Should().BeNull();
    }

    [Fact]
    public async Task Not_Installed_From_The_Apt_Repo_Is_Pointed_At_The_Docs_Instead()
    {
        var h = new Harness();
        h.NotFromAptRepo();
        h.Answers(Index("0.89.0"));

        await h.RunOnceAsync();

        h.Lines[0].Should().NotContain("apt install --only-upgrade",
            "a station that did not come from the repository cannot upgrade with that command");
        h.Lines[0].Should().Contain("docs/01-install.md");
        var view = h.Check.View();
        view.Should().NotBeNull();
        view!.Command.Should().BeNull();
        view.SetupDocs.Should().Contain("docs/01-install.md");
    }

    [Fact]
    public async Task The_View_Carries_A_Link_To_The_Releases_Notes()
    {
        var h = new Harness();
        h.Answers(Index("0.89.0"));

        await h.RunOnceAsync();

        h.Check.View()!.Release.Should().Be("https://github.com/packet-net/pdn-soundmodem/releases/tag/v0.89.0");
    }

    [Fact]
    public void It_Checks_Soon_After_Start_Then_Once_A_Day()
    {
        var h = new Harness();
        h.Answers(Index("0.89.0"));
        var check = h.Check;
        int checks = 0;
        check.Checked += () => checks++;

        check.Start(CancellationToken.None);
        checks.Should().Be(0, "nothing happens before the first delay elapses");

        h.Time.Advance(check.FirstDelay);
        checks.Should().Be(1, "the first look, soon after start-up");
        h.Lines.Should().ContainSingle();

        h.Time.Advance(UpdateCheck.Every - TimeSpan.FromSeconds(1));
        checks.Should().Be(1, "not due yet");

        h.Time.Advance(TimeSpan.FromSeconds(1));
        checks.Should().Be(2, "a day after the first look");
        h.Lines.Should().HaveCount(2);

        check.Dispose();
    }

    [Fact]
    public void Disposing_Stops_Further_Checks()
    {
        var h = new Harness();
        h.Answers(Index("0.89.0"));
        var check = h.Check;
        int checks = 0;
        check.Checked += () => checks++;
        check.Start(CancellationToken.None);
        h.Time.Advance(check.FirstDelay);
        checks.Should().Be(1);

        check.Dispose();
        h.Time.Advance(UpdateCheck.Every);

        checks.Should().Be(1, "the timer was disposed before the next interval");
    }
}
