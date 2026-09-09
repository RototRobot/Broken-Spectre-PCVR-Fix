# Broken Spectre PCVR Fix

Makes the Steam build of **Broken Spectre** run on non-Meta PC VR headsets.

The Steam store page says it plainly: *"This game has only been tested for Meta Quest VR Headsets"*, with VR support listed as **Oculus devices only**. On anything else the game launches, renders a frame, and then locks up — the view is welded to your face and nothing responds.

That is not a bug. It is three deliberate vendor gates stacked on top of each other, plus one genuine hardware gap. This fixes all of them.

**Verified on:** HP Reverb G2 (`hpmotioncontroller`) + SteamVR 2.17 OpenXR, Windows 11.
**Should also work on:** Valve Index, HTC Vive, other WMR headsets — any SteamVR/OpenXR runtime. Untested; reports welcome.

---

## Symptoms this fixes

| What you see | Actual cause |
|---|---|
| View head-locked, nothing responds, stuck at language select | `MetaXRFeature` disables itself → `OVRPlugin` never initialises → `OVRCameraRig`/`OVRInput` are dead |
| Game crashes instantly on launch after a partial fix | Managed gate opened but native `OVRPlugin` still refused; Unity tears down a half-initialised feature |
| Tutorial "palm up + pinch" gesture never completes | Gesture logic reads `OVRHand` finger-bone data your headset cannot produce |
| The menu button does nothing; Windows button opens the SteamVR dashboard | The game asks for a button path that does not exist on your controller |

---

## What was actually wrong

### 1. The managed gate — `MetaXRFeature`

`Meta.XR.MetaXRFeature.OnInstanceCreate` in `Oculus.VR.dll` decompiles to roughly this:

```csharp
bool found = false;
foreach (string ext in OpenXRRuntime.GetAvailableExtensions())
    if (ext == "XR_META_headset_id") { found = true; break; }

if (!found) {
    string n = OpenXRRuntime.name.ToLower();
    if (!n.Contains("meta") && !n.Contains("oculus")) {
        Debug.LogWarningFormat("[MetaXRFeature] MetaXRFeature is disabled ...");
        return false;                                   // <-- the blocker
    }
}
return OVRPlugin.UnityOpenXR.OnInstanceCreate(xrInstance);
```

SteamVR advertises 44 extensions; `XR_META_headset_id` is not among them, and it is not named "meta" or "oculus". So the feature switches itself off, `OVRPlugin` never gets an XR session, and every `OVRCameraRig` / `OVRInput` call downstream returns nothing.

**Fix:** a Harmony transpiler flips the `ldc.i4.0` at `IL_0000` to `ldc.i4.1`, seeding the local "extension found" flag true. One opcode, scoped to that single method.

### 2. The native gate — `OVRPlugin.dll`

Opening the managed gate alone makes the game **crash on launch**. `OVRPlugin` has its own check, and it is explicit about it:

```
[OVRPlugin][INFO]  CompositorOpenXR::PreInitialize(... preinitializeFlags=0x1)
[OVRPlugin][INFO]  Support Non-Oculus runtime: NO
[OVRPlugin][INFO]  Preinitialize: xrCreateInstance() succeeded
[OVRPlugin][INFO]  OpenXR runtime name: SteamVR/OpenXR, version 2.17.8
[OVRPlugin][ERROR] Non-Oculus OpenXR runtime is not supported. (CompositorOpenXR.cpp:3433)
[OVRPlugin][ERROR] Plugin failed to initialize.               (CompositorOpenXR.cpp:1922)
[OVRPlugin][ERROR] Unable to create compositor: -1006
```

Read the order carefully. OVRPlugin **successfully created an OpenXR instance on SteamVR**, negotiated the proc-address hook, read the runtime name and version, and enumerated extensions. Everything technical worked. Then it looked at the name and quit.

Note `Support Non-Oculus runtime: **NO**`. That line only exists because there is a **YES** — Meta wrote a non-Oculus path and shipped it switched off. It is bit 3 of `preinitializeFlags`, and the caller hard-codes the value as 1:

```asm
180099E93:  xor  edx,edx
180099E95:  xor  ecx,ecx
180099E97:  lea  r8d,[rdx+1]      ; 44 8D 42 01   preinitializeFlags = 1
180099E9B:  call 1800C2CD0        ; CompositorOpenXR::PreInitialize
```

**Fix:** one byte, `0x01` → `0x09`, at file offset `0x9929A`. That sets bit 3 and the log becomes `Support Non-Oculus runtime: YES`. This is not forcing past a guard — it turns on Meta's own code path.

After that, initialisation completes normally:

```
Support Non-Oculus runtime: YES
Preinitialize: xrGetSystem() succeeded. xrSystemId 503
OVRPlugin 1.94.0 ... preinitialized
ovrp_UnityOpenXR_OnSessionCreate -> OnSessionBegin -> OnSessionStateChange(4, 5)
```

The only remaining errors are cosmetic: `XR_OCULUS_audio_device_guid not activated`, repeating per frame. That is Meta trying to auto-select your headset's audio device through an Oculus-only extension. Windows' default output is used instead.

