"""Makes every app and website icon from branding/logo.png (the OpenRewind logo).

    python3 scripts/make-icons.py

Needs Pillow. The outputs are committed, so building the apps does not need this:
  packaging/AppIcon.iconset/      Mac app icon (logo on a white rounded tile)
  windows/OpenRewind/OpenRewind.ico   Windows app and tray icon (logo on transparent)
  website/assets/                 site logo, favicons, Apple touch icon
"""
import os
from PIL import Image, ImageDraw, ImageFilter

ROOT = os.path.join(os.path.dirname(__file__), "..")
LOGO = Image.open(os.path.join(ROOT, "branding/logo.png")).convert("RGBA")
TIGHT = LOGO.crop(LOGO.getbbox())


def fit(px, frac):
    """The logo centered on a transparent px square, its wide side frac of px."""
    w, h = TIGHT.size
    k = px * frac / max(w, h)
    lg = TIGHT.resize((max(1, round(w * k)), max(1, round(h * k))), Image.LANCZOS)
    out = Image.new("RGBA", (px, px), (0, 0, 0, 0))
    # Nudge right a little: the chevrons point left, so optical center sits right of the box center.
    out.paste(lg, ((px - lg.width) // 2 + round(px * 0.01), (px - lg.height) // 2), lg)
    return out


def tile(px, full_bleed=False):
    """White rounded tile with the logo, on Apple's icon grid (824/1024 body)."""
    k = 4
    s = px * k
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    inset = 0 if full_bleed else s * 100 / 1024
    box = [inset, inset, s - inset, s - inset]
    if not full_bleed:
        shadow = Image.new("RGBA", (s, s), (0, 0, 0, 0))
        ImageDraw.Draw(shadow).rounded_rectangle([box[0], box[1] + s * 0.012, box[2], box[3] + s * 0.012],
                                                 radius=s * 0.185, fill=(0, 0, 0, 70))
        img = Image.alpha_composite(img, shadow.filter(ImageFilter.GaussianBlur(s * 0.012)))
    body = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    bd = ImageDraw.Draw(body)
    top, bottom = (255, 255, 255), (236, 241, 252)
    for y in range(s):
        t = y / s
        bd.line([(0, y), (s, y)], fill=tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,))
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle(box, radius=0 if full_bleed else s * 0.185, fill=255)
    img.paste(body, (0, 0), mask)
    body_px = s - 2 * inset
    lg = fit(round(body_px), 0.62)
    img.alpha_composite(lg, (round(inset), round(inset)))
    return img.resize((px, px), Image.LANCZOS)


def save(img, *path):
    p = os.path.join(ROOT, *path)
    os.makedirs(os.path.dirname(p), exist_ok=True)
    img.save(p)


# Mac
for name, px in [("16x16", 16), ("16x16@2x", 32), ("32x32", 32), ("32x32@2x", 64), ("128x128", 128),
                 ("128x128@2x", 256), ("256x256", 256), ("256x256@2x", 512), ("512x512", 512), ("512x512@2x", 1024)]:
    save(tile(px), "packaging/AppIcon.iconset", f"icon_{name}.png")

# Windows: tighter margin at tray sizes so the shape stays readable.
sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
frames = [fit(n, 0.98 if n <= 24 else 0.92) for n in sizes]
ico = os.path.join(ROOT, "windows/OpenRewind/OpenRewind.ico")
frames[-1].save(ico, sizes=[(n, n) for n in sizes], append_images=frames[:-1])

# Website
save(TIGHT.resize((240, round(240 * TIGHT.height / TIGHT.width)), Image.LANCZOS), "website/assets/logo.png")
save(fit(64, 0.96), "website/assets/favicon.png")
save(tile(180, full_bleed=True).convert("RGB"), "website/assets/apple-touch-icon.png")
fit(48, 0.96).save(os.path.join(ROOT, "website/assets/favicon.ico"), sizes=[(16, 16), (32, 32), (48, 48)])
save(tile(1024), "website/assets/app-icon.png")
print("done")
