using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using M0LTE.Radio.Audio;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Rig;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Rig;

/// <summary>
/// Rig control through rigctld, against <see cref="FakeRigctld"/>: connecting and reading the rig,
/// the band plan's dial, PTT, a rigctld that dies part way, and tuning windows that are always put
/// back and never overlap a transmission. Every window and every backoff runs on a fake clock; the
/// only real waiting is for the fake server's socket to answer, and nothing here is timed.
/// </summary>
public sealed class RigControlTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeRigctld _fake = new();
    private readonly FakeTimeProvider _time = new(Noon);
    private readonly ConcurrentQueue<string> _said = new();
    private readonly ConcurrentQueue<string> _warned = new();
    private readonly ConcurrentQueue<RigState> _changes = new();
    private readonly List<RigControl> _rigs = [];
    private readonly string _dir = Directory.CreateTempSubdirectory("pdnsm-rig").FullName;
    private volatile bool _pending;

    private string RestorePath => Path.Combine(_dir, RigRestoreFile.NameFor(_fake.Endpoint));

    public async ValueTask DisposeAsync()
    {
        foreach (RigControl rig in _rigs)
        {
            await rig.DisposeAsync();
        }

        await _fake.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    private RigControl Rig(
        bool keysThroughRig = false, RigTuning? plan = null, RigctldEndpoint? endpoint = null,
        bool persist = false, TimeSpan? replyTimeout = null)
    {
        var rig = new RigControl(new RigControlOptions
        {
            // Real socket waits, so generous unless a test is about the timeout itself: a busy CI
            // runner has taken longer than 3 s to answer the fake rigctld.
            ReplyTimeout = replyTimeout ?? TimeSpan.FromSeconds(30),
            ConnectTimeout = TimeSpan.FromSeconds(30),
            RestoreFile = persist ? RestorePath : null,
            TransmitPending = () => _pending,
            Endpoint = endpoint ?? _fake.Endpoint,
            Time = _time,
            KeysThroughRig = keysThroughRig,
            Plan = plan,
        });
        rig.Journal += _said.Enqueue;
        rig.Problem += _warned.Enqueue;
        rig.Changed += _changes.Enqueue;
        _rigs.Add(rig);
        return rig;
    }

    private async Task<RigControl> Started(bool keysThroughRig = false, RigTuning? plan = null, bool persist = false)
    {
        RigControl rig = Rig(keysThroughRig, plan, persist: persist);
        (await rig.StartAsync(CancellationToken.None)).Should().BeTrue();
        return rig;
    }

    /// <summary>
    /// Waits for something the rig does on its own thread, stepping the fake clock by
    /// <paramref name="step"/> between looks.
    /// </summary>
    /// <remarks>
    /// Stepped, each look is taken with every rig's watch back waiting on the clock, and the clock
    /// moves only then (<see cref="RigClock"/>), so the bound is a count of steps of fake time,
    /// far beyond anything a working build needs, and a slow rigctld cannot make the clock run on
    /// ahead of what the rig has done. Unstepped, it is waiting for socket work only, and the
    /// bound is <see cref="RigClock.Patience"/>, a safety net for a hang.
    /// </remarks>
    private async Task Eventually(Func<bool> condition, string what, TimeSpan step = default)
    {
        var patience = Stopwatch.StartNew();
        for (int look = 0; ; look++)
        {
            if (step > TimeSpan.Zero)
            {
                await RigClock.UntilWaitingAsync(_rigs);
            }

            if (condition())
            {
                return;
            }

            if (step > TimeSpan.Zero)
            {
                if (look >= 2000)
                {
                    break;
                }

                await RigClock.StepAsync(_time, _rigs, step);
                await Task.Delay(1);
            }
            else
            {
                if (patience.Elapsed > RigClock.Patience)
                {
                    break;
                }

                await Task.Delay(5);
            }
        }

        condition().Should().BeTrue(what);
    }

    [Fact]
    public async Task It_Connects_Reads_The_Rig_And_Says_What_It_Is_On()
    {
        RigControl rig = await Started();

        RigState state = rig.Snapshot();
        state.Connected.Should().BeTrue();
        state.Tuning.Should().Be(new RigTuning(14_074_000, "USB", 2400));
        _said.Should().Contain($"rig: rigctld at {_fake.Endpoint}: 14.074000 MHz USB (2400 Hz passband)");
        _fake.Sets.Should().BeEmpty("with no band plan and no rigctld PTT nothing is set");
    }

    [Fact]
    public async Task The_Band_Plan_Sets_The_Mode_And_Passband_Then_The_Dial()
    {
        await Started(plan: new RigTuning(7_049_450, "USB", 2400));

        _fake.DialHz.Should().Be(7_049_450);
        _fake.Mode.Should().Be("USB");
        _fake.PassbandHz.Should().Be(2400);
        _fake.Sets.Should().Equal(["M USB 2400", "F 7049450"], "the mode goes first, as some rigs move the dial on a mode change");
        _said.Should().Contain("rig: setting the rig to 7.049450 MHz USB (2400 Hz passband) from the band plan");
        _warned.Should().BeEmpty();
    }

    [Fact]
    public async Task A_Passband_The_Rig_Refuses_Falls_Back_To_Its_Normal_Width_And_Says_So()
    {
        _fake.RefusesPassband = true;
        _fake.NormalPassbandHz = 2700;

        await Started(plan: new RigTuning(7_049_450, "PKTUSB", 2400));

        _fake.Sets.Should().Equal(["M PKTUSB 2400", "M PKTUSB 0", "F 7049450"]);
        _fake.Mode.Should().Be("PKTUSB");
        _warned.Should().ContainSingle(w => w.Contains("would not take a 2400 Hz passband in PKTUSB"));
    }

    [Fact]
    public async Task A_Passband_The_Rig_Sets_Differently_Is_Warned_About()
    {
        _fake.WidestPassbandHz = 1800;

        await Started(plan: new RigTuning(7_049_450, "USB", 2400));

        _warned.Should().ContainSingle(w =>
            w.Contains("asked the rig for a 2400 Hz passband from the band plan, and it reports 1800 Hz"));
    }

    [Fact]
    public async Task A_Dial_The_Rig_Refuses_Is_A_Warning_And_The_Station_Carries_On()
    {
        _fake.RefusesFrequency = true;

        RigControl rig = await Started(plan: new RigTuning(7_049_450, "USB", 2400));

        rig.Connected.Should().BeTrue();
        _warned.Should().ContainSingle(w => w.Contains("RPRT -11") && w.Contains("set the rig to 7.049450 MHz USB"));
    }

    [Fact]
    public async Task Nothing_Listening_Is_A_Warning_Not_A_Failure()
    {
        // Nobody can be listening on it: a port 0 bind elsewhere in the suite could be handed a
        // number that was merely probed and let go, but never one of these.
        int port = FreePorts.Next();

        RigControl rig = Rig(endpoint: new RigctldEndpoint("127.0.0.1", port));
        bool answered = await rig.StartAsync(CancellationToken.None);

        answered.Should().BeFalse();
        rig.Connected.Should().BeFalse();
        _warned.Should().ContainSingle(w => w.Contains($"cannot reach rigctld at 127.0.0.1:{port}"));
        rig.HoldsTransmitter.Should().BeFalse("a missing rigctld does not stop a station keyed by its own line");
    }

    [Fact]
    public async Task A_Rigctld_That_Was_Not_Ready_Is_Retried_And_The_Plan_Applied_When_It_Answers()
    {
        _fake.Accepting = false;
        RigControl rig = Rig(plan: new RigTuning(7_049_450, "USB", 2400));

        (await rig.StartAsync(CancellationToken.None)).Should().BeFalse();
        _fake.DialHz.Should().Be(14_074_000);

        _fake.Accepting = true;
        // Connected is set before the new connection has applied the plan, so wait for both.
        await Eventually(
            () => rig.Connected && _fake.DialHz == 7_049_450,
            "the backoff should bring the connection up and the plan be applied",
            TimeSpan.FromSeconds(1));

        _said.Should().Contain(
            $"rig: rigctld at {_fake.Endpoint}: 14.074000 MHz USB (2400 Hz passband)",
            "a connection that hung up before answering never counted as one");
    }

    [Fact]
    public async Task A_Reconnect_That_Finds_The_Rig_Elsewhere_Puts_It_Back_On_The_Band_Plan()
    {
        RigControl rig = await Started(plan: new RigTuning(7_049_450, "USB", 2400));

        // A rig that was power-cycled while rigctld was restarted.
        _fake.DialHz = 145_000_000;
        _fake.Mode = "FM";
        _fake.Kill();

        await Eventually(() => _fake.DialHz == 7_049_450, "the reconnect should restore the plan", TimeSpan.FromSeconds(5));
        _fake.Mode.Should().Be("USB");
        _said.Should().Contain("rig: the rig is not where the band plan puts it; setting it back to 7.049450 MHz USB (2400 Hz passband)");
        rig.Connected.Should().BeTrue();
    }

    [Fact]
    public async Task Ptt_Through_Rigctld_Keys_With_T_1_And_Unkeys_With_T_0()
    {
        RigControl rig = await Started(keysThroughRig: true);
        IPttControl ptt = rig.KeyingPtt();

        ptt.Key();
        _fake.Ptt.Should().BeTrue();
        rig.Snapshot().Keyed.Should().BeTrue();

        ptt.Unkey();
        _fake.Ptt.Should().BeFalse();
        rig.Snapshot().Keyed.Should().BeFalse();

        // The connection opened with an unkey, the fail-safe, before any keyup of ours.
        _fake.Sets.Should().Equal(["T 0", "T 1", "T 0"]);
    }

    [Fact]
    public async Task Rigctld_Dying_Mid_Keyup_Is_Unkeyed_As_Soon_As_It_Answers_Again()
    {
        RigControl rig = await Started(keysThroughRig: true);
        IPttControl ptt = rig.KeyingPtt();
        ptt.Key();
        _fake.Ptt.Should().BeTrue();

        _fake.Kill();
        Action unkey = ptt.Unkey;

        unkey.Should().Throw<IOException>();
        rig.Snapshot().Keyed.Should().BeFalse("the station's own idea of keyed is cleared whatever the command does");

        // The radio is still keyed (rigctld never got the T 0) until a new connection's first act.
        await Eventually(() => !_fake.Ptt && _fake.Connections == 2, "the reconnect should unkey the radio", TimeSpan.FromSeconds(1));
        _fake.Commands.Where(c => c.Connection == 2).Select(c => c.Command).Take(2)
            .Should().Equal(["\\chk_vfo", "T 0"], "after checking it can be spoken to, the unkey is the first thing a new connection does");
        _warned.Should().Contain(w => w.Contains("lost rigctld") && w.Contains("may still be keyed"));
        _said.Should().Contain("rig: unkeyed the radio, which had been keyed when rigctld went away");
    }

    [Fact]
    public async Task A_Keyup_With_Rigctld_Gone_Fails_And_Nothing_Is_Left_Keyed()
    {
        RigControl rig = await Started(keysThroughRig: true);
        IPttControl ptt = rig.KeyingPtt();
        _fake.Accepting = false;
        _fake.Kill();

        Action key = ptt.Key;

        key.Should().Throw<IOException>();
        rig.Snapshot().Keyed.Should().BeFalse();
    }

    [Fact]
    public async Task Shutdown_Unkeys_A_Rigctld_Ptt()
    {
        RigControl rig = await Started(keysThroughRig: true);
        rig.KeyingPtt().Key();

        await rig.DisposeAsync();

        _fake.Ptt.Should().BeFalse();
        _fake.Sets[^1].Should().Be("T 0");
    }

    [Fact]
    public async Task A_Window_Tunes_The_Rig_And_Puts_It_Back_When_Its_Time_Runs_Out()
    {
        RigControl rig = await Started();

        RigTuneResult result = rig.Tune(new RigTuning(7_052_000, "usb", 0), TimeSpan.FromSeconds(60), "mailcast");

        result.Outcome.Should().Be(RigTuneOutcome.Tuned);
        _fake.DialHz.Should().Be(7_052_000);
        _fake.Mode.Should().Be("USB");
        rig.HoldsTransmitter.Should().BeTrue();
        result.Window!.Expires.Should().Be(Noon.AddSeconds(60));

        _time.Advance(TimeSpan.FromSeconds(59));
        _fake.DialHz.Should().Be(7_052_000);

        _time.Advance(TimeSpan.FromSeconds(1));
        _fake.DialHz.Should().Be(14_074_000);
        _fake.Mode.Should().Be("USB");
        _fake.PassbandHz.Should().Be(2400);
        rig.HoldsTransmitter.Should().BeFalse();
        _said.Should().Contain(s => s.Contains("has ended (its time ran out)"));
        _said.Should().Contain("rig: put back to 14.074000 MHz USB (2400 Hz passband); transmissions resume");
    }

    [Fact]
    public async Task Releasing_A_Window_Puts_The_Rig_Back_At_Once()
    {
        RigControl rig = await Started();
        RigTuneResult result = rig.Tune(new RigTuning(7_052_000, "USB", 0), TimeSpan.FromSeconds(60), "mailcast");

        result.Window!.Dispose();

        _fake.DialHz.Should().Be(14_074_000);
        rig.Snapshot().Window.Should().BeNull();
        rig.Release("mailcast").Should().BeFalse("it has already gone");
    }

    [Fact]
    public async Task A_Window_Whose_Owner_Goes_Away_Is_Put_Back()
    {
        RigControl rig = await Started();
        using var owner = new CancellationTokenSource();
        rig.Tune(new RigTuning(7_052_000, "USB", 0), TimeSpan.FromSeconds(300), "mailcast", owner.Token);

        await owner.CancelAsync();

        _fake.DialHz.Should().Be(14_074_000);
        rig.HoldsTransmitter.Should().BeFalse();
        _said.Should().Contain(s => s.Contains("has ended (mailcast went away)"));
    }

    [Fact]
    public async Task Shutdown_Puts_An_Open_Window_Back()
    {
        RigControl rig = await Started();
        rig.Tune(new RigTuning(7_052_000, "USB", 0), TimeSpan.FromSeconds(300), "mailcast");

        await rig.DisposeAsync();

        _fake.DialHz.Should().Be(14_074_000);
        _said.Should().Contain(s => s.Contains("has ended (the station is stopping)"));
    }

    [Fact]
    public async Task A_Window_Is_Capped_At_Five_Minutes_And_Renewed_By_Its_Owner()
    {
        RigControl rig = await Started();

        RigTuneResult first = rig.Tune(new RigTuning(7_052_000, "USB", 0), TimeSpan.FromMinutes(20), "mailcast");
        first.Capped.Should().BeTrue();
        first.Window!.Expires.Should().Be(Noon.AddMinutes(5));

        _time.Advance(TimeSpan.FromMinutes(4));
        RigTuneResult renewed = rig.Tune(new RigTuning(7_052_000, "USB", 0), TimeSpan.FromMinutes(5), "mailcast");
        renewed.Outcome.Should().Be(RigTuneOutcome.Renewed);
        renewed.Window.Should().BeSameAs(first.Window);

        _time.Advance(TimeSpan.FromMinutes(4));
        _fake.DialHz.Should().Be(7_052_000, "the renewal moved the end to 12:09");

        _time.Advance(TimeSpan.FromMinutes(1));
        _fake.DialHz.Should().Be(14_074_000);
    }

    [Fact]
    public async Task A_Renewal_Somewhere_New_Retunes_And_Still_Restores_To_Where_It_Started()
    {
        RigControl rig = await Started();
        rig.Tune(new RigTuning(7_052_000, "USB", 0), TimeSpan.FromSeconds(60), "mailcast");

        rig.Tune(new RigTuning(3_582_000, "LSB", 0), TimeSpan.FromSeconds(60), "mailcast")
            .Outcome.Should().Be(RigTuneOutcome.Renewed);
        _fake.DialHz.Should().Be(3_582_000);
        _fake.Mode.Should().Be("LSB");

        rig.Release("mailcast").Should().BeTrue();
        _fake.DialHz.Should().Be(14_074_000);
        _fake.Mode.Should().Be("USB");
    }

    [Fact]
    public async Task Another_Owner_Is_Refused_While_A_Window_Is_Open()
    {
        RigControl rig = await Started();
        rig.Tune(new RigTuning(7_052_000, "USB", 0), TimeSpan.FromSeconds(60), "mailcast");

        RigTuneResult other = rig.Tune(new RigTuning(10_147_000, "USB", 0), TimeSpan.FromSeconds(60), "the API");

        other.Outcome.Should().Be(RigTuneOutcome.Refused);
        other.Why.Should().Contain("already tuned to 7.052000 MHz USB").And.Contain("for mailcast");
        _fake.DialHz.Should().Be(7_052_000);
    }

    [Fact]
    public async Task The_Rig_Is_Never_Retuned_While_The_Transmitter_Is_Keyed()
    {
        RigControl rig = await Started();
        IPttControl ptt = rig.Guard(new NullPtt());
        ptt.Key();

        RigTuneResult result = rig.Tune(new RigTuning(7_052_000, "USB", 0), TimeSpan.FromSeconds(60), "mailcast");

        result.Outcome.Should().Be(RigTuneOutcome.Refused);
        result.Why.Should().Contain("keyed");
        _fake.DialHz.Should().Be(14_074_000);
        _fake.Sets.Should().BeEmpty();

        ptt.Unkey();
        rig.Tune(new RigTuning(7_052_000, "USB", 0), TimeSpan.FromSeconds(60), "mailcast")
            .Outcome.Should().Be(RigTuneOutcome.Tuned);
    }

    [Fact]
    public async Task A_Keyup_Is_Refused_While_A_Window_Is_Open_And_Allowed_Once_It_Is_Put_Back()
    {
        RigControl rig = await Started(keysThroughRig: true);
        IPttControl ptt = rig.KeyingPtt();
        RigTuneResult window = rig.Tune(new RigTuning(7_052_000, "USB", 0), TimeSpan.FromSeconds(60), "mailcast");

        Action key = ptt.Key;

        key.Should().Throw<InvalidOperationException>().WithMessage("*tuned to 7.052000 MHz USB (normal passband) for mailcast*");
        _fake.Ptt.Should().BeFalse();

        window.Window!.Dispose();
        ptt.Key();
        _fake.Ptt.Should().BeTrue();
        ptt.Unkey();
    }

    [Fact]
    public async Task A_Guarded_Line_Is_Refused_During_A_Window_Without_Touching_The_Line()
    {
        RigControl rig = await Started();
        var line = new CountingPtt();
        IPttControl ptt = rig.Guard(line);
        rig.Tune(new RigTuning(7_052_000, "USB", 0), TimeSpan.FromSeconds(60), "mailcast");

        Action key = ptt.Key;

        key.Should().Throw<InvalidOperationException>();
        line.Keys.Should().Be(0);
        rig.Guard(ptt).Should().BeSameAs(ptt, "guarding twice would be one lock taken twice for nothing");
    }

    [Fact]
    public async Task A_Transmission_The_Inhibit_Does_Not_Hold_Is_Still_Refused_At_The_Key_During_A_Window()
    {
        // ARDOP's own bursts own the channel's timing and so pass every inhibit; the PTT is the
        // backstop that keeps them off a retuned rig too.
        RigControl rig = await Started();
        var channel = new SoundModemChannel(12000, randomSeed: 3);
        channel.AddModem(0, sink => ModemCatalog.Create("afsk1200", 12000, sink));
        channel.Csma.Persistence = 255;
        var output = new Packet.SoundModem.Tests.Channel.FakeAudioOutput(12000);
        var line = new CountingPtt();
        using var stop = new CancellationTokenSource();
        Task transmitter = channel.RunTransmitterAsync(output, rig.Guard(line), stop.Token);
        RigTuneResult window = rig.Tune(new RigTuning(7_052_000, "USB", 0), TimeSpan.FromSeconds(60), "mailcast");

        Task during = channel.EnqueueTransmit(_ => new float[1200], rejected: null, ownsChannelTiming: true);
        Func<Task> waitDuring = () => during.WaitAsync(TimeSpan.FromSeconds(30));

        (await waitDuring.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*tuned to 7.052000 MHz*");
        line.Keys.Should().Be(0);
        output.WrittenCount.Should().Be(0);

        window.Window!.Dispose();
        await channel.EnqueueTransmit(_ => new float[1200], rejected: null, ownsChannelTiming: true)
            .WaitAsync(TimeSpan.FromSeconds(30));
        line.Keys.Should().Be(1);

        await stop.CancelAsync();
        Func<Task> ended = () => transmitter;
        await ended.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_Restore_That_Cannot_Reach_The_Rig_Is_Owed_And_Holds_The_Transmitter_Until_It_Is_Done()
    {
        RigControl rig = await Started();
        rig.Tune(new RigTuning(7_052_000, "USB", 0), TimeSpan.FromSeconds(60), "mailcast");
        _fake.Accepting = false;
        _fake.Kill();

        // The window ends with nobody to tell: the restore is owed, and the station stays off the air.
        _time.Advance(TimeSpan.FromSeconds(60));
        await Eventually(() => rig.Snapshot().RestoreOwed is not null, "the restore should be owed");
        rig.HoldsTransmitter.Should().BeTrue();
        _fake.DialHz.Should().Be(7_052_000);

        _fake.Accepting = true;
        await Eventually(() => !rig.HoldsTransmitter, "the reconnect should do the restore", TimeSpan.FromSeconds(1));
        _fake.DialHz.Should().Be(14_074_000);
    }

    [Fact]
    public async Task A_Tune_With_Rigctld_Gone_Is_Refused_And_Says_Why()
    {
        RigControl rig = await Started();
        _fake.Accepting = false;
        _fake.Kill();
        await Eventually(() => !rig.Connected, "the poll should notice the connection has gone", TimeSpan.FromSeconds(5));

        RigTuneResult result = rig.Tune(new RigTuning(7_052_000, "USB", 0), TimeSpan.FromSeconds(60), "mailcast");

        result.Outcome.Should().Be(RigTuneOutcome.Refused);
        result.Why.Should().Contain("not connected");
    }

    [Theory]
    [InlineData(0, "USB", 0, "is not a dial frequency")]
    [InlineData(7_052_000, "DIGU", 0, "is not a mode")]
    [InlineData(7_052_000, "USB", -5, "is not one")]
    public async Task A_Tune_That_Makes_No_Sense_Is_Invalid(long dial, string mode, int passband, string why)
    {
        RigControl rig = await Started();

        RigTuneResult result = rig.Tune(new RigTuning(dial, mode, passband), TimeSpan.FromSeconds(60), "mailcast");

        result.Outcome.Should().Be(RigTuneOutcome.Invalid);
        result.Why.Should().Contain(why);
    }

    [Fact]
    public async Task The_Api_Tunes_Renews_Refuses_And_Releases()
    {
        RigControl rig = await Started();

        (int status, JsonObject answer) = RigApi.Handle(rig, "/api/rig", "GET", "");
        status.Should().Be(200);
        answer["connected"]!.GetValue<bool>().Should().BeTrue();
        answer["dialHz"]!.GetValue<long>().Should().Be(14_074_000);
        answer["window"].Should().BeNull();

        (status, answer) = RigApi.Handle(rig, "/api/rig/tune", "POST", """{"dialHz": 7052000, "mode": "USB", "seconds": 90}""");
        status.Should().Be(200);
        answer["renewed"]!.GetValue<bool>().Should().BeFalse();
        answer["transmitHeld"]!.GetValue<bool>().Should().BeTrue();
        answer["window"]!["expires"]!.GetValue<string>().Should().Be("2026-10-05T12:01:30Z");
        answer["window"]!["restoreTo"]!["dialHz"]!.GetValue<long>().Should().Be(14_074_000);

        (status, answer) = RigApi.Handle(rig, "/api/rig/tune", "POST", """{"dialHz": 7052000, "mode": "USB", "seconds": 600}""");
        status.Should().Be(200);
        answer["renewed"]!.GetValue<bool>().Should().BeTrue();
        answer["capped"]!.GetValue<bool>().Should().BeTrue();
        answer["seconds"]!.GetValue<double>().Should().Be(300);

        (status, answer) = RigApi.Handle(rig, "/api/rig/tune", "POST", """{"release": true}""");
        status.Should().Be(200);
        answer["released"]!.GetValue<bool>().Should().BeTrue();
        answer["window"].Should().BeNull();
        _fake.DialHz.Should().Be(14_074_000);
    }

    [Fact]
    public async Task The_Api_Answers_409_While_Keyed_And_400_For_A_Bad_Request()
    {
        RigControl rig = await Started();
        IPttControl ptt = rig.Guard(new NullPtt());
        ptt.Key();

        (int status, JsonObject answer) = RigApi.Handle(rig, "/api/rig/tune", "POST", """{"dialHz": 7052000, "mode": "USB"}""");
        status.Should().Be(409);
        answer["refused"]!.GetValue<string>().Should().Contain("keyed");
        answer["keyed"]!.GetValue<bool>().Should().BeTrue();
        ptt.Unkey();

        RigApi.Handle(rig, "/api/rig/tune", "POST", """{"mode": "USB"}""").Status.Should().Be(400);
        RigApi.Handle(rig, "/api/rig/tune", "POST", """{"dialHz": 7052000.5, "mode": "USB"}""").Status.Should().Be(400);
        RigApi.Handle(rig, "/api/rig/tune", "POST", """{"dialHz": "7052000", "mode": "USB"}""").Status.Should().Be(400);
        RigApi.Handle(rig, "/api/rig/tune", "POST", """{"dialHz": 7052000, "mode": "USB", "seconds": 0}""").Status.Should().Be(400);
        RigApi.Handle(rig, "/api/rig/tune", "POST", "not json").Status.Should().Be(400);
        RigApi.Handle(rig, "/api/rig", "POST", "").Status.Should().Be(405);
        _fake.Sets.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- the receive window (issue #585)

    private SoundModemChannel Channel(int seed) => new(12000, _time, randomSeed: seed);

    [Fact]
    public async Task The_Receive_Window_Tunes_Renews_And_Releases()
    {
        RigControl rig = await Started();
        SoundModemChannel channel = Channel(21);

        (int status, JsonObject answer) = ReceiveWindowApi.Handle(rig, channel.TransmitLease, "GET", "");
        status.Should().Be(200);
        answer["window"].Should().BeNull();

        (status, answer) = ReceiveWindowApi.Handle(
            rig, channel.TransmitLease, "POST", """{"dialHz": 7052000, "seconds": 90}""");
        status.Should().Be(200);
        answer["renewed"]!.GetValue<bool>().Should().BeFalse();
        answer["transmitHeld"]!.GetValue<bool>().Should().BeTrue();
        answer["window"]!["owner"]!.GetValue<string>().Should().Be(WaterfallWebServer.ReceiveWindowOwner);
        answer["window"]!["dialHz"]!.GetValue<long>().Should().Be(7_052_000);
        answer["window"]!["mode"]!.GetValue<string>().Should().Be("USB");
        answer["window"]!["passbandHz"]!.GetValue<int>().Should().Be(ReceiveWindowApi.DefaultWidthHz);
        _fake.DialHz.Should().Be(7_052_000);
        _fake.PassbandHz.Should().Be(ReceiveWindowApi.DefaultWidthHz);

        (status, answer) = ReceiveWindowApi.Handle(
            rig, channel.TransmitLease, "POST",
            """{"dialHz": 7052000, "seconds": 600, "widthHz": 2000}""");
        status.Should().Be(200);
        answer["renewed"]!.GetValue<bool>().Should().BeTrue();
        answer["capped"]!.GetValue<bool>().Should().BeTrue();
        answer["seconds"]!.GetValue<double>().Should().Be(RigControl.MaxWindow.TotalSeconds);
        _fake.PassbandHz.Should().Be(2000, "a renewal that asks for a different width retunes to it");

        (status, answer) = ReceiveWindowApi.Handle(rig, channel.TransmitLease, "POST", """{"release": true}""");
        status.Should().Be(200);
        answer["released"]!.GetValue<bool>().Should().BeTrue();
        answer["window"].Should().BeNull();
        _fake.DialHz.Should().Be(14_074_000);
    }

    [Theory]
    [InlineData(null, "USB")]
    [InlineData("USB", "USB")]
    [InlineData("PKTUSB", "PKTUSB")]
    [InlineData("LSB", "USB")]
    [InlineData("FM", "USB")]
    public async Task The_Receive_Window_Asks_For_The_Rigs_Own_Usb_Family_Mode_Or_Falls_Back_To_Usb(
        string? planMode, string expectedMode)
    {
        RigTuning? plan = planMode is null ? null : new RigTuning(7_049_450, planMode, 2400);
        RigControl rig = await Started(plan: plan);
        SoundModemChannel channel = Channel(22);

        (_, JsonObject answer) = (ReceiveWindowApi.Handle(
            rig, channel.TransmitLease, "POST", """{"dialHz": 7052000}"""));

        answer["window"]!["mode"]!.GetValue<string>().Should().Be(expectedMode);
    }

    [Fact]
    public async Task The_Receive_Window_Is_Refused_While_Keyed()
    {
        RigControl rig = await Started();
        IPttControl ptt = rig.Guard(new NullPtt());
        ptt.Key();
        SoundModemChannel channel = Channel(23);

        (int status, JsonObject answer) = ReceiveWindowApi.Handle(
            rig, channel.TransmitLease, "POST", """{"dialHz": 7052000}""");

        status.Should().Be(409);
        answer["refused"]!.GetValue<string>().Should().Contain("keyed");
        ptt.Unkey();
    }

    [Fact]
    public async Task The_Receive_Window_Answers_400_For_A_Bad_Request_And_405_For_Other_Methods()
    {
        RigControl rig = await Started();
        SoundModemChannel channel = Channel(24);

        ReceiveWindowApi.Handle(rig, channel.TransmitLease, "POST", "{}").Status.Should().Be(400);
        ReceiveWindowApi.Handle(rig, channel.TransmitLease, "POST", """{"dialHz": 7052000.5}""")
            .Status.Should().Be(400);
        ReceiveWindowApi.Handle(rig, channel.TransmitLease, "POST", """{"dialHz": "7052000"}""")
            .Status.Should().Be(400);
        ReceiveWindowApi.Handle(rig, channel.TransmitLease, "POST", """{"dialHz": 7052000, "seconds": 0}""")
            .Status.Should().Be(400);
        ReceiveWindowApi.Handle(rig, channel.TransmitLease, "POST", """{"dialHz": 7052000, "widthHz": 0}""")
            .Status.Should().Be(400);
        ReceiveWindowApi.Handle(rig, channel.TransmitLease, "POST", """{"dialHz": 7052000, "widthHz": 999999}""")
            .Status.Should().Be(400);
        ReceiveWindowApi.Handle(rig, channel.TransmitLease, "POST", "not json").Status.Should().Be(400);
        ReceiveWindowApi.Handle(rig, channel.TransmitLease, "DELETE", "").Status.Should().Be(405);
        _fake.Sets.Should().BeEmpty();
    }

    [Fact]
    public async Task A_Receive_Window_And_A_Transmit_Lease_Refuse_Each_Other_But_A_Lease_Alone_Is_Granted()
    {
        RigControl rig = await Started();
        SoundModemChannel channel = Channel(25);
        channel.AddModem(3, sink => new Afsk1200Modem(12000, sink));

        // A lease is still granted normally with no window open.
        (int leaseStatus, JsonObject leaseAnswer) = TxLeaseApi.Handle(
            channel, "POST", """{"subChannel": 3, "seconds": 60}""", null, rig);
        leaseStatus.Should().Be(200);
        leaseAnswer["held"]!.GetValue<bool>().Should().BeTrue();
        channel.TransmitLease.Release(3);

        // A receive window is refused while the lease is held.
        TxLeaseApi.Handle(channel, "POST", """{"subChannel": 3, "seconds": 60}""", null, rig);
        (int windowStatus, JsonObject windowAnswer) = ReceiveWindowApi.Handle(
            rig, channel.TransmitLease, "POST", """{"dialHz": 7052000}""");
        windowStatus.Should().Be(409);
        windowAnswer["refused"]!.GetValue<string>().Should().Contain("holds the transmit lease");
        channel.TransmitLease.Release(3);

        // And a lease is refused while a receive window is open.
        ReceiveWindowApi.Handle(rig, channel.TransmitLease, "POST", """{"dialHz": 7052000}""");
        (int refusedLeaseStatus, JsonObject refusedLease) = TxLeaseApi.Handle(
            channel, "POST", """{"subChannel": 3, "seconds": 60}""", null, rig);
        refusedLeaseStatus.Should().Be(409);
        refusedLease["refused"]!.GetValue<string>().Should().Contain("receive window");
        channel.TransmitLease.Holder.Should().BeNull("the lease was refused, not granted");
        ReceiveWindowApi.Handle(rig, channel.TransmitLease, "POST", """{"release": true}""");
    }

    [Fact]
    public async Task A_Receive_Window_Is_Capped_At_Five_Minutes_And_Ends_On_Its_Own()
    {
        RigControl rig = await Started();
        SoundModemChannel channel = Channel(26);

        (_, JsonObject answer) = ReceiveWindowApi.Handle(
            rig, channel.TransmitLease, "POST", """{"dialHz": 7052000, "seconds": 3600}""");
        answer["capped"]!.GetValue<bool>().Should().BeTrue();
        answer["seconds"]!.GetValue<double>().Should().Be(RigControl.MaxWindow.TotalSeconds);
        rig.HoldsTransmitter.Should().BeTrue();

        _time.Advance(RigControl.MaxWindow);

        _fake.DialHz.Should().Be(14_074_000);
        rig.HoldsTransmitter.Should().BeFalse();
    }

    [Fact]
    public async Task A_Receive_Window_Writes_Its_Restore_Target_Down_For_Recovery_After_A_Crash()
    {
        RigControl rig = await Started(persist: true);
        SoundModemChannel channel = Channel(27);

        ReceiveWindowApi.Handle(rig, channel.TransmitLease, "POST", """{"dialHz": 7052000, "seconds": 60}""");

        // What a SIGKILL during this window would leave for the next start-up to read back - the
        // same file and format proven generically above (An_Open_Window_Writes_Its_Restore_Target...).
        RigRestoreFile.Read(RestorePath, _fake.Endpoint, out _, out _)
            .Should().Be(new RigTuning(14_074_000, "USB", 2400));

        ReceiveWindowApi.Handle(rig, channel.TransmitLease, "POST", """{"release": true}""");
        File.Exists(RestorePath).Should().BeFalse();
    }

    [Fact]
    public async Task A_Station_Killed_During_A_Receive_Window_Puts_The_Rig_Back_At_Its_Next_Start()
    {
        RigControl first = await Started(persist: true);
        SoundModemChannel channel = Channel(28);
        ReceiveWindowApi.Handle(
            first, channel.TransmitLease, "POST", """{"dialHz": 7052000, "seconds": 60}""").Status.Should().Be(200);
        byte[] leftBehind = await File.ReadAllBytesAsync(RestorePath);

        // A SIGKILL never gets to put the rig back: what it leaves is the rig where the window put
        // it and the file the window wrote. Stopped cleanly here (which puts the rig back), then
        // both set back to exactly that, so the next start-up is the only thing that can restore it.
        await first.DisposeAsync();
        _fake.DialHz = 7_052_000;
        _fake.PassbandHz = ReceiveWindowApi.DefaultWidthHz;
        await File.WriteAllBytesAsync(RestorePath, leftBehind);

        RigControl next = Rig(persist: true);
        await next.StartAsync(CancellationToken.None);

        await Eventually(() => !next.HoldsTransmitter, "the restore is done on connecting", TimeSpan.FromSeconds(1));
        _said.Should().Contain(s => s.Contains("stopped during a tuning window last time"));
        _fake.DialHz.Should().Be(14_074_000);
        _fake.PassbandHz.Should().Be(2400);
        File.Exists(RestorePath).Should().BeFalse();
    }

    [Fact]
    public async Task A_Receive_Window_Holds_Kiss_Frames_And_Refuses_The_Key_Until_It_Ends()
    {
        // The receive window is an ordinary rig window, so everything RigControl.HoldsTransmitter
        // holds is held by it; MailcastRetuneTests.Every_Source_Of_Transmission_Is_Held_While_The_Rig_Is_On_The_Mailcast_Dial
        // goes through every source (KISS, idents, paging, the transmitter test, ARDOP) for any
        // window. This pins that a window opened here is one of them.
        RigControl rig = await Started();
        var channel = new SoundModemChannel(12000, randomSeed: 29);
        channel.AddModem(0, sink => ModemCatalog.Create("afsk1200", 12000, sink));
        channel.Csma.Persistence = 255;
        var line = new CountingPtt();
        var output = new Packet.SoundModem.Tests.Channel.FakeAudioOutput(12000);
        using var stop = new CancellationTokenSource();
        Task transmitter = channel.RunTransmitterAsync(output, rig.HoldTransmissions(channel, line), stop.Token);
        ReceiveWindowApi.Handle(
            rig, channel.TransmitLease, "POST", """{"dialHz": 7052000, "seconds": 60}""").Status.Should().Be(200);

        Task kiss = channel.EnqueueTransmit(
            0, Packet.SoundModem.Tests.Mailcast.MailcastSlotAudio.Ui("G8ABC", "GB7TST", "hello"u8));
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        rig.HoldsTransmitter.Should().BeTrue("idents read this, and are not sent while it is true");
        kiss.IsCompleted.Should().BeFalse("held, not sent and not dropped");
        line.Keys.Should().Be(0);
        Action key = rig.Guard(new CountingPtt()).Key;
        key.Should().Throw<TransmitterHeldException>().WithMessage("*for a receive window*");

        ReceiveWindowApi.Handle(rig, channel.TransmitLease, "POST", """{"release": true}""").Status.Should().Be(200);

        await kiss.WaitAsync(TimeSpan.FromSeconds(30));
        line.Keys.Should().Be(1);
        _fake.DialHz.Should().Be(14_074_000);
        await stop.CancelAsync();
        Func<Task> ended = () => transmitter;
        await ended.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_Rig_That_Is_Switched_Off_Is_A_Warning_And_Is_Picked_Up_When_It_Comes_On()
    {
        // rigctld answers, the rig behind it does not: every read is RPRT -5.
        _fake.RigOff = true;
        RigControl rig = Rig(plan: new RigTuning(7_049_450, "USB", 2400));

        (await rig.StartAsync(CancellationToken.None)).Should().BeFalse();
        rig.Connected.Should().BeFalse();
        _warned.Should().ContainSingle(w => w.Contains("the rig did not") && w.Contains("RPRT -5"));

        // Still retrying, quietly, with the backoff.
        await Eventually(() => _fake.Connections >= 3, "the watch keeps trying", TimeSpan.FromSeconds(1));
        _warned.Should().HaveCount(1);

        _fake.RigOff = false;
        await Eventually(() => rig.Connected && _fake.DialHz == 7_049_450, "the rig is picked up", TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_Rig_Switched_Off_Mid_Session_Does_Not_Stop_The_Watch()
    {
        RigControl rig = await Started(keysThroughRig: true);
        _fake.RigOff = true;
        await Eventually(() => rig.Snapshot().LastProblem is not null, "the poll should notice", TimeSpan.FromSeconds(5));

        // The watch is alive: it still acts on what is owed.
        _fake.RigOff = false;
        _fake.Kill();
        await Eventually(() => _fake.Connections == 2 && rig.Connected, "and still reconnects", TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_Rigctld_Started_With_Vfo_Is_Refused_Saying_Why()
    {
        _fake.VfoMode = true;
        RigControl rig = Rig();

        (await rig.StartAsync(CancellationToken.None)).Should().BeFalse();

        _warned.Should().ContainSingle(w => w.Contains("--vfo"));
        _fake.Sets.Should().BeEmpty("nothing is sent to a rigctld this station cannot speak to");
    }

    [Fact]
    public async Task A_Refused_Keyup_Is_Unkeyed_At_Once()
    {
        RigControl rig = await Started(keysThroughRig: true);
        _fake.RefusesKey = true;

        Action key = rig.KeyingPtt().Key;

        key.Should().Throw<RigctldException>();
        _fake.Ptt.Should().BeFalse("a T 1 that was refused may still have keyed the rig, so T 0 follows at once");
        _fake.Sets.Should().EndWith(["T 1", "T 0"]);
        rig.HoldsTransmitter.Should().BeFalse();
    }

    [Fact]
    public async Task An_Unkey_That_Is_Refused_Is_Owed_Retried_Every_Second_And_Holds_Everything_Until_It_Goes()
    {
        RigControl rig = await Started(keysThroughRig: true);
        IPttControl ptt = rig.KeyingPtt();
        ptt.Key();
        _fake.RefusesUnkey = true;

        Action unkey = ptt.Unkey;
        unkey.Should().Throw<RigctldException>();

        rig.Snapshot().UnkeyOwed.Should().BeTrue();
        rig.HoldsTransmitter.Should().BeTrue();
        Action keyAgain = ptt.Key;
        keyAgain.Should().Throw<TransmitterHeldException>().WithMessage("*unkey has not been confirmed*");
        rig.Tune(new RigTuning(7_052_000, "USB", 0), TimeSpan.FromSeconds(60), "mailcast")
            .Why.Should().Contain("unkey has not been confirmed");

        await Eventually(() => _warned.Count(w => w.Contains("the unkey was refused")) >= 3, "retried", TimeSpan.FromSeconds(1));
        _fake.Ptt.Should().BeTrue();

        _fake.RefusesUnkey = false;
        await Eventually(() => !rig.HoldsTransmitter, "the next retry unkeys", TimeSpan.FromSeconds(1));
        _fake.Ptt.Should().BeFalse();
        _said.Should().Contain(s => s.StartsWith("rig: unkeyed the radio after", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_First_Tune_That_Loses_Rigctld_Part_Way_Owes_The_Restore()
    {
        RigControl rig = await Started();
        _fake.DieOn = "F";

        RigTuneResult result = rig.Tune(new RigTuning(7_052_000, "LSB", 0), TimeSpan.FromSeconds(60), "mailcast");

        result.Outcome.Should().Be(RigTuneOutcome.Failed);
        _fake.Mode.Should().Be("LSB", "the mode went before the connection did");
        rig.Snapshot().RestoreOwed.Should().Be(new RigTuning(14_074_000, "USB", 2400));
        rig.HoldsTransmitter.Should().BeTrue();

        await Eventually(() => !rig.HoldsTransmitter, "the reconnect puts it back", TimeSpan.FromSeconds(1));
        _fake.Mode.Should().Be("USB");
        _fake.DialHz.Should().Be(14_074_000);
    }

    [Fact]
    public async Task An_Open_Window_Writes_Its_Restore_Target_Down_And_Removes_It_Once_Put_Back()
    {
        RigControl rig = await Started(persist: true);

        RigTuneResult result = rig.Tune(new RigTuning(7_052_000, "USB", 0), TimeSpan.FromSeconds(60), "mailcast");

        RigRestoreFile.Read(RestorePath, _fake.Endpoint, out _, out _).Should().Be(new RigTuning(14_074_000, "USB", 2400));
        result.Window!.Dispose();
        File.Exists(RestorePath).Should().BeFalse();
    }

    [Fact]
    public async Task A_Station_Killed_Mid_Window_Puts_The_Rig_Back_At_Start_Up_Before_The_Band_Plan()
    {
        // What a SIGKILL during a window leaves behind: the rig on the window's frequency, and the file.
        RigRestoreFile.Write(RestorePath, _fake.Endpoint, new RigTuning(7_049_450, "USB", 2400));
        _fake.DialHz = 7_052_000;
        _fake.Accepting = false;
        RigControl rig = Rig(plan: new RigTuning(7_049_450, "USB", 2400), persist: true);

        (await rig.StartAsync(CancellationToken.None)).Should().BeFalse();
        rig.HoldsTransmitter.Should().BeTrue("nothing is sent until the rig is back where it belongs");
        _said.Should().Contain(s => s.Contains("stopped during a tuning window last time"));

        _fake.Accepting = true;
        await Eventually(() => !rig.HoldsTransmitter, "the restore is done on connecting", TimeSpan.FromSeconds(1));
        _fake.DialHz.Should().Be(7_049_450);
        File.Exists(RestorePath).Should().BeFalse();
        _fake.Sets.Should().StartWith(["M USB 2400", "F 7049450"]);
    }

    [Fact]
    public async Task A_Restore_File_For_Another_Rigctld_Is_Ignored()
    {
        RigRestoreFile.Write(RestorePath, new RigctldEndpoint("10.0.0.9", 4532), new RigTuning(3_582_000, "LSB", 0));

        RigControl rig = await Started(persist: true);

        rig.HoldsTransmitter.Should().BeFalse();
        _fake.Sets.Should().BeEmpty();
        _warned.Should().ContainSingle(w => w.Contains("is for the rigctld at 10.0.0.9:4532"));
        File.Exists(RestorePath).Should().BeTrue("another station's promise is not this one's to delete");
    }

    [Fact]
    public async Task A_Dial_Moved_By_Hand_Is_Said_At_Most_Once_A_Minute()
    {
        await Started();

        _fake.DialHz = 14_075_000;
        await Eventually(() => _said.Any(s => s.Contains("changed outside")), "the first change is said", TimeSpan.FromSeconds(5));
        _fake.DialHz = 14_076_000;
        await Eventually(() => _fake.Commands.Count(c => c.Command == "f") >= 4, "polled again", TimeSpan.FromSeconds(5));
        _said.Count(s => s.Contains("changed outside")).Should().Be(1);

        _time.Advance(TimeSpan.FromMinutes(1));
        _fake.DialHz = 14_077_000;
        await Eventually(() => _said.Count(s => s.Contains("changed outside")) == 2, "a minute later it is said again", TimeSpan.FromSeconds(5));
        _said.Should().Contain(s => s.Contains("14.077000 MHz") && s.Contains("1 more changes since the last line"));
    }

    [Fact]
    public async Task No_Poll_Is_Made_While_A_Transmission_Is_Queued()
    {
        await Started();
        int reads = _fake.Commands.Count(c => c.Command == "f");
        _pending = true;

        for (int i = 0; i < 10; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(5);
        }

        _fake.Commands.Count(c => c.Command == "f").Should().Be(reads);
        _pending = false;
        await Eventually(() => _fake.Commands.Count(c => c.Command == "f") > reads, "polls resume", TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Changes_Are_Raised_As_The_Connection_And_A_Window_Come_And_Go()
    {
        RigControl rig = await Started();
        _changes.Should().ContainSingle(c => c.Connected && c.Window == null);

        RigTuneResult window = rig.Tune(new RigTuning(7_052_000, "USB", 0), TimeSpan.FromSeconds(60), "mailcast");
        _changes.Last().Window.Should().BeSameAs(window.Window);

        window.Window!.Dispose();
        _changes.Last().Window.Should().BeNull();
        _changes.Last().RestoreOwed.Should().BeNull();
        _changes.Last().Tuning!.DialHz.Should().Be(14_074_000);

        _fake.Accepting = false;
        _fake.Kill();
        await Eventually(() => !_changes.Last().Connected, "a drop is raised", TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Key_And_Unkey_Decide_From_The_Rig_Whether_To_Send_T()
    {
        // A rig that does not key through rigctld: the public Key marks the keyup and sends nothing.
        RigControl rig = await Started();
        rig.Key();
        _fake.Sets.Should().BeEmpty();
        _fake.Ptt.Should().BeFalse();
        rig.Snapshot().Keyed.Should().BeTrue();
        rig.Tune(new RigTuning(7_052_000, "USB", 0), TimeSpan.FromSeconds(60), "mailcast")
            .Outcome.Should().Be(RigTuneOutcome.Refused);
        rig.Unkey();
        _fake.Sets.Should().BeEmpty();
        rig.HoldsTransmitter.Should().BeFalse();
        Func<IPttControl> keying = rig.KeyingPtt;
        keying.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Key_And_Unkey_On_A_Rig_That_Keys_Through_Rigctld_Always_Send_T()
    {
        RigControl rig = await Started(keysThroughRig: true);

        rig.Key();
        _fake.Ptt.Should().BeTrue();
        rig.Unkey();
        _fake.Ptt.Should().BeFalse();
        _fake.Sets.Should().Equal(["T 0", "T 1", "T 0"]);
        Func<IPttControl> guard = () => rig.Guard(new NullPtt());
        guard.Should().Throw<InvalidOperationException>("a second keying line would key the radio twice");
    }

    [Fact]
    public async Task After_A_Start_Up_Restore_The_Band_Plan_Is_Checked_Too()
    {
        // The restore puts the rig back to where it was before the window; the config's band plan
        // has moved since, and takes effect at once.
        RigRestoreFile.Write(RestorePath, _fake.Endpoint, new RigTuning(7_049_450, "USB", 2400));
        _fake.DialHz = 7_052_000;
        RigControl rig = await Started(plan: new RigTuning(7_050_100, "USB", 2400), persist: true);

        rig.HoldsTransmitter.Should().BeFalse();
        _fake.Sets.Should().Equal(["M USB 2400", "F 7049450", "M USB 2400", "F 7050100"]);
        _fake.DialHz.Should().Be(7_050_100);
    }

    [Fact]
    public async Task A_Rigctld_That_Ignores_Chk_Vfo_Is_Taken_As_Too_Old_For_Vfo_And_Used()
    {
        _fake.IgnoresChkVfo = true;
        RigControl rig = Rig(replyTimeout: TimeSpan.FromSeconds(2));

        (await rig.StartAsync(CancellationToken.None)).Should().BeFalse();
        await Eventually(() => rig.Connected, "the next connection does not ask", TimeSpan.FromSeconds(1));

        _fake.Commands.Count(c => c.Command == "\\chk_vfo").Should().Be(1);
        _warned.Should().ContainSingle(w => w.Contains("did not answer \\chk_vfo"));
    }

    [Fact]
    public void Restore_Files_Are_Named_For_Their_Rigctld()
    {
        RigRestoreFile.NameFor(new RigctldEndpoint("127.0.0.1", 4532)).Should().Be("rig-restore-127.0.0.1-4532.json");
        RigRestoreFile.NameFor(new RigctldEndpoint("::1", 4533)).Should().Be("rig-restore-__1-4533.json");
    }

    [Fact]
    public async Task A_Huge_Number_Of_Seconds_Is_Capped_Rather_Than_Overflowing()
    {
        RigControl rig = await Started();

        (int status, JsonObject answer) = RigApi.Handle(
            rig, "/api/rig/tune", "POST", """{"dialHz": 7052000, "mode": "USB", "seconds": 1e300}""");

        status.Should().Be(200);
        answer["capped"]!.GetValue<bool>().Should().BeTrue();
        answer["seconds"]!.GetValue<double>().Should().Be(300);
    }

    [Theory]
    [InlineData("127.0.0.1:4532", "127.0.0.1", 4532)]
    [InlineData("localhost", "localhost", 4532)]
    [InlineData("rig.lan:4533", "rig.lan", 4533)]
    [InlineData("[::1]:4534", "::1", 4534)]
    [InlineData("[::1]", "::1", 4532)]
    public void An_Endpoint_Is_Read_As_Host_And_Port(string text, string host, int port)
    {
        RigctldEndpoint.TryParse(text, out _).Should().Be(new RigctldEndpoint(host, port));
    }

    [Theory]
    [InlineData("")]
    [InlineData(":4532")]
    [InlineData("host:0")]
    [InlineData("host:port")]
    [InlineData("::1:4532")]
    [InlineData("[::1")]
    public void An_Endpoint_That_Is_Not_One_Is_Refused(string text)
    {
        RigctldEndpoint.TryParse(text, out string why).Should().BeNull();
        why.Should().NotBeEmpty();
    }

    private sealed class CountingPtt : IPttControl
    {
        internal int Keys { get; private set; }

        public void Key() => Keys++;

        public void Unkey()
        {
        }
    }
}
