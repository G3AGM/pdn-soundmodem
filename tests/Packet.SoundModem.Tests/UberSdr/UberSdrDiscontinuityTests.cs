using System.Buffers.Binary;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.UberSdr;
using ZstdSharp;

using DaemonStation = Packet.SoundModem.Daemon.Station;

namespace Packet.SoundModem.Tests.UberSdr;

/// <summary>
/// What an <c>ubersdr:</c> device counts as lost audio, against a receiver on this machine that
/// speaks the instance's wire format: the counter the daemon reads per block to mark the channel
/// audio stream's gaps. A session picked up again leaves a hole, and so does audio dropped
/// because nothing drained the buffer; a Read that comes back empty does not, and that rule was
/// the 2026-10-11 bug (dozens of false gaps a slot, each one resetting a reader's modem).
/// </summary>
public sealed class UberSdrDiscontinuityTests
{
    /// <summary>One packet of IQ: 20 ms at 48 kHz.</summary>
    private const int PairsPerFrame = 960;

    /// <summary>Audio samples at 12 kHz in one packet.</summary>
    private const int AudioPerFrame = PairsPerFrame / 4;

    private static readonly TimeSpan GiveUp = TimeSpan.FromSeconds(30);

    private static UberSdrTuning Tuning => new()
    {
        FrequencyHz = 7_052_000,
        OutputRate = 12000,
        StartupGuardMs = 0,
    };

    [Fact]
    public async Task A_Reconnect_Counts_One_Discontinuity()
    {
        using var receiver = new FakeReceiver([new Session(Frames: 25, Hold: false), new Session(Frames: 25, Hold: true)]);
        var clock = new FakeTimeProvider();
        using UberSdrAudioInput input = await UberSdrAudioInput.OpenAsync(
            receiver.Endpoint, Tuning, log: null, CancellationToken.None, clock);

        await DrainAsync(input, 20 * AudioPerFrame);
        input.Discontinuities.Should().Be(0, "the first session follows nothing");

        // The receiver closed the first session after half a second; the input backs off on its
        // clock before asking again.
        await UntilAsync(() =>
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            return receiver.Sessions >= 2;
        }, "the input reconnects");
        await DrainAsync(input, 20 * AudioPerFrame);

