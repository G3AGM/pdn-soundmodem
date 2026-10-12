using System.Buffers.Binary;
using AwesomeAssertions;
using Packet.SoundModem.SdrConnect;

namespace Packet.SoundModem.Tests.SdrConnect;

[CollectionDefinition(nameof(SdrConnectAllocationTests), DisableParallelization = true)]
public sealed class SdrConnectAllocationCollection;

/// <summary>
/// The SDRconnect IQ path in steady state allocates nothing per message. The receive loop runs on
/// pool threads, so the per-thread counter cannot see it; the whole process is measured instead,
/// with nothing else running (this collection is not run in parallel) and the fake's messages built
/// beforehand, less what the sending thread itself allocates: the fake's async send allocates about
/// 72 bytes a message of its own (measured), which is the test's cost, not the receive path's.
/// </summary>
[Collection(nameof(SdrConnectAllocationTests))]
public sealed class SdrConnectAllocationTests
{
    [Fact]
    public async Task A_Steady_Iq_Stream_Allocates_Nothing_Per_Message()
    {
        using var host = new FakeSdrConnectHost();
        using var source = new SdrConnectIqSource(FakeSdrConnectHost.Endpoint, new SdrConnectOptions
        {
            CentreFrequencyHz = 7_075_000,
            SampleRate = 2_000_000,
            RetuneSettle = TimeSpan.Zero,
            WatchRate = true,
        }, host.ConnectAsync);
        Task opening = source.OpenAsync(TestContext.Current.CancellationToken);
        FakeSdrConnect fake = await host.NextAsync();
        await opening;
        await fake.Streaming.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // One message of 4096 pairs, built once and sent over and over.
        var message = new byte[2 + (4 * 4096)];
        BinaryPrimitives.WriteUInt16LittleEndian(message, SdrConnectProtocol.PrimaryIq);
        new Random(3).NextBytes(message.AsSpan(2));

        using var reading = new CancellationTokenSource();
        long read = 0;
        long sending = 0;
        var reader = new Thread(() =>
        {
            var buffer = new float[16384];
            while (!reading.IsCancellationRequested)
            {
                int n = source.Read(buffer);
                if (n == 0)
                {
                    return;
                }

                Interlocked.Add(ref read, n);
            }
        });
        reader.Start();

        // Paced: each message is sent once the reader has had the last, so the fake's sends
        // complete at once and its own async machinery allocates nothing either. A sender that
        // ran ahead would find its sends waiting, and each waiting send allocates on its side.
        void Send(int count)
        {
            for (int k = 0; k < count; k++)
            {
                long target = Interlocked.Read(ref read) + (2 * 4096);
                long mine = GC.GetAllocatedBytesForCurrentThread();
                fake.SendRawAsync(message).GetAwaiter().GetResult();
                sending += GC.GetAllocatedBytesForCurrentThread() - mine;
                while (Interlocked.Read(ref read) < target)
                {
                    Thread.Yield();
                }
            }
        }

        Send(500); // warm-up: buffers sized, code jitted, pools filled
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long before = GC.GetTotalAllocatedBytes(precise: true);
        long sentBefore = sending;
        const int messages = 2000;
        Send(messages);
        long after = GC.GetTotalAllocatedBytes(precise: true) - (sending - sentBefore);
        reading.Cancel();
        source.Dispose();
        reader.Join(TimeSpan.FromSeconds(5));

        long perMessage = (after - before) / messages;
        TestContext.Current.SendDiagnosticMessage($"sdrconnect: {after - before} bytes over {messages} messages, {perMessage} a message");
        Console.WriteLine($"sdrconnect allocation: {after - before} bytes over {messages} messages, {perMessage} a message");
        source.Overflows.Should().Be(0);
        // Measured: 256 bytes over the 2000 messages, nothing a message. One small allocation a
        // message on the receive path would be 48 bytes or more a message, so this bound fails it.
        perMessage.Should().BeLessThan(16, "a steady stream, rate watch and all, allocates nothing a message on the receive path");
    }
}
