using M0LTE.Radio.Audio;
using Packet.SoundModem.Iq;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Rig;

namespace Packet.SoundModem.Daemon;

/// <summary>The <c>iqpipe:</c> kind: raw IQ from a command, a FIFO or standard input, through
/// the IQ front end, receive only.</summary>
internal sealed class IqPipeDeviceKind : DeviceKind
{
    /// <summary>The prefix.</summary>
    public const string Prefix = "iqpipe:";

    /// <inheritdoc/>
    public override string Name => "iqpipe";

    /// <inheritdoc/>
    public override string Spelling => "iqpipe:[FIFO|-]";

    /// <inheritdoc/>
    public override bool Matches(string device) => device.StartsWith(Prefix, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override StationDevice Create(string device, DeviceSettings settings) =>
        new IqPipeStationDevice(this, device, settings.IqPipe);

    /// <inheritdoc/>
    public override string? TransmitTestRefusal =>
        "tx test: refused, this station's audio comes from an SDR receiving through iqpipe:, "
        + "which has no transmitter - there is nothing here to key";

    /// <inheritdoc/>
    public override string? RigRefusal(string device) =>
        $"\"rig\" is set and \"device\" is \"{device}\", an SDR's IQ, which is tuned by the "
        + "band plan itself and has no rig to control - remove \"rig\".";
}

/// <summary>
/// Raw IQ from a pipe: <c>iqpipe:</c> runs <c>"iqpipe"."command"</c> (<c>rx_sdr</c>,
/// <c>rtl_sdr</c>) and reads its standard output, <c>iqpipe:PATH</c> reads a FIFO another
/// program writes, <c>iqpipe:-</c> reads standard input. The IQ goes through the one IQ front end
/// (<see cref="IqReceiver"/>), which filters and demodulates the band plan's channel itself.
/// Receive only.
/// </summary>
internal sealed class IqPipeStationDevice : StationDevice
{
    private readonly IqPipeConfig? _config;
    private readonly string _path;

    public IqPipeStationDevice(DeviceKind kind, string spec, IqPipeConfig? config)
        : base(kind, spec)
    {
        _config = config;
        _path = spec[IqPipeDeviceKind.Prefix.Length..].Trim();
    }

    /// <summary>Whether the device runs the configured command.</summary>
    public bool RunsCommand => _path.Length == 0;

    /// <summary>Whether it reads standard input.</summary>
    public bool ReadsStandardInput => _path == "-";

    /// <summary>Its own sample clock: <c>--capture-rate</c> is an ALSA concept.</summary>
    public override bool CaptureRateApplies => false;

    /// <inheritdoc/>
    public override bool SelfTunes => true;

    /// <inheritdoc/>
    public override string? NoReceiveDialRefusal =>
        $"\"device\": \"{Spec}\" hands this station a band of IQ, and the station has to be told "
        + "where in it to listen. Give every modem an \"rfFrequency\" and the dial is worked out "
        + "from them, or set \"dialFrequency\" to pin it.";

    /// <inheritdoc/>
    public override string? ReceiveOnlyReason =>
        $"this station receives only: its audio comes from an SDR through \"{Spec}\", which has no "
        + "transmitter.";

    /// <inheritdoc/>
    public override string? PttRefusal =>
        "--device iqpipe: is a receive-only station - an SDR's IQ has no transmitter behind it, "
        + "so there is nothing for a PTT line to key. Remove \"ptt\".";

    /// <inheritdoc/>
    public override string? SettingsProblem => Problem(Spec, _config);

