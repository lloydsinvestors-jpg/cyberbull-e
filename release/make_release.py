"""Build the public CyberBull-E download (the BullyLink mod's public name) (bring-your-own-Bully, 2026-10-11, for Nexus / GitHub Releases).
Run on the dev machine:  python release/make_release.py

  1. the public effects bundle (D:/Unity/BullyLinkPublic/Bundle/bullylink: placeholder dots, no Bully pixels)
     -> assets/public/bullylink, the mod's only embedded resource
  2. the mod (dotnet build -c Release)            -> bin/Release/net6.0/BullyLink.dll
  3. the launcher (dotnet publish)                -> launcher/publish/CyberBull-E Launcher.exe
  4. the exporter (PyInstaller, folder build)     -> tools/dist/CyberBull-E Exporter/
  5. staged as release/CyberBull-E-<version>/ :  CyberBull-E Launcher.exe, README.txt,
         payload/BullyLink.dll, payload/export_version.txt, payload/exporter/...
  6. check_release.py over the stage (no Bully-derived bytes, no machine paths), then the zip.
Nothing here uploads anything: publishing the zip is Austin's call.
"""
import hashlib
import os
import re
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

PROJECT = Path(__file__).resolve().parent.parent
REL = PROJECT / "release"
TOOLS = PROJECT / "tools"
UNITY_BUNDLE = Path(os.environ.get("BULLYLINK_PUBLIC_BUNDLE", "D:/Unity/BullyLinkPublic/Bundle/bullylink"))   # fresh Unity build, if any


def run(cmd, cwd):
    print(">", " ".join(str(c) for c in cmd), flush=True)
    r = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True)
    if r.returncode != 0:
        print(r.stdout[-4000:], r.stderr[-4000:])
        raise SystemExit(f"FAILED: {cmd[0]} (exit {r.returncode})")
    return r.stdout


def main():
    version = re.search(r'MelonInfo\(typeof\(BullyLink\.Mod\), "BullyLink", "([\d.]+)"', (PROJECT / "Mod.cs").read_text(encoding="utf-8")).group(1)
    export_version = re.search(r"^VERSION = (\d+)", (TOOLS / "byob_export.py").read_text(encoding="utf-8"), re.M).group(1)
    print(f"CyberBull-E {version}, exporter v{export_version}")

    # 1. public bundle
    pub = PROJECT / "assets" / "public" / "bullylink"
    pub.parent.mkdir(parents=True, exist_ok=True)
    if UNITY_BUNDLE.exists():
        shutil.copy2(UNITY_BUNDLE, pub)
    if not pub.exists():
        raise SystemExit(f"no public bundle: build it first (D:/Unity/BullyLinkPublic, BuildBully.Build)")

    # 2-4. builds
    run(["dotnet", "build", "-c", "Release"], PROJECT)
    run(["dotnet", "publish", "-c", "Release", "-o", "publish"], PROJECT / "launcher")
    work = REL / "_build"
    run([sys.executable, "-m", "PyInstaller", "--noconfirm", "--clean", "--onedir", "--console", "--name", "CyberBull-E Exporter",
         "--distpath", str(TOOLS / "dist"), "--workpath", str(work / "pyi"), "--specpath", str(work),
         "--add-data", f"{TOOLS / 'anim_picks.txt'};.", "--collect-all", "UnityPy",
         "--exclude-module", "tkinter", "--exclude-module", "matplotlib", "--exclude-module", "scipy",
         "--exclude-module", "pandas", "--exclude-module", "IPython", "byob_export.py"], TOOLS)

    # 5. stage
    name = f"CyberBull-E-{version}"
    stage = REL / name
    if stage.exists():
        shutil.rmtree(stage)
    payload = stage / "payload"
    payload.mkdir(parents=True)
    shutil.copy2(PROJECT / "launcher" / "publish" / "CyberBull-E Launcher.exe", stage)
    shutil.copy2(REL / "README.txt", stage / "README.txt")
    shutil.copy2(PROJECT / "bin" / "Release" / "net6.0" / "BullyLink.dll", payload)
    (payload / "export_version.txt").write_text(export_version + "\n")
    shutil.copytree(TOOLS / "dist" / "CyberBull-E Exporter", payload / "exporter")

    # 6. check, then zip
    import check_release
    if not check_release.check(stage):
        raise SystemExit("RELEASE CHECK FAILED: nothing zipped")
    zpath = REL / f"{name}.zip"
    zpath.unlink(missing_ok=True)
    with zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for f in sorted(stage.rglob("*")):
            if f.is_file():
                z.write(f, Path(name) / f.relative_to(stage))
    h = hashlib.sha256(zpath.read_bytes()).hexdigest()
    print(f"wrote {zpath} ({zpath.stat().st_size / 1e6:.1f} MB) sha256 {h}")


if __name__ == "__main__":
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    main()
