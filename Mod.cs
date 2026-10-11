// BULLYLINK (Tweak, 2026-10-08): Bully x Schedule I, NATIVE. You play Schedule I as Jimmy Hopkins.
// Austin: "would it be easier to scalp the elements from bully and make the mod file run native on schedual1?" ->
// "yes go native, build it". The passthrough version (both games running at once) is retired.
//
//   * Jimmy's real model + textures come from the player's own Bully install (Stream/World.img -> PLAYER.nif/.nft),
//     converted by tools/export_jimmy.py. v0.23 (bring your own Bully): the launcher's exporter writes them into
//     UserData\BullyLink on the player's machine; nothing from Bully ships inside this DLL.
//   * He replaces your Schedule I body: Retarget.cs copies every pose Schedule I's own animator makes (walk, run,
//     crouch, skate, ragdoll) onto Jimmy's Bully skeleton, bone for bone, every frame. (v0.2.0 used Unity's
//     AvatarBuilder/HumanPoseHandler; the game strips those and the interop stand-ins crashed it on load.)
//   * Three keys (v0.12): F6 (pad: L3+R3) the mod on / off: Jimmy + Bully's controls, weapon wheel, items,
//     grapple/wedgie, social moves, voices, HUD (Kit.cs, BullyInput.cs, Voice.cs), or stock Schedule I. F8: first /
//     third person. F10: save the pose + diagnostics. Any Schedule I screen, station, the phone or a car hands every
//     key back to the game and drops to first person until it closes.
//   * Next: his prank kit (slingshot, firecrackers, stink bombs, itching powder, marbles) + fighting, rebuilt natively.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppScheduleOne.PlayerScripts;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;
using Il2CppInterop.Runtime;
using SIAvatar = Il2CppScheduleOne.AvatarFramework.Avatar;

