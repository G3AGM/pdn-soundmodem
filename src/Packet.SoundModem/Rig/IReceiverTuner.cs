namespace Packet.SoundModem.Rig;

/// <summary>
/// A receiver the station can point somewhere else while it runs: the device's own tuning,
/// asked through the device rather than through a rig control program.
/// </summary>
/// <remarks>
/// A web receiver tunes itself to the band plan's dial when it opens. This is the same tuning,
/// offered afterwards, so that a later caller (the one-receiver plan's receive window, #585)
/// can move the dial without knowing what kind of receiver is behind it. Receive only: nothing
/// here touches a transmitter, and a device with one has its own rig control.
/// </remarks>
public interface IReceiverTuner
{
    /// <summary>The dial the receiver is asked to listen on, in Hz.</summary>
    double DialHz { get; }

    /// <summary>
    /// Moves the receiver to <paramref name="dialHz"/>, keeping its demodulator and passband.
    /// </summary>
    /// <returns>True when the receiver can hear the new dial; false when it is asked but cannot,
    /// because the band the receiver is on does not reach it. The dial is remembered either way,
    /// and taken up when the receiver can.</returns>
    Task<bool> TuneAsync(double dialHz, CancellationToken cancellation);
}
