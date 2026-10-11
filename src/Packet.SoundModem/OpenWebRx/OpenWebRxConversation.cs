using System.Globalization;

namespace Packet.SoundModem.OpenWebRx;

/// <summary>What one message, or one retune, makes the client do.</summary>
/// <param name="Send">Text messages to send, in order.</param>
/// <param name="Lines">Sentences for the journal, without the device name in front.</param>
internal sealed record OpenWebRxReaction(IReadOnlyList<string> Send, IReadOnlyList<string> Lines)
{
    /// <summary>Nothing to do.</summary>
    public static readonly OpenWebRxReaction None = new([], []);
}

/// <summary>
/// One session's side of the conversation with an OpenWebRX server, apart from the socket:
/// what the receiver has said about itself, and what to send it so that it demodulates the
/// dial this station wants. Pure, so every turn of it is testable without a server.
/// </summary>
/// <remarks>
/// <para><b>Where it listens.</b> An OpenWebRX receiver is an SDR on one band at a time, its
/// centre and sample rate set by the profile its operator, or a listener, last chose. The
/// demodulator is placed by its offset from that centre. A dial the band does not reach is not
/// tuned to: the station says so and waits, and takes the dial up the moment the band reaches
/// it again.</para>
/// <para><b>Profiles are everybody's.</b> Choosing a profile moves the SDR for every listener
/// on it, and OpenWebRX+ counts frequent changes against the client making them. So a profile
/// is asked for only when the configuration names one, only once a session, and never asked
/// back when somebody else moves the receiver away mid-session; the next session asks again.</para>
/// </remarks>
internal sealed class OpenWebRxConversation
{
    private OpenWebRxTuning _tuning;
    private string? _wantedProfileId;
    private bool _profileAsked;
    private bool _awaitingProfile;
    private bool _mayAskProfile = true;
    private bool _started;
    private long? _sentOffset;
    private OpenWebRxTuning? _sentTuning;
    private bool _outOfBand;

    /// <param name="tuning">Where to listen, and how.</param>
    public OpenWebRxConversation(OpenWebRxTuning tuning)
    {
        _tuning = tuning;
    }

    /// <summary>The tuning in force.</summary>
    public OpenWebRxTuning Tuning => _tuning;

    /// <summary>The server's answer to the handshake arrived.</summary>
    public bool Greeted { get; private set; }

    /// <summary>What the server says it is, e.g. <c>openwebrx v1.2.2</c>, or null before the
    /// handshake.</summary>
    public string? Server { get; private set; }

    /// <summary>The receiver's centre frequency, Hz, once it has said.</summary>
    public long? CentreHz { get; private set; }

    /// <summary>The receiver's sample rate, Hz, once it has said.</summary>
    public long? SampleRate { get; private set; }

    /// <summary>Whether the audio is ADPCM rather than plain 16-bit samples.</summary>
    public bool Adpcm { get; private set; }

    /// <summary>The SDR and profile in force, <c>sdr|profile</c>, once the receiver has said.</summary>
    public string? CurrentProfileId => SdrId is null || ProfileId is null ? null : $"{SdrId}|{ProfileId}";

    /// <summary>The profiles offered, or null before the receiver has listed them.</summary>
    public IReadOnlyList<OpenWebRxProfile>? Profiles { get; private set; }

    /// <summary>The receiver's name and location, or null.</summary>
    public string? Description { get; private set; }

    /// <summary>The demodulator has been started on the wanted dial.</summary>
    public bool Listening => _started && !_outOfBand;

    /// <summary>The receiver's band does not reach the wanted dial.</summary>
    public bool OutOfBand => _outOfBand;

    /// <summary>Why the server will not serve this client now, once it has said.</summary>
    public string? Refusal { get; private set; }

    /// <summary>Why this session cannot go on, once something has said: the receiver has no
    /// working SDR, or does not offer the profile asked for.</summary>
    public string? Fault { get; private set; }

    private string? SdrId { get; set; }

    private string? ProfileId { get; set; }

