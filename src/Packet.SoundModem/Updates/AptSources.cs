namespace Packet.SoundModem.Updates;

/// <summary>
/// Whether this machine installs from packet-net's apt repository, as the install guide sets it
/// up (<c>/etc/apt/sources.list.d/packet-net.list</c>), or had the .deb installed by hand.
/// </summary>
internal static class AptSources
{
    /// <summary>The repository's host, which any entry for it names.</summary>
    public const string RepoHost = "packet-net.github.io";

    /// <summary>The classic single-file sources list, which apt still reads alongside <see cref="SourcesDirectory"/>.</summary>
    public const string SourcesFile = "/etc/apt/sources.list";

    /// <summary>Where apt keeps its per-repository sources.</summary>
    public const string SourcesDirectory = "/etc/apt/sources.list.d";

    /// <summary>
    /// Whether any of <paramref name="files"/> (each file's text) has a live entry naming
    /// <see cref="RepoHost"/>: a one-line <c>deb</c> entry, or a deb822 <c>URIs:</c> field, not a
    /// comment, and not in a deb822 stanza marked <c>Enabled: no</c>.
    /// </summary>
    public static bool NamesRepo(IEnumerable<string> files)
    {
        foreach (string text in files)
        {
            foreach (string stanza in text.Replace("\r", "", StringComparison.Ordinal).Split("\n\n"))
            {
                var lines = stanza.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
                bool disabled = lines.Any(l => l.StartsWith("Enabled:", StringComparison.OrdinalIgnoreCase)
                    && l["Enabled:".Length..].Trim().Equals("no", StringComparison.OrdinalIgnoreCase));
                if (!disabled && lines.Any(l => l.Contains(RepoHost, StringComparison.OrdinalIgnoreCase)
                    && (l.StartsWith("deb ", StringComparison.Ordinal) || l.StartsWith("deb\t", StringComparison.Ordinal)
                        || l.StartsWith("URIs:", StringComparison.OrdinalIgnoreCase))))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The check against this machine's own apt sources: <see cref="SourcesFile"/> itself and
    /// every file in <see cref="SourcesDirectory"/> - a station that added the packet-net line
    /// to the classic single file, rather than its own file under the <c>.d</c> directory, is
    /// found the same way. Never throws.
    /// </summary>
    public static bool ThisMachine() => ThisMachine(SourcesFile, SourcesDirectory);

    /// <summary>The same check against the given paths, for tests. Never throws.</summary>
    internal static bool ThisMachine(string sourcesFile, string sourcesDirectory)
    {
        try
        {
            var texts = new List<string> { ReadIfExists(sourcesFile) };
            if (Directory.Exists(sourcesDirectory))
            {
                texts.AddRange(Directory.EnumerateFiles(sourcesDirectory)
                    .Where(f => f.EndsWith(".list", StringComparison.Ordinal) || f.EndsWith(".sources", StringComparison.Ordinal))
                    .Select(ReadIfExists));
            }

            return NamesRepo(texts);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary><paramref name="path"/>'s text, or empty for one that does not exist or cannot be read.</summary>
    private static string ReadIfExists(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}
