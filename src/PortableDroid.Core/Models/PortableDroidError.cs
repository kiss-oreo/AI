namespace PortableDroid.Core.Models;

/// <summary>
/// Structured, user-facing error (spec §36): what happened, why it may have happened,
/// what the user can try. Raw diagnostics stay in <see cref="Diagnostics"/> and go to logs only.
/// </summary>
public sealed record PortableDroidError(
    string Code,
    string What,
    string Why,
    string WhatToTry,
    string? Diagnostics = null)
{
    public override string ToString() => $"[{Code}] {What}";

    public string ToUserMessage() =>
        $"{What}\n\nPossible causes:\n{Why}\n\nWhat you can try:\n{WhatToTry}";

    // ---- Catalogue of known failures -------------------------------------------------

    public static PortableDroidError WhpxUnavailable(string? diag = null) => new(
        "ERR_WHPX_UNAVAILABLE",
        "Hardware acceleration is not available.",
        "- Virtualization (VT-x / AMD-V) is disabled in your BIOS or UEFI\n" +
        "- The Windows Hypervisor Platform feature is turned off\n" +
        "- Another hypervisor or anti-cheat driver is holding the CPU virtualization extensions",
        "1. Reboot into BIOS/UEFI and enable Intel VT-x or AMD-V / SVM.\n" +
        "2. In Windows, open 'Turn Windows features on or off' and enable 'Windows Hypervisor Platform'.\n" +
        "3. Restart Windows and start PortableDroid again.\n" +
        "PortableDroid can still run without acceleration, but it will be very slow.",
        diag);

    public static PortableDroidError RuntimeMissing(string expectedPath) => new(
        "ERR_RUNTIME_MISSING",
        "The Android runtime files are missing.",
        "- The runtime folder was not provisioned yet\n" +
        "- Files were moved, deleted, or blocked by antivirus\n" +
        "- The download or extraction was incomplete",
        "Run 'Setup Runtime' from the Settings page, or place the runtime files in:\n" +
        expectedPath,
        expectedPath);

    public static PortableDroidError BootTimeout(int seconds, string? diag = null) => new(
        "ERR_BOOT_TIMEOUT",
        $"Android did not finish starting within {seconds} seconds.",
        "- The machine is slow, or a hard disk is being used instead of an SSD\n" +
        "- Hardware acceleration is unavailable, so the system is emulating the CPU\n" +
        "- The graphics mode is not supported by your GPU driver\n" +
        "- The Android image is damaged",
        "1. Try the 'Compatibility' graphics mode in Settings.\n" +
        "2. Lower the performance profile to 'Ultra Low'.\n" +
        "3. Check that hardware acceleration is enabled.\n" +
        "4. Look at logs\\runtime.log for the runtime's own messages.",
        diag);

    public static PortableDroidError GpuInitFailed(string? diag = null) => new(
        "ERR_GPU_INIT",
        "Graphics acceleration could not be initialised.",
        "- Your GPU driver is older than the accelerated graphics path requires\n" +
        "- An integrated GPU is being used with a limited OpenGL implementation\n" +
        "- The graphics device was rejected by the runtime",
        "PortableDroid has switched to Compatibility (software) rendering so you can keep working.\n" +
        "To get better performance, update your graphics driver and then set Graphics back to 'Automatic'.",
        diag);

    public static PortableDroidError AdbUnavailable(string? diag = null) => new(
        "ERR_ADB_UNAVAILABLE",
        "PortableDroid cannot talk to Android.",
        "- Android is not running yet, or is still starting\n" +
        "- The connection to the Android device was lost\n" +
        "- The platform tools are missing from the runtime folder",
        "1. Wait a few seconds and try again.\n" +
        "2. Use 'Restart Android' on the dashboard.\n" +
        "3. If the problem continues, check logs\\adb.log.",
        diag);

    public static PortableDroidError ApkInstallFailed(string reason, string? diag = null) => new(
        "ERR_APK_INSTALL",
        "The app could not be installed.",
        "- The APK file is invalid or incomplete\n" +
        "- The app requires a processor type this runtime does not provide\n" +
        "- Android is not running\n" +
        "- There is not enough storage inside Android\n" +
        $"- The installer reported: {reason}",
        "1. Make sure Android is running.\n" +
        "2. Re-download the APK and try again.\n" +
        "3. If the app is ARM-only, it cannot run without a translation layer.\n" +
        "Full technical details are in logs\\adb.log.",
        diag);

    public static PortableDroidError ApkArchMismatch(IEnumerable<string> abis) => new(
        "ERR_APK_ARCH_MISMATCH",
        "This app only supports ARM processors.",
        $"The APK contains native code for: {string.Join(", ", abis)}, but this Android runtime is x86_64.\n" +
        "PortableDroid does not bundle an ARM translation layer, because those components are proprietary " +
        "and cannot be legally redistributed.",
        "1. Look for an x86 or 'universal' build of the same app.\n" +
        "2. Or install a native bridge yourself into the Android image (advanced, unsupported).",
        null);

    public static PortableDroidError InvalidApk(string path, string? diag = null) => new(
        "ERR_APK_INVALID",
        "That file is not a valid APK.",
        "- The file is not an Android package\n" +
        "- The download is corrupt or truncated\n" +
        "- It is a .xapk / .apks bundle, which is not supported yet",
        "Check the file and try again. PortableDroid currently supports single .apk files only.\n" +
        $"File: {Path.GetFileName(path)}",
        diag);

    public static PortableDroidError InsufficientStorage(long neededMB, long freeMB) => new(
        "ERR_DISK_SPACE",
        "There is not enough free disk space.",
        $"PortableDroid needs about {neededMB} MB free, but only {freeMB} MB is available on this drive.",
        "Free up disk space, or move the PortableDroid folder to a drive with more room.\n" +
        "You can copy the whole folder to another drive and it will keep working.",
        null);

    public static PortableDroidError InsufficientMemory(long totalMB) => new(
        "ERR_LOW_MEMORY",
        "This computer may not have enough memory to run Android.",
        $"Only {totalMB} MB of RAM was detected. Android needs roughly 1.2 GB on top of Windows.",
        "PortableDroid has selected the smallest performance profile.\n" +
        "Close other applications before starting Android.",
        null);

    public static PortableDroidError RuntimeCrashed(int exitCode, string? diag = null) => new(
        "ERR_RUNTIME_CRASHED",
        "Android stopped unexpectedly.",
        "- The runtime ran out of memory\n" +
        "- A graphics driver problem\n" +
        "- The Android image or userdata is damaged",
        "Your data has NOT been deleted.\n" +
        "1. Use 'Restart Android'.\n" +
        "2. If it keeps happening, switch to Compatibility graphics and a lower profile.\n" +
        $"3. Send logs\\crash.log if you report this. (exit code {exitCode})",
        diag);

    public static PortableDroidError Unexpected(string operation, Exception ex) => new(
        "ERR_UNEXPECTED",
        $"Something went wrong while trying to {operation}.",
        "This is an unexpected internal error.",
        "Try the operation again. If it keeps failing, check logs\\portable-droid.log.",
        ex.ToString());
}

/// <summary>Exception carrying a user-presentable error.</summary>
public sealed class PortableDroidException : Exception
{
    public PortableDroidError Error { get; }

    public PortableDroidException(PortableDroidError error, Exception? inner = null)
        : base(error.What, inner) => Error = error;
}
