"""UNIVERSUM icon: a letter U in deep space with a ring around it (drawn here, no fonts or images used).

python3 tools/icon/make_icon.py <output folder>  ->  universum.ico (16-256 px), universum-256.png, preview.png
"""
import math
import random
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

S = 1024  # drawing size; every size is drawn at 4x and reduced


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(len(a)))


def radial(size, center, stops):
    """Radial gradient: stops = [(t, (r, g, b)), ...] by distance / size."""
    img = Image.new("RGB", (size, size))
    px = img.load()
    cx, cy = center[0] * size, center[1] * size
    for y in range(size):
        for x in range(size):
            t = math.hypot(x - cx, y - cy) / size
            for (t0, c0), (t1, c1) in zip(stops, stops[1:]):
                if t <= t1:
                    px[x, y] = lerp(c0, c1, max(0.0, (t - t0) / (t1 - t0)))
                    break
            else:
                px[x, y] = stops[-1][1]
    return img


def vertical(size, top, bottom, height_range):
    img = Image.new("RGB", (size, size))
    d = ImageDraw.Draw(img)
    y0, y1 = height_range
    for y in range(size):
        t = min(1.0, max(0.0, (y - y0) / (y1 - y0)))
        d.line([(0, y), (size, y)], fill=lerp(top, bottom, t))
    return img


def u_mask(size, detail):
    """The letter U: two stems and a half ring, rounded at the top."""
    m = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(m)
    # small sizes: a larger, bolder letter without the ring
    w = size * (0.15 if detail else 0.2)                                     # stroke width
    left, right = (size * 0.30, size * 0.70) if detail else (size * 0.2, size * 0.8)
    top, bottom = (size * 0.25, size * 0.74) if detail else (size * 0.2, size * 0.82)
    r_out = (right - left) / 2
    cx = (left + right) / 2
    cy = bottom - r_out
    d.ellipse([cx - r_out, cy - r_out, cx + r_out, cy + r_out], fill=255)
    r_in = r_out - w
    d.ellipse([cx - r_in, cy - r_in, cx + r_in, cy + r_in], fill=0)
    d.rectangle([0, 0, size, cy], fill=0)
    d.rectangle([left, top, left + w, cy], fill=255)
    d.rectangle([right - w, top, right, cy], fill=255)
    d.ellipse([left, top - w / 2, left + w, top + w / 2], fill=255)
    d.ellipse([right - w, top - w / 2, right, top + w / 2], fill=255)
    return m


def ring_masks(size, detail):
    """An orbit ring around the U, tilted; its back half goes behind the letter, the front half over it."""
    ring = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(ring)
    cx, cy = size * 0.5, size * 0.53
    rx, ry = size * 0.43, size * 0.12
    width = size * (0.022 if detail else 0.045)
    d.ellipse([cx - rx, cy - ry, cx + rx, cy + ry], outline=255, width=int(width))
    ring = ring.rotate(-18, resample=Image.BICUBIC, center=(cx, cy))
    # front = lower half after the tilt (over the letter), back = upper half (behind it)
    half = Image.new("L", (size, size), 0)
    ImageDraw.Draw(half).rectangle([0, cy, size, size], fill=255)
    half = half.rotate(-18, resample=Image.BICUBIC, center=(cx, cy))
    front = Image.composite(ring, Image.new("L", (size, size), 0), half)
    back = Image.composite(Image.new("L", (size, size), 0), ring, half)
    return back, front


def draw(size, detail=True, seed=7):
    big = size * 4
    rnd = random.Random(seed)
    # space: violet glow upper left fading into deep navy
    space = radial(big // 8, (0.32, 0.28), [(0.0, (88, 60, 190)), (0.38, (28, 26, 92)), (0.8, (8, 10, 34)), (1.2, (4, 5, 16))])
    space = space.resize((big, big), Image.BICUBIC)
    if detail:
        # nebula clouds
        neb = Image.new("RGB", (big, big), (0, 0, 0))
        nd = ImageDraw.Draw(neb)
        for (x, y, r, c) in [(0.75, 0.25, 0.22, (190, 60, 200)), (0.2, 0.8, 0.25, (30, 150, 210)), (0.6, 0.85, 0.15, (120, 70, 220))]:
            nd.ellipse([(x - r) * big, (y - r) * big, (x + r) * big, (y + r) * big], fill=c)
        neb = neb.filter(ImageFilter.GaussianBlur(big * 0.09))
        space = Image.blend(space, Image.composite(neb, space, Image.new("L", (big, big), 255)), 0.35)
        # stars
        sd = ImageDraw.Draw(space)
        for _ in range(int(140 * (big / 4096) ** 0.5) + 60):
            x, y = rnd.random() * big, rnd.random() * big
            r = big * (0.0012 + rnd.random() * 0.0028)
            v = rnd.randint(150, 255)
            sd.ellipse([x - r, y - r, x + r, y + r], fill=(v, v, min(255, v + 20)))
        for (x, y, r) in [(0.18, 0.2, 0.05), (0.84, 0.66, 0.035), (0.8, 0.14, 0.025)]:
            cx, cy, rr = x * big, y * big, r * big
            sd.polygon([(cx, cy - rr), (cx + rr * 0.18, cy - rr * 0.18), (cx + rr, cy), (cx + rr * 0.18, cy + rr * 0.18),
                        (cx, cy + rr), (cx - rr * 0.18, cy + rr * 0.18), (cx - rr, cy), (cx - rr * 0.18, cy - rr * 0.18)], fill=(255, 255, 240))

    back, front = ring_masks(big, detail)
    ring_color = vertical(big, (255, 214, 130), (255, 140, 60), (big * 0.35, big * 0.7))
    if detail:
        space.paste(ring_color, (0, 0), back)

    letter = u_mask(big, detail)
    glow = letter.filter(ImageFilter.GaussianBlur(big * 0.035))
    space.paste(Image.new("RGB", (big, big), (120, 220, 255)), (0, 0), glow.point(lambda v: int(v * 0.85)))
    letter_color = vertical(big, (150, 235, 255), (190, 140, 255), (big * 0.25, big * 0.75))
    space.paste(letter_color, (0, 0), letter)
    if detail:
        space.paste(ring_color, (0, 0), front)

    # rounded square
    mask = Image.new("L", (big, big), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, big - 1, big - 1], radius=big * 0.22, fill=255)
    out = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    out.paste(space, (0, 0), mask)
    return out.resize((size, size), Image.LANCZOS)


