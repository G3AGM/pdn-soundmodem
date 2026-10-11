using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// <c>POST /api/mailcast/measure</c> end to end: an HTTP request in, over a real socket, through
/// the real handler, with the real key check - the same way <c>/api/mixer</c> is tested
/// (<see cref="MixerApiTests"/>), against a real <see cref="MailcastFilterMeasurer"/> fed
/// synthetic shaped noise rather than a sound card.
/// </summary>
public sealed class MailcastMeasureApiTests
{
    private const string Key = "test-key-not-a-secret";
    private const int Rate = 48000;

    [Fact]
    public async Task Without_A_Mailcast_Receiver_It_Is_A_404()
    {
        using var station = new Station(measurer: null);

        HttpResponseMessage answer = await station.Client.PostAsync(station.Url, content: null);

        answer.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await answer.Content.ReadAsStringAsync()).Should().Contain("no mailcast receiver");
    }

    [Fact]
    public async Task Without_The_Key_It_Is_A_401()
    {
        var measurer = new MailcastFilterMeasurer(Rate, skip: () => false);
        using var station = new Station(measurer, presentTheKey: false);

        HttpResponseMessage answer = await station.Client.PostAsync(station.Url, content: null);

        answer.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_Get_Is_A_405()
    {
        var measurer = new MailcastFilterMeasurer(Rate, skip: () => false);
        using var station = new Station(measurer);

        HttpResponseMessage answer = await station.Client.GetAsync(station.Url);

        answer.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task A_Measurement_Runs_On_Shaped_Noise_And_Answers_With_The_Edges()
    {
        // The measurer asks skip about a block only once a measurement is running (Process drops
        // anything before that unread), so the first call is the measurement being armed.
        bool armed = false;
        var measurer = new MailcastFilterMeasurer(Rate, skip: () =>
        {
            armed = true;
            return false;
        });
        using var station = new Station(measurer);
        // One long clip of genuinely continuous noise, not a short chunk replayed: a replayed
        // chunk is periodic at the replay rate, which reads as a comb of spectral lines rather
        // than the flat passband real noise gives (MailcastFilterMeasurerTests found the same
        // thing the other way round, with deterministic tones in place of noise).
        float[] clip = ShapedNoiseChunk(700, 3200, (int)(11 * Rate), seed: 11);

        Task<HttpResponseMessage> post = station.Client.PostAsync(station.Url, content: null);

        // Offers the clip's first block until the request has armed the measurement, and only
        // then plays on through it: the measurer counts samples rather than seconds, and needs
        // 10 s of the 11 s clip, which this loop feeds in well under a second. Played straight
        // through from the start, a request that took longer than that to arrive - one HTTP
        // round trip on a loaded box - found the clip already gone and waited for audio that
        // never came, until the client gave up at 100 s (#686). Held at its start, the block
        // that arms it is the first one measured, and everything after it is real,
        // non-repeating audio. A request that ends early (a 401, a 404) stops the loop too.
        const int Block = Rate / 10;
        int at = 0;
        while (!post.IsCompleted && at < clip.Length)
        {
            measurer.Process(clip.AsSpan(at, Math.Min(Block, clip.Length - at)));
            if (armed)
            {
                at += Block;
            }
            else
            {
                await Task.Delay(1);
            }
        }

        HttpResponseMessage answer = await post;
        answer.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement body = JsonSerializer.Deserialize<JsonElement>(await answer.Content.ReadAsStringAsync());
        body.GetProperty("lowHz").GetDouble().Should().BeInRange(600, 800);
        body.GetProperty("highHz").GetDouble().Should().BeInRange(3100, 3300);
        body.GetProperty("note").ValueKind.Should().Be(JsonValueKind.Null);
    }

    /// <summary>One chunk of white noise through a windowed-sinc FIR bandpass, flat from
    /// <paramref name="lowHz"/> to <paramref name="highHz"/> - the same shape
    /// <c>MailcastFilterMeasurerTests.ShapedNoise</c> builds, trimmed to one chunk since this
    /// test loops feeding it rather than rendering one long clip up front.</summary>
    private static float[] ShapedNoiseChunk(double lowHz, double highHz, int samples, int seed)
    {
        const int Taps = 401;
        int half = Taps / 2;
        var fir = new double[Taps];
        double fLow = lowHz / Rate, fHigh = highHz / Rate;
        for (int n = 0; n < Taps; n++)
        {
            int k = n - half;
            double ideal = LowPass(k, fHigh) - LowPass(k, fLow);
            double window = 0.54 - (0.46 * Math.Cos(2 * Math.PI * n / (Taps - 1)));
            fir[n] = ideal * window;
        }

        var random = new Random(seed);
        var white = new double[samples + Taps];
        for (int n = 0; n < white.Length; n++)
        {
            white[n] = (random.NextDouble() * 2) - 1;
        }

        var audio = new float[samples];
        for (int n = 0; n < samples; n++)
        {
            double sum = 0;
            for (int t = 0; t < Taps; t++)
            {
                sum += fir[t] * white[n + Taps - 1 - t];
            }

            audio[n] = (float)sum;
        }

        return audio;

        static double LowPass(int k, double fc) => k == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * k) / (Math.PI * k);
    }

    /// <summary>A <see cref="ConfigApi"/> on a real socket, with or without a mailcast filter
    /// measurer behind it - the arrangement the operator page's "Measure my filter" button and a
    /// curl both talk to.</summary>
    private sealed class Station : IDisposable
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("pdnsm-mailcast-measure-api").FullName;
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;

        public Station(MailcastFilterMeasurer? measurer, bool presentTheKey = true)
        {
            string configPath = Path.Combine(_dir, "soundmodem.json");
            File.WriteAllText(configPath, """{"device": "null"}""");

            var api = new ConfigApi(
                Key, configPath, Path.Combine(_dir, "pending.json"),
                runningJson: () => File.ReadAllText(configPath),
                ephemeralInForce: false,
                requestRestart: () => throw new InvalidOperationException(
                    "measuring a filter must never restart the station"));

            if (measurer is not null)
            {
                api.ServeMailcastMeasure(measurer);
            }

            if (presentTheKey)
            {
                Client.DefaultRequestHeaders.Add("X-API-Key", Key);
            }

            int port = FreePorts.Next();
            Url = $"http://127.0.0.1:{port}/api/mailcast/measure";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _serving = ServeAsync(api);
        }

        public string Url { get; }

        public HttpClient Client { get; } = new();

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
                // The listener was closed under the accept, which is how this loop ends.
            }

            Client.Dispose();
            _stop.Dispose();
            Directory.Delete(_dir, recursive: true);
        }

        private async Task ServeAsync(ConfigApi api)
        {
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
                    return;
                }

                _ = Task.Run(async () =>
                {
                    if (!await api.HandleAsync(context, context.Request.Url!.AbsolutePath))
                    {
                        context.Response.StatusCode = 404;
                        context.Response.Close();
                    }
                });
            }
        }
    }
}
