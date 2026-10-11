using M0LTE.Radio.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Rig;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// The station's PTT with a rig in the way: keyed through rigctld (<see cref="Inner"/> null), or
/// through the station's own line with the rig's tuning windows guarding it.
/// </summary>
internal sealed class RigPtt(RigControl rig, IPttControl? inner) : IPttControl
{
    /// <summary>The rig.</summary>
    internal RigControl Rig => rig;

    /// <summary>The line that really keys the radio, or null when rigctld does.</summary>
    internal IPttControl? Inner => inner;

    /// <inheritdoc />
    public void Key()
    {
        rig.Key();
        if (inner is null)
        {
            return;
        }

        try
        {
            inner.Key();
        }
        catch
        {
            rig.Unkey();
            throw;
        }
    }

    /// <inheritdoc />
    public void Unkey()
    {
        if (inner is null)
        {
            rig.Unkey();
            return;
        }

        try
        {
            inner.Unkey();
        }
        finally
        {
            rig.Unkey();
        }
    }
}

/// <summary>The station's side of a <see cref="RigControl"/>: its PTT and where its files go.</summary>
internal static class RigStation
{
    /// <summary>A PTT that keys the radio with rigctld's <c>T 1</c> and <c>T 0</c>. Only for a
    /// rig set up to key through rigctld.</summary>
    internal static IPttControl KeyingPtt(this RigControl rig) =>
        rig.KeysThroughRig
            ? new RigPtt(rig, null)
            : throw new InvalidOperationException("this rig is not set up to key through rigctld");

    /// <summary>
    /// The PTT for a device with no keying line of its own - a <c>pipe:</c> station or a
    /// <c>--wav-loop</c> recording. Keys through <paramref name="rig"/> exactly like a sound-card
    /// station does when <paramref name="pttConfig"/> says rigctld, which is useful for
    /// exercising rig features (tuning windows, the receive window) with no hardware at all
    /// (#655). Otherwise keys nothing, same as before this existed. Config validation
    /// (<c>DaemonConfig.ValidateRig</c>) refuses a rigctld PTT with no <c>"rig"</c> section, so
    /// <paramref name="rig"/> is never null here when <paramref name="pttConfig"/> says rigctld -
    /// the null check stays only as a defence against that invariant moving later.
    /// </summary>
    internal static IPttControl PttForTestDevice(PttConfig? pttConfig, RigControl? rig) =>
        pttConfig?.Type == "rigctld" && rig is not null
            ? rig.KeyingPtt()
            : new NullPtt();

    /// <summary>
    /// <paramref name="inner"/> with this rig's tuning windows guarding it: a keyup is refused
    /// while the rig is retuned, and the rig is never retuned while it is keyed. A PTT that
    /// already goes through this rig is returned as it is.
    /// </summary>
    internal static IPttControl Guard(this RigControl rig, IPttControl inner) =>
        inner is RigPtt mine && ReferenceEquals(mine.Rig, rig) ? inner
        : rig.KeysThroughRig
            ? throw new InvalidOperationException(
                "this rig keys through rigctld, so a second keying line beside it would key the radio twice")
        : new RigPtt(rig, inner);

    /// <summary>
    /// Makes <paramref name="channel"/> hold every transmission while this rig is retuned or owed
    /// its restore, and returns <paramref name="ptt"/> guarded by it. Ordinary frames wait behind
    /// the channel's inhibit (composed over whatever is already installed, never instead of it),
    /// and anything that gets past the inhibit - a burst that owns the channel's timing, the
    /// transmit lease holder's frames, which the lease lets past it - is refused at the PTT,
    /// unkeyed. That is every source the station has: KISS ports, ARDOP, idents, paging, the
    /// transmitter test and the lease all reach the air through this channel and this PTT.
    /// </summary>
    internal static IPttControl HoldTransmissions(this RigControl rig, SoundModemChannel channel, IPttControl ptt)
    {
        Func<bool>? prior = channel.TransmitInhibit;
        channel.TransmitInhibit = () => (prior?.Invoke() ?? false) || rig.HoldsTransmitter;
        return rig.Guard(ptt);
    }

    /// <summary>Where a window's restore target is written down: the state directory under
    /// systemd, else beside the config file, the same as the mixer state file.</summary>
    internal static string RestoreFilePath(string? configPath, RigctldEndpoint endpoint) =>
        Path.Combine(
            StateDirectory.Current
                ?? (Path.GetDirectoryName(configPath ?? "") is { Length: > 0 } beside ? beside : "."),
            RigRestoreFile.NameFor(endpoint));
}
