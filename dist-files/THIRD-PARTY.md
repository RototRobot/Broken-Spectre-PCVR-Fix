# Third-party components

This applies to the **`-with-BepInEx`** release archive, which redistributes BepInEx and its
dependencies for convenience. The plugin-only archive and the source repository contain no
third-party binaries.

Full license texts are in `THIRD-PARTY-LICENSES/`.

| Component | Version | License | Copyright | Upstream |
|---|---|---|---|---|
| BepInEx (`BepInEx.dll`, `BepInEx.Preloader.dll`, `winhttp.dll`, `doorstop_config.ini`) | 5.4.23.5 | LGPL-2.1 | BepInEx contributors | https://github.com/BepInEx/BepInEx |
| HarmonyX (`0Harmony.dll`, `0Harmony20.dll`, `BepInEx.Harmony.dll`, `HarmonyXInterop.dll`) | bundled with BepInEx 5.4.23.5 | MIT | (c) 2020 BepInEx | https://github.com/BepInEx/HarmonyX |
| MonoMod (`MonoMod.RuntimeDetour.dll`, `MonoMod.Utils.dll`) | bundled with BepInEx 5.4.23.5 | MIT | (c) 2015–2020 0x0ade | https://github.com/MonoMod/MonoMod |
| Mono.Cecil (`Mono.Cecil.dll`, `.Mdb`, `.Pdb`, `.Rocks`) | bundled with BepInEx 5.4.23.5 | MIT | (c) 2008–2015 Jb Evain; (c) 2008–2011 Novell, Inc. | https://github.com/jbevain/cecil |

All binaries are **unmodified**, taken verbatim from the official BepInEx release
[`BepInEx_win_x64_5.4.23.5.zip`](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5).

## LGPL-2.1 note

BepInEx is LGPL-2.1. It is redistributed here unmodified and unlinked — this project's plugin
is a separate work that BepInEx loads at runtime. The complete corresponding source for BepInEx
is publicly available at the upstream URL above, at the tagged release named in this file.

If you would prefer to obtain BepInEx yourself, use the plugin-only archive
(`BrokenSpectrePCVRFix-<version>.zip`) instead; it contains no third-party code.

## Not redistributed

- **OVRPlugin.dll / Meta XR SDK** — proprietary to Meta Platforms, Inc. `Patch-OVRPlugin.ps1`
  modifies one byte of the copy already present in your own game install, and can revert it.
- **Broken Spectre game files** — property of Stitch Media. The plugin is built against your
  own installed copy.