    /// <summary>Why <paramref name="spec"/> with <paramref name="config"/> cannot run, or null.</summary>
    internal static string? Problem(string spec, IqPipeConfig? config)
    {
        string path = spec[IqPipeDeviceKind.Prefix.Length..].Trim();
        bool runsCommand = path.Length == 0;
        if (config is null)
        {
            return $"\"device\": \"{spec}\" needs an \"iqpipe\" section saying what the IQ is: "
                + "\"format\" (" + IqSampleFormats.Spellings + ") and \"rate\""
                + (runsCommand ? ", and the \"command\" that makes it" : ", and its \"centreFrequency\"");
        }

        if (!IqSampleFormats.TryParse(config.Format, out _))
        {
            return config.Format is null
                ? $"\"iqpipe\".\"format\" is missing: say how the IQ is laid out, {IqSampleFormats.Spellings}"
                : $"\"iqpipe\".\"format\" is \"{config.Format}\"; it has to be {IqSampleFormats.Spellings}";
        }

        if (config.Rate is not int rate || rate < 1)
        {
            return "\"iqpipe\".\"rate\" has to be the IQ's sample rate, in samples per second";
        }

        bool hasCommand = !string.IsNullOrWhiteSpace(config.Command);
        if (runsCommand && !hasCommand)
        {
            return $"\"device\": \"{spec}\" runs \"iqpipe\".\"command\", and there is none. Set it, e.g. "
                + "\"rx_sdr -F CF32 -s {rate} -f {frequency} -\", or name a FIFO: \"iqpipe:/run/iq\"";
        }

        if (!runsCommand && hasCommand)
        {
            return $"\"device\": \"{spec}\" reads {(path == "-" ? "standard input" : path)}, and "
                + "\"iqpipe\".\"command\" is set too. Use \"device\": \"iqpipe:\" to run the command, or "
                + "remove it.";
        }

        bool tunes = hasCommand && config.Command!.Contains(ChildProcessIqFeedLauncher.FrequencyPlaceholder, StringComparison.Ordinal);
        if (config.CentreFrequency is null && !tunes)
        {
            return "\"iqpipe\".\"centreFrequency\" is missing: say what RF frequency, in Hz, the IQ is centred on"
                + (hasCommand ? ", or put {frequency} in the command for the station to choose it" : "");
        }

        if (config.CentreFrequency is double centre && (!double.IsFinite(centre) || centre <= 0))
        {
            return "\"iqpipe\".\"centreFrequency\" has to be a frequency above 0 Hz";
        }

        if (config.Gain is double gain && (!double.IsFinite(gain) || gain <= 0))
        {
            return "\"iqpipe\".\"gain\" has to be above 0";
        }

        return null;
    }

    /// <summary>The front end channel for a station on <paramref name="sideband"/> listening at
    /// <paramref name="offsetHz"/> from the centre, with <paramref name="modes"/> on it.</summary>
    /// <remarks>On FM the IF is the widest the modes need and the deviation the largest any is
    /// sent with, so no mode on the channel is trimmed or clipped.</remarks>
    internal static IqChannelSpec ChannelFor(string sideband, double offsetHz, IEnumerable<string> modes, float gain)
    {
        if (RfPlan.IsFmRadio(sideband))
        {
            var spec = new IqChannelSpec { OffsetHz = offsetHz, Demodulation = IqDemodulation.Fm, Gain = gain };
            double ifHz = 0;
            double deviationHz = 0;
            foreach (string mode in modes)
            {
                if (FmModeProfiles.For(mode) is null)
                {
                    continue;
                }

                IqChannelSpec forMode = IqChannelSpec.ForFmMode(mode, offsetHz);
                ifHz = Math.Max(ifHz, forMode.FmIfBandwidthHz);
                deviationHz = Math.Max(deviationHz, forMode.FmDeviationHz);
            }

            return ifHz > 0
                ? spec with { FmIfBandwidthHz = ifHz, FmDeviationHz = deviationHz }
                : spec;
        }

        return new IqChannelSpec
        {
            OffsetHz = offsetHz,
            Demodulation = sideband.Equals("lsb", StringComparison.OrdinalIgnoreCase) ? IqDemodulation.Lsb : IqDemodulation.Usb,
            Gain = gain,
        };
    }

