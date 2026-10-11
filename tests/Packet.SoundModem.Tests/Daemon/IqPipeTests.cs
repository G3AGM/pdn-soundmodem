using System.Buffers.Binary;
using AwesomeAssertions;
using M0LTE.Radio.Audio;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Iq;
using Packet.SoundModem.Ota;
using Packet.SoundModem.Tests.Iq;
using DaemonStation = Packet.SoundModem.Daemon.Station;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The <c>iqpipe:</c> device (issue #597): raw IQ read from a child process, a FIFO or standard
/// input in each of the four formats, a child that dies restarted and its restart marked as a
/// gap, a retune that restarts the command at a new centre, a stall measured against the clock,
/// and a stalled feed seen by the station's dead-feed watch. Real child processes through
/// <c>/bin/sh</c> where the process boundary is the point; a scripted feed on a fake clock where
/// the timing is.
/// </summary>
public sealed class IqPipeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pdnsm-iqpipe").FullName;
    private readonly List<string> _lines = [];
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

    // ---- formats ----

    [Theory]
    [InlineData(IqSampleFormat.Cu8)]
    [InlineData(IqSampleFormat.Cs8)]
    [InlineData(IqSampleFormat.Cs16)]
    [InlineData(IqSampleFormat.Cf32)]
    public void Each_Format_Is_Read_Bit_Exact_From_A_Recorded_File(IqSampleFormat format)
    {
        (byte[] bytes, float[] expected) = Recording(format);
        string file = Path.Combine(_dir, $"capture.{IqSampleFormats.Name(format)}");
        File.WriteAllBytes(file, bytes);

        using var source = Source(Command($"cat '{file}'"), format, rate: 48_000, maxYoungRuns: 1);
        source.StartNow();
        float[] read = ReadToEnd(source);

        read.Length.Should().Be(expected.Length);
        for (int k = 0; k < expected.Length; k++)
        {
            BitConverter.SingleToInt32Bits(read[k]).Should().Be(
                BitConverter.SingleToInt32Bits(expected[k]), $"value {k} of {IqSampleFormats.Name(format)}");
        }
    }

    [Fact]
    public void The_Edges_Of_Each_Format_Are_Full_Scale()
    {
        var out2 = new float[2];
        IqSampleFormats.Convert(IqSampleFormat.Cu8, [0, 255], out2);
        out2.Should().Equal(-1f, 1f);
        IqSampleFormats.Convert(IqSampleFormat.Cs8, [0x80, 0x00], out2);
        out2.Should().Equal(-1f, 0f);
        IqSampleFormats.Convert(IqSampleFormat.Cs16, [0x00, 0x80, 0xFF, 0x7F], out2);
        out2.Should().Equal(-1f, 32767f / 32768f);
    }

    [Fact]
    public void A_Pair_Split_Across_Reads_Is_Joined_And_Not_Dropped()
    {
        (byte[] bytes, float[] expected) = Recording(IqSampleFormat.Cs16);
        var feed = new ScriptedFeed();
        for (int k = 0; k < bytes.Length; k += 3)
        {
            feed.Chunks.Enqueue((bytes[k..Math.Min(bytes.Length, k + 3)], TimeSpan.Zero));
        }

        using var source = Source(new ScriptedLauncher(feed), IqSampleFormat.Cs16, rate: 48_000, maxYoungRuns: 1);
        ReadToEnd(source).Should().Equal(expected);
    }

    [Theory]
    [InlineData("cu8", IqSampleFormat.Cu8)]
    [InlineData("CS8", IqSampleFormat.Cs8)]
    [InlineData(" cs16 ", IqSampleFormat.Cs16)]
    [InlineData("Cf32", IqSampleFormat.Cf32)]
    public void A_Format_Is_Read_By_Name_In_Any_Case(string text, IqSampleFormat format)
    {
        IqSampleFormats.TryParse(text, out IqSampleFormat parsed).Should().BeTrue();
        parsed.Should().Be(format);
    }

    // ---- the child process ----

    [Fact]
    public void A_Command_Is_Given_The_Centre_And_The_Rate_In_Whole_Hertz()
    {
        ChildProcessIqFeedLauncher.Expand("rx_sdr -f {frequency} -s {rate} -", 144_775_000.4, 960_000)
            .Should().Be("rx_sdr -f 144775000 -s 960000 -");
    }

    [Fact]
    public void A_Child_That_Dies_Is_Restarted_And_Each_Restart_Is_A_Gap()
    {
        // 1000 pairs a run, then exit 3 with a word on standard error.
        using var source = Source(
            Command("sh -c 'echo boom >&2; head -c 2000 /dev/zero; exit 3'"), IqSampleFormat.Cu8, rate: 48_000, maxYoungRuns: 3);
        string? lost = null;
        source.Lost += reason => lost = reason;
        source.StartNow();

        var gapsSeen = new List<long>();
        long floats = 0;
        var buffer = new float[4096];
        long lastLost = 0;
        int got;
        while ((got = source.Read(buffer)) > 0)
        {
            floats += got;
            if (source.SamplesLost != lastLost)
            {
                gapsSeen.Add(source.SamplesLost - lastLost);
                lastLost = source.SamplesLost;
            }
        }

        floats.Should().Be(3 * 2000, "three runs of 1000 pairs each were read whole");
        gapsSeen.Should().HaveCount(2, "the two restarts are each a gap before the run that followed");
        gapsSeen.Should().OnlyContain(g => g >= 1);
        lost.Should().Contain("3 runs in a row");
        source.IsLost.Should().BeTrue();
        Errors().Should().Contain(line => line.Contains("sh exited with code 3") && line.Contains("boom"),
            "the journal says how the child ended and its last words");
        Lines().Should().Contain(line => line.Contains("iqpipe: sh: boom"), "standard error is the journal's");
    }

    [Fact]
    public void Restarting_The_Child_Leaks_No_Descriptors()
    {
        static int Open() => Directory.GetFiles("/proc/self/fd").Length;
        using var source = Source(Command("head -c 200 /dev/zero"), IqSampleFormat.Cu8, rate: 48_000, maxYoungRuns: 30);
        source.StartNow();
        var buffer = new float[4096];
        source.Read(buffer).Should().BeGreaterThan(0);
        int before = Open();

        ReadToEnd(source);

        // Two pipes a run, 29 more runs: a leak would be dozens. Other tests' threads open and
        // close their own, so a few either way is noise.
        (Open() - before).Should().BeLessThan(10);
    }

    [Fact]
    public async Task A_Retune_Restarts_The_Command_At_The_New_Centre_And_Is_Reported_On_The_Next_Block()
    {
        using var source = Source(
            Command("echo tuned to {frequency} >&2; exec cat /dev/zero"), IqSampleFormat.Cu8, rate: 48_000,
            maxYoungRuns: 3, centreHz: 144_775_000);
        source.Tunes.Should().BeTrue();
        source.StartNow();

        var buffer = new float[4096];
        source.Read(buffer).Should().BeGreaterThan(0);
        source.TuningGeneration.Should().Be(0);
        source.CentreFrequencyHz.Should().Be(144_775_000);

        source.RetuneTo(145_000_000);
        using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await Task.Run(() =>
        {
            while (source.TuningGeneration == 0)
            {
                giveUp.Token.ThrowIfCancellationRequested();
                source.Read(buffer).Should().BeGreaterThan(0);
            }
        });

        source.TuningGeneration.Should().Be(1);
        source.CentreFrequencyHz.Should().Be(145_000_000);
        source.SamplesLost.Should().BeGreaterThan(0, "the time the restart took is a gap");
        Lines().Should().Contain(line => line.Contains("tuned to 145000000"));
        Errors().Should().BeEmpty("a retune is not a failure");
    }

    [Fact]
    public void A_Fifo_Or_Standard_Input_Cannot_Be_Retuned()
    {
        using var source = Source(new FifoIqFeedLauncher(Path.Combine(_dir, "never")), IqSampleFormat.Cu8, 48_000, 1);
        source.Tunes.Should().BeFalse();
        FluentActions.Invoking(() => source.RetuneTo(1e6)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task A_Command_That_Cannot_Run_Is_Given_Up_After_Its_Young_Runs()
    {
        using var source = Source(Command("exec /nonexistent/rx_sdr"), IqSampleFormat.Cf32, rate: 48_000, maxYoungRuns: 3);
        source.StartNow();
        int got = await Task.Run(() => source.Read(new float[64])).WaitAsync(TimeSpan.FromSeconds(20));
        got.Should().Be(0);
        source.IsLost.Should().BeTrue();
        Errors().Should().Contain(line => line.Contains("exited with code 127"));
    }

    [Fact]
    public async Task Disposing_Kills_The_Child_And_Ends_A_Blocked_Read()
    {
        using var source = Source(Command("exec sleep 600"), IqSampleFormat.Cu8, rate: 48_000, maxYoungRuns: 3);
        source.StartNow();
        Task<int> reading = Task.Run(() => source.Read(new float[64]));
        await Task.Delay(200);
        reading.IsCompleted.Should().BeFalse("nothing has been written");

        source.Dispose();
        (await reading.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(0);
        Errors().Should().BeEmpty("a stop is not a failure");
    }

    // ---- the FIFO ----

    [Fact]
    public async Task A_Fifo_Writer_That_Goes_And_Another_That_Comes_Is_A_Gap_Not_A_Splice()
    {
        string fifo = Path.Combine(_dir, "iq");
        PipeAudio.EnsureFifo(fifo);
        var source = Source(new FifoIqFeedLauncher(fifo), IqSampleFormat.Cu8, rate: 10_000, maxYoungRuns: 5);
        var heard = new List<(int Floats, long Lost)>();
        Task reader = Task.Run(() =>
        {
            var buffer = new float[4096];
            int got;
            while ((got = source.Read(buffer)) > 0)
            {
                lock (heard)
                {
                    heard.Add((got, source.SamplesLost));
                }
            }
        });

        await WriteAsync(fifo, new byte[1000]);
        await UntilAsync(() => Heard(heard) >= 1000, "the first writer's 500 pairs are read");
        await Task.Delay(300);
        await WriteAsync(fifo, new byte[1000]);
        await UntilAsync(() => Heard(heard) >= 2000, "the second writer's are read too");

        long lost;
        lock (heard)
        {
            lost = heard[^1].Lost;
        }

        lost.Should().BeGreaterThan(2000, "300 ms or more passed between the writers, at 10 kS/s");

        // The source waits in open for a third writer; disposing wakes it.
        source.Dispose();
        await reader.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // ---- gaps and the rate, against a fake clock ----

    [Fact]
    public void A_Stall_That_Leaves_The_Feed_Behind_The_Clock_Is_A_Gap_Of_The_Shortfall()
    {
        var clock = new FakeTimeProvider();
        var feed = new ScriptedFeed(clock);
        // 0.1 s of IQ, then a read that waits 2 s for the next 0.1 s.
        feed.Chunks.Enqueue((new byte[2 * 1000], TimeSpan.Zero));
        feed.Chunks.Enqueue((new byte[2 * 1000], TimeSpan.FromSeconds(2)));
        using var source = Source(new ScriptedLauncher(feed), IqSampleFormat.Cu8, rate: 10_000, maxYoungRuns: 1, clock: clock);

        var buffer = new float[1 << 16];
        source.Read(buffer).Should().Be(2000);
        source.SamplesLost.Should().Be(0);
        source.Read(buffer).Should().Be(2000);
        source.SamplesLost.Should().Be(18_000, "2 s at 10 kS/s, less the 0.1 s the feed was ahead and the 0.1 s the wait brought");
    }

    [Fact]
    public void A_Feed_That_Pauses_And_Catches_Up_Is_Not_A_Gap()
    {
        var clock = new FakeTimeProvider();
        var feed = new ScriptedFeed(clock);
        // 0.1 s, then a 2 s pause, then the 2.1 s it owes in one go: far more than one read takes,
        // the rest waiting in the pipe. Nothing was lost; it was late.
        feed.Chunks.Enqueue((new byte[2 * 1000], TimeSpan.Zero));
        feed.Chunks.Enqueue((new byte[2 * 21_000], TimeSpan.FromSeconds(2)));
        using var source = Source(new ScriptedLauncher(feed), IqSampleFormat.Cu8, rate: 10_000, maxYoungRuns: 1, clock: clock);

        ReadToEnd(source).Length.Should().Be(2 * 22_000);
        source.SamplesLost.Should().Be(0, "what the pause left waiting in the pipe is not lost");
    }

    [Fact]
    public void A_Writer_That_Sends_In_Bursts_On_Time_Has_No_Gaps()
    {
        var clock = new FakeTimeProvider();
        var feed = new ScriptedFeed(clock);
        // A second of IQ written each second, all at once: the reader waits a second each time, and
        // the stall rule sees a feed that is on time.
        feed.Chunks.Enqueue((new byte[2 * 10_000], TimeSpan.Zero));
        for (int k = 0; k < 5; k++)
        {
            feed.Chunks.Enqueue((new byte[2 * 10_000], TimeSpan.FromSeconds(1)));
        }

        using var source = Source(new ScriptedLauncher(feed), IqSampleFormat.Cu8, rate: 10_000, maxYoungRuns: 1, clock: clock);
        ReadToEnd(source).Length.Should().Be(6 * 2 * 10_000);
        source.SamplesLost.Should().Be(0, "a writer that buffers is not one that loses");
    }

    [Theory]
    [InlineData(1.10, "short")]
    [InlineData(0.90, "faster than it can be")]
    public void A_Feed_Off_Its_Stated_Rate_Is_Said(double secondsPerSecondOfIq, string said)
    {
        var clock = new FakeTimeProvider();
        var feed = new ScriptedFeed(clock);
        for (int k = 0; k < 400; k++)
        {
            // 0.1 s of IQ at the stated rate, taking a different time to arrive.
            feed.Chunks.Enqueue((new byte[2 * 1000], TimeSpan.FromSeconds(0.1 * secondsPerSecondOfIq)));
        }

        using var source = Source(new ScriptedLauncher(feed), IqSampleFormat.Cu8, rate: 10_000, maxYoungRuns: 1, clock: clock);
        ReadToEnd(source);
        source.MeasuredRate.Should().BeApproximately(10_000 / secondsPerSecondOfIq, 50);
        Errors().Should().ContainSingle(line => line.Contains("not the 10000 S/s configured") && line.Contains(said),
            "said once, and not again within ten minutes");
    }

    [Fact]
    public void A_Feed_On_Its_Stated_Rate_Says_Nothing()
    {
        var clock = new FakeTimeProvider();
        var feed = new ScriptedFeed(clock);
        for (int k = 0; k < 400; k++)
        {
            feed.Chunks.Enqueue((new byte[2 * 1000], TimeSpan.FromSeconds(0.1)));
        }

        using var source = Source(new ScriptedLauncher(feed), IqSampleFormat.Cu8, rate: 10_000, maxYoungRuns: 1, clock: clock);
        ReadToEnd(source);
        source.MeasuredRate.Should().BeApproximately(10_000, 50);
        Errors().Should().NotContain(line => line.Contains("configured"));
    }

    [Fact]
    public void Steady_State_Reading_Allocates_Nothing()
    {
        var feed = new EndlessFeed();
        using var source = Source(new ScriptedLauncher(feed), IqSampleFormat.Cs16, rate: 48_000, maxYoungRuns: 1);
        var buffer = new float[8192];
        for (int k = 0; k < 20; k++)
        {
            source.Read(buffer);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int k = 0; k < 200; k++)
        {
            source.Read(buffer);
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }

    // ---- through the front end and the station ----

    [Fact]
    public void Afsk1200_Decodes_Through_A_Child_Process_And_The_Front_End()
    {
        const int rate = 240_000;
        (float[] audio, byte[] frame, SimModem sim) = IqReceiverTests.Burst("afsk1200", seed: 21);
        float[] wide = IqSynth.Resample(audio, sim.Rate, rate);
        for (int k = 0; k < wide.Length; k++)
        {
            wide[k] *= 0.3f;
        }

        float[] iq = IqSynth.Dsb(wide, rate, 30_000);
        string file = Path.Combine(_dir, "afsk.cs16");
        var bytes = new byte[iq.Length * 2];
        for (int k = 0; k < iq.Length; k++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(2 * k), (short)Math.Round(Math.Clamp(iq[k], -1f, 1f) * 32767));
        }

        File.WriteAllBytes(file, bytes);

        using var source = Source(Command($"cat '{file}'"), IqSampleFormat.Cs16, rate, maxYoungRuns: 1, centreHz: 144_770_000);
        source.StartNow();
        using var receiver = new IqReceiver(
            source,
            [IqPipeStationDevice.ChannelFor("usb", 30_000, ["afsk1200"], 1f)],
            new IqReceiverOptions { DspRate = sim.Rate });

        sim.Decode(IqSynth.Receive(receiver)[0], frame).Matched.Should().BeTrue();
    }

    [Fact]
    public async Task A_Stalled_Child_Raises_The_Dead_Feed_Alarm()
    {
        // Half a second of IQ, then the child stays alive and writes nothing: a wedged dongle.
        using var source = Source(Command("head -c 48000 /dev/zero; exec sleep 600"), IqSampleFormat.Cu8, rate: 48_000, maxYoungRuns: 3);
        source.StartNow();
        using var receiver = new IqReceiver(
            source,
            [IqPipeStationDevice.ChannelFor("usb", 6_000, [], 1f)],
            new IqReceiverOptions { DspRate = 12_000 });
        var counting = new CountingInput(new IqPipeAudioInput(receiver.Channels[0], receiver, source));
        receiver.Start();

        var clock = new FakeTimeProvider();
        using var stopping = new CancellationTokenSource();
        var journal = new StationJournal("", line => { lock (_lines) { _lines.Add(line); } }, line => { lock (_errors) { _errors.Add(line); } });
        using var station = new DaemonStation(
            new StationOptions
            {
                Channel = new SoundModemChannel(12_000),
                Input = counting,
                DspRate = 12_000,
                Journal = journal,
                DeviceKind = DeadFeedDevice.Alsa,
                TimeProvider = clock,
            },
            stopping.Token);
        var faults = new List<StationFault>();
        station.Faulted += faults.Add;
        Task running = Task.Factory.StartNew(station.Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        await UntilAsync(() => counting.Delivered > 0 && counting.EmptyAfterDelivery >= 3, "the IQ came and then stopped");
        clock.Advance(TimeSpan.FromSeconds(30));

        await UntilAsync(() => faults.Count > 0, "the starvation watch fires");
        faults[0].Reason.Should().Contain("receive feed starved");
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        counting.Dispose();
    }

    // ---- the device's settings ----

    [Fact]
    public void The_Iqpipe_Prefix_Is_Its_Own_Kind()
    {
        DeviceKinds.Of("iqpipe:").Should().BeSameAs(DeviceKinds.IqPipe);
        DeviceKinds.Of("iqpipe:/run/iq").Should().BeSameAs(DeviceKinds.IqPipe);
        DeviceKinds.Of("iqpipe:-").Should().BeSameAs(DeviceKinds.IqPipe);
    }

    [Theory]
    [InlineData("iqpipe:", null, "needs an \"iqpipe\" section")]
    [InlineData("iqpipe:", "{\"format\":\"cu9\",\"rate\":1,\"command\":\"x\"}", "has to be cu8, cs8, cs16 or cf32")]
    [InlineData("iqpipe:", "{\"format\":\"cu8\",\"command\":\"x\"}", "\"rate\" has to be")]
    [InlineData("iqpipe:", "{\"format\":\"cu8\",\"rate\":2400000}", "runs \"iqpipe\".\"command\", and there is none")]
    [InlineData("iqpipe:/run/iq", "{\"format\":\"cu8\",\"rate\":2400000,\"command\":\"rtl_sdr -\"}", "\"command\" is set too")]
    [InlineData("iqpipe:", "{\"format\":\"cu8\",\"rate\":2400000,\"command\":\"rtl_sdr -f 144775000 -\"}", "\"centreFrequency\" is missing")]
    [InlineData("iqpipe:-", "{\"format\":\"cu8\",\"rate\":2400000}", "\"centreFrequency\" is missing")]
    [InlineData("iqpipe:-", "{\"format\":\"cu8\",\"rate\":2400000,\"centreFrequency\":1,\"gain\":0}", "\"gain\" has to be above 0")]
    public void A_Section_That_Cannot_Run_Is_Refused_With_A_Reason(string device, string? json, string reason)
    {
        IqPipeConfig? config = json is null
            ? null
            : System.Text.Json.JsonSerializer.Deserialize<IqPipeConfig>(json, DaemonConfig.Options);
        IqPipeStationDevice.Problem(device, config).Should().Contain(reason);
    }

    [Theory]
    [InlineData("iqpipe:", "{\"format\":\"cf32\",\"rate\":250000,\"command\":\"rx_sdr -F CF32 -s {rate} -f {frequency} -\"}")]
    [InlineData("iqpipe:/run/iq", "{\"format\":\"cu8\",\"rate\":2400000,\"centreFrequency\":144775000}")]
    [InlineData("iqpipe:-", "{\"format\":\"cs16\",\"rate\":96000,\"centreFrequency\":7040000,\"swapIq\":true}")]
    public void A_Section_That_Can_Run_Is_Taken(string device, string json)
    {
        var config = System.Text.Json.JsonSerializer.Deserialize<IqPipeConfig>(json, DaemonConfig.Options);
        IqPipeStationDevice.Problem(device, config).Should().BeNull();
    }

    [Fact]
    public void An_Fm_Channel_Is_As_Wide_As_The_Widest_Mode_On_It()
    {
        IqChannelSpec spec = IqPipeStationDevice.ChannelFor("fm", 25_000, ["afsk1200", "fsk9600-il2p", "not-fm"], 1f);
        spec.Demodulation.Should().Be(IqDemodulation.Fm);
        spec.FmIfBandwidthHz.Should().Be(20_000);
        spec.FmDeviationHz.Should().Be(3000, "the largest deviation any mode on it is sent with");
        IqPipeStationDevice.ChannelFor("LSB", -5_000, [], 1f).Demodulation.Should().Be(IqDemodulation.Lsb);
    }

    [Fact]
    public void A_Program_Line_Is_Made_Printable_Ascii()
    {
        ChildProcessIqFeed.Printable("\u001b[1mFound\u001b[0m 1 device\u00b7s  \t")
            .Should().Be("?[1mFound?[0m 1 device?s");
    }

    // ---- helpers ----

    private ChildProcessIqFeedLauncher Command(string line) => new(line, Note, TimeProvider.System);

    private IqPipeSource Source(
        IIqFeedLauncher launcher, IqSampleFormat format, int rate, int maxYoungRuns,
        double centreHz = 144_800_000, TimeProvider? clock = null) => new(launcher, new IqPipeSourceOptions
        {
            Format = format,
            Rate = rate,
            CentreHz = centreHz,
            RestartDelay = TimeSpan.FromMilliseconds(20),
            MaxRestartDelay = TimeSpan.FromMilliseconds(50),
            MaxYoungRuns = maxYoungRuns,
            TimeProvider = clock ?? TimeProvider.System,
            StallGap = TimeSpan.FromSeconds(1),
            Journal = Note,
            JournalError = NoteError,
        });

    private void Note(string line)
    {
        lock (_lines)
        {
            _lines.Add(line);
        }
    }

    private void NoteError(string line)
    {
        lock (_errors)
        {
            _errors.Add(line);
        }
    }

    private List<string> Lines()
    {
        lock (_lines)
        {
            return [.. _lines];
        }
    }

    private List<string> Errors()
    {
        lock (_errors)
        {
            return [.. _errors];
        }
    }

    private static float[] ReadToEnd(IqPipeSource source)
    {
        var all = new List<float>();
        var buffer = new float[4096];
        int got;
        while ((got = source.Read(buffer)) > 0)
        {
            all.AddRange(buffer.AsSpan(0, got));
        }

        return [.. all];
    }

    private static int Heard(List<(int Floats, long Lost)> heard)
    {
        lock (heard)
        {
            return heard.Sum(h => h.Floats);
        }
    }

    private static async Task WriteAsync(string fifo, byte[] bytes)
    {
        await using var writer = new FileStream(fifo, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        await writer.WriteAsync(bytes);
    }

    private static async Task UntilAsync(Func<bool> condition, string because)
    {
        using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition())
        {
            giveUp.Token.ThrowIfCancellationRequested();
            await Task.Delay(5, giveUp.Token);
        }

        condition().Should().BeTrue(because);
    }

    /// <summary>A recording in <paramref name="format"/> that crosses every edge of its range,
    /// and the floats it stands for, worked out here from the format's definition rather than by
    /// the code under test.</summary>
    private static (byte[] Bytes, float[] Floats) Recording(IqSampleFormat format)
    {
        const int pairs = 300;
        var random = new Random(597);
        switch (format)
        {
            case IqSampleFormat.Cu8:
            {
                var bytes = new byte[2 * pairs];
                random.NextBytes(bytes);
                bytes[0] = 0;
                bytes[1] = 255;
                bytes[2] = 127;
                bytes[3] = 128;
                return (bytes, [.. bytes.Select(b => (float)((b - 127.5) / 127.5))]);
            }

            case IqSampleFormat.Cs8:
            {
                var bytes = new byte[2 * pairs];
                random.NextBytes(bytes);
                bytes[0] = 0x80;
                bytes[1] = 0x7F;
                return (bytes, [.. bytes.Select(b => (float)((sbyte)b / 128.0))]);
            }

            case IqSampleFormat.Cs16:
            {
                var bytes = new byte[4 * pairs];
                random.NextBytes(bytes);
                bytes[0] = 0x00;
                bytes[1] = 0x80;
                bytes[2] = 0xFF;
                bytes[3] = 0x7F;
                var floats = new float[2 * pairs];
                for (int k = 0; k < floats.Length; k++)
                {
                    floats[k] = (float)(((short)(bytes[2 * k] | (bytes[(2 * k) + 1] << 8))) / 32768.0);
                }

                return (bytes, floats);
            }

            default:
            {
                var floats = new float[2 * pairs];
                for (int k = 0; k < floats.Length; k++)
                {
                    floats[k] = (float)((random.NextDouble() * 2) - 1);
                }

                floats[0] = -1f;
                floats[1] = 1f;
                floats[2] = float.Epsilon;
                var bytes = new byte[4 * floats.Length];
                for (int k = 0; k < floats.Length; k++)
                {
                    int bits = BitConverter.SingleToInt32Bits(floats[k]);
                    bytes[4 * k] = (byte)bits;
                    bytes[(4 * k) + 1] = (byte)(bits >> 8);
                    bytes[(4 * k) + 2] = (byte)(bits >> 16);
                    bytes[(4 * k) + 3] = (byte)(bits >> 24);
                }

                return (bytes, floats);
            }
        }
    }

    /// <summary>A launcher that hands out one feed, once.</summary>
    private sealed class ScriptedLauncher(IIqFeed feed) : IIqFeedLauncher
    {
        private int _started;

        public string Description => "scripted";

        public bool CanRestart => false;

        public bool Tunes => false;

        public IIqFeed Start(double centreHz, int rate, CancellationToken cancellation) =>
            Interlocked.Exchange(ref _started, 1) == 0 ? feed : throw new IOException("only once");
    }

    /// <summary>A feed that returns its chunks one per read, each after advancing the clock by
    /// its wait, then ends.</summary>
    private sealed class ScriptedFeed : IIqFeed
    {
        private readonly FakeTimeProvider? _clock;

        public ScriptedFeed(FakeTimeProvider? clock = null)
        {
            _clock = clock;
            Output = new ChunkStream(this);
        }

        public Queue<(byte[] Bytes, TimeSpan Wait)> Chunks { get; } = new();

        public Stream Output { get; }

        public string EndReason => "the script ended";

        /// <summary>What is left of the chunk being read: written, not yet read.</summary>
        public int BytesWaiting => ((ChunkStream)Output).Left;

        public void Stop()
        {
        }

        public void Dispose()
        {
        }

        private sealed class ChunkStream(ScriptedFeed feed) : Stream
        {
            private byte[]? _pending;
            private int _offset;

            public int Left => _pending is null ? 0 : _pending.Length - _offset;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override int Read(Span<byte> buffer)
            {
                if (_pending is null)
                {
                    if (!feed.Chunks.TryDequeue(out (byte[] Bytes, TimeSpan Wait) next))
                    {
                        return 0;
                    }

                    feed._clock?.Advance(next.Wait);
                    _pending = next.Bytes;
                    _offset = 0;
                }

                int take = Math.Min(buffer.Length, _pending.Length - _offset);
                _pending.AsSpan(_offset, take).CopyTo(buffer);
                _offset += take;
                if (_offset == _pending.Length)
                {
                    _pending = null;
                }

                return take;
            }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    /// <summary>A feed that never ends and never allocates: a sine-ish ramp of CS16.</summary>
    private sealed class EndlessFeed : IIqFeed
    {
        private readonly EndlessStream _stream = new();

        public Stream Output => _stream;

        public string EndReason => "never";

        public int BytesWaiting => 0;

        public void Stop()
        {
        }

        public void Dispose()
        {
        }

        private sealed class EndlessStream : Stream
        {
            private byte _next;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override int Read(Span<byte> buffer)
            {
                // An odd count now and then, so the carry is exercised too.
                int take = buffer.Length - (_next & 1);
                for (int k = 0; k < take; k++)
                {
                    buffer[k] = _next++;
                }

                return take;
            }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    /// <summary>The station's input, counted: how much it delivered, and how many empty reads
    /// came after it had.</summary>
    private sealed class CountingInput(IqPipeAudioInput inner) : IAudioInput, IDisposable
    {
        private long _delivered;
        private long _empty;

        public long Delivered => Interlocked.Read(ref _delivered);

        public long EmptyAfterDelivery => Interlocked.Read(ref _empty);

        public int SampleRate => inner.SampleRate;

        public int Read(Span<float> destination)
        {
            int got = inner.Read(destination);
            if (got > 0)
            {
                Interlocked.Add(ref _delivered, got);
            }
            else if (Delivered > 0)
            {
                Interlocked.Increment(ref _empty);
            }

            return got;
        }

        public void Dispose() => inner.Dispose();
    }
}
