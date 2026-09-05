using System.Diagnostics;
using PortableDroid.Core.Abstractions;
using PortableDroid.Core.Models;

namespace PortableDroid.Runtime;

/// <summary>
/// Orchestrates the whole start/stop lifecycle: hardware detection, profile resolution,
/// backend launch, ADB connection and boot detection (spec §11). The UI only talks to this.
/// </summary>
public sealed class RuntimeManager : IRuntimeManager
{
    private readonly IAndroidBackend _backend;
    private readonly IAdbService _adb;
    private readonly IHardwareDetector _hardware;
    private readonly IProfileService _profiles;
    private readonly ISettingsService _settings;
    private readonly IStorageManager _storage;
    private readonly ILogService _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RuntimeManager(
        IAndroidBackend backend,
        IAdbService adb,
        IHardwareDetector hardware,
        IProfileService profiles,
        ISettingsService settings,
        IStorageManager storage,
        ILogService log)
    {
        _backend = backend;
        _adb = adb;
        _hardware = hardware;
        _profiles = profiles;
        _settings = settings;
        _storage = storage;
        _log = log;

        _backend.StateChanged += OnBackendStateChanged;
    }

    public RuntimeState State { get; private set; } = RuntimeState.Stopped;
    public RuntimeProfile? ActiveProfile { get; private set; }

    public event EventHandler<RuntimeState>? StateChanged;
    public event EventHandler<PortableDroidError>? ErrorRaised;

    private void SetState(RuntimeState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(this, state);
    }

    private void OnBackendStateChanged(object? sender, RuntimeState state)
    {
        // The backend owns Crashed/Stopped transitions; Running is only declared once ADB agrees.
        if (state is RuntimeState.Crashed)
        {
            SetState(RuntimeState.Crashed);
            ErrorRaised?.Invoke(this, PortableDroidError.RuntimeCrashed(-1));
        }
        else if (state is RuntimeState.Stopped && State != RuntimeState.Starting)
        {
            SetState(RuntimeState.Stopped);
        }
    }

    public async Task StartAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (State is RuntimeState.Running or RuntimeState.Starting) return;
            SetState(RuntimeState.Starting);

            progress?.Report("Checking your hardware...");
            var hw = await _hardware.DetectAsync(ct).ConfigureAwait(false);

            if (!hw.VirtualizationAvailable)
            {
                var warning = PortableDroidError.WhpxUnavailable();
                _log.Warn("runtime", "Starting without hardware acceleration; this will be slow.");
                ErrorRaised?.Invoke(this, warning);
            }

            if (hw.TotalRamMB is > 0 and < 3600)
                ErrorRaised?.Invoke(this, PortableDroidError.InsufficientMemory(hw.TotalRamMB));

            progress?.Report("Choosing a performance profile...");
            var profile = _profiles.Resolve(hw, _settings.Current, _settings.Current.ActiveProfile);
            ActiveProfile = profile;

            _storage.VerifyFreeSpaceOrThrow(_settings.Current.Storage.MinFreeSpaceMB);
            _storage.EnsureUserdataImage(profile);

            progress?.Report("Starting Android...");
            await _backend.StartAsync(profile, ct).ConfigureAwait(false);

            progress?.Report("Waiting for the system to come up...");
            var timeout = TimeSpan.FromSeconds(_settings.Current.Runtime.BootTimeoutSeconds);
            var processUp = await _backend.WaitForBootAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            if (!processUp && !_backend.IsProcessAlive)
            {
                SetState(RuntimeState.Error);
                throw new PortableDroidException(PortableDroidError.RuntimeCrashed(-1));
            }

            progress?.Report("Connecting to Android...");
            var booted = await WaitForAndroidAsync(profile, timeout, progress, ct).ConfigureAwait(false);

            if (!booted)
            {
                SetState(RuntimeState.Error);
                var error = PortableDroidError.BootTimeout(_settings.Current.Runtime.BootTimeoutSeconds);
                ErrorRaised?.Invoke(this, error);
                throw new PortableDroidException(error);
            }

            stopwatch.Stop();
            SetState(RuntimeState.Running);
            _log.Info("runtime", $"Android is ready in {stopwatch.Elapsed.TotalSeconds:n1} s " +
                                 $"(profile {profile.Kind}, {profile.MemoryMB} MB, {profile.CpuCores} cores).");
            progress?.Report($"Android is ready ({stopwatch.Elapsed.TotalSeconds:n0}s).");
        }
        catch (OperationCanceledException)
        {
            SetState(RuntimeState.Stopped);
            throw;
        }
        catch (PortableDroidException ex)
        {
            _log.Error("runtime", $"Start failed: {ex.Error.Code}", ex);
            ErrorRaised?.Invoke(this, ex.Error);
            SetState(RuntimeState.Error);
            throw;
        }
        catch (Exception ex)
        {
            var error = PortableDroidError.Unexpected("start Android", ex);
            _log.Error("runtime", "Unexpected start failure.", ex);
            ErrorRaised?.Invoke(this, error);
            SetState(RuntimeState.Error);
            throw new PortableDroidException(error, ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> WaitForAndroidAsync(
        RuntimeProfile profile, TimeSpan timeout, IProgress<string>? progress, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var connected = false;
        var announced = false;

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (!_backend.IsProcessAlive)
            {
                _log.Error("runtime", "The runtime process exited while waiting for Android to boot.");
                return false;
            }

            if (!connected)
                connected = await _adb.ConnectAsync(profile.AdbPort, ct).ConfigureAwait(false);

            if (connected)
            {
                if (!announced)
                {
                    announced = true;
                    progress?.Report("Android is booting, this can take a minute on the first run...");
                }

                if (await _adb.IsBootCompletedAsync(ct).ConfigureAwait(false))
                    return true;
            }

            await Task.Delay(2000, ct).ConfigureAwait(false);
        }

        return false;
    }

    public async Task StopAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State == RuntimeState.Stopped) return;
            SetState(RuntimeState.Stopping);

            progress?.Report("Asking Android to shut down...");
            try
            {
                // Give Android itself the chance to flush app data before the VM goes away.
                await _adb.PowerOffAsync(ct).ConfigureAwait(false);
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Debug("runtime", "Guest power-off request failed: " + ex.Message);
            }

            await _adb.DisconnectAsync(ct).ConfigureAwait(false);

            progress?.Report("Stopping the runtime...");
            await _backend.StopAsync(ct).ConfigureAwait(false);
            await _adb.KillServerAsync(ct).ConfigureAwait(false);

            SetState(RuntimeState.Stopped);
            progress?.Report("Android stopped. Your data has been saved.");
            _log.Info("runtime", "Android stopped; userdata image closed cleanly.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestartAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await StopAsync(progress, ct).ConfigureAwait(false);
        await Task.Delay(1000, ct).ConfigureAwait(false);
        await StartAsync(progress, ct).ConfigureAwait(false);
    }

    public async Task<string?> GetAndroidVersionAsync(CancellationToken ct = default)
    {
        if (State != RuntimeState.Running) return null;
        return await _adb.GetPropertyAsync("ro.build.version.release", ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _backend.StateChanged -= OnBackendStateChanged;
        try
        {
            if (State is RuntimeState.Running or RuntimeState.Starting)
                await StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
        await _backend.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
