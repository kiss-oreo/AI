using System.Text.Json;
using PortableDroid.Core.Abstractions;
using PortableDroid.Core.Models;

namespace PortableDroid.Runtime;

/// <summary>
/// Decides between accelerated and software rendering, and remembers profiles where
/// acceleration has already failed so the user is not made to sit through the same
/// failed boot twice (spec §21, §22).
/// </summary>
public sealed class GraphicsManager : IGraphicsManager
{
    private readonly IPathService _paths;
    private readonly ILogService _log;
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public GraphicsManager(IPathService paths, ILogService log)
    {
        _paths = paths;
        _log = log;
        Load();
    }

    private string StateFile => Path.Combine(_paths.Cache, "graphics-state.json");

    public GraphicsMode Resolve(HardwareInfo hardware, GraphicsMode requested)
    {
        if (requested != GraphicsMode.Auto)
            return requested;

        // Without CPU virtualization the guest is already software-emulated; asking the GPU
        // path to work on top of that is a common way to get a black screen.
        if (!hardware.VirtualizationAvailable)
        {
            _log.Info("runtime", "Graphics: compatibility mode (no hardware virtualization detected).");
            return GraphicsMode.Compatibility;
        }

        if (IsKnownProblematicGpu(hardware.GpuName))
        {
            _log.Info("runtime", $"Graphics: compatibility mode (GPU '{hardware.GpuName}' predates reliable GL support).");
            return GraphicsMode.Compatibility;
        }

        _log.Info("runtime", "Graphics: hardware acceleration selected.");
        return GraphicsMode.Hardware;
    }

    /// <summary>
    /// Very old integrated parts do not expose an OpenGL version the accelerated path needs.
    /// This list is conservative and only excludes hardware that is known to be too old.
    /// </summary>
    internal static bool IsKnownProblematicGpu(string gpuName)
    {
        if (string.IsNullOrWhiteSpace(gpuName)) return true;

        var name = gpuName.ToLowerInvariant();
        string[] tooOld =
        {
            "gma", "hd graphics 2000", "hd graphics 3000", "hd graphics 4000",
            "standard vga", "microsoft basic display", "vmware svga", "virtualbox"
        };
        return tooOld.Any(marker => name.Contains(marker));
    }

    public void RecordFailure(string profileName)
    {
        lock (_gate)
        {
            if (_failed.Add(profileName))
            {
                _log.Warn("runtime", $"Recorded a graphics acceleration failure for profile '{profileName}'.");
                Save();
            }
        }
    }

    public bool HasPreviouslyFailed(string profileName)
    {
        lock (_gate) return _failed.Contains(profileName);
    }

    public void ClearFailure(string profileName)
    {
        lock (_gate)
        {
            if (_failed.Remove(profileName)) Save();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(StateFile)) return;
            var names = JsonSerializer.Deserialize<string[]>(File.ReadAllText(StateFile));
            if (names is null) return;
            foreach (var n in names) _failed.Add(n);
        }
        catch (Exception ex)
        {
            _log.Debug("runtime", "Could not read graphics state: " + ex.Message);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(_paths.Cache);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(_failed.ToArray()));
        }
        catch (Exception ex)
        {
            _log.Debug("runtime", "Could not save graphics state: " + ex.Message);
        }
    }
}
