// JIMMY EFFECTS (Tweak, v0.11.0, 2026-10-10): Schedule I's drug effects on Jimmy, the way the game puts them on its own
// avatar. Every product effect (Effects.Zombifying, Glowie, Shrinking, ...) runs Effect.ApplyToPlayer, which calls the
// avatar's AvatarEffects.Set* / Trigger* methods; those flip particle systems, scale the head and neck bones through
// FloatSmoothers, tint the skin through a ColorSmoother. The game copies them onto a second avatar itself
// (AvatarEffects.MirrorEffectsTo, the first-person arms). We do the same for Jimmy:
//   * every AvatarEffects call on the LOCAL player's avatar is traced (Harmony postfixes below), so a test run shows
//     exactly what the game did (the drug classes themselves can't be hooked: see FxPatches);
//   * the game's particles (stink, vomit, fart, head poof, fire, zap, anti-grav, foggy) stay drawn: v0.10's
//     HideOriginal turned off every renderer under the avatar, particle renderers included (Mod.HideOriginal now
//     skips them), and the ones on the head / neck are shifted onto Jimmy's head each frame;
//   * the head / neck size boosts (big head, giraffe) copy the game's own head / neck bone scale onto Jimmy's;
//   * player scale (Shrinking) scales Jimmy with the game's body instead of leaving him at 1.80 m.
// Skin colour, glow and hair are v0.14 (they need Jimmy's skin split into its own submesh + texture variants).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2CppScheduleOne.AvatarFramework;
using Il2CppScheduleOne.PlayerScripts;
using UnityEngine;
using SIAvatar = Il2CppScheduleOne.AvatarFramework.Avatar;

namespace BullyLink
{
    public class JimmyEffects
    {
        public static Action<string> Trace;
        /// <summary>The local player's AvatarEffects; calls on anyone else's (NPCs, the mirrored first-person arms) are ignored.</summary>
        public static IntPtr Local;
        static readonly Dictionary<string, string> _lastCall = new();

        /// <summary>One trace line per change: a call repeated with the same arguments (some run every frame) is logged once.</summary>
        internal static void Called(AvatarEffects fx, string method, string args = "")
        {
            try
            {
                if (fx == null || Local == IntPtr.Zero || fx.Pointer != Local) return;
                if (_lastCall.TryGetValue(method, out var prev) && prev == args) return;
                _lastCall[method] = args;
                Trace?.Invoke($"fx: game called AvatarEffects.{method}({args})");
            }
            catch { }
        }

        readonly SIAvatar _avatar;
        readonly AvatarEffects _fx;
        readonly JimmyModel _jimmy;
        readonly Transform _siRoot, _jHead, _jNeck;
        readonly float _scaleBase;
        readonly float _baseScale;            // the body's own scale with no effect on (Jimmy: AdultScale; a cast NPC: its height match)
        readonly bool _local;                 // the player (traces + the call tracer); false = a cast NPC (Cast.cs, v0.17)
        Vector3 _headBase = Vector3.one, _neckBase = Vector3.one;
        float _headRatio = 1f, _neckRatio = 1f, _bodyRatio = 1f;
        // head / neck emitters: original local position under the game's bone, and which Jimmy bone they follow
        readonly List<(Transform t, Vector3 local, Transform siBone, Transform jBone)> _emitters = new();

