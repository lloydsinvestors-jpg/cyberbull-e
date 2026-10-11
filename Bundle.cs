// bullylink.bundle: built in the Unity editor at Schedule I's exact versions (D:\Unity\BullyLinkAssets, BuildBully.cs),
// embedded in this DLL. Carries compiled URP shader variants, so its materials and particle effects draw properly
// (the runtime-cloned materials kept coming out magenta). Everything degrades to the old runtime path if it won't load.
// v0.23 (bring your own Bully): the embedded bundle is the PUBLIC build (D:\Unity\BullyLinkPublic): plain soft dots where
// Bully's effect textures were, no Jimmy / prop materials. Paint() puts the player's own effect textures on (exported from
// their Bully install to UserData\BullyLink\data\fx_<name>.png); without them the effects still draw, as soft puffs.
using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace BullyLink
{
    public static class Bundle
    {
        static AssetBundle _ab;
        static bool _tried;
        static readonly Dictionary<string, UnityEngine.Object> Cache = new();
        public static Action<string> Trace = _ => { };

        public static bool Ready
        {
            get
            {
                if (!_tried)
                {
                    _tried = true;
                    try
                    {
                        Trace("bundle: loading bullylink (AssetBundle.LoadFromMemory is an interop stand-in; if the game dies here, this is why)");
                        _ab = AssetBundle.LoadFromMemory(JimmyModel.Res("bullylink"));
                        Trace(_ab != null ? $"bundle: loaded, {_ab.GetAllAssetNames().Length} assets" : "bundle: LoadFromMemory returned null");
                    }
                    catch (Exception e) { Trace($"bundle: failed ({e.Message}); falling back to runtime materials"); }
                }
                return _ab != null;
            }
        }

        static T Load<T>(string path) where T : UnityEngine.Object
        {
            if (!Ready) return null;
            if (Cache.TryGetValue(path, out var o)) return o?.TryCast<T>();
            try
            {
                var a = _ab.LoadAsset(path, Il2CppType.Of<T>());
                Cache[path] = a;
                if (a == null) Trace($"bundle: no {path}");
                return a?.TryCast<T>();
            }
            catch (Exception e) { Trace($"bundle: {path}: {e.Message}"); Cache[path] = null; return null; }
        }

        // The bundle's own Lit materials never drew in Schedule I: its copy of the URP Lit shader doesn't match the variants
        // the game compiled, even after matching the URP asset (v0.4-v0.11, F9 test). Jimmy and the props use the game's
        // own in-use material (JimmyModel.Paint); the F9 switch was retired in v0.12. The bundle stays for its particle FX.

        /// <summary>Spawn one of the particle effects (FX_StinkCloud, FX_Itch, FX_ItchCloud, FX_Bang, FX_RocketTrail,
        /// FX_EggSplat, FX_Dust, FX_Hit). One-shots clean themselves up; pass life to kill looping ones.</summary>
        public static GameObject Fx(string name, Vector3 at, Quaternion? rot = null, float life = -1f, Transform parent = null)
        {
            var prefab = Load<GameObject>($"assets/bully/fx/{name.ToLowerInvariant()}.prefab");
            if (prefab == null) return null;
            var go = UnityEngine.Object.Instantiate(prefab).TryCast<GameObject>();
            if (go == null) return null;
            try { Paint(go); } catch (Exception e) { Trace($"bundle: paint {name}: {e.Message}"); }
            if (parent != null) go.transform.SetParent(parent, false);
            else go.transform.SetPositionAndRotation(at, rot ?? Quaternion.identity);
            UnityEngine.Object.Destroy(go, life > 0 ? life : 6f);
            return go;
        }

        static readonly Dictionary<string, Texture2D> Painted = new();
        static readonly HashSet<string> Unexported = new();

        /// <summary>Each particle material (fxmat_<name>) gets fx_<name>.png from the player's export; the material is the
        /// bundle's own asset, so every later instance shares it. Re-checked per spawn in case the texture got unloaded.</summary>
        static void Paint(GameObject go)
        {
            foreach (var r in go.GetComponentsInChildren<ParticleSystemRenderer>(true))
            {
                var m = r.sharedMaterial;
                if (m == null || !m.name.StartsWith("fxmat_")) continue;
                var key = "fx_" + m.name.Substring(6);
                if (Unexported.Contains(key)) continue;
                var cur = m.GetTexture("_BaseMap");
                if (cur != null && cur.name == "byob_" + key) continue;
                if (!Painted.TryGetValue(key, out var t) || t == null)
                {
                    var f = System.IO.Path.Combine(JimmyModel.DataDir, key + ".png");
                    if (!System.IO.File.Exists(f)) { Unexported.Add(key); Trace($"bundle: no {key}.png exported; {m.name} keeps its plain dot"); continue; }
                    t = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = "byob_" + key, wrapMode = TextureWrapMode.Clamp };
                    ImageConversion.LoadImage(t, System.IO.File.ReadAllBytes(f));
                    t.hideFlags = HideFlags.DontUnloadUnusedAsset;
                    Painted[key] = t;
                    Trace($"bundle: {m.name} painted with the player's {key}");
                }
                m.SetTexture("_BaseMap", t);
            }
        }
    }
}
