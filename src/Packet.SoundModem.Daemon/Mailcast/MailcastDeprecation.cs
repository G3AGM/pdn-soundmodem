using System.Text.Json.Nodes;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// The notice that the built-in mailcast receiver is going (step 7 of the one-receiver plan,
/// issue #587): said once in the journal at start-up and shown on the mailcast panel, on a
/// station with a <c>mailcast</c> section and on no other. It changes nothing about how the
/// receiver runs; pdn-mailcast-receiver 0.9.0 or later, reading this station's audio, replaces it.
/// </summary>
internal static class MailcastDeprecation
{
    /// <summary>The receiver guide's section on running it beside pdn-soundmodem.</summary>
    internal const string GuideUrl =
        "https://github.com/packet-net/pdn-mailcast/blob/main/docs/moving-from-built-in.md";

    /// <summary>What replaces the built-in receiver, as the journal and the panel name it.</summary>
    internal const string Replacement = "pdn-mailcast-receiver 0.9.0 or later";

    /// <summary>
    /// The journal lines, plain ASCII: none for a station without the section, else what is going,
    /// what replaces it, what to copy across and where the guide is.
    /// </summary>
    internal static IReadOnlyList<string> JournalLines(MailcastConfig? config) =>
        config is null
            ? []
            :
            [
                "mailcast: WARNING - this built-in mailcast receiver is deprecated and will be removed in a later release",
                $"mailcast: its replacement is {Replacement}, which reads this station's audio (\"audio\": \"soundmodem\")",
                $"mailcast: to move across, {CopyAdvice(config)}",
                $"mailcast: the guide: {GuideUrl}",
            ];

    /// <summary>The same notice for the mailcast panel, short, with the guide as a link.</summary>
    internal static JsonObject Panel(MailcastConfig config) => new()
    {
        ["text"] = $"Deprecated: this built-in receiver will be removed. {Replacement} replaces it.",
        ["copy"] = $"To move across, {CopyAdvice(config)}.",
        ["guide"] = GuideUrl,
    };

    /// <summary>
    /// The keys this section sets that the receiver takes under the same names: <c>bbs</c> always
    /// (it is required), and <c>sources</c>, <c>dialKHz</c>, <c>hooks</c> and <c>retune</c> only
    /// where this section gives them. <c>stateDirectory</c> is not among them: the receiver keeps
    /// its own state, and the BBS refuses a bulletin it already has.
    /// </summary>
    internal static IReadOnlyList<string> KeysToCopy(MailcastConfig config)
    {
        List<string> keys = ["bbs"];
        if (!config.SourcesDefaulted)
        {
            keys.Add("sources");
        }

        if (config.DialKHz != MailcastConfig.DefaultDialKHz)
        {
            keys.Add("dialKHz");
        }

        if (config.Hooks is { Before: not null } or { After: not null })
        {
            keys.Add("hooks");
        }

        if (config.Retune)
        {
            keys.Add("\"retune\": true");
        }

        return keys;
    }

    private static string CopyAdvice(MailcastConfig config)
    {
        IReadOnlyList<string> keys = KeysToCopy(config);
        string list = keys.Count == 1 ? keys[0] : $"{string.Join(", ", keys.Take(keys.Count - 1))} and {keys[^1]}";
        return $"copy {list} from this \"mailcast\" section to the receiver's config (the same names), then remove the section here";
    }
}
