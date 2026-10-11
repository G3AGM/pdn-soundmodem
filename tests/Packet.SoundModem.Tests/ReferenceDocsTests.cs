using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests;

/// <summary>
/// The reference pages under docs/reference name everything the code accepts: every key of the
/// configuration file, every command-line flag, every catalogue mode, and the usage text as
/// <c>--help</c> prints it.
/// </summary>
/// <remarks>
/// The pages are written from the code, and nothing else holds them to it. A key added to
/// DaemonConfig.cs, a flag added to the argument switch in Program.cs, or a mode added to the
/// catalogue would otherwise go undocumented until a reader noticed, so each of these reads the
/// source of truth (the deserialiser's own view of the config classes, the case labels of the
/// switch, <see cref="ModemCatalog.KnownModes"/>, <see cref="Usage.Text"/>) and looks for it on
/// the page in code font. The failure message lists every name that is missing, so one run
/// says everything the page needs.
/// </remarks>
public partial class ReferenceDocsTests
{
    private static readonly string ConfigPage = Path.Combine("docs", "reference", "config.md");
    private static readonly string CommandLinePage = Path.Combine("docs", "reference", "command-line.md");
    private static readonly string ModesPage = Path.Combine("docs", "05-modes.md");

    [Fact]
    public void Every_Config_Key_Is_Documented_In_The_Configuration_Reference()
    {
        // A copy of the daemon's own options: the same naming policy and case rule the file is
        // read with, so a JsonPropertyName attribute or a naming policy added later is honoured
        // here too. Made read-only as the first Deserialize would, which installs the resolver
        // the metadata comes from.
        var options = new JsonSerializerOptions(DaemonConfig.Options);
        options.MakeReadOnly(populateMissingResolver: true);
        StringComparer keyComparer = options.PropertyNameCaseInsensitive
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        List<(Type Owner, string Name)> keys = [.. ConfigKeys(options)];
        keys.Should().HaveCountGreaterThan(
            50,
            "the config classes are found by walking DaemonConfig's property types; if that "
            + "found almost nothing the walk broke, and it needs fixing rather than this test "
            + "deleting");

        HashSet<string> documented = CodeTokens(ReadPage(ConfigPage), keyComparer);

        List<string> missing = keys
            .Where(k => !documented.Contains(k.Name))
            .Select(k => $"{k.Owner.Name}.{k.Name}")
            .ToList();

        // The list goes in the reason: BeEmpty on its own reports one item and stops.
        missing.Should().BeEmpty(
            "every JSON property of the configuration has to appear in {0} as a code-font "
            + "token (the key in backticks), with its type, default and one sentence. "
            + "Missing (class.property):{1}",
            ConfigPage,
            string.Concat(missing.Select(m => Environment.NewLine + "  " + m)));
    }

    [Fact]
    public void Every_Flag_The_Parser_Accepts_Is_Documented_In_The_Command_Line_Reference()
    {
        string program = Path.Combine(
            FindRepoRoot(), "src", "Packet.SoundModem.Daemon", "Program.cs");

        // The argument switch is the only place in the file a case label is a "--" string;
        // UsageTests reads the same labels the same way.
        List<string> flags = CaseLabel().Matches(File.ReadAllText(program))
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        flags.Should().HaveCountGreaterThan(
            20,
            "the argument switch in Program.cs is found by a regex over its case labels; if "
            + "that found almost nothing the switch changed shape, and the regex needs updating "
            + "rather than this test deleting");

        HashSet<string> documented = CodeTokens(ReadPage(CommandLinePage), StringComparer.Ordinal);
        List<string> missing = flags.Where(f => !documented.Contains(f)).ToList();

        missing.Should().BeEmpty(
            "every flag Program.cs parses has to appear in {0} as a code-font token, with its "
            + "argument, default and one sentence. Missing:{1}",
            CommandLinePage,
            string.Concat(missing.Select(m => Environment.NewLine + "  " + m)));
    }

