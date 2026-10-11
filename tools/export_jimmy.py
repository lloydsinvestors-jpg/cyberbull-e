"""Export Jimmy (PLAYER.nif + PLAYER.nft from Austin's own Bully install) into jimmy.bytes + PNG textures for the
Schedule I mod. Run: python export_jimmy.py   (writes ../assets/out/)

Coordinates. Bully's data is right-handed, z up, Jimmy faces -y, his left is +x (checked by render: the crest lands on
his left chest). Unity is left-handed, y up, forward +z, right +x. So unity = C . bully with
    C = [[-1,0,0],[0,0,1],[0,-1,0]]   (det -1: the handedness flip; triangle winding is re-derived from the normals).

File layout (little-endian):
  "JIMY" u32 version
  u32 nbones; per bone: str name, i32 parent, 3f localPos, 4f localRot(x,y,z,w) [bind pose], 4f tposeLocalRot
  u32 nmeshes; per mesh: str name, str texture, u32 nverts, nverts*(3f pos, 3f normal, 2f uv, 4i bone, 4f weight),
                          u32 nidx, nidx*u32
  str = u16 length + utf-8
"""
import struct
from pathlib import Path
import numpy as np
from PIL import Image
from nif import Nif, world_map, xf4, local, decode_pixels
import paths

OUT = None   # None = paths.OUT at call time; export_cast points it at paths.PEDS
C = np.array([[-1, 0, 0], [0, 0, 1], [0, -1, 0]], float)


def quat(Rm):
    """3x3 proper rotation -> (x, y, z, w), Unity convention (column vectors, v' = R v)."""
    m = Rm; tr = m[0, 0] + m[1, 1] + m[2, 2]
    if tr > 0:
        s = np.sqrt(tr + 1.0) * 2; w = 0.25 * s
        x = (m[2, 1] - m[1, 2]) / s; y = (m[0, 2] - m[2, 0]) / s; z = (m[1, 0] - m[0, 1]) / s
    elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
        s = np.sqrt(1.0 + m[0, 0] - m[1, 1] - m[2, 2]) * 2; w = (m[2, 1] - m[1, 2]) / s
        x = 0.25 * s; y = (m[0, 1] + m[1, 0]) / s; z = (m[0, 2] + m[2, 0]) / s
    elif m[1, 1] > m[2, 2]:
        s = np.sqrt(1.0 + m[1, 1] - m[0, 0] - m[2, 2]) * 2; w = (m[0, 2] - m[2, 0]) / s
        x = (m[0, 1] + m[1, 0]) / s; y = 0.25 * s; z = (m[1, 2] + m[2, 1]) / s
    else:
        s = np.sqrt(1.0 + m[2, 2] - m[0, 0] - m[1, 1]) * 2; w = (m[1, 0] - m[0, 1]) / s
        x = (m[0, 2] + m[2, 0]) / s; y = (m[1, 2] + m[2, 1]) / s; z = 0.25 * s
    q = np.array([x, y, z, w]); return q / np.linalg.norm(q)


def rot_between(a, b):
    a = a / np.linalg.norm(a); b = b / np.linalg.norm(b)
    v = np.cross(a, b); c = a @ b
    if np.linalg.norm(v) < 1e-9:
        return np.eye(3)
    K = np.array([[0, -v[2], v[1]], [v[2], 0, -v[0]], [-v[1], v[0], 0]])
    return np.eye(3) + K + K @ K * (1 / (1 + c))


def ortho(Rm):
    u, _, vt = np.linalg.svd(Rm); return u @ vt


