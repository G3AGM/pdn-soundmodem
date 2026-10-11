namespace Packet.SoundModem.Modems;

/// <summary>
/// The kind of radio a mode is meant to go through: the "Radio path" column of the mode table.
/// </summary>
/// <remarks>
/// A fact about each mode, recorded so that code choosing modes for a station can leave out the
/// ones that make no sense on its radio. The survey's prospector was the first to need it: with
/// nothing to go on but the DSP rate, a 48 kHz HF station tried the FM 9600 family, which feeds a
/// radio's data socket and cannot come through an SSB receiver, against every unread 40 m burst.
/// </remarks>
public enum RadioPath
{
    /// <summary>An SSB receiver's audio, normally on HF: the 300 baud AFSK family, the BPSK and
    /// QPSK modes below <c>qpsk3600</c>, FreeDV DATAC and MIL-STD-188-110D.</summary>
    Ssb,

    /// <summary>An FM radio's audio or its 9600 baud data socket: 1200 baud AFSK,
    /// <c>qpsk3600</c>, the baseband FSK and C4FSK modes, and OFDM-FM.</summary>
    Fm,
}
