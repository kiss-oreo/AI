using PortableDroid.Core.Models;

namespace PortableDroid.Core.Abstractions;

/// <summary>All filesystem locations, derived from the executable folder. Spec §5, §30, §31.</summary>
public interface IPathService
{
    string Root { get; }
    string Runtime { get; }
    string QemuDirectory { get; }
    string PlatformToolsDirectory { get; }
    string AndroidImageDirectory { get; }
    string ProfilesRoot { get; }
    string Config { get; }
    string Cache { get; }
    string Logs { get; }
    string Temp { get; }
    string Backups { get; }
    string AppsImported { get; }
    string SettingsFile { get; }

    string ProfileDirectory(string profileName);
    string ProfileUserdataDirectory(string profileName);
    string ProfileUserdataImage(string profileName);
    string ProfileMetadataFile(string profileName);

    /// <summary>Creates any missing folders. Never deletes anything.</summary>
    void EnsureLayout();

    /// <summary>Makes an absolute path relative to <see cref="Root"/> for storage in config.</summary>
    string ToRelative(string absolutePath);

    /// <summary>Resolves a config-stored relative path back to an absolute one.</summary>
    string ToAbsolute(string relativePath);
}

public interface ILogService
{
    void Debug(string channel, string message);
    void Info(string channel, string message);
    void Warn(string channel, string message, Exception? ex = null);
    void Error(string channel, string message, Exception? ex = null);
    void Crash(string message, Exception? ex = null);
    IReadOnlyList<string> ReadRecent(string channel, int lines);
    IReadOnlyList<string> Channels { get; }
}

public interface ISettingsService
{
    AppSettings Current { get; }
    Task LoadAsync(CancellationToken ct = default);
    Task SaveAsync(CancellationToken ct = default);
    event EventHandler? SettingsChanged;
}

public interface IHardwareDetector
{
    Task<HardwareInfo> DetectAsync(CancellationToken ct = default);
    HardwareInfo? Cached { get; }
}

public interface IProfileService
{
    /// <summary>Chooses a safe profile for the detected hardware. Spec §18–§20.</summary>
    RuntimeProfile Resolve(HardwareInfo hardware, AppSettings settings, string profileName = "default");

    IReadOnlyList<string> ListProfiles();
    void CreateProfile(string name);
    void DeleteProfile(string name);
}

public interface IGraphicsManager
{
    GraphicsMode Resolve(HardwareInfo hardware, GraphicsMode requested);
    void RecordFailure(string profileName);
    bool HasPreviouslyFailed(string profileName);
}

/// <summary>The pluggable Android runtime backend (QEMU today, others later). Spec §11.</summary>
public interface IAndroidBackend : IAsyncDisposable
{
    RuntimeState State { get; }
    event EventHandler<RuntimeState>? StateChanged;

    Task StartAsync(RuntimeProfile profile, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task<bool> WaitForBootAsync(TimeSpan timeout, CancellationToken ct = default);
    Task<long> GetRuntimeMemoryMBAsync();
    bool IsProcessAlive { get; }
}

/// <summary>Thin, well-tested wrapper around adb. Nothing else may spawn adb. Spec §13.</summary>
public interface IAdbService
{
    Task<bool> ConnectAsync(int port, CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
    Task<bool> IsDeviceOnlineAsync(CancellationToken ct = default);
    Task<bool> IsBootCompletedAsync(CancellationToken ct = default);

    Task<AdbResult> InstallApkAsync(string apkPath, IProgress<string>? progress = null, CancellationToken ct = default);
    Task<AdbResult> UninstallAsync(string packageName, CancellationToken ct = default);
    Task<AdbResult> LaunchAsync(string packageName, CancellationToken ct = default);
    Task<AdbResult> ForceStopAsync(string packageName, CancellationToken ct = default);
    Task<IReadOnlyList<PackageInfo>> ListPackagesAsync(bool includeSystem = false, CancellationToken ct = default);
    Task<AdbResult> ShellAsync(string command, CancellationToken ct = default);
    Task<AdbResult> PushAsync(string localPath, string remotePath, CancellationToken ct = default);
    Task<AdbResult> PullAsync(string remotePath, string localPath, CancellationToken ct = default);
    Task<string?> GetPropertyAsync(string property, CancellationToken ct = default);
    Task<AdbResult> PowerOffAsync(CancellationToken ct = default);
    Task KillServerAsync(CancellationToken ct = default);
}

public interface IApkManager
{
    Task<ApkMetadata> InspectAsync(string apkPath, CancellationToken ct = default);
    Task<InstalledApp> InstallAsync(string apkPath, IProgress<string>? progress = null, CancellationToken ct = default);
    Task UninstallAsync(string packageName, CancellationToken ct = default);
    Task LaunchAsync(string packageName, CancellationToken ct = default);
    Task StopAsync(string packageName, CancellationToken ct = default);
    Task<IReadOnlyList<InstalledApp>> GetLibraryAsync(CancellationToken ct = default);
}

public interface IStorageManager
{
    long GetFreeSpaceMB();
    void EnsureUserdataImage(RuntimeProfile profile);
    bool UserdataImageExists(string profileName);
    long GetUserdataSizeMB(string profileName);
    void VerifyFreeSpaceOrThrow(long requiredMB);
}

public interface IBackupManager
{
    Task<BackupInfo> CreateBackupAsync(string profileName, IProgress<string>? progress = null, CancellationToken ct = default);
    Task RestoreBackupAsync(string backupFilePath, string profileName, IProgress<string>? progress = null, CancellationToken ct = default);
    IReadOnlyList<BackupInfo> ListBackups();
    void DeleteBackup(string backupFilePath);
}

public interface IResourceMonitor
{
    Task<ResourceSnapshot> SampleAsync(CancellationToken ct = default);
}

/// <summary>Top-level orchestration used by the UI. The UI touches nothing below this. Spec §10–§12.</summary>
public interface IRuntimeManager : IAsyncDisposable
{
    RuntimeState State { get; }
    event EventHandler<RuntimeState>? StateChanged;
    event EventHandler<PortableDroidError>? ErrorRaised;

    RuntimeProfile? ActiveProfile { get; }

    Task StartAsync(IProgress<string>? progress = null, CancellationToken ct = default);
    Task StopAsync(IProgress<string>? progress = null, CancellationToken ct = default);
    Task RestartAsync(IProgress<string>? progress = null, CancellationToken ct = default);
    Task<string?> GetAndroidVersionAsync(CancellationToken ct = default);
}
