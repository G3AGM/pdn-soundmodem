using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// One run of whatever the raw IQ comes from: a child process's standard output, a FIFO between
/// one writer's open and its close, or standard input. Its <see cref="Output"/> reads 0 at the
/// end of the run.
/// </summary>
internal interface IIqFeed : IDisposable
{
    /// <summary>The raw IQ bytes. <c>Read</c> blocks until there are some, and returns 0 when
    /// the run is over. Disposing the feed from another thread ends a blocked <c>Read</c>.</summary>
    Stream Output { get; }

    /// <summary>Why the run ended, in words for the journal, once <see cref="Output"/> has
    /// returned 0: a child's exit code and its last lines of standard error, say.</summary>
    string EndReason { get; }

    /// <summary>Bytes already waiting in the pipe, readable without blocking, or 0 where that
    /// cannot be asked. What tells a stall that lost samples from one that only delayed them.</summary>
    int BytesWaiting { get; }

    /// <summary>Ends the run from another thread: a blocked read of <see cref="Output"/> returns
    /// 0 soon after. Quick, and safe to call more than once; <see cref="IDisposable.Dispose"/>
    /// still releases the run afterwards.</summary>
    void Stop();
}

/// <summary>Asking a pipe how much is waiting in it.</summary>
internal static class PipeLevel
{
    private const int Fionread = 0x541B; // Linux, every architecture this daemon is built for.

    /// <summary>The bytes readable now from <paramref name="handle"/>, or 0 if it cannot say.</summary>
    public static int BytesWaiting(SafeHandle? handle)
    {
        if (handle is null || handle.IsInvalid || handle.IsClosed)
        {
            return 0;
        }

        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            return Ioctl((int)handle.DangerousGetHandle(), Fionread, out int waiting) == 0 ? Math.Max(0, waiting) : 0;
        }
        catch (ObjectDisposedException)
        {
            return 0;
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    /// <summary>The handle under a pipe or file stream, or null.</summary>
    public static SafeHandle? HandleOf(Stream stream) => stream switch
    {
        System.IO.Pipes.PipeStream pipe => pipe.SafePipeHandle,
        FileStream file => file.SafeFileHandle,
        _ => null,
    };

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int Ioctl(int fd, int request, out int value);
}

/// <summary>What starts an <see cref="IIqFeed"/>, as many times as the source asks.</summary>
internal interface IIqFeedLauncher
{
    /// <summary>What the feed is, for the journal: a command line, a FIFO's path.</summary>
    string Description { get; }

    /// <summary>Whether a run that ends can be followed by another. False for standard input,
    /// which ends once.</summary>
    bool CanRestart { get; }

    /// <summary>Whether a run can be started at a frequency of the source's choosing (a command
    /// with <c>{frequency}</c> in it), so that moving the source is a restart.</summary>
    bool Tunes { get; }

