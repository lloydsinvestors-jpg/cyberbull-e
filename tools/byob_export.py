"""BullyLink exporter, bring-your-own-Bully (2026-10-11, Austin: "build the bring your own bully launcher for nexus").
The public mod ships with nothing from Bully in it. This builds every Bully-derived file the mod needs from the player's
own copy of Bully: Scholarship Edition (and the carnival shop's room from their own Schedule I), straight into
Schedule I/UserData/BullyLink:
    data/     Jimmy, the carnie, props + HUD icons, animations, sound effects, the logo, particle textures, the shop
    peds/     the Bully adults for the cast swap (+ roster.txt)
    clothes/  Jimmy's wardrobe (+ clothes.txt)
    data/export.txt   what was built, by which exporter version (the launcher re-runs the export when that changes)
Packed into "CyberBull-E Exporter.exe" (PyInstaller) and run by the CyberBull-E Launcher (public name since 2026-10-11). Read-only on both games.

python byob_export.py --bully "<Bully folder>" --schedule "<Schedule I folder>" [--out "<UserData/BullyLink>"]
"""
import argparse
import sys
import time
import traceback
from datetime import datetime
from pathlib import Path

import paths

VERSION = 1           # bump when any exporter's output changes, so installs re-export
FX = ["smoke5", "ash", "geo_dust1", "EggSplat01", "Sparks01", "smoke2", "HitImpact01", "Fireball3"]


def say(s):
    print(s, flush=True)


def export_fx():
    """The particle textures for the bundle's effects (Bundle.cs swaps them in): TXD/effects.nft, decoded as-is."""
    from PIL import Image
    from nif import Nif, decode_pixels
    n = Nif((paths.bully() / "TXD" / "effects.nft").read_bytes())
    want = {f.lower(): f for f in FX}
    got = 0
    for b in n.blocks:
        if b["type"] == "NiSourceTexture" and b.get("pixels", -1) >= 0:
            key = b["file"].replace("\\", "/").split("/")[-1].rsplit(".", 1)[0].lower()
            if key in want:
                Image.fromarray(decode_pixels(n, n.blocks[b["pixels"]])).save(paths.OUT / f"fx_{want[key]}.png")
                got += 1
    if got < len(FX):
        raise RuntimeError(f"only {got} of {len(FX)} particle textures found in effects.nft")


def steps():
    import export_jimmy, export_props, export_anims, export_sfx, export_logo, export_cast, export_clothes
    # (name, function, required): a required step failing fails the export; the rest just switch that feature off
    return [
        ("Jimmy", lambda: export_jimmy.main(), True),
        ("the carnie", lambda: export_jimmy.main("Carni01", "carnie"), False),   # Carni01 wears TO_Carnie_female's textures
        ("items, rides + HUD icons", export_props.main, True),
        ("animations", export_anims.main, True),
        ("sound effects", export_sfx.main, True),
        ("logo", export_logo.main, False),
        ("particle textures", export_fx, False),
        ("carnival shop room (from Schedule I)", _shop, False),
        ("Bully adults (cast)", lambda: _check(export_cast.run()), False),
        ("Jimmy's wardrobe", lambda: _check(export_clothes.main(set())), False),
    ]


def _shop():
    import export_shop          # here, not up top: UnityPy is the slow import and only this step needs it
    export_shop.main()


def _check(result):
    ok, bad = result
    if ok == 0:
        raise RuntimeError("nothing exported" + (f": {bad[0]}" if bad else ""))


def main():
    ap = argparse.ArgumentParser(description="Build CyberBull-E's Bully files from your own Bully install.")
    ap.add_argument("--bully", help="Bully: Scholarship Edition folder (default: found through Steam)")
    ap.add_argument("--schedule", help="Schedule I folder (default: found through Steam)")
    ap.add_argument("--out", help="output folder (default: <Schedule I>/UserData/BullyLink)")
    a = ap.parse_args()

    paths.configure(bully_dir=a.bully, schedule_dir=a.schedule)
    bully, sched = paths.bully(), paths.schedule()
    if not paths.is_bully(bully):
        say(f"ERROR: {bully} doesn't look like Bully: Scholarship Edition (no Stream/World.img, TXD/effects.nft or Audio/PLAYLIST/Speech.bin)")
        return 2
    out = Path(a.out) if a.out else sched / "UserData" / "BullyLink"
    paths.configure(out_root=out)
    for d in (paths.OUT, paths.PEDS, paths.CLOTHES, paths.CACHE):
        d.mkdir(parents=True, exist_ok=True)
    (paths.OUT / "export.txt").unlink(missing_ok=True)       # no manifest until this run finishes

    say(f"CyberBull-E exporter v{VERSION}")
    say(f"Bully:      {bully}")
    say(f"Schedule I: {sched}")
    say(f"Output:     {out}")
    t0 = time.time()
    todo = steps()
    failed_required, results = [], []
    for i, (name, fn, required) in enumerate(todo, 1):
        say(f"[{i}/{len(todo)}] {name}...")
        t = time.time()
        try:
            fn()
            results.append(f"ok\t{name}")
            say(f"[{i}/{len(todo)}] {name}: done ({time.time() - t:.0f} s)")
        except BaseException as e:            # SystemExit from a tool included
            if isinstance(e, KeyboardInterrupt): raise
            results.append(f"FAILED\t{name}\t{e}")
            say(f"[{i}/{len(todo)}] {name}: FAILED ({e})" + ("" if required else "; the mod runs without it"))
            traceback.print_exc(file=sys.stdout)
            if required: failed_required.append(name)
    if failed_required:
        say(f"EXPORT FAILED: {', '.join(failed_required)}")
        return 1
    (paths.OUT / "export.txt").write_text(
        f"BullyLink export v{VERSION}\ndate\t{datetime.now():%Y-%m-%d %H:%M}\nbully\t{bully}\nschedule\t{sched}\n" + "\n".join(results) + "\n",
        encoding="utf-8")
    say(f"EXPORT OK in {time.time() - t0:.0f} s")
    return 0


if __name__ == "__main__":
    sys.exit(main())
