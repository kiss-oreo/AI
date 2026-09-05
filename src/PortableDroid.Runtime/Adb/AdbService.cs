using PortableDroid.Core.Abstractions;
using PortableDroid.Core.Models;
using PortableDroid.Infrastructure;

namespace PortableDroid.Runtime.Adb;

/// <summary>
/// The only component allowed to invoke adb.exe (spec §13). Everything above talks to this
/// interface, so swapping the backend later does not ripple through the app.
/// </summary>
public sealed class AdbService : IAdbService
{
    private readonly IPathService _paths;
    private readonly ILogService _log;
    private readonly ISettingsService _settings;
    private string? _serial;

    public AdbService(IPathService paths, ILogService log, ISettingsService settings)
    {
        _paths = paths;
        _log = log;
        _settings = settings;
    }

    public string AdbExecutable => Path.Combine(_paths.PlatformToolsDirectory, "adb.exe");

    /// <summary>
    /// A private adb server port keeps PortableDroid from fighting Android Studio or any other
    /// adb already running on the machine.
    /// </summary>
    private IDictionary<string, string> Environment => new Dictionary<string, string>
    {
        ["ANDROID_ADB_SERVER_PORT"] = (_settings.Current.Network.AdbPort + 100).ToString(),
        ["ADB_TRACE"] = ""
    };

    private void EnsureAvailable()
    {
        if (!File.Exists(AdbExecutable))
            throw new PortableDroidException(PortableDroidError.RuntimeMissing(_paths.PlatformToolsDirectory));
    }

    private async Task<AdbResult> RunAsync(IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        EnsureAvailable();
        var list = args.ToList();
        _log.Debug("adb", "adb " + ProcessRunner.QuoteForDisplay(list));

        var result = await ProcessRunner.RunAsync(AdbExecutable, list, timeout, Environment, ct: ct)
            .ConfigureAwait(false);

        if (!result.Success)
            _log.Warn("adb", $"adb {ProcessRunner.QuoteForDisplay(list)} -> exit {result.ExitCode}: {result.StandardError.Trim()}");

        return new AdbResult(result.Success, result.StandardOutput.Trim(), result.StandardError.Trim(), result.ExitCode);
    }

    /// <summary>Prefixes the device serial so we never touch a phone the user has plugged in.</summary>
    private List<string> Targeted(params string[] args)
    {
        var list = new List<string>();
        if (_serial is not null)
        {
            list.Add("-s");
            list.Add(_serial);
        }
        list.AddRange(args);
        return list;
    }

    public async Task<bool> ConnectAsync(int port, CancellationToken ct = default)
    {
        var target = $"127.0.0.1:{port}";
        var result = await RunAsync(new[] { "connect", target }, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);

        var connected = result.StandardOutput.Contains("connected", StringComparison.OrdinalIgnoreCase) &&
                        !result.StandardOutput.Contains("cannot", StringComparison.OrdinalIgnoreCase) &&
                        !result.StandardOutput.Contains("failed", StringComparison.OrdinalIgnoreCase);

        if (connected)
        {
            _serial = target;
            _log.Info("adb", $"Connected to {target}.");
        }
        return connected;
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_serial is null) return;
        await RunAsync(new[] { "disconnect", _serial }, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        _serial = null;
    }

