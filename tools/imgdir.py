"""Bully SE Stream/World.img + World.dir reader (GTA-style archive).
.dir = 32-byte entries: u32 offset (2048-byte sectors), u32 size (sectors), char name[24].
Usage: python imgdir.py list [substring]   |   python imgdir.py get <name> <outdir>
The Bully folder comes from paths.py (Steam lookup or BULLYLINK_BULLY).
"""
import struct, sys
from pathlib import Path
import paths

SECTOR = 2048
_cache = {}


def entries(dirpath=None):
    dirpath = Path(dirpath or paths.bully() / "Stream" / "World.dir")
    if dirpath not in _cache:
        raw = dirpath.read_bytes()
        out = []
        for i in range(0, len(raw) - 31, 32):
            off, size = struct.unpack_from("<II", raw, i)
            name = raw[i + 8:i + 32].split(b"\0", 1)[0].decode("latin-1")
            out.append((name, off, size))
        _cache[dirpath] = out
    return _cache[dirpath]


def read(name, img=None):
    img = img or paths.bully() / "Stream" / "World.img"
    for n, off, size in entries():
        if n.lower() == name.lower():
            with open(img, "rb") as f:
                f.seek(off * SECTOR)
                return f.read(size * SECTOR)
    raise KeyError(name)


if __name__ == "__main__":
    cmd = sys.argv[1]
    if cmd == "list":
        sub = sys.argv[2].lower() if len(sys.argv) > 2 else ""
        es = entries()
        print(len(es), "entries")
        for n, off, size in es:
            if sub in n.lower():
                print(f"{n:24s} {size * SECTOR:>10d}")
    elif cmd == "get":
        out = Path(sys.argv[3]); out.mkdir(parents=True, exist_ok=True)
        (out / sys.argv[2]).write_bytes(read(sys.argv[2]))
        print("wrote", out / sys.argv[2])
