"""Bully's logo lettering for BullyLink's main menu title (Menu.cs): Austin's install TXD/liNA.nft -> BullyB2.tga holds
the gold serif letters B U L Y (the loading screens reuse the L); cut them apart at the empty columns and set BULLY.
Black background -> transparent (alpha from brightness, so the letters' soft edges stay soft).
python export_logo.py   -> ../assets/out/bully_logo.png
"""
from pathlib import Path
import numpy as np
from PIL import Image
from nif import Nif, decode_pixels
import paths


def main():
    n = Nif((paths.bully() / "TXD" / "liNA.nft").read_bytes())
    px = None
    for b in n.blocks:
        if b["type"] == "NiSourceTexture" and "BullyB2" in b["file"]:
            px = decode_pixels(n, n.blocks[b["pixels"]])
    assert px is not None, "BullyB2 not found"
    im = np.asarray(Image.fromarray(px).convert("RGBA")).astype(np.float32)
    lum = im[..., :3].max(axis=2)
    ink = lum > 40
    cols = ink.any(axis=0)
    # letter spans = runs of inked columns
    spans, start = [], None
    for x, c in enumerate(cols):
        if c and start is None: start = x
        if not c and start is not None: spans.append((start, x)); start = None
    if start is not None: spans.append((start, len(cols)))
    spans = [s for s in spans if s[1] - s[0] > 4]
    print("letter spans:", spans)
    assert len(spans) == 4, "expected B U L Y"
    rows = np.where(ink.any(axis=1))[0]
    top, bot = rows.min(), rows.max() + 1
    alpha = np.clip((lum - 20) / 90.0, 0, 1) * 255
    rgba = im.copy(); rgba[..., 3] = alpha
    letters = [rgba[top:bot, a:b] for a, b in spans]
    B, U, L, Y = letters
    gap = 4
    seq = [B, U, L, L, Y]
    w = sum(l.shape[1] for l in seq) + gap * (len(seq) - 1) + 8
    h = bot - top + 8
    out = np.zeros((h, w, 4), np.float32)
    x = 4
    for l in seq:
        out[4:4 + l.shape[0], x:x + l.shape[1]] = l
        x += l.shape[1] + gap
    img = Image.fromarray(out.astype(np.uint8), "RGBA")
    img = img.resize((img.width * 4, img.height * 4), Image.LANCZOS)     # crisp at title size
    paths.OUT.mkdir(parents=True, exist_ok=True)
    dst = paths.OUT / "bully_logo.png"
    img.save(dst)
    print("wrote", dst, img.size)


if __name__ == "__main__":
    main()
