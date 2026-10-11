using AwesomeAssertions;
using Packet.SoundModem.Audio;

namespace Packet.SoundModem.Tests.Audio;

/// <summary>
/// The ALSA transfer loop and its xrun recovery, against a scripted card.
/// </summary>
/// <remarks>
/// <para>These are the sequences the bench produced. On radio1 (2026-09-06) a qpsk3600 station
/// on a CM108 died at every start-up: the first pass through the modem took 150 ms, the 120 ms
/// capture buffer overran, and the recovery - prepare, then read again 5 ms later - got
/// <c>-EIO</c> and the daemon called the device dead. Both halves of that are fixed here, and
/// the third case is the one that must still be allowed to happen: a card that has really gone
/// is not worth retrying forever.</para>
/// </remarks>
public class PcmTransferTests
{
    private const int Epipe = -32;
    private const int Eio = -5;
    private const int Enodev = -19;

    [Fact]
    public void An_Overrun_Is_Recovered_And_The_Read_Completes()
    {
        var card = new ScriptedPcm(isCapture: true, Epipe, 480);

        int frames = PcmTransfer.Run(card, 480, out int failure);

        frames.Should().Be(480);
        failure.Should().Be(0);
        card.Xruns.Should().Be(1, "an overrun is audio that was lost, and the operator is told");
        card.Calls.Should().Equal(
            "transfer", "recover", "start", "transfer");
        card.Pauses.Should().BeEmpty("the ordinary xrun recovers at once and waiting costs audio");
    }

    [Fact]
    public void Stopped_At_An_Overrun_A_Read_Returns_What_Came_Before_It_And_The_Next_One_Counts_It()
    {
        // An IQ capture (#596) is told where a gap lies, so a read must not span one. ALSA keeps
        // answering EPIPE until the stream is prepared, which the script stands in for by
        // answering it twice.
        var card = new ScriptedPcm(isCapture: true, 200, Epipe, Epipe, 480);

        int before = PcmTransfer.Run(card, 480, out int failure, stopAtXrun: true);

        before.Should().Be(200, "the frames before the overrun, and none after it");
        failure.Should().Be(0);
        card.Xruns.Should().Be(0, "counted by the read that returns the frames after the hole");
        card.Calls.Should().Equal("transfer", "transfer");

        int after = PcmTransfer.Run(card, 480, out failure, stopAtXrun: true);

        after.Should().Be(480);
        failure.Should().Be(0);
        card.Xruns.Should().Be(1);
        card.Calls.Should().Equal("transfer", "transfer", "transfer", "recover", "start", "transfer");
    }

    [Fact]
    public void A_Card_That_Will_Not_Restart_Yet_Is_Given_A_Pause_And_Another_Go()
    {
        // The bench failure exactly: EPIPE, prepare and start, and the card is still not ready.
        var card = new ScriptedPcm(isCapture: true, Epipe, Eio, 480);

        int frames = PcmTransfer.Run(card, 480, out int failure);

        frames.Should().Be(480);
        failure.Should().Be(0, "the second attempt worked, so nothing died");
        card.Pauses.Should().HaveCount(
            1, "the retry waits for the endpoint to finish stopping before asking again");
        card.Pauses[0].Should().Be(PcmTransfer.RecoveryPauseMilliseconds);
        card.Xruns.Should().Be(1, "the EIO is the recovery not taking, not a second hole in the audio");
    }

    [Fact]
    public void A_Device_That_Stays_Dead_Gives_Up_And_Says_Why()
    {
        var card = new ScriptedPcm(isCapture: true) { Always = Eio };

        int frames = PcmTransfer.Run(card, 480, out int failure);

        frames.Should().Be(0);
        failure.Should().Be(Eio, "the caller reports the device's own error, as it always did");
        card.Pauses.Should().HaveCount(
            PcmTransfer.MaxRecoveryAttempts - 1, "one immediate attempt, then the rest spaced out");
        card.Calls.Count(c => c == "transfer")
            .Should().Be(PcmTransfer.MaxRecoveryAttempts + 1, "the retries are bounded");
    }

