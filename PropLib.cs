// Bully's item models (slingshot, cherry bomb, stink bomb, itching powder, marble, egg, spud gun, bottle rocket +
// launcher, banana, potato can), exported by tools/export_props.py from Austin's install into props.bytes + PNGs.
// Built at runtime as plain MeshRenderers with the same game-compiled URP material Jimmy wears.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BullyLink
{
    public static class PropLib
    {
        class Part { public Mesh mesh; public Material mat; public string tag, tex; public Vector3 pivot; public bool cut; }
        static Dictionary<string, List<Part>> _props;

        static void Load()
        {
            if (_props != null) return;
            _props = new Dictionary<string, List<Part>>(System.StringComparer.OrdinalIgnoreCase);
            using var r = new BinaryReader(new MemoryStream(JimmyModel.Res("props.bytes")));
            if (new string(r.ReadChars(4)) != "PROP") throw new InvalidDataException("props.bytes");
            uint ver = r.ReadUInt32();
            int n = r.ReadInt32();
            for (int k = 0; k < n; k++)
            {
                string name = Str(r);
                int np = r.ReadInt32();
                var parts = new List<Part>();
                for (int p = 0; p < np; p++)
                {
                    string tex = Str(r);
                    string tag = ver >= 2 ? Str(r) : "";
                    var pivot = ver >= 2 ? new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()) : Vector3.zero;
                    int nv = r.ReadInt32();
                    var pos = new Vector3[nv]; var nrm = new Vector3[nv]; var uv = new Vector2[nv];
                    for (int i = 0; i < nv; i++)
                    {
                        pos[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        nrm[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        uv[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
                    }
                    int ni = r.ReadInt32();
                    var idx = new int[ni];
                    for (int i = 0; i < ni; i++) idx[i] = (int)r.ReadUInt32();
                    var mesh = new Mesh { name = name };
                    mesh.vertices = pos; mesh.normals = nrm; mesh.uv = uv;
                    mesh.SetTriangles(idx, 0);
                    mesh.RecalculateBounds();
                    // see-through textures (the bike's spoked wheels) need an alpha-cutout material, or they draw black squares
                    bool cut = tex.IndexOf("wheel", StringComparison.OrdinalIgnoreCase) >= 0 || tex.IndexOf("spoke", StringComparison.OrdinalIgnoreCase) >= 0;
                    parts.Add(new Part { mesh = mesh, tex = tex, cut = cut, tag = tag, pivot = pivot });
                }
                _props[name] = parts;
            }
        }

        static string Str(BinaryReader r) => System.Text.Encoding.UTF8.GetString(r.ReadBytes(r.ReadUInt16()));

        /// <summary>A new GameObject showing Bully's model `name` (null if it isn't in the pack).</summary>
        public static GameObject Make(string name, float scale = 1f)
        {
            Load();
            if (!_props.TryGetValue(name, out var parts)) return null;
            var go = new GameObject("Bully_" + name);
            foreach (var p in parts)
            {
                // v0.22.8 (Austin: "spud gun just drains ammo nothing happens"; diag: the held spud gun "no material"): the
                // materials were made once at the first load and never checked; made before a base material existed, or
                // unloaded with a scene, the prop drew nothing. Made again whenever they're missing or gone.
                if (p.mat == null) p.mat = p.tex.Length == 0 ? JimmyModel.Tint(Color.gray) : p.cut ? JimmyModel.TexturedCutout(p.tex) : JimmyModel.Textured(p.tex);
                // spinning parts (wheels, crank) sit on their own pivot (hub) so they can turn: named by their tag
                var child = new GameObject(string.IsNullOrEmpty(p.tag) ? name + "_part" : p.tag);
                child.transform.SetParent(go.transform, false);
                child.transform.localPosition = p.pivot;
                child.AddComponent<MeshFilter>().sharedMesh = p.mesh;
                var mr = child.AddComponent<MeshRenderer>();
                mr.sharedMaterial = p.mat;
            }
            go.transform.localScale = Vector3.one * scale;
            return go;
        }
    }
}
