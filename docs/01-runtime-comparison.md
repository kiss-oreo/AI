# PortableDroid — Phase 1: Android Runtime Technology Comparison

Status: research / decision document. **No code has been written or tested yet.**
Date: 2026-09-05.

## 1. What we are actually choosing

PortableDroid = Windows management app + an Android runtime it controls.
So the decision is only: *which Android-on-Windows execution stack do we drive?*

Constraints that dominate the choice:

| Constraint | Weight |
|---|---|
| Low-end PCs (4–8 GB RAM, iGPU) | very high |
| Fully portable (no installer, runs from D:\ / E:\) | very high |
| Persistent userdata across restarts | very high |
| Redistributable open-source licensing | high |
| GPU acceleration | high |
| APK (incl. eventually ARM) compatibility | medium-high |
| Maintenance burden for a small team | high |

## 2. Candidates

### A. QEMU (Windows build) + WHPX + Android-x86-derived system image
Windows-native `qemu-system-x86_64.exe`, hardware acceleration via the Windows
Hypervisor Platform (WHPX), guest = an Android-x86 / Bliss OS style x86_64 build.
GPU via virtio-gpu + virglrenderer (GL) or Venus (Vulkan); ADB over a forwarded
TCP port. Community QEMU-on-Windows builds already ship WHPX + virgl/Venus, e.g.
WINQ-EMU (QEMU GPL-2.0, virglrenderer MIT) ([winq-emu](https://github.com/cmspam/winq-emu)),
and the classic WHPX build recipe is well documented
([qemu_whpx](https://gist.github.com/startergo/9a46fec6caf879027518db08a13df503)).
Bliss OS documents QEMU installation and virgl works on Bliss 14/15
([r/Androidx86](https://www.reddit.com/r/Androidx86/comments/w4ic1x/run_androidx86_on_qemukvm_with_virgl_arm/)).

+ Fully portable: QEMU is a folder of DLLs + exe; no service, no registry.
+ Runtime dir is relocatable; disk images are just files under `userdata/`.
+ Persistence is free and robust: qcow2 `userdata.img` + `-snapshot` never used.
+ WHPX = near-native x86 CPU speed on any Win10/11 machine with VT-x/AMD-V.
+ Open source (QEMU GPL-2.0, virglrenderer MIT) — redistributable with notices.
+ We control RAM (`-m`), cores (`-smp`), resolution, so the profile system maps 1:1.
− RAM floor: a VM needs its own kernel/zygote; realistically ~1.2 GB minimum.
− Requires Hyper-V/WHPX feature enabled → needs admin once, and conflicts with
  some anti-cheat/other hypervisors. TCG fallback exists but is slow.
− Graphics stack (virgl/Venus) is the fragile part on old Intel iGPU drivers.
− Guest image licensing must be chosen carefully (see §4).

### B. Windows Subsystem for Android (WSA) / WSABuilds
Microsoft's Hyper-V-based Android container.
− **Discontinued by Microsoft (support ended 2025)**; community rebuilds only
  ([WSABuilds](https://github.com/MustardChef/WSABuilds), [waydroid#2108](https://github.com/waydroid/waydroid/issues/2108)).
− Installs as an MSIX package into the user profile → **not portable**, userdata
  lives in an AppData VHDX we don't own. Fails requirement §5/§30 outright.
− Redistribution of MS binaries is not permitted for us. **Rejected.**

### C. Waydroid inside WSL2
Container-based, very low overhead on Linux
([WayDroid](https://www.reddit.com/r/windows/comments/1d1187u/windows_subsystem_for_android_wsa_alternatives/)).
− Needs WSL2 + a custom kernel with binder/ashmem, systemd, Wayland compositor,
  GPU passthrough. Not portable (WSL distros live under AppData), heavy setup,
  brittle. Great tech, wrong host. **Rejected for MVP; revisit as backend #2.**

### D. Android Studio AVD / emulator (`qemu2` + gfxstream)
+ Best GPU story (gfxstream), official, ARM64 images exist.
− Licensing: the emulator binaries are Apache-2.0-ish but system images are under
  the Android SDK ToS which restricts redistribution; AVD state lives in
  `%USERPROFILE%\.android` by default (relocatable via env vars, but clunky).
− Heavier RAM footprint, slower boot than a stripped Android-x86 image.
  **Rejected as bundled runtime; keep as an optional "bring your own SDK" backend.**

### E. Write our own hypervisor / CPU emulator
Explicitly out of scope per §8. **Rejected.**

### F. Bare QEMU with an ARM64 guest via WHPX-ARM fork
([qemu-whpx-arm64](https://github.com/lokashrinav/qemu-whpx-arm64)) — solves ARM
app compat natively, but only hardware-accelerated on ARM64 Windows hosts; on
x86 hosts it is TCG (slow). Useless for our low-end x86 target. **Rejected**,
noted as future path for Snapdragon Windows laptops.

## 3. Scorecard (1–5, higher is better)

| Criterion | A QEMU+WHPX | B WSA | C Waydroid/WSL2 | D AVD | F QEMU-ARM |
|---|---|---|---|---|---|
| Low-end performance | 4 | 4 | 4 | 3 | 1 |
| RAM footprint | 3 | 3 | 5 | 2 | 1 |
| Portability (D:/E:) | 5 | 1 | 1 | 2 | 5 |
| Persistent userdata | 5 | 3 | 4 | 4 | 5 |
| GPU acceleration | 3 | 4 | 3 | 5 | 2 |
| APK compatibility | 4 | 4 | 4 | 4 | 5 |
| ARM app support | 2 | 3 | 3 | 3 | 5 |
| Licensing / redistribution | 4 | 1 | 3 | 2 | 4 |
| Ease of integration | 4 | 2 | 1 | 3 | 3 |
| Maintenance | 3 | 1 | 2 | 3 | 2 |
| **Total** | **37** | **26** | **30** | **31** | **33** |

## 4. Decision

**Selected: (A) Windows-native QEMU + WHPX accelerator + an x86_64 Android-x86-derived guest, driven over ADB (TCP), with a pluggable `IAndroidBackend` so Waydroid/AVD can be added later.**

Rationale: it is the only candidate that satisfies the two non-negotiables —
*portable folder* and *persistent userdata we own* — while staying open source
and giving us direct dials for RAM/CPU/resolution profiles.

### Guest image policy (licensing — §38)
- We **do not ship** an Android image in the repo or the ZIP by default.
- PortableDroid ships a *runtime provisioning* step: the user points at, or the
  app downloads over HTTPS with checksum + explicit consent, a **Vanilla/FOSS
  Bliss OS or plain AOSP x86_64** build. Bliss's own licensing page says public
  Vanilla/FOSS/AOSP builds are the redistribution-safe path, and that Gapps,
  Widevine and the **ARM Native Bridge (Houdini / libndk_translation) are
  proprietary and not licensed for product bundling**
  ([blissos.org/licensing](https://blissos.org/licensing.html)).
- Therefore: **no Google Play Services, no Houdini/libndk in the distribution.**
  ARM translation is a *user-provided, opt-in* module the app can detect and
  report on (§24), never bundled. Bliss 15.8.5 FOSS already ships without
  libhoudini ([r/BlissOS](https://www.reddit.com/r/BlissOS/comments/141onqp/how_to_install_arm_apps_on_bliss_os_158/)).
- QEMU is GPL-2.0 → we ship it unmodified as a separate process (no linking),
  with source offer + license text in `THIRD_PARTY_LICENSES/`.

### Consequences / risks accepted
1. WHPX must be enabled → one-time admin action. We detect and explain, never
   silently change Windows config (§37).
2. Graphics: try `virtio-gpu-gl` (virgl) → fall back to `virtio-vga` software.
   Fallback is a first-class code path (§22), not an error case.
3. ARM-only APKs will be reported "ARM-only / unsupported" (§26) unless the user
   supplies a native bridge themselves.
4. ~1.2 GB RAM floor on 4 GB machines; Ultra Low profile targets that.

## 5. Target architecture (summary)

```
PortableDroid.exe (WPF, .NET 8, self-contained, x64)
  └─ Core
      RuntimeManager ── IAndroidBackend ── QemuBackend ── qemu-system-x86_64.exe
      AndroidManager ── IAdbService ───── AdbService ─── adb.exe (127.0.0.1:5555)
      ApkManager, StorageManager, HardwareDetector, GraphicsManager,
      NetworkManager, ProfileManager, BackupManager, LogManager
  └─ Infrastructure (paths, json config, process host, serilog)
```

Everything path-related derives from `AppContext.BaseDirectory` → `PathService`.
No absolute paths anywhere; see `docs/02-architecture.md`.

## 6. Open questions to verify with measurement (not guesses)

- Real boot time of a stripped Bliss/AOSP x86_64 image at 1.5 GB / 2 vCPU on a
  4 GB Haswell laptop, HDD vs SSD.
- Whether virgl works on Intel HD 4000-era WDDM drivers or needs the Venus path.
- qcow2 vs raw userdata: write amplification on HDD.
- Idle RAM of the WPF shell (target < 80 MB).

## 7. Next step

`docs/02-architecture.md` (done) + `docs/03-mvp-milestones.md` (done), then
Milestone M1: boot a guest via a hand-written `qemu.args` and confirm `adb devices`
reports the device. No UI until M1 passes.
