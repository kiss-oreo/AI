using PortableDroid.Core.Abstractions;
using PortableDroid.Core.Models;

namespace PortableDroid.Infrastructure;

/// <summary>
/// Owns the persistent Android disk image (spec §4, §32). The image is created exactly once
/// and reused for the life of the profile; nothing here ever deletes user data.
/// </summary>
public sealed class StorageManager : IStorageManager
{
    private readonly IPathService _paths;
    private readonly ISettingsService _settings;
    private readonly ILogService _log;

    public StorageManager(IPathService paths, ISettingsService settings, ILogService log)
    {
        _paths = paths;
        _settings = settings;
        _log = log;
    }

    public long GetFreeSpaceMB()
    {
        try
        {
            var root = Path.GetPathRoot(_paths.Root);
            if (string.IsNullOrWhiteSpace(root)) return long.MaxValue;
            return new DriveInfo(root).AvailableFreeSpace / 1024 / 1024;
        }
        catch
        {
            return long.MaxValue;
        }
    }

    public bool UserdataImageExists(string profileName) =>
        File.Exists(_paths.ProfileUserdataImage(profileName));

    public long GetUserdataSizeMB(string profileName)
    {
        var path = _paths.ProfileUserdataImage(profileName);
        return File.Exists(path) ? new FileInfo(path).Length / 1024 / 1024 : 0;
    }

    public void VerifyFreeSpaceOrThrow(long requiredMB)
    {
        var free = GetFreeSpaceMB();
        if (free < requiredMB)
            throw new PortableDroidException(PortableDroidError.InsufficientStorage(requiredMB, free));
    }

    /// <summary>
    /// Makes sure the profile folder and its qcow2 exist. Creating the image is delegated to
    /// qemu-img by the runtime layer; here we only guarantee the folder and check space.
    /// </summary>
    public void EnsureUserdataImage(RuntimeProfile profile)
    {
        Directory.CreateDirectory(_paths.ProfileUserdataDirectory(profile.Name));

        if (!UserdataImageExists(profile.Name))
        {
            // qcow2 is sparse, so we only need headroom, not the full virtual size.
            VerifyFreeSpaceOrThrow(_settings.Current.Storage.MinFreeSpaceMB);
            _log.Info("core", $"Userdata image for profile '{profile.Name}' does not exist yet; it will be created.");
        }
        else
        {
            _log.Info("core",
                $"Using existing userdata image ({GetUserdataSizeMB(profile.Name)} MB on disk) for profile '{profile.Name}'.");
        }
    }
}
