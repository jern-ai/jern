#!/usr/bin/env python3
"""Rasterize brand/icon.svg's geometry to the PNG sizes the site and GitHub need.

PIL can't parse SVG, so this draws the identical geometry (jera rune, two
chevron polylines, stroke 14, square caps, on a 128 grid) supersampled 8x.

Usage: python3 brand/render.py
"""
import math
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
BOX, S = 128, 8
W = BOX * S

BG = (0x16, 0x18, 0x1D, 255)
GRAD_TOP = (0xD9, 0x7A, 0x48)
GRAD_BOT = (0xB0, 0x55, 0x2F)
STROKE, RADIUS = 12, 28
CHEVRONS = [
    [(46, 24), (83, 43), (46, 62)],
    [(82, 104), (45, 85), (82, 66)],
]


def _unit(a, b):
    dx, dy = b[0] - a[0], b[1] - a[1]
    length = math.hypot(dx, dy)
    return dx / length, dy / length


def _intersect(p, u, q, v):
    """Intersection of lines p + t*u and q + s*v."""
    det = u[0] * v[1] - u[1] * v[0]
    t = ((q[0] - p[0]) * v[1] - (q[1] - p[1]) * v[0]) / det
    return p[0] + t * u[0], p[1] + t * u[1]


def chevron_poly(a, b, c, w):
    """Closed outline of polyline a->b->c: stroke w, square caps, miter join."""
    h = w / 2
    u1, u2 = _unit(a, b), _unit(b, c)
    n1, n2 = (-u1[1], u1[0]), (-u2[1], u2[0])
    cap_a = (a[0] - u1[0] * h, a[1] - u1[1] * h)
    cap_c = (c[0] + u2[0] * h, c[1] + u2[1] * h)
    def off(p, n, sign):
        return (p[0] + sign * n[0] * h, p[1] + sign * n[1] * h)
    miter_l = _intersect(off(a, n1, +1), u1, off(c, n2, +1), u2)
    miter_r = _intersect(off(a, n1, -1), u1, off(c, n2, -1), u2)
    return [
        off(cap_a, n1, +1), miter_l, off(cap_c, n2, +1),
        off(cap_c, n2, -1), miter_r, off(cap_a, n1, -1),
    ]


def rune_mask():
    mask = Image.new("L", (W, W), 0)
    d = ImageDraw.Draw(mask)
    for a, b, c in CHEVRONS:
        pts = chevron_poly(
            (a[0] * S, a[1] * S), (b[0] * S, b[1] * S), (c[0] * S, c[1] * S), STROKE * S
        )
        d.polygon(pts, fill=255)
    return mask


def gradient():
    g = Image.new("RGB", (W, W))
    for y in range(W):
        t = y / (W - 1)
        row = tuple(round(GRAD_TOP[i] + (GRAD_BOT[i] - GRAD_TOP[i]) * t) for i in range(3))
        g.paste(row, (0, y, W, y + 1))
    return g


def tile(size, rounded=True):
    img = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    if rounded:
        d.rounded_rectangle([0, 0, W - 1, W - 1], radius=RADIUS * S, fill=BG)
    else:
        d.rectangle([0, 0, W - 1, W - 1], fill=BG)
    img.paste(gradient(), (0, 0), rune_mask())
    return img.resize((size, size), Image.LANCZOS)


if __name__ == "__main__":
    out = {
        ROOT / "brand" / "avatar-1024.png": tile(1024),           # GitHub org avatar
        ROOT / "website" / "icon-512.png": tile(512),             # og:image
        ROOT / "website" / "favicon-32.png": tile(32),
        ROOT / "website" / "apple-touch-icon.png": tile(180, rounded=False),  # iOS rounds it
    }
    for path, img in out.items():
        img.save(path)
        print(f"wrote {path.relative_to(ROOT)} ({img.size[0]}px)")
