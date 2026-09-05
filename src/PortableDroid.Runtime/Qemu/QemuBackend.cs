using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using PortableDroid.Core.Abstractions;
using PortableDroid.Core.Models;
using PortableDroid.Infrastructure;

namespace PortableDroid.Runtime.Qemu;

/// <summary>Drives qemu-system-x86_64.exe as a child process (spec §11).</summary>
public sealed class QemuBackend : IAndroidBackend
{
    private readonly IPathService _paths;
    private readonly ILogService _log;
    private readonly ISettingsService _settings;
    private readonly IGraphicsManager _graphics;
    private readonly IHardwareDetector _hardware;
    private readonly ChildProcessJob _job = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Process? _process;
    private RuntimeState _state = RuntimeState.Stopped;
    private RuntimeProfile? _profile;
    private volatile bool _stopRequested;

    public QemuBackend(
        IPathService paths,
        ILogService log,
        ISettingsService settings,
        IGraphicsManager graphics,
        IHardwareDetector hardware)
    {
        _paths = paths;
        _log = log;
        _settings = settings;
        _graphics = graphics;
        _hardware = hardware;
    }

    public RuntimeState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            _log.Info("runtime", $"State -> {value}");
            StateChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<RuntimeState>? StateChanged;

    public bool IsProcessAlive
    {
        get
        {
            try { return _process is { HasExited: false }; }
            catch { return false; }
        }
    }

    public string QemuExecutable => Path.Combine(_paths.QemuDirectory, "qemu-system-x86_64.exe");
    public string QemuImgExecutable => Path.Combine(_paths.QemuDirectory, "qemu-img.exe");

    public async Task StartAsync(RuntimeProfile profile, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State is RuntimeState.Running or RuntimeState.Starting) return;

            _stopRequested = false;
            _profile = profile;
            State = RuntimeState.Starting;

            if (!File.Exists(QemuExecutable))
            {
                State = RuntimeState.Error;
                throw new PortableDroidException(PortableDroidError.RuntimeMissing(_paths.QemuDirectory));
            }

            var systemImage = FindSystemImage();
            var installerIso = systemImage is null ? FindInstallerIso() : null;
            if (systemImage is null && installerIso is null)
            {
                State = RuntimeState.Error;
                throw new PortableDroidException(PortableDroidError.RuntimeMissing(_paths.AndroidImageDirectory));
            }

            await EnsureUserdataImageAsync(profile, ct).ConfigureAwait(false);

            var hw = _hardware.Cached ?? await _hardware.DetectAsync(ct).ConfigureAwait(false);
            var effectiveGraphics = _graphics.Resolve(hw, profile.GraphicsMode);
            if (_graphics.HasPreviouslyFailed(profile.Name) && effectiveGraphics == GraphicsMode.Hardware)
            {
                _log.Warn("runtime", "Hardware graphics failed previously for this profile; using compatibility mode.");
                effectiveGraphics = GraphicsMode.Compatibility;
            }

            var args = QemuArgumentBuilder.Build(
                profile,
                profile.UserdataImagePath,
                effectiveGraphics,
                hw.VirtualizationAvailable,
                _settings.Current.Network.Enabled,
                installerIso,
                systemImage);

            _log.Info("runtime", $"Launching: {Path.GetFileName(QemuExecutable)} {ProcessRunner.QuoteForDisplay(args)}");

