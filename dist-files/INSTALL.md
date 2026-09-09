# Broken Spectre PCVR Fix — Install

Makes the Steam build of Broken Spectre run on non-Meta PC VR headsets.
Verified on HP Reverb G2 + SteamVR. Full write-up: see the project page.

Three steps, about two minutes.

---

## 1. Install BepInEx

Download **`BepInEx_win_x64_5.4.23.5.zip`**:

  https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5

It must be the **win_x64** build — the game is 64-bit Mono.

Extract it into your Broken Spectre folder so `winhttp.dll` sits next to `BrokenSpectre.exe`:

```
Steam\steamapps\common\Broken Spectre\
├── BrokenSpectre.exe
├── winhttp.dll            <- from BepInEx
├── doorstop_config.ini    <- from BepInEx
└── BepInEx\               <- from BepInEx
```

Launch the game once, then quit. This creates `BepInEx\plugins\`.

> Default install path is usually
> `C:\Program Files (x86)\Steam\steamapps\common\Broken Spectre`

---

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

This changes **one byte** in Meta's plugin to enable a non-Oculus code path Meta already
shipped but left switched off. It verifies the file first, backs it up to `OVRPlugin.dll.orig`,
and refuses to touch anything it does not recognise.

Expected output:

```
Bytes  : 44 8D 42 01  (expect 44 8D 42 01 unpatched, 44 8D 42 09 patched)
Wrote  : 0x01 -> 0x09 at offset 0x9929A
Verified against known-good patched hash.
Done. Patched.
```

To undo at any time: `.\Patch-OVRPlugin.ps1 -Revert`

---

## 3. Install the plugin

Copy **`BrokenSpectrePCVRFix.dll`** into:

```
Steam\steamapps\common\Broken Spectre\BepInEx\plugins\
```

---

## Playing

- At the startup prompt, choose **"Continue with controllers"**.
- **Grip** = grasp, **Trigger** = pinch, **Y or B** = menu button.

The Windows button on WMR/Reverb controllers will always open the SteamVR dashboard —
that is SteamVR reserving it, and it cannot be used by the game.

---

## If something goes wrong

**Game crashes on launch** — you installed the plugin but skipped step 2. Both are required;
the plugin alone opens a gate that the unpatched native library then refuses, and Unity
crashes on the half-initialised state. Run the patcher.

**Game still frozen / head-locked** — step 2 ran but the plugin is not loading. Check that
`BepInEx\LogOutput.log` contains `Loading [Broken Spectre PCVR Fix 1.2.0]`.

**Stuck somewhere later** — open `BepInEx\config\dave.brokenspectre.metagate.cfg`, set
`LogCalibrationState = true`, reproduce, then attach `BepInEx\LogOutput.log` to an issue.
It records scene changes, on-screen text and live gesture state.

**After a Steam update** — updates overwrite `OVRPlugin.dll`. Re-run the patcher.

---

## Uninstall

1. `.\Patch-OVRPlugin.ps1 -Revert`  (or use Steam → *Verify integrity of game files*)
2. Delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` and the `BepInEx` folder

Nothing else in your install is modified.

---

## Notes

There is no anti-cheat in Broken Spectre, no VAC involvement (single-player), and no Steam
DRM wrapper. There is no ban risk. The worst case is Steam restoring the original file.

Apache 2.0 — see LICENSE and NOTICE. Unofficial and unaffiliated; not endorsed by
Stitch Media, Meta Platforms, Valve, or HP.