    /// <summary>Starts a run at <paramref name="centreHz"/> and <paramref name="rate"/>. May
    /// block (a FIFO waits here for a writer); <paramref name="cancellation"/> ends the wait.</summary>
    /// <exception cref="IOException">The run could not be started; the message says why.</exception>
    IIqFeed Start(double centreHz, int rate, CancellationToken cancellation);
}

/// <summary>
/// A command run through <c>/bin/sh -c</c>, its standard output the IQ: <c>rx_sdr</c>,
/// <c>rtl_sdr</c>, or a pipeline of the operator's. <c>{frequency}</c> and <c>{rate}</c> in the
/// command are replaced with the centre and the rate, in whole hertz, on every start.
/// </summary>
/// <remarks>
/// Its standard error is the journal's: the first lines of each run in full (a device's banner
/// is where its serial number, its gain and its complaints are), then at most one line in ten
/// seconds, and the last few kept to say why the run ended. The whole process tree is killed when
/// the run is disposed, so a pipeline does not leave its first program holding the device.
/// </remarks>
internal sealed class ChildProcessIqFeedLauncher(
    string command, Action<string> journal, TimeProvider timeProvider) : IIqFeedLauncher
{
    /// <summary>The placeholder for the centre frequency, Hz.</summary>
    public const string FrequencyPlaceholder = "{frequency}";

    /// <summary>The placeholder for the sample rate, S/s.</summary>
    public const string RatePlaceholder = "{rate}";

    /// <inheritdoc/>
    public string Description => command;

    /// <inheritdoc/>
    public bool CanRestart => true;

    /// <inheritdoc/>
    public bool Tunes => command.Contains(FrequencyPlaceholder, StringComparison.Ordinal);

    /// <summary>The command line for a run at <paramref name="centreHz"/> and <paramref name="rate"/>.</summary>
    public static string Expand(string command, double centreHz, int rate) => command
        .Replace(FrequencyPlaceholder, Math.Round(centreHz).ToString("F0", CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace(RatePlaceholder, rate.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    /// <inheritdoc/>
    public IIqFeed Start(double centreHz, int rate, CancellationToken cancellation)
    {
        string line = Expand(command, centreHz, rate);
        var info = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(line);

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new IOException($"'{line}' did not start");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new IOException($"'{line}' did not start: {e.Message}", e);
        }

        return new ChildProcessIqFeed(process, ProgramName(line), journal, timeProvider);
    }

    /// <summary>The program a command line runs, for prefixing its lines: the first word that
    /// is not <c>exec</c> or a <c>NAME=value</c> setting, without its directory.</summary>
    internal static string ProgramName(string line)
    {
        foreach (string word in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word == "exec" || word.Contains('=', StringComparison.Ordinal))
            {
                continue;
            }

            int slash = word.LastIndexOf('/');
            return slash >= 0 ? word[(slash + 1)..] : word;
        }

        return "the command";
    }
}

/// <summary>One run of a <see cref="ChildProcessIqFeedLauncher"/>'s command.</summary>
internal sealed class ChildProcessIqFeed : IIqFeed
{
    /// <summary>Lines of standard error said in full at the start of each run.</summary>
    internal const int OpeningLines = 20;

    private const int KeptLines = 3;
    private static readonly TimeSpan QuietLineInterval = TimeSpan.FromSeconds(10);

    private readonly Process _process;
    private readonly string _program;
    private readonly Action<string> _journal;
    private readonly TimeProvider _time;
    private readonly Thread _stderr;
    private readonly Queue<string> _lastLines = new();
    private readonly SafeHandle? _outputHandle;
    private int _disposed;

    public ChildProcessIqFeed(Process process, string program, Action<string> journal, TimeProvider timeProvider)
    {
        _process = process;
        _program = program;
        _journal = journal;
        _time = timeProvider;
        Output = process.StandardOutput.BaseStream;
        _outputHandle = PipeLevel.HandleOf(Output);
        _stderr = new Thread(PumpStandardError) { IsBackground = true, Name = "iqpipe-stderr" };
        _stderr.Start();
    }

    /// <inheritdoc/>
    public Stream Output { get; }

    /// <inheritdoc/>
    public string EndReason
    {
        get
        {
            string how;
            if (_process.WaitForExit(TimeSpan.FromSeconds(2)))
            {
                // Let the standard error thread finish, so the last lines are the last lines.
                _stderr.Join(TimeSpan.FromSeconds(1));
                int code = _process.ExitCode;
                how = code > 128 ? $"was ended by signal {code - 128}" : $"exited with code {code}";
            }
            else
            {
                how = "closed its standard output and is still running; it has been killed";
                Kill();
            }

            string[] last;
            lock (_lastLines)
            {
                last = [.. _lastLines];
            }

            return last.Length == 0
                ? $"{_program} {how}"
                : $"{_program} {how}; its last words: {string.Join(" / ", last)}";
        }
    }

    /// <inheritdoc/>
    public int BytesWaiting => Volatile.Read(ref _disposed) != 0 ? 0 : PipeLevel.BytesWaiting(_outputHandle);

    /// <inheritdoc/>
    /// <remarks>Kills the process tree and returns at once; its end is waited for in
    /// <see cref="Dispose"/>, on the reader's thread.</remarks>
    public void Stop() => Kill();

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Kill();
        _process.WaitForExit(TimeSpan.FromSeconds(2));

        // Process.Dispose leaves redirected streams that were read for their owner to close, so
        // close them here, or every restart leaks two descriptors until a finaliser runs.
        Output.Dispose();
        _stderr.Join(TimeSpan.FromSeconds(1));
        _process.StandardError.Dispose();
        _process.Dispose();
    }

    private void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    private void PumpStandardError()
    {
        int said = 0;
        long quiet = 0;
        DateTimeOffset nextLine = DateTimeOffset.MinValue;
        try
        {
            while (_process.StandardError.ReadLine() is string raw)
            {
                string line = Printable(raw);
                if (line.Length == 0)
                {
                    continue;
                }

                lock (_lastLines)
                {
                    _lastLines.Enqueue(line);
                    while (_lastLines.Count > KeptLines)
                    {
                        _lastLines.Dequeue();
                    }
                }

                if (said < OpeningLines)
                {
                    said++;
                    _journal($"iqpipe: {_program}: {line}");
                    continue;
                }

                DateTimeOffset now = _time.GetUtcNow();
                if (now < nextLine)
                {
                    quiet++;
                    continue;
                }

                nextLine = now + QuietLineInterval;
                string more = quiet > 0 ? $" ({quiet} more line(s) not shown)" : "";
                _journal($"iqpipe: {_program}: {line}{more}");
                quiet = 0;
            }
        }
        catch (Exception)
        {
            // The process is gone and its pipe with it, or the pipe was closed under the read. On
            // a thread of its own, so nothing here may escape: it would end the daemon.
        }
    }

    /// <summary>A line as the journal can take it: printable ASCII, a program's colour codes and
    /// anything above 0x7E made into '?', and no trailing space.</summary>
    internal static string Printable(string raw)
    {
        var text = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            text.Append(c is >= ' ' and <= '~' ? c : c == '\t' ? ' ' : '?');
        }

        return text.ToString().TrimEnd();
    }
}

