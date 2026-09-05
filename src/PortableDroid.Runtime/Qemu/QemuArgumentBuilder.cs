using PortableDroid.Core.Models;

namespace PortableDroid.Runtime.Qemu;

/// <summary>
/// Builds the QEMU command line. Pure and side-effect free so it can be unit tested without
/// QEMU present — this is the highest-risk string in the product, so it is tested hard.
/// </summary>
public static class QemuArgumentBuilder
{
    /// <summary>
    /// This flag would make QEMU write all guest changes to a throwaway temp file, silently
    /// destroying every app and login the user has. It must never appear.
    /// </summary>
    public const string ForbiddenSnapshotFlag = "-snapshot";

    public static IReadOnlyList<string> Build(
        RuntimeProfile profile,
        string userdataImagePath,
        GraphicsMode effectiveGraphics,
        bool accelerationAvailable,
        bool networkEnabled,
        string? installerIsoPath = null,
        string? systemImagePath = null)
    {
        if (string.IsNullOrWhiteSpace(userdataImagePath))
            throw new ArgumentException("A userdata image path is required.", nameof(userdataImagePath));

        var args = new List<string>
        {
            "-name", "PortableDroid",
            "-machine", "q35",
            "-accel", accelerationAvailable ? "whpx,kernel-irqchip=off" : "tcg,thread=multi",
            "-cpu", accelerationAvailable ? "host" : "qemu64",
            "-smp", profile.CpuCores.ToString(),
            "-m", profile.MemoryMB.ToString(),
            "-rtc", "base=localtime",
            "-no-reboot"
        };

        // Persistent Android data. cache=writeback keeps HDDs usable; discard=unmap keeps the
        // sparse qcow2 from growing forever.
        args.Add("-drive");
        args.Add($"file={userdataImagePath},if=virtio,format=qcow2,cache=writeback,discard=unmap,id=userdata");

        if (!string.IsNullOrWhiteSpace(systemImagePath) && File.Exists(systemImagePath))
        {
            args.Add("-drive");
            args.Add($"file={systemImagePath},if=virtio,format=raw,readonly=on,id=system");
        }

        if (!string.IsNullOrWhiteSpace(installerIsoPath))
        {
            args.Add("-cdrom");
            args.Add(installerIsoPath);
            args.Add("-boot");
            args.Add("d");
        }

        // Graphics (spec §21/§22).
        if (effectiveGraphics == GraphicsMode.Compatibility)
        {
            args.Add("-device");
            args.Add("virtio-vga");
            args.Add("-display");
            args.Add("sdl,gl=off");
        }
        else
        {
            args.Add("-device");
            args.Add("virtio-gpu-gl-pci");
            args.Add("-display");
            args.Add("sdl,gl=on");
        }

        // Networking: user-mode NAT only. No bridge, no host shares (spec §23, §37).
        if (networkEnabled)
        {
            args.Add("-netdev");
            args.Add($"user,id=n0,hostfwd=tcp:127.0.0.1:{profile.AdbPort}-:5555");
            args.Add("-device");
            args.Add("virtio-net-pci,netdev=n0");
        }
        else
        {
            args.Add("-nic");
            args.Add("none");
        }

        args.Add("-device");
        args.Add("virtio-tablet-pci");
        args.Add("-device");
        args.Add("virtio-keyboard-pci");

        // Control channel for health checks and graceful shutdown.
        args.Add("-qmp");
        args.Add($"tcp:127.0.0.1:{profile.QmpPort},server=on,wait=off");

        Validate(args);
        return args;
    }

    public static IReadOnlyList<string> BuildCreateImageArguments(string imagePath, int sizeGB) => new[]
    {
        "create", "-f", "qcow2", imagePath, $"{sizeGB}G"
    };

    /// <summary>Last line of defence: refuse to launch anything that would discard user data.</summary>
    public static void Validate(IReadOnlyList<string> args)
    {
        if (args.Any(a => string.Equals(a, ForbiddenSnapshotFlag, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                "Refusing to start: '-snapshot' would discard all Android data on shutdown.");

        if (!args.Contains("-drive") && !args.Contains("-cdrom"))
            throw new InvalidOperationException("Refusing to start: no disk image was configured.");
    }
}
