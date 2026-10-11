"""Every piece of Jimmy's clothing from Austin's Bully install, for the wardrobe (Wardrobe.cs; Austin 2026-10-10: "in
every owned property put a wardrobe from bully and pull every possible outfit for jimmy and make it saveable").
Config/dat/Cloths.dat lists 460 items (ModelName, ClothingType = slot, Style, Warmth, Flags). Each item's NIF in
Stream/World.img holds ONE mesh skinned to Jimmy's own bone names; its textures sit in the matching .nft.

Per item: assets/out/clothes/<name>.bytes  ("CLTH" u32 1, str slot, str style, str flags, u32 nmeshes; per mesh: str
texture, u32 nverts, nverts*(3f pos, 3f normal, 2f uv, 4 x (str bone), 4f weight), u32 nidx, nidx*u32), positions in
Unity space at Jimmy's rest pose (Sum w * M_bone . S_bone . v, like export_jimmy), + <name>_<tex>.png textures.
assets/out/clothes/clothes.txt: name <TAB> slot <TAB> style <TAB> flags, the wardrobe's list. Loaded from disk at runtime
(UserData/BullyLink/clothes), not embedded.
python export_clothes.py [name ...]
"""
import struct, sys
from pathlib import Path
import numpy as np
from PIL import Image
from nif import Nif, world_map, xf4, decode_pixels
from imgdir import read
from export_jimmy import C
import paths


def items():
    out = []
    for line in (paths.bully() / "Config" / "dat" / "Cloths.dat").read_text(encoding="latin-1").splitlines():
        if not line or line.startswith("#"): continue
        f = line.split("\t")
        if len(f) >= 2 and f[0].strip():
            out.append((f[0].strip(), f[1].strip(), f[2].strip() if len(f) > 2 else "", f[4].strip() if len(f) > 4 else ""))
    return out


_SKIN = None
def skin_tone():
    """Jimmy's skin colour: the median of his face texture's cheek / jaw area (PLAYER.nft Player_01_d)."""
    global _SKIN
    if _SKIN is None:
        t = Nif(read("PLAYER.nft"))
        for b in t.blocks:
            if b["type"] == "NiSourceTexture" and "Player_01_d" in b["file"]:
                px = decode_pixels(t, t.blocks[b["pixels"]]).astype(np.float32)
                h, w = px.shape[:2]
                patch = px[int(h * 0.62):int(h * 0.72), int(w * 0.18):int(w * 0.30), :3].reshape(-1, 3)
                _SKIN = np.clip(np.median(patch, axis=0) * 1.1, 0, 255)   # the cheeks carry some blush: a touch lighter
        print("skin tone", _SKIN)
    return _SKIN


def skin_fill(px):
    """Bully's body clothing is see-through where skin shows (bare arms, legs); the game paints the skin underneath.
    Fill those texels with Jimmy's skin tone, shaded by the grey the texture keeps there, and make them opaque."""
    px = px.copy()
    if px.shape[2] < 4: return px
    a = px[..., 3:4].astype(np.float32) / 255.0
    if (a < 0.5).mean() < 0.002: return px
    rgb = px[..., :3].astype(np.float32)
    lum = rgb.mean(axis=2, keepdims=True) / 255.0
    shade = np.clip(lum / 0.72, 0.55, 1.15)
    skin = skin_tone()[None, None, :] * shade
    out = rgb * a + skin * (1 - a)
    px[..., :3] = np.clip(out, 0, 255).astype(np.uint8)
    px[..., 3] = 255
    return px


