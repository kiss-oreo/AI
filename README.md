# PortableDroid

A lightweight, **portable** Android runtime manager for low-end Windows PCs.
Extract to `D:\PortableDroid`, run `PortableDroid.exe`, install APKs, and your
apps and their data are still there next time.

> **Project status: Phase 1 complete (research + architecture + scaffold).**
> No application code exists yet and nothing has been benchmarked. Everything in
> `docs/` marked as a value or timing is a hypothesis until `docs/benchmarks.md`
> has real rows in it.

## What it is

PortableDroid is *not* a new emulator. It is a Windows management layer that
drives an existing open-source Android runtime:

```
WPF UI  →  Core services  →  QEMU (WHPX)  →  Android x86_64  →  your APKs
                          →  ADB
```

## Documents

| Doc | Contents |
|---|---|
| [docs/01-runtime-comparison.md](docs/01-runtime-comparison.md) | Runtime candidates, scorecard, the decision and why |
| [docs/02-architecture.md](docs/02-architecture.md) | Layers, interfaces, QEMU invocation, persistence, profiles, errors |
| [docs/03-mvp-milestones.md](docs/03-mvp-milestones.md) | M0–M10 with exit tests, Definition of Done |
| [docs/benchmarks.md](docs/benchmarks.md) | Measured performance (empty until measured) |
| [docs/compatibility.md](docs/compatibility.md) | App compatibility matrix (empty until tested) |
| [THIRD_PARTY_LICENSES/](THIRD_PARTY_LICENSES/README.md) | What may and may not be bundled |

## Repository layout

```
src/     PortableDroid.App | .Core | .Runtime | .Infrastructure   (scaffolded, empty)
tests/   PortableDroid.Tests                                      (scaffolded, empty)
scripts/ boot-prototype.ps1  — M1 QEMU boot experiment (untested)
payload/ the portable folder template that becomes the release ZIP
docs/    architecture and research
```

## Non-negotiable rules for contributors

1. No hardcoded drive letters or absolute paths. Everything derives from
   `AppContext.BaseDirectory`.
2. `-snapshot` must never be passed to QEMU — it silently destroys userdata.
3. Userdata is never deleted or reset automatically, not even after a crash.
4. No proprietary components (Google Play, Houdini/libndk) are bundled.
5. No performance claim without a row in `docs/benchmarks.md`.
6. Build milestone by milestone; each one must run and be tested before the next.

## Next step

Milestone M1: provision `payload/runtime/`, run `scripts/boot-prototype.ps1` on a
real Windows machine, and get `sys.boot_completed = 1` over ADB.
