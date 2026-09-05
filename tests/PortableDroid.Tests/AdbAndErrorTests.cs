using PortableDroid.Core.Models;
using PortableDroid.Runtime;
using PortableDroid.Runtime.Adb;
using Xunit;

namespace PortableDroid.Tests;

public class AdbParsingTests
{
    [Fact]
    public void ParsePackageList_ReadsPlainOutput()
    {
        const string output = """
                              package:com.example.one
                              package:com.example.two
                              package:com.example.three
                              """;

        var packages = AdbService.ParsePackageList(output);

        Assert.Equal(3, packages.Count);
        Assert.Contains(packages, p => p.PackageName == "com.example.two");
    }

    [Fact]
    public void ParsePackageList_ReadsPathQualifiedOutput()
    {
        const string output = "package:/data/app/com.example.one-1/base.apk=com.example.one";
        var packages = AdbService.ParsePackageList(output);

        Assert.Single(packages);
        Assert.Equal("com.example.one", packages[0].PackageName);
        Assert.Equal("/data/app/com.example.one-1/base.apk", packages[0].ApkPath);
    }

    [Fact]
    public void ParsePackageList_IgnoresNoiseAndDuplicates()
    {
        const string output = """

                              * daemon started successfully
                              package:com.example.one
                              package:com.example.one
                              """;

        var packages = AdbService.ParsePackageList(output);
        Assert.Single(packages);
    }

    [Fact]
    public void ParsePackageList_HandlesEmptyOutput() =>
        Assert.Empty(AdbService.ParsePackageList(""));

    [Theory]
    [InlineData("Failure [INSTALL_FAILED_NO_MATCHING_ABIS]", "ARM-only")]
    [InlineData("Failure [INSTALL_FAILED_INSUFFICIENT_STORAGE]", "storage")]
    [InlineData("Failure [INSTALL_FAILED_OLDER_SDK]", "newer Android")]
    [InlineData("Failure [INSTALL_PARSE_FAILED_NOT_APK]", "corrupt")]
    public void ExtractFailureReason_TranslatesInstallerCodesIntoPlainEnglish(string output, string expected) =>
        Assert.Contains(expected, AdbService.ExtractFailureReason(output), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void ExtractFailureReason_HandlesEmptyOutput() =>
        Assert.False(string.IsNullOrWhiteSpace(AdbService.ExtractFailureReason("")));
}

public class ErrorPresentationTests
{
    public static IEnumerable<object[]> AllErrors()
    {
        yield return new object[] { PortableDroidError.WhpxUnavailable() };
        yield return new object[] { PortableDroidError.RuntimeMissing(@"X:\runtime") };
        yield return new object[] { PortableDroidError.BootTimeout(180) };
        yield return new object[] { PortableDroidError.GpuInitFailed() };
        yield return new object[] { PortableDroidError.AdbUnavailable() };
        yield return new object[] { PortableDroidError.ApkInstallFailed("reason") };
        yield return new object[] { PortableDroidError.ApkArchMismatch(new[] { "arm64-v8a" }) };
        yield return new object[] { PortableDroidError.InvalidApk("x.apk") };
        yield return new object[] { PortableDroidError.InsufficientStorage(8192, 100) };
        yield return new object[] { PortableDroidError.InsufficientMemory(2048) };
        yield return new object[] { PortableDroidError.RuntimeCrashed(1) };
    }

    [Theory]
    [MemberData(nameof(AllErrors))]
    public void EveryErrorTellsTheUserWhatHappenedWhyAndWhatToDo(PortableDroidError error)
    {
        Assert.False(string.IsNullOrWhiteSpace(error.Code));
        Assert.False(string.IsNullOrWhiteSpace(error.What));
        Assert.False(string.IsNullOrWhiteSpace(error.Why));
        Assert.False(string.IsNullOrWhiteSpace(error.WhatToTry));
        Assert.StartsWith("ERR_", error.Code);

        var message = error.ToUserMessage();
        Assert.Contains(error.What, message);
        Assert.Contains("What you can try", message);
    }

    [Theory]
    [MemberData(nameof(AllErrors))]
    public void ErrorsAvoidRawJargonInTheHeadline(PortableDroidError error)
    {
        foreach (var jargon in new[] { "Exception", "stacktrace", "null reference", "0x8007" })
            Assert.DoesNotContain(jargon, error.What, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CrashMessageReassuresTheUserTheirDataIsSafe()
    {
        var error = PortableDroidError.RuntimeCrashed(1);
        Assert.Contains("NOT been deleted", error.WhatToTry);
    }
}

public class GraphicsManagerTests
{
    [Theory]
    [InlineData("Intel(R) HD Graphics 4000", true)]
    [InlineData("Intel(R) GMA 950", true)]
    [InlineData("Microsoft Basic Display Adapter", true)]
    [InlineData("", true)]
    [InlineData("NVIDIA GeForce GTX 1650", false)]
    [InlineData("Intel(R) UHD Graphics 620", false)]
    [InlineData("AMD Radeon RX 6600", false)]
    public void KnowsWhichGpusCannotHandleAcceleratedGraphics(string gpu, bool problematic) =>
        Assert.Equal(problematic, GraphicsManager.IsKnownProblematicGpu(gpu));
}

public class ApkManagerNamingTests
{
    [Theory]
    [InlineData("com.instagram.android", "Android")]
    [InlineData("com.example.calculator", "Calculator")]
    [InlineData("single", "Single")]
    public void TurnsAPackageNameIntoSomethingReadable(string package, string expected) =>
        Assert.Equal(expected, ApkManager.PrettifyPackageName(package));
}