            var psi = new ProcessStartInfo
            {
                FileName = QemuExecutable,
                WorkingDirectory = _paths.QemuDirectory,
                UseShellExecute = false,
                CreateNoWindow = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) _log.Info("runtime", "qemu: " + e.Data); };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) _log.Debug("runtime", "qemu: " + e.Data); };
            process.Exited += OnProcessExited;

            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("Process.Start returned false.");
            }
            catch (Exception ex)
            {
                State = RuntimeState.Error;
                _log.Error("runtime", "Failed to launch the runtime process.", ex);
                throw new PortableDroidException(PortableDroidError.Unexpected("start Android", ex), ex);
            }

            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            _job.Assign(process);
            _process = process;

            // A near-instant exit means the arguments or the host environment are wrong.
            await Task.Delay(1500, ct).ConfigureAwait(false);
            if (process.HasExited)
            {
                State = RuntimeState.Error;
                var code = process.ExitCode;
                _log.Error("runtime", $"Runtime exited immediately with code {code}.");

                if (effectiveGraphics != GraphicsMode.Compatibility)
                {
                    _graphics.RecordFailure(profile.Name);
                    throw new PortableDroidException(PortableDroidError.GpuInitFailed($"exit code {code}"));
                }
                throw new PortableDroidException(PortableDroidError.RuntimeCrashed(code));
            }

            _log.Info("runtime", $"Runtime process started (pid {process.Id}).");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var code = -1;
        try { code = _process?.ExitCode ?? -1; } catch { /* ignore */ }

        if (_stopRequested)
        {
            State = RuntimeState.Stopped;
            _log.Info("runtime", "Runtime stopped cleanly.");
        }
        else
        {
            State = RuntimeState.Crashed;
            _log.Crash($"The Android runtime exited unexpectedly with code {code}. User data has been left untouched.");
        }
    }

    public async Task<bool> WaitForBootAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        // The backend only knows the process is alive; ADB decides when Android is truly up.
        // RuntimeManager combines both. Here we just watch for an early death.
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (!IsProcessAlive) return false;
            if (await IsQmpReachableAsync(ct).ConfigureAwait(false))
            {
                State = RuntimeState.Running;
                return true;
            }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        return IsProcessAlive;
    }

    private async Task<bool> IsQmpReachableAsync(CancellationToken ct)
    {
        if (_profile is null) return false;
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(750));
            await client.ConnectAsync("127.0.0.1", _profile.QmpPort, cts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_process is null || _process.HasExited)
            {
                State = RuntimeState.Stopped;
                return;
            }

            _stopRequested = true;
            State = RuntimeState.Stopping;

            // 1. Ask the guest to power off politely so Android flushes its filesystem.
            await SendQmpAsync("system_powerdown", ct).ConfigureAwait(false);

            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < deadline && !_process.HasExited)
                await Task.Delay(500, ct).ConfigureAwait(false);

            // 2. Ask QEMU itself to quit.
            if (!_process.HasExited)
            {
                _log.Warn("runtime", "Guest did not power down in time; asking the runtime to quit.");
                await SendQmpAsync("quit", ct).ConfigureAwait(false);
                var hardDeadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < hardDeadline && !_process.HasExited)
                    await Task.Delay(250, ct).ConfigureAwait(false);
            }

            // 3. Last resort. Logged loudly because it risks a dirty guest filesystem.
            if (!_process.HasExited)
            {
                _log.Warn("runtime", "Runtime did not exit; terminating the process (dirty shutdown).");
                ProcessRunner.TryKill(_process);
            }

            State = RuntimeState.Stopped;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Sends a single QMP command. Returns false if the monitor is unreachable.</summary>
    private async Task<bool> SendQmpAsync(string command, CancellationToken ct)
    {
        if (_profile is null) return false;
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync("127.0.0.1", _profile.QmpPort, cts.Token).ConfigureAwait(false);

            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            await using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);              // greeting
            await writer.WriteLineAsync("{\"execute\":\"qmp_capabilities\"}").ConfigureAwait(false);
            await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);              // ack
            await writer.WriteLineAsync($"{{\"execute\":\"{command}\"}}").ConfigureAwait(false);

            _log.Debug("runtime", $"QMP command sent: {command}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Debug("runtime", $"QMP command '{command}' failed: {ex.Message}");
            return false;
        }
    }

    public Task<long> GetRuntimeMemoryMBAsync()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Refresh();
                return Task.FromResult(_process.WorkingSet64 / 1024 / 1024);
            }
        }
        catch
        {
            // ignore
        }
        return Task.FromResult(0L);
    }

    private async Task EnsureUserdataImageAsync(RuntimeProfile profile, CancellationToken ct)
    {
        var image = profile.UserdataImagePath;
        Directory.CreateDirectory(Path.GetDirectoryName(image)!);
        if (File.Exists(image)) return;

        if (!File.Exists(QemuImgExecutable))
            throw new PortableDroidException(PortableDroidError.RuntimeMissing(QemuImgExecutable));

        var sizeGB = _settings.Current.Storage.UserdataSizeGB;
        _log.Info("runtime", $"Creating persistent userdata image ({sizeGB} GB) at {_paths.ToRelative(image)}");

        var result = await ProcessRunner.RunAsync(
            QemuImgExecutable,
            QemuArgumentBuilder.BuildCreateImageArguments(image, sizeGB),
            TimeSpan.FromMinutes(2),
            ct: ct).ConfigureAwait(false);

        if (!result.Success || !File.Exists(image))
            throw new PortableDroidException(PortableDroidError.Unexpected(
                "create the Android data image",
                new InvalidOperationException(result.StandardError)));
    }

    private string? FindSystemImage()
    {
        if (!Directory.Exists(_paths.AndroidImageDirectory)) return null;
        return Directory.GetFiles(_paths.AndroidImageDirectory, "system.img").FirstOrDefault();
    }

    private string? FindInstallerIso()
    {
        if (!Directory.Exists(_paths.AndroidImageDirectory)) return null;
        return Directory.GetFiles(_paths.AndroidImageDirectory, "*.iso").FirstOrDefault();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (IsProcessAlive) await StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }

        if (_process is not null)
        {
            _process.Exited -= OnProcessExited;
            _process.Dispose();
            _process = null;
        }

        _job.Dispose();
        _gate.Dispose();
    }
}
