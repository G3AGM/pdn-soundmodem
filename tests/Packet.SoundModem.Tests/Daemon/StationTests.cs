using AwesomeAssertions;
using M0LTE.Radio.Audio;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Modems;

// The daemon's Station type and the library's Packet.SoundModem.Station NAMESPACE (the
// frequency-matching policy and friends) share a name, and from inside Packet.SoundModem.Tests
// the enclosing Packet.SoundModem is searched before this file's using directives are - so the
// bare name would find the namespace. The alias says which of the two is meant, once.
using DaemonStation = Packet.SoundModem.Daemon.Station;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// One station: an audio input, the channel its samples feed, the watches that decide the feed
/// has died, and the loop that turns between them. Carved out of the daemon's top-level
/// statements so a process can hold more than one of it. What is pinned here is what the live
/// stations depend on staying exactly as it was, plus the one thing that did change: a station
/// reports a fault and never ends the process itself.
/// </summary>
public class StationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pdnsm-station").FullName;
    private readonly List<string> _out = [];
    private readonly List<string> _errors = [];

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>An input the test drives: it delivers a block of one level, or nothing at all,
    /// and counts how many times it was asked. Paced like the real inputs, every one of which
    /// waits inside <c>Read</c> rather than returning 0 in a spin.</summary>
    private sealed class TestInput(int sampleRate, float level, bool deliver = true) : IAudioInput
    {
        private int _reads;

        public int SampleRate { get; } = sampleRate;

        public int Reads => Volatile.Read(ref _reads);

        /// <summary>The USB-card-unplugged family: <c>Read</c> throws instead of returning.</summary>
        public Exception? Death { get; set; }

        public int Read(Span<float> buffer)
        {
            Interlocked.Increment(ref _reads);
            if (Death is Exception death)
            {
                throw death;
            }

            Thread.Sleep(1);
            if (!deliver)
            {
                return 0;
            }

            buffer.Fill(level);
            return buffer.Length;
        }
    }

    /// <summary>The ALSA stall: a card that stops producing period interrupts leaves
    /// <c>Read</c> blocked for ever, so cancellation never reaches the loop and the orderly
    /// shutdown cannot run. The one case a station is entitled to call itself stalled.</summary>
    private sealed class WedgedInput(int sampleRate) : IAudioInput, IDisposable
    {
        private readonly ManualResetEventSlim _entered = new(false);
        private readonly ManualResetEventSlim _release = new(false);

        public int SampleRate { get; } = sampleRate;

        public void WaitUntilWedged() =>
            _entered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the loop should be in Read");

        public void Release() => _release.Set();

        public int Read(Span<float> buffer)
        {
            _entered.Set();
            _release.Wait();
            return 0;
        }

        public void Dispose()
        {
            _release.Set();
            _entered.Dispose();
            _release.Dispose();
        }
    }

    /// <summary>An input that plays one recording out at the loop's block size and then goes
    /// quiet, so a station can be driven with real modulated audio.</summary>
    private sealed class RecordingInput(int sampleRate, float[] audio) : IAudioInput
    {
        private int _position;

        public int SampleRate { get; } = sampleRate;

        public int Read(Span<float> buffer)
        {
            Thread.Sleep(1);
            buffer.Clear();
            int taken = Math.Min(buffer.Length, audio.Length - _position);
            if (taken > 0)
            {
                audio.AsSpan(_position, taken).CopyTo(buffer);
                _position += taken;
            }

            return buffer.Length;
        }
    }

    // Written by the loop thread and read by the test thread, so both ends take the list's lock.
    private StationJournal Journal(string tag = "") => new(
        tag,
        line => { lock (_out) { _out.Add(line); } },
        line => { lock (_errors) { _errors.Add(line); } });

    private List<string> Errors()
    {
        lock (_errors)
        {
            return [.. _errors];
        }
    }

    private async Task UntilAsync(Func<bool> condition, string because)
    {
        using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition())
        {
            giveUp.Token.ThrowIfCancellationRequested();
            await Task.Delay(5, giveUp.Token);
        }

        condition().Should().BeTrue(because);
    }

    /// <summary>The CT 146 shape: a receive-only UberSDR station at 12 kHz, dead-feed thresholds
    /// left at the device family's own defaults, which is how the live one runs.</summary>
    private StationOptions UberSdrOptions(
        IAudioInput input, StationJournal journal, TimeProvider? clock = null) => new()
        {
            Channel = new SoundModemChannel(12000),
            Input = input,
            DspRate = 12000,
            Journal = journal,
            DeviceKind = DeadFeedDevice.UberSdr,
            TimeProvider = clock ?? TimeProvider.System,
        };

    /// <summary>Runs the station on a thread of its own, as a host with more than one would.</summary>
    private static Task RunAsync(DaemonStation station)
        => Task.Factory.StartNew(
            station.Run,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    [Fact]
    public async Task A_Station_Runs_Its_Receive_Loop_Until_Its_Token_Is_Cancelled()
    {
        var input = new TestInput(12000, level: 0.1f);
        using var stopping = new CancellationTokenSource();
        using var station = new DaemonStation(UberSdrOptions(input, Journal()), stopping.Token);

        Task running = RunAsync(station);

        // The loop is turning: it keeps asking the input for audio.
        await UntilAsync(() => input.Reads >= 5, "the loop keeps asking the input for audio");
        running.IsCompleted.Should().BeFalse("nothing has asked the station to stop");

        await stopping.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        Errors().Should().BeEmpty("a station stopped by its host has nothing to report");
        _out.Should().BeEmpty("a receive loop that is working says nothing at all");
    }

    [Fact]
    public async Task A_Starved_Station_Faults_Instead_Of_Exiting_The_Process()
    {
        var clock = new FakeTimeProvider();
        // An input that never delivers: the hung-WebSocket case, where Read returns 0 forever
        // and the silence watch has nothing to look at.
        var input = new TestInput(12000, level: 0, deliver: false);
        using var stopping = new CancellationTokenSource();
        using var station = new DaemonStation(UberSdrOptions(input, Journal(), clock), stopping.Token);
        var faults = new List<StationFault>();
        station.Faulted += faults.Add;

        Task running = RunAsync(station);
        await UntilAsync(() => input.Reads >= 2, "the loop is turning");

        clock.Advance(TimeSpan.FromSeconds(30));

        faults.Should().ContainSingle().Which.Should().Be(new StationFault(
            "receive feed starved: an open session delivered no audio for 30 s - a hung stream "
            + "- restarting to reconnect afresh",
            Stalled: false));

        // The station stopped itself, without the host having to ask - and without ending the
        // process, which is what this whole extraction exists to make possible.
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_Station_That_Stopped_Tidily_Does_Not_Report_A_Stalled_Shutdown()
    {
        // The grace timer exists for a loop that CANNOT be brought down. This one came down on
        // its own the moment Read returned, so the grace period passing means nothing, and a
        // host told otherwise would end a whole process because one receiver went quiet.
        var clock = new FakeTimeProvider();
        var input = new TestInput(12000, level: 0, deliver: false);
        using var stopping = new CancellationTokenSource();
        using var station = new DaemonStation(UberSdrOptions(input, Journal(), clock), stopping.Token);
        var faults = new List<StationFault>();
        station.Faulted += faults.Add;

        Task running = RunAsync(station);
        await UntilAsync(() => input.Reads >= 2, "the loop is turning");

        clock.Advance(TimeSpan.FromSeconds(30));
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        clock.Advance(TimeSpan.FromMinutes(5));

        faults.Should().ContainSingle("the starvation is the only thing that happened");
        faults[0].Stalled.Should().BeFalse();
    }

    [Fact]
    public async Task A_Station_Wedged_Inside_Read_Reports_A_Stalled_Shutdown()
    {
        // The ALSA stall, and the only case that earns the second fault: cancellation cannot
        // reach a loop sitting inside a Read that never returns, so after the grace period the
        // station says so and the host decides what that costs. The station still does not exit.
        var clock = new FakeTimeProvider();
        using var input = new WedgedInput(12000);
        using var stopping = new CancellationTokenSource();
        StationOptions options = UberSdrOptions(input, Journal(), clock) with
        {
            DeviceKind = DeadFeedDevice.Alsa,
        };
        using var station = new DaemonStation(options, stopping.Token);
        var faults = new List<StationFault>();
        station.Faulted += faults.Add;

        Task running = RunAsync(station);
        input.WaitUntilWedged();

        clock.Advance(TimeSpan.FromSeconds(30));
        faults.Should().ContainSingle().Which.Stalled.Should().BeFalse("the shutdown has only just been asked for");
        running.IsCompleted.Should().BeFalse("Read has not returned, so the loop cannot get out");

        clock.Advance(TimeSpan.FromSeconds(15));
        faults.Should().HaveCount(2);
        faults[1].Should().Be(new StationFault(
            "receive feed starved: the orderly shutdown stalled (the input's Read is blocked) - "
            + "exiting hard so the service restarts",
            Stalled: true));

        input.Release();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_Dead_Feed_Faults_The_Station_And_Says_Which_One()
    {
        // Exact zeros at full rate: an UberSDR whose own SDR feed has died while it goes on
        // streaming. One second of them is a dead feed for the purposes of this test.
        var input = new TestInput(12000, level: 0);
        StationJournal journal = Journal("m9psy-1");
        StationOptions options = UberSdrOptions(input, journal) with
        {
            DeadFeed = new DeadFeedConfig { SilenceSeconds = 1, StarvationSeconds = 0 },
        };
        using var stopping = new CancellationTokenSource();
        using var station = new DaemonStation(options, stopping.Token);

        // Journalling a fault is the host's, which is what puts the station's tag on it - and
        // is how fifty stations in one journal stay readable.
        station.Faulted += fault => journal.WriteError(fault.Reason);

        await RunAsync(station).WaitAsync(TimeSpan.FromSeconds(10));

        Errors().Should().ContainSingle().Which.Should().Be(
            "m9psy-1: receive feed dead: 1 s of unbroken digital silence from the receiver's IQ "
            + "stream - its SDR feed has likely died - restarting to reconnect afresh");
    }

    [Fact]
    public async Task A_Stations_Journal_Lines_Carry_Its_Tag_When_It_Has_One()
    {
        const string Excuse = "receive feed silent because this station stood down from a "
            + "contested slice, which is deliberate - not restarting.";

        // Silence the station itself asked for: journalled, and the loop keeps turning. Run
        // twice, once for a station with a tag and once for the only station in the process,
        // which is the case every live node station is in.
        foreach (string tag in new[] { "m9psy-1", "" })
        {
            lock (_errors)
            {
                _errors.Clear();
            }

            var input = new TestInput(12000, level: 0);
            StationOptions options = UberSdrOptions(input, Journal(tag)) with
            {
                DeadFeed = new DeadFeedConfig { SilenceSeconds = 1, StarvationSeconds = 0 },
                SilenceExcuse = () => Excuse,
            };
            using var stopping = new CancellationTokenSource();
            using var station = new DaemonStation(options, stopping.Token);
            station.Faulted += fault => throw new InvalidOperationException(
                $"an excused silence is not a fault: {fault.Reason}");

            Task running = RunAsync(station);
            await UntilAsync(() => Errors().Count > 0, "the excused silence is journalled");

            await stopping.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(5));

            Errors()[0].Should().Be(tag.Length == 0 ? Excuse : $"m9psy-1: {Excuse}");
        }
    }

    [Fact]
    public async Task The_Health_Poll_Reports_Lost_Audio_And_Unwritten_Rows()
    {
        // Every ten seconds while audio is flowing, and in this order: what the sound device
        // lost, then whatever else the host has hung on the poll (the frame log's and the
        // survey's dropped-write counters, which were dead counters until this read them).
        var clock = new FakeTimeProvider();
        var input = new TestInput(12000, level: 0.1f);
        int captureXruns = 0;
        int playbackXruns = 0;
        string? logDrops = null;
        StationOptions options = UberSdrOptions(input, Journal("m9psy-1"), clock) with
        {
            XrunCounters = () => (captureXruns, playbackXruns),
            HealthChecks = [() => logDrops],
        };
        using var stopping = new CancellationTokenSource();
        using var station = new DaemonStation(options, stopping.Token);

        Task running = RunAsync(station);
        await UntilAsync(() => input.Reads >= 2, "the loop is turning");

        clock.Advance(TimeSpan.FromSeconds(10));
        await UntilAsync(() => input.Reads >= 10, "the poll has come round with nothing to say");
        Errors().Should().BeEmpty("a healthy station's poll is silent");

        captureXruns = 3;
        playbackXruns = 1;
        logDrops = "frame log: 2 frames dropped unwritten (2 total) - the disk cannot keep up, "
            + "is full, or is unwritable";
        clock.Advance(TimeSpan.FromSeconds(10));
        await UntilAsync(() => Errors().Count >= 2, "both probes have something to say");

        await stopping.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        List<string> errors = Errors();
        errors[0].Should().StartWith(
            "m9psy-1: audio: 3 capture overruns, 1 playback underrun (4 since start)");
        errors[1].Should().Be($"m9psy-1: {logDrops}");
        errors.Should().HaveCount(2, "an xrun is reported once, as a delta, not every poll");
    }

    [Fact]
    public async Task An_On_Demand_Station_With_No_Session_Is_Not_Starved()
    {
        // The 40 m monitor's whole idea: with nobody watching there is no session, so there is
        // nothing to be starved of. Without the stand-down the 30 s starvation default would
        // restart an idle daemon every half minute and hammer somebody else's receiver.
        var clock = new FakeTimeProvider();
        var input = new TestInput(12000, level: 0, deliver: false);
        StationOptions options = UberSdrOptions(input, Journal(), clock) with
        {
            SessionLive = () => false,
        };
        using var stopping = new CancellationTokenSource();
        using var station = new DaemonStation(options, stopping.Token);
        var faults = new List<StationFault>();
        station.Faulted += faults.Add;

        Task running = RunAsync(station);
        await UntilAsync(() => input.Reads >= 2, "the loop is turning");

        clock.Advance(TimeSpan.FromMinutes(10));
        faults.Should().BeEmpty("quiet with nobody watching is the arrangement, not a fault");

        await stopping.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_Single_Station_Writes_The_Same_Lines_As_Before()
    {
        // The CT 146 configuration's shape - "device": "ubersdr:...", one station in the
        // process, dead-feed thresholds at the UberSDR family's 30 s defaults - and every
        // sentence it can write. Byte for byte what Program.cs printed before there was a
        // Station type, and with no tag, because the operator reading this journal has learned
        // what these lines look like and the tests that pin them are the proof they have not
        // moved.
        var clock = new FakeTimeProvider();

        // A feed that stops delivering at all, and then a shutdown that cannot get out of the
        // blocked Read it is sitting in.
        using var wedged = new WedgedInput(12000);
        await RunUntilStoppedAsync(
            UberSdrOptions(wedged, Journal(), clock),
            running: async () =>
            {
                wedged.WaitUntilWedged();
                clock.Advance(TimeSpan.FromSeconds(30));
                clock.Advance(TimeSpan.FromSeconds(15));
                await UntilAsync(() => Errors().Count >= 2, "starved, then stalled");
                wedged.Release();
            });

        // A feed that keeps delivering exact zeros.
        var silent = new TestInput(12000, level: 0);
        await RunUntilStoppedAsync(UberSdrOptions(silent, Journal(), clock));

        // And the input dying under the loop.
        var dead = new TestInput(12000, level: 0.1f)
        {
            Death = new InvalidOperationException("snd_pcm_readi: No such device"),
        };
        await RunUntilStoppedAsync(UberSdrOptions(dead, Journal(), clock));

        Errors().Should().Equal(
        [
            "receive feed starved: an open session delivered no audio for 30 s - a hung stream "
                + "- restarting to reconnect afresh",
            "receive feed starved: the orderly shutdown stalled (the input's Read is blocked) - "
                + "exiting hard so the service restarts",
            "receive feed dead: 30 s of unbroken digital silence from the receiver's IQ stream "
                + "- its SDR feed has likely died - restarting to reconnect afresh",
            "receive feed dead: the input device failed (snd_pcm_readi: No such device) - "
                + "restarting to reopen it",
        ]);
        _out.Should().BeEmpty("none of this belongs on stdout");
    }

    /// <summary>Runs one station to its fault, journalling it the way the daemon's host does.</summary>
    private async Task RunUntilStoppedAsync(StationOptions options, Func<Task>? running = null)
    {
        using var stopping = new CancellationTokenSource();
        using var station = new DaemonStation(options, stopping.Token);
        station.Faulted += fault => options.Journal.WriteError(fault.Reason);

        Task loop = RunAsync(station);
        if (running is not null)
        {
            await running();
        }

        await loop.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Two_Stations_In_One_Process_Keep_Separate_Frame_Logs()
    {
        // Flavour B writes frames-<slug>.db per station under one directory. Two stations run
        // here at once, each with its own channel, its own modem, its own recording and its own
        // log, wired the way the daemon wires them - and what one hears must not turn up in the
        // other's file.
        string firstPath = System.IO.Path.Combine(_dir, "frames-m9psy-1.db");
        string secondPath = System.IO.Path.Combine(_dir, "frames-g4eyr.db");
        FrameLog first = FrameLog.Open(firstPath);
        FrameLog second = FrameLog.Open(secondPath);

        using var stoppingOne = new CancellationTokenSource();
        using var stoppingTwo = new CancellationTokenSource();
        StationOptions optionsOne = Listening(Modulate("M0LTE"), Journal("m9psy-1"));
        StationOptions optionsTwo = Listening(Modulate("G4EYR"), Journal("g4eyr"));
        using var stationOne = new DaemonStation(optionsOne, stoppingOne.Token);
        using var stationTwo = new DaemonStation(optionsTwo, stoppingTwo.Token);
        stationOne.Channel.Should().NotBeSameAs(stationTwo.Channel);

        // Exactly the daemon's wiring: every frame this station's channel decodes goes to this
        // station's own log, and to no other's.
        stationOne.Channel.FrameReceivedWithQuality += (sub, frame, quality) =>
            first.Record(sub, frame, quality, audioHz: 1700, rfHz: 7_050_300);
        stationTwo.Channel.FrameReceivedWithQuality += (sub, frame, quality) =>
            second.Record(sub, frame, quality, audioHz: 1700, rfHz: 7_051_600);

        Task runningOne = RunAsync(stationOne);
        Task runningTwo = RunAsync(stationTwo);
        await UntilAsync(
            () => first.Recent(10).Count > 0 && second.Recent(10).Count > 0,
            "each station decoded its own recording into its own log");

        await stoppingOne.CancelAsync();
        await stoppingTwo.CancelAsync();
        await Task.WhenAll(runningOne, runningTwo).WaitAsync(TimeSpan.FromSeconds(10));

        // One station closing must not reach across and shut every other station's connection
        // pools, which is what a process-wide ClearAllPools did - and there are two pools per
        // log, because the backlog reads above opened a read-only one.
        await first.DisposeAsync();

        second.Record(0, Frame("M0LTE-2"), Quality(), audioHz: 1700, rfHz: 7_051_600);
        await second.DisposeAsync();

        ReadSources(firstPath).Should().Equal(["M0LTE"]);
        ReadSources(secondPath).Should().Equal(["G4EYR", "M0LTE-2"]);
    }

    /// <summary>A station listening to one recording on an AFSK1200 modem, with the dead-feed
    /// watches off: the recording runs out and the silence afterwards is the test's doing.</summary>
    private StationOptions Listening(float[] audio, StationJournal journal)
    {
        StationOptions options = UberSdrOptions(new RecordingInput(12000, audio), journal) with
        {
            DeadFeed = new DeadFeedConfig { SilenceSeconds = 0, StarvationSeconds = 0 },
        };
        options.Channel.AddModem(0, sink => new Afsk1200Modem(12000, sink));
        return options;
    }

    /// <summary>One AX.25 UI frame as audio, through the same modem that will hear it.</summary>
    private static float[] Modulate(string from)
    {
        var modulator = new Afsk1200Modem(12000, _ => { });
        return modulator.Modulate(Frame(from), txDelayMilliseconds: 100);
    }

    /// <summary>An input that follows a script, one entry per <c>Read</c>: deliver a block whose
    /// samples all carry the read's number, or deliver nothing; and a loss counter the test
    /// moves, as a sound card's overrun count or a radio's lost-packet count moves.</summary>
    private sealed class ScriptedInput(int sampleRate, bool[] delivers) : IAudioInput
    {
        private int _reads;
        private long _lost;

        public int SampleRate { get; } = sampleRate;

        public int Reads => Volatile.Read(ref _reads);

        public long Lost => Volatile.Read(ref _lost);

        /// <summary>The read on which the loss counter moves, or -1 for never.</summary>
        public int LoseOnRead { get; init; } = -1;

        public int Read(Span<float> buffer)
        {
            int read = Interlocked.Increment(ref _reads) - 1;
            Thread.Sleep(1);
            if (read == LoseOnRead)
            {
                Interlocked.Increment(ref _lost);
            }

            if (read >= delivers.Length || !delivers[read])
            {
                return 0;
            }

            buffer.Fill(read + 1);
            return buffer.Length;
        }
    }

    /// <summary>Runs a scripted input through a station and returns what the channel saw, in
    /// order: "lost" for each loss it was told of, and the read number of each block.</summary>
    private async Task<List<string>> ChannelSequenceAsync(ScriptedInput input, int reads)
    {
        var channel = new SoundModemChannel(12000);
        var seen = new List<string>();
        channel.ReceiveAudioLost += () => { lock (seen) { seen.Add("lost"); } };
        channel.AddReceiveTap(samples => { lock (seen) { seen.Add(((int)samples[0]).ToString(System.Globalization.CultureInfo.InvariantCulture)); } });
        using var stopping = new CancellationTokenSource();
        using var station = new DaemonStation(
            UberSdrOptions(input, Journal()) with
            {
                Channel = channel,
                InputLossCount = () => input.Lost,
            },
            stopping.Token);

        Task running = RunAsync(station);
        await UntilAsync(() => input.Reads >= reads, "the loop has read the whole script");
        await stopping.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        lock (seen)
        {
            return [.. seen];
        }
    }

    [Fact]
    public async Task Audio_The_Input_Says_It_Lost_Is_Told_To_The_Channel_Before_The_Next_Block()
    {
        var input = new ScriptedInput(12000, [true, true, true, true]) { LoseOnRead = 2 };

        List<string> seen = await ChannelSequenceAsync(input, 4);

        seen.Should().StartWith(["1", "2", "lost", "3", "4"],
            "the overrun happened during the third read, so the third block follows the hole");
        seen.Count(s => s == "lost").Should().Be(1);
    }

    [Fact]
    public async Task An_Input_That_Stalled_And_Came_Back_Is_Told_To_The_Channel_Once()
    {
        // Two reads with nothing (a Flex or a web receiver that stopped delivering), then audio.
        var input = new ScriptedInput(12000, [true, false, false, true, true]);

        List<string> seen = await ChannelSequenceAsync(input, 5);

        seen.Should().StartWith(["1", "lost", "4", "5"],
            "whatever the input should have delivered while it was stalled is gone");
        seen.Count(s => s == "lost").Should().Be(1, "one hole, however many empty reads it took");
    }

    [Fact]
    public async Task A_Loss_Counter_That_Was_Already_Running_At_Start_Up_Is_Not_A_Loss()
    {
        var input = new ScriptedInput(12000, [true, true, true]);
        const long already = 7;
        var channel = new SoundModemChannel(12000);
        int losses = 0;
        channel.ReceiveAudioLost += () => Interlocked.Increment(ref losses);
        using var stopping = new CancellationTokenSource();
        using var station = new DaemonStation(
            UberSdrOptions(input, Journal()) with { Channel = channel, InputLossCount = () => already },
            stopping.Token);

        Task running = RunAsync(station);
        await UntilAsync(() => input.Reads >= 3, "the loop has read the script");
        await stopping.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        Volatile.Read(ref losses).Should().Be(0, "overruns from before the station started are not this stream's");
    }

    /// <summary>An AX.25 UI frame, so the log has a real callsign to file the row under.</summary>
    private static byte[] Frame(string from) =>
        Packet.SoundModem.Waterfall.Ax25UiFrame.Build(from, "GB7RDG", new byte[8]);

    private static FrameQuality Quality() =>
        new("bpsk300-il2pc", FrameBytes: 32, CorrectedBytes: 0, CrcValid: true,
            FrequencyOffsetHz: null, EmphasisDb: null);

    private static List<string> ReadSources(string path)
    {
        var sources = new List<string>();
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using SqliteCommand read = connection.CreateCommand();
            read.CommandText = "SELECT source FROM frames ORDER BY id";
            using SqliteDataReader reader = read.ExecuteReader();
            while (reader.Read())
            {
                sources.Add(reader.GetString(0));
            }

            SqliteConnection.ClearPool(connection);
        }

        return sources;
    }
}
