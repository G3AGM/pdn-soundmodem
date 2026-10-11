using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// The transmit lease: one sub-channel holds the transmitter, everyone else's transmissions are
/// refused (never queued), and a holder that stops renewing loses it on its own. Every clock here
/// is fake, so expiry is reached by advancing it rather than by waiting.
/// </summary>
public class TransmitLeaseTests
{
    private const int SampleRate = 12000;

    private static byte[] Frame(byte marker)
    {
        byte[] frame = new byte[24];
        byte[] header = [0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0];
        header.CopyTo(frame, 0);
        frame.AsSpan(16).Fill(marker);
        return frame;
    }

    private static (SoundModemChannel Channel, FakeTimeProvider Time) Station()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        var channel = new SoundModemChannel(SampleRate, time, randomSeed: 42);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        channel.AddModem(3, sink => new Afsk1200Modem(SampleRate, sink));
        channel.Csma.Persistence = 255;
        return (channel, time);
    }

    [Fact]
    public void A_Lease_Is_Taken_Renewed_Refused_To_Others_And_Released()
    {
        (SoundModemChannel channel, FakeTimeProvider time) = Station();
        TransmitLease lease = channel.TransmitLease;
        var changes = new List<TransmitLeaseEvent>();
        lease.Changed += changes.Add;

        lease.Holder.Should().BeNull("nobody holds the transmitter until asked");
        TransmitLeaseGrant taken = lease.Take(3, TimeSpan.FromSeconds(60));
        taken.Should().Be(new TransmitLeaseGrant(true, 3, time.GetUtcNow().AddSeconds(60), Renewed: false));

        time.Advance(TimeSpan.FromSeconds(30));
        TransmitLeaseGrant renewed = lease.Take(3, TimeSpan.FromSeconds(60));
        renewed.Renewed.Should().BeTrue();
        lease.Expires.Should().Be(time.GetUtcNow().AddSeconds(60));

        TransmitLeaseGrant other = lease.Take(0, TimeSpan.FromSeconds(60));
        other.Granted.Should().BeFalse();
        other.SubChannel.Should().Be(3, "the answer names who holds it");

        lease.Release().Should().BeTrue();
        lease.Holder.Should().BeNull();
        lease.Release().Should().BeFalse("there is nothing left to give back");

        changes.Select(c => c.Change).Should().Equal(
            TransmitLeaseChange.Taken, TransmitLeaseChange.Renewed, TransmitLeaseChange.Released);
        changes[^1].HeldFor.Should().Be(TimeSpan.FromSeconds(30));
        changes[^1].Renewals.Should().Be(1);
    }

    [Fact]
    public void A_Lease_Asked_For_Longer_Than_The_Cap_Gets_The_Cap()
    {
        (SoundModemChannel channel, FakeTimeProvider time) = Station();

        TransmitLeaseGrant grant = channel.TransmitLease.Take(3, TimeSpan.FromHours(1));

        grant.Expires.Should().Be(time.GetUtcNow() + TransmitLease.MaxDuration);
        TransmitLease.MaxDuration.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void A_Holder_That_Stops_Renewing_Loses_The_Lease_When_It_Runs_Out()
    {
        (SoundModemChannel channel, FakeTimeProvider time) = Station();
        var changes = new List<TransmitLeaseEvent>();
        channel.TransmitLease.Changed += changes.Add;
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));

        time.Advance(TimeSpan.FromSeconds(59));
        channel.TransmitLease.Holder.Should().Be(3);
        changes.Should().ContainSingle("nothing has happened yet but the take");

        time.Advance(TimeSpan.FromSeconds(1));

        // Reported by the lease's own timer as the clock passes the instant, not by somebody
        // happening to ask afterwards: that is what puts the line in the journal on time.
        changes.Should().HaveCount(2);
        changes[1].Change.Should().Be(TransmitLeaseChange.Expired);
        changes[1].SubChannel.Should().Be(3);
        changes[1].HeldFor.Should().Be(TimeSpan.FromSeconds(60));
        channel.TransmitLease.Holder.Should().BeNull();
        channel.TransmitLease.Admits(0).Should().BeTrue();
    }

    [Fact]
    public void A_Renewal_Moves_The_Expiry_So_The_Old_One_Does_Not_Fire()
    {
        (SoundModemChannel channel, FakeTimeProvider time) = Station();
        var changes = new List<TransmitLeaseEvent>();
        channel.TransmitLease.Changed += changes.Add;
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));

        for (int i = 0; i < 10; i++)
        {
            time.Advance(TimeSpan.FromSeconds(30));
            channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));
        }

        channel.TransmitLease.Holder.Should().Be(3);
        changes.Should().NotContain(c => c.Change == TransmitLeaseChange.Expired);
    }

    [Fact]
    public void Frames_From_Other_Sub_Channels_Are_Refused_Not_Queued()
    {
        (SoundModemChannel channel, _) = Station();
        var refused = new List<(int Sub, string Why)>();
        channel.TransmitRejected += (sub, _, why) => refused.Add((sub, why.Message));
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));

        Task other = channel.EnqueueTransmit(0, Frame(0x41));
        Task again = channel.EnqueueTransmit(0, Frame(0x42));
        Task holders = channel.EnqueueTransmit(3, Frame(0x43));

        other.IsFaulted.Should().BeTrue("refused the moment it is handed over");
        again.IsFaulted.Should().BeTrue();
        holders.IsCompleted.Should().BeFalse("the holder's frame is queued for the transmitter");
        refused.Should().HaveCount(2);
        refused.Should().OnlyContain(r => r.Sub == 0);
        refused.Select(r => r.Why).Distinct().Should().ContainSingle(
            "one sentence every time, so the station's rate-limited DROPPED line folds them together")
            .Which.Should().Be("sub-channel 3 holds the transmit lease, so other transmissions are refused until it ends");

        TransmitLeaseEvent released = default;
        channel.TransmitLease.Changed += change => released = change;
        channel.TransmitLease.Release();
        released.Refused.Should().Be(2, "the release says how much was turned away");
    }

    [Fact]
    public async Task What_Others_Had_Already_Queued_Is_Refused_When_The_Lease_Is_Taken()
    {
        (SoundModemChannel channel, _) = Station();
        var refused = new List<int>();
        channel.TransmitRejected += (sub, _, _) => refused.Add(sub);

        Task queuedBefore = channel.EnqueueTransmit(0, Frame(0x41));
        Task holdersBefore = channel.EnqueueTransmit(3, Frame(0x42));
        Task service = channel.EnqueueTransmit(_ => new float[100]);
        queuedBefore.IsCompleted.Should().BeFalse("nothing is running the transmitter yet");

        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));

        // A sub-channel frame's task is the announcing wrapper's, which resumes on its own
        // continuation, so it is awaited rather than read; the refusal itself was synchronous.
        Func<Task> waiting = () => queuedBefore.WaitAsync(TimeSpan.FromSeconds(10));
        await waiting.Should().ThrowAsync<InvalidOperationException>(
            "a queue would release stale frames when the lease ends, so it is emptied instead");
        service.IsFaulted.Should().BeTrue("a transmission that names no sub-channel belongs to nobody");
        holdersBefore.IsCompleted.Should().BeFalse("the holder's own frame keeps its place");
        refused.Should().Equal(0);
    }

    [Fact]
    public void A_Service_Transmission_Attributed_To_The_Holder_Goes_Through()
    {
        // The holder's Morse ident: queued through the delegate overload under the identifier,
        // which the station attributes to its modem's sub-channel.
        (SoundModemChannel channel, _) = Station();
        object holdersIdent = new();
        object othersIdent = new();
        channel.TransmitLease.Attribute(holdersIdent, 3);
        channel.TransmitLease.Attribute(othersIdent, 0);
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));

        Task mine = channel.EnqueueTransmit(_ => new float[100], source: holdersIdent);
        Task theirs = channel.EnqueueTransmit(_ => new float[100], source: othersIdent);

        mine.IsCompleted.Should().BeFalse("queued, waiting for the transmitter");
        theirs.IsFaulted.Should().BeTrue();
        channel.TransmitLease.Admits(holdersIdent).Should().BeTrue();
        channel.TransmitLease.Admits(othersIdent).Should().BeFalse();
    }

    [Fact]
    public void Normal_Service_Resumes_When_The_Lease_Runs_Out()
    {
        (SoundModemChannel channel, FakeTimeProvider time) = Station();
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));
        channel.EnqueueTransmit(0, Frame(0x41)).IsFaulted.Should().BeTrue();

        time.Advance(TimeSpan.FromSeconds(60));

        channel.EnqueueTransmit(0, Frame(0x42)).IsCompleted.Should().BeFalse(
            "queued again like any frame, once nobody holds the transmitter");
    }

    [Fact]
    public async Task The_Holders_Frames_Go_On_The_Air_While_Everyone_Else_Is_Refused()
    {
        (SoundModemChannel channel, FakeTimeProvider time) = Station();
        var sent = new List<int>();
        channel.FrameTransmitted += (sub, _) => { lock (sent) { sent.Add(sub); } };
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));

        Task other = channel.EnqueueTransmit(0, Frame(0x41));
        Task holders = channel.EnqueueTransmit(3, Frame(0x42));

        var ptt = new RecordingPtt();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Task transmitter = channel.RunTransmitterAsync(new FakeAudioOutput(SampleRate), ptt, cancellation.Token);
        await holders.WaitAsync(TimeSpan.FromSeconds(30));
        await cancellation.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }

        other.IsFaulted.Should().BeTrue();
        sent.Should().Equal(3);
        time.GetUtcNow().Should().Be(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero),
            "nothing here needed the clock to move");
    }

    /// <summary>A clock whose wall time can be stepped without its monotonic time moving.</summary>
    private sealed class SteppedWallClock(FakeTimeProvider inner, TimeSpan earlyBy = default) : TimeProvider
    {
        public TimeSpan Step { get; set; }

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow() + Step;

        public override long GetTimestamp() => inner.GetTimestamp();

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            inner.CreateTimer(callback, state, dueTime > earlyBy ? dueTime - earlyBy : dueTime, period);
    }

    [Fact]
    public void A_Wall_Clock_Step_Neither_Shortens_Nor_Lengthens_A_Lease()
    {
        var fake = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        var clock = new SteppedWallClock(fake);
        var channel = new SoundModemChannel(SampleRate, clock, randomSeed: 1);
        channel.AddModem(3, sink => new Afsk1200Modem(SampleRate, sink));
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));

        clock.Step = TimeSpan.FromHours(1);
        channel.TransmitLease.Holder.Should().Be(3, "NTP stepping the clock forward is not the lease running out");

        clock.Step = TimeSpan.FromHours(-1);
        fake.Advance(TimeSpan.FromSeconds(60));
        channel.TransmitLease.Holder.Should().BeNull("nor is stepping it back a reason to keep it");
    }

    [Fact]
    public void A_Timer_That_Fires_Early_Rearms_So_The_Expiry_Is_Still_Reported()
    {
        var fake = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        var clock = new SteppedWallClock(fake, earlyBy: TimeSpan.FromSeconds(5));
        var channel = new SoundModemChannel(SampleRate, clock, randomSeed: 1);
        channel.AddModem(3, sink => new Afsk1200Modem(SampleRate, sink));
        var changes = new List<TransmitLeaseChange>();
        channel.TransmitLease.Changed += change => changes.Add(change.Change);
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));

        fake.Advance(TimeSpan.FromSeconds(55));
        changes.Should().Equal([TransmitLeaseChange.Taken], "the timer fired five seconds early and found nothing due");

        fake.Advance(TimeSpan.FromSeconds(5));
        changes.Should().Equal(TransmitLeaseChange.Taken, TransmitLeaseChange.Expired);
    }

    [Fact]
    public void A_Release_Naming_Another_Sub_Channel_Frees_Nothing()
    {
        (SoundModemChannel channel, _) = Station();
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));

        channel.TransmitLease.Release(onlyIf: 0).Should().BeFalse("a late release must not free somebody else's lease");
        channel.TransmitLease.Holder.Should().Be(3);
        channel.TransmitLease.Release(onlyIf: 3).Should().BeTrue();
        channel.TransmitLease.Holder.Should().BeNull();
    }

    /// <summary>A modem that tells carrier sense the channel is clear, and runs the test's hook
    /// the first time it does, before the transmitter can act on the answer.</summary>
    private sealed class ClearingModem(Action onFirstClear) : IModem
    {
        private int _asked;

        public volatile bool Armed;

        public bool Fired => Volatile.Read(ref _asked) == 1;

        public string Mode => "clearing";

        public event Action<byte[], FrameQuality>? FrameDecoded
        {
            add { }
            remove { }
        }

        public bool CarrierDetect => false;

        public bool ChannelBusy
        {
            get
            {
                if (Armed && Interlocked.Exchange(ref _asked, 1) == 0)
                {
                    onFirstClear();
                }

                return false;
            }
        }

        public void Process(ReadOnlySpan<float> samples)
        {
        }

        public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds) => [];

        public void ResetCarrierState()
        {
        }
    }

    /// <summary>A packing modem that renders instantly.</summary>
    private sealed class QuickPacker : IModem, IFramePackingModem
    {
        public string Mode => "quick-packer";

        public event Action<byte[], FrameQuality>? FrameDecoded
        {
            add { }
            remove { }
        }

        public bool CarrierDetect => false;

        public bool ChannelBusy => false;

        public FramePacking? Packing { get; set; } = new(TimeSpan.FromSeconds(30), TimeSpan.Zero);

        public void Process(ReadOnlySpan<float> samples)
        {
        }

        public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds) => new float[100];

        public void ResetCarrierState()
        {
        }

        public int FramesPerBurst(IReadOnlyList<byte[]> frames) => frames.Count;

        public float[] ModulateFrames(IReadOnlyList<byte[]> frames, int txDelayMilliseconds) => new float[1000];
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_Lease_Taken_As_The_Channel_Clears_Is_Honoured_Before_The_Radio_Keys(bool packing)
    {
        // The latest moment a lease can arrive: carrier sense is answering "clear" for a frame
        // that is about to key. Whatever the transmitter already holds or still has queued, the
        // radio must not key for a sub-channel the lease does not cover.
        var channel = new SoundModemChannel(SampleRate, randomSeed: 42);
        if (packing)
        {
            channel.AddModem(0, _ => new QuickPacker());
        }
        else
        {
            channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        }

        var clearing = new ClearingModem(() => channel.TransmitLease.Take(1, TimeSpan.FromSeconds(60)));
        channel.AddModem(1, _ => clearing);
        channel.Csma.Persistence = 255;

        Task frame = channel.EnqueueTransmit(0, Frame(0x41));
        frame.IsCompleted.Should().BeFalse("queued before any lease exists");
        clearing.Armed = true;
        var ptt = new RecordingPtt();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Task transmitter = channel.RunTransmitterAsync(new FakeAudioOutput(SampleRate), ptt, cancellation.Token);

        Func<Task> waiting = () => frame.WaitAsync(TimeSpan.FromMinutes(1));
        await waiting.Should().ThrowAsync<InvalidOperationException>();
        clearing.Fired.Should().BeTrue("the lease arrived from inside carrier sense");
        ptt.Events.Should().BeEmpty("nothing keys for a frame the lease does not cover");

        await cancellation.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Completes when the lease reports <paramref name="change"/>; the end of a closing
    /// lease is reported from its own continuation, so a test awaits it rather than reads it.</summary>
    private static Task Reported(TransmitLease lease, TransmitLeaseChange change)
    {
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lease.Changed += reported =>
        {
            if (reported.Change == change)
            {
                seen.TrySetResult();
            }
        };
        return seen.Task;
    }

    [Fact]
    public async Task A_Closing_Transmission_Keeps_The_Lease_Until_It_Has_Gone()
    {
        (SoundModemChannel channel, _) = Station();
        var ident = new TaskCompletionSource();
        int? askedFor = null;
        channel.TransmitLease.Closing = holder =>
        {
            askedFor = holder;
            return ident.Task;
        };
        var changes = new List<TransmitLeaseChange>();
        channel.TransmitLease.Changed += change => changes.Add(change.Change);
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));
        Task released = Reported(channel.TransmitLease, TransmitLeaseChange.Released);

        channel.TransmitLease.Release(onlyIf: 3).Should().BeTrue();

        askedFor.Should().Be(3);
        channel.TransmitLease.IsClosing.Should().BeTrue();
        channel.TransmitLease.Holder.Should().Be(3, "nobody else keys until the closing ident has gone");
        channel.EnqueueTransmit(0, Frame(0x41)).IsFaulted.Should().BeTrue();
        channel.TransmitLease.Take(0, TimeSpan.FromSeconds(60)).Granted.Should().BeFalse();
        changes.Should().Equal(TransmitLeaseChange.Taken);

        ident.SetResult();
        await released.WaitAsync(TimeSpan.FromMinutes(1));
        changes.Should().Equal(TransmitLeaseChange.Taken, TransmitLeaseChange.Released);
        channel.TransmitLease.Holder.Should().BeNull();
        channel.TransmitLease.IsClosing.Should().BeFalse();
    }

    [Fact]
    public async Task A_Closing_Transmission_That_Never_Goes_Lets_The_Lease_Go_After_The_Timeout()
    {
        (SoundModemChannel channel, FakeTimeProvider time) = Station();
        channel.TransmitLease.Closing = _ => new TaskCompletionSource().Task;
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));
        Task released = Reported(channel.TransmitLease, TransmitLeaseChange.Released);
        channel.TransmitLease.Release();

        time.Advance(TransmitLease.ClosingTimeout - TimeSpan.FromSeconds(1));
        channel.TransmitLease.Holder.Should().Be(3);

        time.Advance(TimeSpan.FromSeconds(1));
        await released.WaitAsync(TimeSpan.FromMinutes(1));
        channel.TransmitLease.Holder.Should().BeNull("a busy channel cannot hold everyone off for ever");
    }

    [Fact]
    public async Task An_Expiring_Lease_Closes_Too_And_Reports_The_Expiry_After_It()
    {
        (SoundModemChannel channel, FakeTimeProvider time) = Station();
        var ident = new TaskCompletionSource();
        channel.TransmitLease.Closing = _ => ident.Task;
        var changes = new List<TransmitLeaseChange>();
        channel.TransmitLease.Changed += change => changes.Add(change.Change);
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));
        Task expired = Reported(channel.TransmitLease, TransmitLeaseChange.Expired);

        time.Advance(TimeSpan.FromSeconds(60));
        channel.TransmitLease.IsClosing.Should().BeTrue();
        channel.TransmitLease.Holder.Should().Be(3);

        ident.SetResult();
        await expired.WaitAsync(TimeSpan.FromMinutes(1));
        changes.Should().Equal(TransmitLeaseChange.Taken, TransmitLeaseChange.Expired);
        channel.TransmitLease.Holder.Should().BeNull();
    }

    [Fact]
    public void A_Lease_With_Nothing_To_Close_With_Ends_At_Once()
    {
        (SoundModemChannel channel, _) = Station();
        channel.TransmitLease.Closing = _ => null;
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));

        channel.TransmitLease.Release().Should().BeTrue();
        channel.TransmitLease.Holder.Should().BeNull();
    }

    [Fact]
    public void Drop_Queued_Takes_The_Sub_Channels_Unsent_Frames_And_Nothing_Else()
    {
        (SoundModemChannel channel, _) = Station();
        var refused = new List<(int Sub, string Why)>();
        channel.TransmitRejected += (sub, _, why) => { lock (refused) { refused.Add((sub, why.Message)); } };
        object ident = new();
        channel.TransmitLease.Attribute(ident, 3);
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));

        Task[] frames = [channel.EnqueueTransmit(3, Frame(0x41)), channel.EnqueueTransmit(3, Frame(0x42))];
        Task identTask = channel.EnqueueTransmit(_ => new float[100], source: ident);

        channel.DropQueued(3).Should().Be(2);

        refused.Should().HaveCount(2);
        refused.Should().OnlyContain(r => r.Sub == 3 && r.Why == SoundModemChannel.DroppedByHolderReason);
        identTask.IsCompleted.Should().BeFalse("the ident is not one of the broadcast's frames");
        channel.DropQueued(3).Should().Be(0);
    }

    [Fact]
    public async Task A_Lease_That_Runs_Out_Drops_Its_Holders_Unsent_Frames_So_They_Never_Key()
    {
        // A head end that died mid-slot: its frames are still queued, its renewals have stopped.
        (SoundModemChannel channel, FakeTimeProvider time) = Station();
        var refused = new List<string>();
        channel.TransmitRejected += (_, _, why) => { lock (refused) { refused.Add(why.Message); } };
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));
        Task[] frames = [channel.EnqueueTransmit(3, Frame(0x41)), channel.EnqueueTransmit(3, Frame(0x42))];

        time.Advance(TimeSpan.FromSeconds(60));

        refused.Should().Equal(SoundModemChannel.ExpiredDropReason, SoundModemChannel.ExpiredDropReason);

        // Normal service resumes with nothing of the dead broadcast left to send.
        var ptt = new RecordingPtt();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Task normal = channel.EnqueueTransmit(0, Frame(0x43));
        Task transmitter = channel.RunTransmitterAsync(new FakeAudioOutput(SampleRate), ptt, cancellation.Token);
        await normal.WaitAsync(TimeSpan.FromMinutes(1));
        foreach (Task frame in frames)
        {
            Func<Task> waiting = () => frame.WaitAsync(TimeSpan.FromMinutes(1));
            await waiting.Should().ThrowAsync<InvalidOperationException>();
        }

        await cancellation.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }

        ptt.Events.Should().Equal(["key", "unkey"], "one keyup, for the frame queued after the lease, and none for the dead one's");
    }

    [Fact]
    public void A_Released_Lease_Keeps_Its_Queue()
    {
        (SoundModemChannel channel, _) = Station();
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));
        Task frame = channel.EnqueueTransmit(3, Frame(0x41));

        channel.TransmitLease.Release();

        frame.IsCompleted.Should().BeFalse("a graceful release drops nothing unless asked to");
    }

    /// <summary>Busy whenever carrier sense asks, and says when it first has after being armed.</summary>
    private sealed class AlwaysBusyModem : IModem
    {
        public volatile bool Armed;

        public TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Mode => "always-busy";

        public event Action<byte[], FrameQuality>? FrameDecoded
        {
            add { }
            remove { }
        }

        public bool CarrierDetect => false;

        public bool ChannelBusy
        {
            get
            {
                if (Armed)
                {
                    Asked.TrySetResult();
                }

                return true;
            }
        }

        public void Process(ReadOnlySpan<float> samples)
        {
        }

        public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds) => [];

        public void ResetCarrierState()
        {
        }
    }

    [Fact]
    public async Task A_Lease_With_A_Carrier_Wait_Sends_The_Holders_Frame_Once_It_Runs_Out()
    {
        var time = new FakeTimeProvider();
        var channel = new SoundModemChannel(SampleRate, time, randomSeed: 42);
        var busy = new AlwaysBusyModem();
        channel.AddModem(3, sink => new Afsk1200Modem(SampleRate, sink));
        channel.AddModem(1, _ => busy);
        channel.Csma.Persistence = 255;
        channel.Csma.SlotTimeMilliseconds = 100;
        var cut = new List<(int Sub, TimeSpan Waited)>();
        channel.TransmitLease.CarrierWaitCutShort += (sub, waited) => cut.Add((sub, waited));
        channel.TransmitLease.Take(3, TimeSpan.FromMinutes(5), maxCarrierWait: TimeSpan.FromSeconds(30));
        channel.TransmitLease.MaxCarrierWait.Should().Be(TimeSpan.FromSeconds(30));

        Task frame = channel.EnqueueTransmit(3, Frame(0x41));
        busy.Armed = true;
        var ptt = new RecordingPtt();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Task transmitter = channel.RunTransmitterAsync(new FakeAudioOutput(SampleRate), ptt, cancellation.Token);

        // The first time carrier sense finds the channel busy starts the wait. The clock is then
        // cranked rather than jumped: each slot's wait is set on the transmitter's own thread, and
        // a jump taken before one was set would be a jump nothing was waiting for.
        frame.IsCompleted.Should().BeFalse("the channel is busy and the clock has not moved");
        await busy.Asked.Task.WaitAsync(TimeSpan.FromMinutes(1));
        using var crank = new CancellationTokenSource();
        _ = Task.Run(
            async () =>
            {
                while (!crank.IsCancellationRequested)
                {
                    VirtualAir.Tick(time, TimeSpan.FromMilliseconds(100));
                    await Task.Delay(1, CancellationToken.None);
                }
            },
            CancellationToken.None);
        await frame.WaitAsync(TimeSpan.FromMinutes(1));
        await crank.CancelAsync();
        await cancellation.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }

        ptt.Events.Should().StartWith("key");
        cut.Should().ContainSingle().Which.Sub.Should().Be(3);
        cut[0].Waited.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void A_Renewal_Without_A_Carrier_Wait_Keeps_The_One_It_Had()
    {
        (SoundModemChannel channel, _) = Station();
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30));
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));
        channel.TransmitLease.MaxCarrierWait.Should().Be(TimeSpan.FromSeconds(30));

        channel.TransmitLease.Release();
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));
        channel.TransmitLease.MaxCarrierWait.Should().BeNull("a new lease starts with ordinary carrier sense");
    }

    private static async Task RunUntil(SoundModemChannel channel, RecordingPtt ptt, params Task[] settled)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Task transmitter = channel.RunTransmitterAsync(new FakeAudioOutput(SampleRate), ptt, cancellation.Token);
        foreach (Task task in settled)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromMinutes(1));
            }
            catch (InvalidOperationException)
            {
                // Refused, which is what most of these are waiting for.
            }
        }

        await cancellation.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task A_Drop_That_Lands_As_A_Burst_Is_Taken_Refuses_The_Burst_Too()
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 42);
        channel.AddModem(3, _ => new QuickPacker());
        channel.Csma.Persistence = 255;
        channel.TransmitLease.Take(3, TimeSpan.FromMinutes(5));
        var refused = new List<string>();
        channel.TransmitRejected += (_, _, why) => { lock (refused) { refused.Add(why.Message); } };
        int drops = 0;
        channel.TakingPackedBurst = () =>
        {
            if (Interlocked.Increment(ref drops) == 1)
            {
                channel.DropQueued(3);
            }
        };

        Task first = channel.EnqueueTransmit(3, Frame(0x41));
        Task second = channel.EnqueueTransmit(3, Frame(0x42));
        var ptt = new RecordingPtt();
        await RunUntil(channel, ptt, first, second);

        first.IsFaulted.Should().BeTrue();
        second.IsFaulted.Should().BeTrue();
        refused.Should().OnlyContain(r => r == SoundModemChannel.DroppedByHolderReason);
        ptt.Events.Should().BeEmpty("a dropped burst is neither put back nor keyed");
    }

    [Fact]
    public async Task A_Lease_That_Runs_Out_As_Its_Burst_Is_Taken_Refuses_The_Burst_Before_It_Keys()
    {
        var time = new FakeTimeProvider();
        var channel = new SoundModemChannel(SampleRate, time, randomSeed: 42);
        channel.AddModem(3, _ => new QuickPacker());
        channel.Csma.Persistence = 255;
        channel.TransmitLease.Closing = _ => new TaskCompletionSource().Task; // an ident still to go
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));
        int taken = 0;
        channel.TakingPackedBurst = () =>
        {
            // The head end died: its lease runs out at the moment its burst is in hand.
            if (Interlocked.Increment(ref taken) == 1)
            {
                time.Advance(TimeSpan.FromSeconds(60));
            }
        };

        Task burst = channel.EnqueueTransmit(3, Frame(0x41));
        var ptt = new RecordingPtt();
        await RunUntil(channel, ptt, burst);

        Func<Task> waiting = () => burst;
        (await waiting.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Be(SoundModemChannel.ExpiredDropReason);
        ptt.Events.Should().BeEmpty("a dead head end's burst never follows its lease out");
        channel.TransmitLease.IsClosing.Should().BeTrue();

        channel.EnqueueTransmit(3, Frame(0x42)).IsFaulted.Should().BeTrue(
            "nor does anything it queues while its lease closes");
    }

    [Fact]
    public void A_Lease_Is_Refused_At_Once_While_A_Receive_Window_Is_Opening_And_Granted_After()
    {
        (SoundModemChannel channel, _) = Station();
        TransmitLease lease = channel.TransmitLease;

        lease.TryBeginOpeningWindow(out int? holder).Should().BeTrue();
        holder.Should().BeNull();
        lease.TakeUnlessWindow(() => false, 3, TimeSpan.FromSeconds(60)).Should().BeNull(
            "the window is still talking to the rig, and a lease must not be granted underneath it");
        lease.Holder.Should().BeNull();

        lease.EndOpeningWindow();
        lease.TakeUnlessWindow(() => true, 3, TimeSpan.FromSeconds(60)).Should().BeNull(
            "once opened, the rig's own state says the window is there");
        lease.TakeUnlessWindow(() => false, 3, TimeSpan.FromSeconds(60))!.Value.Granted.Should().BeTrue();
    }

    [Fact]
    public void A_Receive_Window_Cannot_Begin_Opening_While_A_Lease_Is_Held()
    {
        (SoundModemChannel channel, _) = Station();
        TransmitLease lease = channel.TransmitLease;
        lease.Take(3, TimeSpan.FromSeconds(60)).Granted.Should().BeTrue();

        lease.TryBeginOpeningWindow(out int? holder).Should().BeFalse();
        holder.Should().Be(3);
        lease.TakeUnlessWindow(() => false, 3, TimeSpan.FromSeconds(60))!.Value.Renewed.Should().BeTrue(
            "a refused window leaves nothing behind to block the lease");
    }
}