def glyph(kind, size):
    """Small icons of the context menu items, in the colours of the U."""
    big = size * 8
    img = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    m = Image.new("L", (big, big), 0)
    d = ImageDraw.Draw(m)
    w = int(big * 0.11)

    def line(points):
        pts = [(x * big, y * big) for x, y in points]
        d.line(pts, fill=255, width=w, joint="curve")
        for x, y in (pts[0], pts[-1]):
            d.ellipse([x - w / 2, y - w / 2, x + w / 2, y + w / 2], fill=255)

    if kind == "mux":
        # two inputs merging into one output
        line([(0.12, 0.22), (0.42, 0.22), (0.62, 0.5)])
        line([(0.12, 0.78), (0.42, 0.78), (0.62, 0.5)])
        line([(0.62, 0.5), (0.86, 0.5)])
        d.polygon([(0.72 * big, 0.32 * big), (0.96 * big, 0.5 * big), (0.72 * big, 0.68 * big)], fill=255)
    elif kind == "batch":
        # three stacked cards
        for i, (x, y) in enumerate([(0.30, 0.10), (0.20, 0.25), (0.10, 0.40)]):
            box = [x * big, y * big, (x + 0.6) * big, (y + 0.48) * big]
            d.rounded_rectangle(box, radius=big * 0.06, fill=0 if i < 2 else 255, outline=255, width=w)
    elif kind == "single":
        # one document with lines
        d.polygon([(0.2 * big, 0.06 * big), (0.62 * big, 0.06 * big), (0.82 * big, 0.26 * big), (0.82 * big, 0.94 * big), (0.2 * big, 0.94 * big)],
                  outline=255, width=w)
        d.line([(0.62 * big, 0.06 * big), (0.62 * big, 0.26 * big), (0.82 * big, 0.26 * big)], fill=255, width=w)
        for y in (0.48, 0.66, 0.82):
            d.line([(0.34 * big, y * big), (0.68 * big, y * big)], fill=255, width=int(w * 0.8))
    elif kind == "info":
        d.ellipse([0.06 * big, 0.06 * big, 0.94 * big, 0.94 * big], outline=255, width=w)
        d.ellipse([0.43 * big, 0.22 * big, 0.57 * big, 0.36 * big], fill=255)
        d.rounded_rectangle([0.44 * big, 0.44 * big, 0.56 * big, 0.78 * big], radius=w * 0.4, fill=255)
    color = vertical(big, (95, 205, 255), (160, 110, 255), (big * 0.1, big * 0.9))
    img.paste(color, (0, 0), m)
    return img.resize((size, size), Image.LANCZOS)


def main(folder):
    out = Path(folder)
    out.mkdir(parents=True, exist_ok=True)
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    images = [draw(s, detail=s >= 48) for s in sizes]
    images[-1].save(out / "universum.ico", sizes=[(s, s) for s in sizes], append_images=images[:-1])
    images[-1].save(out / "universum-256.png")
    for kind in ("mux", "batch", "single", "info"):
        small = [glyph(kind, s) for s in (16, 20, 24, 32, 40, 48)]
        small[-1].save(out / f"menu-{kind}.ico", sizes=[(s, s) for s in (16, 20, 24, 32, 40, 48)], append_images=small[:-1])
        small[-1].save(out / f"menu-{kind}.png")
    preview = Image.new("RGBA", (256 + 128 + 64 + 48 + 32 + 16 + 80, 270), (240, 240, 240, 255))
    x = 10
    for img in [images[-1], images[7], images[6], images[5], images[3], images[0]]:
        preview.paste(img, (x, 10), img)
        x += img.width + 10
    dark = Image.new("RGBA", preview.size, (32, 32, 32, 255))
    both = Image.new("RGBA", (preview.width, preview.height * 2), (0, 0, 0, 255))
    both.paste(preview, (0, 0))
    x = 10
    for img in [images[-1], images[7], images[6], images[5], images[3], images[0]]:
        dark.paste(img, (x, 10), img)
        x += img.width + 10
    both.paste(dark, (0, preview.height))
    both.save(out / "preview.png")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else ".")
