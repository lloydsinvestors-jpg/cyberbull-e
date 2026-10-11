"""Export chosen Bully animations (decoded from Austin's install by agr.py) for the mod's runtime player.
python export_anims.py   -> ../assets/out/anims.bytes

anims.bytes: "ANIM" u32 version, u32 nclips; per clip: str name, f32 duration, u8 loop, u16 ntracks;
  per track: str bone, u16 nrot, nrot x (f32 t01, 4f local rotation x y z w, Unity space),
             u16 npos, npos x (f32 t01, 3f local position, Unity space; Root only, played in place)
Each clip was picked by eye from tools/anim_sheet.py contact sheets (assets/anims/).
"""
import struct
from pathlib import Path
import numpy as np
import agr
from export_jimmy import C, quat
import paths

CLIPS = [  # (name in the mod, group, index, loop)
    ("sling_aim", "W_Slingshot.agr", 0, False),   # raise the slingshot and draw; the end pose is held while aiming
    ("sling_fire", "W_Slingshot.agr", 2, False),  # let it fly
    ("throw", "W_Thrown.agr", 4, False),          # overhand throw (eggs, firecrackers, stink bombs, itching powder)
    ("spud_aim", "W_SpudGun.agr", 0, False),      # spud gun shouldered
    ("plant", "W_PRANK.agr", 0, False),           # crouch and plant (marbles, banana peels)
    ("wedgie", "SHUMIL_01.agr", 2, False),        # Bully's humiliation: grab the waistband and haul
]
EXTRA = Path(__file__).with_name("anim_picks.txt")
KEEP_TRAVEL = {"bike_on", "moped_on", "kart_on"}
# v0.22.20 (Austin: "his head is stuck looking back make it look forward"): Bully's Go_Cart idle / left / right poses all
# hold the head turned ~95 deg (its look-around pose); they take kart_cheer's forward-looking head instead
HEAD_FROM = {"kart_idle": "kart_cheer", "kart_left": "kart_cheer", "kart_right": "kart_cheer"}   # climb-in clips: their root travel carries Jimmy into the seat   # more picks, one "name group index loop" per line


def qmat(q):
    """(w, x, y, z) -> 3x3. Kept here, not imported from anim_sheet: that dev tool loads assets/raw/PLAYER.nif at import,
    which only exists on the dev machine (the public exporter died on it, 2026-10-11)."""
    w, x, y, z = q
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                     [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                     [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


def track_rot_unity(q_wxyz):
    R = qmat(q_wxyz)
    return quat(C @ R @ C.T)


def main():
    clips = list(CLIPS)
    if not EXTRA.exists(): raise FileNotFoundError(f"{EXTRA} is missing (most of the mod's clips are picked there)")
    for line in EXTRA.read_text().splitlines():
        p = line.split()
        if len(p) >= 3 and not line.startswith("#"):
            clips.append((p[0], p[1], int(p[2]), len(p) > 3 and p[3] == "loop"))
    buf = bytearray(b"ANIM") + struct.pack("<I", 1) + struct.pack("<I", len(clips))
    def wstr(s):
        e = s.encode(); buf.extend(struct.pack("<H", len(e)) + e)
    for name, group, idx, loop in clips:
        a = agr.load(group)[idx]
        arrow = agr.PED_BONES.index("ARROW")
        a0 = a.pos_tracks.get(arrow, [(0, np.zeros(3))])[0][1]
        tracks = []
        for b in range(min(a.nbones, len(agr.PED_BONES))):
            bone = agr.PED_BONES[b]
            if bone in ("Dummy", "ARROW"):
                continue
            rot = [(t, track_rot_unity(q)) for t, q, _ in a.tracks[b]]
            pos = []
            if bone == "Root" and b in a.pos_tracks:
                for t, p in a.pos_tracks[b]:
                    travel = agr._lerp_pos(a.pos_tracks[arrow], t) - a0 if arrow in a.pos_tracks else np.zeros(3)
                    # v0.22.15 (Austin: the go-kart / moped get-in "has me sitting on the side then blinks me inside"): the
                    # climb-in clips' travel IS the move (beside the ride -> into the seat); keep it for them
                    if name in KEEP_TRAVEL: travel = np.zeros(3)
                    pos.append((t, C @ (np.array(p) - travel)))
                # v0.18.3: some clips keep their travel on the Root itself (no ARROW track): npc_walkfast drifted 2.15 m per
                # loop, fist_block 1.3 m, so the body glided off and snapped back. The game moves the character; strip any
                # straight-line horizontal drift over the clip (> 0.3 m), keeping the sway.
                if len(pos) > 1 and name.startswith(("npc", "hit_", "fist_")):   # the bike's clips (signed off) keep theirs
                    drift = pos[-1][1] - pos[0][1]
                    t0, t1 = pos[0][0], pos[-1][0]
                    if np.hypot(drift[0], drift[2]) > 0.3 and t1 > t0:
                        pos = [(t, p - np.array([drift[0], 0.0, drift[2]]) * ((t - t0) / (t1 - t0))) for t, p in pos]
                        print(f"  {name}: stripped {np.hypot(drift[0], drift[2]):.2f} m of root drift")
            if bone == "Root Head" and name in HEAD_FROM:
                src = next(c for c in clips if c[0] == HEAD_FROM[name])
                sa = agr.load(src[1])[src[2]]
                q0 = track_rot_unity(sa.tracks[b][0][1])
                rot = [(0.0, q0)]
                print(f"  {name}: head from {src[0]} (looks forward)")
            if rot or pos:
                tracks.append((bone, rot, pos))
        wstr(name); buf += struct.pack("<fBH", a.duration, 1 if loop else 0, len(tracks))
        for bone, rot, pos in tracks:
            wstr(bone)
            buf += struct.pack("<H", len(rot))
            for t, q in rot:
                buf += struct.pack("<f4f", t, *q)
            buf += struct.pack("<H", len(pos))
            for t, p in pos:
                buf += struct.pack("<f3f", t, *p)
        print(f"{name:12s} <- {group} #{idx}: {a.duration:.2f}s, {len(tracks)} tracks")
    paths.OUT.mkdir(parents=True, exist_ok=True)
    (paths.OUT / "anims.bytes").write_bytes(bytes(buf))
    print("wrote anims.bytes", len(buf), "bytes")


if __name__ == "__main__":
    main()
