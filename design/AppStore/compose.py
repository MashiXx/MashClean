from PIL import Image, ImageDraw, ImageFont, ImageFilter
import os
OUT = "/Users/mashi/mashicode/mstore/MashClean/design/AppStore"
SF = "/System/Library/Fonts/SFNS.ttf"
def font(size, weight):
    f = ImageFont.truetype(SF, size)
    try: f.set_variation_by_axes([100, 96 if size > 80 else 28, 400, weight])
    except Exception: pass
    return f
SHOTS = [
 ("01-smartscan",   "One scan. A cleaner Mac.",            "Smart Scan finds safe junk and app leftovers in seconds."),
 ("02-systemjunk",  "Know what every byte is.",            "Each item explains what it is, which app it belongs to, and why it's safe."),
 ("03-uninstaller", "Uninstall apps completely.",          "Remove apps together with every leftover file they scattered around."),
 ("04-spacelens",   "See where your space went.",          "Space Lens maps your disk so you can drill into the biggest folders."),
 ("05-largeoldfiles", "Find large & forgotten files.",     "Filter by size, kind and last opened. Nothing is preselected — you decide."),
 ("06-duplicates",  "Clear out duplicate files.",          "Identical files are matched by content, so you only keep one copy."),
]
W, H = 2880, 1800
def background():
    top, bottom = (38, 16, 92), (104, 46, 214)
    bg = Image.new("RGB", (W, H))
    d = ImageDraw.Draw(bg)
    for y in range(H):
        t = y / H
        d.line([(0, y), (W, y)], fill=tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)))
    glow = Image.new("L", (W, H), 0)
    ImageDraw.Draw(glow).ellipse((W * 0.15, H * 0.35, W * 0.85, H * 1.4), fill=110)
    glow = glow.filter(ImageFilter.GaussianBlur(220))
    bg.paste(Image.new("RGB", (W, H), (170, 120, 255)), mask=glow)
    return bg
os.makedirs(f"{OUT}/2880x1800", exist_ok=True); os.makedirs(f"{OUT}/1440x900", exist_ok=True)
for i, (name, title, sub) in enumerate(SHOTS, 1):
    img = background()
    d = ImageDraw.Draw(img)
    ft, fs = font(112, 700), font(50, 400)
    d.text((W / 2, 120), title, font=ft, fill="white", anchor="mt")
    d.text((W / 2, 268), sub, font=fs, fill=(225, 214, 255), anchor="mt")
    shot = Image.open(f"{OUT}/raw/{name}.png").convert("RGBA")
    maxw, maxh = 2440, H - 390 - 70
    s = min(maxw / shot.width, maxh / shot.height)
    shot = shot.resize((int(shot.width * s), int(shot.height * s)), Image.LANCZOS)
    x, y = (W - shot.width) // 2, 390
    shadow = Image.new("RGBA", img.size, (0, 0, 0, 0))
    a = shot.getchannel("A").point(lambda v: int(v * 0.55))
    shadow.paste((10, 0, 30, 255), (x, y + 30), a)
    shadow = shadow.filter(ImageFilter.GaussianBlur(45))
    img = Image.alpha_composite(img.convert("RGBA"), shadow)
    img.alpha_composite(shot, (x, y))
    img = img.convert("RGB")
    fn = f"{i:02d}-{name.split('-',1)[1]}.png"
    img.save(f"{OUT}/2880x1800/{fn}")
    img.resize((1440, 900), Image.LANCZOS).save(f"{OUT}/1440x900/{fn}")
    print(fn)