    [Fact]
    public void Every_Catalogue_Mode_Is_Documented_In_The_Mode_Table()
    {
        HashSet<string> documented = CodeTokens(ReadPage(ModesPage), StringComparer.Ordinal);
        List<string> missing = ModemCatalog.KnownModes.Where(m => !documented.Contains(m)).ToList();

        missing.Should().BeEmpty(
            "every mode in ModemCatalog.KnownModes has to have a row in {0}, with its name "
            + "in code font. Missing:{1}",
            ModesPage,
            string.Concat(missing.Select(m => Environment.NewLine + "  " + m)));
    }

    [Fact]
    public void The_Mode_Tables_Radio_Path_Column_Is_What_The_Catalogue_Says()
    {
        // The survey proposes modes by this answer, so a row saying FM for a mode the catalogue
        // calls SSB is a station being offered the wrong modes, not a typo.
        var wrong = new List<string>();
        int checkedRows = 0;
        int pathColumn = -1;
        foreach (string line in ReadPage(ModesPage).Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (!line.StartsWith('|'))
            {
                pathColumn = -1;
                continue;
            }

            string[] cells = line.Split('|').Select(c => c.Trim()).ToArray();
            int header = Array.IndexOf(cells, "Radio path");
            if (header >= 0)
            {
                pathColumn = header;
                continue;
            }

            if (pathColumn < 0 || cells.Length <= pathColumn
                || CodeSpan().Match(cells[1]) is not { Success: true } name)
            {
                continue;
            }

            string mode = name.Groups[1].Value;
            string cell = cells[pathColumn];
            RadioPath? documented = cell.Contains("SSB", StringComparison.Ordinal) ? RadioPath.Ssb
                : cell.Contains("FM", StringComparison.Ordinal) ? RadioPath.Fm
                : null;
            checkedRows++;
            if (documented != ModemCatalog.RadioPathFor(mode))
            {
                wrong.Add($"{mode}: the page says \"{cell}\", the catalogue {ModemCatalog.RadioPathFor(mode)}");
            }
        }

        checkedRows.Should().BeGreaterThan(20, "the NinoTNC-lineage table has a Radio path column");
        wrong.Should().BeEmpty(
            "{0}'s Radio path column and ModemCatalog.RadioPathFor have to agree:{1}",
            ModesPage,
            string.Concat(wrong.Select(w => Environment.NewLine + "  " + w)));
    }

