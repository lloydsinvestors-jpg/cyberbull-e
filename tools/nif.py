"""Minimal Gamebryo 20.3.0.9 NIF reader for Bully: Scholarship Edition (little-endian files only).
Written from the format notes in LiquidSnake0/bully-re docs/nif.md + docs/nft.md (no code copied).
Decodes: NiNode, NiTriShape(Data), NiTriStrips(Data), NiSkinInstance, NiSkinData, NiTexturingProperty,
NiSourceTexture, NiMaterialProperty, NiAlphaProperty, NiStencilProperty, NiPixelData, NiPalette. Others are skipped by size.
"""
import struct
import numpy as np


class R:
    """Reader; e = "<" little-endian, ">" big-endian (Bully's console-exported CS_* files and the SCOOTER)."""
    def __init__(self, b, p=0, e="<"):
        self.b, self.p, self.e = b, p, e

    def u8(self): v = self.b[self.p]; self.p += 1; return v
    def u16(self): v, = struct.unpack_from(self.e + "H", self.b, self.p); self.p += 2; return v
    def u32(self): v, = struct.unpack_from(self.e + "I", self.b, self.p); self.p += 4; return v
    def i32(self): v, = struct.unpack_from(self.e + "i", self.b, self.p); self.p += 4; return v
    def f(self): v, = struct.unpack_from(self.e + "f", self.b, self.p); self.p += 4; return v
    def fs(self, n): v = struct.unpack_from(f"{self.e}{n}f", self.b, self.p); self.p += 4 * n; return v
    def arr(self, n, dt): return np.frombuffer(self.raw(n * np.dtype(dt).itemsize), (self.e + dt)).astype(dt).copy()
    def raw(self, n): v = self.b[self.p:self.p + n]; self.p += n; return v
    def refs(self): return [self.i32() for _ in range(self.u32())]


class Block(dict):
    pass


