# PortableDroid — Technical Architecture

Backend decision: see `01-runtime-comparison.md` (QEMU + WHPX + Android-x86 guest).

## 1. Processes

```
PortableDroid.exe            WPF UI thread + Core services (in-proc)
  ├─ qemu-system-x86_64.exe  child, job-object-bound, args from ProfileService
  └─ adb.exe                 child server on a PortableDroid-private port
```

Both children are launched under a Windows **Job Object** with
`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, so a crash of the UI can never orphan a VM.
ADB is started with `ADB_VENDOR_KEYS` / `ANDROID_ADB_SERVER_PORT` pointed inside
the portable folder so we never fight a system-wide adb server.

## 2. Layers

```
PortableDroid.App          WPF views + viewmodels only. No Process, no paths.
PortableDroid.Core         Interfaces + domain logic + orchestration.
PortableDroid.Runtime      QemuBackend, AdbService, argument builders.
PortableDroid.Infrastructure  PathService, JsonConfigStore, ProcessHost, Logging.
PortableDroid.Tests        xunit; unit tests for arg building, paths, parsing.
```

Dependency direction is strictly inward: App → Core ← Runtime/Infrastructure.
Core defines the interfaces; the others implement them; DI wires them at startup
(`Microsoft.Extensions.Hosting` generic host, so async + cancellation are natural).

## 3. Key interfaces (contracts, not yet implemented)

```csharp
public interface IPathService {
    string Root { get; }              // AppContext.BaseDirectory, normalized
    string Runtime { get; }           // <Root>\runtime
    string Profile(string name);      // <Root>\profiles\<name>
    string Userdata(string profile);  // <Root>\profiles\<name>\userdata
    string Logs { get; } string Temp { get; } string Backups { get; }
    void EnsureLayout();
}

public interface IAndroidBackend {                 // QemuBackend implements
    RuntimeState State { get; }
    event EventHandler<RuntimeState> StateChanged;
    Task StartAsync(RuntimeProfile p, CancellationToken ct);
    Task StopAsync(CancellationToken ct);          // graceful: adb reboot -p, then QMP
    Task<bool> WaitForBootAsync(TimeSpan timeout, CancellationToken ct);
}

public interface IAdbService {
    Task<AdbResult> InstallApkAsync(string path, IProgress<string>? p, CancellationToken ct);
    Task<AdbResult> UninstallAsync(string package, CancellationToken ct);
    Task<AdbResult> LaunchAsync(string package, CancellationToken ct);
    Task<AdbResult> ForceStopAsync(string package, CancellationToken ct);
    Task<IReadOnlyList<PackageInfo>> ListPackagesAsync(CancellationToken ct);
    Task<AdbResult> ShellAsync(string command, CancellationToken ct);
    Task<AdbResult> PushAsync(string local, string remote, CancellationToken ct);
    Task<AdbResult> PullAsync(string remote, string local, CancellationToken ct);
}

public interface IApkManager {                     // uses IAdbService + local apk parsing
    Task<ApkMetadata> InspectAsync(string apkPath, CancellationToken ct); // aapt-free: parse manifest
    Task<InstallOutcome> InstallAsync(string apkPath, IProgress<string>? p, CancellationToken ct);
    Task<IReadOnlyList<InstalledApp>> GetLibraryAsync(CancellationToken ct);
}

public interface IHardwareDetector { Task<HardwareInfo> DetectAsync(CancellationToken ct); }
public interface IProfileService { RuntimeProfile Resolve(HardwareInfo hw); /* + CRUD */ }
public interface IBackupManager { Task<string> BackupAsync(...); Task RestoreAsync(...); }
```

`RuntimeState` = `Stopped | Starting | Running | Stopping | Crashed | Error` (§11).

## 4. QEMU invocation (draft — to be validated in M1, values are hypotheses)

```
qemu-system-x86_64w.exe
  -accel whpx,kernel-irqchip=off        (fallback: -accel tcg,thread=multi)
  -cpu host                              (fallback: qemu64)
  -smp <profile.cpuCores>
  -m <profile.memoryMB>
  -drive file=<profile>\userdata\android.qcow2,if=virtio,cache=writeback,discard=unmap
  -device virtio-gpu-gl-pci             (fallback: -device virtio-vga)
  -display sdl,gl=on                    (embedded later via HWND reparenting)
  -netdev user,id=n0,hostfwd=tcp:127.0.0.1:<adbPort>-:5555
  -device virtio-net-pci,netdev=n0
  -device virtio-tablet-pci -device virtio-keyboard-pci
  -qmp tcp:127.0.0.1:<qmpPort>,server=on,wait=off
  -no-reboot -rtc base=localtime