    [Fact]
    public void The_Usage_Block_In_The_Command_Line_Reference_Is_What_Help_Prints()
    {
        string[] lines = ReadPage(CommandLinePage).Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        // The heading that introduces the printed usage, then the first fenced block after it.
        int[] headings = lines
            .Select((line, index) => (line, index))
            .Where(p => p.line.StartsWith("## ", StringComparison.Ordinal)
                        && p.line.Contains("usage", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.index)
            .ToArray();

        headings.Should().ContainSingle(
            $"{CommandLinePage} has one section quoting the usage text, and this test finds it "
            + "by a heading containing the word usage");

        int open = Array.FindIndex(lines, headings[0], line => Fence().IsMatch(line));
        open.Should().BePositive($"the usage section of {CommandLinePage} opens a fenced block");

        int close = Array.FindIndex(lines, open + 1, line => Fence().IsMatch(line));
        close.Should().BePositive($"the fenced block in the usage section of {CommandLinePage} closes");

        string[] quoted = lines[(open + 1)..close];
        string[] printed = Usage.Text.Split('\n');

        // Line by line, naming the first line that differs: the two collections are long, and
        // a plain Equal would print both in full.
        int differs = Enumerable.Range(0, Math.Min(quoted.Length, printed.Length))
            .FirstOrDefault(i => quoted[i] != printed[i], -1);

        differs.Should().Be(
            -1,
            "the fenced block under the usage heading of {0} is what pdn-soundmodem --help "
            + "prints, line for line; when Usage.cs changes, paste the new text over the block. "
            + "Page line {1} reads {2} and --help prints {3}",
            CommandLinePage,
            open + 2 + differs,
            differs >= 0 ? $"\"{quoted[differs]}\"" : "",
            differs >= 0 ? $"\"{printed[differs]}\"" : "");

        quoted.Should().HaveCount(
            printed.Length,
            "the fenced block under the usage heading of {0} has every line --help prints and "
            + "no more; the first {1} lines match",
            CommandLinePage,
            Math.Min(quoted.Length, printed.Length));
    }

    /// <summary>
    /// Every JSON property the deserialiser would read, starting from <see cref="DaemonConfig"/>
    /// and following every property type reachable from it: nested config classes, and the
    /// element types of arrays and lists. Each name is the property's name as
    /// <paramref name="options"/> resolves it, so a JsonPropertyName attribute or a naming
    /// policy is honoured. Extension-data dictionaries and ignored properties are not keys.
    /// </summary>
    private static IEnumerable<(Type Owner, string Name)> ConfigKeys(JsonSerializerOptions options)
    {
        var seen = new HashSet<Type> { typeof(DaemonConfig) };
        var pending = new Queue<Type>([typeof(DaemonConfig)]);

        while (pending.Count > 0)
        {
            Type type = pending.Dequeue();

            foreach (JsonPropertyInfo property in options.GetTypeInfo(type).Properties)
            {
                if (property.IsExtensionData || IsIgnored(property))
                {
                    continue;
                }

                yield return (type, property.Name);

                foreach (Type nested in ConfigTypesIn(property.PropertyType))
                {
                    if (seen.Add(nested))
                    {
                        pending.Enqueue(nested);
                    }
                }
            }
        }
    }

    /// <summary>
    /// The config classes a property of type <paramref name="type"/> leads to: the type itself,
    /// or what it is a nullable of, an array of, or a collection of, when that is one of ours
    /// (a class in a Packet.SoundModem namespace) rather than a string, a number or JSON.
    /// </summary>
    private static IEnumerable<Type> ConfigTypesIn(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type.IsArray)
        {
            type = type.GetElementType()!;
        }
        else if (type != typeof(string))
        {
            Type? enumerable = type.GetInterfaces().Concat([type])
                .FirstOrDefault(i => i.IsGenericType
                                     && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            if (enumerable is not null)
            {
                type = enumerable.GetGenericArguments()[0];
            }
        }

        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type.IsClass && type != typeof(string)
            && type.Namespace?.StartsWith("Packet.SoundModem", StringComparison.Ordinal) == true)
        {
            yield return type;
        }
    }

    /// <summary>A property the deserialiser never reads: marked [JsonIgnore] outright.</summary>
    private static bool IsIgnored(JsonPropertyInfo property) =>
        property.AttributeProvider?
            .GetCustomAttributes(typeof(JsonIgnoreAttribute), inherit: false)
            .OfType<JsonIgnoreAttribute>()
            .Any(a => a.Condition == JsonIgnoreCondition.Always) == true;

    /// <summary>Every inline code span in <paramref name="markdown"/>, as the text between the backticks.</summary>
    private static HashSet<string> CodeTokens(string markdown, StringComparer comparer) =>
        CodeSpan().Matches(markdown)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(comparer);

    private static string ReadPage(string relative)
    {
        string path = Path.Combine(FindRepoRoot(), relative);
        File.Exists(path).Should().BeTrue($"{relative} is a reference page this test pins to the code");
        return File.ReadAllText(path);
    }

    // A case label of the argument switch in Program.cs.
    [GeneratedRegex("case \"(--[a-z0-9-]+)\":")]
    private static partial Regex CaseLabel();

    // An inline code span of one backtick pair on one line.
    [GeneratedRegex("`([^`\n]+)`")]
    private static partial Regex CodeSpan();

    // A fence line: ``` or ~~~, optionally indented up to three spaces, optionally with an info string.
    [GeneratedRegex(@"^\s{0,3}(```|~~~)")]
    private static partial Regex Fence();

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