    [Fact]
    public void An_Unplugged_Card_Is_Not_Retried_At_All()
    {
        // ENODEV is not a stream that can be brought back, and spending a fifth of a second
        // pretending otherwise only delays the restart that does fix it.
        var card = new ScriptedPcm(isCapture: true) { Always = Enodev };

        int frames = PcmTransfer.Run(card, 480, out int failure);

        frames.Should().Be(0);
        failure.Should().Be(Enodev);
        card.Calls.Should().Equal("transfer");
    }

    [Fact]
    public void A_Recovery_The_Library_Will_Not_Do_Is_Done_By_Hand()
    {
        // snd_pcm_recover knows EPIPE and ESTRPIPE and hands everything else straight back.
        var card = new ScriptedPcm(isCapture: true, Eio, 480) { RecoverFails = true };

        PcmTransfer.Run(card, 480, out int failure);

        failure.Should().Be(0);
        card.Calls.Should().Equal("transfer", "recover", "prepare", "start", "transfer");
    }

    [Fact]
    public void Playback_Is_Prepared_But_Not_Started_By_Hand()
    {
        // An underrun recovers the same way, except that starting a playback stream on an empty
        // buffer would only underrun again: it starts itself when enough has been written.
        var card = new ScriptedPcm(isCapture: false, Epipe, 480);

        PcmTransfer.Run(card, 480, out int failure);

        failure.Should().Be(0);
        card.Calls.Should().Equal("transfer", "recover", "transfer");
        card.Xruns.Should().Be(1);
    }

    [Fact]
    public void A_Short_Transfer_Is_Finished_Off_Rather_Than_Returned()
    {
        // ALSA is allowed to hand back fewer frames than were asked for; the loop's whole job is
        // to hide that from the modem.
        var card = new ScriptedPcm(isCapture: true, 100, 300, 80);

        int frames = PcmTransfer.Run(card, 480, out int failure);

        frames.Should().Be(480);
        failure.Should().Be(0);
        // Each go starts where the last one stopped.
        card.Offsets.Should().Equal(0, 100, 400);
        card.Wanted.Should().Equal(480, 380, 80);
    }

    [Fact]
    public void A_Stall_Recovered_From_Does_Not_Count_Against_The_Next_One()
    {
        // Otherwise a station up for a week would be one hiccup away from the give-up threshold.
        var card = new ScriptedPcm(isCapture: true, Epipe, 240, Epipe, 240);

        int frames = PcmTransfer.Run(card, 480, out int failure);

        frames.Should().Be(480);
        failure.Should().Be(0);
        card.Pauses.Should().BeEmpty("neither recovery needed a second attempt");
        card.Xruns.Should().Be(2);
    }

    /// <summary>A card that answers from a script: frame counts, or negative errnos.</summary>
    private sealed class ScriptedPcm(bool isCapture, params int[] answers) : IPcmTransfer
    {
        private readonly Queue<int> _answers = new(answers);

        internal List<string> Calls { get; } = [];

        internal List<int> Pauses { get; } = [];

        internal List<int> Offsets { get; } = [];

        internal List<int> Wanted { get; } = [];

        internal int Xruns { get; private set; }

        /// <summary>Set to answer every transfer the same way, however many there are.</summary>
        internal int? Always { get; init; }

        /// <summary>snd_pcm_recover handing the error back untouched, as it does for EIO.</summary>
        internal bool RecoverFails { get; init; }

        public bool IsCapture { get; } = isCapture;

        public long Transfer(int frameOffset, int frames)
        {
            Calls.Add("transfer");
            Offsets.Add(frameOffset);
            Wanted.Add(frames);
            return Always ?? (_answers.Count > 0 ? _answers.Dequeue() : frames);
        }

        public int Recover(int error)
        {
            Calls.Add("recover");
            return RecoverFails ? error : 0;
        }

        public int Prepare()
        {
            Calls.Add("prepare");
            return 0;
        }

        public int Start()
        {
            Calls.Add("start");
            return 0;
        }

        public void CountXrun() => Xruns++;

        public void Pause(int milliseconds) => Pauses.Add(milliseconds);
    }
}
