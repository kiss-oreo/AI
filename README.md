# PortableDroid

A lightweight, **portable** Android runtime manager for low-end Windows PCs.
Extract to `D:\PortableDroid`, run `PortableDroid.exe`, install APKs — and your apps
and their data are still there next time.

---

## Status — read this first

| What | State |
|---|---|
| Windows application (WPF, .NET 8) | **Built, compiles, packaged as a portable EXE by CI** |
| Unit tests | **Passing on `windows-latest` in GitHub Actions** |
| End-to-end Android boot / APK install | **Never executed** — needs QEMU + an Android image on real hardware |
| Performance numbers | **None measured.** `docs/benchmarks.md` is deliberately empty |

The application is complete and builds into a real executable. What has **not** happened is
a run against a live Android guest: this project was developed in a Linux container with no
Windows machine, no QEMU and no Android image, so milestones M1/M2 (guest boots, data
survives a restart) are still unverified. Treat this as a tested codebase with an
unverified integration, not as a finished product. Nothing in these docs claims a
performance figure that has not been measured.

## Download

CI builds `PortableDroid-portable-win-x64.zip` on every push:
**[Actions → latest run → Artifacts](../../actions/workflows/build.yml)**

The ZIP contains a self-contained `PortableDroid.exe` (no .NET install required) plus the
portable folder layout.

## Getting it running

```
1. Extract the ZIP anywhere:  D:\PortableDroid
2. Run Get-Runtime.ps1        -> tells you which runtime pieces are missing
3. Provide them (see below)
4. Run PortableDroid.exe      -> Start Android -> drag an APK onto the window
```

### The runtime pieces you supply

PortableDroid ships **without** QEMU, adb or an Android image, on purpose — see
[THIRD_PARTY_LICENSES](THIRD_PARTY_LICENSES/README.md). `Get-Runtime.ps1` checks for and
explains each one:

| Component | Goes in | Where to get it |
|---|---|---|
| QEMU for Windows (with WHPX) | `runtime\qemu\` | https://qemu.weilnetz.de/w64/ |
| Android platform-tools (adb) | `runtime\platform-tools\` | Google; the script can fetch it with your consent |
| Android x86_64 image (Vanilla/FOSS) | `runtime\android\` | Bliss OS or Android-x86 — **not** a GApps build |

You also need hardware virtualization: VT-x/AMD-V in BIOS, and "Windows Hypervisor
Platform" in Windows Features. PortableDroid detects when it is missing and explains
how to enable it; it never changes your Windows configuration itself.

## What it does

- **Portable** — every path derives from the folder the EXE sits in. Copy `D:\PortableDroid`
  to `E:\` or a USB stick and it keeps working. No registry, no `%APPDATA%`, no installer.
- **Persistent** — your apps and logins live in a qcow2 disk image inside the folder.
  `-snapshot` is banned in code and by a unit test, because it would silently wipe user data.
- **Low-end first** — profiles cap Android at 45% of RAM and never hand it every CPU core.
  Ultra Low targets a 4 GB machine.
- **Honest errors** — every failure says what happened, why, and what to try. Raw diagnostics
  go to `logs\`, not into your face.
- **Safe by default** — APKs never touch Windows; they are installed into the guest over ADB.
  No elevation, no bridged networking, no host filesystem shares. Nothing is ever
  auto-deleted, not even after a crash.

## Architecture

```
PortableDroid.exe (WPF)
  └─ RuntimeManager ── IAndroidBackend ── QemuBackend ─── qemu-system-x86_64.exe
                    └─ IAdbService ───── AdbService ───── adb.exe (127.0.0.1)
                    └─ ApkManager, ProfileService, GraphicsManager, StorageManager,
                       BackupManager, HardwareDetector, FileLogService
```

`IAndroidBackend` is the seam: QEMU today, Waydroid or AVD later without touching the UI.
Full detail in [docs/02-architecture.md](docs/02-architecture.md); the runtime choice and
the alternatives considered are in [docs/01-runtime-comparison.md](docs/01-runtime-comparison.md).

## Repository layout

```
src/PortableDroid.Core            models, interfaces, profile logic, APK/AXML parser
src/PortableDroid.Infrastructure  paths, logging, settings, hardware, storage, backup
src/PortableDroid.Runtime         QEMU backend, ADB service, runtime manager, APK manager
src/PortableDroid.App             WPF UI
tests/PortableDroid.Tests         unit tests (run in CI on Windows)
payload/                          the portable folder template that becomes the ZIP
scripts/Get-Runtime.ps1           runtime provisioning checker
scripts/boot-prototype.ps1        standalone QEMU boot experiment (M1, untested)
docs/                             research, architecture, milestones, benchmarks
```

## Building it yourself

```powershell
dotnet build PortableDroid.sln -c Release
dotnet test  tests/PortableDroid.Tests/PortableDroid.Tests.csproj -c Release
dotnet publish src/PortableDroid.App/PortableDroid.App.csproj -c Release -r win-x64 `
  --self-contained -p:PublishSingleFile=true -o publish/app
```

Requires the .NET 8 SDK on Windows (WPF cannot be built on Linux or macOS).

## What is tested, and what is not

Covered by unit tests: memory/CPU clamping, profile selection, resolution parsing, QEMU
argument construction (including the `-snapshot` ban), path portability across drive
letters, settings sanitisation, APK manifest parsing from real binary XML, ARM-only and
Google-dependency detection, ADB output parsing, installer error translation, GPU
allowlisting, error-message completeness.

**Not covered:** anything that needs a running Android guest. That is the next milestone.

## Contributing rules

1. No hardcoded drive letters or absolute paths — derive from `AppContext.BaseDirectory`.
2. `-snapshot` must never reach QEMU.
3. Userdata is never deleted automatically, not even after a crash.
4. No proprietary components bundled (Google Play, Houdini/libndk_translation).
5. No performance claim without a row in `docs/benchmarks.md`.
6. `Process.Start` belongs in `ProcessRunner` only.

## Licensing

Project code: see the repository. Third-party redistribution rules — including why Google
Play and ARM translation layers are **not** bundled — are documented in
[THIRD_PARTY_LICENSES](THIRD_PARTY_LICENSES/README.md).
