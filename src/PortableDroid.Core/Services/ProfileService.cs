using PortableDroid.Core.Abstractions;
using PortableDroid.Core.Models;

namespace PortableDroid.Core.Services;

/// <summary>
/// Chooses safe runtime settings for the detected machine (spec §18–§20).
///
/// The numbers below are STARTING POINTS, not benchmarked truth. See docs/benchmarks.md.
/// Two hard safety rules are always enforced, whatever the profile or user config says:
///   * Android may never be given more than 45% of total RAM.
///   * Android may never be given every CPU core; Windows must keep at least 2 where possible.
/// </summary>
public sealed class ProfileService : IProfileService
{
    public const int AbsoluteMinMemoryMB = 1024;
    public const double MaxRamFraction = 0.45;

    private readonly IPathService _paths;
    private readonly ILogService _log;

    public ProfileService(IPathService paths, ILogService log)
    {
        _paths = paths;
        _log = log;
    }

    public RuntimeProfile Resolve(HardwareInfo hw, AppSettings settings, string profileName = "default")
    {
        var kind = settings.Runtime.PerformanceProfile;
        if (kind == PerformanceProfileKind.Auto)
            kind = ClassifyHardware(hw);

        var (memory, cores, width, height, fps) = BaselineFor(kind);

        // Explicit user overrides (non-zero) win over the baseline, but never over the safety clamps.
        if (settings.Runtime.MemoryMB > 0) memory = settings.Runtime.MemoryMB;
        if (settings.Runtime.CpuCores > 0) cores = settings.Runtime.CpuCores;
        if (settings.Runtime.Fps > 0) fps = settings.Runtime.Fps;
        if (TryParseResolution(settings.Runtime.Resolution, out var w, out var h))
        {
            width = w;
            height = h;
        }

        memory = ClampMemory(memory, hw);
        cores = ClampCores(cores, hw);

        var profile = new RuntimeProfile
        {
            Name = profileName,
            Kind = kind,
            MemoryMB = memory,
            CpuCores = cores,
            Width = width,
            Height = height,
            Fps = fps,
            GraphicsMode = settings.Graphics.Mode,
            AdbPort = settings.Network.AdbPort,
            QmpPort = settings.Network.QmpPort,
            UserdataImagePath = _paths.ProfileUserdataImage(profileName)
        };

        _log.Info("core",
            $"Resolved profile '{profileName}': {kind}, {profile.MemoryMB} MB, {profile.CpuCores} cores, " +
            $"{profile.Resolution}@{profile.Fps}, graphics={profile.GraphicsMode}");

        return profile;
    }

    /// <summary>Picks a profile band from total RAM and core count.</summary>
    public static PerformanceProfileKind ClassifyHardware(HardwareInfo hw)
    {
        var ram = hw.TotalRamMB;
        if (ram < 5_000) return PerformanceProfileKind.UltraLow;   // ~4 GB machines
        if (ram < 7_000) return PerformanceProfileKind.Low;        // ~6 GB machines
        if (ram < 12_000) return PerformanceProfileKind.Balanced;  // ~8 GB machines
        return PerformanceProfileKind.Performance;                 // 16 GB and up
    }

    /// <summary>Baseline knobs per band. Spec §18/§19 table.</summary>
    public static (int memoryMB, int cores, int width, int height, int fps) BaselineFor(PerformanceProfileKind kind) =>
        kind switch
        {
            PerformanceProfileKind.UltraLow => (1280, 2, 1280, 720, 30),
            PerformanceProfileKind.Low => (1792, 2, 1280, 720, 30),
            PerformanceProfileKind.Balanced => (2560, 3, 1600, 900, 60),
            PerformanceProfileKind.Performance => (4096, 4, 1920, 1080, 60),
            _ => (2048, 2, 1280, 720, 30)
        };

    /// <summary>Never let Android starve Windows (spec §19).</summary>
    public static int ClampMemory(int requestedMB, HardwareInfo hw)
    {
        if (hw.TotalRamMB <= 0) return Math.Max(AbsoluteMinMemoryMB, requestedMB);

        var ceiling = (int)(hw.TotalRamMB * MaxRamFraction);
        var value = Math.Min(requestedMB, ceiling);
        value = Math.Max(value, Math.Min(AbsoluteMinMemoryMB, ceiling));

        // QEMU is happiest with a multiple of 128 MB.
        value = value / 128 * 128;
        return Math.Max(value, 512);
    }

    /// <summary>Never assign every core (spec §20).</summary>
    public static int ClampCores(int requested, HardwareInfo hw)
    {
        var available = hw.PhysicalCores > 0 ? hw.PhysicalCores : Math.Max(1, hw.LogicalCores / 2);
        var ceiling = available <= 2 ? 1 : Math.Max(2, available - 2);
        var value = Math.Min(requested, ceiling);
        return Math.Max(1, value);
    }

    public static bool TryParseResolution(string? text, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('x', 'X');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0].Trim(), out width) || !int.TryParse(parts[1].Trim(), out height)) return false;
        if (width < 320 || height < 240 || width > 3840 || height > 2160)
        {
            width = 0;
            height = 0;
            return false;
        }
        return true;
    }

    public IReadOnlyList<string> ListProfiles()
    {
        if (!Directory.Exists(_paths.ProfilesRoot)) return new[] { "default" };
        var names = Directory.GetDirectories(_paths.ProfilesRoot)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return names.Count == 0 ? new List<string> { "default" } : names;
    }

    public void CreateProfile(string name)
    {
        ValidateProfileName(name);
        Directory.CreateDirectory(_paths.ProfileUserdataDirectory(name));
        _log.Info("core", $"Created profile '{name}'.");
    }

    /// <summary>Destructive. The UI must confirm with the user first (spec §50).</summary>
    public void DeleteProfile(string name)
    {
        ValidateProfileName(name);
        if (string.Equals(name, "default", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The default profile cannot be deleted.");

        var dir = _paths.ProfileDirectory(name);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
            _log.Warn("core", $"Deleted profile '{name}' and all of its Android data (user confirmed).");
        }
    }

    public static void ValidateProfileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Profile name cannot be empty.", nameof(name));
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("..") ||
            name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException($"Invalid profile name '{name}'.", nameof(name));
    }
}
