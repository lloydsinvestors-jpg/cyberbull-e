"""Export Bully's item models (rigid NIFs) + their textures + Bully's HUD icons for the Schedule I mod.
python export_props.py   -> ../assets/out/props.bytes, ../assets/out/prop_*.png, ../assets/out/hud_*.png

props.bytes (little-endian): "PROP" u32 version, u32 nprops; per prop: str name, u32 nparts;
  per part: str texture (png name without .png, "" = none), u32 nverts, nverts*(3f pos, 3f normal, 2f uv), u32 nidx, nidx*u32
Positions are in Unity space (C . bully, metres), relative to the model's own origin.
"""
import struct
from pathlib import Path
import numpy as np
from PIL import Image
from nif import Nif, world_map, decode_pixels
from imgdir import read
from export_jimmy import C
import paths

PROPS = ["bike", "SCOOTER", "GoCart", "ISouv_Gifts02", "SK8Board", "armoir", "potato", "slingshot", "Cherryb", "stinkbomb", "W_Itch", "marble", "EGGPROJ", "spudg", "BROCKET", "BROCKETLAUNCHER", "banana", "ammo_potatocan", "MarbBag"]
# Bully's cars carry no wheels in their own model: the engine bolts a shared wheel model onto each wheel_*_dummy.
# GoCart: dummies sit 0.119 m up (= the wheel radius); Classic_wheel (chunky tread, yellow hub) scaled to 0.24 m x 0.15 m.
WHEELS = {"GoCart": ("Classic_wheel", 0.24, 0.15, {"wheel_lf_dummy": "spin_wheel_front_l", "wheel_rf_dummy": "spin_wheel_front_r",
                                                   "wheel_lb_dummy": "spin_wheel_rear_l", "wheel_rb_dummy": "spin_wheel_rear_r"})}
TRIM = {"slingshot": 0.27}   # metres from the Handle node
# the carnival prize shop's shirt + wig + clown-shoe rack (Bully's ISouv_Gifts02, behind the carnie's counter): a placed
# map chunk, so it's recentred (centred in x / z, standing on y 0) and borrows the prize shop's texture dictionary.
# (ISouv_Gifts01 / 03 and the arcade row are too big for the pawn-shop room without gutting its layout.)
TEXFROM = {"ISouv_Gifts02": "ISouv_Gifts01"}
RECENTRE = {"ISouv_Gifts02"}
# spinning parts of Bully's skinned vehicles: bone -> part tag (wheels spin on their hubs, the crank turns with pedalling)
SPIN = {
    "bike": {"wheel_front": "spin_wheel_front", "wheel_rear": "spin_wheel_rear",
             "Pedal_Left": "spin_crank", "Pedal_Right": "spin_crank", "windscreen_hi_ok": "spin_crank"},
    "SCOOTER": {"wheel_front": "spin_wheel_front", "wheel_rear": "spin_wheel_rear"},
}
ICONS = ["HUDIcon_fist", "HUDicon_sling", "HUDicon_cbomb", "HUDIcon_STINKBOMB", "HUDIcon_itchpowd", "HUDIcon_marble", "HUDicon_egg",
         "HUDIcon_SPD", "HUDicon_brocketL", "banana", "HUDIcon_skate", "HUDIconBG", "Infinity", "Social_Wedgie01", "Social_Taunt01",
         "Social_Apologize01", "Social_Wave01", "Social_Shove01", "target2A", "HUD_xhair", "Bar_outline", "Bar_health_full", "Bar_other_full",
         "Bar_iconhealth", "Icon_Punish", "Social_bull", "HUD_circleoutline", "MI_bike"]


def base(f):
    return f.replace("\\", "/").split("/")[-1].rsplit(".", 1)[0]


def textures(nft_bytes):
    n = Nif(nft_bytes)
    out = {}
    for b in n.blocks:
        if b["type"] == "NiSourceTexture" and b.get("pixels", -1) >= 0:
            try:
                out[base(b["file"]).lower()] = decode_pixels(n, n.blocks[b["pixels"]])
            except Exception as e:
                print("  texture", b["file"], e)
    return out


