using AwesomeAssertions;
using Packet.SoundModem.Updates;

namespace Packet.SoundModem.Tests.Updates;

/// <summary>
/// Whether this machine installs from packet-net's apt repository: read from
/// <c>sources.list</c> itself and from every file under <c>sources.list.d</c>, so a station that
/// added the line to either place is found the same way.
/// </summary>
public class AptSourcesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pdn-apt-sources-").FullName;

    private string SourcesFile => Path.Combine(_root, "sources.list");

    private string SourcesDirectory => Path.Combine(_root, "sources.list.d");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private const string Line =
        "deb [signed-by=/usr/share/keyrings/packet-net.gpg] https://packet-net.github.io/apt ./";

    [Fact]
    public void A_Line_In_Sources_List_Itself_Is_Found()
    {
        File.WriteAllText(SourcesFile, Line + "\n");

        AptSources.ThisMachine(SourcesFile, SourcesDirectory).Should().BeTrue(
            "the classic single file is still apt's, not just the .d directory");
    }

    [Fact]
    public void A_Line_In_A_File_Under_Sources_List_D_Is_Found()
    {
        Directory.CreateDirectory(SourcesDirectory);
        File.WriteAllText(Path.Combine(SourcesDirectory, "packet-net.list"), Line + "\n");

        AptSources.ThisMachine(SourcesFile, SourcesDirectory).Should().BeTrue();
    }

    [Fact]
    public void Neither_Place_Naming_The_Repository_Is_Not_From_The_Apt_Repo()
    {
        File.WriteAllText(SourcesFile, "deb http://deb.debian.org/debian bookworm main\n");
        Directory.CreateDirectory(SourcesDirectory);
        File.WriteAllText(Path.Combine(SourcesDirectory, "other.list"), "deb http://example.com/apt ./\n");

        AptSources.ThisMachine(SourcesFile, SourcesDirectory).Should().BeFalse();
    }

    [Fact]
    public void Missing_Files_And_A_Missing_Directory_Are_Read_As_Not_From_The_Apt_Repo_Not_As_A_Failure()
    {
        AptSources.ThisMachine(SourcesFile, SourcesDirectory).Should().BeFalse(
            "a machine with neither file yet is not an error, just not installed from the repository");
    }

    [Fact]
    public void Both_Places_Are_Consulted_Even_When_Only_One_Names_The_Repository()
    {
        File.WriteAllText(SourcesFile, "deb http://deb.debian.org/debian bookworm main\n");
        Directory.CreateDirectory(SourcesDirectory);
        File.WriteAllText(Path.Combine(SourcesDirectory, "packet-net.list"), Line + "\n");

        AptSources.ThisMachine(SourcesFile, SourcesDirectory).Should().BeTrue(
            "a line in sources.list.d must still be found when sources.list says nothing");
    }

    [Fact]
    public void The_Real_Paths_Are_The_Classic_Single_File_And_The_D_Directory()
    {
        AptSources.SourcesFile.Should().Be("/etc/apt/sources.list");
        AptSources.SourcesDirectory.Should().Be("/etc/apt/sources.list.d");
    }
}