    /// <inheritdoc/>
    public override Task<DeviceOpening> OpenAsync(DeviceOpenContext context)
    {
        IqPipeConfig config = _config!;
        StationJournal journal = context.Journal;
        IqSampleFormats.TryParse(config.Format, out IqSampleFormat format);
        int rate = config.Rate!.Value;
        double dialHz = context.ReceiveDialHz!.Value;

        IIqFeedLauncher launcher;
        if (RunsCommand)
        {
            launcher = new ChildProcessIqFeedLauncher(config.Command!.Trim(), journal.Write, TimeProvider.System);
        }
        else if (ReadsStandardInput)
        {
            launcher = new StandardInputIqFeedLauncher();
        }
        else
        {
            try
            {
                PipeAudio.EnsureFifo(_path);
            }
            catch (IOException e)
            {
                Console.Error.WriteLine($"\"device\": \"{Spec}\": {e.Message}");
                return Task.FromResult(DeviceOpening.Refused(2));
            }

            launcher = new FifoIqFeedLauncher(_path);
        }

        double dcClear = IqTuning.DcClearOffsetHz(rate);
        double centreHz = config.CentreFrequency ?? (dialHz - dcClear);
        double offsetHz = dialHz - centreHz;
        IqChannelSpec spec = ChannelFor(
            context.Sideband, offsetHz, ModesOf(context), (float)(config.Gain ?? 1.0));

        var source = new IqPipeSource(launcher, new IqPipeSourceOptions
        {
            Format = format,
            Rate = rate,
            CentreHz = centreHz,
            Journal = journal.Write,
            JournalError = journal.WriteError,
        });

        IqReceiver receiver;
        try
        {
            receiver = new IqReceiver(source, [spec], new IqReceiverOptions
            {
                DspRate = context.DspRate,
                SwapIq = config.SwapIq,
                Journal = journal.Write,
            });
        }
        catch (ArgumentException e)
        {
            source.Dispose();
            Console.Error.WriteLine(
                $"\"device\": \"{Spec}\" at {rate} S/s centred on {RfPlan.Mhz(centreHz)} cannot carry the "
                + $"channel at {RfPlan.Mhz(dialHz)}: {e.Message}. Move \"iqpipe\".\"centreFrequency\" "
                + "or raise the rate.");
            return Task.FromResult(DeviceOpening.Refused(2));
        }

        if (RunsCommand)
        {
            try
            {
                source.StartNow();
            }
            catch (IOException e)
            {
                receiver.Dispose();
                source.Dispose();
                Console.Error.WriteLine($"iqpipe: {e.Message}");
                return Task.FromResult(DeviceOpening.Refused(1));
            }
        }

        // Given up for good (standard input ended, or the command keeps dying): exit 1 so the
        // unit restarts and tries afresh, as an UberSDR receiver that stays away does. The source
        // has already said why.
        source.Lost += _ => context.RadioLost();
        receiver.Start();

        IqReceiverChannel channel = receiver.Channels[0];
        journal.Write(
            $"audio: iqpipe {launcher.Description}: {IqSampleFormats.Name(format)} at {rate} S/s centred on "
            + $"{RfPlan.Mhz(centreHz)} -> {spec.Demodulation.ToString().ToUpperInvariant()} at {RfPlan.Mhz(dialHz)} "
            + $"({offsetHz:+0;-0} Hz), audio at {context.DspRate} Hz (RECEIVE ONLY)");
        if (Math.Abs(offsetHz) < 1000)
        {
            journal.WriteError(
                $"iqpipe: the channel is {Math.Abs(offsetHz):F0} Hz from the centre of the IQ, where most SDRs "
                + $"have a spike at DC. Centre the IQ about {dcClear:F0} Hz below the dial, or leave "
                + "\"centreFrequency\" out and put {frequency} in the command.");
        }

        return Task.FromResult(new DeviceOpening
        {
            Ptt = new NullPtt(),
            Playback = new NullAudioOutput(context.DspRate),
            Input = new IqPipeAudioInput(channel, receiver, source),
            InputLossCount = () => channel.Discontinuities,
            Tuner = new IqPipeTuner(channel, source, dcClear),
        });
    }

    private static IEnumerable<string> ModesOf(DeviceOpenContext context)
    {
        foreach (IModem modem in context.Channel.Modems.Values)
        {
            yield return modem.Mode;
        }
    }
}

/// <summary>
/// The station's input on an <c>iqpipe:</c> device: the front end's channel, and on disposal the
/// source and the front end behind it, the source first so the reader thread is not left waiting
/// in a read.
/// </summary>
internal sealed class IqPipeAudioInput(IqReceiverChannel channel, IqReceiver receiver, IqPipeSource source)
    : IAudioInput, IDisposable
{
    /// <inheritdoc/>
    public int SampleRate => channel.SampleRate;

    /// <inheritdoc/>
    public int Read(Span<float> destination) => channel.Read(destination);

    /// <inheritdoc/>
    public void Dispose()
    {
        source.Dispose();
        receiver.Dispose();
    }
}

/// <summary>
/// Tuning an <c>iqpipe:</c> device: the channel moves within the band the front end keeps, and a
/// dial outside it restarts a command that takes <c>{frequency}</c> at a new centre, the dial
/// less the DC-clear offset. A feed that cannot be tuned answers false, as the interface asks.
/// </summary>
internal sealed class IqPipeTuner(IqReceiverChannel channel, IqPipeSource source, double dcClearHz) : IReceiverTuner
{
    /// <inheritdoc/>
    public double DialHz => channel.DialHz;

    /// <inheritdoc/>
    public async Task<bool> TuneAsync(double dialHz, CancellationToken cancellation)
    {
        // The channel remembers the dial either way, and follows it once a retune brings it in.
        if (await channel.TuneAsync(dialHz, cancellation).ConfigureAwait(false))
        {
            return true;
        }

        if (!source.Tunes)
        {
            return false;
        }

        source.RetuneTo(dialHz - dcClearHz);
        return true;
    }
}
