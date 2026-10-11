"""Schedule I's pawn shop interior, rebuilt inside the Chemical Plant's Warehouse01 (Austin, 2026-10-10: the carnival
shop "at the building across from the gas station where i just saved the pose ... build the inside to look like the
pawn shop"). Read-only on the game's files (UnityPy).

The pawn shop (Map/Hyland Point/Region_Northtown/Pawn shop) is mostly STATIC-BATCHED: its renderers point into big
world-space combined meshes, so copying the objects at runtime would draw the wrong geometry. This cuts each
renderer's own submeshes out of the combined mesh (MeshRenderer.m_StaticBatchInfo), groups the triangles by the game's
material NAME (the mod looks the live material up by name, so it renders exactly like the real shop), and adds the
box colliders, the lights and a few anchors.

Warehouse01 is a fake building: one exterior mesh + solid box colliders filling it. The shop room (10 x 8 x 3.5 m) is
scaled to fit inside (9.7 x 9 x 3.3 m) with its glass-door wall on the warehouse's west (door) wall.

python export_shop.py  ->  ../assets/out/shop.bytes   (+ ../assets/shop_plan.png, a top-down check)

shop.bytes (little-endian): "SHOP" u32 1; container pos 3f, yaw f, scale f;
  u32 nmesh: str material, u32 nv, nv*(3f pos, 3f nrm, 2f uv), u32 ni, ni*u32       (container-local)
  u32 nbox: 3f centre, 4f rotation (x y z w), 3f size                               (container-local)
  u32 nlight: 3f pos, 3f colour, f intensity, f range, u32 type (0 spot 1 directional 2 point), f spot angle
  u32 nanchor: str name, 3f pos, f yaw
"""
import math, os, pickle, re, struct
from pathlib import Path
import numpy as np
import UnityPy
from UnityPy.helpers.MeshHelper import MeshHandler

import paths
SHOP = "Map/Hyland Point/Region_Northtown/Pawn shop"
WAREHOUSE = "Map/Hyland Point/Region_Westville/ChemicalPlant/Warehouse01"
# outside the room or replaced: the street sign, exterior LOD shell, rocks, the glass entrance doors (Warehouse01 keeps
# its own hinged door), the outdoor benches / bin and the outside security camera
SKIP = re.compile(r"^(SignBase|Sign|LOD|Rock(\.\d+)?|Basic Metal Glass Door.*|OutdoorBench.*|Rubbish bin|Security Camera \(Barrel\)|pawnshop|Classical Wood Door \(Static\))$")
# v0.13.4 (Austin: "mirror the wall of the warehouse on the inside as well so it has a square shape not the unique shape
# of the pawnshop inside"): the pawn shop's shell (pawnshop/*: walls, alcove, roof, floor, corners) and its back-room door
# are left out; the room is a plain box in the pawn shop's footprint, built from the shell's own wall / floor / ceiling
# materials, so the furniture and everything hung on its walls stay where they belong.
SHELL = {"InnerWalls": "wall", "Ground": "floor", "InnerRoof": "ceiling"}
LOD_N = re.compile(r"(_LOD[1-9]|LOD[1-9])$")


def load_scene(env):
    # cached per game build: a Schedule I update changes level1, and a stale cache would place the wrong geometry
    CACHE = paths.CACHE / f"scene_cache_{(paths.schedule() / 'Schedule I_Data' / 'level1').stat().st_size}.pkl"
    if CACHE.exists():
        return pickle.loads(CACHE.read_bytes())
    tf, gos, comps, active = {}, {}, {}, {}
    for o in env.objects:
        t = o.type.name
        if t in ("Transform", "RectTransform"):
            d = o.read_typetree()
            lp, lr, ls = d["m_LocalPosition"], d["m_LocalRotation"], d["m_LocalScale"]
            tf[o.path_id] = (d["m_Father"]["m_PathID"], (lp["x"], lp["y"], lp["z"]), (lr["x"], lr["y"], lr["z"], lr["w"]),
                             (ls["x"], ls["y"], ls["z"]), d["m_GameObject"]["m_PathID"], [c["m_PathID"] for c in d["m_Children"]])
        elif t == "GameObject":
            d = o.read_typetree()
            gos[o.path_id] = d["m_Name"]; active[o.path_id] = bool(d["m_IsActive"])
            comps[o.path_id] = [c["component"]["m_PathID"] for c in d["m_Component"]]
    types = {o.path_id: o.type.name for o in env.objects}
    CACHE.parent.mkdir(parents=True, exist_ok=True)
    CACHE.write_bytes(pickle.dumps((tf, gos, comps, active, types)))
    return tf, gos, comps, active, types


