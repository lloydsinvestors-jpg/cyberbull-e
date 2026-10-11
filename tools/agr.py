"""Bully SE .agr animation groups (format from LiquidSnake0/bully-re docs/agr.md; no code copied).
A group = animations back to back: u32 0x100, u32 type (999..1004), u32 nkeys, u32 0, f32 duration, nkeys keys,
then 8-byte position records (u16 key index, i16 x, y, z in mm) up to the next animation.
Keys of all bones are interleaved in time order; each points to the previous key of the same bone (0 = none);
the first keys (prev 0 AND time 0) open one track per bone, in skeleton order.
A key's quaternion REPLACES the bone's local rotation (same convention as NiAVObject rotations).
"""
import struct
from pathlib import Path
import numpy as np

KEYSIZE = {999: 32, 1000: 20, 1001: 12, 1002: 8, 1003: 20, 1004: 12}
PED_BONES = ["Dummy", "Root", "Root Pelvis", "Root L Thigh", "Root L Calf", "Root L Foot", "Root R Thigh", "Root R Calf",
             "Root R Foot", "Root01", "Root Spine", "Root Spine1", "Root Spine2", "Root Neck", "Root Head", "Root Ponytail1",
             "Root EyeLids", "Root Brow", "Root Eyes", "Root L Clavicle", "Root L UpperArm", "Root L Forearm", "Root L Hand",
             "Root L Finger0", "Root L Finger1", "Root L Finger11", "Left_Shoulder", "Root R Clavicle", "Root R UpperArm",
             "Root R Forearm", "Root R Hand", "Root R Finger0", "Root R Finger1", "Root R Finger11", "Right_Shoulder", "ARROW"]


def _q1002(w0, w1):
    x = ((w0 >> 21) & 1023) / 1023.0 * (-1 if (w0 >> 20) & 1 else 1)
    y = (w1 & 1023) / 1023.0 * (-1 if (w0 >> 31) & 1 else 1)
    z = ((w1 >> 11) & 1023) / 1023.0 * (-1 if (w1 >> 10) & 1 else 1)
    w = ((w1 >> 22) & 1023) / 1023.0 * (-1 if (w1 >> 21) & 1 else 1)
    return (w, x, y, z)


def _t1004(u):
    x = (u & 1023) * (-1 if (u >> 10) & 1 else 1)
    y = ((u >> 11) & 1023) * (-1 if (u >> 21) & 1 else 1)
    z = ((u >> 22) & 511) * (-1 if (u >> 31) & 1 else 1)
    return (x * 0.01, y * 0.01, z * 0.01)


class Anim:
    def __init__(self, index, typ, duration, keys, positions):
        self.index, self.type, self.duration = index, typ, duration
        self.keys = keys          # list of (prev, t01, quat wxyz, trans or None)
        self.positions = positions  # list of (keyindex, (x, y, z) metres)
        heads = 0
        while heads < len(keys) and keys[heads][0] == 0 and keys[heads][1] == 0.0:
            heads += 1
        # the second key of bone 0 also says prev 0 (meaning "key 0") but has a non-zero time: heads stop there
        self.nbones = max(1, heads)
        bone = []
        for i, k in enumerate(keys):
            bone.append(i if i < self.nbones else bone[k[0]])
        self.bone = bone
        self.tracks = [[] for _ in range(self.nbones)]   # per bone: [(t01, quat, trans)]
        for i, k in enumerate(keys):
            if np.dot(k[2], k[2]) < 1e-6:
                continue                                   # null key: no data for this bone
            self.tracks[bone[i]].append((k[1], np.array(k[2]), k[3]))
        self.pos_tracks = {}
        for ki, p in positions:
            if ki < len(keys):
                self.pos_tracks.setdefault(bone[ki], []).append((keys[ki][1], np.array(p)))
        for v in self.pos_tracks.values():
            v.sort(key=lambda e: e[0])

    def sample(self, t01):
        """-> {bone index: (quat wxyz, translation or None)} at normalised time t01."""
        out = {}
        for b, tr in enumerate(self.tracks):
            if not tr:
                continue
            q, tv = _interp(tr, t01)
            out[b] = (q, tv)
        for b, tr in self.pos_tracks.items():
            p = _lerp_pos(tr, t01)
            q = out.get(b, (None, None))[0]
            out[b] = (q, p)
        return out


