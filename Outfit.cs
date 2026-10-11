// Jimmy's Bully clothes (v0.22; Austin, 2026-10-10: "pull every possible outfit for jimmy and make it saveable").
// tools/export_clothes.py exports all 460 items in Bully's Config/dat/Cloths.dat (hair, hats, tops, bottoms, shoes,
// wrist pieces) into UserData\BullyLink\clothes: each is one mesh skinned to Jimmy's own bone names, so it's built as a
// SkinnedMeshRenderer on Jimmy's skeleton with his bind matrices, and the part of his own outfit it replaces (the vest +
// arms, the jeans, the boots) is blanked. Bare skin on tops / shorts was baked in at export (Bully paints it at runtime).
// v0.22.6 (Austin: "saved outfits dont save if the game dosnt save" -> "make save outfit save to last save"): the outfit
// belongs to the save slot being played: the wardrobe's Enter writes it to UserData\BullyLink\outfits\<SaveGame_N>.txt at
// once (and every game save writes it again). Loading a save puts on that save's outfit; each save keeps its own.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BullyLink
{
    public static class Outfit
    {
        public static Action<string> Trace = _ => { };
        public static readonly string[] Slots = { "HAIR", "HEAD", "TORSO", "LEGS", "FEET", "LEFT_WRIST", "RIGHT_WRIST" };
        public static readonly Dictionary<string, string> SlotName = new()
        {
            ["HAIR"] = "Hair", ["HEAD"] = "Hat", ["TORSO"] = "Top", ["LEGS"] = "Bottoms", ["FEET"] = "Shoes",
            ["LEFT_WRIST"] = "Left wrist", ["RIGHT_WRIST"] = "Right wrist",
        };
        // Jimmy's own outfit parts (PLAYER.nft): what each slot covers
        static readonly Dictionary<string, string> Covers = new() { ["TORSO"] = "Player_02", ["LEGS"] = "Player_03", ["FEET"] = "Player_04" };

        static readonly string LegacyPath = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "BullyLink.outfit.txt");
        static readonly string Dir = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "BullyLink", "outfits");
        /// <summary>The save slot whose outfit is loaded ("SaveGame_3"), and what Jimmy wears now (not yet saved).</summary>
        public static string SaveKey { get; private set; } = "";
        public static Dictionary<string, string> Current = new();
        static Dictionary<string, List<(string name, string style)>> _items;
        /// <summary>What the loaded save last saved him in, slot -> item ("" or missing = his own clothes / nothing).</summary>
        public static Dictionary<string, string> Saved = new();
        public static bool Available => _items != null && _items.Count > 0;

        public static void Load()
        {
            if (_items != null) return;
            _items = new Dictionary<string, List<(string, string)>>();
            try
            {
                var idx = Path.Combine(JimmyModel.ClothesDir, "clothes.txt");
                if (!File.Exists(idx)) { Trace($"outfit: no clothes at {JimmyModel.ClothesDir} (tools/export_clothes.py)"); return; }
                foreach (var line in File.ReadAllLines(idx))
                {
                    var f = line.Split('\t');
                    if (f.Length < 3) continue;
                    if (!_items.TryGetValue(f[1], out var list)) _items[f[1]] = list = new List<(string, string)>();
                    list.Add((f[0], f[2]));
                }
                int n = 0; foreach (var l in _items.Values) n += l.Count;
                Trace($"outfit: {n} Bully clothing items in {_items.Count} slots (outfits saved per save slot, with the game's saves)");
            }
            catch (Exception e) { Trace($"outfit: load failed: {e.Message}"); }
        }

        public static List<(string name, string style)> Items(string slot) =>
            _items != null && _items.TryGetValue(slot, out var l) ? l : new List<(string, string)>();

        /// <summary>A save folder path -> its slot key ("...\\SaveGame_3" -> "SaveGame_3").</summary>
        public static string KeyOf(string saveFolder) =>
            string.IsNullOrEmpty(saveFolder) ? "" : Path.GetFileName(saveFolder.TrimEnd('\\', '/'));

        static Dictionary<string, string> Read(string path)
        {
            var d = new Dictionary<string, string>();
            if (File.Exists(path))
                foreach (var line in File.ReadAllLines(path))
                {
                    var f = line.Split('\t');
                    if (f.Length >= 2 && !line.StartsWith("#")) d[f[0]] = f[1];
                }
            return d;
        }

        /// <summary>The outfit this save slot last saved (the pre-v0.22.6 single outfit moves into the first slot loaded).</summary>
        public static Dictionary<string, string> ForSave(string key)
        {
            var path = Path.Combine(Dir, key + ".txt");
            if (!File.Exists(path) && File.Exists(LegacyPath) && key.Length > 0)
            {
                try { Directory.CreateDirectory(Dir); File.Move(LegacyPath, path); Trace($"outfit: the old single outfit now belongs to {key}"); } catch { }
            }
            return Read(path);
        }

        /// <summary>A save slot loaded: its saved outfit goes on (switching saves swaps outfits).</summary>
        public static void UseSave(string key, JimmyModel j)
        {
            if (key == SaveKey) return;
            SaveKey = key;
            Saved = ForSave(key);
            Current = new Dictionary<string, string>(Saved);
            Apply(j, Current);
            Trace($"outfit: {key}: {(Saved.Count == 0 ? "Jimmy's own clothes" : string.Join(", ", Saved.Values))}");
        }

        /// <summary>The wardrobe's Enter: worn now, written when the game next saves.</summary>
        public static void Wear(Dictionary<string, string> outfit) { Current = new Dictionary<string, string>(outfit); }

        /// <summary>Schedule I just saved: write what he's wearing for this slot.</summary>
        public static void Commit()
        {
            if (SaveKey.Length == 0 || !Available) return;
            Saved = new Dictionary<string, string>(Current);
            try
            {
                Directory.CreateDirectory(Dir);
                var lines = new List<string> { "# BullyLink outfit for this save: slot <TAB> Bully clothing item (Config/dat/Cloths.dat model name)" };
                foreach (var kv in Saved) if (!string.IsNullOrEmpty(kv.Value)) lines.Add($"{kv.Key}\t{kv.Value}");
                File.WriteAllLines(Path.Combine(Dir, SaveKey + ".txt"), lines);
                Trace($"outfit: saved with the game ({SaveKey}, {lines.Count - 1} items)");
            }
            catch (Exception e) { Trace($"outfit: save failed: {e.Message}"); }
        }

        /// <summary>Back to the menu: the next load reads its slot fresh.</summary>
        public static void Unload() { SaveKey = ""; }

        /// <summary>Dress Jimmy (or the menu's Jimmy) in this outfit: build what's new, take off what's gone, blank the
        /// parts of his own clothes the worn items cover.</summary>
        public static void Apply(JimmyModel j, Dictionary<string, string> outfit)
        {
            if (j == null || j.Root == null || !Available) return;
            foreach (var slot in Slots)
            {
                outfit.TryGetValue(slot, out var want); want ??= "";
                j.Worn.TryGetValue(slot, out var cur);
                if ((cur.item ?? "") == want) continue;
                if (cur.go != null) UnityEngine.Object.Destroy(cur.go);
                j.Worn.Remove(slot);
                if (want.Length == 0) continue;
                try { var go = Build(j, want, slot); if (go != null) j.Worn[slot] = (want, go); }
                catch (Exception e) { Trace($"outfit: {want} failed: {e.Message}"); }
            }
            // Jimmy's own vest / jeans / boots: hidden under whatever covers them
            for (int p = 0; p < j.PartTris.Length; p++)
            {
                bool covered = false;
                foreach (var kv in Covers) if (j.Worn.ContainsKey(kv.Key) && j.PartTex[p] != null && j.PartTex[p].Contains(kv.Value)) covered = true;
                var tris = covered ? new int[0] : j.PartTris[p];
                j.Renderer.sharedMesh.SetTriangles(tris, p);
                // the first-person copy: every part but the head, minus what's covered
                if (j.FirstPerson != null && j.FirstPerson.sharedMesh != null) j.FirstPerson.sharedMesh.SetTriangles(IsHead(j, p) ? new int[0] : tris, p);
            }
        }

        static bool IsHead(JimmyModel j, int p) => j.PartTex[p] != null && j.PartTex[p].Contains("Player_01");

        static string Str(BinaryReader r) => System.Text.Encoding.UTF8.GetString(r.ReadBytes(r.ReadUInt16()));

        // v0.22.7 (Austin: "saved outfit is invisible on the player model"; diag: every worn piece "no material"): only the
        // mesh is cached, and only while it still exists. The menu Jimmy dresses first, and materials made in the menu scene
        // were unloaded with it, so in-world Jimmy wore dead ones. Materials are made fresh per wearing.
        static readonly Dictionary<string, (Mesh mesh, string[] texs)> _cache = new();

        static GameObject Build(JimmyModel j, string item, string slot)
        {
            if (!_cache.TryGetValue(item, out var built) || built.mesh == null)
            {
                using var r = new BinaryReader(new MemoryStream(JimmyModel.Res(item + ".bytes")));
                if (new string(r.ReadChars(4)) != "CLTH") throw new InvalidDataException("not a clothing file");
                r.ReadUInt32(); Str(r); Str(r); Str(r);
                int nm = r.ReadInt32();
                var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var uv = new List<Vector2>(); var bw = new List<BoneWeight>();
                var subs = new List<int[]>(); var texs = new List<string>();
                for (int m = 0; m < nm; m++)
                {
                    texs.Add(Str(r));
                    int nv = r.ReadInt32(), b0 = pos.Count;
                    for (int v = 0; v < nv; v++)
                    {
                        pos.Add(new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
                        nrm.Add(new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
                        uv.Add(new Vector2(r.ReadSingle(), r.ReadSingle()));
                        var bn = new[] { Str(r), Str(r), Str(r), Str(r) };
                        float w0 = r.ReadSingle(), w1 = r.ReadSingle(), w2 = r.ReadSingle(), w3 = r.ReadSingle();
                        int I(string n) => j.BoneIndex.TryGetValue(n, out var k) ? k : 0;
                        bw.Add(new BoneWeight { boneIndex0 = I(bn[0]), boneIndex1 = I(bn[1]), boneIndex2 = I(bn[2]), boneIndex3 = I(bn[3]), weight0 = w0, weight1 = w1, weight2 = w2, weight3 = w3 });
                    }
                    int ni = r.ReadInt32();
                    var idx = new int[ni];
                    for (int k = 0; k < ni; k++) idx[k] = b0 + (int)r.ReadUInt32();
                    subs.Add(idx);
                }
                var mesh = new Mesh { name = "Bully_" + item };
                mesh.vertices = pos.ToArray(); mesh.normals = nrm.ToArray(); mesh.uv = uv.ToArray();
                mesh.boneWeights = bw.ToArray();
                mesh.subMeshCount = subs.Count;
                for (int s = 0; s < subs.Count; s++) mesh.SetTriangles(subs[s], s);
                mesh.bindposes = j.Bindposes;
                mesh.RecalculateBounds();
                built = (mesh, texs.ToArray());
                _cache[item] = built;
            }
            bool cut = slot == "HAIR" || slot == "HEAD";                     // hair strands / brims keep their see-through edges
            var mats = new Material[built.texs.Length];
            for (int s = 0; s < mats.Length; s++)
                mats[s] = built.texs[s].Length == 0 ? JimmyModel.Tint(Color.gray) : cut ? JimmyModel.TexturedCutout(built.texs[s]) : JimmyModel.Textured(built.texs[s]);
            if (Array.Exists(mats, m => m == null)) Trace($"outfit: {item}: no base material to dress it with yet");
            var go = new GameObject("BullyLink_Wear_" + item);
            go.transform.SetParent(j.Root.transform, false);
            go.layer = j.Root.layer;
            var smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = built.mesh;
            smr.bones = j.Bones;
            smr.rootBone = j.ByName.TryGetValue("Root", out var rb) ? rb : j.Bones[0];
            smr.sharedMaterials = mats;
            smr.updateWhenOffscreen = true;
            return go;
        }

        /// <summary>What's worn follows Jimmy's own visibility: full = all drawn (third person); fpBody = his first-person body
        /// shows, so clothes are drawn but hair / hats only cast shadows (or they'd sit in front of the eye); neither = Jimmy is
        /// hidden for a Schedule I screen (pickpocketing, shops, stations...) and so is everything he wears.
        /// v0.22.10 (Austin: "when pickpocketing in 3rd person body gets in the way"): the clothes used to keep drawing
        /// while his body was hidden, hanging around the game's first-person camera.</summary>
        public static void View(JimmyModel j, bool full, bool fpBody)
        {
            if (j == null) return;
            foreach (var kv in j.Worn)
            {
                if (kv.Value.go == null) continue;
                var smr = kv.Value.go.GetComponent<SkinnedMeshRenderer>();
                if (smr == null) continue;
                bool head = kv.Key == "HAIR" || kv.Key == "HEAD";
                bool draw = full || (fpBody && !head);
                smr.shadowCastingMode = draw ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
            }
        }
    }
}
