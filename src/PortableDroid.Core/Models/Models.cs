namespace PortableDroid.Core.Models;

/// <summary>Lifecycle of the Android runtime (spec §11).</summary>
public enum RuntimeState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Crashed,
    Error
}

public enum GraphicsMode
{
    Auto,
    Hardware,
    Compatibility
}

public enum PerformanceProfileKind
{
    Auto,
    UltraLow,
    Low,
    Balanced,
    Performance,
    Custom
}

/// <summary>Compatibility classification for an installed/inspected app (spec §26).</summary>
public enum CompatibilityStatus
{
    Unknown,
    Compatible,
    CompatibleWithLimitations,
    Unsupported,
    ArmOnly,
    GoogleDependency,
    GraphicsIssue,
    AndroidApiIssue,
    DrmLimitation
}

public enum StorageMediaType
{
    Unknown,
    Hdd,
    Ssd,
    Removable
}

public sealed record HardwareInfo
{
    public string CpuName { get; init; } = "Unknown";
    public int PhysicalCores { get; init; } = 2;
    public int LogicalCores { get; init; } = 2;
    public string CpuArchitecture { get; init; } = "x64";
    public long TotalRamMB { get; init; }
    public long AvailableRamMB { get; init; }
    public string GpuName { get; init; } = "Unknown";
    public string GpuDriverVersion { get; init; } = "Unknown";
    public bool VirtualizationAvailable { get; init; }
    public bool HypervisorPresent { get; init; }
    public string WindowsVersion { get; init; } = "Unknown";
    public StorageMediaType MediaType { get; init; } = StorageMediaType.Unknown;
    public long FreeDiskSpaceMB { get; init; }

    public override string ToString() =>
        $"{CpuName} ({PhysicalCores}C/{LogicalCores}T {CpuArchitecture}), {TotalRamMB} MB RAM, " +
        $"GPU {GpuName} [{GpuDriverVersion}], virt={VirtualizationAvailable}, {WindowsVersion}, disk={MediaType}";
}

/// <summary>Resolved, validated runtime settings used to launch the backend.</summary>
public sealed record RuntimeProfile
{
    public string Name { get; init; } = "default";
    public PerformanceProfileKind Kind { get; init; } = PerformanceProfileKind.Auto;
    public int MemoryMB { get; init; } = 2048;
    public int CpuCores { get; init; } = 2;
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
    public int Fps { get; init; } = 30;
    public GraphicsMode GraphicsMode { get; init; } = GraphicsMode.Auto;
    public int AdbPort { get; init; } = 5556;
    public int QmpPort { get; init; } = 5557;
    public string UserdataImagePath { get; init; } = "";
    public string Resolution => $"{Width}x{Height}";
}

public sealed record PackageInfo(string PackageName, string? ApkPath = null);

public sealed record ApkMetadata
{
    public string PackageName { get; init; } = "";
    public string ApplicationLabel { get; init; } = "";
    public string VersionName { get; init; } = "";
    public long VersionCode { get; init; }
    public int MinSdkVersion { get; init; }
    public int TargetSdkVersion { get; init; }
    public IReadOnlyList<string> NativeAbis { get; init; } = Array.Empty<string>();
    public bool UsesGooglePlayServices { get; init; }
    public long FileSizeBytes { get; init; }

    /// <summary>True when the APK ships native code but none of it is x86/x86_64.</summary>
    public bool IsArmOnly =>
        NativeAbis.Count > 0 &&
        !NativeAbis.Any(a => a.StartsWith("x86", StringComparison.OrdinalIgnoreCase));
}

public sealed record InstalledApp
{
    public string PackageName { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string VersionName { get; init; } = "";
    public CompatibilityStatus Compatibility { get; init; } = CompatibilityStatus.Unknown;
    public DateTimeOffset InstalledUtc { get; init; }
}

public sealed record AdbResult(bool Success, string StandardOutput, string StandardError, int ExitCode)
{
    public string CombinedOutput =>
        string.Join(Environment.NewLine,
            new[] { StandardOutput, StandardError }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public static AdbResult Ok(string stdout = "") => new(true, stdout, "", 0);
    public static AdbResult Fail(string stderr, int code = -1) => new(false, "", stderr, code);
}

public sealed record ResourceSnapshot(
    double CpuPercent,
    long HostRuntimeRamMB,
    long UiRamMB,
    RuntimeState State,
    bool NetworkConnected);

public sealed record BackupInfo(string FilePath, string FileName, DateTimeOffset CreatedUtc, long SizeBytes);
