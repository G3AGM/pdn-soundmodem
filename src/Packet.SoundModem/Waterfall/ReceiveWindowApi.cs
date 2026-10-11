using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Rig;

namespace Packet.SoundModem.Waterfall;

/// <summary>
/// <c>/rig-window</c> (issue #585, step 2 of the one-receiver plan, packet-net/pdn-mailcast#75):
/// a program on the same machine asks this station to retune its rig and hold all transmitting
/// for a while, then puts everything back. Built on the same <see cref="RigControl.Tune"/> window
/// and <see cref="RigControl.HoldsTransmitter"/> hold the built-in mailcast receiver's own
/// retuner uses, generalised so any local program can ask for it - nothing here is
/// mailcast-specific.
/// </summary>
/// <remarks>
/// <para><b>The request handling without the HTTP</b>, the same split <c>TxLeaseApi</c> and
/// <c>RigApi</c> keep in the daemon: <see cref="WaterfallWebServer"/> checks the remote address
/// and the headers, reads the body and writes the answer, and everything else is
/// here, so a test can reach it with a fake clock and no listener.</para>
/// <para><b>Loopback only, no key.</b> The same rule as the channel audio stream (issue #584,
/// <see cref="ChannelAudioStream"/>): refused unless the request's remote address is loopback,
/// and refused if it carries an <c>Origin</c> header or a proxy's forwarding header at all. There is nobody to tell two local
/// callers apart, so they share the one window a station offers - the same owner name is used
/// whoever asks, and a second local caller's request behaves exactly as a renewal or a refusal of
/// the first one would, through <see cref="RigControl.Tune"/>'s own owner check.</para>
/// <para><b>The mode is never asked for.</b> This station's own <see cref="RigControl.Plan"/>
/// names its own data mode (PKTUSB and the like) if it has a USB-family one; otherwise plain USB
/// is asked for. A caller cannot name a mode, unlike the keyed <c>/api/rig/tune</c>.</para>
/// <para><b>The transmit lease.</b> Refused while a <see cref="TransmitLease"/> is held, so a
/// listener's receive window and a head end's broadcast lease can never both be granted; the
/// daemon's <c>TxLeaseApi</c> is the other half, refusing a lease while this window is open.</para>
/// <para><b>Bounded and self-ending.</b> Every window this opens is capped at
/// <see cref="RigControl.MaxWindow"/> (5 minutes) the same as <c>/api/rig/tune</c>; a caller that
/// stops renewing, or vanishes, is a window that simply runs out and puts the rig back on its
/// own - there is no separate "client disconnected" signal to wire up, because there is no
/// connection to notice losing: each request is a plain HTTP round trip.</para>
/// <para><b>Never a Flex.</b> A <c>rig</c> section is refused on a <c>flex:</c> device
/// (<c>DaemonConfig.ValidateRig</c>), so a Flex station has no <see cref="RigControl"/> at all and
/// this path 404s on one exactly as <c>/api/rig</c> and <c>/api/rig/tune</c> already do.</para>
/// </remarks>
internal static class ReceiveWindowApi
{
    /// <summary>The path this is served under, under whatever base the station page is.</summary>
    internal const string Path = "/rig-window";

    /// <summary>
    /// Who every window opened here is held by, in the rig's journal lines and in
    /// <c>TxLeaseApi</c>'s refusal of a lease while one is open.
    /// </summary>
    internal const string Owner = "a receive window";

    /// <summary>How long a window lasts when the request does not say.</summary>
    internal const double DefaultSeconds = 60;

    /// <summary>
    /// The passband asked for when the request does not say how wide a band it needs: wide
    /// enough for a narrowband data signal such as pdn-mailcast's bulletins, the built-in
    /// receiver's own fixed choice (<c>MailcastRetuner.PassbandHz</c>).
    /// </summary>
    internal const int DefaultWidthHz = 3000;

    /// <summary>What a GET or a malformed request is told about the shape of a good one.</summary>
    internal const string Usage =
        "POST {\"dialHz\": 7052000, \"seconds\": 60} to tune the rig there and hold all "
        + "transmitting for that long (at most 300 s at a time, optionally with \"widthHz\" - "
        + "the band in Hz this caller needs to hear, 3000 by default), renewable with another "
        + "POST before it ends; {\"release\": true} to put the rig back now. GET to read it";

