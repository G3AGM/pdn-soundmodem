using AwesomeAssertions;

namespace Packet.SoundModem.Tests;

/// <summary>
/// Rules about the order the daemon does things in at start-up, asserted against the source text.
/// </summary>
/// <remarks>
/// <para>The same trick as <see cref="SourceTextTests"/>, and for the same reason: the property
/// is real, it is not expressible as a unit test, and it is cheaper to read the file than to
/// leave it unguarded. The ordering here lives in the sound card device's open
/// (<c>AlsaStationDevice.Open</c> in <c>Devices/AlsaStationDevice.cs</c>, since #595), which only
/// runs against a real card, so nothing else in the suite would notice it moving.</para>
/// <para>Ugly, and much better than nothing for a rule whose violation is invisible in CI and
/// intermittent on hardware.</para>
/// </remarks>
public class StartUpOrderTests
{
    /// <summary>
    /// Every ALSA mixer call must be finished with before the capture stream is opened.
    /// </summary>
    /// <remarks>
    /// <para>A capture stream is losing audio as soon as its buffer is full, so everything
    /// between <c>snd_pcm_open</c> and the first steady reads is time the stream can overrun in,
    /// and an overrun the recovery cannot get out of reads as a dead device. Mixer work in that
    /// window is the avoidable kind: nothing in the pass needs the PCM.</para>
    /// <para>Measured on radio1, 2026-09-06, with the mixer pass inside that window: 10 runs dead
    /// out of 13, against 12 of 12 alive for the same source doing two fewer reads per control;
    /// with the pass moved ahead of the open, 10 of 10 alive. It presents as flaky hardware, not
    /// as a bug, and it cost thirteen bench runs to find. The mechanism was misread at the time
    /// as a mixer control transfer colliding with the first read's URB submission; the strace of
    /// the qpsk3600 start-up failure the same evening shows a 120 ms buffer overrunning while the
    /// modem's own first pass was compiled, then <c>-EIO</c> from a read 5 ms after the
    /// recovery's prepare. The buffer and the recovery are fixed in <c>AlsaPcm</c> and
    /// <c>PcmTransfer</c>; this rule is what keeps the gap short to begin with.</para>
    /// <para>This is one tidy-up away from coming back - somebody grouping "all the audio device
    /// setup" together would reintroduce it - so it is pinned here rather than left to the
    /// comment at the block. See CLAUDE.md, and issue #632 for the start-up on a card other than the bench CM108.</para>
    /// </remarks>
    [Fact]
    public void The_Mixer_Is_Read_And_Set_Before_The_Capture_Stream_Is_Opened()
    {
        // The ALSA open, mixer pass included, is the sound card device's own since #595.
        string program = Path.Combine(
            FindRepoRoot(), "src", "Packet.SoundModem.Daemon", "Devices", "AlsaStationDevice.cs");
        string source = File.ReadAllText(program);

        int mixerOpened = source.IndexOf("AlsaMixer.TryOpen(mixerCard", StringComparison.Ordinal);
        int mixerWorked = source.IndexOf("MixerRuntime.Start(", StringComparison.Ordinal);
        int captureOpened = source.IndexOf("new AlsaAudioInput(", StringComparison.Ordinal);
        int playbackOpened = source.IndexOf("new AlsaAudioOutput(", StringComparison.Ordinal);

        mixerOpened.Should().BeGreaterThan(
            -1, "the station's mixer is opened with AlsaMixer.TryOpen(mixerCard, ...); if that "
              + "moved or was renamed, this test needs updating rather than deleting");
        mixerWorked.Should().BeGreaterThan(
            -1, "the mixer is read and set by MixerRuntime.Start(...); if that moved or was "
              + "renamed, this test needs updating rather than deleting");
        captureOpened.Should().BeGreaterThan(
            -1, "the capture stream is opened with new AlsaAudioInput(...)");
        playbackOpened.Should().BeGreaterThan(
            -1, "the playback stream is opened with new AlsaAudioOutput(...)");

        // The boundary is whichever stream is opened FIRST on the device, not the capture one.
        // Playback is constructed a few lines earlier on the same card, so anchoring on the
        // capture construction alone would leave room to slip mixer work between the two.
        int streamOpened = Math.Min(captureOpened, playbackOpened);

        mixerOpened.Should().BeLessThan(
            streamOpened,
            "the mixer is opened before either stream on the device, which is where the whole "
            + "block belongs");

        // Both, because the open is not the work. Opening the mixer is a handful of calls; the
        // dB range reads, the sets and the read-backs are the bulk of the control-transfer
        // traffic and they are inside MixerRuntime.Start. Pinning only the open would let
        // somebody name the card in the journal early and defer the apply, which is the shape a
        // future refactor would most plausibly take and puts the traffic straight back in the
        // window. Pin the LAST mixer call, which is what the rule actually says.
        mixerWorked.Should().BeLessThan(
            streamOpened,
            "every ALSA mixer call must finish BEFORE the capture stream is opened. Everything "
            + "between the open and the first steady reads is time the capture stream can overrun "
            + "in, and the station never receives. It is intermittent, so it presents as flaky "
            + "hardware: 10 dead runs out of 13 on the bench CM108, against 10 of 10 alive with "
            + "the mixer pass ahead of the open. Nothing in the mixer pass needs the PCM - "
            + "--mixer-show reads a card on a running station - so keep the whole block above the "
            + "AlsaAudioOutput/AlsaAudioInput construction");
    }