### 3. Hand tracking the headset does not have

Broken Spectre is built around hand tracking. `HandTrackingController` and `SimpleGestureDetector` gate on real finger joints:

```
IsTrackingGood()   -> ovrHand.HandConfidence
IsPinching(finger) -> ovrHand.GetFingerIsPinching(finger)
IsHandFacingUp()   -> ovrHand.transform.up · Vector3.down
CheckGrasping()    -> IndexProximal/MiddleProximal/... localRotation.eulerAngles.z in 200-320°
```

`CheckGrasping` reads **finger bone rotations**. No controller input can ever satisfy that, which is why the tutorial gesture looks correct on screen — the hand visual is driven by `syntheticControllerHand` — while nothing registers.

**Fix:** those checks are answered from the controllers instead, per-hand via the component's own `handSide` field.

One subtlety worth recording, because it cost a lot of debugging. `SimpleGestureDetector.Update` does this:

```
call CheckGrasping()
pop                      ; return value DISCARDED
ldfld isGrasping         ; reads the FIELD instead
```

`CheckGrasping()` sets the `isGrasping` **field** as a side effect and the caller throws the return value away. A Harmony prefix that only writes `__result` is invisible to the caller. The patch has to write the field.

### 4. A controller button that cannot physically arrive

`OculusAssociation.CheckForOculusMenuGesture` opens the in-game menu and accepts three inputs:

| Call | Meaning |
|---|---|
| `GetDown(Button 256, Controller 96)` | `Start` on `Hands` — hand-tracking system gesture |
| `GetDown(Button 1, Controller 64)` | `One` on `RHand` — hand pinch |
| `GetDown(Button 256, Controller 3)` | `Start` on `Touch` — **the controller menu button** |

The controller path exists and was meant to work. `Button.Start` resolves through OVRPlugin to the action `left_hand_menu_click`, which SteamVR auto-binds to `/user/hand/left/input/menu`.

The HP Reverb G2 controller does not have that input. Its driver profile declares exactly:

```
/input/a  /input/b  /input/x  /input/y
/input/application_menu
/input/emulated_trackpad  /input/grip  /input/joystick  /input/trigger
```

No `menu`, no `system`. SteamVR's auto-remap from `oculus_touch` bound the game's two menu actions to `/user/hand/left/input/menu` and `/user/hand/right/input/system` — **both paths the device lacks**. The only menu-class button is `application_menu`, the Windows button, which SteamVR reserves for its dashboard.

So the one button the tutorial needs is the one that cannot be delivered.

**Fix:** `OVRInput.Get/GetDown/GetUp(Button, Controller)` are patched so that a request for `Button.Start` that returns false is answered from **Y or B**. Reads go through the unpatched `RawButton` overload with a `[ThreadStatic]` reentrancy guard.

> The game's own `ControllerInputManager` / `OculusInputHandlerSO` `GetMenuButton*` wrappers are **dead code** — nothing in the assembly calls them. Patching those does nothing. `OculusAssociation` polls `OVRInput` directly.

---

## Download

Two archives, same fix — pick one:

| Archive | Contains | For |
|---|---|---|
| `BrokenSpectrePCVRFix-<ver>.zip` | Plugin + patcher + docs | You install BepInEx yourself |
| `BrokenSpectrePCVRFix-<ver>-with-BepInEx.zip` | The above, plus BepInEx 5.4.23.5 unmodified, laid out ready to drop in | Extract, run the patcher, play |

The bundle ships all four third-party licenses (see `THIRD-PARTY.md` inside it). If you would rather
obtain BepInEx from its own source, take the plain archive — it contains no third-party code.

Neither archive contains any Meta or Stitch Media file.

---
## Install

### 1. BepInEx

*Skip this step if you downloaded the `-with-BepInEx` archive — it is already included.*

Otherwise, get it from the source so you know what you are running:

> [BepInEx 5.4.23.5 — `BepInEx_win_x64_5.4.23.5.zip`](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5)

Must be the **win_x64** build (the game is 64-bit Mono). Extract into the game folder so `winhttp.dll` sits beside `BrokenSpectre.exe`:

```
Steam\steamapps\common\Broken Spectre\
├── BrokenSpectre.exe
├── winhttp.dll
├── doorstop_config.ini
└── BepInEx\
```