    /// <summary>Answers one request.</summary>
    /// <param name="rig">The station's rig.</param>
    /// <param name="lease">The station's transmit lease, so a window is refused while it is held.</param>
    /// <param name="method">The HTTP method.</param>
    /// <param name="body">The request body, possibly empty.</param>
    internal static (int Status, JsonObject Answer) Handle(
        RigControl rig, TransmitLease lease, string method, string body)
    {
        if (method == "GET")
        {
            return (200, Describe(rig));
        }

        if (method != "POST")
        {
            return (405, Error(Usage));
        }

        JsonNode? asked;
        try
        {
            asked = string.IsNullOrWhiteSpace(body) ? new JsonObject() : JsonNode.Parse(body);
        }
        catch (JsonException bad)
        {
            return (400, Error($"the body is not JSON: {bad.Message}"));
        }

        bool release;
        double? dialHz;
        double seconds;
        int widthHz;
        try
        {
            release = asked?["release"]?.GetValue<bool>() == true;
            dialHz = asked?["dialHz"]?.GetValue<double>();
            seconds = asked?["seconds"]?.GetValue<double>() ?? DefaultSeconds;
            widthHz = asked?["widthHz"]?.GetValue<int>() ?? DefaultWidthHz;
        }
        catch (Exception wrongType) when (wrongType is InvalidOperationException or FormatException)
        {
            return (400, Error(
                "\"dialHz\" and \"seconds\" are numbers, \"widthHz\" a whole number, "
                + $"\"release\" true or false: {wrongType.Message}"));
        }

        if (release)
        {
            bool released = rig.Release(Owner);
            JsonObject answer = Describe(rig);
            answer["released"] = released;
            return (200, answer);
        }

        if (dialHz is not double dial)
        {
            return (400, Error(Usage));
        }

        if (!double.IsFinite(dial) || dial != Math.Floor(dial))
        {
            return (400, Error("\"dialHz\" is the dial frequency in whole hertz, as 7052000"));
        }

        if (!double.IsFinite(seconds) || seconds <= 0)
        {
            return (400, Error("\"seconds\" must be more than 0"));
        }

        if (widthHz <= 0 || widthHz > RigModes.MaxPassbandHz)
        {
            return (400, Error(
                "\"widthHz\" is the band in Hz this caller needs to hear, more than 0 and up to "
                + $"{RigModes.MaxPassbandHz}"));
        }

        // Clamped before it becomes a TimeSpan, which a number like 1e300 would overflow; anything
        // over the cap is still reported as capped.
        double clamped = Math.Min(seconds, RigControl.MaxWindow.TotalSeconds * 2);
        if (dial > 100_000_000_000)
        {
            return (400, Error($"{dial} Hz is not a dial frequency"));
        }

        string mode = rig.Plan is { } plan && RigModes.SidebandOf(plan.Mode) == "usb"
            ? plan.Mode.ToUpperInvariant()
            : "USB";

        // A lease and this window are decided against each other in the lease, never across the
        // tune's talk to rigctld: while this window is opening a lease is refused at once, and
        // once the tune returns the rig shows the window itself.
        if (!lease.TryBeginOpeningWindow(out int? holder))
        {
            JsonObject refused = Describe(rig);
            refused["refused"] =
                $"sub-channel {holder} holds the transmit lease, so a receive window is refused "
                + "until it ends";
            return (409, refused);
        }

        RigTuneResult result;
        try
        {
            result = rig.Tune(
                new RigTuning((long)dial, mode, widthHz), TimeSpan.FromSeconds(clamped), Owner);
        }
        finally
        {
            lease.EndOpeningWindow();
        }

        switch (result.Outcome)
        {
            case RigTuneOutcome.Invalid:
                return (400, Error(result.Why!));

            case RigTuneOutcome.Refused:
            {
                JsonObject refused = Describe(rig);
                refused["refused"] = result.Why;
                return (409, refused);
            }

            case RigTuneOutcome.Failed:
            {
                JsonObject failed = Describe(rig);
                failed["failed"] = result.Why;
                return (500, failed);
            }

            default:
            {
                JsonObject tuned = Describe(rig);
                tuned["renewed"] = result.Outcome == RigTuneOutcome.Renewed;
                tuned["seconds"] = Math.Min(seconds, RigControl.MaxWindow.TotalSeconds);
                tuned["capped"] = result.Capped;
                return (200, tuned);
            }
        }
    }

    /// <summary>The rig as it stands, for this endpoint's own answers.</summary>
    private static JsonObject Describe(RigControl rig)
    {
        RigState state = rig.Snapshot();
        return new JsonObject
        {
            ["connected"] = state.Connected,
            ["dialHz"] = state.Tuning?.DialHz,
            ["mode"] = state.Tuning?.Mode,
            ["passbandHz"] = state.Tuning?.PassbandHz,
            ["transmitHeld"] = rig.HoldsTransmitter,
            ["window"] = state.Window is { } window
                ? new JsonObject
                {
                    ["owner"] = window.Owner,
                    ["dialHz"] = window.Tuning.DialHz,
                    ["mode"] = window.Tuning.Mode,
                    ["passbandHz"] = window.Tuning.PassbandHz,
                    ["expires"] = Utc(window.Expires),
                }
                : null,
            ["problem"] = state.LastProblem,
        };
    }

    private static JsonObject Error(string why) => new() { ["refused"] = why };

    /// <summary>An instant as ISO 8601 UTC to the second.</summary>
    private static string Utc(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
