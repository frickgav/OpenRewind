"""Draws OpenRewind.ico, the same design as scripts/make-icon.swift (Mac).

    python3 windows/tools/make-icon.py windows/OpenRewind/OpenRewind.ico

Needs Pillow. The .ico is committed, so building the app does not need this.
"""
import sys
from PIL import Image, ImageDraw


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(len(a)))


def draw(px):
    k = 4  # draw big, then shrink, for smooth edges
    s = px * k
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    inset = s * 0.1
    # Squircle background, deep blue gradient (top lighter).
    grad = Image.new("RGBA", (s, s))
    gd = ImageDraw.Draw(grad)
    top, bottom = (33, 56, 140, 255), (13, 18, 46, 255)
    for y in range(s):
        gd.line([(0, y), (s, y)], fill=lerp(top, bottom, y / s))
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([inset, inset, s - inset, s - inset], radius=s * 0.2, fill=255)
    img.paste(grad, (0, 0), mask)
    d = ImageDraw.Draw(img)
    # Screen (y is from the top here; the Swift version measures from the bottom).
    scr = [s * 0.24, s * (1 - 0.74), s * 0.76, s * (1 - 0.40)]
    d.rounded_rectangle(scr, radius=s * 0.04, fill=(255, 255, 255, 242))
    i = s * 0.025
    d.rounded_rectangle([scr[0] + i, scr[1] + i, scr[2] - i, scr[3] - i], radius=s * 0.025, fill=(46, 110, 245, 255))
    # Timeline with playhead.
    d.rounded_rectangle([s * 0.24, s * (1 - 0.315), s * 0.76, s * (1 - 0.28)], radius=s * 0.0175, fill=(255, 255, 255, 90))
    cx, cy, r = s * 0.58, s * (1 - 0.2975), s * 0.045
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=(255, 107, 89, 255))
    return img.resize((px, px), Image.LANCZOS)


out = sys.argv[1] if len(sys.argv) > 1 else "OpenRewind.ico"
sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
big = draw(256)
big.save(out, sizes=[(n, n) for n in sizes], append_images=[draw(n) for n in sizes if n != 256])
print("wrote", out)