def main(model="PLAYER", out="jimmy"):
    """Any Bully ped on the standard skeleton: PLAYER -> jimmy.bytes; e.g. Carni01 -> carnie.bytes (textures prefixed
    with the output name so two peds never overwrite each other's PNGs)."""
    out_dir = OUT or paths.OUT
    out_dir.mkdir(parents=True, exist_ok=True)
    def src(ext):
        f = paths.RAW / f"{model}.{ext}" if paths.RAW else None
        if f and f.exists(): return f.read_bytes()
        from imgdir import read
        return read(f"{model}.{ext}")
    nif = Nif(src("nif"))
    tex = Nif(src("nft"))
    tpre = "" if out == "jimmy" else out + "_"
    W = world_map(nif)

    # skeleton = the NiNode subtree under "Dummy" (Root, Pelvis, legs, spine, arms, head)
    dummy = nif.named("Dummy")["index"]
    bones, parent = [], {}
    def walk(i, p):
        bones.append(i); parent[i] = p
        for c in nif.blocks[i]["children"]:
            if c >= 0 and nif.blocks[c]["type"] == "NiNode":
                walk(c, i)
    walk(dummy, -1)
    bidx = {b: k for k, b in enumerate(bones)}
    names = [nif.blocks[b]["name"] for b in bones]
    assert len(set(names)) == len(names), "bone names must be unique"

    # bind locals (Bully frame, relative to the parent bone; Dummy relative to the model root)
    Lb = {b: (local(nif.blocks[b]) if parent[b] >= 0 else W[b]) for b in bones}

    # T-pose for Unity's avatar builder: arms straight out sideways, legs straight down (Bully frame: +x = his left)
    Wt = {}
    def worlds(Ls):
        out = {}
        for b in bones:
            out[b] = (out[parent[b]] @ Ls[b]) if parent[b] >= 0 else Ls[b]
        return out
    Lt = {b: Lb[b].copy() for b in bones}
    targets = [("Root L UpperArm", "Root L Forearm", [1, 0, 0]), ("Root L Forearm", "Root L Hand", [1, 0, 0]),
               ("Root R UpperArm", "Root R Forearm", [-1, 0, 0]), ("Root R Forearm", "Root R Hand", [-1, 0, 0]),
               ("Root L Thigh", "Root L Calf", [0, 0, -1]), ("Root L Calf", "Root L Foot", [0, 0, -1]),
               ("Root R Thigh", "Root R Calf", [0, 0, -1]), ("Root R Calf", "Root R Foot", [0, 0, -1])]
    for bn, cn, d in targets:
        Wt = worlds(Lt)
        b = nif.named(bn)["index"]; c = nif.named(cn)["index"]
        cur = Wt[c][:3, 3] - Wt[b][:3, 3]
        Q = rot_between(cur, np.array(d, float))
        newW = Wt[b].copy(); newW[:3, :3] = Q @ Wt[b][:3, :3]
        Lt[b] = np.linalg.inv(Wt[parent[b]]) @ newW
    Wt = worlds(Lt)
    for bn, cn, d in targets:
        b = nif.named(bn)["index"]; c = nif.named(cn)["index"]
        dv = Wt[c][:3, 3] - Wt[b][:3, 3]
        assert np.allclose(dv / np.linalg.norm(dv), d, atol=1e-6), bn

    def to_unity(M):
        R3 = C @ ortho(M[:3, :3]) @ C.T
        return C @ M[:3, 3], quat(R3)

    buf = bytearray(b"JIMY") + struct.pack("<I", 1)
    def wstr(s):
        e = s.encode("utf-8"); buf.extend(struct.pack("<H", len(e)) + e)
    buf += struct.pack("<I", len(bones))
    for b in bones:
        wstr(nif.blocks[b]["name"])
        buf += struct.pack("<i", bidx[parent[b]] if parent[b] >= 0 else -1)
        p, q = to_unity(Lb[b]); _, qt = to_unity(Lt[b])
        buf += struct.pack("<3f4f4f", *p, *q, *qt)

    # meshes: rest-pose vertices in model space (Sum w * M_bone . S_bone . v, verified to 2 mm against the stored mesh)
    meshes = [b for b in nif.blocks if b["type"] in ("NiTriShape", "NiTriStrips") and b["skin"] >= 0]
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
                infl[vi].append((bidx[bi], w))
        pos = pos @ C.T
        nrm = nrm @ C.T; nrm /= np.maximum(np.linalg.norm(nrm, axis=1, keepdims=True), 1e-9)
        uv = d["uvs"][0].copy(); uv[:, 1] = 1 - uv[:, 1]          # D3D top-left origin -> Unity bottom-left
        tris = d["tris"].copy()
        # winding: Unity draws clockwise (seen from outside) as the front; pick the order whose face normal agrees
        # with the vertex normals (left-handed: front normal = cross(b-a, c-a) for clockwise when seen from outside)
        fn = np.cross(pos[tris[:, 1]] - pos[tris[:, 0]], pos[tris[:, 2]] - pos[tris[:, 0]])
        vn = nrm[tris[:, 0]] + nrm[tris[:, 1]] + nrm[tris[:, 2]]
        flip = (fn * vn).sum(1) < 0
        print(f"{sh['name']}: {n} verts, {len(tris)} tris, flipped {flip.mean():.0%}")
        tris[flip] = tris[flip][:, [0, 2, 1]]
        texfile = None
        for p in sh["props"]:
            pb = nif.blocks[p]
            if pb["type"] == "NiTexturingProperty" and pb["slots"][0] >= 0:
                texfile = nif.blocks[pb["slots"][0]]["file"].replace("\\", "/").split("/")[-1][:-4]
        texnames.add(texfile)
        wstr(sh["name"]); wstr(tpre + texfile if texfile else "")
        buf += struct.pack("<I", n)
        for i in range(n):
            inf = sorted(infl[i], key=lambda t: -t[1])[:4]
            tot = sum(w for _, w in inf) or 1.0
            inf = [(bi, w / tot) for bi, w in inf] + [(0, 0.0)] * (4 - len(inf))
            buf += struct.pack("<3f3f2f", *pos[i], *nrm[i], *uv[i])
            buf += struct.pack("<4i", *[bi for bi, _ in inf]) + struct.pack("<4f", *[w for _, w in inf])
        buf += struct.pack("<I", tris.size) + tris.astype("<u4").tobytes()
    (out_dir / f"{out}.bytes").write_bytes(bytes(buf))

    for b in tex.blocks:
        if b["type"] == "NiSourceTexture":
            f = b["file"].replace("\\", "/").split("/")[-1][:-4]
            if f in texnames:
                Image.fromarray(decode_pixels(tex, tex.blocks[b["pixels"]])).save(out_dir / f"{tpre}{f}.png")
    print("wrote", out + ".bytes,", len(bones), "bones,", len(meshes), "meshes, textures", sorted(tpre + t for t in texnames if t))


if __name__ == "__main__":
    import sys
    main(*sys.argv[1:3]) if len(sys.argv) > 2 else main()
