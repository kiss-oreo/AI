using System.Text.Json;
using System.Text.Json.Serialization;
using PortableDroid.Core.Abstractions;

namespace PortableDroid.Infrastructure;

/// <summary>Loads and atomically saves config/settings.json (spec §46).</summary>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly IPathService _paths;
    private readonly ILogService _log;

    public SettingsService(IPathService paths, ILogService log)
    {
        _paths = paths;
        _log = log;
        Current = AppSettings.CreateDefault();
    }

    public AppSettings Current { get; private set; }

    public event EventHandler? SettingsChanged;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(_paths.SettingsFile))
            {
                _log.Info("core", "No settings.json found; writing defaults.");
                Current = AppSettings.CreateDefault();
                await SaveAsync(ct).ConfigureAwait(false);
                return;
            }

            var json = await File.ReadAllTextAsync(_paths.SettingsFile, ct).ConfigureAwait(false);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
            Current = loaded ?? AppSettings.CreateDefault();
            Sanitize(Current);
            _log.Info("core", $"Loaded settings from {_paths.ToRelative(_paths.SettingsFile)}.");
        }
        catch (Exception ex)
        {
            _log.Error("core", "settings.json could not be read; falling back to defaults.", ex);
            Current = AppSettings.CreateDefault();
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        try
        {
            Sanitize(Current);
            Directory.CreateDirectory(_paths.Config);

            // Atomic write: temp file then replace, so a crash can never truncate settings.
            var tmp = _paths.SettingsFile + ".tmp";
            var json = JsonSerializer.Serialize(Current, Options);
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);

            if (File.Exists(_paths.SettingsFile))
                File.Replace(tmp, _paths.SettingsFile, null);
            else
                File.Move(tmp, _paths.SettingsFile);

            _log.Info("core", "Settings saved.");
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.Error("core", "Failed to save settings.", ex);
        }
    }

    /// <summary>
    /// Rejects absolute paths in config. Storing "D:\..." would break portability the moment
    /// the folder is copied to another drive (spec §5).
    /// </summary>
    internal static void Sanitize(AppSettings settings)
    {
        settings.Storage.ProfilesPath = ForceRelative(settings.Storage.ProfilesPath, "profiles");
        settings.Storage.RuntimePath = ForceRelative(settings.Storage.RuntimePath, "runtime");
        settings.Storage.TempPath = ForceRelative(settings.Storage.TempPath, "temp");

        if (settings.Storage.UserdataSizeGB is < 4 or > 512) settings.Storage.UserdataSizeGB = 16;
        if (settings.Storage.MinFreeSpaceMB < 1024) settings.Storage.MinFreeSpaceMB = 8192;
        if (settings.Runtime.BootTimeoutSeconds is < 30 or > 900) settings.Runtime.BootTimeoutSeconds = 180;
        if (settings.Runtime.Fps is < 15 or > 144) settings.Runtime.Fps = 30;
        if (settings.Network.AdbPort is < 1024 or > 65535) settings.Network.AdbPort = 5556;
        if (settings.Network.QmpPort is < 1024 or > 65535) settings.Network.QmpPort = 5557;
        if (settings.Network.QmpPort == settings.Network.AdbPort) settings.Network.QmpPort = settings.Network.AdbPort + 1;
        if (string.IsNullOrWhiteSpace(settings.ActiveProfile)) settings.ActiveProfile = "default";
    }

    private static string ForceRelative(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return Path.IsPathRooted(value) ? fallback : value.Replace('/', Path.DirectorySeparatorChar);
    }
}