class Nif:
    def __init__(self, data):
        nl = data.index(b"\n")
        self.header = data[:nl].decode()
        r = R(data, nl + 1)
        self.version = r.u32()
        endian = r.u8()
        r.u32()  # user version
        nblocks = r.u32()          # version, endian flag and block count are always little-endian (docs/nft.md)
        E = "<" if endian == 1 else ">"
        r.e = E
        self.endian = E
        types = []
        for _ in range(r.u16()):
            types.append(r.raw(r.u32()).decode())
        btype = [types[r.u16()] for _ in range(nblocks)]
        bsize = [r.u32() for _ in range(nblocks)]
        nstr = r.u32(); r.u32()
        self.strings = [r.raw(r.u32()).decode("latin-1") for _ in range(nstr)]
        for _ in range(r.u32()):
            r.u32()
        self.blocks = []
        for i in range(nblocks):
            start = r.p
            b = Block(type=btype[i], index=i)
            fn = getattr(self, "_" + btype[i], None)
            if fn:
                fn(R(data, start, E), b)
            self.blocks.append(b)
            r.p = start + bsize[i]

    def s(self, idx):
        return self.strings[idx] if 0 <= idx < len(self.strings) else None

    # --- common parts ---
    def _obj(self, r, b):
        b["name"] = self.s(r.i32()); b["extra"] = r.refs(); b["controller"] = r.i32()

    def _av(self, r, b):
        self._obj(r, b)
        b["flags"] = r.u16()
        b["t"] = np.array(r.fs(3)); b["R"] = np.array(r.fs(9)).reshape(3, 3); b["s"] = r.f()
        b["props"] = r.refs(); b["collision"] = r.i32()

    def _NiNode(self, r, b):
        self._av(r, b); b["children"] = r.refs(); b["effects"] = r.refs()

    def _shape(self, r, b):
        self._av(r, b)
        b["data"] = r.i32(); b["skin"] = r.i32()
        n = r.u32(); [r.i32() for _ in range(n)]; [r.i32() for _ in range(n)]
        r.i32(); r.u8()

    _NiTriShape = _shape
    _NiTriStrips = _shape

    def _geom(self, r, b):
        r.i32(); n = r.u16(); r.u8(); r.u8()
        b["nverts"] = n
        b["verts"] = r.arr(3 * n, "f4").reshape(n, 3) if r.u8() else None
        flags = r.u16(); uvsets = flags & 0x3F
        if r.u8():
            b["normals"] = r.arr(3 * n, "f4").reshape(n, 3)
            if flags & 0x1000:
                r.raw(24 * n)
        else:
            b["normals"] = None
        r.fs(4)
        b["colors"] = r.arr(4 * n, "f4").reshape(n, 4) if r.u8() else None
        b["uvs"] = [r.arr(2 * n, "f4").reshape(n, 2) for _ in range(uvsets)]
        r.u16(); r.i32()

    def _NiTriShapeData(self, r, b):
        self._geom(r, b)
        nt = r.u16(); r.u32()
        b["tris"] = r.arr(3 * nt, "u2").reshape(nt, 3).astype(np.int32) if r.u8() else np.zeros((0, 3), np.int32)

    def _NiTriStripsData(self, r, b):
        self._geom(r, b)
        r.u16(); ns = r.u16(); lens = [r.u16() for _ in range(ns)]
        tris = []
        if r.u8():
            for L in lens:
                st = [r.u16() for _ in range(L)]
                for k in range(L - 2):
                    a, c, d = st[k], st[k + 1], st[k + 2]
                    if a == c or c == d or a == d:
                        continue
                    tris.append((a, c, d) if k % 2 == 0 else (a, d, c))
        b["tris"] = np.array(tris, np.int32).reshape(-1, 3)

    def _NiSkinInstance(self, r, b):
        b["data"] = r.i32(); b["partition"] = r.i32(); b["root"] = r.i32(); b["bones"] = r.refs()

    def _xf(self, r):
        Rm = np.array(r.fs(9)).reshape(3, 3); t = np.array(r.fs(3)); s = r.f()
        return Rm, t, s

    def _NiSkinData(self, r, b):
        b["xf"] = self._xf(r)
        nb = r.u32(); hw = r.u8()
        bones = []
        for _ in range(nb):
            xf = self._xf(r); r.fs(4); nv = r.u16()
            w = []
            if hw:
                for _ in range(nv):
                    i = r.u16(); w.append((i, r.f()))
            bones.append({"xf": xf, "weights": w})
        b["bones"] = bones

    def _NiTexturingProperty(self, r, b):
        self._obj(r, b); r.u16()
        slots = []
        for k in range(r.u32()):
            if r.u8():
                src = r.i32(); r.u16()
                if r.u8():
                    r.fs(5); r.u32(); r.fs(2)
                if k == 5:
                    r.fs(6)
                if k == 7:
                    r.f()
                slots.append(src)
            else:
                slots.append(-1)
        b["slots"] = slots

    def _NiSourceTexture(self, r, b):
        self._obj(r, b)
        b["external"] = r.u8(); b["file"] = self.s(r.i32()); b["pixels"] = r.i32()

    def _NiMaterialProperty(self, r, b):
        self._obj(r, b)
        b["ambient"] = r.fs(3); b["diffuse"] = r.fs(3); b["specular"] = r.fs(3); b["emissive"] = r.fs(3)
        b["gloss"] = r.f(); b["alpha"] = r.f()

    def _NiAlphaProperty(self, r, b):
        self._obj(r, b); b["aflags"] = r.u16(); b["threshold"] = r.u8()

    def _NiStencilProperty(self, r, b):
        self._obj(r, b); b["sflags"] = r.u16()

    def _NiPalette(self, r, b):
        r.u8(); n = r.u32(); b["pal"] = np.frombuffer(r.raw(4 * n), np.uint8).reshape(n, 4).copy()

    def _NiPixelData(self, r, b):
        b["fmt"] = r.u32(); r.u8(); r.i32(); r.u32(); r.u8(); r.u32(); r.u8()
        ch = []
        for _ in range(4):
            ch.append((r.u32(), r.u32(), r.u8(), r.u8()))
        b["channels"] = ch
        b["palette"] = r.i32()
        nm = r.u32(); r.u32()
        b["mips"] = [(r.u32(), r.u32(), r.u32()) for _ in range(nm)]
        npx = r.u32(); r.u32()
        b["px"] = r.raw(npx)

    # --- helpers ---
    def named(self, name):
        for b in self.blocks:
            if b.get("name") == name:
                return b

    def roots(self):
        kids = set()
        for b in self.blocks:
            kids.update(c for c in b.get("children", []) if c >= 0)
        return [b for b in self.blocks if b["type"] == "NiNode" and b["index"] not in kids]


