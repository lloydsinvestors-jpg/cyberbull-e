"""Gate for the public CyberBull-E download: fails if anything Bully-derived or any dev-machine path is in the package.
python release/check_release.py <stage folder>     (make_release.py runs it before zipping)

  * no staged file matches (sha256) any Bully-derived file on the dev machine: the exports (assets/out, peds, clothes),
    the raw extracts, contact sheets, the private Unity project's textures + bundle
  * the mod DLL: no PNG inside, none of the export formats' headers, and its one big blob is the public bundle
  * the public bundle: exactly the 8 placeholder particle textures (plain white dots), nothing else image-like
  * the launcher: the only PNGs inside are its own icon's
  * the exporter: code + libraries only (no images / audio / model data files)
  * no user / machine names or Steam library paths anywhere (UTF-8 or UTF-16)
"""
import hashlib
import io
import os
import re
import sys
from pathlib import Path

PROJECT = Path(__file__).resolve().parent.parent
PRIVATE_UNITY = Path(os.environ.get("BULLYLINK_PRIVATE_UNITY", "D:/Unity/BullyLinkAssets"))   # dev machine only; absent = skipped
HEADERS = [b"JIMY\x01\x00\x00\x00", b"CLTH\x01\x00\x00\x00", b"PROP\x02\x00\x00\x00", b"ANIM\x01\x00\x00\x00", b"SFX1",
           b"SHOP\x01\x00\x00\x00", b"SHOP\x02\x00\x00\x00", b"Gamebryo File Format"]
# machine strings: this Windows account's names + release/machine_strings.txt (one per line, gitignored, never published)
MACHINE = sorted({s for s in (os.environ.get("USERNAME"), Path.home().name, Path(os.environ.get("TEMP", "")).parent.parent.parent.name,
                              "Steam" + "Library") if s and len(s) > 3} |   # split: the checker must not flag itself
                 {l.strip() for l in (Path(__file__).with_name("machine_strings.txt").read_text(encoding="utf-8").splitlines()
                   if Path(__file__).with_name("machine_strings.txt").exists() else []) if l.strip()})
DATA_EXT = {".png", ".jpg", ".tga", ".dds", ".wav", ".ogg", ".mp3", ".bytes", ".nif", ".nft", ".agr", ".img", ".rsm", ".sea", ".pkl"}
PNG = b"\x89PNG\r\n\x1a\n"


def sha(b):
    return hashlib.sha256(b).hexdigest()


def known_bully_hashes():
    roots = [PROJECT / "assets" / d for d in ("out", "raw", "fx", "hud", "proptex", "tex", "anims")]
    roots += [PRIVATE_UNITY / "Assets" / "Bully" / "Tex", PRIVATE_UNITY / "Bundle"]
    out = {}
    for r in roots:
        if r.exists():
            for f in r.rglob("*"):
                if f.is_file() and f.suffix != ".meta" and f.stat().st_size > 64:
                    out[sha(f.read_bytes())] = f
    return out


def png_blobs(b):
    """Every PNG embedded in a binary, cut at its IEND chunk."""
    out, i = [], b.find(PNG)
    while i >= 0:
        e = b.find(b"IEND", i)
        if e < 0: break
        out.append(b[i:e + 8]); i = b.find(PNG, e)
    return out


def check(stage):
    stage = Path(stage)
    fails = []
    known = known_bully_hashes()
    print(f"check: {len(known)} Bully-derived reference files on this machine")
    files = [f for f in stage.rglob("*") if f.is_file()]

    for f in files:
        b = f.read_bytes()
        rel = f.relative_to(stage)
        if sha(b) in known:
            fails.append(f"{rel} IS a Bully-derived file ({known[sha(b)].name})")
        for s in MACHINE:
            if s.encode() in b or s.encode("utf-16-le") in b:
                fails.append(f"{rel} contains the machine string '{s}'")
        if "exporter" in rel.parts and f.suffix.lower() in DATA_EXT:
            fails.append(f"{rel}: data file in the exporter")

    dll = stage / "payload" / "BullyLink.dll"
    db = dll.read_bytes()
    if png_blobs(db): fails.append("BullyLink.dll has PNG images inside")
    for h in HEADERS:
        if h in db: fails.append(f"BullyLink.dll contains an export header {h[:4]!r}")
    bundle = (PROJECT / "assets" / "public" / "bullylink").read_bytes()
    if bundle not in db: fails.append("BullyLink.dll doesn't carry assets/public/bullylink")
    if len(db) - len(bundle) > 1_500_000: fails.append(f"BullyLink.dll is {len(db) - len(bundle)} bytes besides the bundle: something else big is embedded")

    import UnityPy
    import numpy as np
    env = UnityPy.load(io.BytesIO(bundle))
    tex = [o.read() for o in env.objects if o.type.name == "Texture2D"]
    others = sorted({o.type.name for o in env.objects} - {"Texture2D", "Material", "Shader", "GameObject", "Transform", "ParticleSystem", "ParticleSystemRenderer", "AssetBundle"})
    if others: fails.append(f"public bundle has unexpected object types {others}")
    if len(tex) != 8: fails.append(f"public bundle has {len(tex)} textures (expected the 8 placeholders)")
    for t in tex:
        px = np.asarray(t.image.convert("RGBA"))
        if not t.m_Name.startswith("fx_") or px.shape[0] > 32 or px.shape[1] > 32 or px[..., :3].min() < 250:
            fails.append(f"public bundle texture {t.m_Name} ({px.shape[1]}x{px.shape[0]}) isn't a plain placeholder dot")

    ico_pngs = set(sha(p) for p in png_blobs((PROJECT / "launcher" / "app.ico").read_bytes()))
    for p in png_blobs((stage / "CyberBull-E Launcher.exe").read_bytes()):
        if sha(p) not in ico_pngs: fails.append("the launcher has a PNG inside that isn't its icon")

    for f in fails: print("  FAIL", f)
    print(f"check: {len(files)} files, {'PASS' if not fails else f'{len(fails)} FAILURE(S)'}")
    return not fails


if __name__ == "__main__":
    sys.exit(0 if check(sys.argv[1]) else 1)
