"""Placeholder particle textures for the PUBLIC bullylink bundle (bring-your-own-Bully, 2026-10-11).
The public bundle ships with no Bully pixels: each fx_<name>.png becomes a plain soft white dot made here (a radial
falloff, nothing derived from Bully). At runtime Bundle.cs swaps in the player's own textures, exported from their Bully
install into UserData/BullyLink/data/fx_<name>.png. If those are missing the effects still draw, as soft puffs.
python make_placeholders.py <Assets/Bully/Tex dir>
"""
import sys
from pathlib import Path
import numpy as np
from PIL import Image

FX = ["smoke5", "ash", "geo_dust1", "EggSplat01", "Sparks01", "smoke2", "HitImpact01", "Fireball3"]


def dot(n=32):
    y, x = np.mgrid[0:n, 0:n]
    r = np.hypot(x - (n - 1) / 2, y - (n - 1) / 2) / (n / 2)
    a = np.clip(1 - r, 0, 1) ** 1.6
    px = np.zeros((n, n, 4), np.uint8)
    px[..., :3] = 255
    px[..., 3] = (a * 255).astype(np.uint8)
    return Image.fromarray(px, "RGBA")


if __name__ == "__main__":
    out = Path(sys.argv[1])
    for name in FX:
        dot().save(out / f"fx_{name}.png")
    print("wrote", len(FX), "placeholders to", out)