        /// <param name="local">false for a cast NPC's Bully body (v0.17, Austin: "apply the same phisical changes the normal
        /// game does to its player models to the bully ones too"): same mirroring, no tracing, its own base scale</param>
        public JimmyEffects(SIAvatar avatar, Player me, Transform siRoot, JimmyModel jimmy, bool local = true, float baseScale = JimmyModel.AdultScale)
        {
            _local = local;
            _baseScale = baseScale;
            _avatar = avatar;
            _fx = avatar.Effects;
            _jimmy = jimmy;
            _siRoot = siRoot;
            jimmy.ByName.TryGetValue("Root Head", out _jHead);
            jimmy.ByName.TryGetValue("Root Neck", out _jNeck);
            if (local) { Local = _fx != null ? _fx.Pointer : IntPtr.Zero; _lastCall.Clear(); }
            // Shrinking scales the game's body. Baseline = its scale now, corrected if we bound while already scaled.
            float ps = 1f;
            try { ps = me.Scale; } catch { }
            float lossy = siRoot.lossyScale.y;
            _scaleBase = ps > 0.1f && ps < 3f && Mathf.Abs(ps - 1f) > 0.01f ? lossy / ps : lossy;
            if (local) Trace?.Invoke($"fx: bound to the local AvatarEffects ({(_fx != null ? "found" : "MISSING")}), player scale {ps:F2}, body scale {lossy:F3}");
            if (_fx == null) return;

            var seen = new HashSet<IntPtr>();
            void Collect(Transform siBone, Transform jBone, string label)
            {
                if (siBone == null || jBone == null) return;
                foreach (var ps2 in siBone.GetComponentsInChildren<ParticleSystem>(true))
                {
                    var t = ps2.transform;
                    if (!seen.Add(t.Pointer)) continue;
                    _emitters.Add((t, t.localPosition, siBone, jBone));
                }
                if (_local) Trace?.Invoke($"fx: {label} bone '{siBone.name}' carries {_emitters.Count} emitters so far");
            }
            Collect(_fx.HeadBone, _jHead, "head");
            Collect(_fx.NeckBone, _jNeck, "neck");
            // where the named particle systems live (tells the test which ones follow Jimmy's head)
            if (local) foreach (var (n, p) in new (string, ParticleSystem)[] { ("vomit", _fx.VomitParticles), ("headpoof", _fx.HeadPoofParticles), ("fart", _fx.FartParticles), ("antigrav", _fx.AntiGravParticles), ("fire", _fx.FireParticles), ("foggy", _fx.FoggyEffects), ("zap", _fx.ZapParticles) })
                Trace?.Invoke($"fx: {n} particles {(p == null ? "none" : "at '" + Path(p.transform) + "'")}");
        }

        static string Path(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; t != null && i < 8; i++, t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        static float Mean(Vector3 v, Vector3 b) =>
            ((b.x != 0 ? v.x / b.x : 1f) + (b.y != 0 ? v.y / b.y : 1f) + (b.z != 0 ? v.z / b.z : 1f)) / 3f;

        static bool AtDefault(Il2CppScheduleOne.Tools.FloatSmoother s)
        {
            try { return s == null || Mathf.Abs(s.CurrentValue - s.DefaultValue) < 1e-3f; } catch { return true; }
        }

        /// <summary>LateUpdate, after Retarget + AnimLayer (they set rotations; this sets scale and moves emitters).</summary>
        public void Apply()
        {
            if (_fx == null) return;
            // head / neck: the game drives its bones' scale from HeadSizeBoost / NeckSizeBoost; re-learn the resting scale
            // whenever the boost is at its default, so the ratio is exactly what the game added
            var hb = _fx.HeadBone; var nb = _fx.NeckBone;
            if (hb != null && _jHead != null)
            {
                if (AtDefault(_fx.HeadSizeBoost)) _headBase = hb.localScale;
                float r = Mean(hb.localScale, _headBase);
                if (_local && Mathf.Abs(r - _headRatio) > 0.02f) Trace?.Invoke($"fx: head scale x{r:F2}");
                _headRatio = r;
                _jHead.localScale = Vector3.one * r;
            }
            if (nb != null && _jNeck != null)
            {
                if (AtDefault(_fx.NeckSizeBoost)) _neckBase = nb.localScale;
                float r = Mean(nb.localScale, _neckBase);
                if (_local && Mathf.Abs(r - _neckRatio) > 0.02f) Trace?.Invoke($"fx: neck scale x{r:F2}");
                _neckRatio = r;
                _jNeck.localScale = Vector3.one * r;
            }
            // body (Shrinking): Jimmy is 1.80 m times whatever the game scaled its body by
            float br = _scaleBase > 1e-4f ? _siRoot.lossyScale.y / _scaleBase : 1f;
            if (_local && Mathf.Abs(br - _bodyRatio) > 0.02f) Trace?.Invoke($"fx: body scale x{br:F2}");
            _bodyRatio = br;
            _jimmy.Root.transform.localScale = Vector3.one * (_baseScale * br);
            // head / neck particles come out of Jimmy's head, not the hidden rig's (a few cm off)
            foreach (var (t, local, siBone, jBone) in _emitters)
            {
                if (t == null || siBone == null) continue;
                t.localPosition = local;
                t.position += jBone.position - siBone.position;
            }
        }

        /// <summary>Put the game's emitters back where it left them and Jimmy back to his own proportions.</summary>
        public void Release()
        {
            foreach (var (t, local, _, _) in _emitters)
                try { if (t != null) t.localPosition = local; } catch { }
            _emitters.Clear();
            if (_jHead != null) _jHead.localScale = Vector3.one;
            if (_jNeck != null) _jNeck.localScale = Vector3.one;
            if (_jimmy?.Root != null) _jimmy.Root.transform.localScale = Vector3.one * _baseScale;
            if (_local && _fx != null && Local == _fx.Pointer) Local = IntPtr.Zero;
        }
    }