def _slerp(a, b, u):
    if np.dot(a, b) < 0:
        b = -b
    d = np.clip(np.dot(a, b), -1, 1)
    if d > 0.9995:
        r = a + u * (b - a)
    else:
        th = np.arccos(d)
        r = (np.sin((1 - u) * th) * a + np.sin(u * th) * b) / np.sin(th)
    return r / np.linalg.norm(r)


def _interp(tr, t):
    if t <= tr[0][0] or len(tr) == 1:
        return tr[0][1], tr[0][2]
    for i in range(1, len(tr)):
        if t <= tr[i][0]:
            t0, q0, p0 = tr[i - 1]; t1, q1, p1 = tr[i]
            u = 0 if t1 <= t0 else (t - t0) / (t1 - t0)
            p = None if p0 is None or p1 is None else np.array(p0) * (1 - u) + np.array(p1) * u
            return _slerp(q0, q1, u), p
    return tr[-1][1], tr[-1][2]


def _lerp_pos(tr, t):
    if t <= tr[0][0] or len(tr) == 1:
        return tr[0][1]
    for i in range(1, len(tr)):
        if t <= tr[i][0]:
            t0, p0 = tr[i - 1]; t1, p1 = tr[i]
            u = 0 if t1 <= t0 else (t - t0) / (t1 - t0)
            return p0 * (1 - u) + p1 * u
    return tr[-1][1]


def _is_header(b, p):
    if p + 20 > len(b):
        return False
    tag, typ, n, z = struct.unpack_from("<4I", b, p)
    return tag == 0x100 and typ in KEYSIZE and z == 0 and 0 < n < 200000


def parse(b):
    anims, p = [], 0
    while p + 20 <= len(b) and _is_header(b, p):
        _, typ, n, _ = struct.unpack_from("<4I", b, p)
        dur, = struct.unpack_from("<f", b, p + 16)
        p += 20
        ks = KEYSIZE[typ]
        keys = []
        for i in range(n):
            o = p + i * ks
            trans = None
            if typ in (1002, 1004):
                w0, w1 = struct.unpack_from("<II", b, o)
                prev, t = w0 & 2047, ((w0 >> 11) & 511) / 511.0
                q = _q1002(w0, w1)
                if typ == 1004:
                    trans = _t1004(struct.unpack_from("<I", b, o + 8)[0])
            elif typ in (999, 1000):
                prev, tt = struct.unpack_from("<HH", b, o)
                t = tt / 65535.0
                q = struct.unpack_from("<4f", b, o + 4)
                if typ == 999:
                    trans = struct.unpack_from("<3f", b, o + 20)
            else:  # 1001, 1003: i16 x y z w
                prev, tt = struct.unpack_from("<HH", b, o)
                t = tt / 65535.0
                x, y, z, w = (v / 32767.0 for v in struct.unpack_from("<4h", b, o + 4))
                q = (w, x, y, z)
                if typ == 1003:
                    trans = tuple(v / 1000.0 for v in struct.unpack_from("<3h", b, o + 12))
            keys.append((prev, t, q, trans))
        p += n * ks
        # position records until the next header (or the end, ignoring sector padding)
        positions = []
        if typ in (1000, 1001, 1002):
            while p + 8 <= len(b) and not _is_header(b, p):
                ki, x, y, z = struct.unpack_from("<H3h", b, p)
                if ki == 0 and x == 0 and y == 0 and z == 0 and all(v == 0 for v in b[p:p + 64]):
                    break
                positions.append((ki, (x / 1000.0, y / 1000.0, z / 1000.0)))
                p += 8
        anims.append(Anim(len(anims), typ, dur, keys, positions))
        # sector padding / alignment: step over zero bytes until the next animation header
        while p + 20 <= len(b) and not _is_header(b, p) and b[p] == 0:
            p += 1
    return anims


def load(name):
    """Group by name: from World.img, or Bully's Anim folder (C_Player.agr, Grap.agr...)."""
    import paths
    loose = paths.bully() / "Anim" / name
    if loose.exists():
        return parse(loose.read_bytes())
    from imgdir import read
    return parse(read(name))


if __name__ == "__main__":
    import sys
    for name in sys.argv[1:]:
        an = load(name)
        print(name, len(an), "animations")
        for a in an[:400]:
            print(f"  #{a.index:3d} type {a.type} {a.duration:6.2f}s keys {len(a.keys):5d} bones {a.nbones:2d} pos {len(a.positions)}")