```

Notes:
- **No `-snapshot`.** Ever. That flag is the #1 way to silently lose userdata.
- QMP is how RuntimeManager does health checks, graceful `system_powerdown`, and
  crash detection (socket closes unexpectedly → `Crashed`).
- Window embedding: MVP shows QEMU's own SDL window; M6 investigates reparenting
  the HWND into the WPF shell (§27).

## 5. Persistence model (§4, the critical milestone)

```
profiles/default/
  profile.json            ram, cores, resolution, fps, graphics mode
  userdata/android.qcow2  the only mutable Android state
  metadata/apps.json      cached library (icons, names) — regenerable, not truth
```

Truth lives in the qcow2. `apps.json` is a cache; if it disagrees with
`adb shell pm list packages`, ADB wins. Shutdown sequence:

```
UI Stop → adb shell reboot -p  (or QMP system_powerdown)
        → wait ≤ 20 s for guest to flush
        → QMP quit
        → if still alive after 5 s: kill job object, log a "dirty shutdown" warning
```
Dirty shutdown is logged and surfaced next boot ("last session ended unexpectedly")
but **never** triggers an automatic userdata reset (§50).

## 6. Portability rules (§5, §30, §31)

- `PathService.Root = Path.GetFullPath(AppContext.BaseDirectory)`.
- Config files store **relative** paths only; absolute paths are rejected by a
  validator and a unit test asserts no `[A-Z]:\\` literal appears in shipped JSON.
- qcow2 backing files: avoid backing chains, or store them with relative
  `backing_file` so moving D:→E: doesn't break the image.
- No registry writes. No `%APPDATA%` writes. Temp goes to `<Root>\temp`.
- A `SelfTest` on startup verifies the root is writable and warns if the folder
  sits on a network/removable volume with poor write latency.

## 7. Hardware detection & profiles (§17–§20)

`HardwareDetector` uses WMI/`GetSystemInfo`/`GlobalMemoryStatusEx`/DXGI:
CPU name, physical cores, RAM total/available, GPU + driver version, WHPX
availability (`WHvGetCapability`), Windows build, and `MSFT_PhysicalDisk.MediaType`
for SSD vs HDD.

Starting-point profile table (**hypotheses to be benchmarked in Phase 6**, §18/§19):

| Profile | Host RAM | Guest RAM | vCPU | Res | FPS |
|---|---|---|---|---|---|
| Ultra Low | 4 GB | 1280 MB | 2 | 1280x720 | 30 |
| Low | 6 GB | 1792 MB | 2 | 1280x720 | 30 |
| Balanced | 8 GB | 2560 MB | 3 | 1600x900 | 60 |
| Performance | 16 GB+ | 4096 MB | 4 | 1920x1080 | 60 |

Hard rule: `guestRam <= 0.45 * totalRam` and `vcpu <= max(2, physicalCores - 2)`.

## 8. Graphics decision tree (§21–§22)

```
GraphicsManager.Resolve(hw, setting)
  auto → WHPX present && GPU driver supports GL 4.3+ → virtio-gpu-gl (virgl)
       → else virtio-vga (software, capped 30 fps, resolution clamped)
  hardware / compatibility → forced
On boot failure with GL: log, mark profile "gl-failed", auto-retry once in
compatibility mode, and tell the user what changed.
```

## 9. Error handling contract (§36)

Every user-facing failure is a `PortableDroidError { Code, What, Why, WhatToTry }`.
UI shows the three sentences; the exception + QEMU stderr + ADB stderr go to logs.
Example codes: `ERR_WHPX_UNAVAILABLE`, `ERR_BOOT_TIMEOUT`, `ERR_GPU_INIT`,
`ERR_APK_ARCH_MISMATCH`, `ERR_DISK_SPACE`, `ERR_RUNTIME_MISSING`.

## 10. Logging (§35)

Serilog, rolling files in `<Root>\logs\`: `portable-droid.log`, `runtime.log`
(raw QEMU stderr), `adb.log`, `crash.log`. Package names are logged; APK contents,
credentials and guest filesystem contents are not.

## 11. Security posture (§37)

Untrusted APKs never touch Windows: they are `adb push`/`pm install`ed into the
guest only. The VM uses QEMU user-mode networking (NAT), no bridged adapter, no
host filesystem shares in the MVP. No elevation is requested by the app itself;
enabling WHPX is an instruction shown to the user, not an action we perform.