[assembly: MelonInfo(typeof(BullyLink.Mod), "BullyLink", "0.23.1", "Tweak")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace BullyLink
{
    /// <summary>Jimmy built from jimmy.bytes: bone transforms, one skinned mesh (4 parts), his Bully textures.</summary>
    public class JimmyModel
    {
        public GameObject Root;
        public Transform[] Bones;
        public Dictionary<string, Transform> ByName = new();
        public Quaternion[] BindRot, TposeRot;
        public Vector3[] BindPos;
        public SkinnedMeshRenderer Renderer;
        /// <summary>The same body without its head, drawn in first person (null if the head part couldn't be found).</summary>
        public SkinnedMeshRenderer FirstPerson;
        /// <summary>v0.22 wardrobe (Outfit.cs): each bone's bind matrix, every part's triangles + texture (to blank a part a
        /// worn item covers), bone name -> index, and what's worn per slot.</summary>
        public Matrix4x4[] Bindposes;
        public int[][] PartTris;
        public string[] PartTex;
        public Dictionary<string, int> BoneIndex = new();
        public readonly Dictionary<string, (string item, GameObject go)> Worn = new();
        /// <summary>Jimmy is 21 in this mod (Austin): Bully's 1.70 m teenager built to 1.80 m. No age stat exists in either game.</summary>
        public const float AdultScale = 1.06f;
        /// <summary>The Bully adults for the cast swap (Cast.cs, tools/export_cast.py): read from disk, ~38 MB is too much to embed.</summary>
        public static readonly string PedDir = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "BullyLink", "peds");
        /// <summary>Jimmy's Bully clothing (tools/export_clothes.py, the wardrobe): also read from disk.</summary>
        public static readonly string ClothesDir = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "BullyLink", "clothes");

        /// <summary>v0.23 (bring your own Bully): nothing from Bully is inside this DLL. The BullyLink Launcher's exporter
        /// builds Jimmy, the props, anims, sounds, icons, logo and effect textures from the player's own Bully install into
        /// here (plus data\export.txt once it finishes); only the effects bundle (no Bully pixels) is still embedded.</summary>
        public static readonly string DataDir = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "BullyLink", "data");
        public static bool Exported => File.Exists(Path.Combine(DataDir, "export.txt"));

        public static byte[] Res(string name)
        {
            foreach (var dir in new[] { DataDir, PedDir, ClothesDir })
            {
                var f = Path.Combine(dir, name);
                if (File.Exists(f)) return File.ReadAllBytes(f);
            }
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (s == null) throw new FileNotFoundException($"{name} missing: run the CyberBull-E Launcher to build it from your Bully install");
            using var m = new MemoryStream();
            s.CopyTo(m);
            return m.ToArray();
        }

        /// <summary>A copy of this (never-drawn, bind-pose) model sharing its mesh and materials: one per cast NPC, cheap
        /// next to rebuilding the mesh and decoding its textures each time. No first-person body.</summary>
        public JimmyModel Clone(string goName)
        {
            var go = UnityEngine.Object.Instantiate(Root).TryCast<GameObject>();
            go.name = goName;
            var c = new JimmyModel { Root = go, BindRot = BindRot, TposeRot = TposeRot, BindPos = BindPos };
            var all = new Dictionary<string, Transform>();
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) all[t.name] = t;
            c.Bones = new Transform[Bones.Length];
            for (int i = 0; i < Bones.Length; i++) { c.Bones[i] = all[Bones[i].name]; c.ByName[Bones[i].name] = c.Bones[i]; }
            c.Renderer = go.GetComponent<SkinnedMeshRenderer>();
            c.Renderer.updateWhenOffscreen = false;          // dozens of NPCs: don't skin the ones off screen
            // v0.18.2 (Austin: punched NPCs "blink in and out of existance"): with updateWhenOffscreen off, Unity culls by
            // localBounds, which is relative to the ROOT BONE (the hips); the mesh's own bounds are measured from the feet in
            // the model's frame, so the box sat ~1 m too high and tilted with Bully's bone axes: up close (punching range)
            // it left the view while the body was in it. A 3.4 m box centred on the hips holds any pose, lying down included.
            try { c.Renderer.localBounds = new Bounds(Vector3.zero, Vector3.one * 3.4f); }
            catch { c.Renderer.updateWhenOffscreen = true; }    // localBounds resolves at runtime (stripcheck NO-NATIVE): if it won't take, never cull
            foreach (var r in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (r != c.Renderer) UnityEngine.Object.Destroy(r.gameObject);
            return c;
        }

        static string Str(BinaryReader r) => System.Text.Encoding.UTF8.GetString(r.ReadBytes(r.ReadUInt16()));
        static Vector3 V3(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        static Quaternion Q(BinaryReader r) => new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        /// <summary>Any Bully ped exported by tools/export_jimmy.py: Jimmy (jimmy.bytes, kept across scene loads) or e.g. the
        /// carnival shop's carnie (carnie.bytes, a scene object: pass keep: false and parent it into the scene).</summary>
        public static JimmyModel Build(Material template, Action<string> trace, string res = "jimmy.bytes", string goName = "BullyLink_Jimmy", float scale = AdultScale, bool keep = true)
        {
            var j = new JimmyModel();
            using var r = new BinaryReader(new MemoryStream(Res(res)));
            if (new string(r.ReadChars(4)) != "JIMY") throw new InvalidDataException("not a ped .bytes file: " + res);
            r.ReadUInt32();
            j.Root = new GameObject(goName);
            if (keep) UnityEngine.Object.DontDestroyOnLoad(j.Root);

            int nb = r.ReadInt32();
            j.Bones = new Transform[nb];
            j.BindRot = new Quaternion[nb];
            j.TposeRot = new Quaternion[nb];
            j.BindPos = new Vector3[nb];
            for (int i = 0; i < nb; i++)
            {
                var name = Str(r);
                int parent = r.ReadInt32();
                var t = new GameObject(name).transform;
                t.SetParent(parent >= 0 ? j.Bones[parent] : j.Root.transform, false);
                j.BindPos[i] = V3(r);
                j.BindRot[i] = Q(r);
                j.TposeRot[i] = Q(r);
                t.localPosition = j.BindPos[i];
                t.localRotation = j.BindRot[i];
                j.Bones[i] = t;
                j.ByName[name] = t;
            }

            // one mesh, one submesh per Bully part (head, shirt + arms, jeans, boots)
            var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var uv = new List<Vector2>();
            var bw = new List<BoneWeight>(); var subs = new List<int[]>(); var texs = new List<string>();
            int nm = r.ReadInt32();
            for (int m = 0; m < nm; m++)
            {
                Str(r);
                texs.Add(Str(r));
                int nv = r.ReadInt32(), b0 = pos.Count;
                for (int v = 0; v < nv; v++)
                {
                    pos.Add(V3(r)); nrm.Add(V3(r)); uv.Add(new Vector2(r.ReadSingle(), r.ReadSingle()));
                    int i0 = r.ReadInt32(), i1 = r.ReadInt32(), i2 = r.ReadInt32(), i3 = r.ReadInt32();
                    bw.Add(new BoneWeight
                    {
                        boneIndex0 = i0, boneIndex1 = i1, boneIndex2 = i2, boneIndex3 = i3,
                        weight0 = r.ReadSingle(), weight1 = r.ReadSingle(), weight2 = r.ReadSingle(), weight3 = r.ReadSingle(),
                    });
                }
                int ni = r.ReadInt32();
                var idx = new int[ni];
                for (int k = 0; k < ni; k++) idx[k] = b0 + (int)r.ReadUInt32();
                subs.Add(idx);
            }

            var mesh = new Mesh { name = "Jimmy" };
            trace($"build: {nb} bones read, mesh {pos.Count} verts / {subs.Count} parts");
            mesh.vertices = pos.ToArray();
            mesh.normals = nrm.ToArray();
            mesh.uv = uv.ToArray();
            trace("build: vertices/normals/uv set");
            mesh.boneWeights = bw.ToArray();
            trace("build: bone weights set");
            mesh.subMeshCount = subs.Count;
            for (int s = 0; s < subs.Count; s++) mesh.SetTriangles(subs[s], s);
            var bind = new Matrix4x4[nb];
            for (int i = 0; i < nb; i++) bind[i] = j.Bones[i].worldToLocalMatrix * j.Root.transform.localToWorldMatrix;
            trace("build: triangles set");
            mesh.bindposes = bind;
            j.Bindposes = bind;
            j.PartTris = subs.ToArray();
            j.PartTex = texs.ToArray();
            for (int i = 0; i < nb; i++) j.BoneIndex[j.Bones[i].name] = i;
            trace("build: bindposes set");
            mesh.RecalculateBounds();

            trace("build: bounds ok");
            var mats = new Material[texs.Count];
            for (int s = 0; s < texs.Count; s++) mats[s] = Paint(template, texs[s], trace);

            trace("build: materials + textures ok");
            j.Renderer = j.Root.AddComponent<SkinnedMeshRenderer>();
            j.Renderer.sharedMesh = mesh;
            j.Renderer.bones = j.Bones;
            j.Renderer.rootBone = j.ByName["Root"];
            j.Renderer.sharedMaterials = mats;
            j.Renderer.updateWhenOffscreen = true;
            trace("build: skinned renderer ok");

            // first person (Austin: "first person mode you cant see jimmys hands"): a second renderer on the same bones
            // drawing every part but the head, the way GTA V hides the head and keeps the body. The head part = the one
            // whose vertices mostly hang off the head bone or its children (eyes, lids, brow, hair).
            if (j.ByName.TryGetValue("Root Head", out var headBone))
            {
                var headSet = new HashSet<int>();
                for (int i = 0; i < nb; i++) if (j.Bones[i] == headBone || j.Bones[i].IsChildOf(headBone)) headSet.Add(i);
                int headPart = -1; float best = 0.5f;
                for (int s = 0; s < subs.Count; s++)
                {
                    var used = new HashSet<int>(subs[s]);
                    int onHead = 0;
                    foreach (var v in used) if (headSet.Contains(bw[v].boneIndex0)) onHead++;
                    float frac = used.Count > 0 ? onHead / (float)used.Count : 0f;
                    if (frac > best) { best = frac; headPart = s; }
                }
                if (headPart >= 0)
                {
                    var fp = new Mesh { name = "Jimmy_FP" };
                    fp.vertices = mesh.vertices; fp.normals = mesh.normals; fp.uv = mesh.uv; fp.boneWeights = mesh.boneWeights;
                    fp.subMeshCount = subs.Count;
                    for (int s = 0; s < subs.Count; s++) fp.SetTriangles(s == headPart ? new int[0] : subs[s], s);
                    fp.bindposes = bind;
                    fp.RecalculateBounds();
                    var fpGo = new GameObject(goName + "_FirstPerson");
                    fpGo.transform.SetParent(j.Root.transform, false);
                    j.FirstPerson = fpGo.AddComponent<SkinnedMeshRenderer>();
                    j.FirstPerson.sharedMesh = fp;
                    j.FirstPerson.bones = j.Bones;
                    j.FirstPerson.rootBone = j.ByName["Root"];
                    // v0.21.5 (Austin: "in first person i can look down and see into my own body"): the body is drawn
                    // double-sided (_Cull 0) so clothes never show holes; from inside it that draws his shirt's lining. The
                    // first-person copy gets back-face-culled materials: from inside, you see through the shell.
                    var fpMats = new Material[mats.Length];
                    for (int m2 = 0; m2 < mats.Length; m2++)
                    {
                        fpMats[m2] = mats[m2] != null ? new Material(mats[m2]) : null;
                        if (fpMats[m2] != null && fpMats[m2].HasProperty("_Cull")) fpMats[m2].SetFloat("_Cull", 2f);   // 2 = Back
                    }
                    j.FirstPerson.sharedMaterials = fpMats;
                    j.FirstPerson.updateWhenOffscreen = true;
                    j.FirstPerson.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;   // the full body casts the shadow (with its head)
                    j.FirstPerson.forceRenderingOff = true;
                    trace($"build: first-person body = every part but #{headPart} (the head: {best:P0} of its vertices on the head bones)");
                }
                else trace("build: no head part found; first person keeps the shadow-only body");
            }
            j.Root.transform.localScale = Vector3.one * scale;
            return j;
        }

        static Material _base;

        static Material _cutBase;
        static bool _cutTried;

        /// <summary>An alpha-cutout copy for textures with see-through parts (the bike's spoked wheels). Cloned from a
        /// cutout (_ALPHATEST_ON) URP Lit material the game is visibly drawing, so its variant is compiled in.</summary>
        public static Material TexturedCutout(string tex)
        {
            if (!_cutTried || _cutBase == null)
            {
                _cutTried = true;
                foreach (var r in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
                {
                    if (r == null || !r.isVisible) continue;
                    foreach (var mm in r.sharedMaterials)
                        if (mm != null && mm.shader != null && mm.shader.name == "Universal Render Pipeline/Lit" && mm.IsKeywordEnabled("_ALPHATEST_ON") && !mm.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT"))
                        { _cutBase = mm; break; }
                    if (_cutBase != null) break;
                }
            }
            if (_cutBase == null) return Textured(tex);           // (also re-found when a scene change unloaded the old one)
            var m = new Material(_cutBase);
            foreach (var p in new[] { "_BumpMap", "_EmissionMap", "_MetallicGlossMap", "_OcclusionMap" })
                if (m.HasProperty(p)) m.SetTexture(p, null);
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = tex };
            ImageConversion.LoadImage(t, Res(tex + ".png"));
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", t);
            m.mainTexture = t;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
            if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", 0.5f);
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);
            return m;
        }

        /// <summary>The game material Jimmy uses, wearing one of the embedded textures (Bully's item models).</summary>
        public static Material Textured(string tex)
        {
            if (_base == null) BaseMaterial(null, _ => { });          // v0.22.7: the base may have gone with the menu scene: find a live one
            if (_base == null) return null;
            var m = new Material(_base);
            foreach (var p in new[] { "_BumpMap", "_EmissionMap", "_MetallicGlossMap", "_OcclusionMap" })
                if (m.HasProperty(p)) m.SetTexture(p, null);
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = tex };
            ImageConversion.LoadImage(t, Res(tex + ".png"));
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", t);
            m.mainTexture = t;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
            m.color = Color.white;
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);
            return m;
        }

        /// <summary>A flat-colour copy of the game material Jimmy uses (item / projectile visuals).</summary>
        public static Material Tint(Color c)
        {
            if (_base == null) return null;
            var m = new Material(_base);
            foreach (var p in new[] { "_BaseMap", "_BumpMap", "_EmissionMap", "_MetallicGlossMap", "_OcclusionMap" })
                if (m.HasProperty(p)) m.SetTexture(p, null);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            m.color = c;
            return m;
        }

        /// <summary>The material Jimmy and his props are cloned from. URP compiles only the shader variants the game
        /// actually draws, so a material that merely exists can still come out magenta: v0.2.1 (Shader.Find) and the
        /// v0.3.1 effect puffs ("EyeBrow (Instance)", a runtime-only material) both did. Now: the opaque URP Lit
        /// material that the most renderers in the world are drawing with right now.</summary>
        static Material BaseMaterial(Material template, Action<string> trace)
        {
            if (_base != null) return _base;
            var count = new Dictionary<IntPtr, (Material m, int n)>();
            foreach (var r in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
            {
                if (r == null || !r.enabled || !r.isVisible) continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || m.shader == null || m.shader.name != "Universal Render Pipeline/Lit") continue;
                    if (m.IsKeywordEnabled("_ALPHATEST_ON") || m.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT") || m.IsKeywordEnabled("_EMISSION")) continue;
                    if (m.renderQueue > 2450) continue;
                    var k = m.Pointer;
                    count[k] = count.TryGetValue(k, out var c) ? (m, c.n + 1) : (m, 1);
                }
            }
            Material best = null; int bn = 0;
            foreach (var kv in count) if (kv.Value.n > bn) { best = kv.Value.m; bn = kv.Value.n; }
            if (best != null) trace($"material: cloning '{best.name}', drawn by {bn} visible renderers (keywords: {string.Join(",", best.shaderKeywords)})");
            else
            {
                best = template;
                trace($"material: nothing visible draws URP Lit; cloning the body's '{template?.name}' ({template?.shader?.name})");
            }
            _base = best;
            return best;
        }

        static Material Paint(Material template, string tex, Action<string> trace)
        {
            var m = new Material(BaseMaterial(template, trace));
            foreach (var p in new[] { "_BumpMap", "_EmissionMap", "_MetallicGlossMap", "_OcclusionMap", "_ParallaxMap", "_SpecGlossMap", "_DetailAlbedoMap", "_DetailNormalMap" })
                if (m.HasProperty(p)) m.SetTexture(p, null);
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = tex };
            ImageConversion.LoadImage(t, Res(tex + ".png"));
            t.wrapMode = TextureWrapMode.Repeat;
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", t);
            m.mainTexture = t;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
            m.color = Color.white;
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.15f);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);          // draw both sides (a render state, not a shader variant)
            return m;
        }

        /// <summary>Jimmy's bones by humanoid slot (see Retarget.Pairs).</summary>
        public Dictionary<HumanBodyBones, Transform> Humanoid()
        {
            var d = new Dictionary<HumanBodyBones, Transform>();
            foreach (var (hb, name) in Retarget.Pairs)
                if (ByName.TryGetValue(name, out var t)) d[hb] = t;
            return d;
        }
    }

    public class Mod : MelonMod
    {
        JimmyModel _jimmy;
        Retarget _rt;
        JimmyEffects _fx;
        bool _survived;
        Animator _srcAnim;
        SIAvatar _srcAvatar;
        readonly List<Renderer> _hidden = new();
        float _rescan;
        bool _on = true, _broken;        // F6: the whole mod (Jimmy + Bully controls) on / off = stock Schedule I
        float _dumpAt = -1, _diagAt = -1;
        bool _wasRag;
        float _rebindAt = -1;
        float _barUntil = 10f;           // the key bar shows for a few seconds after load and after every toggle
        readonly List<Renderer> _hiddenViewmodel = new();
        bool _camOn;
        bool _camWanted = true;          // F8 pick (third person by default: Bully is third person); kept across toggles and scenes,
                                         // suspended while a Schedule I screen is open or the mod is off
        Kit _kit;
        Skate _skate;
        Vehicles _rides;
        Shop _shop;
        Wardrobe _wardrobe;
        AnimLayer _anim;
        Hud _hud;
        string _status = "load into the world";
        float _statusUntil;

        static readonly string TracePath = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "BullyLink.trace.txt");

        /// <summary>A breadcrumb that hits the disk immediately: if the game dies natively, the last line names the step.</summary>
        static void Trace(string s)
        {
            try { File.AppendAllText(TracePath, $"{DateTime.Now:HH:mm:ss.fff} {s}{Environment.NewLine}"); } catch { }
        }

        void Say(string s, float secs = 6f)
        {
            _status = s;
            _statusUntil = Time.unscaledTime + secs;
            LoggerInstance.Msg(s);
        }

        public override void OnInitializeMelon()
        {
            Voice.Trace = Trace;
            Sfx.Trace = Trace;
            Bundle.Trace = Trace;
            JimmyEffects.Trace = Trace;
            try { FxPatches.Install(HarmonyInstance, Trace, Info.Version); } catch (Exception e) { Trace($"fx: hooks not installed ({e.Message})"); }
            try { Voice.Load(); } catch (Exception e) { Trace($"voice: couldn't open Bully's speech ({e.Message}); Jimmy stays quiet"); }
            Cast.Trace = Trace;
            Menu.Trace = Trace;
            Outfit.Trace = Trace;
            try { Outfit.Load(); } catch (Exception e) { Trace($"outfit: {e.Message}"); }
            try { Cast.Install(HarmonyInstance, Info.Version); } catch (Exception e) { Trace($"cast: voice hook not installed ({e.Message})"); }
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (!_survived) { _survived = true; FxPatches.Survived(); Cast.Survived(); Trace($"fx: past startup ('{sceneName}'), effect hooks {(FxPatches.Installed ? "in" : "off")}"); }
            Cast.OnSceneChange();
            if (sceneName == "Menu") Outfit.Unload();
            _saveMgr = null; _lastSinceSave = -1f;
            Menu.OnScene(sceneName);
            _rides?.OnSceneChange();
            _shop?.OnSceneChange();
            _wardrobe?.OnSceneChange();
            Unbind();                       // the player object is rebuilt per scene; Jimmy re-attaches when it exists
            _camOn = false;
            BullyInput.BullyMode = false;   // back on by itself once Jimmy re-binds (if the mod is on)
        }

        float _hudErrAt;

        void UpdateHud(Player me)
        {
            bool show = BullyInput.BullyMode && _kit != null && !SchedOneUiOpen();
            try
            {
                if (show && (_hud == null || !_hud.Built)) { _hud = new Hud { Trace = Trace }; _hud.Build(); Trace("hud: built (Canvas + RawImage + TextMeshPro)"); }
                if (_hud == null) return;
                _hud.Show(show);
                if (!show) return;
                float hp = 1f;
                try { hp = me.Health.CurrentHealth / Il2CppScheduleOne.PlayerScripts.Health.PlayerHealth.MaxHealth; } catch { }
                _hud.Update(_kit, me, Camera.main, hp, ViewPoint);
            }
            catch (Exception e)
            {
                if (Time.unscaledTime > _hudErrAt) { Trace($"hud: {e}"); _hudErrAt = Time.unscaledTime + 10f; }   // once per 10 s, not 50x a second
            }
        }

        /// <summary>Bully tilts Jimmy's upper body to where you aim: bend the spine by the camera's pitch.</summary>
        void AimPitch(float weight)
        {
            var cam = Camera.main;
            if (cam == null || _jimmy == null) return;
            float pitch = weight * Mathf.Clamp(Mathf.Asin(Mathf.Clamp(cam.transform.forward.y, -1f, 1f)) * Mathf.Rad2Deg, -55f, 55f);
            var right = _jimmy.Root.transform.right;
            if (_jimmy.ByName.TryGetValue("Root Spine1", out var s1)) s1.rotation = Quaternion.AngleAxis(-pitch * 0.4f, right) * s1.rotation;
            if (_jimmy.ByName.TryGetValue("Root Spine2", out var s2)) s2.rotation = Quaternion.AngleAxis(-pitch * 0.6f, right) * s2.rotation;
        }

        void SetBullyMode(bool on)
        {
            BullyInput.BullyMode = on;
            Kit.Grabbing = false;
            if (on && _kit == null)
            {
                _kit = new Kit { Trace = Trace };
                _kit.Hand = () => _jimmy != null && _jimmy.ByName.TryGetValue("Root R Hand", out var h) ? h : null;
                _kit.HandL = () => _jimmy != null && _jimmy.ByName.TryGetValue("Root L Hand", out var h) ? h : null;
                if (_anim == null && _jimmy != null) _anim = new AnimLayer(_jimmy.ByName);
                _kit.Anim = _anim;
                _kit.View = () => Camera.main;
            }
            if (!on) { _kit?.Hide(); _hud?.Show(false); BullyInput.GameHasControl = false; BullyInput.FirstPerson = false; }
            SetCam(on && _camWanted, quiet: true);   // the mod off is always the game's own first person
            if (on) Voice.Say("Jimmy", "PIDLE");
        }

        /// <summary>True while Schedule I has the player: any screen (storage, shops, dialogue / deals, phone, pause), a
        /// station or anything else that takes the camera (mixing, packaging, chemistry, ATM, bed), or a car.
        /// While it's true the game gets every key back (v0.12: Austin, storage / equipment were blocked by Bully's
        /// controls), Jimmy's kit and ride keys stand down, and third person drops to first person.</summary>
        static PlayerCamera _pc;
        static Il2CppScheduleOne.UI.Phone.Phone _phone;
        static float _uiLookup;
        // the cursor as normal play leaves it (learned at bind): any change = the game opened something that frees the mouse
        // (lockState only: Cursor.visible is an interop stand-in in this build, stripcheck)
        static CursorLockMode _playLock = CursorLockMode.Locked;
        static bool _cursorKnown;
        public static string GameElement { get; private set; } = "";

        static bool SchedOneUiOpen()
        {
            try
            {
                if ((_pc == null || _phone == null) && Time.unscaledTime > _uiLookup)
                {
                    _uiLookup = Time.unscaledTime + 1f;
                    if (_pc == null) _pc = UnityEngine.Object.FindObjectOfType<PlayerCamera>();
                    if (_phone == null) _phone = UnityEngine.Object.FindObjectOfType<Il2CppScheduleOne.UI.Phone.Phone>();
                }
                GameElement = "";
                if (_pc != null && _pc.ActiveUIElementCount > 0) GameElement = "a Schedule I screen";
                else if (_pc != null && _pc.transformOverriden && !Vehicles.Riding) GameElement = "a station";
                else if (_phone != null && _phone.IsOpen) GameElement = "the phone";
                else if (Player.Local != null && Player.Local.IsInVehicle) GameElement = "a car";
                else if (_cursorKnown && Cursor.lockState != _playLock) GameElement = "a menu";
                return GameElement.Length > 0;
            }
            catch { _pc = null; _phone = null; GameElement = ""; return false; }
        }

        static void LearnPlayCursor()
        {
            try { _playLock = Cursor.lockState; _cursorKnown = true; Trace($"input: play cursor = {_playLock}"); }
            catch (Exception e) { _cursorKnown = false; Trace($"input: cursor state unreadable ({e.Message}); menus caught by the UI count only"); }
        }

        void Unbind()
        {
            try { if (Vehicles.Riding) _rides?.ForceOff(Player.Local); } catch { }
            ShowOriginal();
            ReleaseHeldItems();
            try { _fx?.Release(); } catch { }
            _fx = null;
            _rt = null;
            _srcAnim = null;
            _srcAvatar = null;
            if (_jimmy != null) _jimmy.Root.SetActive(false);
        }

        public override void OnUpdate()
        {
            BullyInput.Update();
            Menu.Update();
            // a storage / shop / station / phone / car takes every key back from Bully mode until it closes
            bool gameHas = BullyInput.BullyMode && _rt != null && SchedOneUiOpen();
            if (gameHas != BullyInput.GameHasControl)
            {
                BullyInput.GameHasControl = gameHas;
                if (gameHas) { Kit.Grabbing = false; _kit?.Hide(); Say($"game controls while {GameElement} is open (Bully controls come back when you leave)", 4f); }
                Trace($"input: {(gameHas ? "game has control (" + GameElement + ")" : "Bully controls back")}");
            }
            // three keys (Austin, 2026-10-10: "simplify the amount of buttons"): F6 mod, F8 camera, F10 pose
            if (BullyInput.ToggleDown)
            {
                _on = !_on;
                _barUntil = Time.unscaledTime + 5f;
                if (!_on) { SetBullyMode(false); Unbind(); _shop?.SetActive(false); Cast.Clear(); Say("mod off: stock Schedule I (F6 brings Jimmy back)", 4f); }
                else Say("mod on: Jimmy + Bully controls", 4f);   // OnLateUpdate binds him and turns the controls on
            }
            if (BullyInput.KeyDown(BullyInput.VK_F8) && BullyInput.BullyMode && _srcAnim != null)
            {
                _camWanted = !_camWanted;
                _barUntil = Time.unscaledTime + 5f;
                SetCam(_camWanted);
            }
            // the phone and every other Schedule I screen work in first person: drop to it while one is open
            if (_camWanted && BullyInput.BullyMode && _srcAnim != null)
            {
                bool ui = SchedOneUiOpen();
                if (ui && _camOn) SetCam(false, quiet: true);
                else if (!ui && !_camOn) SetCam(true, quiet: true);
            }
            if (BullyInput.BullyMode && _kit != null && Player.Local != null && _rt != null)
            {
                try { if (!BullyInput.GameHasControl) _kit.Update(Player.Local); _kit.RunTimers(); }
                catch (Exception e) { Trace($"kit: {e}"); }
            }
            if (BullyInput.KeyDown(BullyInput.VK_F10) && _rt != null) { DumpPose("F10"); Diag.Dump(Trace); }
            if (_diagAt > 0 && Time.unscaledTime > _diagAt) { _diagAt = -1; Diag.Dump(Trace); }
            if (_dumpAt > 0 && Time.unscaledTime > _dumpAt && _rt != null) { _dumpAt = -1; DumpPose("auto"); }
        }

        public override void OnLateUpdate()
        {
            if (!_on || _broken) return;
            var me = Player.Local;
            if (me == null) { if (_srcAnim != null) Unbind(); return; }
            try
            {
                if (_srcAnim == null && !Bind(me)) return;
                if (_srcAnim == null) return;
                if (!BullyInput.BullyMode) SetBullyMode(true);   // the mod is on: Jimmy comes with his controls (re-binds after a ragdoll keep them)
                // after any ragdoll (death, knockout, respawn) re-bind once he's back up: fresh calibration on both rigs
                bool rag = false;
                try { rag = _srcAvatar != null && _srcAvatar.Ragdolled; } catch { }
                if (_wasRag && !rag) _rebindAt = Time.unscaledTime + 1.5f;
                _wasRag = rag;
                if (_rebindAt > 0 && Time.unscaledTime > _rebindAt)
                {
                    _rebindAt = -1;
                    Trace("bind: re-calibrating after a ragdoll / respawn");
                    Unbind();
                    return;
                }
                if (Time.unscaledTime > _rescan) { HideOriginal(); if (_camOn || _fpBody) SetViewmodelHidden(true); _rescan = Time.unscaledTime + 2f; }
                // v0.22.14 (Austin: "when jimmy first pulls out the skateboard in the schedual1 hot bar it shows 2 times in 3rd
                // person"): a newly equipped item's first-person model hangs off the camera and stayed visible until the 2 s
                // rescan; the camera's children are now checked every 0.2 s
                else if ((_camOn || _fpBody || Skate.Riding || Vehicles.Riding) && Time.unscaledTime > _vmScan) { _vmScan = Time.unscaledTime + 0.2f; HideCameraItems(); }
                // Jimmy stands where Schedule I's animator stands and wears its pose. He's not parented to it: he
                // lives across scene loads instead of dying with the player object.
                _rt.Apply();
                _rides ??= new Vehicles { Trace = Trace, Say = (s, t) => Say(s, t) };
                if (!BullyInput.BullyMode && Vehicles.Riding) _rides.ForceOff(me);
                _rides.Kit = _kit;
                _rides.Update(me, _jimmy.Root.transform, _anim, input: BullyInput.BullyMode && !BullyInput.GameHasControl);   // native controls / a game screen: no mod keys
                _shop ??= new Shop { Trace = Trace, Say = (s, t) => Say(s, t), Rides = _rides, GetKit = () => _kit };
                _shop.SetActive(BullyInput.BullyMode);
                _shop.Update(me, input: BullyInput.BullyMode && !BullyInput.GameHasControl && !Vehicles.Riding);
                _wardrobe ??= new Wardrobe { Trace = Trace, Jimmy = () => _jimmy };
                _wardrobe.Update(me, input: BullyInput.BullyMode && !BullyInput.GameHasControl && !Vehicles.Riding && !Shop.AtCounter);
                WatchSaves();
                if (Wardrobe.Open) EnsureHooks();
                if (_anim == null) _anim = new AnimLayer(_jimmy.ByName);
                _anim.Apply(Time.deltaTime);
                _rides?.LateFit(_jimmy.Root.transform);            // the bike's pedals under Jimmy's feet (v0.17.1)
                _skate?.LateFit(_jimmy.Root.transform, _srcAnim);  // his feet where Schedule I's skater's feet are (v0.21.1)
                {   // v0.22.26: Jimmy's feet on the ground (not on a ride / board, not ragdolled)
                    bool down = false; try { down = _srcAvatar != null && _srcAvatar.Ragdolled; } catch { }
                    _plant?.Apply(me.transform.root, Time.deltaTime, !Vehicles.Riding && !Skate.Riding && !down);
                }
                try { _fx?.Apply(); }
                catch (Exception e) { Trace($"fx: {e.Message}; effects off until the next bind"); _fx = null; }
                NpcReact.Update(Time.deltaTime);
                Cast.On = true;
                try { Cast.Update(); } catch (Exception e) { Trace($"cast: {e.Message}"); }   // never takes Jimmy down with it
                if (BullyInput.BullyMode)
                {
                    _skate ??= new Skate { Trace = Trace };
                    _skate.Update(me, _anim);
                }
                else if (_skate != null) _skate.Off(_anim);
                bool fp = View();
                if (BullyInput.BullyMode && _kit != null && !Skate.Riding && !BullyInput.GameHasControl)
                {
                    if (_kit.Aiming) AimPitch(1f);
                    else if (fp && !Vehicles.Riding) AimPitch(0.55f);     // first person: the body leans with the look
                    _kit.LateHold();
                }
                else if (fp && !Vehicles.Riding) AimPitch(0.55f);
                MouthJimmy();
                SyncHeldItems();
                BullyInput.LateUpdate();
                UpdateHud(me);
            }
            catch (Exception e)
            {
                _broken = true;
                Unbind();
                LoggerInstance.Error($"Jimmy stopped: {e}");
                Say("Jimmy hit an error (see the MelonLoader log); Schedule I's body is back", 30f);
            }
        }

        bool Bind(Player me)
        {
            _srcAvatar = me.Avatar;
            if (_srcAvatar == null) return false;
            try { if (_srcAvatar.Ragdolled) return false; } catch { }    // dead / knocked down: wait until he's back up
            Animator anim = null;
            foreach (var a in _srcAvatar.GetComponentsInChildren<Animator>(true))
                if (a.avatar != null && a.avatar.isHuman) { anim = a; break; }
            if (anim == null)
            {
                _broken = true;
                Say("Schedule I's player has no humanoid animator: can't retarget (log has details)", 30f);
                foreach (var a in _srcAvatar.GetComponentsInChildren<Animator>(true))
                    LoggerInstance.Warning($"animator {a.name}: avatar={(a.avatar != null ? a.avatar.name : "none")} human={(a.avatar != null && a.avatar.isHuman)}");
                return false;
            }
            Trace($"bind: animator '{anim.name}' avatar '{anim.avatar.name}'");
            if (_jimmy == null && !JimmyModel.Exported)
            {
                _broken = true;
                Say("CyberBull-E: no Bully files yet. Run the CyberBull-E Launcher: it builds them from your own copy of Bully.", 30f);
                Trace($"bind: nothing exported at {JimmyModel.DataDir}; Schedule I stays stock");
                return false;
            }
            if (_jimmy == null)
            {
                var anyMat = FirstBodyRenderer()?.sharedMaterial;
                _jimmy = JimmyModel.Build(anyMat, Trace);
                _plant = _jimmy != null ? new FootPlant(_jimmy) : null;            // v0.22.26: measured in the bind pose
                LoggerInstance.Msg($"Jimmy built: {_jimmy.Bones.Length} bones, {_jimmy.Renderer.sharedMesh.vertexCount} verts");
                NpcReact.Trace = Trace;
                NpcReact.SkeletonSource = () => _jimmy?.Root;
                for (int i = 0; i < _jimmy.Bones.Length; i++) NpcReact.BindPose[_jimmy.Bones[i].name] = (_jimmy.BindPos[i], _jimmy.BindRot[i]);
            }
            var si = new Dictionary<HumanBodyBones, Transform>();
            foreach (var (hb, _) in Retarget.Pairs)
            {
                var t = anim.GetBoneTransform(hb);
                if (t != null) si[hb] = t;
            }
            Trace($"bind: Schedule I skeleton has {si.Count}/{Retarget.Pairs.Length} of the mapped bones");
            if (!si.ContainsKey(HumanBodyBones.Hips)) throw new Exception("Schedule I's skeleton has no hips bone");
            _jimmy.Root.SetActive(true);
            // Calibrate from Jimmy's REST pose. A rebind after death / respawn used to calibrate against whatever pose he
            // was frozen in, which warped every bone (Austin: "dying and respawning warped the player model").
            for (int i = 0; i < _jimmy.Bones.Length; i++) { _jimmy.Bones[i].localPosition = _jimmy.BindPos[i]; _jimmy.Bones[i].localRotation = _jimmy.BindRot[i]; }
            _anim?.Stop();
            _rt = new Retarget(anim.transform, si, _jimmy.Root.transform, _jimmy.Humanoid());
            Trace($"bind: retarget linked {_rt.Count} bones");
            // v0.22.6: this save slot's outfit (saved with the game's own saves)
            try { Outfit.UseSave(Outfit.KeyOf(Il2CppScheduleOne.Persistence.LoadManager.Instance?.LoadedGameFolderPath), _jimmy); }
            catch (Exception e) { Trace($"outfit: {e.Message}"); }
            try { _fx = new JimmyEffects(_srcAvatar, me, anim.transform, _jimmy); }
            catch (Exception e) { _fx = null; Trace($"fx: couldn't hook the avatar's effects ({e.Message}); Jimmy runs without them"); }
            _dumpAt = Time.unscaledTime + 3f;
            _diagAt = Time.unscaledTime + 5f;
            _srcAnim = anim;
            var body = FirstBodyRenderer();
            // Default layer, not the body layer: v0.5.1 added layer 19 to the camera for Jimmy and that layer also holds
            // the no-material boxes and the first-person items (Austin: the game's own skateboard third person never
            // shows them). The camera's mask is never touched now; first person hides Jimmy by drawing shadow only.
            _jimmy.Root.layer = 0;
            _jimmy.Root.SetActive(true);
            HideOriginal();
            if (!SchedOneUiOpen()) LearnPlayCursor();
            if (!BullyInput.BullyMode) { Say("You're Jimmy Hopkins now.", 4f); _barUntil = Time.unscaledTime + 8f; }
            LoggerInstance.Msg($"retargeting from '{anim.name}' (avatar '{anim.avatar.name}')");
            return true;
        }

        SkinnedMeshRenderer FirstBodyRenderer()
        {
            if (_srcAvatar == null) return null;
            foreach (var r in _srcAvatar.GetComponentsInChildren<SkinnedMeshRenderer>(true)) return r;
            return null;
        }

        /// <summary>Hide Schedule I's body (skin, clothes, hair, eyes) but not what's in your hands.</summary>
        void HideOriginal()
        {
            if (_srcAvatar == null) return;
            Transform lh = _srcAvatar.LeftHandContainer, rh = _srcAvatar.RightHandContainer;
            ShadowCastingMode mode = ShadowCastingMode.On;
            bool sawBody = false;
            foreach (var r in _srcAvatar.GetComponentsInChildren<Renderer>(true))
            {
                if (_jimmy != null && r.transform.IsChildOf(_jimmy.Root.transform)) continue;
                if ((lh != null && r.transform.IsChildOf(lh)) || (rh != null && r.transform.IsChildOf(rh))) continue;
                // the game's drug-effect particles (stink, vomit, fire, zap, anti-grav, foggy) and blood ride on this
                // avatar; v0.10 hid them with the body, so no effect ever showed on Jimmy (v0.11 fix)
                if (r.TryCast<ParticleSystemRenderer>() != null || r.TryCast<TrailRenderer>() != null || r.TryCast<LineRenderer>() != null) continue;
                if (!sawBody && r.TryCast<SkinnedMeshRenderer>() != null && !r.forceRenderingOff)
                {
                    mode = r.shadowCastingMode;              // first person: the game draws its body as shadow only
                    sawBody = true;
                }
                if (!r.forceRenderingOff) { r.forceRenderingOff = true; _hidden.Add(r); }
            }
            if (sawBody) _bodyMode = mode;
            ApplyJimmyShadows();
        }

        ShadowCastingMode _bodyMode = ShadowCastingMode.On;

        void ApplyJimmyShadows()
        {
            if (_jimmy == null) return;
            // third person (F8, or the game's own skateboard camera): all of Jimmy. First person with the mod on: his
            // headless body is drawn (GTA V style) and the full one only casts the shadow, head included. The game's own
            // first person (mod off, or a Schedule I screen open): his shadow only, like the body he replaces.
            bool full = _camOn || Skate.Riding || Vehicles.Riding || Wardrobe.Open;
            Outfit.View(_jimmy, full, _fpBody && !full);
            _jimmy.Renderer.shadowCastingMode = full ? ShadowCastingMode.On : ShadowCastingMode.ShadowsOnly;
            if (_jimmy.FirstPerson != null) _jimmy.FirstPerson.forceRenderingOff = !(_fpBody && !full);
            _kit?.SetHeldVisible(full || _fpBody);
        }

        // -- first person as Jimmy (v0.13.2, Austin: "first person mode you cant see jimmys hands" / "allow jimmys hands
        // to use schedual1 items"). FIRST-PERSON-DESIGN.md steps 2 + 3, first cut: the game's own camera (a little
        // forward of his collar while it renders), his headless body, the spine leaning with the look, Schedule I's
        // first-person arms hidden and its held items carried in Jimmy's hands (SyncHeldItems).
        bool _fpBody, _wasSkating;

        /// <summary>Decide this frame's view; returns true while first person shows Jimmy's own body.</summary>
        bool View()
        {
            bool want = BullyInput.BullyMode && !_camOn && !Skate.Riding && !Vehicles.Riding && !Wardrobe.Open && _jimmy?.FirstPerson != null && !SchedOneUiOpen();
            BullyInput.FirstPerson = want && !Vehicles.Riding;    // right click = Bully's punch in first person (v0.18)
            bool forcedTp = Skate.Riding || Vehicles.Riding;
            if (forcedTp != _wasSkating)
            {   // v0.21.2: forced third person on the board: render hooks on, the first-person arms out of the shot
                _wasSkating = forcedTp;
                if (_wasSkating) EnsureHooks();
                SetViewmodelHidden(_camOn || _fpBody || _wasSkating);
            }
            if (want != _fpBody)
            {
                _fpBody = want;
                if (want) EnsureHooks();
                SetViewmodelHidden(_camOn || _fpBody || forcedTp);
                Trace($"view: first person {(want ? "as Jimmy (his body + hands, Schedule I's arms hidden)" : "back to Schedule I's own")}");
            }
            ApplyJimmyShadows();
            return _fpBody;
        }

        // ---- Schedule I's held items in Jimmy's hands (v0.18.1: shown by visual twins, the game's own items untouched;
        // see HandItems). Calibrated once against a pose Retarget made alone, so an untouched pose moves nothing but the
        // hand position.
        HandItems _items;
        float _itemScan;
        string _lastItems = "";

        void SyncHeldItems()
        {
            if (_srcAvatar == null || _jimmy == null || _srcAnim == null) return;
            if (_items == null)
            {
                _items = new HandItems(hideOriginals: false, trace: Trace);
                try
                {
                    _items.Add(_srcAvatar.RightHandContainer, _srcAnim.GetBoneTransform(HumanBodyBones.RightHand), _jimmy.ByName.TryGetValue("Root R Hand", out var rh) ? rh : null);
                    _items.Add(_srcAvatar.LeftHandContainer, _srcAnim.GetBoneTransform(HumanBodyBones.LeftHand), _jimmy.ByName.TryGetValue("Root L Hand", out var lh) ? lh : null);
                }
                catch { }
            }
            if (_anim == null || _anim.Playing == null) _items.Calibrate();      // only from a pose Retarget made alone
            _items.Suspended = Skate.Riding;                                     // v0.21: the board is under his feet, not in his hand
            bool rescan = Time.unscaledTime >= _itemScan;
            if (rescan) _itemScan = Time.unscaledTime + 0.25f;
            var n = _items.Sync(rescan);
            if (n != null && n != _lastItems) { _lastItems = n; if (n.Length > 0) Trace($"held: Jimmy carries {n}"); }
        }

        /// <summary>v0.17 (Austin: "make sure when they smoke or drink it actually goes to their mouth"): when Schedule I
        /// brings the player's hand to its face, Jimmy's hand goes to his (BodyFit.Mouth); SyncHeldItems then carries the item.</summary>
        void MouthJimmy()
        {
            if (_srcAnim == null || _jimmy == null || _lastItems.Length == 0) return;   // only with a Schedule I item in hand
            var b = _jimmy.ByName;
            Transform T(string n) => b.TryGetValue(n, out var t) ? t : null;
            var siHead = _srcAnim.GetBoneTransform(HumanBodyBones.Head);
            BodyFit.Mouth(siHead, _srcAnim.GetBoneTransform(HumanBodyBones.LeftHand), T("Root Head"), T("Root L UpperArm"), T("Root L Forearm"), T("Root L Hand"));
            BodyFit.Mouth(siHead, _srcAnim.GetBoneTransform(HumanBodyBones.RightHand), T("Root Head"), T("Root R UpperArm"), T("Root R Forearm"), T("Root R Hand"));
        }

        void ReleaseHeldItems()
        {
            _items?.Release();
            _items = null;
            _lastItems = "";
        }

        void ShowOriginal()
        {
            foreach (var r in _hidden)
                if (r != null) r.forceRenderingOff = false;
            _hidden.Clear();
        }

        // -- F8: third-person camera ------------------------------------------------------------------------------
        // Third person WITHOUT a camera of our own (v0.5.0). A second camera never matched Schedule I's: wrong renderer /
        // camera data, so tree Billboards and no-material helper boxes came out magenta (v0.3-v0.4.2, even with the same
        // culling mask). Now Schedule I's own camera is moved over Jimmy's shoulder right before it renders and put back
        // right after, so the game's own logic never sees the move and everything renders exactly as the game renders it.
        bool _hooked;
        FootPlant _plant;
        Vector3 _tpPos, _tpOff, _savedPos; Quaternion _savedRot; int _savedMask; bool _moved;
        readonly Dictionary<IntPtr, (Camera cam, int mask)> _blanked = new();
        int _lastHidden = -1;

        void EnsureHooks()
        {
            if (!_hooked)
            {
                try
                {
                    RenderPipelineManager.beginCameraRendering += (Il2CppSystem.Action<ScriptableRenderContext, Camera>)new Action<ScriptableRenderContext, Camera>(OnBeginCamera);
                    RenderPipelineManager.endCameraRendering += (Il2CppSystem.Action<ScriptableRenderContext, Camera>)new Action<ScriptableRenderContext, Camera>(OnEndCamera);
                    _hooked = true;
                    Trace("camera: third person rides on Schedule I's own camera (render hooks on)");
                }
                catch (Exception e) { Trace($"camera: render hook failed: {e.Message}"); }
            }
        }

        void SetCam(bool on, bool quiet = false)
        {
            if (on) EnsureHooks();
            if (on && !_camOn && Camera.main != null) _tpPos = Camera.main.transform.position;
            _camOn = on;
            SetViewmodelHidden(on || _fpBody);
            ApplyJimmyShadows();
            if (!quiet) Say(on ? "third-person camera (F8: back to first person)" : "first person");
        }

        void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            if (_srcAnim == null || cam == null) return;
            // v0.22 the wardrobe: the camera in front of Jimmy, looking at him
            if (Wardrobe.Open && Wardrobe.ViewTarget != null && cam == Camera.main)
            {
                var wt = cam.transform;
                _savedPos = wt.position; _savedRot = wt.rotation; _savedMask = cam.cullingMask; _moved = true;
                var jt = Wardrobe.ViewTarget;
                var jf = new Vector3(jt.forward.x, 0, jt.forward.z); if (jf.sqrMagnitude < 1e-4f) jf = Vector3.forward; jf.Normalize();
                var eye = jt.position + jf * 2.1f + Vector3.up * 1.25f;
                wt.SetPositionAndRotation(eye, Quaternion.LookRotation(jt.position + Vector3.up * 1.0f - eye, Vector3.up));
                return;
            }
            // v0.21.2 (Austin: "when hes in first person force to 3rd when riding a skateboard"): skating always gets the
            // over-the-shoulder camera below (it used to leave the game's own skateboard camera in charge)
            bool tp = _camOn || Skate.Riding || Vehicles.Riding;              // v0.22.13: every ride forces third person
            if (!tp && _fpBody && cam == Camera.main)
            {
                // first person as Jimmy: the game's eye, 9 cm forward (flat) so the near plane clears his collar and
                // shoulders; small enough that the crosshair / interaction rays (from the game's own position) still agree
                var ft = cam.transform;
                _savedPos = ft.position; _savedRot = ft.rotation; _savedMask = cam.cullingMask; _moved = true;
                var f = new Vector3(ft.forward.x, 0, ft.forward.z);
                // v0.21.5: looking down, ease further forward (up to 0.27 m) so the eye clears his chest, as GTA V does;
                // looking ahead it stays 9 cm, so the crosshair still agrees with the game's own aim
                float down = Mathf.Clamp01(-ft.forward.y / 0.9f);
                // v0.22.9 (Austin: "when first person and crouching the body blocks vision"): crouched, his knees and chest
                // come up around the eye; the lower the eye sits over his feet (1.6 m standing, ~1 m crouched), the further
                // ahead of him it goes (up to 0.32 m more)
                float eye = _srcAnim != null ? _savedPos.y - _srcAnim.transform.position.y : 1.6f;
                float crouch = Mathf.Clamp01((1.45f - eye) / 0.4f);
                if (f.sqrMagnitude > 1e-4f) ft.position = _savedPos + f.normalized * (0.09f + 0.18f * down * down + 0.32f * crouch);
                return;
            }
            if (!tp) return;
            if (cam != Camera.main)
            {
                // other game cameras are blanked in the chosen third person; never while skating (if the game's board view
                // is its own camera, blanking it would black the screen out)
                if (!Skate.Riding && cam.cameraType == CameraType.Game && cam.cullingMask != 0) { _blanked[cam.Pointer] = (cam, cam.cullingMask); cam.cullingMask = 0; }
                return;
            }
            var t = cam.transform;
            _savedPos = t.position; _savedRot = t.rotation; _savedMask = cam.cullingMask; _moved = true;
            var me = _srcAnim.transform;
            var look = t.forward;
            var flat = new Vector3(look.x, 0, look.z);
            if (flat.sqrMagnitude < 1e-4f) flat = me.forward;
            flat.Normalize();
            // v0.22.17 (Austin: "the speed makes the player model jittery"): on a ride the camera rides on Jimmy (placed
            // from the ride's interpolated body every frame), not on Schedule I's teleported player; and it eases only its
            // offset from him, never his travel. Easing the world position lagged ~1.7 m at 85 km/h, and every uneven
            // frame time turned that lag into shake.
            var follow = Vehicles.Riding && _jimmy?.Root != null ? _jimmy.Root.transform : me;
            var pivot = follow.position + Vector3.up * 1.55f + Vector3.Cross(Vector3.up, flat) * 0.45f;   // over the right shoulder
            var want = pivot - look.normalized * 3.0f + Vector3.up * 0.25f;
            if (Physics.Linecast(pivot, want, out var hit, ~0, QueryTriggerInteraction.Ignore) && !hit.transform.IsChildOf(me.root)
                && !hit.transform.IsChildOf(follow.root) && (Vehicles.RideRoot == null || !hit.transform.IsChildOf(Vehicles.RideRoot)))
                want = hit.point + (pivot - want).normalized * 0.2f;
            var off = want - pivot;
            if ((_tpOff - off).sqrMagnitude > 25f) _tpOff = off;          // first frame of a forced third person: no long sweep
            _tpOff = Vector3.Lerp(_tpOff, off, 1f - Mathf.Exp(-14f * Time.deltaTime));
            _tpPos = pivot + _tpOff;
            t.SetPositionAndRotation(_tpPos, _savedRot);
        }

        void OnEndCamera(ScriptableRenderContext ctx, Camera cam)
        {
            if (cam == null) return;
            if (_blanked.TryGetValue(cam.Pointer, out var bl)) { cam.cullingMask = bl.mask; _blanked.Remove(cam.Pointer); return; }
            if (!_moved || cam != Camera.main) return;
            cam.transform.SetPositionAndRotation(_savedPos, _savedRot);
            cam.cullingMask = _savedMask;
            _moved = false;
        }

        /// <summary>Project a world point the way the third-person view sees it (the HUD lock ring).</summary>
        public Vector3 ViewPoint(Vector3 world)
        {
            var cam = Camera.main;
            if (cam == null) return Vector3.zero;
            if (!_camOn) return cam.WorldToScreenPoint(world);
            var t = cam.transform;
            var p = t.position;
            t.position = _tpPos;
            var sp = cam.WorldToScreenPoint(world);
            t.position = p;
            return sp;
        }

        float _vmScan;
        /// <summary>The light check: new renderers under the main camera (a just-equipped item's first-person model) hidden too.</summary>
        void HideCameraItems()
        {
            var main = Camera.main;
            if (main == null) return;
            foreach (var r in main.GetComponentsInChildren<Renderer>(true))
                if (r != null && !r.forceRenderingOff) { r.forceRenderingOff = true; _hiddenViewmodel.Add(r); }
        }

        /// <summary>Schedule I's first-person arms (ViewmodelAvatar + first-person items) float in front of the first-person
        /// camera; seen from the third-person camera they're the "tiny hands popping up over him".</summary>
        void SetViewmodelHidden(bool hide)
        {
            foreach (var r in _hiddenViewmodel)
                if (r != null) r.forceRenderingOff = false;
            _hiddenViewmodel.Clear();
            if (!hide) return;
            var main = Camera.main;
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<Il2CppScheduleOne.PlayerScripts.ViewmodelAvatar>()))
            {
                var c = o.TryCast<Component>();
                if (c == null) continue;
                foreach (var r in c.GetComponentsInChildren<Renderer>(true))
                    if (!r.forceRenderingOff) { r.forceRenderingOff = true; _hiddenViewmodel.Add(r); }
            }
            if (main != null)                                   // first-person items hang off the camera too
                foreach (var r in main.GetComponentsInChildren<Renderer>(true))
                    if (!r.forceRenderingOff) { r.forceRenderingOff = true; _hiddenViewmodel.Add(r); }
            if (_hiddenViewmodel.Count != _lastHidden) Trace($"camera: hid {_hiddenViewmodel.Count} first-person renderers");
            _lastHidden = _hiddenViewmodel.Count;
        }

        /// <summary>Both skeletons' joint positions (relative to the character root) to UserData\BullyLink.pose.txt,
        /// so the pose can be rendered and checked outside the game (tools/render_pose.py).</summary>
        void DumpPose(string why)
        {
            var path = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "BullyLink.pose.txt");
            var sb = new System.Text.StringBuilder();
            var root = _srcAnim.transform;
            sb.AppendLine($"# {why} {DateTime.Now:HH:mm:ss} root {root.position} rot {root.rotation.eulerAngles}");
            foreach (var t in _jimmy.Bones)
            {
                var lp = root.InverseTransformPoint(t.position);
                var lr = Quaternion.Inverse(root.rotation) * t.rotation;
                sb.AppendLine($"J\t{t.name}\t{lp.x:F4}\t{lp.y:F4}\t{lp.z:F4}\t{lr.x:F5}\t{lr.y:F5}\t{lr.z:F5}\t{lr.w:F5}");
            }
            foreach (var (hb, _) in Retarget.Pairs)
            {
                var t = _srcAnim.GetBoneTransform(hb);
                if (t == null) continue;
                var lp = root.InverseTransformPoint(t.position);
                sb.AppendLine($"S\t{hb}\t{lp.x:F4}\t{lp.y:F4}\t{lp.z:F4}");
            }
            var r = _jimmy.Renderer;
            sb.AppendLine($"# renderer enabled={r.enabled} active={r.gameObject.activeInHierarchy} layer={r.gameObject.layer} bounds={r.bounds.center}/{r.bounds.size} mats={string.Join(",", System.Linq.Enumerable.Select(r.sharedMaterials, m => m == null ? "null" : m.shader.name))}");
            File.WriteAllText(path, sb.ToString());
            Say($"pose saved ({why})", 3f);
        }

        /// <summary>The three keys along the top with their live state; shows for a few seconds after load and after a
        /// toggle, then gets out of the way.</summary>
        void ControlsBar()
        {
            if (Time.unscaledTime > _barUntil || SchedOneUiOpen()) return;
            string mod = _on ? "<color=#FFD24A>BullyLink ON</color>" : "<color=#AAAAAA>BullyLink off</color>";
            string cam = !_on ? "<color=#AAAAAA>-</color>" : _camWanted ? "<color=#7CFC7C>3rd person</color>" : "<color=#9AD0FF>1st person</color>";
            string text = $"<b>F6</b> {mod}   <b>F8</b> {cam}   <b>F10</b> save pose";
            var r = new Rect(Screen.width / 2f - 230, 4, 460, 26);
            GUI.Box(r, "");
            GUI.Label(new Rect(r.x + 10, r.y + 4, r.width - 20, 22), text);
        }

        // v0.22.6: Schedule I saved (autosave, sleep, manual): SaveManager.SecondsSinceLastSave starts again from zero
        float _lastSinceSave = -1f;
        Il2CppScheduleOne.Persistence.SaveManager _saveMgr;
        void WatchSaves()
        {
            try
            {
                if (_saveMgr == null) _saveMgr = UnityEngine.Object.FindObjectOfType<Il2CppScheduleOne.Persistence.SaveManager>();
                if (_saveMgr == null) return;
                float s = _saveMgr.SecondsSinceLastSave;
                if (_lastSinceSave >= 0f && s + 1f < _lastSinceSave) Outfit.Commit();
                _lastSinceSave = s;
            }
            catch { _saveMgr = null; }
        }

        public override void OnFixedUpdate() { try { _rides?.FixedStep(); } catch { } }

        public override void OnGUI()
        {
            ControlsBar();
            _shop?.OnGUI();
            _wardrobe?.OnGUI();
            if (Time.unscaledTime < _statusUntil)
                GUI.Label(new Rect(12, 34, 1200, 24), $"BullyLink · {_status}");
            var prompt = _rides?.Prompt ?? _wardrobe?.Prompt;
            if (!string.IsNullOrEmpty(prompt) && !SchedOneUiOpen())
                GUI.Label(new Rect(Screen.width / 2f - 300, Screen.height - 110, 600, 24), $"<b>{prompt}</b>");

        }
    }
}