/// <summary>
/// A FIFO some other program writes raw IQ into. Each run is one writer's: opened read-only, so
/// the open waits for a writer, and the writer going away is the end of the run, which the
/// source then reports as a gap rather than splicing the next writer's IQ onto the last's.
/// </summary>
internal sealed class FifoIqFeedLauncher(string path) : IIqFeedLauncher
{
    /// <inheritdoc/>
    public string Description => path;

    /// <inheritdoc/>
    public bool CanRestart => true;

    /// <inheritdoc/>
    public bool Tunes => false;

    /// <inheritdoc/>
    public IIqFeed Start(double centreHz, int rate, CancellationToken cancellation)
    {
        // The open blocks until a writer comes. Whoever cancels wakes it by opening the far end
        // themselves (FifoIqFeed.Wake), which is the only thing that ends a FIFO open.
        // A wake that lands before this thread is in open finds no reader and does nothing, so
        // it is tried again until the open has returned.
        var opened = new ManualResetEventSlim();
        using CancellationTokenRegistration wake = cancellation.Register(() => Task.Run(() =>
        {
            for (int k = 0; k < 40 && !opened.IsSet; k++)
            {
                FifoIqFeed.Wake(path);
                opened.Wait(TimeSpan.FromMilliseconds(50));
            }
        }));
        cancellation.ThrowIfCancellationRequested();
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 0, useAsync: false);
        }
        catch (UnauthorizedAccessException e)
        {
            throw new IOException($"{path}: {e.Message}", e);
        }
        finally
        {
            opened.Set();
        }

        return new FifoIqFeed(path, stream);
    }
}

/// <summary>One writer's run on a <see cref="FifoIqFeedLauncher"/>'s FIFO.</summary>
internal sealed class FifoIqFeed(string path, FileStream stream) : IIqFeed
{
    private const int OWronly = 1;
    private const int ONonblock = 0x800; // Linux, every architecture this daemon is built for.
    private int _disposed;

    /// <inheritdoc/>
    public Stream Output => stream;

    /// <inheritdoc/>
    public string EndReason => $"the program writing to {path} closed it";

    /// <inheritdoc/>
    public int BytesWaiting => Volatile.Read(ref _disposed) != 0 ? 0 : PipeLevel.BytesWaiting(stream.SafeFileHandle);

    /// <inheritdoc/>
    /// <remarks>A read blocked on a quiet writer is not ended by closing the descriptor under it,
    /// so this writes to the FIFO: the read returns that byte, and the source, seeing it is
    /// stopping, goes. Only on a stop: a run that ended at its writer's close is disposed without
    /// it, so no stray byte lands at the head of the next writer's run.</remarks>
    public void Stop()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            Wake(path);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        stream.Dispose();
    }

    /// <summary>Opens <paramref name="path"/> for writing without blocking and writes one byte,
    /// so a reader waiting in <c>open</c> or <c>read</c> on it comes out. Nothing happens when
    /// there is no reader.</summary>
    internal static void Wake(string path)
    {
        int fd = Open(path, OWronly | ONonblock);
        if (fd < 0)
        {
            return;
        }

        try
        {
            byte zero = 0;
            _ = Write(fd, ref zero, 1);
        }
        finally
        {
            _ = Close(fd);
        }
    }

    // DllImport rather than LibraryImport, as in PipeAudio: the newer attribute's generator
    // emits unsafe code. FileStream cannot open with O_NONBLOCK, which is the point here.
    [DllImport("libc", EntryPoint = "open", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern nint Write(int fd, ref byte buffer, nint count);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fd);
}

/// <summary>Standard input: the daemon started as <c>rtl_sdr - | pdn-soundmodem ...</c>.
/// One run only; when the writer goes the station has nothing more to hear.</summary>
internal sealed class StandardInputIqFeedLauncher : IIqFeedLauncher
{
    private int _started;

    /// <inheritdoc/>
    public string Description => "standard input";

    /// <inheritdoc/>
    public bool CanRestart => false;

    /// <inheritdoc/>
    public bool Tunes => false;

    /// <inheritdoc/>
    public IIqFeed Start(double centreHz, int rate, CancellationToken cancellation)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new IOException("standard input has already been read to its end");
        }

        return new StandardInputIqFeed(Console.OpenStandardInput());
    }

    private sealed class StandardInputIqFeed(Stream stream) : IIqFeed
    {
        public Stream Output => stream;

        public string EndReason => "standard input closed";

        public int BytesWaiting => PipeLevel.BytesWaiting(PipeLevel.HandleOf(stream));

        // A read of standard input cannot be ended from another thread; at shutdown the front
        // end's two-second join is what it gets.
        public void Stop()
        {
        }

        public void Dispose() => stream.Dispose();
    }
}
