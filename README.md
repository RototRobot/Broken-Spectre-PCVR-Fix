<div align="center">

# Broken Spectre PCVR Fix

**Makes the Steam build of [Broken Spectre](https://store.steampowered.com/app/1642060/) run on non-Meta PC VR headsets.**

![License](https://img.shields.io/badge/license-Apache--2.0-blue)
![Tested](https://img.shields.io/badge/tested-HP%20Reverb%20G2%20%2B%20SteamVR-brightgreen)
![BepInEx](https://img.shields.io/badge/BepInEx-5.4.23.5-orange)
![Platform](https://img.shields.io/badge/platform-Windows%20x64-lightgrey)

</div>

The Steam store page says it plainly: *"This game has only been tested for Meta Quest VR Headsets"*,
with VR support listed as **Oculus devices only**. On anything else the game launches, renders a
frame, and then locks up — the view welded to your face, nothing responding.

That is not a bug. It is three deliberate vendor gates stacked on top of each other, plus one
genuine hardware gap. This fixes all four.

---

## Contents

- [Disclosure](#disclosure)
- [Symptoms this fixes](#symptoms-this-fixes)
- [Download](#download)
- [Install](#install)
- [What was actually wrong](#what-was-actually-wrong)
- [Build from source](#build-from-source)
- [Configuration](#configuration)
- [Known limitations](#known-limitations)
- [Uninstall](#uninstall)
- [Legal](#legal)
- [Thanks](#thanks)

---

## Disclosure

> [!NOTE]
> This mod was made with heavy AI (Claude) assistance. I want to be upfront about that.

The reverse engineering, patch design and tooling came out of a long back-and-forth session:
disassembling `OVRPlugin.dll` with `dumpbin`, reading the game's IL with `Mono.Cecil`, and —
crucially — instrumenting the running game rather than guessing. Several early attempts patched the
*wrong* components. The write-up below documents what turned out to be true, including the dead
ends, because that is the part most likely to help the next person.

Everything here was verified against a real headset on real hardware. No log line, offset or byte
sequence in this README is inferred; all of it came from actual runs.

---

## Symptoms this fixes

| What you see | Actual cause |
| --- | --- |
| View head-locked, nothing responds, stuck at language select | `MetaXRFeature` disables itself → `OVRPlugin` never initialises → `OVRCameraRig`/`OVRInput` are dead |
| Game crashes instantly on launch after a partial fix | Managed gate opened but native `OVRPlugin` still refused; Unity tears down a half-initialised feature |
| Tutorial "palm up + pinch" gesture never completes | Gesture logic reads `OVRHand` finger-bone data your headset cannot produce |
| Menu button does nothing; Windows button opens the SteamVR dashboard | The game asks for a button path that does not exist on your controller |

---

## Download

Two archives, same fix.

| Archive | Contains | For |
| --- | --- | --- |
| **`BrokenSpectrePCVRFix-1.2.0-with-BepInEx.zip`** | Plugin, patcher, docs **+ BepInEx 5.4.23.5 unmodified**, pre-laid-out | Extract, run patcher, play |
| **`BrokenSpectrePCVRFix-1.2.0.zip`** | Plugin, patcher, docs only | You install BepInEx yourself |

> [!IMPORTANT]
> **Both install steps are required** — the plugin *and* the `OVRPlugin.dll` patch.
> Installing only the plugin makes the game **crash on launch**: it opens a gate that the unpatched
> native library then refuses, leaving Unity to tear down a half-initialised feature.

> [!TIP]
> The bundle ships all four third-party licenses (`THIRD-PARTY.md` inside it). If you would rather
> obtain BepInEx from its own source, take the plain archive — it contains no third-party code.

Neither archive contains any Meta or Stitch Media file. See [Legal](#legal).

**Verified on:** HP Reverb G2 (`hpmotioncontroller`) + SteamVR 2.17 OpenXR, Windows 11.
**Should also work on:** Valve Index, HTC Vive, other WMR headsets — any SteamVR/OpenXR runtime.
Untested; reports welcome.

---

## Install

<details open>
<summary><b>Option A — bundled archive (recommended)</b></summary>

<br>

**1. Extract into the game folder**

```
Steam\steamapps\common\Broken Spectre\
├── BrokenSpectre.exe          <- already there
├── winhttp.dll                <- from the archive
├── doorstop_config.ini        <- from the archive
├── .doorstop_version          <- from the archive
└── BepInEx\
    ├── core\                  <- from the archive
    └── plugins\
        └── BrokenSpectrePCVRFix.dll
```

**2. Patch `OVRPlugin.dll`**

Right-click `Patch-OVRPlugin.ps1` → **Run with PowerShell**, or:

```powershell
.\Patch-OVRPlugin.ps1
```

</details>

<details>
<summary><b>Option B — plugin only (bring your own BepInEx)</b></summary>

<br>

**1. Install BepInEx**

Download [`BepInEx_win_x64_5.4.23.5.zip`](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5).

> [!WARNING]
> It must be the **win_x64** build. The game is 64-bit Mono; the x86 build will not load.

Extract into the game folder so `winhttp.dll` sits beside `BrokenSpectre.exe`. Launch the game once
and quit — this creates `BepInEx\plugins\`.

**2. Copy the plugin**

Put `BrokenSpectrePCVRFix.dll` into `Broken Spectre\BepInEx\plugins\`.

**3. Patch `OVRPlugin.dll`**

```powershell
.\Patch-OVRPlugin.ps1
```

</details>

<br>

If your game is on another drive:

```powershell
.\Patch-OVRPlugin.ps1 -GamePath "D:\SteamLibrary\steamapps\common\Broken Spectre"
```

Expected output:

```
Bytes  : 44 8D 42 01  (expect 44 8D 42 01 unpatched, 44 8D 42 09 patched)
Wrote  : 0x01 -> 0x09 at offset 0x9929A
Verified against known-good patched hash.
Done. Patched.
```

The patcher verifies the instruction bytes **and** SHA-256 before writing, backs up to
`OVRPlugin.dll.orig`, and refuses to touch a build it does not recognise. Undo with `-Revert`.

### Playing

At the startup prompt, choose **"Continue with controllers"**.

| Input | Action |
| --- | --- |
| **Grip** | Grasp (tutorial gesture) |
| **Trigger** | Pinch |
| **Y** or **B** | Menu button |

> [!NOTE]
> The Windows button on WMR/Reverb controllers will always open the SteamVR dashboard. SteamVR
> reserves it at the driver level — the game can never see it, and no mod can change that.

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

SteamVR advertises 44 extensions; `XR_META_headset_id` is not among them, and it is not named
"meta" or "oculus". So the feature switches itself off, `OVRPlugin` never gets an XR session, and
every `OVRCameraRig` / `OVRInput` call downstream returns nothing.

**Fix** — a Harmony transpiler flips `ldc.i4.0` at `IL_0000` to `ldc.i4.1`, seeding the local
"extension found" flag true. One opcode, scoped to that single method.

### 2. The native gate — `OVRPlugin.dll`

Opening the managed gate alone makes the game **crash on launch**. `OVRPlugin` has its own check,
and it is explicit about it:

```
[OVRPlugin][INFO]  CompositorOpenXR::PreInitialize(... preinitializeFlags=0x1)
[OVRPlugin][INFO]  Support Non-Oculus runtime: NO
[OVRPlugin][INFO]  Preinitialize: xrCreateInstance() succeeded
[OVRPlugin][INFO]  OpenXR runtime name: SteamVR/OpenXR, version 2.17.8
[OVRPlugin][ERROR] Non-Oculus OpenXR runtime is not supported. (CompositorOpenXR.cpp:3433)
[OVRPlugin][ERROR] Plugin failed to initialize.               (CompositorOpenXR.cpp:1922)
[OVRPlugin][ERROR] Unable to create compositor: -1006
```

Read the order carefully. OVRPlugin **successfully created an OpenXR instance on SteamVR**,
negotiated the proc-address hook, read the runtime name and version, and enumerated extensions.
Everything technical worked. Then it looked at the name and quit.

> [!IMPORTANT]
> `Support Non-Oculus runtime: NO` only exists because there is a **YES**. Meta wrote a non-Oculus
> code path and shipped it switched off. This patch turns on their own path — it is not forcing
> past a guard.

It is bit 3 of `preinitializeFlags`, and the caller hard-codes the value as 1:

```asm
180099E93:  xor  edx,edx
180099E95:  xor  ecx,ecx
180099E97:  lea  r8d,[rdx+1]      ; 44 8D 42 01   preinitializeFlags = 1
180099E9B:  call 1800C2CD0        ; CompositorOpenXR::PreInitialize
```

**Fix** — one byte, `0x01` → `0x09`, at file offset `0x9929A`.

<details>
<summary>Initialisation after the patch</summary>

<br>

```
Support Non-Oculus runtime: YES
Preinitialize: xrCreateInstance() succeeded
Preinitialize: xrGetSystem() succeeded. xrSystemId 503
OVRPlugin 1.94.0 ... preinitialized
ovrp_UnityOpenXR_OnSessionCreate -> OnSessionBegin -> OnSessionStateChange(4, 5)
```

The only remaining errors are cosmetic: `XR_OCULUS_audio_device_guid not activated`, repeating per
frame. That is Meta trying to auto-select your headset's audio device through an Oculus-only
extension. Windows' default output is used instead.

</details>

### 3. Hand tracking the headset does not have

Broken Spectre is built around hand tracking. `HandTrackingController` and `SimpleGestureDetector`
gate on real finger joints:

```
IsTrackingGood()   -> ovrHand.HandConfidence
IsPinching(finger) -> ovrHand.GetFingerIsPinching(finger)
IsHandFacingUp()   -> ovrHand.transform.up · Vector3.down
CheckGrasping()    -> IndexProximal/MiddleProximal/... localRotation.eulerAngles.z in 200-320°
```

`CheckGrasping` reads **finger bone rotations**. No controller input can ever satisfy that, which is
why the tutorial gesture *looks* correct on screen — the hand visual is driven by
`syntheticControllerHand` — while nothing registers.

**Fix** — those checks are answered from the controllers instead, per-hand via the component's own
`handSide` field.

> [!CAUTION]
> One subtlety cost a lot of debugging. `SimpleGestureDetector.Update` does this:
>
> ```
> call CheckGrasping()
> pop                      ; return value DISCARDED
> ldfld isGrasping         ; reads the FIELD instead
> ```
>
> `CheckGrasping()` sets the `isGrasping` **field** as a side effect and the caller throws the
> return value away. A Harmony prefix that only writes `__result` is invisible to the caller — the
> patch has to write the field.

### 4. A controller button that cannot physically arrive

`OculusAssociation.CheckForOculusMenuGesture` opens the in-game menu and accepts three inputs:

| Call | Meaning |
| --- | --- |
| `GetDown(Button 256, Controller 96)` | `Start` on `Hands` — hand-tracking system gesture |
| `GetDown(Button 1, Controller 64)` | `One` on `RHand` — hand pinch |
| `GetDown(Button 256, Controller 3)` | `Start` on `Touch` — **the controller menu button** |

The controller path exists and was meant to work. `Button.Start` resolves through OVRPlugin to the
action `left_hand_menu_click`, which SteamVR auto-binds to `/user/hand/left/input/menu`.

The HP Reverb G2 controller does not have that input. Its driver profile declares exactly:

```
/input/a  /input/b  /input/x  /input/y
/input/application_menu
/input/emulated_trackpad  /input/grip  /input/joystick  /input/trigger
```

No `menu`, no `system`. SteamVR's auto-remap from `oculus_touch` bound the game's two menu actions
to `/user/hand/left/input/menu` and `/user/hand/right/input/system` — **both paths the device
lacks**. The only menu-class button is `application_menu`, the Windows button, which SteamVR
reserves for its dashboard.

So the one button the tutorial needs is the one that cannot be delivered.

**Fix** — `OVRInput.Get/GetDown/GetUp(Button, Controller)` are patched so a request for
`Button.Start` that returns false is answered from **Y or B**. Reads go through the unpatched
`RawButton` overload with a `[ThreadStatic]` reentrancy guard.

> [!NOTE]
> The game's own `ControllerInputManager` / `OculusInputHandlerSO` `GetMenuButton*` wrappers are
> **dead code** — nothing in the assembly calls them. Patching those does nothing; `OculusAssociation`
> polls `OVRInput` directly. This was one of the wrong turns mentioned in the disclosure.

---

## Build from source

> [!TIP]
> No .NET SDK required. Uses the Roslyn compiler from Visual Studio Build Tools 2022.

```powershell
.\tools\Build.ps1 -Install
```

Output lands in `build\BrokenSpectrePCVRFix.dll`.

> [!IMPORTANT]
> The plugin compiles against the game's **own** managed assemblies with `-nostdlib+`, because Unity
> targets netstandard 2.1 and the desktop .NET Framework reference set alone fails with
> `CS0012: The type 'Object' is defined in an assembly that is not referenced`. That unusual
> reference list is deliberate, not incidental.

To rebuild both release archives:

```powershell
.\tools\Make-Release.ps1 -Version 1.2.0
```

BepInEx is not stored in this repository — point `-BepInExDir` at an extracted copy of the official
release. The script validates it before using it.

---

## Configuration

`BepInEx\config\dave.brokenspectre.metagate.cfg`, generated on first run.

| Section | Key | Default | Purpose |
| --- | --- | --- | --- |
| Runtime | `BypassMetaGate` | `true` | The `MetaXRFeature` transpiler |
| HandTracking | `EmulateHandTracking` | `true` | Answer hand-tracking checks from controllers |
| HandTracking | `PinchInput` | `IndexTrigger` | `IndexTrigger`, `HandTrigger`, or `Either` |
| HandTracking | `GraspInput` | `HandTrigger` | Same options, for the tutorial grasp |
| HandTracking | `FacingUpAlwaysTrue` | `true` | Report palm-up regardless of controller orientation |
| MenuButton | `Enabled` | `true` | Answer `Button.Start` from Y/B |
| Diagnostics | `LogCalibrationState` | `false` | Scene changes, on-screen text, gesture state |

> [!TIP]
> Stuck somewhere? Set `LogCalibrationState = true`, reproduce, then attach `BepInEx\LogOutput.log`
> to an issue. It records the active scene, every visible UI string and live gesture values — which
> is exactly how the remaining gates were found.

---

## Known limitations

> [!CAUTION]
> - **Real hand tracking is not restored.** The Reverb G2 has no hand-tracking hardware; OVRPlugin
>   queries and gets nothing back. Sections designed around finger tracking run on controller
>   approximations.
> - **`FacingUpAlwaysTrue` is blunt.** It reports palm-up unconditionally, which can make
>   edge-triggered palm-direction events fire earlier than intended.
> - **Only the tutorial and early game were tested end to end.** Later hand-tracking-specific
>   sequences may need more work.

> [!WARNING]
> A Steam game update will overwrite `OVRPlugin.dll`. Re-run the patcher afterwards.
> *Verify integrity of game files* also reverts it — which doubles as a clean uninstall.

The Vive `XR_APILAYER_VIVE_hand_tracking` layer **cannot** substitute for hand tracking here — it
loads `cosmos_camera.dll` and requires Vive hardware on your head.

---

## Uninstall

1. `.\Patch-OVRPlugin.ps1 -Revert` — or Steam → *Verify integrity of game files*
2. Delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` and the `BepInEx` folder

Nothing else in your install is touched.

---

## Legal

Apache 2.0 — see [LICENSE](LICENSE) and [NOTICE](NOTICE).

This repository contains **no third-party binaries**. No Meta or Stitch Media files are
redistributed. The patcher edits one byte of the copy already in your own install and can revert it.
The plugin is compiled against your own game files. The optional `-with-BepInEx` archive
redistributes BepInEx unmodified, with all four third-party license texts and a `THIRD-PARTY.md`
manifest.

> [!NOTE]
> **There is no ban risk.** Broken Spectre has no anti-cheat, no VAC involvement (it is
> single-player), and no Steam DRM wrapper — the executable runs directly. The worst case is Steam
> restoring the original file on update.

Unofficial and unaffiliated. Not endorsed by Stitch Media, Meta Platforms, Valve, or HP.

---

## Thanks

- **[BepInEx](https://github.com/BepInEx/BepInEx)** — the Unity mod loader that makes all of this
  possible, and whose Doorstop injection kept working even when the game destroyed the plugin's host
  object mid-session.
- **[HarmonyX](https://github.com/BepInEx/HarmonyX)** — runtime patching. Every managed fix here is
  a Harmony prefix, postfix or transpiler.
- **[Mono.Cecil](https://github.com/jbevain/cecil)** — used to read the game's IL. Seeing the `pop`
  after `CheckGrasping()` was the moment the hand-tracking problem became solvable.
- **Microsoft `dumpbin`** (Visual Studio Build Tools) — disassembling `OVRPlugin.dll` to find the
  `preinitializeFlags` call site, and the Roslyn compiler for building the plugin.
- **[Khronos OpenXR](https://www.khronos.org/openxr/)** and **SteamVR's OpenXR runtime**, whose
  diagnostic logging made the failure legible rather than mysterious.
- **[UploadVR](https://www.uploadvr.com/metas-unity-unreal-openxr-sdks-block-other-pc-vr-headsets/)**
  — for documenting that Meta's PC integrations deliberately block other headsets, which reframed
  this from "something is broken" to "something is switched off".
- **[Revive](https://github.com/LibreVR/Revive)** — the original inspiration, and the first thing
  tried. It turned out not to apply: the game never calls the LibOVR API, so there was nothing for
  Revive to proxy. Stated plainly so the next person does not spend time on it.
- Everyone on the Steam forums who posted *"Cannot go past the menu screen — appears frozen"* and
  got no answer. This is the answer.