def local(b):
    """4x4 for v' = R.(s.v) + t, R as read (row-major)."""
    M = np.eye(4); M[:3, :3] = b["R"] * b["s"]; M[:3, 3] = b["t"]
    return M


def xf4(xf):
    Rm, t, s = xf
    M = np.eye(4); M[:3, :3] = Rm * s; M[:3, 3] = t
    return M


def world_map(nif):
    """block index -> 4x4 world transform (model space) for every NiAVObject reachable from the roots."""
    W = {}
    def walk(i, P):
        b = nif.blocks[i]
        M = P @ local(b); W[i] = M
        for c in b.get("children", []):
            if c >= 0 and "R" in nif.blocks[c]:
                walk(c, M)
    for rt in nif.roots():
        walk(rt["index"], np.eye(4))
    return W


# ---- texture decode ----
def _565(c):
    r = (c >> 11) & 31; g = (c >> 5) & 63; b = c & 31
    return np.array([(r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2)], np.int32)


def _dxt_color(blk, rgba, x0, y0, w, dxt1):
    c0, c1 = struct.unpack_from("<HH", blk, 0)
    a, b = _565(c0), _565(c1)
    if c0 > c1 or not dxt1:
        cols = [a, b, (2 * a + b) // 3, (a + 2 * b) // 3]; alpha = [255] * 4
    else:
        cols = [a, b, (a + b) // 2, np.zeros(3, np.int32)]; alpha = [255, 255, 255, 0]
    bits, = struct.unpack_from("<I", blk, 4)
    for k in range(16):
        idx = (bits >> (2 * k)) & 3
        y, x = y0 + k // 4, x0 + k % 4
        if y < rgba.shape[0] and x < rgba.shape[1]:
            rgba[y, x, :3] = cols[idx]
            if dxt1:
                rgba[y, x, 3] = alpha[idx]


def _dxt5_alpha(blk, rgba, x0, y0):
    a0, a1 = blk[0], blk[1]
    if a0 > a1:
        al = [a0, a1] + [((6 - i) * a0 + (i + 1) * a1) // 7 for i in range(6)]
    else:
        al = [a0, a1] + [((4 - i) * a0 + (i + 1) * a1) // 5 for i in range(4)] + [0, 255]
    bits = int.from_bytes(blk[2:8], "little")
    for k in range(16):
        y, x = y0 + k // 4, x0 + k % 4
        if y < rgba.shape[0] and x < rgba.shape[1]:
            rgba[y, x, 3] = al[(bits >> (3 * k)) & 7]


def decode_pixels(nif, pd):
    w, h, off = pd["mips"][0]
    px = pd["px"]; fmt = pd["fmt"]
    rgba = np.full((h, w, 4), 255, np.uint8)
    if fmt in (4, 6):
        bs = 8 if fmt == 4 else 16
        p = off
        for by in range(0, h, 4):
            for bx in range(0, w, 4):
                blk = px[p:p + bs]; p += bs
                if fmt == 4:
                    _dxt_color(blk, rgba, bx, by, w, True)
                else:
                    _dxt5_alpha(blk, rgba, bx, by)
                    _dxt_color(blk[8:], rgba, bx, by, w, False)
    elif fmt in (0, 1):
        n = 3 if fmt == 0 else 4
        a = np.frombuffer(px[off:off + w * h * n], np.uint8).reshape(h, w, n)
        rgba[..., :n] = a
    elif fmt == 2:
        pal = nif.blocks[pd["palette"]]["pal"]
        rgba[:] = pal[np.frombuffer(px[off:off + w * h], np.uint8).reshape(h, w)]
    else:
        raise ValueError(f"pixel format {fmt}")
    return rgba
