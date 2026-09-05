using PortableDroid.Core.Models;
using PortableDroid.Core.Services;
using Xunit;

namespace PortableDroid.Tests;

/// <summary>
/// These guard the two rules that decide whether PortableDroid ruins someone's 4 GB laptop:
/// never take more than 45% of RAM, never take every core.
/// </summary>
public class ProfileServiceTests
{
    private static HardwareInfo Machine(long ramMB, int cores) => new()
    {
        TotalRamMB = ramMB,
        PhysicalCores = cores,
        LogicalCores = cores * 2
    };

    [Theory]
    [InlineData(4096, PerformanceProfileKind.UltraLow)]
    [InlineData(6144, PerformanceProfileKind.Low)]
    [InlineData(8192, PerformanceProfileKind.Balanced)]
    [InlineData(16384, PerformanceProfileKind.Performance)]
    public void ClassifyHardware_PicksBandFromRam(long ramMB, PerformanceProfileKind expected) =>
        Assert.Equal(expected, ProfileService.ClassifyHardware(Machine(ramMB, 4)));

    [Theory]
    [InlineData(4096)]
    [InlineData(6144)]
    [InlineData(8192)]
    [InlineData(16384)]
    public void ClampMemory_NeverExceeds45PercentOfRam(long totalRamMB)
    {
        var hw = Machine(totalRamMB, 4);
        var clamped = ProfileService.ClampMemory(999_999, hw);
        Assert.True(clamped <= totalRamMB * ProfileService.MaxRamFraction,
            $"{clamped} MB exceeds 45% of {totalRamMB} MB");
    }

    [Fact]
    public void ClampMemory_LeavesWindowsUsableOnA4GbMachine()
    {
        var clamped = ProfileService.ClampMemory(4096, Machine(4096, 2));
        Assert.True(clamped <= 1856, $"Android would take {clamped} MB of a 4 GB machine");
        Assert.True(clamped >= 512);
    }

    [Fact]
    public void ClampMemory_IsAMultipleOf128() =>
        Assert.Equal(0, ProfileService.ClampMemory(2000, Machine(16384, 8)) % 128);

    [Theory]
    [InlineData(2, 1)]   // dual core: Android gets one, Windows keeps one
    [InlineData(4, 2)]
    [InlineData(6, 4)]
    [InlineData(8, 4)]   // baseline caps at 4 even though 6 would fit
    public void ClampCores_LeavesCoresForWindows(int physicalCores, int expectedMax)
    {
        var hw = Machine(16384, physicalCores);
        var baseline = ProfileService.BaselineFor(PerformanceProfileKind.Performance).cores;
        var clamped = ProfileService.ClampCores(baseline, hw);
        Assert.True(clamped <= expectedMax, $"{clamped} cores on a {physicalCores}-core CPU");
        Assert.True(clamped >= 1);
    }

    [Fact]
    public void ClampCores_NeverTakesEveryCore()
    {
        for (var cores = 3; cores <= 32; cores++)
        {
            var clamped = ProfileService.ClampCores(999, Machine(16384, cores));
            Assert.True(clamped < cores, $"Android took all {cores} cores");
        }
    }

    [Theory]
    [InlineData("1280x720", 1280, 720)]
    [InlineData("1920x1080", 1920, 1080)]
    [InlineData(" 1600 x 900 ", 1600, 900)]
    public void TryParseResolution_AcceptsValidValues(string text, int w, int h)
    {
        Assert.True(ProfileService.TryParseResolution(text, out var width, out var height));
        Assert.Equal(w, width);
        Assert.Equal(h, height);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("100x100")]      // too small
    [InlineData("99999x99999")]  // absurd
    [InlineData("1280")]
    public void TryParseResolution_RejectsBadValues(string text) =>
        Assert.False(ProfileService.TryParseResolution(text, out _, out _));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("../escape")]
    [InlineData("sub\\dir")]
    [InlineData("sub/dir")]
    public void ValidateProfileName_RejectsPathTraversal(string name) =>
        Assert.ThrowsAny<ArgumentException>(() => ProfileService.ValidateProfileName(name));

    [Fact]
    public void ValidateProfileName_AcceptsNormalNames()
    {
        ProfileService.ValidateProfileName("default");
        ProfileService.ValidateProfileName("gaming");
        ProfileService.ValidateProfileName("test-2");
    }
}
