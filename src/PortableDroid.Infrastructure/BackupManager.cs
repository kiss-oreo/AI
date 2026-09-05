using System.IO.Compression;
using PortableDroid.Core.Abstractions;
using PortableDroid.Core.Models;

namespace PortableDroid.Infrastructure;

/// <summary>
/// Backup and restore of a profile (spec §29). A .pdbackup file is a zip containing the
/// profile folder. Backups may only be taken while the runtime is stopped, so the disk image
/// is guaranteed to be in a consistent state — we never snapshot a live, half-written image.
/// </summary>
public sealed class BackupManager : IBackupManager
{
    public const string Extension = ".pdbackup";

    private readonly IPathService _paths;
    private readonly ILogService _log;
    private readonly IRuntimeManager _runtime;

    public BackupManager(IPathService paths, ILogService log, IRuntimeManager runtime)
    {
        _paths = paths;
        _log = log;
        _runtime = runtime;
    }

    public async Task<BackupInfo> CreateBackupAsync(
        string profileName, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (_runtime.State != RuntimeState.Stopped)
            throw new InvalidOperationException(
                "Android must be stopped before a backup can be made, so that the data is not copied mid-write.");

        var source = _paths.ProfileDirectory(profileName);
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"Profile '{profileName}' does not exist.");

        Directory.CreateDirectory(_paths.Backups);
        var fileName = $"PortableDroid_{profileName}_{DateTime.Now:yyyy-MM-dd_HHmmss}{Extension}";
        var destination = Path.Combine(_paths.Backups, fileName);
        var staging = destination + ".part";

        progress?.Report("Preparing backup...");
        _log.Info("core", $"Creating backup of profile '{profileName}' -> {fileName}");

        try
        {
            await Task.Run(() =>
            {
                if (File.Exists(staging)) File.Delete(staging);
                // Write to a .part file first so an interrupted backup never looks valid.
                ZipFile.CreateFromDirectory(source, staging, CompressionLevel.Fastest, includeBaseDirectory: false);
            }, ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            File.Move(staging, destination);

            var info = new FileInfo(destination);
            progress?.Report("Backup complete.");
            _log.Info("core", $"Backup written: {fileName} ({info.Length / 1024 / 1024} MB)");
            return new BackupInfo(destination, fileName, info.CreationTimeUtc, info.Length);
        }
        catch
        {
            if (File.Exists(staging))
            {
                try { File.Delete(staging); } catch { /* ignore */ }
            }
            throw;
        }
    }

    public async Task RestoreBackupAsync(
        string backupFilePath, string profileName, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (_runtime.State != RuntimeState.Stopped)
            throw new InvalidOperationException("Android must be stopped before restoring a backup.");
        if (!File.Exists(backupFilePath))
            throw new FileNotFoundException("Backup file not found.", backupFilePath);

        var target = _paths.ProfileDirectory(profileName);

        // Existing data is moved aside, never deleted, until the restore has succeeded (spec §50).
        string? rescued = null;
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            rescued = target + $".replaced-{DateTime.Now:yyyyMMdd-HHmmss}";
            progress?.Report("Moving existing data aside...");
            Directory.Move(target, rescued);
            _log.Warn("core", $"Existing profile data moved to {Path.GetFileName(rescued)} before restore.");
        }

        try
        {
            progress?.Report("Restoring...");
            Directory.CreateDirectory(target);
            await Task.Run(() => ZipFile.ExtractToDirectory(backupFilePath, target, overwriteFiles: true), ct)
                .ConfigureAwait(false);

            progress?.Report("Restore complete.");
            _log.Info("core", $"Restored '{Path.GetFileName(backupFilePath)}' into profile '{profileName}'. " +
                              (rescued is null ? "" : $"Previous data kept at {Path.GetFileName(rescued)}."));
        }
        catch (Exception ex)
        {
            _log.Error("core", "Restore failed; rolling back to previous data.", ex);
            try
            {
                if (rescued is not null)
                {
                    if (Directory.Exists(target)) Directory.Delete(target, true);
                    Directory.Move(rescued, target);
                }
            }
            catch (Exception rollbackEx)
            {
                _log.Error("core", $"Rollback also failed. Previous data is at {rescued}", rollbackEx);
            }
            throw;
        }
    }

    public IReadOnlyList<BackupInfo> ListBackups()
    {
        if (!Directory.Exists(_paths.Backups)) return Array.Empty<BackupInfo>();

        return Directory.GetFiles(_paths.Backups, "*" + Extension)
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.CreationTimeUtc)
            .Select(f => new BackupInfo(f.FullName, f.Name, f.CreationTimeUtc, f.Length))
            .ToList();
    }

    public void DeleteBackup(string backupFilePath)
    {
        if (!File.Exists(backupFilePath)) return;
        var full = Path.GetFullPath(backupFilePath);

        // Only ever delete inside our own backups folder.
        if (!full.StartsWith(Path.GetFullPath(_paths.Backups), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to delete a file outside the backups folder.");

        File.Delete(full);
        _log.Info("core", $"Deleted backup {Path.GetFileName(full)} (user confirmed).");
    }
}