def qmat(q):
    x, y, z, w = q
    return np.array([[1 - 2*(y*y + z*z), 2*(x*y - z*w), 2*(x*z + y*w)],
                     [2*(x*y + z*w), 1 - 2*(x*x + z*z), 2*(y*z - x*w)],
                     [2*(x*z - y*w), 2*(y*z + x*w), 1 - 2*(x*x + y*y)]])


def mat_to_quat(R):
    R = R / np.linalg.norm(R, axis=0)
    w = math.sqrt(max(0.0, 1 + R[0, 0] + R[1, 1] + R[2, 2])) / 2
    x = math.sqrt(max(0.0, 1 + R[0, 0] - R[1, 1] - R[2, 2])) / 2
    y = math.sqrt(max(0.0, 1 - R[0, 0] + R[1, 1] - R[2, 2])) / 2
    z = math.sqrt(max(0.0, 1 - R[0, 0] - R[1, 1] + R[2, 2])) / 2
    x = math.copysign(x, R[2, 1] - R[1, 2]); y = math.copysign(y, R[0, 2] - R[2, 0]); z = math.copysign(z, R[1, 0] - R[0, 1])
    return (x, y, z, w)


def main():
    env = UnityPy.load(str(paths.schedule() / "Schedule I_Data" / "level1"))
    objs = {o.path_id: o for o in env.objects}
    tf, gos, comps, active, types = load_scene(env)

    def name(i): return gos.get(tf[i][4], "?")

    def path(i):
        n = []
        while i in tf:
            n.append(name(i)); p = tf[i][0]
            if p == 0: break
            i = p
        return "/".join(reversed(n))

    def world(i):
        M = np.eye(4); chain = []
        while i in tf:
            chain.append(i); p = tf[i][0]
            if p == 0: break
            i = p
        for j in reversed(chain):
            _, lp, lr, ls, _, _ = tf[j]
            L = np.eye(4); L[:3, :3] = qmat(lr) * np.array(ls); L[:3, 3] = lp
            M = M @ L
        return M

    by_path = {}
    for i in tf:
        if tf[i][0] == 0 or name(i) in ("Pawn shop", "Warehouse01"):
            p = path(i)
            if p in (SHOP, WAREHOUSE): by_path[p] = i
    shop, wh = by_path[SHOP], by_path[WAREHOUSE]
    S = world(shop); Sinv = np.linalg.inv(S)

    # every transform in the room, minus the skipped subtrees and inactive objects
    keep = []
    stack = [shop]
    while stack:
        i = stack.pop()
        if i != shop and (SKIP.match(name(i)) or not active.get(tf[i][4], True)):
            continue
        keep.append(i); stack.extend(tf[i][5])

    handlers = {}
    def tris_of(mesh_obj):
        if mesh_obj.path_id not in handlers:
            m = mesh_obj.read(); h = MeshHandler(m); h.process()
            nv = h.m_VertexCount
            v = np.array(h.m_Vertices, float).reshape(nv, -1)[:, :3]          # channels can carry 3 or 4 components
            n = np.array(h.m_Normals, float).reshape(nv, -1)[:, :3] if h.m_Normals else np.tile([0, 1.0, 0], (nv, 1))
            uv = np.array(h.m_UV0, float).reshape(nv, -1)[:, :2] if h.m_UV0 else np.zeros((nv, 2))
            subs = [np.array(t, int).reshape(-1, 3) for t in h.get_triangles()]
            handlers[mesh_obj.path_id] = (v, n, uv, subs)
        return handlers[mesh_obj.path_id]

    def cut(i):
        """This transform's own renderer, cut out of whatever (combined) mesh it draws from: [(material, v, n, uv, tris)]
        in shop-local space."""
        g = tf[i][4]; cs = comps.get(g, []); out = []
        mr = next((c for c in cs if types.get(c) == "MeshRenderer"), None)
        mf = next((c for c in cs if types.get(c) == "MeshFilter"), None)
        if not (mr and mf) or LOD_N.search(name(i)): return out
        rd = objs[mr].read_typetree()
        if not rd.get("m_Enabled", 1): return out
        mesh_ptr = objs[mf].read().m_Mesh
        if not mesh_ptr.m_PathID: return out
        v, n, uv, subs = tris_of(mesh_ptr.deref())
        sb = rd.get("m_StaticBatchInfo", {"firstSubMesh": 0, "subMeshCount": 0})
        if sb["subMeshCount"]:
            idxs = range(sb["firstSubMesh"], sb["firstSubMesh"] + sb["subMeshCount"]); M = Sinv   # already world space
        else:
            idxs = range(len(subs)); M = Sinv @ world(i)
        mats = objs[mr].read().m_Materials
        for k, si in enumerate(idxs):
            if si >= len(subs) or k >= len(mats) or not len(subs[si]): continue
            try: mname = mats[k].read().m_Name
            except Exception: continue
            t = subs[si]; used = np.unique(t); remap = -np.ones(len(v), int); remap[used] = np.arange(len(used))
            lv = (np.c_[v[used], np.ones(len(used))] @ M.T)[:, :3]
            ln = n[used] @ np.linalg.inv(M[:3, :3])
            ln /= np.maximum(np.linalg.norm(ln, axis=1, keepdims=True), 1e-9)
            tt = remap[t]
            if np.linalg.det(M[:3, :3]) < 0: tt = tt[:, [0, 2, 1]]
            out.append((mname, lv, ln, uv[used], tt))
        return out

    def box_of(i, c):
        d = objs[c].read_typetree()
        if not d.get("m_Enabled", 1) or d.get("m_IsTrigger"): return None
        M = Sinv @ world(i)
        ce = (M @ np.r_[d["m_Center"]["x"], d["m_Center"]["y"], d["m_Center"]["z"], 1])[:3]
        axes = M[:3, :3]; sc = np.linalg.norm(axes, axis=0)
        size = np.abs(np.array([d["m_Size"]["x"], d["m_Size"]["y"], d["m_Size"]["z"]]) * sc)
        return (ce, mat_to_quat(axes / sc), size)

    groups = {}      # material -> list of (v, n, uv, tris) in shop-local space
    boxes, lights, anchors = [], [], []
    for i in keep:
        g = tf[i][4]; cs = comps.get(g, [])
        W = world(i)
        for mname, lv, ln, uv_, tt in cut(i):
            groups.setdefault(mname, []).append((lv, ln, uv_, tt))
        for c in cs:
            ty = types.get(c)
            if ty == "BoxCollider":
                b = box_of(i, c)
                if b: boxes.append(b)
            elif ty == "Light":
                d = objs[c].read_typetree()
                if not d.get("m_Enabled", 1): continue
                M = Sinv @ W; col = d["m_Color"]
                lights.append(((M @ np.r_[0, 0, 0, 1])[:3], (col["r"], col["g"], col["b"]), d["m_Intensity"], d["m_Range"], d["m_Type"], d.get("m_SpotAngle", 30.0)))
        if name(i) in ("CashCounter", "Classical Wood Door (Static)"):
            M = Sinv @ W
            fwd = M[:3, :3] @ np.array([0, 0, 1.0])
            anchors.append((name(i), (M @ np.r_[0, 0, 0, 1])[:3], math.degrees(math.atan2(fwd[0], fwd[2]))))

    # the room = the union of the shop's own wall colliders (pawnshop/Main/Cube*); everything outside it (the exterior
    # shell's far faces, the pavement slab, the awning) is cut, so the scale fits the room, not the lot
    walls = [i for i in tf if path(i).startswith(SHOP + "/pawnshop/Main/Cube")]
    corners = []
    for i in walls:
        M = Sinv @ world(i)
        for c in [(x, y, z) for x in (-.5, .5) for y in (-.5, .5) for z in (-.5, .5)]:
            corners.append((M @ np.r_[c, 1])[:3])
    corners = np.array(corners); rlo, rhi = corners.min(0) - 0.05, corners.max(0) + 0.05
    rlo[1] = min(rlo[1], 0.0)
    def inside(p): return np.all((p >= rlo) & (p <= rhi), axis=-1)
    for mname in list(groups):
        kept = []
        for lv, ln, uv_, tt in groups[mname]:
            ok = inside(lv[tt].mean(1))
            if ok.any(): kept.append((lv, ln, uv_, tt[ok]))
        if kept: groups[mname] = kept
        else: del groups[mname]
    boxes = [b for b in boxes if inside(b[0])]
    lights = [l for l in lights if inside(l[0])]
    # fit by the room's walls (the floor at y 0), not by stray vertices of edge triangles (roof overhang, floor slab)
    lo, hi = rlo + 0.05, rhi - 0.05
    lo[1] = 0.0
    print(f"room (from {len(walls)} wall colliders) {lo.round(2)} .. {hi.round(2)}")
    print(f"shop-local bounds {lo.round(2)} .. {hi.round(2)}; {len(groups)} materials, {sum(len(p[0]) for g in groups.values() for p in g)} verts, {len(boxes)} boxes, {len(lights)} lights")

    # ---- the glass double doors, as their own hinged leaves (v0.13.2, Austin: "build the actual wall and door in front
    # ... a glass door like the pawn shop, just make it solid and useable"): each Basic Metal Glass Door's Container/Door
    # is the leaf, its transform is the hinge, Door/Collider its solid box. Exported leaf-local (around the hinge).
    def desc(i):
        out, st = [], [i]
        while st:
            k = st.pop(); out.append(k); st.extend(tf[k][5])
        return out
    doors = []
    for i in tf[shop][5]:
        if not name(i).startswith("Basic Metal Glass Door") or not active.get(tf[i][4], True): continue
        leaf = next((k for k in desc(i) if name(k) == "Door"), None)
        if leaf is None: continue
        pivot = (Sinv @ world(leaf))[:3, 3]
        parts = []
        for k in desc(leaf):
            for mname, lv, ln, uv_, tt in cut(k): parts.append((mname, lv - pivot, ln, uv_, tt))
        lbox = None
        for k in desc(leaf):
            for c in comps.get(tf[k][4], []):
                if types.get(c) == "BoxCollider":
                    b = box_of(k, c)
                    if b: lbox = (b[0] - pivot, b[1], b[2])
        doors.append((pivot, parts, lbox))
        print(f"door leaf '{name(i)}': hinge {pivot.round(3)}, {len(parts)} parts, collider {lbox[2].round(3) if lbox else None}")

    # ---- placement: the glass-door wall onto Warehouse01's door wall
    # the entrance: where the skipped glass doors stood, in shop-local space
    ents = [Sinv @ world(i) @ np.r_[0, 0, 0, 1] for i in tf if path(i).startswith(SHOP + "/Basic Metal Glass Door") and tf[i][0] == shop]
    ent = np.mean([e[:3] for e in ents], axis=0)
    centre = (lo + hi) / 2
    inward = centre - ent; inward[1] = 0; inward /= np.linalg.norm(inward)
    # the doors' pivots sit inside the opening: snap the entrance onto the room's own wall face on that side
    ax = 0 if abs(inward[0]) > abs(inward[2]) else 2
    inward = np.zeros(3); inward[ax] = 1.0 if centre[ax] > ent[ax] else -1.0
    ent = ent.copy(); ent[ax] = lo[ax] if inward[ax] > 0 else hi[ax]
    # Warehouse01: the hinged door, the solid box colliders = the usable room
    whd = next(i for i in tf if tf[i][0] == wh and name(i) == "SM_Door")
    door = world(whd)[:3, 3]
    cubes = [i for i in tf if path(i).startswith(WAREHOUSE + "/Colliders/Cube") and name(i) in ("Cube",)]
    Wc = world(cubes[0]); half = np.abs(Wc[:3, :3]) @ np.array([0.5, 0.5, 0.5])
    room_lo, room_hi = Wc[:3, 3] - half, Wc[:3, 3] + half
    wh_in = np.array([1.0, 0, 0]) if abs(door[0] - room_lo[0]) < abs(door[0] - room_hi[0]) else np.array([-1.0, 0, 0])
    yaw = math.degrees(math.atan2(wh_in[0], wh_in[2]) - math.atan2(inward[0], inward[2]))
    R = qmat((0, math.sin(math.radians(yaw) / 2), 0, math.cos(math.radians(yaw) / 2)))
    width = np.abs(R @ (hi - lo))
    margin = 0.12
    scale = min((room_hi[2] - room_lo[2] - 2 * margin) / width[2], (room_hi[0] - room_lo[0] - 2 * margin) / width[0], (room_hi[1] - room_lo[1]) / (hi[1] - lo[1]), 1.0)
    floor_y = room_lo[1] - 0.05
    # ---- v0.13.3, built the way the game builds its own enterable buildings (Austin: "use the other buildings around
    # as refrance ... the tattoo shop entrance": the building's own exterior mesh, the door in the building's own
    # doorway, invisible box walls around it). Warehouse01's exterior stays the game's (v0.13.2's rebuilt copy came out
    # see-through); its west wall has a real doorway (measured from its mesh below); the pawn-shop room sits flush
    # behind that wall, its entrance on the doorway; the pawn shop's glass double door is seated IN the doorway.
    fw_parts = cut(wh)
    fw = np.vstack([(np.c_[lv, np.ones(len(lv))] @ S.T)[:, :3] for _, lv, _, _, _ in fw_parts])
    near = fw[np.abs(fw[:, 2] - door[2]) < 2.5]
    facade = near[:, 0].min() if wh_in[0] > 0 else near[:, 0].max()       # the west wall's OUTER face
    band = near[(np.abs(near[:, 0] - facade) > 0.1) & (np.abs(near[:, 0] - facade) < 0.45)]
    inner = np.median(band[:, 0])                                           # its INNER face (the wall is a slab)
    # the doorway: where the wall's inner face has no triangles, around the old door (sampled on a 1 cm grid)
    face = []
    for _, lv, _, _, tt in fw_parts:
        wv = (np.c_[lv, np.ones(len(lv))] @ S.T)[:, :3]
        P = wv[tt]
        face.append(P[np.all(np.abs(P[:, :, 0] - inner) < 0.03, axis=1)])
    face = np.vstack(face)
    def covered(z, y):
        a_, b_, c_ = face[:, 0][:, [2, 1]], face[:, 1][:, [2, 1]], face[:, 2][:, [2, 1]]
        d = (b_[:, 1] - c_[:, 1]) * (a_[:, 0] - c_[:, 0]) + (c_[:, 0] - b_[:, 0]) * (a_[:, 1] - c_[:, 1])
        ok = np.abs(d) > 1e-12
        l1 = ((b_[:, 1] - c_[:, 1]) * (z - c_[:, 0]) + (c_[:, 0] - b_[:, 0]) * (y - c_[:, 1])) / np.where(ok, d, 1)
        l2 = ((c_[:, 1] - a_[:, 1]) * (z - c_[:, 0]) + (a_[:, 0] - c_[:, 0]) * (y - c_[:, 1])) / np.where(ok, d, 1)
        return bool(np.any(ok & (l1 >= -1e-6) & (l2 >= -1e-6) & (1 - l1 - l2 >= -1e-6)))
    zs = np.arange(door[2] - 2.0, door[2] + 2.0, 0.01)
    open_z = [z for z in zs if not covered(z, floor_y + 1.0)]
    runs, cur = [], [open_z[0]]
    for z in open_z[1:]:
        if z - cur[-1] > 0.015: runs.append(cur); cur = [z]
        else: cur.append(z)
    runs.append(cur)
    run = min(runs, key=lambda r_: abs((r_[0] + r_[-1]) / 2 - door[2]))
    dz0, dz1 = run[0], run[-1]
    dtop = next(y for y in np.arange(floor_y + 0.5, floor_y + 3.4, 0.01) if covered((dz0 + dz1) / 2, y))
    dmid = (dz0 + dz1) / 2
    print(f"Warehouse01 west wall: outer face x {facade:.3f}, inner face x {inner:.3f}; doorway z {dz0:.2f}..{dz1:.2f} ({dz1 - dz0:.2f} m), up to y {dtop:.2f} ({dtop - floor_y:.2f} m)")
    # the room: its entrance wall flush behind the warehouse wall's inner face, centred on the doorway
    target = np.array([inner + 0.01 * wh_in[0], floor_y, dmid])
    pos = target - R @ (scale * np.array([ent[0], lo[1], 0.0]))
    print(f"warehouse room {room_lo.round(2)} .. {room_hi.round(2)}; container pos {pos.round(3)} yaw {yaw:.1f} scale {scale:.3f}")
    def to_local(wv): return ((np.asarray(wv) - pos) @ R) / scale              # world -> container-local (R orthonormal)

    # the glass double door IN the doorway: the pawn shop's two leaves stretched to the opening (hinges at its two
    # edges, in the middle of the wall's thickness), leaf-local around each hinge, container-local units
    wall_mid = door[0]                              # flush with where Warehouse01's own door stood
    hole_w, hole_h = dz1 - dz0, dtop - floor_y
    leaves = []
    single = hole_w < 1.6                            # a single-door opening: one full leaf, hinged where the old door was
    for pivot, parts, lbox in doors:
        allv = np.vstack([q[1] for q in parts])
        spans_plus = allv[:, 2].mean() > 0           # the leaf hangs toward +z from its hinge
        if single and spans_plus == (door[2] > dmid): continue   # keep the leaf that hangs away from the old hinge side
        hinge_w = np.array([wall_mid, floor_y + hole_h / 2, dz0 if spans_plus else dz1])
        k = np.array([1.0, hole_h / 2.1, hole_w / (1.05 if single else 2.1)])
        new_parts = [(mn, (lv * k) / scale, ln, uv_, tt) for mn, lv, ln, uv_, tt in parts]
        lv_all = np.vstack([q[1] for q in new_parts])
        bl, bh = lv_all.min(0), lv_all.max(0)
        bl[0] = min(bl[0], -0.03 / scale); bh[0] = max(bh[0], 0.03 / scale)     # at least 6 cm thick: solid when shut
        box = ((bl + bh) / 2, (0.0, 0.0, 0.0, 1.0), bh - bl)
        leaves.append((to_local(hinge_w), new_parts, box))
    doors = leaves

    # the walls' collision, like the tattoo parlour's boxes: the west wall's slab beside and above the doorway
    wlo_z, whi_z, roof = room_lo[2], room_hi[2], room_hi[1]
    sx0, sx1 = sorted((facade, inner))
    def wbox(x0, x1, y0, y1, z0, z1):
        c_ = to_local(((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2))
        boxes.append((c_, (0.0, 0.0, 0.0, 1.0), np.array([x1 - x0, y1 - y0, z1 - z0]) / scale))
    wbox(sx0, sx1, floor_y, roof, wlo_z - 0.1, dz0)        # left of the doorway
    wbox(sx0, sx1, floor_y, roof, dz1, whi_z + 0.1)        # right of it
    wbox(sx0, sx1, dtop, roof, dz0, dz1)                   # above it
    print(f"doorway leaves {len(doors)}, wall boxes 3, {len(boxes)} boxes in all")

    # ---- the plain box room (shop-local = container-local units). The shell's materials and texel density are read
    # off the pawn shop's own InnerWalls / Ground / InnerRoof meshes, so the textures land at their real scale.
    shell = {}
    for i in tf:
        nm_ = name(i)
        if nm_ in SHELL and path(i).startswith(SHOP + "/pawnshop/"):
            for mname, lv, ln, uv_, tt in cut(i):
                e = lv[tt[:, 1]] - lv[tt[:, 0]]; du = uv_[tt[:, 1]] - uv_[tt[:, 0]]
                L = np.linalg.norm(e, axis=1); U = np.linalg.norm(du, axis=1)
                ok = L > 0.05
                dens = float(np.median(U[ok] / L[ok])) if ok.any() else 0.5
                shell.setdefault(SHELL[nm_], (mname, dens, float(lv[:, 1].max()) if SHELL[nm_] == "ceiling" else None))
    print("shell materials:", {k: (v[0], round(v[1], 3)) for k, v in shell.items()})
    T = 0.25                                                       # the pawn shop's wall thickness
    ilo = lo.copy(); ihi = hi.copy()
    ilo[2] += T; ihi[2] -= T; ihi[0] -= T                          # inner faces (the entrance side IS the warehouse wall)
    ceil_y = shell.get("ceiling", (None, 0, 3.0))[2] or 3.0
    hz0, hz1 = sorted((to_local((0, 0, dz0))[2], to_local((0, 0, dz1))[2]))
    htop = (dtop - floor_y) / scale
    quads = {}
    def quad(kind, a, b_, c_, d_, normal):
        mname, dens, _ = shell.get(kind, (None, 0.5, None))
        if mname is None: return
        P = np.array([a, b_, c_, d_], float)
        ax = [k for k in range(3) if abs(normal[k]) < 0.5]         # the two in-plane axes -> metric UVs
        uv = np.c_[P[:, ax[0]], P[:, ax[1]]] * dens * scale
        tris = np.array([[0, 1, 2], [0, 2, 3]])
        n = np.tile(np.array(normal, float), (4, 1))
        # wind so the face looks along `normal` (Unity: clockwise seen from the front)
        fn = np.cross(P[1] - P[0], P[2] - P[0])
        if fn @ np.array(normal) > 0: tris = tris[:, [0, 2, 1]]
        quads.setdefault(mname, []).append((P, n, uv, tris))
    x0, x1, z0, z1, y1 = lo[0], ihi[0], ilo[2], ihi[2], ceil_y
    quad("floor", (x0, 0.01, z0), (x1, 0.01, z0), (x1, 0.01, z1), (x0, 0.01, z1), (0, 1, 0))
    quad("ceiling", (x0, y1, z0), (x0, y1, z1), (x1, y1, z1), (x1, y1, z0), (0, -1, 0))
    quad("wall", (x1, 0, z0), (x1, y1, z0), (x1, y1, z1), (x1, 0, z1), (-1, 0, 0))                  # back wall
    quad("wall", (x0, 0, z0), (x0, y1, z0), (x1, y1, z0), (x1, 0, z0), (0, 0, 1))                   # side walls
    quad("wall", (x0, 0, z1), (x1, 0, z1), (x1, y1, z1), (x0, y1, z1), (0, 0, -1))
    xf = x0 + 0.005                                                                                  # the front wall, doorway cut out
    quad("wall", (xf, 0, z0), (xf, y1, z0), (xf, y1, hz0), (xf, 0, hz0), (1, 0, 0))
    quad("wall", (xf, 0, hz1), (xf, y1, hz1), (xf, y1, z1), (xf, 0, z1), (1, 0, 0))
    quad("wall", (xf, htop, hz0), (xf, y1, hz0), (xf, y1, hz1), (xf, htop, hz1), (1, 0, 0))
    for mname, qs in quads.items(): groups.setdefault(mname, []).extend(qs)
    # its walls' collision (the front is the warehouse wall's three boxes above)
    for c_, sz in (((x1 + T / 2, y1 / 2, (z0 + z1) / 2), (T, y1, z1 - z0 + 2 * T)),
                   (((x0 + x1) / 2, y1 / 2, z0 - T / 2), (x1 - x0, y1, T)),
                   (((x0 + x1) / 2, y1 / 2, z1 + T / 2), (x1 - x0, y1, T))):
        boxes.append((np.array(c_), (0.0, 0.0, 0.0, 1.0), np.array(sz)))
    print(f"box room shop-local x {x0:.2f}..{x1:.2f} z {z0:.2f}..{z1:.2f} up to {y1:.2f}; doorway z {hz0:.2f}..{hz1:.2f} up to {htop:.2f}")

    buf = bytearray(b"SHOP") + struct.pack("<I", 2) + struct.pack("<3fff", *pos, yaw, scale)
    def wstr(s):
        e = s.encode(); buf.extend(struct.pack("<H", len(e)) + e)
    meshes = []
    for mname, parts in groups.items():           # chunks under 65k vertices (16-bit index meshes)
        cur = []; n = 0
        for p in parts:
            if n + len(p[0]) > 30000 and cur: meshes.append((mname, cur)); cur = []; n = 0     # doubled below: < 65k
            cur.append(p); n += len(p[0])
        if cur: meshes.append((mname, cur))
    buf += struct.pack("<I", len(meshes))
    for mname, parts in meshes:
        v = np.vstack([p[0] for p in parts]); nr = np.vstack([p[1] for p in parts]); uv = np.vstack([p[2] for p in parts])
        off = np.cumsum([0] + [len(p[0]) for p in parts[:-1]])
        t = np.vstack([p[3] + o for p, o in zip(parts, off)])
        # double-sided: a back copy (reversed winding, normals turned round), so no wall can be see-through from
        # either side whichever way the source mesh was authored (v0.13.2: "the outer walls are see through")
        nvs = len(v)
        v = np.vstack([v, v]); nr = np.vstack([nr, -nr]); uv = np.vstack([uv, uv]); t = np.vstack([t, t[:, [0, 2, 1]] + nvs])
        wstr(mname); buf += struct.pack("<I", len(v))
        buf += np.c_[v, nr, uv].astype("<f4").tobytes()
        buf += struct.pack("<I", t.size) + t.astype("<u4").tobytes()
    buf += struct.pack("<I", len(boxes))
    for ce, q, size in boxes: buf += struct.pack("<3f4f3f", *ce, *q, *size)
    buf += struct.pack("<I", len(lights))
    for p, col, inten, rng, ty, ang in lights: buf += struct.pack("<3f3fffIf", *p, *col, inten, rng, ty, ang)
    buf += struct.pack("<I", len(anchors) + 1)
    wstr("Entrance"); buf += struct.pack("<3ff", *ent, math.degrees(math.atan2(inward[0], inward[2])))
    for nm, p, y in anchors: wstr(nm); buf += struct.pack("<3ff", *p, y)
    # v2: the door leaves: u32 n; per leaf: 3f hinge, u32 nparts (str material, u32 nv, verts, u32 ni, idx), u8 has box,
    # [3f centre, 4f rot, 3f size] -- all leaf-local (around the hinge), shop-local axes
    buf += struct.pack("<I", len(doors))
    for pivot, parts, lbox in doors:
        buf += struct.pack("<3f", *pivot) + struct.pack("<I", len(parts))
        for mname, lv, ln, uv_, tt in parts:
            n_ = len(lv); lv2 = np.vstack([lv, lv]); ln2 = np.vstack([ln, -ln]); uv2 = np.vstack([uv_, uv_]); tt2 = np.vstack([tt, tt[:, [0, 2, 1]] + n_])
            wstr(mname); buf += struct.pack("<I", len(lv2)) + np.c_[lv2, ln2, uv2].astype("<f4").tobytes()
            buf += struct.pack("<I", tt2.size) + tt2.astype("<u4").tobytes()
        buf += struct.pack("<B", 1 if lbox else 0)
        if lbox: buf += struct.pack("<3f4f3f", *lbox[0], *lbox[1], *lbox[2])
    # v2: the room's own box (shop-local), so the mod can keep the rest of Warehouse01 solid around it
    buf += struct.pack("<3f3f", *lo, *hi)
    paths.OUT.mkdir(parents=True, exist_ok=True)
    (paths.OUT / "shop.bytes").write_bytes(bytes(buf))
    print(f"shop.bytes: {len(buf) / 1e6:.1f} MB, {len(meshes)} meshes, anchors {[a[0] for a in anchors]}")

    if paths.PUBLIC: return           # the plan picture is a dev check only
    # top-down check: warehouse footprint, its door, and the placed room
    from PIL import Image, ImageDraw
    img = Image.new("RGB", (700, 700), (24, 24, 30)); d = ImageDraw.Draw(img)
    c0 = (room_lo + room_hi) / 2; k = 50
    def px(p): return (350 + (p[0] - c0[0]) * k, 350 - (p[2] - c0[2]) * k)
    a, b = px(room_lo), px(room_hi)
    d.rectangle([min(a[0], b[0]), min(a[1], b[1]), max(a[0], b[0]), max(a[1], b[1])], outline=(200, 80, 80), width=2)
    Rm = np.eye(4); Rm[:3, :3] = R * scale; Rm[:3, 3] = pos
    for parts in groups.values():
        for lv, _, _, tt in parts:
            wv = (np.c_[lv, np.ones(len(lv))] @ Rm.T)[:, :3]
            for tri in tt[:: max(1, len(tt) // 400)]:
                d.polygon([px(wv[j]) for j in tri], outline=(120, 170, 220))
    for pivot, parts, _ in doors:
        for _, lv, _, _, tt in parts:
            wv = (np.c_[lv + pivot, np.ones(len(lv))] @ Rm.T)[:, :3]
            for tri in tt[:: max(1, len(tt) // 200)]:
                d.polygon([px(wv[j]) for j in tri], outline=(120, 230, 120))
    fx = inner
    d.line([px((fx, 0, room_lo[2] - 2)), px((fx, 0, room_hi[2] + 2))], fill=(240, 240, 240))
    d.ellipse([px(door)[0] - 5, px(door)[1] - 5, px(door)[0] + 5, px(door)[1] + 5], fill=(255, 210, 60))
    img.save(paths.PROJECT / "assets" / "shop_plan.png")


if __name__ == "__main__":
    main()
