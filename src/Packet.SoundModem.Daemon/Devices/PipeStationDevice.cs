using M0LTE.Radio.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Rig;

namespace Packet.SoundModem.Daemon;

/// <summary>The <c>pipe:</c> kind: see <see cref="PipeAudio"/>.</summary>
internal sealed class PipeDeviceKind : DeviceKind
{
    /// <inheritdoc/>
    public override string Name => "pipe";

    /// <inheritdoc/>
    public override string Spelling => "pipe:IN,OUT[,RATE]";

    /// <inheritdoc/>
    public override bool Matches(string device) => PipeAudio.IsPipe(device);

    /// <inheritdoc/>
    public override StationDevice Create(string device, DeviceSettings settings) =>
        new PipeStationDevice(this, device);
}

/// <summary>
/// Two FIFOs standing in for a sound card and a radio, so two daemons can be on the same air
/// with no hardware between them. See <see cref="PipeAudio"/> for what this deliberately does not
/// model.
/// </summary>
/// <remarks>
/// It keeps a sound card's answers to every question start-up asks, the capture-rate check
/// included, although it reads its rate from the device string: that is how it has always been
/// checked, and this seam changes no behaviour.
/// </remarks>
internal sealed class PipeStationDevice(DeviceKind kind, string spec) : StationDevice(kind, spec)
{
    /// <inheritdoc/>
    public override Task<DeviceOpening> OpenAsync(DeviceOpenContext context)
    {
        int dspRate = context.DspRate;
        try
        {
            (string inPipe, string outPipe, int pipeRate) = PipeAudio.Parse(Spec);
            if (pipeRate % dspRate != 0)
            {
                Console.Error.WriteLine(
                    $"pipe rate {pipeRate} is not a multiple of the channel's {dspRate} Hz");
                return Task.FromResult(DeviceOpening.Refused(2));
            }

            var pipeOut = new PipeAudioOutput(outPipe, pipeRate);
            IAudioOutput playback = pipeRate == dspRate
                ? pipeOut
                : new UpsamplingAudioOutput(pipeOut, dspRate);
            var input = new PipeAudioInput(inPipe, pipeRate);
            Console.WriteLine($"audio: pipe in={inPipe} out={outPipe} {pipeRate} Hz -> {dspRate} Hz");
            IPttControl ptt = RigStation.PttForTestDevice(context.Ptt, context.Rig);
            if (context.Rig is RigControl rig && ptt is not NullPtt)
            {
                Console.WriteLine($"ptt: rigctld {rig.Endpoint} (T 1 to key, T 0 to unkey)");
            }

            return Task.FromResult(new DeviceOpening
            {
                Ptt = ptt,
                Playback = playback,
                Input = input,
            });
        }
        catch (Exception failure) when (failure is InvalidDataException or IOException
            or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"audio: {failure.Message}");
            return Task.FromResult(DeviceOpening.Refused(2));
        }
    }
}