    /// <summary>
    /// The mixer block also has to sit above the transmit stream, which is opened first of the
    /// two and is the same PCM device.
    /// </summary>
    [Fact]
    public void The_Mixer_Is_Also_Read_Before_The_Playback_Stream_Is_Opened()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Packet.SoundModem.Daemon", "Devices", "AlsaStationDevice.cs"));

        int playbackOpened = source.IndexOf("new AlsaAudioOutput(", StringComparison.Ordinal);

        source.IndexOf("AlsaMixer.TryOpen(mixerCard", StringComparison.Ordinal)
            .Should().BeLessThan(
                playbackOpened,
                "the playback stream is opened just before the capture one, on the same device");

        // As above: the work, not just the open.
        source.IndexOf("MixerRuntime.Start(", StringComparison.Ordinal)
            .Should().BeLessThan(
                playbackOpened,
                "MixerRuntime.Start is where the card is actually read and set, so it is the call "
                + "that has to be finished with before either stream on the device is opened");
    }

    /// <summary>
    /// The sound-card IQ device (#596) opens a capture stream too, and the same rule holds there:
    /// it does no mixer work today, and any it gains goes above the capture's construction.
    /// </summary>
    [Fact]
    public void The_Iq_Card_Does_No_Mixer_Work_After_Its_Capture_Stream_Is_Opened()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Packet.SoundModem.Daemon", "Devices", "IqCardStationDevice.cs"));

        int captureOpened = source.IndexOf("new AlsaStereoCapture(", StringComparison.Ordinal);
        captureOpened.Should().BeGreaterThan(
            -1, "the IQ card's capture is opened with new AlsaStereoCapture(...); if that moved or "
              + "was renamed, this test needs updating rather than deleting");

        string afterOpen = source[captureOpened..];
        foreach (string mixerCall in new[] { "AlsaMixer.TryOpen(", "MixerRuntime.Start(", "MixerSetup.TryApply(" })
        {
            afterOpen.Should().NotContain(
                mixerCall,
                "every ALSA mixer call must finish before the PCM is opened, so on the IQ card it "
                + "belongs above the AlsaStereoCapture construction");
        }
    }

    /// <summary>
    /// And no mixer work comes back into <c>Program.cs</c> after the device is opened, where the
    /// two tests above, which read the device's own file, could not see it.
    /// </summary>
    /// <remarks>
    /// Anchored on the open rather than on the whole file, because <c>--mixer-show</c> reads and
    /// sets a card near the top of <c>Program.cs</c> and exits before any PCM is opened, which is
    /// allowed.
    /// </remarks>
    [Fact]
    public void Program_Cs_Does_No_Mixer_Work_Once_The_Device_Is_Open()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Packet.SoundModem.Daemon", "Program.cs"));

        int opened = source.IndexOf("stationDevice.OpenAsync(", StringComparison.Ordinal);
        opened.Should().BeGreaterThan(
            -1, "the station's device is opened with stationDevice.OpenAsync(...); if that moved "
              + "or was renamed, this test needs updating rather than deleting");

        string afterOpen = source[opened..];
        foreach (string mixerCall in new[] { "AlsaMixer.TryOpen(", "MixerRuntime.Start(", "MixerSetup.TryApply(" })
        {
            afterOpen.Should().NotContain(
                mixerCall,
                "every ALSA mixer call must finish before the PCM is opened, so it belongs in "
                + "AlsaStationDevice.Open above the AlsaAudioOutput/AlsaAudioInput construction, "
                + "not in Program.cs after the device has opened its streams");
        }
    }

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
