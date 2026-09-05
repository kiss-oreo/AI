# Third-Party Licenses

PortableDroid does not vendor third-party binaries in this repository. Components
are provisioned into `payload/runtime/` at build/release time. Every component that
ever ships inside a PortableDroid ZIP must have its license text placed in this
directory and be listed below before release.

| Component | Purpose | License | Ships in ZIP? | Notes |
|---|---|---|---|---|
| QEMU | x86_64 virtualization (WHPX) | GPL-2.0-only | planned: yes | Run as a separate process, unmodified. Written offer for source must accompany binary distribution. |
| virglrenderer | GPU (virgl/Venus) forwarding | MIT | planned: yes | Preserve copyright notice. |
| SDL2 | QEMU display backend | Zlib | planned: yes | |
| Android platform-tools (adb) | device control | Apache-2.0 (SDK ToS applies) | **no** | Redistribution restricted by the Android SDK Terms; PortableDroid downloads it from Google with user consent, or the user supplies it. |
| Android system image (Vanilla/FOSS Bliss OS or AOSP x86_64) | the guest OS | Apache-2.0 / GPL-2.0 mix; see upstream | **no** | Provisioned by the user or downloaded with explicit consent + checksum verification. See docs/01-runtime-comparison.md §4. |
| Google Play Services / Play Store | — | proprietary | **never** | Not licensed for redistribution. |
| Houdini / libndk_translation (ARM native bridge) | ARM app translation | proprietary (Intel/Google) | **never** | User-supplied, opt-in only. PortableDroid detects it and reports capability. |

Rule: if a component is proprietary or its redistribution terms are unclear, it is
provisioned by the user, not bundled.
