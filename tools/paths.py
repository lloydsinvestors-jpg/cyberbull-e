"""Where the games are and where the exports go, for every tool here (bring-your-own-Bully, 2026-10-11).
No machine paths: the games are found through Steam's library list (plus the Rockstar Launcher's default folder), or
set explicitly with configure() / the BULLYLINK_BULLY and BULLYLINK_SCHEDULE environment variables.

Exports land in OUT (models, textures, sounds: the files the mod reads from UserData/BullyLink/data), PEDS (the Bully
adults) and CLOTHES (the wardrobe). By default those are this project's assets/out folders; the public exporter
(byob_export.py) points them at the player's Schedule I/UserData/BullyLink instead.
"""
import os
import re
from pathlib import Path

PROJECT = Path(__file__).resolve().parent.parent
OUT = PROJECT / "assets" / "out"
PEDS = OUT / "peds"
CLOTHES = OUT / "clothes"
RAW = None              # a folder of loose .nif / .nft overrides (dev only); None = always read Stream/World.img
CACHE = PROJECT / "assets" / "raw"
PUBLIC = False          # True in the public exporter: no preview images, no dev side files

BULLY_DIR = "Bully Scholarship Edition"
SCHEDULE_DIR = "Schedule I"
_bully = os.environ.get("BULLYLINK_BULLY")
_schedule = os.environ.get("BULLYLINK_SCHEDULE")


def steam_libraries():
    """Every Steam library folder: Steam's own folder + each "path" in steamapps/libraryfolders.vdf."""
    libs = []
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as k:
            steam = winreg.QueryValueEx(k, "SteamPath")[0]
        libs.append(Path(steam))
        vdf = Path(steam) / "steamapps" / "libraryfolders.vdf"
        if vdf.exists():
            for m in re.finditer(r'"path"\s+"(.+?)"', vdf.read_text(encoding="utf-8", errors="replace")):
                libs.append(Path(m.group(1).replace("\\\\", "\\")))
    except OSError:
        pass
    return libs


def is_bully(d):
    d = Path(d)
    return all((d / p).exists() for p in ("Stream/World.img", "Stream/World.dir", "TXD/effects.nft", "Audio/PLAYLIST/Speech.bin"))


def is_schedule(d):
    d = Path(d)
    return (d / "Schedule I.exe").exists() and (d / "Schedule I_Data").is_dir()


def find_bully():
    cands = [lib / "steamapps" / "common" / BULLY_DIR for lib in steam_libraries()]
    for env in ("ProgramFiles", "ProgramFiles(x86)"):
        if os.environ.get(env):
            cands.append(Path(os.environ[env]) / "Rockstar Games" / BULLY_DIR)
    return next((c for c in cands if is_bully(c)), None)


def find_schedule():
    return next((c for c in (lib / "steamapps" / "common" / SCHEDULE_DIR for lib in steam_libraries()) if is_schedule(c)), None)


def bully():
    global _bully
    if _bully is None:
        _bully = find_bully()
    if _bully is None:
        raise SystemExit("Bully: Scholarship Edition not found (set BULLYLINK_BULLY to its folder)")
    return Path(_bully)


def schedule():
    global _schedule
    if _schedule is None:
        _schedule = find_schedule()
    if _schedule is None:
        raise SystemExit("Schedule I not found (set BULLYLINK_SCHEDULE to its folder)")
    return Path(_schedule)


def configure(bully_dir=None, schedule_dir=None, out_root=None, cache=None):
    """Public export: everything under out_root (= Schedule I/UserData/BullyLink): data/, peds/, clothes/."""
    global _bully, _schedule, OUT, PEDS, CLOTHES, CACHE, PUBLIC, RAW
    if bully_dir: _bully = Path(bully_dir)
    if schedule_dir: _schedule = Path(schedule_dir)
    if out_root:
        root = Path(out_root)
        OUT, PEDS, CLOTHES = root / "data", root / "peds", root / "clothes"
        CACHE = Path(cache) if cache else root / "cache"
        PUBLIC, RAW = True, None
