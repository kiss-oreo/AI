using System.Collections.Concurrent;
using System.Text;
using PortableDroid.Core.Abstractions;

namespace PortableDroid.Infrastructure;

/// <summary>
/// Small dependency-free structured logger writing one file per channel (spec §35).
/// Channels: portable-droid, runtime, adb, crash.
/// Writes are queued and flushed on a background task so logging never blocks the UI.
/// </summary>
public sealed class FileLogService : ILogService, IDisposable
{
    private readonly IPathService _paths;
    private readonly BlockingCollection<(string Channel, string Line)> _queue = new(4096);
    private readonly Task _worker;
    private readonly ConcurrentDictionary<string, object> _fileLocks = new();
    private readonly CancellationTokenSource _cts = new();
    private const long MaxFileBytes = 4 * 1024 * 1024;

    public FileLogService(IPathService paths)
    {
        _paths = paths;
        Directory.CreateDirectory(_paths.Logs);
        _worker = Task.Factory.StartNew(Pump, TaskCreationOptions.LongRunning);
    }

    public IReadOnlyList<string> Channels { get; } = new[] { "portable-droid", "runtime", "adb", "crash" };

    public void Debug(string channel, string message) => Write(channel, "DBG", message);
    public void Info(string channel, string message) => Write(channel, "INF", message);

    public void Warn(string channel, string message, Exception? ex = null) =>
        Write(channel, "WRN", ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");

    public void Error(string channel, string message, Exception? ex = null) =>
        Write(channel, "ERR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    public void Crash(string message, Exception? ex = null)
    {
        Write("crash", "CRT", ex is null ? message : $"{message}{Environment.NewLine}{ex}");
        Write("portable-droid", "CRT", message);
    }

    private void Write(string channel, string level, string message)
    {
        var file = MapChannel(channel);
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        try
        {
            if (!_queue.IsAddingCompleted) _queue.TryAdd((file, line));
        }
        catch (InvalidOperationException)
        {
            // shutting down; drop the line rather than crash
        }
    }

    private static string MapChannel(string channel) => channel switch
    {
        "runtime" or "qemu" => "runtime",
        "adb" => "adb",
        "crash" => "crash",
        _ => "portable-droid"
    };

    private void Pump()
    {
        foreach (var (channel, line) in _queue.GetConsumingEnumerable())
        {
            try
            {
                AppendLine(channel, line);
            }
            catch
            {
                // Logging must never take the app down.
            }
        }
    }

    private void AppendLine(string channel, string line)
    {
        var path = Path.Combine(_paths.Logs, channel + ".log");
        var gate = _fileLocks.GetOrAdd(path, _ => new object());
        lock (gate)
        {
            RollIfNeeded(path);
            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
        }
    }

    private static void RollIfNeeded(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxFileBytes) return;

            var archived = Path.ChangeExtension(path, $".{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.Move(path, archived, overwrite: true);

            var dir = Path.GetDirectoryName(path)!;
            var prefix = Path.GetFileNameWithoutExtension(path);
            var old = Directory.GetFiles(dir, prefix + ".*.log")
                .OrderByDescending(f => f)
                .Skip(5);
            foreach (var f in old)
            {
                try { File.Delete(f); } catch { /* ignore */ }
            }
        }
        catch
        {
            // ignore roll failures
        }
    }

    public IReadOnlyList<string> ReadRecent(string channel, int lines)
    {
        var path = Path.Combine(_paths.Logs, MapChannel(channel) + ".log");
        if (!File.Exists(path)) return Array.Empty<string>();

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var all = new List<string>();
            while (sr.ReadLine() is { } l) all.Add(l);
            return all.Count <= lines ? all : all.GetRange(all.Count - lines, lines);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public void Dispose()
    {
        try
        {
            _queue.CompleteAdding();
            _worker.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // ignore
        }
        _cts.Cancel();
        _cts.Dispose();
        _queue.Dispose();
    }
}