    // -- the game's AvatarEffects calls, traced (local player only) ----------------------------------------------------
    // Installed one by one from Mod.OnInitializeMelon, each in its own try: attribute patches go through one PatchAll,
    // and a single hook that failed would abort every patch after it (Bully's controls included).
    // v0.11.0 crashed the game natively right after its 101 hooks went in (2026-10-10, no managed error). IL2CPP folds
    // identical method bodies into one piece of machine code, so many effect classes' ApplyToPlayer / ClearFromPlayer
    // share an address, and detouring one address twice corrupts it. v0.11.1 hooked each address once and still died:
    // the folded ones (Euphoric / Focused / Munchies / Refreshing, base Effect) are empty one-instruction bodies, and the
    // ~14-byte jump a detour writes overruns into the next function. v0.11.2 drops the effect-class hooks (they only named
    // the drug in the trace) and refuses to detour anything whose code ends inside 16 bytes.
    // Guard: a marker is written before the hooks go in and removed once the game has run past startup; if a launch
    // dies in between, the next launch of the same build skips the hooks (a new build tries again).
    static class FxPatches
    {
        static readonly string Guard = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "BullyLink.fxguard.txt");

        /// <summary>The game ran past startup with the hooks in: clear the crash guard.</summary>
        public static void Survived()
        {
            try { if (System.IO.File.Exists(Guard)) System.IO.File.Delete(Guard); } catch { }
        }

        /// <summary>The IL2CPP machine-code address behind a generated method (0 if it can't be read).</summary>
        internal static unsafe IntPtr NativeCode(MethodBase m)
        {
            try
            {
                var f = Il2CppInterop.Common.Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(m);
                var info = f != null ? (IntPtr)f.GetValue(null) : IntPtr.Zero;
                return info == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)info;     // MethodInfo.methodPointer is its first field
            }
            catch { return IntPtr.Zero; }
        }

        /// <summary>A detour overwrites the first ~14 bytes with a jump. A function whose code ends sooner (a ret, or the
        /// int3 padding after it) would have its neighbour overwritten. Conservative: 0xC3 / 0xCC anywhere in the first
        /// 16 bytes counts, even inside another instruction (a false skip only loses a trace line).</summary>
        internal static unsafe bool TooSmall(IntPtr code)
        {
            var p = (byte*)code;
            for (int i = 0; i < 16; i++) if (p[i] == 0xC3 || p[i] == 0xCC) return true;
            return false;
        }

        public static void Install(HarmonyLib.Harmony h, Action<string> trace, string version)
        {
            try
            {
                if (System.IO.File.Exists(Guard) && System.IO.File.ReadAllText(Guard).Trim() == version)
                {
                    trace?.Invoke($"fx: the last launch of v{version} died after its effect hooks went in; skipping them this time (effects still mirror, no fx: call trace)");
                    return;
                }
                System.IO.File.WriteAllText(Guard, version);
            }
            catch { }
            int ok = 0, bad = 0, shared = 0, tiny = 0;
            var done = new Dictionary<IntPtr, string>();
            void One(MethodBase target, MethodInfo post)
            {
                var label = $"{target?.DeclaringType?.Name}.{target?.Name}";
                var code = NativeCode(target);
                if (code == IntPtr.Zero) { bad++; trace?.Invoke($"fx: {label}: no native address, not hooked"); return; }
                if (done.TryGetValue(code, out var first)) { shared++; trace?.Invoke($"fx: {label} shares machine code with {first} (0x{code.ToInt64():X}); hooked once"); return; }
                if (TooSmall(code)) { tiny++; trace?.Invoke($"fx: {label} is too small to detour safely (0x{code.ToInt64():X}); not hooked"); return; }
                done[code] = label;
                trace?.Invoke($"fx: hooking {label} @0x{code.ToInt64():X}");    // breadcrumb: if the game dies, the last line names it
                try { h.Patch(target, postfix: new HarmonyMethod(post)); ok++; }
                catch (Exception e) { bad++; trace?.Invoke($"fx: couldn't hook {label}: {e.Message}"); }
            }
            foreach (var post in typeof(FxPatches).GetMethods(BindingFlags.NonPublic | BindingFlags.Static))
            {
                var ps = post.GetParameters();                       // the postfixes: (AvatarEffects __instance, ...)
                if (ps.Length == 0 || ps[0].ParameterType != typeof(AvatarEffects)) continue;
                var target = typeof(AvatarEffects).GetMethod(post.Name, BindingFlags.Public | BindingFlags.Instance);
                if (target == null) { bad++; trace?.Invoke($"fx: AvatarEffects.{post.Name} not found"); continue; }
                One(target, post);
            }
            trace?.Invoke($"fx: {ok} effect hooks installed, {shared} skipped (shared machine code), {tiny} skipped (too small to detour), {bad} failed");
            Installed = ok > 0;
        }