    /// <summary>The band the receiver is on, in words, for the journal.</summary>
    public string BandDescription =>
        CentreHz is long centre && SampleRate is long rate
            ? $"{Mhz(centre - (rate / 2))} to {Mhz(centre + (rate / 2))} MHz"
              + (CurrentProfileId is string id ? $" (profile {ProfileName(id)})" : "")
            : "a band it has not named";

    /// <summary>The profiles on offer, in words, for an operator choosing one.</summary>
    public string ProfileList =>
        Profiles is { Count: > 0 } all
            ? string.Join(", ", all.Select(p => $"\"{OpenWebRxProtocol.Ascii(p.Name)}\" ({OpenWebRxProtocol.Ascii(p.Id)})"))
            : "none listed";

    /// <summary>The messages that open a session, after the socket is up.</summary>
    public static IReadOnlyList<string> Opening() =>
        [OpenWebRxProtocol.Handshake, OpenWebRxProtocol.ConnectionProperties()];

    /// <summary>Takes one text message from the server.</summary>
    /// <param name="text">The message.</param>
    /// <param name="mayAskProfile">Whether a profile may be asked for yet. OpenWebRX+ counts a
    /// profile change soon after connecting against the client as a robot, so the input holds
    /// the ask back for the first seconds of a session; any later message then carries it.</param>
    public OpenWebRxReaction OnText(string text, bool mayAskProfile = true)
    {
        _mayAskProfile = mayAskProfile;
        var lines = new List<string>();
        switch (OpenWebRxProtocol.Parse(text))
        {
            case OpenWebRxMessage.HandshakeReply reply:
                Greeted = true;
                Server = OpenWebRxProtocol.Ascii(
                    $"{reply.Server ?? "an unnamed server"} {reply.Version ?? "of unstated version"}");
                return OpenWebRxReaction.None;

            case OpenWebRxMessage.Config config:
                bool moved = (config.CentreHz is long c && c != CentreHz)
                    || (config.SampleRate is long r && r != SampleRate);
                if (config.SdrId is string sdr && SdrId is not null && sdr != SdrId)
                {
                    // The server builds a fresh, unstarted demodulator for another SDR, as when one
                    // fails; the browser starts its own again, and so does this.
                    _started = false;
                    _sentOffset = null;
                }

                CentreHz = config.CentreHz ?? CentreHz;
                SampleRate = config.SampleRate ?? SampleRate;
                SdrId = config.SdrId ?? SdrId;
                ProfileId = config.ProfileId ?? ProfileId;
                if (_awaitingProfile && config.ProfileId is not null && CurrentProfileId != _wantedProfileId)
                {
                    // OpenWebRX+ answers a locked profile by sending the current one again.
                    Fault = $"the receiver kept profile {ProfileName(CurrentProfileId ?? "?")} rather than "
                        + $"moving to {ProfileName(_wantedProfileId!)}; it may be locked by its operator";
                    return new([], lines);
                }

                if (config.AudioCompression is string compression)
                {
                    Adpcm = compression.Equals("adpcm", StringComparison.OrdinalIgnoreCase);
                }

                return Evaluate(lines, moved);

            case OpenWebRxMessage.Profiles profiles:
                Profiles = profiles.All;
                return Evaluate(lines, moved: false);

            case OpenWebRxMessage.ReceiverDetails details:
                Description = details.Description;
                return OpenWebRxReaction.None;

            case OpenWebRxMessage.Backoff backoff:
                Refusal = OpenWebRxProtocol.Ascii(backoff.Reason);
                return OpenWebRxReaction.None;

            case OpenWebRxMessage.SdrError error:
                Fault = $"the receiver reports no working SDR ({OpenWebRxProtocol.Ascii(error.Message)})";
                return OpenWebRxReaction.None;

            case OpenWebRxMessage.DemodulatorError error:
                return new([], [$"the receiver could not set up the demodulator: {OpenWebRxProtocol.Ascii(error.Message)}"]);

            case OpenWebRxMessage.LogMessage log when log.Message.Length > 0:
                return new([], [$"the receiver says: {OpenWebRxProtocol.Ascii(log.Message)}"]);

            default:
                // A profile held back for the first seconds of a session is asked on whatever
                // arrives next, which is usually a meter reading.
                return _tuning.Profile is not null && !_profileAsked && mayAskProfile
                    ? Evaluate(lines, moved: false)
                    : OpenWebRxReaction.None;
        }
    }

