using System.Text.Json;
using PortableDroid.Core.Abstractions;
using PortableDroid.Infrastructure;
using Xunit;

namespace PortableDroid.Tests;

/// <summary>
/// The product promise is "copy the folder to another drive and it still works".
/// These tests fail if anyone ever reintroduces a hardcoded path.
/// </summary>
public class PortabilityTests : IDisposable
{
    private readonly string _tempRoot;

    public PortabilityTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "pd-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, true); } catch { /* ignore */ }
    }

    [Fact]
    public void EveryPathLivesUnderTheApplicationRoot()
    {
        var paths = new PathService(_tempRoot);

        var all = new[]
        {
            paths.Runtime, paths.QemuDirectory, paths.PlatformToolsDirectory, paths.AndroidImageDirectory,
            paths.ProfilesRoot, paths.Config, paths.Cache, paths.Logs, paths.Temp, paths.Backups,
            paths.AppsImported, paths.SettingsFile,
            paths.ProfileDirectory("default"), paths.ProfileUserdataDirectory("default"),
            paths.ProfileUserdataImage("default"), paths.ProfileMetadataFile("default")
        };

        foreach (var p in all)
            Assert.StartsWith(paths.Root, Path.GetFullPath(p), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PathsFollowTheFolderWhenItMovesToAnotherDrive()
    {
        var onD = new PathService(@"D:\PortableDroid");
        var onE = new PathService(@"E:\PortableDroid");

        Assert.Equal(@"D:\PortableDroid\profiles\default\userdata\android.qcow2",
            onD.ProfileUserdataImage("default"));
        Assert.Equal(@"E:\PortableDroid\profiles\default\userdata\android.qcow2",
            onE.ProfileUserdataImage("default"));

        // Same relative shape on both drives - nothing is pinned to C:.
        Assert.Equal(
            onD.ToRelative(onD.ProfileUserdataImage("default")),
            onE.ToRelative(onE.ProfileUserdataImage("default")));
    }

    [Fact]
    public void NoPathMentionsTheSystemDriveUnlessTheAppIsThere()
    {
        var paths = new PathService(@"E:\PortableDroid");
        foreach (var p in new[] { paths.Runtime, paths.Logs, paths.SettingsFile, paths.Backups })
            Assert.DoesNotContain(@"C:\", p, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureLayout_CreatesEveryFolderAndDeletesNothing()
    {
        var canary = Path.Combine(_tempRoot, "profiles", "default", "userdata", "important.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(canary)!);
        File.WriteAllText(canary, "user data");

        var paths = new PathService(_tempRoot);
        paths.EnsureLayout();
        paths.EnsureLayout(); // idempotent

        Assert.True(Directory.Exists(paths.Runtime));
        Assert.True(Directory.Exists(paths.Logs));
        Assert.True(Directory.Exists(paths.ProfileUserdataDirectory("default")));
        Assert.True(File.Exists(canary));
        Assert.Equal("user data", File.ReadAllText(canary));
    }

    [Fact]
    public void RelativeAndAbsoluteRoundTrip()
    {
        var paths = new PathService(_tempRoot);
        var absolute = paths.ProfileUserdataImage("default");
        var relative = paths.ToRelative(absolute);

        Assert.False(Path.IsPathRooted(relative));
        Assert.Equal(absolute, paths.ToAbsolute(relative));
    }

    [Fact]
    public void SettingsRejectAbsolutePathsSoTheFolderStaysMovable()
    {
        var settings = AppSettings.CreateDefault();
        settings.Storage.ProfilesPath = @"C:\Somewhere\Else";
        settings.Storage.RuntimePath = @"D:\Fixed\Runtime";

        SettingsService.Sanitize(settings);

        Assert.False(Path.IsPathRooted(settings.Storage.ProfilesPath));
        Assert.False(Path.IsPathRooted(settings.Storage.RuntimePath));
    }

    [Fact]
    public void SettingsSanitiserRepairsNonsenseValues()
    {
        var settings = AppSettings.CreateDefault();
        settings.Runtime.BootTimeoutSeconds = 5;
        settings.Runtime.Fps = 5000;
        settings.Network.AdbPort = 70;
        settings.Network.QmpPort = 70;
        settings.Storage.UserdataSizeGB = 0;
        settings.ActiveProfile = "";

        SettingsService.Sanitize(settings);

        Assert.InRange(settings.Runtime.BootTimeoutSeconds, 30, 900);
        Assert.InRange(settings.Runtime.Fps, 15, 144);
        Assert.InRange(settings.Network.AdbPort, 1024, 65535);
        Assert.NotEqual(settings.Network.AdbPort, settings.Network.QmpPort);
        Assert.True(settings.Storage.UserdataSizeGB >= 4);
        Assert.Equal("default", settings.ActiveProfile);
    }

    [Fact]
    public void ShippedSettingsFileContainsNoDriveLetters()
    {
        // payload/config/settings.json is what users get; a drive letter in it breaks portability.
        var repoRoot = FindRepositoryRoot();
        var shipped = Path.Combine(repoRoot, "payload", "config", "settings.json");
        if (!File.Exists(shipped)) return; // not fatal when tests run from a package

        var json = File.ReadAllText(shipped);
        using var doc = JsonDocument.Parse(json);

        Assert.DoesNotMatch(@"[A-Za-z]:\\\\", json);
        Assert.False(json.Contains(@"C:\"), "settings.json must not hardcode a drive letter");
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PortableDroid.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