def export(name, slot, style, flags):
    nif = Nif(read(name + ".nif"))
    W = world_map(nif)
    meshes = [b for b in nif.blocks if b["type"] in ("NiTriShape", "NiTriStrips") and b["skin"] >= 0]
    buf = bytearray(b"CLTH") + struct.pack("<I", 1)
    def wstr(s):
        e = s.encode("utf-8"); buf.extend(struct.pack("<H", len(e)) + e)
    wstr(slot); wstr(style); wstr(flags)
    buf += struct.pack("<I", len(meshes))
    texnames = set()
    for sh in meshes:
        d = nif.blocks[sh["data"]]; si = nif.blocks[sh["skin"]]; sd = nif.blocks[si["data"]]
        n = d["nverts"]
        v4 = np.c_[d["verts"], np.ones(n)]
        pos = np.zeros((n, 3)); nrm = np.zeros((n, 3))
        infl = [[] for _ in range(n)]
        for bi, bone in zip(si["bones"], sd["bones"]):
            T = W[bi] @ xf4(bone["xf"])
            for vi, w in bone["weights"]:
                pos[vi] += w * (T @ v4[vi])[:3]
                if d["normals"] is not None:
                    nrm[vi] += w * (T[:3, :3] @ d["normals"][vi])
                infl[vi].append((nif.blocks[bi]["name"], w))
        pos = pos @ C.T
        nrm = nrm @ C.T; nrm /= np.maximum(np.linalg.norm(nrm, axis=1, keepdims=True), 1e-9)
        uv = d["uvs"][0].copy(); uv[:, 1] = 1 - uv[:, 1]
        tris = d["tris"].copy()
        fn = np.cross(pos[tris[:, 1]] - pos[tris[:, 0]], pos[tris[:, 2]] - pos[tris[:, 0]])
        vn = nrm[tris[:, 0]] + nrm[tris[:, 1]] + nrm[tris[:, 2]]
        flip = (fn * vn).sum(1) < 0
        tris[flip] = tris[flip][:, [0, 2, 1]]
        texfile = None
        for p in sh["props"]:
            pb = nif.blocks[p]
            if pb["type"] == "NiTexturingProperty" and pb["slots"][0] >= 0:
                texfile = nif.blocks[pb["slots"][0]]["file"].replace("\\", "/").split("/")[-1][:-4]
        texnames.add(texfile)
        wstr(name + "_" + texfile if texfile else "")
        buf += struct.pack("<I", n)
        for i in range(n):
            inf = sorted(infl[i], key=lambda t: -t[1])[:4]
            tot = sum(w for _, w in inf) or 1.0
            inf = [(b, w / tot) for b, w in inf] + [("", 0.0)] * (4 - len(inf))
            buf += struct.pack("<3f3f2f", *pos[i], *nrm[i], *uv[i])
            for b, _ in inf: wstr(b)
            buf += struct.pack("<4f", *[w for _, w in inf])
        buf += struct.pack("<I", tris.size) + tris.astype("<u4").tobytes()
    (paths.CLOTHES / f"{name}.bytes").write_bytes(bytes(buf))
    tex = Nif(read(name + ".nft"))
    for b in tex.blocks:
        if b["type"] == "NiSourceTexture":
            f = b["file"].replace("\\", "/").split("/")[-1][:-4]
            if f in texnames:
                px = decode_pixels(tex, tex.blocks[b["pixels"]])
                if slot not in ("HAIR", "HEAD"): px = skin_fill(px)
                Image.fromarray(px).save(paths.CLOTHES / f"{name}_{f}.png")
    return len(meshes)


def main(want=None):
    paths.CLOTHES.mkdir(parents=True, exist_ok=True)
    want = set(a.lower() for a in sys.argv[1:]) if want is None else want
    rows, bad = [], []
    for name, slot, style, flags in items():
        if want and name.lower() not in want: continue
        try:
            if export(name, slot, style, flags) > 0: rows.append(f"{name}\t{slot}\t{style}\t{flags}")
            else: bad.append(f"{name}: no skinned mesh")
        except Exception as e:
            bad.append(f"{name}: {e}")
    if not want: (paths.CLOTHES / "clothes.txt").write_text("\n".join(rows) + "\n")
    print(f"exported {len(rows)}; failed {len(bad)}")
    for b in bad[:20]: print("  FAILED", b)
    return len(rows), bad


if __name__ == "__main__":
    main()
