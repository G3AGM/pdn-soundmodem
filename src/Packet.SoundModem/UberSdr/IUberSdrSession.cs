using M0LTE.Radio.Audio;

namespace Packet.SoundModem.UberSdr;

/// <summary>
/// One streaming session with an UberSDR instance, as <see cref="OnDemandUberSdrInput"/> sees
/// it: audio, whether the stream is currently expected to be delivering, and the one event a
/// session raises when it has given up. <see cref="UberSdrAudioInput"/> is the real one; the
/// tests stand in a fake so the on-demand state machine can be driven without a socket.
/// </summary>
internal interface IUberSdrSession : IAudioInput, IDisposable
{
    /// <summary>The pre-flight reply this session was opened on.</summary>
    ConnectionResponse Connection { get; }

    /// <summary>True while an IQ session is open and expected to be delivering audio; false
    /// between sessions when quiet is deliberate. See <see cref="UberSdrAudioInput.SessionLive"/>.</summary>
    bool SessionLive { get; }

    /// <summary>A running count that moves whenever the session's audio has a hole in it. See
    /// <see cref="UberSdrAudioInput.Discontinuities"/>.</summary>
    long Discontinuities { get; }

    /// <summary>Raised once when the receiver has been unreachable for long enough that the
    /// session's own reconnecting has stopped being hopeful.</summary>
    event Action<string>? Lost;
}
