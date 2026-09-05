# PortableDroid — MVP Milestones

Rule for every milestone (§44): Plan → Implement → Build → Run → Test → Measure →
Fix → Document. A milestone is not "done" until it has been *actually executed on
Windows hardware* and the result recorded in `docs/benchmarks.md`.

| # | Milestone | Exit test | Status |
|---|---|---|---|
| M0 | Research + architecture + repo scaffold | docs reviewed, folder layout exists | **done** |
| M1 | Runtime prototype: QEMU boots the guest, `adb devices` shows it | `scripts/boot-prototype.ps1` → `getprop sys.boot_completed` = 1 | **blocked: needs Windows hardware + QEMU + image** |
| M2 | Persistence: install an app, power off, reboot, app still there | scripted round-trip, no `-snapshot` anywhere | **code done, run blocked on M1** |
| M3 | Core library: PathService, config, logging, HardwareDetector + unit tests | `dotnet test` green | **done, green in CI** |
| M4 | AdbService + ApkManager (install/uninstall/launch/stop/list) | integration test against a booted guest | **code done + unit tested; integration blocked on M1** |
| M5 | RuntimeManager state machine + QMP health/crash detection | kill QEMU externally → UI state becomes `Crashed`, logs written | **code done, run blocked on M1** |
| M6 | WPF shell: Dashboard, Applications, Install APK (drag & drop), Settings, Logs | manual walkthrough of §40 | **done, builds + packages in CI** |
| M7 | Profiles + graphics fallback | 4 GB machine boots on Ultra Low; GL failure falls back cleanly | **logic done + unit tested; needs hardware validation** |
| M8 | Portability pass: copy D:\ → E:\, run | §40 workflow passes from both drives | **enforced by unit tests; end-to-end run pending** |
| M9 | Backup/restore | backup while stopped, restore into a fresh folder, data intact | **code done, run pending** |
| M10 | Benchmarks + compatibility matrix | `docs/benchmarks.md` and `docs/compatibility.md` filled with measured data | **not started — requires hardware** |

## Definition of Done (§55) — tracked, none checked yet

- [ ] Runs from D: or E:
- [ ] No hardcoded C: paths (enforced by unit test)
- [ ] Runtime starts / stops reliably
- [ ] ADB connects reliably
- [ ] APK install / uninstall / launch
- [ ] Multiple apps installed
- [ ] Data survives restart
- [ ] Hardware detection
- [ ] Ultra Low profile works on 4 GB
- [ ] GPU acceleration where supported, with fallback
- [ ] Network access from guest
- [ ] Graceful errors, logs generated
- [ ] Folder copyable between drives
- [ ] Docs + THIRD_PARTY_LICENSES complete
- [ ] No known security regression

## Explicitly out of MVP scope

Individual app windows, desktop shortcuts, `.xapk`/split APK, controller/keymapping,
clipboard sync, screen recording, cloud backup, snapshots, Google Play, bundled ARM
native bridge.

## The honest gap

Everything that can be verified without an Android guest has been built and is green in CI
on `windows-latest`. Everything that requires booting Android is written but unproven.
The blocking dependency is a Windows machine with virtualization, a QEMU build and a
Vanilla/FOSS Android x86_64 image — none of which existed in the environment this was
developed in.

## Immediate next action (M1)

1. Provision `runtime/`: user-supplied or consented download of a Vanilla/FOSS
   Android-x86_64 image + a QEMU-for-Windows build with WHPX and virgl.
2. Hand-write `scripts/boot-prototype.ps1` with the argument list from
   `02-architecture.md §4` — no C# yet.
3. Measure: boot time, guest idle RAM as seen by Windows, host CPU idle.
4. Only when `adb devices` is stable do we start M3 (C# core).