    public async Task<bool> IsDeviceOnlineAsync(CancellationToken ct = default)
    {
        if (_serial is null) return false;
        var result = await RunAsync(new[] { "devices" }, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        return result.StandardOutput
            .Split('\n')
            .Any(line => line.Contains(_serial, StringComparison.OrdinalIgnoreCase) &&
                         line.Contains("device", StringComparison.OrdinalIgnoreCase) &&
                         !line.Contains("offline", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> IsBootCompletedAsync(CancellationToken ct = default)
    {
        var value = await GetPropertyAsync("sys.boot_completed", ct).ConfigureAwait(false);
        return value?.Trim() == "1";
    }

    public async Task<string?> GetPropertyAsync(string property, CancellationToken ct = default)
    {
        var result = await RunAsync(Targeted("shell", "getprop", property), TimeSpan.FromSeconds(10), ct)
            .ConfigureAwait(false);
        return result.Success ? result.StandardOutput.Trim() : null;
    }

    public async Task<AdbResult> InstallApkAsync(
        string apkPath, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!File.Exists(apkPath))
            return AdbResult.Fail($"File not found: {apkPath}");

        progress?.Report("Installing...");
        // -r reinstall keeping data, -g grant runtime permissions, -d allow downgrade
        var result = await RunAsync(
            Targeted("install", "-r", "-g", "-d", apkPath),
            TimeSpan.FromMinutes(10),
            ct).ConfigureAwait(false);

        var output = result.CombinedOutput;
        var succeeded = output.Contains("Success", StringComparison.OrdinalIgnoreCase);

        _log.Info("adb", $"install {Path.GetFileName(apkPath)} -> {(succeeded ? "Success" : output)}");
        return succeeded ? AdbResult.Ok(output) : AdbResult.Fail(ExtractFailureReason(output), result.ExitCode);
    }

    internal static string ExtractFailureReason(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "The installer gave no reason.";

        var line = output.Split('\n')
            .FirstOrDefault(l => l.Contains("Failure", StringComparison.OrdinalIgnoreCase) ||
                                 l.Contains("INSTALL_FAILED", StringComparison.OrdinalIgnoreCase));
        if (line is null) return output.Trim();

        var trimmed = line.Trim();
        return trimmed switch
        {
            _ when trimmed.Contains("INSTALL_FAILED_NO_MATCHING_ABIS") =>
                "the app has no version for this processor type (it is ARM-only)",
            _ when trimmed.Contains("INSTALL_FAILED_INSUFFICIENT_STORAGE") =>
                "Android has run out of storage space",
            _ when trimmed.Contains("INSTALL_FAILED_OLDER_SDK") =>
                "the app needs a newer Android version than this runtime provides",
            _ when trimmed.Contains("INSTALL_FAILED_ALREADY_EXISTS") =>
                "the app is already installed",
            _ when trimmed.Contains("INSTALL_PARSE_FAILED") =>
                "the APK file could not be read and may be corrupt",
            _ when trimmed.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE") =>
                "a different version of this app is installed and must be uninstalled first",
            _ => trimmed
        };
    }

    public async Task<AdbResult> UninstallAsync(string packageName, CancellationToken ct = default)
    {
        var result = await RunAsync(Targeted("uninstall", packageName), TimeSpan.FromMinutes(2), ct)
            .ConfigureAwait(false);
        var ok = result.CombinedOutput.Contains("Success", StringComparison.OrdinalIgnoreCase);
        _log.Info("adb", $"uninstall {packageName} -> {(ok ? "Success" : result.CombinedOutput)}");
        return ok ? AdbResult.Ok(result.StandardOutput) : AdbResult.Fail(result.CombinedOutput, result.ExitCode);
    }

    public async Task<AdbResult> LaunchAsync(string packageName, CancellationToken ct = default)
    {
        var result = await RunAsync(
            Targeted("shell", "monkey", "-p", packageName, "-c", "android.intent.category.LAUNCHER", "1"),
            TimeSpan.FromSeconds(30),
            ct).ConfigureAwait(false);

        var ok = result.Success && !result.CombinedOutput.Contains("No activities found", StringComparison.OrdinalIgnoreCase);
        _log.Info("adb", $"launch {packageName} -> {(ok ? "started" : result.CombinedOutput)}");
        return ok ? AdbResult.Ok(result.StandardOutput) : AdbResult.Fail(result.CombinedOutput, result.ExitCode);
    }

    public async Task<AdbResult> ForceStopAsync(string packageName, CancellationToken ct = default)
    {
        var result = await RunAsync(Targeted("shell", "am", "force-stop", packageName), TimeSpan.FromSeconds(30), ct)
            .ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<PackageInfo>> ListPackagesAsync(
        bool includeSystem = false, CancellationToken ct = default)
    {
        var args = includeSystem
            ? Targeted("shell", "pm", "list", "packages")
            : Targeted("shell", "pm", "list", "packages", "-3");

        var result = await RunAsync(args, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (!result.Success) return Array.Empty<PackageInfo>();

        return ParsePackageList(result.StandardOutput);
    }

    internal static IReadOnlyList<PackageInfo> ParsePackageList(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return Array.Empty<PackageInfo>();

        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("package:", StringComparison.OrdinalIgnoreCase))
            .Select(l => l["package:".Length..].Trim())
            .Where(l => l.Length > 0)
            .Select(entry =>
            {
                // "pm list packages -f" yields "<path>=<package>"; plain mode yields just the name.
                var idx = entry.LastIndexOf('=');
                return idx > 0
                    ? new PackageInfo(entry[(idx + 1)..], entry[..idx])
                    : new PackageInfo(entry);
            })
            .DistinctBy(p => p.PackageName)
            .OrderBy(p => p.PackageName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task<AdbResult> ShellAsync(string command, CancellationToken ct = default) =>
        RunAsync(Targeted("shell", command), TimeSpan.FromMinutes(2), ct);

    public Task<AdbResult> PushAsync(string localPath, string remotePath, CancellationToken ct = default) =>
        RunAsync(Targeted("push", localPath, remotePath), TimeSpan.FromMinutes(10), ct);

    public Task<AdbResult> PullAsync(string remotePath, string localPath, CancellationToken ct = default) =>
        RunAsync(Targeted("pull", remotePath, localPath), TimeSpan.FromMinutes(10), ct);

    public Task<AdbResult> PowerOffAsync(CancellationToken ct = default) =>
        RunAsync(Targeted("shell", "reboot", "-p"), TimeSpan.FromSeconds(20), ct);

    public async Task KillServerAsync(CancellationToken ct = default)
    {
        if (!File.Exists(AdbExecutable)) return;
        try
        {
            await RunAsync(new[] { "kill-server" }, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        }
        catch
        {
            // best effort
        }
    }
}
