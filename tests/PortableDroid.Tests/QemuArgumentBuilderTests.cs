using PortableDroid.Core.Models;
using PortableDroid.Runtime.Qemu;
using Xunit;

namespace PortableDroid.Tests;

/// <summary>
/// The command line is the single most dangerous string in the product: one wrong flag and
/// every app the user installed disappears. It is therefore tested harder than anything else.
/// </summary>
public class QemuArgumentBuilderTests
{
    private static readonly RuntimeProfile Profile = new()
    {
        Name = "default",
        MemoryMB = 2048,
        CpuCores = 2,
        Width = 1280,
        Height = 720,
        AdbPort = 5556,
        QmpPort = 5557
    };

    private static IReadOnlyList<string> Build(
        GraphicsMode graphics = GraphicsMode.Hardware,
        bool accel = true,
        bool network = true) =>
        QemuArgumentBuilder.Build(Profile, @"X:\pd\profiles\default\userdata\android.qcow2",
            graphics, accel, network, systemImagePath: null);

    [Fact]
    public void NeverEmitsSnapshotFlag()
    {
        foreach (var graphics in Enum.GetValues<GraphicsMode>())
        foreach (var accel in new[] { true, false })
        foreach (var network in new[] { true, false })
        {
            var args = QemuArgumentBuilder.Build(Profile, "img.qcow2", graphics, accel, network);
            Assert.DoesNotContain("-snapshot", args);
        }
    }

    [Fact]
    public void Validate_ThrowsIfSnapshotSneaksIn()
    {
        var poisoned = Build().Concat(new[] { "-snapshot" }).ToList();
        var ex = Assert.Throws<InvalidOperationException>(() => QemuArgumentBuilder.Validate(poisoned));
        Assert.Contains("discard all Android data", ex.Message);
    }

    [Fact]
    public void Validate_ThrowsIfNoDiskConfigured() =>
        Assert.Throws<InvalidOperationException>(() =>
            QemuArgumentBuilder.Validate(new[] { "-m", "2048" }));

    [Fact]
    public void UsesTheProfileMemoryAndCores()
    {
        var args = Build();
        Assert.Equal("2048", ValueAfter(args, "-m"));
        Assert.Equal("2", ValueAfter(args, "-smp"));
    }

    [Fact]
    public void UsesHardwareAccelerationWhenAvailable()
    {
        var args = Build(accel: true);
        Assert.StartsWith("whpx", ValueAfter(args, "-accel"));
        Assert.Equal("host", ValueAfter(args, "-cpu"));
    }

    [Fact]
    public void FallsBackToEmulationWhenAccelerationIsMissing()
    {
        var args = Build(accel: false);
        Assert.StartsWith("tcg", ValueAfter(args, "-accel"));
        Assert.Equal("qemu64", ValueAfter(args, "-cpu"));
    }

    [Fact]
    public void HardwareGraphicsRequestsTheGlDevice()
    {
        var args = Build(GraphicsMode.Hardware);
        Assert.Contains("virtio-gpu-gl-pci", args);
        Assert.Contains(args, a => a.Contains("gl=on"));
    }

    [Fact]
    public void CompatibilityGraphicsAvoidsGl()
    {
        var args = Build(GraphicsMode.Compatibility);
        Assert.Contains("virtio-vga", args);
        Assert.Contains(args, a => a.Contains("gl=off"));
        Assert.DoesNotContain("virtio-gpu-gl-pci", args);
    }

    [Fact]
    public void ForwardsTheAdbPortOnLoopbackOnly()
    {
        var netdev = Build().First(a => a.StartsWith("user,id=n0"));
        Assert.Contains("hostfwd=tcp:127.0.0.1:5556-:5555", netdev);
    }

    [Fact]
    public void DisablingNetworkRemovesTheNic()
    {
        var args = Build(network: false);
        Assert.Contains("-nic", args);
        Assert.Contains("none", args);
        Assert.DoesNotContain(args, a => a.Contains("hostfwd"));
    }

    [Fact]
    public void BindsTheControlChannelToLoopbackOnly()
    {
        var qmp = ValueAfter(Build(), "-qmp");
        Assert.StartsWith("tcp:127.0.0.1:5557", qmp);
    }

    [Fact]
    public void AttachesTheUserdataImageAsAPersistentDisk()
    {
        var drive = Build().First(a => a.StartsWith("file="));
        Assert.Contains("android.qcow2", drive);
        Assert.Contains("format=qcow2", drive);
        Assert.DoesNotContain("readonly", drive);
    }

    [Fact]
    public void RejectsAnEmptyUserdataPath() =>
        Assert.Throws<ArgumentException>(() =>
            QemuArgumentBuilder.Build(Profile, "", GraphicsMode.Auto, true, true));

    [Fact]
    public void CreateImageArguments_MakeAQcow2OfTheRequestedSize()
    {
        var args = QemuArgumentBuilder.BuildCreateImageArguments(@"X:\img.qcow2", 16);
        Assert.Equal(new[] { "create", "-f", "qcow2", @"X:\img.qcow2", "16G" }, args);
    }

    private static string ValueAfter(IReadOnlyList<string> args, string flag)
    {
        var index = args.ToList().IndexOf(flag);
        Assert.True(index >= 0 && index + 1 < args.Count, $"flag {flag} not found");
        return args[index + 1];
    }
}
