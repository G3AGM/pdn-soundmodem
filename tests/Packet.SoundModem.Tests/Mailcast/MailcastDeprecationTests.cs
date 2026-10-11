using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Mailcast;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Tests.Daemon;

namespace Packet.SoundModem.Tests.Mailcast;

/// <summary>
/// The built-in receiver is going (issue #587): a station with a <c>mailcast</c> section says so
/// once at start-up and on its mailcast panel, with what replaces it, what to copy and the guide;
/// a station without the section says nothing. How the receiver runs is not touched, so the other
/// mailcast tests are the proof of that.
/// </summary>
public sealed class MailcastDeprecationTests : IAsyncDisposable
{
    private const string Guide =
        "https://github.com/packet-net/pdn-mailcast/blob/main/docs/moving-from-built-in.md";

    private readonly ScratchDirectory _dir = new("pdnsm-mailcast-deprecation");
    private readonly List<MailcastReceiver> _receivers = [];

    public async ValueTask DisposeAsync()
    {
        foreach (MailcastReceiver receiver in _receivers)
        {
            await receiver.DisposeAsync();
        }

        _dir.Dispose();
    }

    private static MailcastConfig Everything() => new()
    {
        Bbs = new MailcastBbsConfig { Password = "secret" },
        Sources = ["GB7RDG"],
        DialKHz = 7052.52,
        Retune = true,
        Hooks = new MailcastHooksConfig { After = new HookCommand { Command = "/bin/true" } },
    };

    [Fact]
    public void A_Station_With_The_Section_Is_Told_The_Receiver_Is_Going_And_What_To_Copy()
    {
        MailcastDeprecation.JournalLines(Everything()).Should().Equal(
            "mailcast: WARNING - this built-in mailcast receiver is deprecated and will be removed in a later release",
            "mailcast: its replacement is pdn-mailcast-receiver 0.9.0 or later, which reads this station's audio (\"audio\": \"soundmodem\")",
            "mailcast: to move across, copy bbs, sources, dialKHz, hooks and \"retune\": true from this \"mailcast\" section "
                + "to the receiver's config (the same names), then remove the section here",
            $"mailcast: the guide: {Guide}");
    }

    [Fact]
    public void Only_The_Keys_The_Section_Sets_Are_Listed_To_Copy()
    {
        var bbsOnly = new MailcastConfig { Bbs = new MailcastBbsConfig { Password = "secret" } };

        MailcastDeprecation.KeysToCopy(bbsOnly).Should().Equal(["bbs"], "sources, dialKHz, hooks and retune are all left at their defaults");
        MailcastDeprecation.JournalLines(bbsOnly).Should().Contain(
            "mailcast: to move across, copy bbs from this \"mailcast\" section to the receiver's config (the same names), "
            + "then remove the section here");
    }

    [Fact]
    public void A_Station_Without_The_Section_Is_Told_Nothing()
    {
        MailcastDeprecation.JournalLines(null).Should().BeEmpty();
    }

    [Fact]
    public void The_Notice_Is_Plain_Ascii_And_Never_Carries_The_Password()
    {
        IReadOnlyList<string> lines = MailcastDeprecation.JournalLines(Everything());
        string panel = MailcastDeprecation.Panel(Everything()).ToJsonString();

        lines.Should().AllSatisfy(line => line.All(c => c is >= ' ' and <= '~').Should().BeTrue(line));
        panel.All(c => c is >= ' ' and <= '~').Should().BeTrue(panel);
        lines.Should().NotContain(line => line.Contains("secret"));
        panel.Should().NotContain("secret");
    }

    [Fact]
    public void The_Panel_Status_Carries_The_Notice_With_The_Guide()
    {
        var placement = new MailcastPlacement(false, 1800, 350, 3250);
        MailcastReceiver receiver = MailcastReceiver.Create(
            Everything(), placement, MailcastSlotAudio.Rate, _dir.FullName,
            new FakeTimeProvider(MailcastSlotAudio.Noon), new ConcurrentQueue<string>().Enqueue);
        _receivers.Add(receiver);

        JsonNode deprecated = receiver.Status()["deprecated"]!;

        deprecated["text"]!.GetValue<string>().Should().Be(
            "Deprecated: this built-in receiver will be removed. pdn-mailcast-receiver 0.9.0 or later replaces it.");
        deprecated["copy"]!.GetValue<string>().Should().Be(
            "To move across, copy bbs, sources, dialKHz, hooks and \"retune\": true from this \"mailcast\" section "
            + "to the receiver's config (the same names), then remove the section here.");
        deprecated["guide"]!.GetValue<string>().Should().Be(Guide);
    }

    /// <summary>
    /// The daemon itself, as a process: an unknown mode stops it right after the notice would be
    /// said and before anything is opened, so the run is short and needs no sound card.
    /// </summary>
    [Fact]
    public void The_Daemon_Says_It_Once_At_Start_Up_With_The_Section_And_Not_Without()
    {
        string with = Path.Combine(_dir.FullName, "with.json");
        string without = Path.Combine(_dir.FullName, "without.json");
        File.WriteAllText(with,
            """{ "modems": [ { "mode": "no-such-mode" } ], "mailcast": { "bbs": { "password": "secret" }, "retune": true } }""");
        File.WriteAllText(without, """{ "modems": [ { "mode": "no-such-mode" } ] }""");

        (int withCode, string withOut, string withErr) = UsageTests.RunDaemon("--config", with);
        (int withoutCode, string withoutOut, string withoutErr) = UsageTests.RunDaemon("--config", without);

        withCode.Should().Be(2, withErr);
        withoutCode.Should().Be(2, withoutErr);
        // The WARNING line goes to stderr, so journald grades it as a warning; the rest to stdout.
        string[] said = withErr.Split('\n').Where(line => line.Contains("deprecated", StringComparison.Ordinal)).ToArray();
        said.Should().ContainSingle().Which.Should().Be(
            "mailcast: WARNING - this built-in mailcast receiver is deprecated and will be removed in a later release");
        withOut.Should().NotContain("deprecated");
        withOut.Should().Contain(
            "mailcast: to move across, copy bbs and \"retune\": true from this \"mailcast\" section");
        withOut.Should().Contain($"mailcast: the guide: {Guide}");
        (withoutOut + withoutErr).Should().NotContain("mailcast:").And.NotContain("deprecated");
    }
}
