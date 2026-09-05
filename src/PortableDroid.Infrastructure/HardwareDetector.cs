using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using PortableDroid.Core.Abstractions;
using PortableDroid.Core.Models;

namespace PortableDroid.Infrastructure;

/// <summary>Detects the machine PortableDroid is running on (spec §17).</summary>
public sealed class HardwareDetector : IHardwareDetector
{
    private readonly ILogService _log;
    private readonly IPathService _paths;

    public HardwareDetector(ILogService log, IPathService paths)
    {
        _log = log;
        _paths = paths;
    }

    public HardwareInfo? Cached { get; private set; }

    public async Task<HardwareInfo> DetectAsync(CancellationToken ct = default)
    {
        var info = await Task.Run(Detect, ct).ConfigureAwait(false);
        Cached = info;
        _log.Info("core", "Hardware: " + info);
        return info;
    }

    private HardwareInfo Detect()
    {
        var cpuName = "Unknown";
        var physicalCores = 0;
        var gpuName = "Unknown";
        var gpuDriver = "Unknown";
        var virtualizationEnabled = false;
        var mediaType = StorageMediaType.Unknown;
        long totalRamMB = 0;
        long availableRamMB = 0;

        TryWmi("SELECT Name, NumberOfCores, VirtualizationFirmwareEnabled FROM Win32_Processor", mo =>
        {
            cpuName = (mo["Name"] as string)?.Trim() ?? cpuName;
            if (mo["NumberOfCores"] is uint cores) physicalCores += (int)cores;
            if (mo["VirtualizationFirmwareEnabled"] is bool vfe && vfe) virtualizationEnabled = true;
        });

        TryWmi("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem", mo =>
        {
            if (mo["TotalPhysicalMemory"] is ulong bytes) totalRamMB = (long)(bytes / 1024 / 1024);
        });

        TryWmi("SELECT FreePhysicalMemory FROM Win32_OperatingSystem", mo =>
        {
            if (mo["FreePhysicalMemory"] is ulong kb) availableRamMB = (long)(kb / 1024);
        });

        TryWmi("SELECT Name, DriverVersion FROM Win32_VideoController", mo =>
        {
            var name = (mo["Name"] as string)?.Trim();
            if (string.IsNullOrWhiteSpace(name)) return;
            // Prefer a discrete GPU name if several controllers exist.
            if (gpuName == "Unknown" || LooksDiscrete(name))
            {
                gpuName = name!;
                gpuDriver = (mo["DriverVersion"] as string)?.Trim() ?? gpuDriver;
            }
        });

        mediaType = DetectMediaType();

        if (totalRamMB <= 0)
            totalRamMB = (long)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024);
        if (physicalCores <= 0)
            physicalCores = Math.Max(1, Environment.ProcessorCount / 2);

        var hypervisorPresent = IsHypervisorPresent();

        return new HardwareInfo
        {
            CpuName = cpuName,
            PhysicalCores = physicalCores,
            LogicalCores = Environment.ProcessorCount,
            CpuArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            TotalRamMB = totalRamMB,
            AvailableRamMB = availableRamMB,
            GpuName = gpuName,
            GpuDriverVersion = gpuDriver,
            VirtualizationAvailable = virtualizationEnabled || hypervisorPresent,
            HypervisorPresent = hypervisorPresent,
            WindowsVersion = RuntimeInformation.OSDescription,
            MediaType = mediaType,
            FreeDiskSpaceMB = GetFreeSpaceMB()
        };
    }

    private static bool LooksDiscrete(string name) =>
        name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Arc", StringComparison.OrdinalIgnoreCase);

    private bool IsHypervisorPresent()
    {
        var present = false;
        TryWmi("SELECT HypervisorPresent FROM Win32_ComputerSystem", mo =>
        {
            if (mo["HypervisorPresent"] is bool hp) present = hp;
        });
        return present;
    }

    private StorageMediaType DetectMediaType()
    {
        try
        {
            var driveLetter = Path.GetPathRoot(_paths.Root)?.TrimEnd('\\', '/');
            if (string.IsNullOrWhiteSpace(driveLetter)) return StorageMediaType.Unknown;

            var drive = new DriveInfo(driveLetter);
            if (drive.DriveType == DriveType.Removable) return StorageMediaType.Removable;
            if (drive.DriveType == DriveType.Network) return StorageMediaType.Unknown;

            var result = StorageMediaType.Unknown;
            TryWmi("SELECT MediaType FROM MSFT_PhysicalDisk", mo =>
            {
                if (mo["MediaType"] is ushort mt)
                {
                    // 3 = HDD, 4 = SSD, 5 = SCM per MSFT_PhysicalDisk documentation
                    if (mt == 4 && result != StorageMediaType.Ssd) result = StorageMediaType.Ssd;
                    else if (mt == 3 && result == StorageMediaType.Unknown) result = StorageMediaType.Hdd;
                }
            }, @"root\Microsoft\Windows\Storage");

            return result;
        }
        catch
        {
            return StorageMediaType.Unknown;
        }
    }

    private long GetFreeSpaceMB()
    {
        try
        {
            var root = Path.GetPathRoot(_paths.Root);
            if (string.IsNullOrWhiteSpace(root)) return 0;
            return new DriveInfo(root).AvailableFreeSpace / 1024 / 1024;
        }
        catch
        {
            return 0;
        }
    }

    private void TryWmi(string query, Action<ManagementBaseObject> handler, string scope = @"root\cimv2")
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, query);
            foreach (var o in searcher.Get())
            {
                using var mo = o;
                try { handler(mo); } catch { /* per-row failures are non-fatal */ }
            }
        }
        catch (Exception ex)
        {
            _log.Debug("core", $"WMI query failed ({query}): {ex.Message}");
        }
    }
}

/// <summary>Samples CPU/RAM for the performance page (spec §34).</summary>
public sealed class ResourceMonitor : IResourceMonitor
{
    private readonly IAndroidBackend _backend;
    private DateTime _lastSampleTime = DateTime.UtcNow;
    private TimeSpan _lastCpuTotal = TimeSpan.Zero;

    public ResourceMonitor(IAndroidBackend backend) => _backend = backend;

    public async Task<ResourceSnapshot> SampleAsync(CancellationToken ct = default)
    {
        var runtimeRam = await _backend.GetRuntimeMemoryMBAsync().ConfigureAwait(false);
        var self = Process.GetCurrentProcess();
        var uiRam = self.WorkingSet64 / 1024 / 1024;

        var now = DateTime.UtcNow;
        var cpuTotal = self.TotalProcessorTime;
        var elapsed = (now - _lastSampleTime).TotalMilliseconds;
        var used = (cpuTotal - _lastCpuTotal).TotalMilliseconds;
        _lastSampleTime = now;
        _lastCpuTotal = cpuTotal;

        var cpuPercent = elapsed > 0
            ? Math.Clamp(used / (elapsed * Environment.ProcessorCount) * 100.0, 0, 100)
            : 0;

        return new ResourceSnapshot(
            Math.Round(cpuPercent, 1),
            runtimeRam,
            uiRam,
            _backend.State,
            _backend.State == RuntimeState.Running);
    }
}
