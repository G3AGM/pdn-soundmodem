using M0LTE.Radio.Audio;
using Packet.SoundModem.Rig;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// A recording standing in for the capture device (<c>--wav-loop</c>): same decimation path, no
/// transmit side.
/// </summary>
/// <remarks>
/// <para>Not a kind of its own. <c>--wav-loop</c> is a flag rather than a device string, and the
/// station it replaces the audio of is still the one <c>device</c> names: every question start-up
/// asks about the device (whether <c>--ptt</c> is taken, whether the capture rate applies,
/// whether the dial is the station's to set) is answered by the named device, exactly as it was
/// before there was a seam. Only the open, and the dead-feed family, are the recording's.</para>
/// </remarks>
internal sealed class WavLoopStationDevice(StationDevice named, string path)
    : StationDevice(named.Kind, named.Spec)
{
    /// <summary>The device the recording replaces the audio of.</summary>
    public StationDevice Named { get; } = named;

    /// <summary>The recording.</summary>
    public string Recording { get; } = path;

    /// <inheritdoc/>
    public override bool CaptureRateApplies => Named.CaptureRateApplies;

    /// <inheritdoc/>
    public override bool OwnsTheRadio => Named.OwnsTheRadio;

    /// <inheritdoc/>
    public override bool SelfTunes => Named.SelfTunes;

    /// <inheritdoc/>
    public override string? NoReceiveDialRefusal => Named.NoReceiveDialRefusal;

    /// <inheritdoc/>
    public override string? ReceiveOnlyReason => Named.ReceiveOnlyReason;

    /// <inheritdoc/>
    public override string? PttRefusal => Named.PttRefusal;

    /// <inheritdoc/>
    public override bool ReportsTransmitFilter => Named.ReportsTransmitFilter;

    /// <inheritdoc/>
    public override MailcastRadioKind MailcastKind => Named.MailcastKind;

    /// <summary>A bench input with no radio behind it.</summary>
    public override DeadFeedDevice DeadFeedKind => DeadFeedDevice.WavLoop;

    /// <inheritdoc/>
    public override bool ClosesItsOwnStreams => Named.ClosesItsOwnStreams;

    /// <inheritdoc/>
    public override string? SettingsProblem => Named.SettingsProblem;

    /// <inheritdoc/>
    public override Task<DeviceOpening> OpenAsync(DeviceOpenContext context)
    {
        var wavLoop = new WavLoopAudioInput(Recording);
        if (wavLoop.SampleRate % context.DspRate != 0)
        {
            Console.Error.WriteLine($"--wav-loop rate {wavLoop.SampleRate} is not a multiple of {context.DspRate}");
            return Task.FromResult(DeviceOpening.Refused(2));
        }

        Console.WriteLine($"audio: wav-loop {Recording} {wavLoop.SampleRate} Hz -> {context.DspRate} Hz");
        IPttControl ptt = RigStation.PttForTestDevice(context.Ptt, context.Rig);
        if (context.Rig is RigControl rig && ptt is not NullPtt)
        {
            Console.WriteLine($"ptt: rigctld {rig.Endpoint} (T 1 to key, T 0 to unkey)");
        }

        return Task.FromResult(new DeviceOpening
        {
            Ptt = ptt,
            Playback = new NullAudioOutput(context.DspRate),
            Input = wavLoop,
        });
    }
}
