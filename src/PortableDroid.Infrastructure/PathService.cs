using PortableDroid.Core.Abstractions;
using PortableDroid.Core.Services;

namespace PortableDroid.Infrastructure;

/// <summary>
/// Every path in PortableDroid comes from here, and every one of them is derived from the
/// folder the executable lives in. No drive letter, %APPDATA% or registry key is ever used,
/// which is what makes the D:\ -> E:\ copy work (spec §5, §30, §31).
/// </summary>
public sealed class PathService : IPathService
{
    public PathService(string? rootOverride = null)
    {
        var root = rootOverride ?? AppContext.BaseDirectory;
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    public string Root { get; }

    public string Runtime => Path.Combine(Root, "runtime");
    public string QemuDirectory => Path.Combine(Runtime, "qemu");
    public string PlatformToolsDirectory => Path.Combine(Runtime, "platform-tools");
    public string AndroidImageDirectory => Path.Combine(Runtime, "android");
    public string ProfilesRoot => Path.Combine(Root, "profiles");
    public string Config => Path.Combine(Root, "config");
    public string Cache => Path.Combine(Root, "cache");
    public string Logs => Path.Combine(Root, "logs");
    public string Temp => Path.Combine(Root, "temp");
    public string Backups => Path.Combine(Root, "backups");
    public string AppsImported => Path.Combine(Root, "apps", "imported");
    public string SettingsFile => Path.Combine(Config, "settings.json");

    public string ProfileDirectory(string profileName)
    {
        ProfileService.ValidateProfileName(profileName);
        return Path.Combine(ProfilesRoot, profileName);
    }

    public string ProfileUserdataDirectory(string profileName) =>
        Path.Combine(ProfileDirectory(profileName), "userdata");

    public string ProfileUserdataImage(string profileName) =>
        Path.Combine(ProfileUserdataDirectory(profileName), "android.qcow2");

    public string ProfileMetadataFile(string profileName) =>
        Path.Combine(ProfileDirectory(profileName), "apps.json");

    public void EnsureLayout()
    {
        foreach (var dir in new[]
                 {
                     Runtime, QemuDirectory, PlatformToolsDirectory, AndroidImageDirectory,
                     ProfilesRoot, Config, Cache, Logs, Temp, Backups, AppsImported,
                     ProfileUserdataDirectory("default")
                 })
        {
            Directory.CreateDirectory(dir);
        }
    }

    public string ToRelative(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return absolutePath;
        var full = Path.GetFullPath(absolutePath);
        return full.StartsWith(Root, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(Root, full)
            : full;
    }

    public string ToAbsolute(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return Root;
        return Path.IsPathRooted(relativePath)
            ? Path.GetFullPath(relativePath)
            : Path.GetFullPath(Path.Combine(Root, relativePath));
    }
}