        public static bool Installed;

        static void SetStinkParticlesActive(AvatarEffects __instance, bool active) => JimmyEffects.Called(__instance, "SetStinkParticlesActive", $"{active}");
        static void TriggerSick(AvatarEffects __instance) => JimmyEffects.Called(__instance, "TriggerSick", Time.time.ToString("F0"));
        static void SetAntiGrav(AvatarEffects __instance, bool active) => JimmyEffects.Called(__instance, "SetAntiGrav", $"{active}");
        static void SetFoggy(AvatarEffects __instance, bool active) => JimmyEffects.Called(__instance, "SetFoggy", $"{active}");
        static void VanishHair(AvatarEffects __instance) => JimmyEffects.Called(__instance, "VanishHair");
        static void ReturnHair(AvatarEffects __instance) => JimmyEffects.Called(__instance, "ReturnHair");
        static void SetZapped(AvatarEffects __instance, bool zapped) => JimmyEffects.Called(__instance, "SetZapped", $"{zapped}");
        static void OverrideHairColor(AvatarEffects __instance, Color color) => JimmyEffects.Called(__instance, "OverrideHairColor", $"{color}");
        static void ResetHairColor(AvatarEffects __instance) => JimmyEffects.Called(__instance, "ResetHairColor");
        static void OverrideEyeColor(AvatarEffects __instance, Color color, float emission) => JimmyEffects.Called(__instance, "OverrideEyeColor", $"{color}, {emission:F2}");
        static void ResetEyeColor(AvatarEffects __instance) => JimmyEffects.Called(__instance, "ResetEyeColor");
        static void SetEyeLightEmission(AvatarEffects __instance, float intensity, Color color) => JimmyEffects.Called(__instance, "SetEyeLightEmission", $"{intensity:F2}, {color}");
        static void EnableLaxative(AvatarEffects __instance) => JimmyEffects.Called(__instance, "EnableLaxative");
        static void DisableLaxative(AvatarEffects __instance) => JimmyEffects.Called(__instance, "DisableLaxative");
        static void SetFireActive(AvatarEffects __instance, bool active) => JimmyEffects.Called(__instance, "SetFireActive", $"{active}");
        static void SetBigHeadActive(AvatarEffects __instance, bool active) => JimmyEffects.Called(__instance, "SetBigHeadActive", $"{active}");
        static void SetGiraffeActive(AvatarEffects __instance, bool active) => JimmyEffects.Called(__instance, "SetGiraffeActive", $"{active}");
        static void SetSkinColorInverted(AvatarEffects __instance, bool inverted) => JimmyEffects.Called(__instance, "SetSkinColorInverted", $"{inverted}");
        static void SetSicklySkinColor(AvatarEffects __instance) => JimmyEffects.Called(__instance, "SetSicklySkinColor");
        static void SetDefaultSkinColor(AvatarEffects __instance, Color color) => JimmyEffects.Called(__instance, "SetDefaultSkinColor", $"{color}");
        static void SetGenderInverted(AvatarEffects __instance, bool inverted) => JimmyEffects.Called(__instance, "SetGenderInverted", $"{inverted}");
        static void AddAdditionalWeightOverride(AvatarEffects __instance, float value, string label) => JimmyEffects.Called(__instance, "AddAdditionalWeightOverride", $"{value:F2}, {label}");
        static void RemoveAdditionalWeightOverride(AvatarEffects __instance, string label) => JimmyEffects.Called(__instance, "RemoveAdditionalWeightOverride", label);
        static void SetGlowingOn(AvatarEffects __instance, Color color) => JimmyEffects.Called(__instance, "SetGlowingOn", $"{color}");
        static void SetGlowingOff(AvatarEffects __instance) => JimmyEffects.Called(__instance, "SetGlowingOff");
        static void TriggerCountdownExplosion(AvatarEffects __instance) => JimmyEffects.Called(__instance, "TriggerCountdownExplosion");
        static void StopCountdownExplosion(AvatarEffects __instance) => JimmyEffects.Called(__instance, "StopCountdownExplosion");
        static void SetCyclopean(AvatarEffects __instance, bool enabled) => JimmyEffects.Called(__instance, "SetCyclopean", $"{enabled}");
        static void SetZombified(AvatarEffects __instance, bool zombified) => JimmyEffects.Called(__instance, "SetZombified", $"{zombified}");
    }
}
