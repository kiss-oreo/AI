using System.Text.Json;
using PortableDroid.Core.Abstractions;
using PortableDroid.Core.Apk;
using PortableDroid.Core.Models;

namespace PortableDroid.Runtime;

/// <summary>
/// Installs and manages APKs (spec §14). Metadata is cached in the profile folder so the
/// library still shows names and versions while Android is stopped, but ADB is always the
/// source of truth when it is running.
/// </summary>
public sealed class ApkManager : IApkManager
{
    private readonly IAdbService _adb;
    private readonly IPathService _paths;
    private readonly ILogService _log;
    private readonly ISettingsService _settings;
    private readonly IRuntimeManager _runtime;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);

    public ApkManager(
        IAdbService adb,
        IPathService paths,
        ILogService log,
        ISettingsService settings,
        IRuntimeManager runtime)
    {
        _adb = adb;
        _paths = paths;
        _log = log;
        _settings = settings;
        _runtime = runtime;
    }

    private string CacheFile => _paths.ProfileMetadataFile(_settings.Current.ActiveProfile);

    public Task<ApkMetadata> InspectAsync(string apkPath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (!ApkInspector.LooksLikeApk(apkPath))
                throw new PortableDroidException(PortableDroidError.InvalidApk(apkPath));

            try
            {
                var metadata = ApkInspector.Inspect(apkPath);
                _log.Info("core",
                    $"Inspected {Path.GetFileName(apkPath)}: {metadata.PackageName} {metadata.VersionName} " +
                    $"abis=[{string.Join(',', metadata.NativeAbis)}] minSdk={metadata.MinSdkVersion}");
                return metadata;
            }
            catch (PortableDroidException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Error("core", $"Could not read {Path.GetFileName(apkPath)}.", ex);
                throw new PortableDroidException(PortableDroidError.InvalidApk(apkPath, ex.Message), ex);
            }
        }, ct);
    }

    public async Task<InstalledApp> InstallAsync(
        string apkPath, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (_runtime.State != RuntimeState.Running)
            throw new PortableDroidException(PortableDroidError.AdbUnavailable("Android is not running."));

        progress?.Report("Checking the app...");
        var metadata = await InspectAsync(apkPath, ct).ConfigureAwait(false);

        // Fail fast with a clear message instead of letting pm produce NO_MATCHING_ABIS.
        if (metadata.IsArmOnly)
        {
            var error = PortableDroidError.ApkArchMismatch(metadata.NativeAbis);
            _log.Warn("core", $"{metadata.PackageName} is ARM-only ({string.Join(',', metadata.NativeAbis)}).");
            throw new PortableDroidException(error);
        }

        progress?.Report($"Installing {metadata.ApplicationLabel}...");
        var result = await _adb.InstallApkAsync(apkPath, progress, ct).ConfigureAwait(false);

        if (!result.Success)
            throw new PortableDroidException(
                PortableDroidError.ApkInstallFailed(result.StandardError, result.CombinedOutput));

        // Keep a copy of the APK so the user can reinstall without hunting for the file again.
        TryImportApk(apkPath);

        var runtimeApi = await GetRuntimeApiLevelAsync(ct).ConfigureAwait(false);
        var app = new InstalledApp
        {
            PackageName = metadata.PackageName,
            DisplayName = string.IsNullOrWhiteSpace(metadata.ApplicationLabel)
                ? metadata.PackageName
                : metadata.ApplicationLabel,
            VersionName = metadata.VersionName,
            Compatibility = ApkInspector.Classify(metadata, runtimeApi),
            InstalledUtc = DateTimeOffset.UtcNow
        };

        await UpsertCacheAsync(app, ct).ConfigureAwait(false);
        progress?.Report($"{app.DisplayName} installed.");
        _log.Info("core", $"Installed {app.PackageName} ({app.VersionName}).");
        return app;
    }

    private void TryImportApk(string apkPath)
    {
        try
        {
            Directory.CreateDirectory(_paths.AppsImported);
            var destination = Path.Combine(_paths.AppsImported, Path.GetFileName(apkPath));
            if (!File.Exists(destination))
                File.Copy(apkPath, destination);
        }
        catch (Exception ex)
        {
            _log.Debug("core", "Could not keep a copy of the APK: " + ex.Message);
        }
    }

    private async Task<int> GetRuntimeApiLevelAsync(CancellationToken ct)
    {
        var value = await _adb.GetPropertyAsync("ro.build.version.sdk", ct).ConfigureAwait(false);
        return int.TryParse(value, out var api) ? api : 0;
    }

    public async Task UninstallAsync(string packageName, CancellationToken ct = default)
    {
        var result = await _adb.UninstallAsync(packageName, ct).ConfigureAwait(false);
        if (!result.Success)
            throw new PortableDroidException(PortableDroidError.ApkInstallFailed(
                "the app could not be removed", result.CombinedOutput));

        await RemoveFromCacheAsync(packageName, ct).ConfigureAwait(false);
        _log.Info("core", $"Uninstalled {packageName} (user confirmed).");
    }

    public async Task LaunchAsync(string packageName, CancellationToken ct = default)
    {
        if (_runtime.State != RuntimeState.Running)
            throw new PortableDroidException(PortableDroidError.AdbUnavailable("Android is not running."));

        var result = await _adb.LaunchAsync(packageName, ct).ConfigureAwait(false);
        if (!result.Success)
            throw new PortableDroidException(new PortableDroidError(
                "ERR_APP_LAUNCH",
                "The app would not start.",
                "- The app has no launcher screen\n" +
                "- It crashed immediately\n" +
                "- It needs Google Play services, which are not part of this runtime",
                "1. Try starting it from the Android home screen instead.\n" +
                "2. If the app needs Google services it may not work here.",
                result.CombinedOutput));
    }

    public async Task StopAsync(string packageName, CancellationToken ct = default)
    {
        await _adb.ForceStopAsync(packageName, ct).ConfigureAwait(false);
        _log.Info("core", $"Force-stopped {packageName}.");
    }

    public async Task<IReadOnlyList<InstalledApp>> GetLibraryAsync(CancellationToken ct = default)
    {
        var cached = await ReadCacheAsync(ct).ConfigureAwait(false);

        if (_runtime.State != RuntimeState.Running)
            return cached.OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

        var packages = await _adb.ListPackagesAsync(includeSystem: false, ct).ConfigureAwait(false);
        var byName = cached.ToDictionary(a => a.PackageName, StringComparer.OrdinalIgnoreCase);

        var library = packages.Select(p =>
            byName.TryGetValue(p.PackageName, out var known)
                ? known
                : new InstalledApp
                {
                    PackageName = p.PackageName,
                    DisplayName = PrettifyPackageName(p.PackageName),
                    VersionName = "",
                    Compatibility = CompatibilityStatus.Unknown,
                    InstalledUtc = DateTimeOffset.UtcNow
                })
            .OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        await WriteCacheAsync(library, ct).ConfigureAwait(false);
        return library;
    }

    internal static string PrettifyPackageName(string packageName)
    {
        var last = packageName.Split('.').LastOrDefault();
        if (string.IsNullOrWhiteSpace(last)) return packageName;
        return char.ToUpperInvariant(last[0]) + last[1..];
    }

    private async Task<List<InstalledApp>> ReadCacheAsync(CancellationToken ct)
    {
        await _cacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(CacheFile)) return new List<InstalledApp>();
            var json = await File.ReadAllTextAsync(CacheFile, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<List<InstalledApp>>(json) ?? new List<InstalledApp>();
        }
        catch (Exception ex)
        {
            _log.Debug("core", "App cache could not be read: " + ex.Message);
            return new List<InstalledApp>();
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private async Task WriteCacheAsync(IEnumerable<InstalledApp> apps, CancellationToken ct)
    {
        await _cacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            var json = JsonSerializer.Serialize(apps, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(CacheFile, json, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Debug("core", "App cache could not be written: " + ex.Message);
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private async Task UpsertCacheAsync(InstalledApp app, CancellationToken ct)
    {
        var list = await ReadCacheAsync(ct).ConfigureAwait(false);
        list.RemoveAll(a => string.Equals(a.PackageName, app.PackageName, StringComparison.OrdinalIgnoreCase));
        list.Add(app);
        await WriteCacheAsync(list, ct).ConfigureAwait(false);
    }

    private async Task RemoveFromCacheAsync(string packageName, CancellationToken ct)
    {
        var list = await ReadCacheAsync(ct).ConfigureAwait(false);
        list.RemoveAll(a => string.Equals(a.PackageName, packageName, StringComparison.OrdinalIgnoreCase));
        await WriteCacheAsync(list, ct).ConfigureAwait(false);
    }
}