def wheel_parts(car, carW, wheel, dia, width, dummies):
    """One part per wheel dummy: the shared wheel model centred on its hub, scaled (Bully x = axle / width, y z = the
    disc), placed at the dummy and tagged to spin (and steer, for the fronts) at runtime. Pivot = the hub, Unity space."""
    wn = Nif(read(wheel + ".nif")); wW = world_map(wn)
    try:
        wtex = textures(read(wheel + ".nft"))
    except KeyError:
        wtex = {}
    shapes = []
    for b in wn.blocks:
        if b["type"] not in ("NiTriShape", "NiTriStrips") or b["index"] not in wW:
            continue
        d = wn.blocks[b["data"]]
        if d.get("verts") is None or not len(d["tris"]) or not d["uvs"]:
            continue
        M = wW[b["index"]]
        v = (np.c_[d["verts"], np.ones(d["nverts"])] @ M.T)[:, :3]
        nrm = d["normals"] if d["normals"] is not None else np.tile([1.0, 0, 0], (d["nverts"], 1))
        nrm = nrm @ M[:3, :3].T
        tname = ""
        for p in b["props"]:
            pb = wn.blocks[p]
            if pb["type"] == "NiTexturingProperty" and pb["slots"] and pb["slots"][0] >= 0:
                key = base(wn.blocks[pb["slots"][0]].get("file") or "").lower()
                if key in wtex:
                    tname = f"prop_{wheel}_{key}"
                    Image.fromarray(wtex[key]).save(paths.OUT / f"{tname}.png")
        uv = d["uvs"][0].copy(); uv[:, 1] = 1 - uv[:, 1]
        shapes.append((tname, v, nrm, uv, d["tris"].copy()))
    allv = np.vstack([s[1] for s in shapes])
    hub = (allv.min(0) + allv.max(0)) / 2
    size = np.maximum(allv.max(0) - allv.min(0), 1e-6)
    k = np.array([width / size[0], dia / size[1], dia / size[2]])
    at = {car.blocks[i].get("name"): carW[i][:3, 3] for i in carW}
    out = []
    for dummy, tag in dummies.items():
        if dummy not in at:
            print("  no", dummy); continue
        pivot = at[dummy] @ C.T
        for tname, v, nrm, uv, tris in shapes:
            vu = ((v - hub) * k) @ C.T                      # wheel-local, Unity axes, around the hub
            nu = (nrm / k) @ C.T
            nu /= np.maximum(np.linalg.norm(nu, axis=1, keepdims=True), 1e-9)
            fn = np.cross(vu[tris[:, 1]] - vu[tris[:, 0]], vu[tris[:, 2]] - vu[tris[:, 0]])
            vn = nu[tris[:, 0]] + nu[tris[:, 1]] + nu[tris[:, 2]]
            t = tris.copy(); flip = (fn * vn).sum(1) < 0; t[flip] = t[flip][:, [0, 2, 1]]
            out.append((tname, vu, nu, uv, t, tag, pivot))
    return out


