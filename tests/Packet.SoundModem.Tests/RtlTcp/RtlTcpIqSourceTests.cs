using System.Collections.Concurrent;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.RtlTcp;

namespace Packet.SoundModem.Tests.RtlTcp;

/// <summary>
/// The rtl_tcp source against a fake server on loopback: what it sends, what it does not, and
/// how it reports a server that goes away, goes quiet, or changes under it.
/// </summary>
public sealed class RtlTcpIqSourceTests
{
    private const int Rate = 250_000;

    private readonly ConcurrentQueue<string> _journal = new();

    private RtlTcpSourceOptions Controlled(double centreHz = 144_775_000) => new()
    {
        CentreFrequencyHz = centreHz,
        SampleRate = Rate,
        TunerGainDb = 29.6,
        FrequencyCorrectionPpm = -12,
        RetuneSettle = TimeSpan.FromMilliseconds(1),
        RetuneDiscardFloorBytes = 0,
        WatchProfile = false,
        Journal = _journal.Enqueue,
    };

    private RtlTcpSourceOptions ReadOnly() => new()
    {
        ReadOnly = true,
        CentreFrequencyHz = 144_775_000,
        SampleRate = Rate,
        WatchProfile = false,
        Journal = _journal.Enqueue,
    };

    [Fact]
    public async Task A_Controlled_Server_Is_Sent_Rate_Centre_Correction_Gain_And_Agc_In_That_Order()
    {
        using var server = new FakeRtlTcpServer();
        using var source = new RtlTcpIqSource(server.Endpoint, Controlled());

        await source.OpenAsync(CancellationToken.None);
        server.WaitForCommands(6);

        server.Commands.Select(c => (c.Command, c.Parameter)).Should().Equal(
            (RtlTcpCommand.SetSampleRate, 250_000u),
            (RtlTcpCommand.SetFrequency, 144_775_000u),
            (RtlTcpCommand.SetFrequencyCorrection, unchecked((uint)-12)),
            (RtlTcpCommand.SetGainMode, 1u),
            (RtlTcpCommand.SetGain, 296u),
            (RtlTcpCommand.SetAgcMode, 0u));
        source.Header.Should().Be(new RtlTcpHeader(RtlTcpTuner.R820T, 29));
        _journal.Should().Contain(l => l.Contains("R820T tuner", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_No_Gain_Given_The_Tuner_Is_Left_On_Its_Own_Gain()
    {
        using var server = new FakeRtlTcpServer();
        using var source = new RtlTcpIqSource(server.Endpoint, Controlled() with { TunerGainDb = null, RtlAgc = true });

        await source.OpenAsync(CancellationToken.None);
        server.WaitForCommands(5);
        Thread.Sleep(100);

        server.Commands.Select(c => c.Command).Should().NotContain(RtlTcpCommand.SetGain);
        server.Commands.Should().Contain(c => c.Command == RtlTcpCommand.SetGainMode && c.Parameter == 0);
        server.Commands.Should().Contain(c => c.Command == RtlTcpCommand.SetAgcMode && c.Parameter == 1);
    }

    [Fact]
    public async Task A_Read_Only_Server_Is_Sent_Nothing_And_Cannot_Be_Retuned()
    {
        using var server = new FakeRtlTcpServer();
        using var source = new RtlTcpIqSource(server.Endpoint, ReadOnly());
        await source.OpenAsync(CancellationToken.None);
        server.WaitForConnections(1);

        server.Send([0, 255, 127, 128]);
        float[] got = ReadAtLeast(source, 4);
        Thread.Sleep(200);

        got.Should().Equal(-1f, 1f, -0.5f / 127.5f, 0.5f / 127.5f);
        server.BytesFromClient.Should().Be(0, "a read-only server is never sent a byte");
        Action tune = () => source.Tune(145_000_000);
        tune.Should().Throw<InvalidOperationException>().WithMessage("*read-only*");
        source.TuningGeneration.Should().Be(0);
        source.SamplesLost.Should().Be(0);
    }

    [Fact]
    public async Task A_Server_With_No_Header_Is_Taken_As_Samples_From_The_Start()
    {
        using var server = new FakeRtlTcpServer(header: null) { Header = null };
        using var source = new RtlTcpIqSource(server.Endpoint, ReadOnly());

        // The first twelve bytes are waited for as a header; without the magic they are let go.
        Task open = source.OpenAsync(CancellationToken.None);
        server.WaitForConnections(1);
        server.Send(new byte[12]);
        await open;
        server.Send([255, 0]);

        ReadAtLeast(source, 2).Should().Equal(1f, -1f);
        source.Header.Should().BeNull();
        _journal.Should().Contain(l => l.Contains("sent no dongle header", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_Dropped_Connection_Is_Reconnected_Settings_Resent_And_The_Time_Away_Reported_As_A_Gap()
    {
        using var server = new FakeRtlTcpServer();
        using var source = new RtlTcpIqSource(server.Endpoint, Controlled() with
        {
            ReconnectDelay = TimeSpan.FromMilliseconds(20),
        });
        await source.OpenAsync(CancellationToken.None);
        server.WaitForConnections(1);
        server.WaitForCommands(6);

        // 500 bytes of settling are thrown away after the settings; then samples.
        server.Send(new byte[500]);
        server.Send(Ramp(1000));
        ReadAtLeast(source, 1000).Length.Should().Be(1000);
        source.SamplesLost.Should().Be(0, "nothing came before the first block, so nothing was lost");

        server.DropClient();
        Task<float[]> after = Task.Run(() => ReadAtLeast(source, 1000));
        server.WaitForConnections(2);
        server.WaitForCommands(12);
        server.Send(new byte[500]);
        server.Send(Ramp(1000));
        (await after).Length.Should().Be(1000);

        source.Reconnects.Should().Be(1);
        source.SamplesLost.Should().BeGreaterThanOrEqualTo(251, "the time away, at least one sample, and the 250 settling samples");
        source.TuningGeneration.Should().Be(0, "the tuning is the same as before the drop");
        server.Commands.Where(c => c.Connection == 2).Select(c => c.Command).Should().Equal(
            server.Commands.Where(c => c.Connection == 1).Select(c => c.Command));
        _journal.Should().Contain(l => l.Contains("reconnected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_Server_That_Goes_Quiet_Is_Dropped_After_The_Timeout_And_Reconnected()
    {
        using var server = new FakeRtlTcpServer();
        using var source = new RtlTcpIqSource(server.Endpoint, ReadOnly() with
        {
            NoDataTimeout = TimeSpan.FromSeconds(1),
            ReconnectDelay = TimeSpan.FromMilliseconds(20),
        });
        await source.OpenAsync(CancellationToken.None);
        server.WaitForConnections(1);
        server.Send(Ramp(100));
        ReadAtLeast(source, 100);

        Task<float[]> after = Task.Run(() => ReadAtLeast(source, 100));
        server.WaitForConnections(2);
        server.Send(Ramp(100));
        await after;

        _journal.Should().Contain(l => l.Contains("sent nothing for", StringComparison.Ordinal));
        source.Reconnects.Should().BeGreaterThanOrEqualTo(1);
        source.SamplesLost.Should().BeGreaterThan(Rate, "at least a second went missing at 250 kS/s");
    }

    [Fact]
    public async Task A_Server_Gone_For_Good_Is_Given_Up_On_And_Said_Once()
    {
        using var server = new FakeRtlTcpServer();
        using var source = new RtlTcpIqSource(server.Endpoint, ReadOnly() with
        {
            ReconnectDelay = TimeSpan.FromMilliseconds(20),
            ReconnectDelayCap = TimeSpan.FromMilliseconds(50),
            GiveUpAfter = TimeSpan.FromMilliseconds(500),
            ConnectTimeout = TimeSpan.FromMilliseconds(200),
        });
        var lost = new ConcurrentQueue<string>();
        source.Lost += lost.Enqueue;
        await source.OpenAsync(CancellationToken.None);
        server.WaitForConnections(1);
        server.Send(Ramp(10));
        ReadAtLeast(source, 10);

        server.Stop();
        int got = await Task.Run(() => source.Read(new float[64])).WaitAsync(TimeSpan.FromSeconds(20));

        got.Should().Be(0, "a source that has given up is closing");
        lost.Should().ContainSingle().Which.Should().Contain("delivered nothing").And.Contain("Connection refused");
        source.Connected.Should().BeFalse();
        source.SessionLive.Should().BeFalse();
    }

    [Fact]
    public async Task A_Server_That_Takes_The_Connection_And_Sends_Nothing_Is_Given_Up_On_Without_A_Flood()
    {
        using var server = new FakeRtlTcpServer();
        using var source = new RtlTcpIqSource(server.Endpoint, ReadOnly() with
        {
            ReconnectDelay = TimeSpan.FromMilliseconds(20),
            ReconnectDelayCap = TimeSpan.FromMilliseconds(200),
            GiveUpAfter = TimeSpan.FromSeconds(1.5),
        });
        var lost = new ConcurrentQueue<string>();
        source.Lost += lost.Enqueue;
        await source.OpenAsync(CancellationToken.None);
        server.WaitForConnections(1);
        server.Send(Ramp(10));
        ReadAtLeast(source, 10);

        // From now on every connection is taken, given a header, and closed at once.
        server.CloseOnAccept = true;
        server.DropClient();
        int got = await Task.Run(() => source.Read(new float[64])).WaitAsync(TimeSpan.FromSeconds(20));

        got.Should().Be(0);
        lost.Should().ContainSingle().Which.Should().Contain("delivered nothing");
        server.Connections.Should().BeLessThan(20, "a server that accepts and drops is retried with a growing wait, not hammered");
        source.Read(new float[64]).Should().Be(0, "a source that has given up stays closed");
    }

    // 192.0.2.1 is TEST-NET-1 (RFC 5737): routed nowhere, so a connect to it either hangs for
    // the OS's own timeout or fails at once, depending on the network. Either way these must
    // come back promptly.
    private static readonly RtlTcpEndpoint Blackhole = new("192.0.2.1", 1234);

    [Fact]
    public async Task Disposing_While_Connecting_To_A_Host_That_Never_Answers_Returns_Promptly()
    {
        var source = new RtlTcpIqSource(Blackhole, ReadOnly() with
        {
            ConnectTimeout = TimeSpan.FromMinutes(5),
            ReconnectDelay = TimeSpan.FromMilliseconds(10),
            GiveUpAfter = TimeSpan.MaxValue,
        });
        Task<int> reading = Task.Run(() => source.Read(new float[64]));
        await Task.Delay(300);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        source.Dispose();
        int got = await reading.WaitAsync(TimeSpan.FromSeconds(10));

        got.Should().Be(0);
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task An_Open_To_A_Host_That_Never_Answers_Is_Cancelled_Promptly()
    {
        using var source = new RtlTcpIqSource(Blackhole, ReadOnly() with { ConnectTimeout = TimeSpan.FromMinutes(5) });
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        Func<Task> open = () => source.OpenAsync(cancel.Token);

        // Cancelled, or refused by a network that says so at once: never the five minutes.
        await open.Should().ThrowAsync<Exception>().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task An_Unreachable_Server_Fails_The_Open_So_Start_Up_Can_Say_So()
    {
        int port;
        using (var server = new FakeRtlTcpServer())
        {
            port = server.Port;
        }

        using var source = new RtlTcpIqSource(new RtlTcpEndpoint("127.0.0.1", port), ReadOnly());
        Func<Task> open = () => source.OpenAsync(CancellationToken.None);

        await open.Should().ThrowAsync<System.Net.Sockets.SocketException>();
    }

    [Fact]
    public async Task A_Retune_Is_Sent_And_The_First_Block_After_It_Is_Marked_New_With_The_Settling_Counted_As_Lost()
    {
        using var server = new FakeRtlTcpServer();
        using var source = new RtlTcpIqSource(server.Endpoint, Controlled());
        await source.OpenAsync(CancellationToken.None);
        server.WaitForConnections(1);
        server.WaitForCommands(6);
        server.Send(new byte[500]);
        server.Send(Ramp(1000));
        ReadAtLeast(source, 1000);

        source.Tune(145_000_000);
        server.WaitForCommands(7);
        server.Commands[^1].Should().Be((1, RtlTcpCommand.SetFrequency, 145_000_000u));
        source.CentreFrequencyHz.Should().Be(144_775_000, "nothing at the new tuning has been read yet");
        source.TargetCentreHz.Should().Be(145_000_000);

        server.Send(new byte[500]);
        server.Send(Ramp(1000));
        float[] after = ReadAtLeast(source, 1000);

        after.Length.Should().Be(1000);
        source.CentreFrequencyHz.Should().Be(145_000_000);
        source.TuningGeneration.Should().Be(1);
        source.SamplesLost.Should().Be(250, "1 ms of settling at 250 kS/s is thrown away and counted");
    }

    [Fact]
    public async Task A_Retune_Can_Change_The_Rate_Too()
    {
        using var server = new FakeRtlTcpServer();
        using var source = new RtlTcpIqSource(server.Endpoint, Controlled());
        await source.OpenAsync(CancellationToken.None);
        server.WaitForCommands(6);
        server.Send(new byte[500]);
        server.Send(Ramp(100));
        ReadAtLeast(source, 100);

        source.Tune(145_000_000, 1_024_000);
        server.WaitForCommands(8);

        server.Commands.Skip(6).Select(c => (c.Command, c.Parameter)).Should().Equal(
            (RtlTcpCommand.SetSampleRate, 1_024_000u),
            (RtlTcpCommand.SetFrequency, 145_000_000u));
        server.Send(new byte[2048]);
        server.Send(Ramp(100));
        ReadAtLeast(source, 100);
        source.SampleRate.Should().Be(1_024_000);
        source.TuningGeneration.Should().Be(1);
    }

    [Fact]
    public void A_Rate_No_Dongle_Runs_At_Is_Refused_Before_Anything_Is_Sent()
    {
        Action make = () => _ = new RtlTcpIqSource(new RtlTcpEndpoint("127.0.0.1", 1), Controlled() with { SampleRate = 600_000 });

        make.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*225001 to 300000*");
    }

    [Fact]
    public async Task A_Read_Only_Server_Whose_Rate_Changes_Is_Dropped_Until_It_Is_Right_Again()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
        using var server = new FakeRtlTcpServer();
        using var source = new RtlTcpIqSource(server.Endpoint, ReadOnly() with
        {
            SampleRate = 100_000,
            WatchProfile = true,
            RateWindow = TimeSpan.FromSeconds(1),
            TimeProvider = clock,
        });
        await source.OpenAsync(CancellationToken.None);
        server.WaitForConnections(1);

        long lastGeneration = 0;
        long lastLost = 0;
        using var reading = new CancellationTokenSource();
        Task reader = Task.Run(() =>
        {
            var buffer = new float[65536];
            while (!reading.IsCancellationRequested && source.Read(buffer) > 0)
            {
                Volatile.Write(ref lastGeneration, source.TuningGeneration);
                Volatile.Write(ref lastLost, source.SamplesLost);
            }
        });

        long sent = 0;
        void Stream(double seconds, int actualRate)
        {
            // A tenth of a second at a time: send it, let the source take it, move the clock on.
            for (int step = 0; step < seconds * 10; step++)
            {
                int bytes = 2 * actualRate / 10;
                server.Send(Ramp(bytes));
                sent += bytes;
                SpinWait.SpinUntil(() => source.BytesReceived >= sent, 5000).Should().BeTrue();
                clock.Advance(TimeSpan.FromMilliseconds(100));
            }
        }

        Stream(8, 100_000);
        source.ProfileDrops.Should().Be(0, "the server is at the rate it was said to be");
        source.SessionLive.Should().BeTrue();

        Stream(5, 50_000);
        source.ProfileDrops.Should().Be(1, "half the rate it should be is a profile switch");
        source.SessionLive.Should().BeFalse("the station waits while the stream is wrong rather than restarting");
        _journal.Should().Contain(l => l.Contains("profile has probably been switched", StringComparison.Ordinal));

        Stream(5, 100_000);
        source.SessionLive.Should().BeTrue();
        _journal.Should().Contain(l => l.Contains("again", StringComparison.Ordinal));
        SpinWait.SpinUntil(() => Volatile.Read(ref lastGeneration) == 1, 5000).Should().BeTrue(
            "the first block back is a fresh start");
        Volatile.Read(ref lastLost).Should().BeGreaterThan(0, "what was dropped is reported as lost");

        reading.Cancel();
        source.Dispose();
        await reader.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_Steady_Stream_Is_Read_Without_Allocating()
    {
        const int rate = 1_000_000;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
        using var server = new FakeRtlTcpServer();
        using var source = new RtlTcpIqSource(server.Endpoint, ReadOnly() with
        {
            SampleRate = rate,
            WatchProfile = true,
            RateWindow = TimeSpan.FromMilliseconds(500),
            TimeProvider = clock,
        });
        await source.OpenAsync(CancellationToken.None);
        server.WaitForConnections(1);

        // Noise-like bytes, the same block over and over, so the step watch has nothing to say.
        var block = new byte[65536];
        new Random(9).NextBytes(block);
        for (int k = 0; k < block.Length; k++)
        {
            block[k] = (byte)(124 + (block[k] & 7));
        }

        using var streaming = new CancellationTokenSource();
        Task feeder = Task.Run(() =>
        {
            try
            {
                while (!streaming.IsCancellationRequested)
                {
                    // A pause now and then, so the reader often has to wait, warm-up included:
                    // a read that waits is the one that would allocate on a socket left
                    // non-blocking.
                    server.Send(block);
                    Thread.Sleep(1);
                }
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException)
            {
            }
        });

        // The reads move the clock on by exactly what they returned, so the rate watch closes its
        // windows on a stream at its rate and has nothing to say either: the steady state.
        var buffer = new float[16384];
        long allocated = 0;
        int allocatingReads = 0;
        int pausedReads = 0;
        for (int read = 0; read < 600; read++)
        {
            TimeSpan paused = GC.GetTotalPauseDuration();
            long before = GC.GetAllocatedBytesForCurrentThread();
            int got = source.Read(buffer);
            long after = GC.GetAllocatedBytesForCurrentThread();
            got.Should().BeGreaterThan(0);
            if (read >= 100 && GC.GetTotalPauseDuration() != paused)
            {
                pausedReads++;
            }
            else if (read >= 100 && after != before)
            {
                allocated += after - before;
                allocatingReads++;
            }

            clock.Advance(TimeSpan.FromTicks(TimeSpan.TicksPerSecond * (got / 2) / rate));
        }

        streaming.Cancel();
        source.Dispose();
        server.DropClient();
        await feeder.WaitAsync(TimeSpan.FromSeconds(10));

        // Zero, give or take one read, among the reads no collection paused. The runtime retires
        // the unused rest of a thread's allocation context (under 8 KiB) when it suspends the
        // thread, which the counter shows as a jump that nothing here allocated; a collection
        // started by another test's allocations does it on whichever reads it lands in, and on
        // a loaded box that was two reads, not one (#688). Those reads say nothing about the
        // read path, so they are set aside, the way #668 set aside the rate bridge's paused
        // stretches. A real allocation in the read path shows on every read, or on every read
        // that waits, which with the feeder's pauses is over a hundred of these five hundred
        // (measured: a socket left non-blocking by an async connect allocates on about 120 of
        // them), so it fails this whatever the collector does.
        pausedReads.Should().BeLessThan(250, "most reads need to run unpaused for this to measure anything");
        allocatingReads.Should().BeLessThanOrEqualTo(1, "a read of a steady stream, rate watch and all, allocates nothing");
        allocated.Should().BeLessThan(8192);
        source.ProfileDrops.Should().Be(0);
        source.SuspectedProfileChanges.Should().Be(0);
    }

    private static byte[] Ramp(int length)
    {
        var bytes = new byte[length];
        for (int k = 0; k < length; k++)
        {
            bytes[k] = (byte)(k * 7);
        }

        return bytes;
    }

    private static float[] ReadAtLeast(RtlTcpIqSource source, int floats)
    {
        var result = new List<float>();
        var buffer = new float[Math.Max(2, floats)];
        while (result.Count < floats)
        {
            int got = source.Read(buffer.AsSpan(0, Math.Max(2, floats - result.Count)));
            if (got == 0)
            {
                break;
            }

            result.AddRange(buffer.AsSpan(0, got));
        }

        return [.. result];
    }
}
