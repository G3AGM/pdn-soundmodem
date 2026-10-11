using System.Diagnostics;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The usage text is the one description of the command line, so it has to name every flag the
/// parser accepts, and both ways of asking for it have to work from a shell.
/// </summary>
/// <remarks>
/// The flags are parsed in Program.cs's top-level statements, which no unit test can call, so
/// the first test reads the source the way <see cref="SourceTextTests"/> does and the last two
/// run the built daemon as a process. Both ways are short: the daemon prints and exits without
/// opening anything.
/// </remarks>
public class UsageTests
{
    [Fact]
    public void The_Usage_Text_Lists_Exactly_The_Flags_The_Parser_Accepts_In_The_Parser_Order()
    {
        string program = Path.Combine(
            FindRepoRoot(), "src", "Packet.SoundModem.Daemon", "Program.cs");
        string source = File.ReadAllText(program);

        // The argument switch is the only place in the file a case label is a "--" string.
        List<string> parsed = Regex.Matches(source, "case \"(--[a-z0-9-]+)\":")
            .Select(m => m.Groups[1].Value)
            .ToList();

        parsed.Should().HaveCountGreaterThan(
            20,
            "the argument switch in Program.cs is found by a regex over its case labels; if "
            + "that found almost nothing the switch changed shape, and the regex needs updating "
            + "rather than this test deleting");
        parsed.Should().OnlyHaveUniqueItems();

        // An option's line starts at column 2 with its flag. Continuation lines are indented
        // further, the synopsis lines start with the program name and the prose after the
        // options starts at column 0, so none of those match.
        List<string> described = Regex.Matches(Usage.Text, "\n  (--[a-z0-9-]+)")
            .Select(m => m.Groups[1].Value)
            .ToList();

        described.Should().Equal(
            parsed,
            "every flag Program.cs parses has to be described on a line of its own under "
            + "Options, no line may describe a flag the parser no longer has, and the usage "
            + "lists them in the order the parser's switch does");
    }

    [Fact]
    public void The_Usage_Text_Is_Plain_Ascii_And_Fits_An_80_Column_Terminal()
    {
        Usage.Text.Where(c => c > 127).Should().BeEmpty(
            "journalctl's pager renders non-ASCII as <XX> hex escapes under a C locale");

        Usage.Text.Split('\n').Where(line => line.Length > 80).Should().BeEmpty(
            "the usage is read in a terminal, and the mode list is wrapped for one");
    }

    [Fact]
    public void Help_Prints_The_Usage_And_Exits_0()
    {
        (int exitCode, string stdout, string stderr) = RunDaemon("--help");

        exitCode.Should().Be(0);
        stdout.Should().Contain(Usage.Text);
        stderr.Should().BeEmpty();
    }

    [Fact]
    public void No_Arguments_Prints_The_Usage_And_Exits_2()
    {
        (int exitCode, string stdout, string stderr) = RunDaemon();

        exitCode.Should().Be(
            Usage.NoArgumentsExitCode,
            "a bare command line used to start a station on the default sound card; now it "
            + "starts nothing, and says so with the code an unknown option gets");
        stderr.Should().Contain(Usage.Text);
        stdout.Should().BeEmpty();
    }

    /// <summary>
    /// Runs the daemon built beside these tests (the project reference copies pdn-soundmodem.dll
    /// and its runtimeconfig into the test output) and waits for it to finish. Shared with the
    /// mailcast deprecation tests, whose config stops the daemon before it opens anything.
    /// </summary>
    internal static (int ExitCode, string Stdout, string Stderr) RunDaemon(params string[] args)
    {
        string dll = Path.Combine(AppContext.BaseDirectory, "pdn-soundmodem.dll");
        File.Exists(dll).Should().BeTrue(
            $"the daemon is a project reference of the tests, so its dll is copied to "
            + $"{AppContext.BaseDirectory}");

        var info = new ProcessStartInfo(DotnetHost())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(dll);
        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"pdn-soundmodem {string.Join(' ', args)} did not exit within 60 s; it is "
                + "supposed to print and exit without opening anything");
        }

        return (process.ExitCode, stdout.Result, stderr.Result);
    }

    /// <summary>
    /// The dotnet host to run the daemon's dll with: the one running these tests when that is
    /// the muxer, else the one DOTNET_ROOT names (CI sets it so that apphosts can find the
    /// runtime), else the one on PATH.
    /// </summary>
    private static string DotnetHost()
    {
        string exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

        string? self = Environment.ProcessPath;
        if (self is not null && Path.GetFileName(self) == exe)
        {
            return self;
        }

        string? root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (root is not null && File.Exists(Path.Combine(root, exe)))
        {
            return Path.Combine(root, exe);
        }

        return exe;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pdn-soundmodem.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }
}