def main():
    paths.OUT.mkdir(parents=True, exist_ok=True)
    buf = bytearray(b"PROP") + struct.pack("<I", 2) + struct.pack("<I", len(PROPS))
    def wstr(s):
        e = s.encode(); buf.extend(struct.pack("<H", len(e)) + e)
    for name in PROPS:
        nif = Nif(read(name + ".nif"))
        try:
            tex = textures(read(TEXFROM.get(name, name) + ".nft"))
        except KeyError:
            tex = {}
        W = world_map(nif)
        # Bully grips a prop by its "Handle" node: put the handle at the origin (= the hand bone). The spud gun and
        # slingshot are modelled away from their origin, so origin-attach floated the spud gun half a metre off his hands.
        grip = np.zeros(3)
        for i in W:
            if (nif.blocks[i].get("name") or "").lower() == "handle":
                grip = W[i][:3, 3].copy()
                break
        parts = []
        for b in nif.blocks:
            if b["type"] not in ("NiTriShape", "NiTriStrips") or b["index"] not in W:
                continue
            d = nif.blocks[b["data"]]
            if d.get("verts") is None or not len(d["tris"]) or not d["uvs"]:
                continue
            M = W[b["index"]]
            v = ((np.c_[d["verts"], np.ones(d["nverts"])] @ M.T)[:, :3] - grip) @ C.T
            nrm = d["normals"] if d["normals"] is not None else np.tile([0, 0, 1.0], (d["nverts"], 1))
            nrm = (nrm @ M[:3, :3].T) @ C.T
            nrm /= np.maximum(np.linalg.norm(nrm, axis=1, keepdims=True), 1e-9)
            uv = d["uvs"][0].copy(); uv[:, 1] = 1 - uv[:, 1]
            tris = d["tris"].copy()
            if name in TRIM:
                # the slingshot's band + pouch are rigged to its own skeleton and stored STRETCHED out ~0.75 m; held
                # static they stick out sideways like a broom handle. Keep only geometry near the grip (fork + handle).
                near = np.linalg.norm((np.c_[d["verts"], np.ones(d["nverts"])] @ M.T)[:, :3] - grip, axis=1) <= TRIM[name]
                tris = tris[near[tris].all(axis=1)]
            fn = np.cross(v[tris[:, 1]] - v[tris[:, 0]], v[tris[:, 2]] - v[tris[:, 0]])
            vn = nrm[tris[:, 0]] + nrm[tris[:, 1]] + nrm[tris[:, 2]]
            flip = (fn * vn).sum(1) < 0
            tris[flip] = tris[flip][:, [0, 2, 1]]
            tname = ""
            for p in b["props"]:
                pb = nif.blocks[p]
                if pb["type"] == "NiTexturingProperty" and pb["slots"] and pb["slots"][0] >= 0:
                    src = nif.blocks[pb["slots"][0]]
                    key = base(src.get("file") or "").lower()
                    if key in tex:
                        tname = f"prop_{name}_{key}".replace(" ", "_")
                        Image.fromarray(tex[key]).save(paths.OUT / f"{tname}.png")
            groups = {"": tris}
            if name in SPIN and b["skin"] >= 0:
                si = nif.blocks[b["skin"]]; sd = nif.blocks[si["data"]]
                bone_of = np.full(d["nverts"], -1); best = np.zeros(d["nverts"])
                for bi, bone in zip(si["bones"], sd["bones"]):
                    for vi, w in bone["weights"]:
                        if w > best[vi]: best[vi] = w; bone_of[vi] = bi
                tag_of = np.array([SPIN[name].get(nif.blocks[x]["name"] if x >= 0 else "", "") for x in bone_of])
                t3 = tag_of[tris]
                same = (t3[:, 0] == t3[:, 1]) & (t3[:, 1] == t3[:, 2])
                groups = {"": tris[~same | (t3[:, 0] == "")]}
                for tag in dict.fromkeys(SPIN[name].values()):   # ordered: set() order changes per run (hash seed)
                    sel = tris[same & (t3[:, 0] == tag)]
                    if len(sel): groups[tag] = sel
            for tag, gt in groups.items():
                if not len(gt): continue
                pivot = np.zeros(3)
                if tag:
                    used = v[np.unique(gt)]
                    pivot = (used.min(0) + used.max(0)) / 2      # hub / crank centre
                parts.append((tname, v - pivot, nrm, uv, gt, tag, pivot))
        if name in WHEELS:
            parts += wheel_parts(nif, W, *WHEELS[name])
        if name in RECENTRE and parts:
            allp = np.concatenate([p[1] + p[6] for p in parts]); c = (allp.min(0) + allp.max(0)) / 2; c[1] = allp[:, 1].min()
            parts = [(t, v, n, uv, tr, tag, pv - c) for t, v, n, uv, tr, tag, pv in parts]   # a part draws at pivot + v
        allv = np.concatenate([p[1] for p in parts]) if parts else np.zeros((1, 3))
        print(f"{name}: {len(parts)} parts, {sum(len(p[1]) for p in parts)} verts, size {np.ptp(allv, 0).round(3)}, textures {[p[0] for p in parts]}")
        wstr(name); buf += struct.pack("<I", len(parts))
        for tname, v, nrm, uv, tris, tag, pivot in parts:
            wstr(tname); wstr(tag); buf += struct.pack("<3f", *pivot)
            buf += struct.pack("<I", len(v))
            for i in range(len(v)):
                buf += struct.pack("<3f3f2f", *v[i], *nrm[i], *uv[i])
            buf += struct.pack("<I", tris.size) + tris.astype("<u4").tobytes()
    (paths.OUT / "props.bytes").write_bytes(bytes(buf))

    hud = Nif((paths.bully() / "TXD" / "HUDElems.nft").read_bytes())
    want = {i.lower(): i for i in ICONS}
    for b in hud.blocks:
        if b["type"] == "NiSourceTexture" and b.get("pixels", -1) >= 0 and base(b["file"]).lower() in want:
            nm = want[base(b["file"]).lower()]
            Image.fromarray(decode_pixels(hud, hud.blocks[b["pixels"]])).save(paths.OUT / f"hud_{nm}.png")
    print("icons:", len(list(paths.OUT.glob("hud_*.png"))), "of", len(ICONS))


if __name__ == "__main__":
    main()
