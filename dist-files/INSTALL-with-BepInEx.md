# Broken Spectre PCVR Fix — Install (BepInEx included)

Makes the Steam build of Broken Spectre run on non-Meta PC VR headsets.
Verified on HP Reverb G2 + SteamVR.

**This archive already contains BepInEx.** Two steps, about a minute.

> Prefer to get BepInEx yourself? Use `BrokenSpectrePCVRFix-<version>.zip` instead — same fix,
> no third-party binaries.

---

## 1. Copy the files in

Extract everything in this archive into your Broken Spectre folder, so it looks like this:

```
Steam\steamapps\common\Broken Spectre\
├── BrokenSpectre.exe          <- already there
├── winhttp.dll                <- from this archive
├── doorstop_config.ini        <- from this archive
├── .doorstop_version          <- from this archive
└── BepInEx\
    ├── core\                  <- from this archive
    └── plugins\
        └── BrokenSpectrePCVRFix.dll
```

> Default install path is usually
> `C:\Program Files (x86)\Steam\steamapps\common\Broken Spectre`

You can leave `INSTALL-with-BepInEx.md`, `LICENSE`, `NOTICE`, `THIRD-PARTY.md` and
`THIRD-PARTY-LICENSES\` in the game folder or delete them — they are documentation only.

## 2. Patch OVRPlugin.dll

Right-click **`Patch-OVRPlugin.ps1`** → *Run with PowerShell*.

Or from a PowerShell window:

```powershell
.\Patch-OVRPlugin.ps1
```

If your game is on another drive:

```powershell
.\Patch-OVRPlugin.ps1 -GamePath "D:\SteamLibrary\steamapps\common\Broken Spectre"
```

This changes **one byte** in Meta's plugin to enable a non-Oculus code path Meta already shipped
but left switched off. It verifies the file first, backs it up to `OVRPlugin.dll.orig`, and
refuses to touch anything it does not recognise.

Expected output:

```
Bytes  : 44 8D 42 01  (expect 44 8D 42 01 unpatched, 44 8D 42 09 patched)
Wrote  : 0x01 -> 0x09 at offset 0x9929A
Verified against known-good patched hash.
Done. Patched.
```

To undo at any time: `.\Patch-OVRPlugin.ps1 -Revert`

---

## Playing

- At the startup prompt, choose **"Continue with controllers"**.
- **Grip** = grasp, **Trigger** = pinch, **Y or B** = menu button.

The Windows button on WMR/Reverb controllers will always open the SteamVR dashboard — that is
SteamVR reserving it, and the game cannot see it.

---

## If something goes wrong

**Game crashes on launch** — the plugin is installed but step 2 was skipped. Both are required.
The plugin opens a gate that the unpatched native library then refuses, and Unity crashes on the
half-initialised state. Run the patcher.

**Game still frozen / head-locked** — the plugin is not loading. Check that
`BepInEx\LogOutput.log` contains `Loading [Broken Spectre PCVR Fix 1.2.0]`. If there is no
`LogOutput.log` at all, `winhttp.dll` is not next to `BrokenSpectre.exe`.

**Stuck somewhere later** — open `BepInEx\config\dave.brokenspectre.metagate.cfg`, set
`LogCalibrationState = true`, reproduce, then attach `BepInEx\LogOutput.log` to an issue. It
records scene changes, on-screen text and live gesture state.

**After a Steam update** — updates overwrite `OVRPlugin.dll`. Re-run the patcher.

---

## Uninstall

1. `.\Patch-OVRPlugin.ps1 -Revert`  (or use Steam → *Verify integrity of game files*)
2. Delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` and the `BepInEx` folder

Nothing else in your install is modified.

---

## Notes

There is no anti-cheat in Broken Spectre, no VAC involvement (single-player), and no Steam DRM
wrapper. There is no ban risk. The worst case is Steam restoring the original file.

This fix is Apache 2.0 — see `LICENSE` and `NOTICE`. Bundled third-party components and their
licenses are listed in `THIRD-PARTY.md`. Unofficial and unaffiliated; not endorsed by
Stitch Media, Meta Platforms, Valve, or HP.