Run the game once to generate `BepInEx\plugins\`, then quit.

### 2. Patch OVRPlugin.dll

```powershell
.\tools\Patch-OVRPlugin.ps1
```

Auto-detects the default Steam library; use `-GamePath "D:\SteamLibrary\steamapps\common\Broken Spectre"` otherwise. It verifies the instruction bytes and SHA-256 before writing, backs up to `OVRPlugin.dll.orig`, and refuses to touch anything it does not recognise.

To undo: `.\tools\Patch-OVRPlugin.ps1 -Revert`

### 3. The plugin

Drop `BrokenSpectrePCVRFix.dll` into `Broken Spectre\BepInEx\plugins\`, or build it yourself (below).

### 4. Play

At the startup prompt choose **"Continue with controllers"**. In the tutorial, **grip** is grasp, **trigger** is pinch, and **Y or B** opens the menu.

---

## Build

No .NET SDK needed. Requires **Visual Studio Build Tools 2022** (for the Roslyn compiler) and the game installed.

```powershell
.\tools\Build.ps1 -Install
```

Output lands in `build\BrokenSpectrePCVRFix.dll`.

The plugin compiles against the game's **own** managed assemblies with `-nostdlib+`, because Unity targets netstandard 2.1 and the desktop .NET Framework reference set alone fails with `CS0012: The type 'Object' is defined in an assembly that is not referenced`. That is the reason for the unusual reference list; it is not incidental.

---

## Configuration

`BepInEx\config\dave.brokenspectre.metagate.cfg`, generated on first run:

| Section | Key | Default | Purpose |
|---|---|---|---|
| Runtime | `BypassMetaGate` | `true` | The `MetaXRFeature` transpiler |
| HandTracking | `EmulateHandTracking` | `true` | Answer hand-tracking checks from controllers |
| HandTracking | `PinchInput` | `IndexTrigger` | `IndexTrigger`, `HandTrigger`, or `Either` |
| HandTracking | `GraspInput` | `HandTrigger` | Same options, for the tutorial grasp |
| HandTracking | `FacingUpAlwaysTrue` | `true` | Report palm-up regardless of controller orientation |
| MenuButton | `Enabled` | `true` | Answer `Button.Start` from Y/B |
| Diagnostics | `LogCalibrationState` | `false` | Scene changes, on-screen text, gesture state |

Turn diagnostics on if you get stuck somewhere; the log names the scene, the visible UI text and the live gesture values, which is how the remaining gates were found.

---

## Known limitations

- **Real hand tracking is not restored.** The Reverb G2 has no hand-tracking hardware; OVRPlugin queries and gets nothing back. Sections designed around finger tracking are driven by controller approximations. The Vive `XR_APILAYER_VIVE_hand_tracking` layer cannot substitute — it loads `cosmos_camera.dll` and needs Vive hardware on your head.
- **`FacingUpAlwaysTrue` is blunt.** It reports palm-up unconditionally, which can make edge-triggered palm-direction events fire earlier than intended.
- **Steam updates overwrite `OVRPlugin.dll`.** Re-run the patcher afterwards. "Verify integrity of game files" also reverts it — which doubles as a clean uninstall.
- Only the tutorial and early game were tested end to end. Later hand-tracking-specific sequences may need more work.

---

## Uninstall

1. `.\tools\Patch-OVRPlugin.ps1 -Revert` (or verify integrity in Steam)
2. Delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` and the `BepInEx` folder

Nothing else in the install is touched.

---

## Legal

Apache 2.0 — see [LICENSE](LICENSE) and [NOTICE](NOTICE).

This repository contains **no third-party binaries**. No Meta or Stitch Media files are redistributed. The OVRPlugin patcher edits one byte of the copy already in your own install and can revert it. The plugin is compiled against your own game files.

There is no anti-cheat in Broken Spectre, no VAC involvement (it is single-player), and no Steam DRM wrapper — the executable runs directly. Modding it carries no ban risk. The worst case is Steam restoring the original file on update.

Unofficial and unaffiliated. Not endorsed by Stitch Media, Meta Platforms, Valve, or HP.

---

## Thanks

- **[BepInEx](https://github.com/BepInEx/BepInEx)** — the Unity mod loader that makes all of this possible, and whose Doorstop injection kept working even when the game destroyed the plugin's host object mid-session.
- **[HarmonyX](https://github.com/BepInEx/HarmonyX)** — runtime patching. Every managed fix here is a Harmony prefix, postfix or transpiler.
- **[Mono.Cecil](https://github.com/jbevain/cecil)** — used to read the game's IL. Reading `SimpleGestureDetector.Update` and seeing the `pop` after `CheckGrasping()` was the moment the hand-tracking problem became solvable.
- **Microsoft `dumpbin`** (Visual Studio Build Tools) — disassembling `OVRPlugin.dll` to find the `preinitializeFlags` call site, and the Roslyn compiler for building the plugin.
- **[Khronos OpenXR](https://www.khronos.org/openxr/)** and **SteamVR's OpenXR runtime**, whose diagnostic logging made the failure legible rather than mysterious.
- **[UploadVR](https://www.uploadvr.com/metas-unity-unreal-openxr-sdks-block-other-pc-vr-headsets/)** — for documenting that Meta's PC integrations deliberately block other headsets, which reframed this from "something is broken" to "something is switched off".
- **[Revive](https://github.com/LibreVR/Revive)** — the original inspiration, and the first thing tried. It turned out not to apply here: the game never calls the LibOVR API, so there was nothing for Revive to proxy. Worth stating plainly so the next person does not spend time on it.
- Everyone on the Steam forums who posted *"Cannot go past the menu screen — appears frozen"* and got no answer. This is the answer.