        input.Discontinuities.Should().Be(1, "the time between the two sessions was not heard");
        input.DroppedSamples.Should().Be(0);
    }

    [Fact]
    public async Task Samples_Dropped_Because_The_Buffer_Filled_Count()
    {
        // Ten seconds of IQ into an eight-second ring that nobody reads.
        using var receiver = new FakeReceiver([new Session(Frames: 500, Hold: true)]);
        using UberSdrAudioInput input = await UberSdrAudioInput.OpenAsync(
            receiver.Endpoint, Tuning, log: null, CancellationToken.None, new FakeTimeProvider());

        await UntilAsync(() => receiver.FramesSent >= 500 && input.DroppedSamples > 0, "the ring overflows");

        // The last packets may still be on their way through the converter.
        long settled;
        do
        {
            settled = input.DroppedSamples;
            await Task.Delay(200);
        }
        while (input.DroppedSamples != settled);

        input.Discontinuities.Should().Be(0, "the count is as of the last Read, and nothing has been read");
        input.Read(new float[1200]).Should().Be(1200);
        input.Discontinuities.Should().Be(input.DroppedSamples,
            "every dropped sample is lost audio, and all of it came before the samples just read");
    }

    [Fact]
    public async Task A_Reconnect_Reaches_The_Channel_As_Lost_Audio_And_Empty_Reads_Do_Not()
    {
        using var receiver = new FakeReceiver([new Session(Frames: 25, Hold: false), new Session(Frames: 25, Hold: true)]);
        var clock = new FakeTimeProvider();
        using UberSdrAudioInput input = await UberSdrAudioInput.OpenAsync(
            receiver.Endpoint, Tuning, log: null, CancellationToken.None, clock);

        var channel = new SoundModemChannel(12000);
        int losses = 0;
        long delivered = 0;
        long lostAt = -1;
        channel.ReceiveAudioLost += () =>
        {
            Interlocked.Increment(ref losses);
            Interlocked.Exchange(ref lostAt, Interlocked.Read(ref delivered));
        };
        channel.AddReceiveTap(samples => Interlocked.Add(ref delivered, samples.Length));
        using var stopping = new CancellationTokenSource();

        // Wired as UberSdrStationDevice wires it. The station runs on the real clock: the fake one
        // is the input's, for its reconnect backoff, and the starvation watch is left out of it.
        using var station = new DaemonStation(
            new StationOptions
            {
                Channel = channel,
                Input = input,
                DspRate = 12000,
                Journal = new StationJournal("", _ => { }, _ => { }),
                DeviceKind = DeadFeedDevice.UberSdr,
                TimeProvider = TimeProvider.System,
                InputLossCount = () => input.Discontinuities,
            },
            stopping.Token);
        Task running = Task.Factory.StartNew(
            station.Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        await UntilAsync(() => Interlocked.Read(ref delivered) >= 20 * AudioPerFrame, "the first session is heard");

        // Empty reads while the receiver is between sessions: the loop turns, nothing is lost.
        await Task.Delay(300);
        Volatile.Read(ref losses).Should().Be(0, "a Read that comes back empty has lost nothing");

        long before = Interlocked.Read(ref delivered);
        await UntilAsync(() =>
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            return receiver.Sessions >= 2;
        }, "the input reconnects");
        await UntilAsync(() => Interlocked.Read(ref delivered) >= before + (20 * AudioPerFrame), "the second session is heard");

        Volatile.Read(ref losses).Should().Be(1, "one hole, between the two sessions");
        Interlocked.Read(ref lostAt).Should().Be(before,
            "the mark goes on the first block after the hole: every sample of the first session "
            + "came before it and none of the second");

        await stopping.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task DrainAsync(UberSdrAudioInput input, int samples)
    {
        var buffer = new float[1200];
        int taken = 0;
        using var giveUp = new CancellationTokenSource(GiveUp);
        while (taken < samples)
        {
            giveUp.Token.ThrowIfCancellationRequested();
            taken += input.Read(buffer);
            await Task.Yield();
        }
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        using var giveUp = new CancellationTokenSource(GiveUp);
        while (!condition())
        {
            if (giveUp.IsCancellationRequested)
            {
                throw new TimeoutException($"timed out waiting for {what}");
            }

            await Task.Delay(20, CancellationToken.None);
        }
    }

    /// <summary>One WebSocket session the fake receiver serves: so many packets, then either a
    /// close (the instance's session limit) or a held-open socket.</summary>
    private sealed record Session(int Frames, bool Hold);

    /// <summary>
    /// An UberSDR instance on loopback: the pre-flight, no description, and an IQ WebSocket that
    /// serves one scripted session per connection in zstd-wrapped "PC"/"PM" packets of a tone.
    /// </summary>
    private sealed class FakeReceiver : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Session[] _script;
        private readonly Task _serving;
        private int _sessions;
        private int _framesSent;

        public FakeReceiver(Session[] script)
        {
            _script = script;
            int port = FreePorts.Next();
            Endpoint = new UberSdrEndpoint("127.0.0.1", port, Ssl: false);
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _serving = Task.Run(ServeAsync);
        }

        public UberSdrEndpoint Endpoint { get; }

        /// <summary>WebSocket sessions opened so far.</summary>
        public int Sessions => Volatile.Read(ref _sessions);

        /// <summary>IQ packets sent so far, over every session.</summary>
        public int FramesSent => Volatile.Read(ref _framesSent);

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
            try
            {
                _serving.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }

            _stop.Dispose();
        }

        private async Task ServeAsync()
        {
            var running = new List<Task>();
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException
                                            or InvalidOperationException)
                {
                    break;
                }

                running.Add(Task.Run(() => AnswerAsync(context)));
            }

            await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        private async Task AnswerAsync(HttpListenerContext context)
        {
            string path = context.Request.Url!.AbsolutePath;
            if (path == "/connection")
            {
                byte[] body = Encoding.UTF8.GetBytes(
                    """{"allowed": true, "max_session_time": 3600, "allowed_iq_modes": ["iq48"]}""");
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
                return;
            }

            if (path != "/ws" || !context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            int index = Interlocked.Increment(ref _sessions) - 1;
            HttpListenerWebSocketContext socketContext = await context.AcceptWebSocketAsync(null);
            using WebSocket socket = socketContext.WebSocket;
            Session session = _script[Math.Min(index, _script.Length - 1)];
            try
            {
                using var compressor = new Compressor();
                ulong gpsNanos = 1_000_000_000UL * (ulong)(index + 1) * 1000;
                for (int f = 0; f < session.Frames; f++)
                {
                    byte[] frame = Frame(gpsNanos, full: f == 0);
                    await socket.SendAsync(compressor.Wrap(frame).ToArray(), WebSocketMessageType.Binary, true, _stop.Token);
                    Interlocked.Increment(ref _framesSent);
                    gpsNanos += 20_000_000UL;
                }

                if (session.Hold)
                {
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                }

                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "session limit", CancellationToken.None);
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException)
            {
            }
        }

        /// <summary>One packet: a 1 kHz tone as big-endian I/Q, behind a full or minimal header.</summary>
        private static byte[] Frame(ulong gpsNanos, bool full)
        {
            int header = full ? 29 : 13;
            var frame = new byte[header + (PairsPerFrame * 4)];
            if (full)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(frame, 0x5043); // "PC"
                frame[2] = 1;
                frame[3] = 2;
                BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(4), gpsNanos);
                BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(20), 48000);
                frame[24] = 2;
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(frame, 0x504D); // "PM"
                frame[2] = 1;
                BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(3), gpsNanos);
            }

            for (int n = 0; n < PairsPerFrame; n++)
            {
                double phase = 2 * Math.PI * 1000 * n / 48000.0;
                BinaryPrimitives.WriteInt16BigEndian(frame.AsSpan(header + (n * 4)), (short)(8000 * Math.Cos(phase)));
                BinaryPrimitives.WriteInt16BigEndian(frame.AsSpan(header + (n * 4) + 2), (short)(8000 * Math.Sin(phase)));
            }

            return frame;
        }
    }
}
