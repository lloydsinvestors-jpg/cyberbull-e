"""The launcher's icon (2026-10-11, public release): original art, nothing from Bully. A black rounded tile with "BL" in
Windows' Segoe UI Black, Schedule I-ish green. python make_icon.py  -> app.ico (16..256 px)"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

S = 256
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
d.rounded_rectangle([8, 8, S - 8, S - 8], radius=44, fill=(18, 18, 20, 255), outline=(120, 200, 80, 255), width=10)
font = ImageFont.truetype(r"C:\Windows\Fonts\seguibl.ttf", 132)
box = d.textbbox((0, 0), "BL", font=font)
w, h = box[2] - box[0], box[3] - box[1]
d.text(((S - w) / 2 - box[0], (S - h) / 2 - box[1]), "BL", font=font, fill=(120, 200, 80, 255))
out = Path(__file__).with_name("app.ico")
img.save(out, sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
img.save(Path(__file__).with_name("app_preview.png"))
print("wrote", out)
