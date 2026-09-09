using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace BrokenSpectreMetaGate
{
    // Broken Spectre PCVR Fix - lets the Steam build run on non-Meta OpenXR runtimes.
    // Verified on SteamVR 2.17 + HP Reverb G2 (hpmotioncontroller).
    //
    // The game is Unity 2022.3.10 (Mono) + Meta XR SDK Core v62. Three separate gates stop it:
    //
    // 1. RUNTIME. Meta.XR.MetaXRFeature.OnInstanceCreate returns false unless the OpenXR runtime
    //    advertises XR_META_headset_id or is named meta/oculus. A transpiler seeds its local
    //    "extension found" flag true. This alone is NOT enough - it must be paired with the
    //    OVRPlugin.dll byte patch (tools/Patch-OVRPlugin.ps1), or native init fails and Unity
    //    hard-crashes in OpenXRLoaderBase.CreateSubsystems.
    //
    // 2. HAND TRACKING. The G2 has no hand tracking, so OVRHand never reports confident data.
    //    HandTrackingController and SimpleGestureDetector gate on it while the hand visual is
    //    driven from the controller - the pose looks right but nothing registers. Answered from
    //    the controllers instead.
    //
    //    Note SimpleGestureDetector.Update discards CheckGrasping return value and reads the
    //    isGrasping FIELD, which the original sets as a side effect. The patch must write that
    //    field, not just __result.
    //
    // 3. MENU BUTTON. OculusAssociation.CheckForOculusMenuGesture opens the in-game menu via
    //    OVRInput.GetDown(Button.Start, Controller.Touch). Button.Start resolves to OVRPlugin
    //    left_hand_menu_click, which SteamVR auto-binds to /user/hand/left/input/menu - a path
    //    hpmotioncontroller does not have (it has application_menu, reserved by the dashboard).
    //    So OVRInput itself is patched to answer Start from Y/B.
    //
    //    The game ControllerInputManager/OculusInputHandlerSO GetMenuButton wrappers are dead
    //    code - nothing calls them. PatchMenuButton is kept only because it is harmless and
    //    documents that dead end.
    [BepInPlugin(Guid, "Broken Spectre PCVR Fix", "1.2.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "dave.brokenspectre.metagate";
        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> BypassMetaGate;
        internal static ConfigEntry<bool> EmulateHandTracking;
        internal static ConfigEntry<string> PinchInput;
        internal static ConfigEntry<string> GraspInput;
        internal static ConfigEntry<bool> FacingUpAlwaysTrue;
        internal static ConfigEntry<bool> MenuFallbackEnabled;
        internal static ConfigEntry<string> MenuFallbackButton;
        internal static ConfigEntry<bool> Diagnostics;

        private static FieldInfo _handSideField;

        // diagnostics reflection
        private static FieldInfo _calInstance, _calStage, _calTimer, _calHandsInPos, _calViewedPreamble,
                                 _calLeftTracker, _calRightTracker, _calLeftPoint, _calRightPoint;
        private static FieldInfo _htHandObject, _htPalmDir, _htHand;
        private static bool _diagReady;

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            BypassMetaGate = Config.Bind("1. Runtime", "BypassMetaGate", true,
                "Force MetaXRFeature to initialise on non-Meta OpenXR runtimes.");
            EmulateHandTracking = Config.Bind("2. HandTracking", "EmulateHandTracking", true,
                "Answer HandTrackingController from the controllers, so gesture gates can complete without real hand tracking.");
            PinchInput = Config.Bind("2. HandTracking", "PinchInput", "IndexTrigger",
                "What counts as a pinch: IndexTrigger, HandTrigger (grip), or Either.");
            GraspInput = Config.Bind("2. HandTracking", "GraspInput", "HandTrigger",
                "What counts as a grasp for the tutorial gesture: IndexTrigger, HandTrigger (grip), or Either.");
            FacingUpAlwaysTrue = Config.Bind("2. HandTracking", "FacingUpAlwaysTrue", true,
                "Report the palm as facing up regardless of controller orientation (the check needs hand skeleton data).");
            MenuFallbackEnabled = Config.Bind("3. MenuButton", "Enabled", true,
                "Also accept a spare button as the menu button, since SteamVR binds the real one to a path the G2 lacks.");
            MenuFallbackButton = Config.Bind("3. MenuButton", "Button", "Four",
                "OVRInput.Button name used as the menu fallback (Four = Y/B, Three = X/A, PrimaryThumbstick, ...).");
            Diagnostics = Config.Bind("4. Diagnostics", "LogCalibrationState", false,
                "Log scene changes, on-screen text and gesture state. Only needed for troubleshooting.");

            SetupDiagnostics();

            // The BepInEx host object appears to stop ticking a few seconds in, so run the probe
            // from an independent DontDestroyOnLoad object and also hook the static scene events,
            // which keep firing regardless of what happens to any particular GameObject.
            if (Diagnostics.Value)
            {
                UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
                UnityEngine.SceneManagement.SceneManager.sceneUnloaded += OnSceneUnloaded;
                UnityEngine.SceneManagement.SceneManager.activeSceneChanged += OnActiveSceneChanged;

                var host = new UnityEngine.GameObject("BSMetaGateProbe");
                UnityEngine.Object.DontDestroyOnLoad(host);
                host.hideFlags = UnityEngine.HideFlags.HideAndDontSave;
                host.AddComponent<Probe>();
                Logger.LogInfo("Probe host created.");
            }

            var harmony = new Harmony(Guid);

            if (BypassMetaGate.Value) PatchMetaGate(harmony);
            if (EmulateHandTracking.Value) { PatchHandTracking(harmony); PatchGestureDetector(harmony); }
            if (MenuFallbackEnabled.Value) { PatchOVRInputMenu(harmony); PatchMenuButton(harmony); }
        }

        // ---------- 1. runtime gate ----------

        private void PatchMetaGate(Harmony harmony)
        {
            Type t = AccessTools.TypeByName("Meta.XR.MetaXRFeature");
            MethodBase target = t == null ? null : AccessTools.Method(t, "OnInstanceCreate", new[] { typeof(ulong) });
            if (target == null) { Logger.LogError("MetaXRFeature.OnInstanceCreate not found - runtime gate NOT bypassed."); return; }

            Try("MetaXRFeature.OnInstanceCreate", () => harmony.Patch(target, null, null,
                new HarmonyMethod(typeof(Plugin).GetMethod(nameof(GateTranspiler), BindingFlags.Static | BindingFlags.NonPublic))));
        }

        private static IEnumerable<CodeInstruction> GateTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            if (code.Count >= 2 && code[0].opcode == OpCodes.Ldc_I4_0 && code[1].opcode == OpCodes.Stloc_0)
            {
                code[0].opcode = OpCodes.Ldc_I4_1;
                Log.LogInfo("Runtime gate bypassed.");
            }
            else Log.LogError("Unexpected IL prologue - runtime gate NOT bypassed.");
            return code;
        }

        // ---------- 2. hand tracking emulation ----------

        private void PatchHandTracking(Harmony harmony)
        {
            Type t = AccessTools.TypeByName("BrokenSpectre.HandTrackingController");
            if (t == null) { Logger.LogError("HandTrackingController not found - hand emulation off."); return; }

            _handSideField = AccessTools.Field(t, "handSide");

            var alwaysTrue = new HarmonyMethod(typeof(Plugin).GetMethod(nameof(AlwaysTrue), BindingFlags.Static | BindingFlags.NonPublic));
            var pinch = new HarmonyMethod(typeof(Plugin).GetMethod(nameof(PinchPrefix), BindingFlags.Static | BindingFlags.NonPublic));
            var notPinch = new HarmonyMethod(typeof(Plugin).GetMethod(nameof(NotPinchPrefix), BindingFlags.Static | BindingFlags.NonPublic));

            var conf = AccessTools.PropertyGetter(t, "HighConfidence");
            if (conf != null) Try("HandTrackingController.HighConfidence", () => harmony.Patch(conf, alwaysTrue));

            var fingerConf = AccessTools.Method(t, "IsFingerDataHighConfident");
            if (fingerConf != null) Try("HandTrackingController.IsFingerDataHighConfident", () => harmony.Patch(fingerConf, alwaysTrue));

            var checkPinch = AccessTools.Method(t, "CheckPinch");
            if (checkPinch != null) Try("HandTrackingController.CheckPinch", () => harmony.Patch(checkPinch, pinch));

            var checkNotPinch = AccessTools.Method(t, "CheckNotPinch");
            if (checkNotPinch != null) Try("HandTrackingController.CheckNotPinch", () => harmony.Patch(checkNotPinch, notPinch));

            // SimpleGestureDetector.Update returns early unless the hand is "in zone" and the palm
            // direction is Up (enum value 2). Both derive from OVRHand data we do not have.
            var inZone = AccessTools.PropertyGetter(t, "IsHandInZone");
            if (inZone != null) Try("HandTrackingController.IsHandInZone", () => harmony.Patch(inZone, alwaysTrue));

            _htPalmDirField = AccessTools.Field(t, "palmDirection");
            var calcPalm = AccessTools.Method(t, "CalcPalmDirection");
            if (calcPalm != null && _htPalmDirField != null)
                Try("HandTrackingController.CalcPalmDirection", () => harmony.Patch(calcPalm,
                    new HarmonyMethod(typeof(Plugin).GetMethod(nameof(PalmDirectionPrefix), BindingFlags.Static | BindingFlags.NonPublic))));
        }

        private static bool AlwaysTrue(ref bool __result) { __result = true; return false; }

        private static FieldInfo _htPalmDirField;

        // PalmDirection.Up == 2. Write the field directly so GetPalmDirection returns it normally.
        private static bool PalmDirectionPrefix(object __instance)
        {
            if (!FacingUpAlwaysTrue.Value) return true;
            try { _htPalmDirField.SetValue(__instance, Enum.ToObject(_htPalmDirField.FieldType, 2)); }
            catch (Exception e) { Log.LogWarning("palmDirection set failed: " + e.Message); }
            return false;
        }

        private static bool PinchPrefix(object __instance, ref bool __result)
        {
            __result = IsPinching(__instance);
            return false;
        }

        private static bool NotPinchPrefix(object __instance, ref bool __result)
        {
            __result = !IsPinching(__instance);
            return false;
        }

        // handSide is an OVRHand.Hand: 0 = None, 1 = HandLeft, 2 = HandRight.
        private static bool IsPinching(object instance)
        {
            OVRInput.Controller controller = OVRInput.Controller.LTouch;
            if (_handSideField != null && instance != null)
            {
                try { if (Convert.ToInt32(_handSideField.GetValue(instance)) == 2) controller = OVRInput.Controller.RTouch; }
                catch (Exception e) { Log.LogWarning("handSide read failed: " + e.Message); }
            }

            bool index = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, controller) > 0.5f;
            bool grip = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, controller) > 0.5f;

            switch (PinchInput.Value)
            {
                case "HandTrigger": return grip;
                case "Either": return index || grip;
                default: return index;
            }
        }

        // ---------- 2b. tutorial gesture detector ----------
        //
        // tutorial_v2 gates on SimpleGestureDetector, which reads OVRHand confidence, pinch state
        // and finger-bone rotations. None of that exists without hand tracking, so answer all four
        // from the controllers instead. handSide here is an OVRHand.Hand (1 = left, 2 = right).
        private static FieldInfo _gestureHandSide;
        private static FieldInfo _gestureIsGrasping;

        private void PatchGestureDetector(Harmony harmony)
        {
            Type t = AccessTools.TypeByName("BrokenSpectre.SimpleGestureDetector");
            if (t == null) { Logger.LogError("SimpleGestureDetector not found - gesture emulation off."); return; }

            _gestureHandSide = AccessTools.Field(t, "handSide");
            _gestureIsGrasping = AccessTools.Field(t, "isGrasping");

            var map = new[]
            {
                new[] { "IsTrackingGood", nameof(TruePrefix) },
                new[] { "IsHandFacingUp", nameof(FacingUpPrefix) },
                new[] { "IsPinching", nameof(GesturePinchPrefix) },
                new[] { "CheckGrasping", nameof(GraspPrefix) }
            };

            foreach (var pair in map)
            {
                var m = AccessTools.Method(t, pair[0]);
                if (m == null) { Logger.LogWarning("SimpleGestureDetector." + pair[0] + " not found."); continue; }
                var pre = new HarmonyMethod(typeof(Plugin).GetMethod(pair[1], BindingFlags.Static | BindingFlags.NonPublic));
                var name = pair[0];
                Try("SimpleGestureDetector." + name, () => harmony.Patch(m, pre));
            }
        }

        private static bool TruePrefix(ref bool __result) { __result = true; return false; }

        private static bool FacingUpPrefix(ref bool __result)
        {
            __result = FacingUpAlwaysTrue.Value;
            return false;
        }

        private static bool GesturePinchPrefix(object __instance, ref bool __result)
        {
            __result = ControllerPinch(GestureController(__instance), PinchInput.Value);
            return false;
        }

        // Update discards this method's return value and reads the isGrasping FIELD, which the
        // original sets as a side effect. Writing __result alone is invisible to the caller.
        private static bool GraspPrefix(object __instance, ref bool __result)
        {
            bool grasping = ControllerPinch(GestureController(__instance), GraspInput.Value);
            __result = grasping;
            if (_gestureIsGrasping != null && __instance != null)
            {
                try { _gestureIsGrasping.SetValue(__instance, grasping); }
                catch (Exception e) { Log.LogWarning("isGrasping set failed: " + e.Message); }
            }
            return false;
        }

        private static OVRInput.Controller GestureController(object instance)
        {
            if (_gestureHandSide == null || instance == null) return OVRInput.Controller.LTouch;
            try { return Convert.ToInt32(_gestureHandSide.GetValue(instance)) == 2 ? OVRInput.Controller.RTouch : OVRInput.Controller.LTouch; }
            catch { return OVRInput.Controller.LTouch; }
        }

        private static bool ControllerPinch(OVRInput.Controller c, string mode)
        {
            bool index = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, c) > 0.5f;
            bool grip = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, c) > 0.5f;
            switch (mode)
            {
                case "HandTrigger": return grip;
                case "Either": return index || grip;
                default: return index;
            }
        }

        // ---------- 3. menu button fallback ----------

        // OculusAssociation.CheckForOculusMenuGesture opens the in-game menu via
        //     OVRInput.GetDown(Button.Start, Controller.Touch)
        // Button.Start resolves to OVRPlugin's left_hand_menu_click, which SteamVR binds to
        // /user/hand/left/input/menu - a path hpmotioncontroller does not have, so it can never
        // fire. Patch OVRInput itself and answer Start from a button the G2 actually has.
        // (The game's own GetMenuButton* wrappers are dead code - nothing calls them.)
        private void PatchOVRInputMenu(Harmony harmony)
        {
            var bt = typeof(OVRInput.Button);
            var ct = typeof(OVRInput.Controller);

            foreach (var name in new[] { "Get", "GetDown", "GetUp" })
            {
                var m = AccessTools.Method(typeof(OVRInput), name, new[] { bt, ct });
                if (m == null) { Logger.LogWarning("OVRInput." + name + "(Button,Controller) not found."); continue; }
                var post = new HarmonyMethod(typeof(Plugin).GetMethod("OVRInput" + name + "Postfix", BindingFlags.Static | BindingFlags.NonPublic));
                var captured = name;
                Try("OVRInput." + captured + "(Button,Controller)", () => harmony.Patch(m, null, post));
            }
        }

        [ThreadStatic] private static bool _inFallback;

        private static bool StartFallback(Func<OVRInput.RawButton, bool> probe)
        {
            if (_inFallback) return false;
            _inFallback = true;
            try
            {
                // Read via the RawButton overload, which is not patched, so this cannot recurse.
                return probe(OVRInput.RawButton.Y) || probe(OVRInput.RawButton.B);
            }
            catch (Exception e) { Log.LogWarning("menu fallback: " + e.Message); return false; }
            finally { _inFallback = false; }
        }

        private static void OVRInputGetPostfix(OVRInput.Button virtualMask, ref bool __result)
        {
            if (__result || !MenuFallbackEnabled.Value || virtualMask != OVRInput.Button.Start) return;
            __result = StartFallback(b => OVRInput.Get(b));
        }

        private static void OVRInputGetDownPostfix(OVRInput.Button virtualMask, ref bool __result)
        {
            if (__result || !MenuFallbackEnabled.Value || virtualMask != OVRInput.Button.Start) return;
            __result = StartFallback(b => OVRInput.GetDown(b));
        }

        private static void OVRInputGetUpPostfix(OVRInput.Button virtualMask, ref bool __result)
        {
            if (__result || !MenuFallbackEnabled.Value || virtualMask != OVRInput.Button.Start) return;
            __result = StartFallback(b => OVRInput.GetUp(b));
        }

        private void PatchMenuButton(Harmony harmony)
        {
            Type t = AccessTools.TypeByName("BrokenSpectre.OculusInputHandlerSO");
            if (t == null) { Logger.LogError("OculusInputHandlerSO not found - menu fallback off."); return; }

            var names = new[]
            {
                new[] { "GetMenuButton", nameof(MenuHeldPostfix) },
                new[] { "GetMenuButtonDown", nameof(MenuDownPostfix) },
                new[] { "GetMenuButtonUp", nameof(MenuUpPostfix) }
            };

            foreach (var pair in names)
            {
                var m = AccessTools.Method(t, pair[0]);
                if (m == null) { Logger.LogWarning(pair[0] + " not found."); continue; }
                var post = new HarmonyMethod(typeof(Plugin).GetMethod(pair[1], BindingFlags.Static | BindingFlags.NonPublic));
                var captured = pair[0];
                Try("OculusInputHandlerSO." + captured, () => harmony.Patch(m, null, post));
            }
        }

        private static OVRInput.Button FallbackButton()
        {
            try { return (OVRInput.Button)Enum.Parse(typeof(OVRInput.Button), MenuFallbackButton.Value, true); }
            catch { return OVRInput.Button.Four; }
        }

        private static void MenuHeldPostfix(ref bool __result) { if (!__result) __result = OVRInput.Get(FallbackButton()); }
        private static void MenuDownPostfix(ref bool __result) { if (!__result) __result = OVRInput.GetDown(FallbackButton()); }
        private static void MenuUpPostfix(ref bool __result) { if (!__result) __result = OVRInput.GetUp(FallbackButton()); }

        // ---------- 4. diagnostics ----------

        private void SetupDiagnostics()
        {
            Type cal = AccessTools.TypeByName("BrokenSpectre.CalibrationStation");
            Type ht = AccessTools.TypeByName("BrokenSpectre.HandTrackingController");
            if (cal == null || ht == null) { Logger.LogWarning("Diagnostics: types not found."); return; }

            _calInstance = AccessTools.Field(cal, "instance");
            _calStage = AccessTools.Field(cal, "currentStage");
            _calTimer = AccessTools.Field(cal, "timer");
            _calHandsInPos = AccessTools.Field(cal, "handsInPosition");
            _calViewedPreamble = AccessTools.Field(cal, "viewedPreamble");
            _calLeftTracker = AccessTools.Field(cal, "leftHandTracker");
            _calRightTracker = AccessTools.Field(cal, "rightHandTracker");
            _calLeftPoint = AccessTools.Field(cal, "leftStationHandPoint");
            _calRightPoint = AccessTools.Field(cal, "rightStationHandPoint");

            _htHandObject = AccessTools.Field(ht, "handObject");
            _htPalmDir = AccessTools.Field(ht, "palmDirection");
            _htHand = AccessTools.Field(ht, "hand");

            _diagReady = _calInstance != null && _calStage != null;
            Logger.LogInfo("Diagnostics ready: " + _diagReady);
        }

        private void OnDestroy() { if (Log != null) Log.LogWarning("Plugin MonoBehaviour was DESTROYED."); }
        private void OnDisable() { if (Log != null) Log.LogWarning("Plugin MonoBehaviour was DISABLED."); }

        private static void OnSceneLoaded(UnityEngine.SceneManagement.Scene s, UnityEngine.SceneManagement.LoadSceneMode m)
        { Log.LogInfo("SCENE LOADED: '" + s.name + "' mode=" + m + " roots=" + (s.isLoaded ? s.rootCount : -1)); }

        private static void OnSceneUnloaded(UnityEngine.SceneManagement.Scene s)
        { Log.LogInfo("SCENE UNLOADED: '" + s.name + "'"); }

        private static void OnActiveSceneChanged(UnityEngine.SceneManagement.Scene a, UnityEngine.SceneManagement.Scene b)
        { Log.LogInfo("ACTIVE SCENE: '" + a.name + "' -> '" + b.name + "'"); }

        // Independent ticker - survives whatever is killing the BepInEx host object.
        internal class Probe : UnityEngine.MonoBehaviour
        {
            private float _next;
            private void Update()
            {
                if (UnityEngine.Time.unscaledTime < _next) return;
                _next = UnityEngine.Time.unscaledTime + 0.5f;
                try { Instance.Tick(); }
                catch (Exception e) { Log.LogError("probe tick: " + e); }
            }
        }

        internal static Plugin Instance;

        internal void Tick()
        {
            if (!_diagReady || Diagnostics == null || !Diagnostics.Value) return;

            object cal = null;
            try { cal = _calInstance.GetValue(null); } catch { }
            if (cal == null || cal.Equals(null)) { ProbeScene(); ProbeTutorial(); return; }

            string msg = "CAL stage=" + Str(_calStage, cal)
                       + " timer=" + Str(_calTimer, cal)
                       + " handsInPosition=" + Str(_calHandsInPos, cal)
                       + " viewedPreamble=" + Str(_calViewedPreamble, cal)
                       + " | L" + Tracker(_calLeftTracker, cal, _calLeftPoint)
                       + " | R" + Tracker(_calRightTracker, cal, _calRightPoint);
            Logger.LogInfo(msg);
        }

        // No CalibrationStation in this scene - report what IS driving the screen, so the
        // real gate can be identified instead of inferred.
        private static string _lastProbe = "";
        private int _probeCount;

        private void ProbeScene()
        {
            if (_probeCount >= 60) return;        // ~30s of samples, then stop
            _probeCount++;

            var sm = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            string scenes = "";
            try
            {
                for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                {
                    var s = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                    scenes += s.name + "(loaded=" + s.isLoaded + ",roots=" + (s.isLoaded ? s.rootCount : -1) + ") ";
                }
            }
            catch (Exception e) { scenes = "<" + e.Message + ">"; }

            int total = 0, mine = 0;
            var names = new SortedSet<string>(StringComparer.Ordinal);
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType(typeof(UnityEngine.MonoBehaviour));
                total = all.Length;
                foreach (var o in all)
                {
                    var mb = o as UnityEngine.MonoBehaviour;
                    if (mb == null) continue;
                    string n = mb.GetType().FullName;
                    if (n == null) continue;
                    if (n.StartsWith("BrokenSpectreMetaGate", StringComparison.Ordinal)) continue;   // skip self
                    if (!n.StartsWith("BrokenSpectre.", StringComparison.Ordinal)) continue;
                    mine++;
                    if (mb.isActiveAndEnabled) names.Add(n.Substring("BrokenSpectre.".Length));
                }
            }
            catch (Exception e) { Logger.LogWarning("probe failed: " + e); return; }

            string joined = string.Join(", ", new List<string>(names).ToArray());
            if (joined.Length > 1200) joined = joined.Substring(0, 1200) + " ...";

            string probe = sm.name + "|" + joined;
            bool changed = probe != _lastProbe;
            _lastProbe = probe;

            // heartbeat every 5th sample even when nothing changed, so a stalled Update is obvious
            if (!changed && (_probeCount % 5) != 0) return;

            Logger.LogInfo("PROBE#" + _probeCount + " active='" + sm.name + "' scenes=[" + scenes.Trim()
                         + "] monoBehaviours=" + total + " brokenSpectre=" + mine
                         + " enabled(" + names.Count + "): " + joined);
        }

        // Read what the game is actually telling the player, plus the live gesture state.
        // Reflection-only so no extra assembly references are needed.
        private static string _lastText = "", _lastGesture = "";

        private void ProbeTutorial()
        {
            UnityEngine.Object[] all;
            try { all = UnityEngine.Object.FindObjectsOfType(typeof(UnityEngine.MonoBehaviour)); }
            catch { return; }

            var texts = new List<string>();
            var gestures = new List<string>();

            foreach (var o in all)
            {
                var mb = o as UnityEngine.MonoBehaviour;
                if (mb == null || !mb.isActiveAndEnabled) continue;
                Type ty = mb.GetType();
                string tn = ty.FullName ?? "";

                if (tn.StartsWith("TMPro.", StringComparison.Ordinal) && texts.Count < 20)
                {
                    try
                    {
                        var p = ty.GetProperty("text");
                        var v = p == null ? null : p.GetValue(mb, null) as string;
                        if (!string.IsNullOrEmpty(v))
                        {
                            v = v.Replace("\n", " / ").Trim();
                            if (v.Length > 90) v = v.Substring(0, 90) + "...";
                            if (!texts.Contains(v)) texts.Add(v);
                        }
                    }
                    catch { }
                }
                else if (tn == "BrokenSpectre.SimpleGestureDetector")
                {
                    try
                    {
                        string side = Str(ty.GetField("handSide", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), mb);
                        string grasp = Str(ty.GetField("isGrasping", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), mb);
                        string live = "?";
                        var cg = ty.GetMethod("CheckGrasping", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (cg != null) live = Convert.ToString(cg.Invoke(mb, null));
                        gestures.Add(mb.gameObject.name + "[side=" + side + " isGrasping=" + grasp + " check=" + live + "]");
                    }
                    catch (Exception e) { gestures.Add("<" + e.GetType().Name + ">"); }
                }
            }

            string t = string.Join(" | ", texts.ToArray());
            string g = string.Join(" ", gestures.ToArray());

            if (t != _lastText) { _lastText = t; Logger.LogInfo("TEXT: " + t); }
            if (g != _lastGesture) { _lastGesture = g; Logger.LogInfo("GESTURE: " + g); }
        }

        private static string Str(FieldInfo f, object o)
        {
            if (f == null || o == null) return "?";
            try { var v = f.GetValue(o); return v == null ? "null" : v.ToString(); }
            catch (Exception e) { return "<" + e.GetType().Name + ">"; }
        }

        private static string Tracker(FieldInfo trackerField, object cal, FieldInfo pointField)
        {
            if (trackerField == null) return "(no field)";
            try
            {
                var tracker = trackerField.GetValue(cal);
                if (tracker == null || tracker.Equals(null)) return "(null tracker)";

                string active = "?", pos = "?";
                var go = _htHandObject == null ? null : _htHandObject.GetValue(tracker) as UnityEngine.GameObject;
                if (go != null)
                {
                    active = go.activeInHierarchy.ToString();
                    pos = go.transform.position.ToString("F2");
                }

                string handActive = "?";
                var hand = _htHand == null ? null : _htHand.GetValue(tracker) as UnityEngine.Component;
                if (hand != null) handActive = hand.gameObject.activeInHierarchy.ToString();

                string target = pointField == null ? "?" : Str(pointField, cal);

                return "[handObj active=" + active + " pos=" + pos + " ovrHandActive=" + handActive
                     + " palm=" + Str(_htPalmDir, tracker) + " target=" + target + "]";
            }
            catch (Exception e) { return "<" + e.GetType().Name + ": " + e.Message + ">"; }
        }

        // ---------- helpers ----------

        private void Try(string what, Action action)
        {
            try { action(); Logger.LogInfo("Patched " + what); }
            catch (Exception e) { Logger.LogError("Failed to patch " + what + ": " + e.Message); }
        }
    }
}
