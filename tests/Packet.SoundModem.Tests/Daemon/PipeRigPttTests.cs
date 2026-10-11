using M0LTE.Radio.Audio;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Rig;
using Packet.SoundModem.Tests.Rig;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// #655: a <c>pipe:</c> or <c>--wav-loop</c> station with a <c>rig</c> section and
/// <c>"ptt": {"type": "rigctld"}</c> used to crash at start-up, because those devices always
/// handed back their own <see cref="NullPtt"/> regardless of what <c>ptt</c> said, and the rig
/// (keying through rigctld) then refused to be guarded with a line beside it
/// (<see cref="RigStation.Guard"/>). Now they key through the rig exactly like a sound-card
/// station does, which is what made this useful in the first place - proving a rig tuning window
/// (#585) on a loopback station with no hardware at all.
/// </summary>
public sealed class PipeRigPttTests : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pdnsm-piperig").FullName;
    private readonly FakeRigctld _fake = new();
    private readonly List<RigControl> _rigs = [];

    public async ValueTask DisposeAsync()
    {
        foreach (RigControl rig in _rigs)
        {
            await rig.DisposeAsync();
        }

        await _fake.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    private async Task<RigControl> StartedRig(bool keysThroughRig)
    {
        var rig = new RigControl(new RigControlOptions
        {
            ReplyTimeout = TimeSpan.FromSeconds(30),
            ConnectTimeout = TimeSpan.FromSeconds(30),
            RestoreFile = null,
            TransmitPending = () => false,
            Endpoint = _fake.Endpoint,
            KeysThroughRig = keysThroughRig,
        });
        _rigs.Add(rig);
        (await rig.StartAsync(CancellationToken.None)).Should().BeTrue();
        return rig;
    }

    private DeviceOpenContext Context(PttConfig? ptt, RigControl? rig) => new()
    {
        DspRate = 12000,
        ConfigPath = null,
        Journal = new StationJournal("", _ => { }, _ => { }),
        Channel = new SoundModemChannel(12000, randomSeed: 1),
        Waterfall = (WaterfallWebServer?)null,
        RadioLost = () => { },
        Cancellation = CancellationToken.None,
        Sideband = "usb",
        ReceiveDialHz = null,
        FlexPacketBuffer = 3,
        FlexTuning = new Packet.SoundModem.FlexRadio.FlexTuning(),
        TransmitBands = [],
        CaptureRate = 12000,
        CaptureDeviceKey = null,
        PlaybackDeviceKey = null,
        Alsa = null,
        Ptt = ptt,
        Rig = rig,
    };

    private string Pipe(string name)
    {
        string inPipe = Path.Combine(_dir, $"{name}-in");
        string outPipe = Path.Combine(_dir, $"{name}-out");
        return $"pipe:{inPipe},{outPipe},12000";
    }

    [Fact]
    public async Task A_Pipe_Station_With_Rigctld_Ptt_Keys_Through_The_Rig_Rather_Than_Crashing()
    {
        RigControl rig = await StartedRig(keysThroughRig: true);
        var kind = new PipeDeviceKind();
        var device = (PipeStationDevice)kind.Create(Pipe("a"), new DeviceSettings(null, HasWaterfall: false));

        DeviceOpening opened = await device.OpenAsync(
            Context(new PttConfig { Type = "rigctld" }, rig));

        opened.ExitCode.Should().BeNull("a rig section on a pipe device is allowed");
        opened.Ptt.Should().NotBeOfType<NullPtt>("it should key through the rig, not nowhere");

        // What used to throw: Program.cs guards the station's PTT with the rig before handing it
        // to the transmitter. A pipe device's own NullPtt made this throw; the rig's own PTT
        // passes straight through.
        Action guard = () => rig.Guard(opened.Ptt);
        guard.Should().NotThrow();

        opened.Ptt.Key();
        _fake.Ptt.Should().BeTrue("keying the device's PTT should key the rig");
        opened.Ptt.Unkey();
        _fake.Ptt.Should().BeFalse();
    }

    [Fact]
    public async Task A_Wav_Loop_Station_With_Rigctld_Ptt_Keys_Through_The_Rig_Rather_Than_Crashing()
    {
        RigControl rig = await StartedRig(keysThroughRig: true);
        string recording = Path.Combine(_dir, "loop.wav");
        WavFile.WriteMono(recording, new float[1200], 12000);
        var named = (PipeStationDevice)new PipeDeviceKind().Create(
            Pipe("b"), new DeviceSettings(null, HasWaterfall: false));
        var device = new WavLoopStationDevice(named, recording);

        DeviceOpening opened = await device.OpenAsync(
            Context(new PttConfig { Type = "rigctld" }, rig));

        opened.ExitCode.Should().BeNull();
        opened.Ptt.Should().NotBeOfType<NullPtt>();

        Action guard = () => rig.Guard(opened.Ptt);
        guard.Should().NotThrow();

        opened.Ptt.Key();
        _fake.Ptt.Should().BeTrue();
        opened.Ptt.Unkey();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("serial")]
    public async Task A_Pipe_Station_With_No_Rigctld_Ptt_Still_Keys_Nothing(string? type)
    {
        PttConfig? ptt = type is null ? null : new PttConfig { Type = type, Device = "/dev/ttyUSB0" };
        var kind = new PipeDeviceKind();
        var device = (PipeStationDevice)kind.Create(Pipe("c"), new DeviceSettings(null, HasWaterfall: false));

        DeviceOpening opened = await device.OpenAsync(Context(ptt, rig: null));

        opened.ExitCode.Should().BeNull();
        opened.Ptt.Should().BeOfType<NullPtt>("a pipe device has no keying line of its own, as before");
    }

    [Fact]
    public async Task A_Pipe_Station_With_Rigctld_Ptt_But_No_Rig_Section_Still_Keys_Nothing()
    {
        // Config validation (DaemonConfig.ValidateRig) refuses this combination before start-up
        // ever opens a device; this pins the device's own defence as a second line, not the
        // primary one.
        var kind = new PipeDeviceKind();
        var device = (PipeStationDevice)kind.Create(Pipe("d"), new DeviceSettings(null, HasWaterfall: false));

        DeviceOpening opened = await device.OpenAsync(
            Context(new PttConfig { Type = "rigctld" }, rig: null));

        opened.ExitCode.Should().BeNull();
        opened.Ptt.Should().BeOfType<NullPtt>();
    }
}