    /// <summary>
    /// Moves the wanted dial, or anything else about the tuning, and says what to send for it.
    /// The profile is the session's and is not changed here.
    /// </summary>
    public OpenWebRxReaction Retune(OpenWebRxTuning tuning)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        _tuning = tuning with { Profile = _tuning.Profile };
        return Evaluate(new List<string>(), moved: false);
    }

    private OpenWebRxReaction Evaluate(List<string> lines, bool moved)
    {
        var send = new List<string>();
        if (CentreHz is not long centre || SampleRate is not long rate || Fault is not null)
        {
            return new(send, lines);
        }

        if (_tuning.Profile is string profile && !_profileAsked)
        {
            if (_wantedProfileId is null)
            {
                _wantedProfileId = ResolveProfile(profile);
                if (_wantedProfileId is null)
                {
                    if (Profiles is not null)
                    {
                        Fault = $"the receiver offers no profile \"{OpenWebRxProtocol.Ascii(profile)}\". "
                            + $"It offers {ProfileList}.";
                    }

                    // Otherwise wait for the list: the name may be on it.
                    return new(send, lines);
                }
            }

            if (CurrentProfileId != _wantedProfileId && !_mayAskProfile)
            {
                return new(send, lines);
            }

            _profileAsked = true;
            if (CurrentProfileId != _wantedProfileId)
            {
                _awaitingProfile = true;
                send.Add(OpenWebRxProtocol.SelectProfile(_wantedProfileId));
                lines.Add($"asking the receiver for profile {ProfileName(_wantedProfileId)}, which moves "
                    + "it for everyone listening");
                return new(send, lines);
            }
        }

        // The centre that came with the old profile says nothing about the new one.
        if (_awaitingProfile)
        {
            if (CurrentProfileId != _wantedProfileId)
            {
                return new(send, lines);
            }

            _awaitingProfile = false;
        }

        if (OpenWebRxProtocol.OffsetFor(centre, rate, _tuning) is not long offset)
        {
            if (!_outOfBand || moved)
            {
                lines.Add($"the receiver is on {BandDescription}, which does not reach "
                    + $"{Mhz(_tuning.FrequencyHz)} MHz; listening there resumes when it does");
            }

            _outOfBand = true;
            return new(send, lines);
        }

        if (_outOfBand)
        {
            lines.Add($"the receiver is on {BandDescription} and reaches {Mhz(_tuning.FrequencyHz)} MHz again");
            _outOfBand = false;
        }

        if (offset != _sentOffset || _tuning != _sentTuning)
        {
            send.Add(OpenWebRxProtocol.DspParameters(offset, _tuning));
            _sentOffset = offset;
            _sentTuning = _tuning;
        }

        if (!_started)
        {
            send.Add(OpenWebRxProtocol.DspStart());
            _started = true;
        }

        return new(send, lines);
    }

    private string? ResolveProfile(string profile)
    {
        if (Profiles is { } all)
        {
            foreach (OpenWebRxProfile p in all)
            {
                if (p.Id == profile)
                {
                    return p.Id;
                }
            }

            foreach (OpenWebRxProfile p in all)
            {
                if (p.Name.Equals(profile, StringComparison.OrdinalIgnoreCase))
                {
                    return p.Id;
                }
            }

            return null;
        }

        // An id is recognisable before the list arrives; a name has to wait for it.
        return profile.Contains('|', StringComparison.Ordinal) ? profile : null;
    }

    private string ProfileName(string id)
    {
        string? name = Profiles?.FirstOrDefault(p => p.Id == id).Name;
        return OpenWebRxProtocol.Ascii(string.IsNullOrEmpty(name) ? id : $"\"{name}\"");
    }

    private static string Mhz(long hz) => (hz / 1e6).ToString("F6", CultureInfo.InvariantCulture);
}
