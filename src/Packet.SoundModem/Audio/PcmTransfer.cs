namespace Packet.SoundModem.Audio;

/// <summary>
/// The handful of ALSA operations the read/write loop needs, behind an interface so the recovery
/// sequence can be tested without a sound card. <see cref="AlsaPcm"/> is the only implementation
/// outside the tests; it is a seam, not an abstraction layer.
/// </summary>
internal interface IPcmTransfer
{
    /// <summary>True for a capture stream, which has to be started by hand after a prepare.</summary>
    bool IsCapture { get; }

    /// <summary><c>snd_pcm_readi</c>/<c>snd_pcm_writei</c> at a frame offset into the caller's
    /// buffer: frames transferred, or a negative errno.</summary>
    long Transfer(int frameOffset, int frames);

    /// <summary><c>snd_pcm_recover(err, silent: 1)</c>.</summary>
    int Recover(int error);

    /// <summary><c>snd_pcm_prepare</c>.</summary>
    int Prepare();

    /// <summary><c>snd_pcm_start</c>.</summary>
    int Start();

    /// <summary>Counts one xrun (an overrun on capture, an underrun on playback).</summary>
    void CountXrun();

    /// <summary>Waits, for the card to be ready to stream again.</summary>
    void Pause(int milliseconds);
}

/// <summary>
/// The transfer loop and its xrun recovery, shared by capture and playback.
/// </summary>
/// <remarks>
/// <para>Recovery is prepare AND start, then a pause and another go. The old loop did what the
/// documentation implies is enough - <c>snd_pcm_recover</c>, which prepares - and read again
/// immediately; on the bench CM108 (radio1, 2026-09-06) that second read came back <c>-EIO</c>
/// 5 ms later and the daemon called the device dead. The card had not finished stopping the
/// endpoint the overrun stopped, and nothing was going to come of asking it again that
/// quickly.</para>
/// <para>So: one immediate attempt, because the ordinary xrun on a healthy card recovers at once
/// and every millisecond here is audio nobody hears, then up to
/// <see cref="MaxRecoveryAttempts"/> more spaced <see cref="RecoveryPauseMilliseconds"/> apart.
/// A device that is still failing after all of them is a device that has gone, and the error goes
/// back to the caller to be reported as a dead feed as before.</para>
/// <para>Only the errors a stream can come back from are retried. An unplugged card
/// (<c>-ENODEV</c>) is not one of them and must not spend 200 ms pretending otherwise: the
/// restart is what fixes that, and the sooner the better.</para>
/// </remarks>
internal static class PcmTransfer
{
    /// <summary>Retries after the first, immediate one.</summary>
    internal const int MaxRecoveryAttempts = 10;

    /// <summary>Between retries. Tens of milliseconds, because that is the scale a USB endpoint
    /// takes to stop and start, and ten of them is still under a quarter of a second before the
    /// feed is declared dead.</summary>
    internal const int RecoveryPauseMilliseconds = 20;

    // Linux errno values as ALSA returns them (negated). Named here rather than in a shared
    // constants file because these five are the whole vocabulary of a PCM transfer.
    private const int Eintr = 4;
    private const int Eio = 5;
    private const int Epipe = 32;
    private const int Ebadfd = 77;
    private const int Estrpipe = 86;

    /// <summary>
    /// Transfers <paramref name="frameCount"/> frames, recovering from xruns.
    /// </summary>
    /// <param name="pcm">The stream.</param>
    /// <param name="frameCount">Frames wanted.</param>
    /// <param name="failure">0, or the negative errno the stream gave up with.</param>
    /// <param name="stopAtXrun">Return what was transferred before an xrun rather than recovering
    /// and carrying on, so the frames returned never span the hole. The stream is left in its
    /// xrun state, so the next call meets the same error at once, counts it and recovers, and the
    /// frames that call returns are the first after the hole. For an IQ capture
    /// (<see cref="Packet.SoundModem.Iq.SoundCardIqSource"/>), whose consumer has to be told
    /// where a gap lies; an audio stream does not care and leaves this off.</param>
    /// <returns>Frames transferred, which is <paramref name="frameCount"/> unless
    /// <paramref name="failure"/> is set or <paramref name="stopAtXrun"/> stopped it short.</returns>
    internal static int Run(IPcmTransfer pcm, int frameCount, out int failure, bool stopAtXrun = false)
    {
        failure = 0;
        int done = 0;
        int attempts = 0;

        while (done < frameCount)
        {
            long moved = pcm.Transfer(done, frameCount - done);
            if (moved >= 0)
            {
                done += (int)moved;

                // Counted per stall, not per stream: a card that hiccups once an hour and
                // recovers is healthy, and should not be one failure away from the give-up
                // threshold for the rest of the run.
                attempts = 0;
                continue;
            }

            int error = (int)moved;
            if (stopAtXrun && done > 0 && error is -Epipe or -Estrpipe)
            {
                // Not counted and not recovered here: the next call meets it first thing.
                return done;
            }

            if (error is -Epipe or -Estrpipe)
            {
                // The xrun proper: audio was lost here. EIO and EBADFD are the recovery itself
                // not taking, so counting them too would report holes that never existed.
                pcm.CountXrun();
            }

            if (!IsRecoverable(error) || ++attempts > MaxRecoveryAttempts)
            {
                failure = error;
                return done;
            }

            if (attempts > 1)
            {
                pcm.Pause(RecoveryPauseMilliseconds);
            }

            Recover(pcm, error);
        }

        return done;
    }

    /// <summary>Errors a stream can be brought back from. Anything else is the device itself.</summary>
    private static bool IsRecoverable(int error) =>
        error is -Epipe or -Estrpipe or -Eio or -Ebadfd or -Eintr;

    private static void Recover(IPcmTransfer pcm, int error)
    {
        // A signal, not a stream fault: the transfer was interrupted, the stream is untouched,
        // and preparing it here would throw away the audio it is still holding.
        if (error == -Eintr)
        {
            return;
        }

        // snd_pcm_recover knows EPIPE (prepare) and ESTRPIPE (resume, then prepare) and hands
        // everything else straight back, so prepare by hand rather than reading again into the
        // same fault.
        if (pcm.Recover(error) < 0)
        {
            pcm.Prepare();
        }

        if (pcm.IsCapture)
        {
            // A prepared capture stream is not a running one. Whether the next read starts it
            // depends on the card and on a start threshold this process may not have set (a
            // fallback configuration leaves it at the buffer size, which no read here ever
            // reaches), so say it: PREPARED -> START is legal and a stream already running
            // refuses it harmlessly.
            //
            // Playback is deliberately not started here. It starts itself when enough has been
            // written, and starting it on an empty buffer would underrun again immediately.
            pcm.Start();
        }

        // Neither return value is inspected on purpose. If the prepare or the start failed, the
        // next transfer says so with the errno that matters, and the attempt count is what stops
        // the loop; a second opinion here would only give up sooner on a card that was about to
        // come back.
    }
}
