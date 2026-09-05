using System.Text.Json.Serialization;
using PortableDroid.Core.Models;

namespace PortableDroid.Core.Abstractions;

/// <summary>Contents of config/settings.json. All paths are RELATIVE to the app root. Spec §46.</summary>
public sealed class AppSettings
{
    public string ActiveProfile { get; set; } = "default";
    public RuntimeSettings Runtime { get; set; } = new();
    public StorageSettings Storage { get; set; } = new();
    public GraphicsSettings Graphics { get; set; } = new();
    public NetworkSettings Network { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();

    public static AppSettings CreateDefault() => new();
}

public sealed class RuntimeSettings
{
    public string Backend { get; set; } = "qemu";

    /// <summary>0 = decide automatically from detected hardware.</summary>
    public int MemoryMB { get; set; }

    /// <summary>0 = decide automatically from detected hardware.</summary>
    public int CpuCores { get; set; }

    public string Resolution { get; set; } = "1280x720";
    public int Fps { get; set; } = 30;
    public int BootTimeoutSeconds { get; set; } = 180;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PerformanceProfileKind PerformanceProfile { get; set; } = PerformanceProfileKind.Auto;
}

public sealed class StorageSettings
{
    public string ProfilesPath { get; set; } = "profiles";
    public string RuntimePath { get; set; } = "runtime";
    public string TempPath { get; set; } = "temp";
    public int MinFreeSpaceMB { get; set; } = 8192;
    public int UserdataSizeGB { get; set; } = 16;
}

public sealed class GraphicsSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public GraphicsMode Mode { get; set; } = GraphicsMode.Auto;
}

public sealed class NetworkSettings
{
    public bool Enabled { get; set; } = true;
    public int AdbPort { get; set; } = 5556;
    public int QmpPort { get; set; } = 5557;
}

public sealed class LoggingSettings
{
    public string Level { get; set; } = "Information";
    public int RetainedFileCount { get; set; } = 5;
}
