"""Generate all app icons (app.ico + MSIX Assets) from code.

Design: white open book (two facing pages) on a deep-green rounded square, with the
Green Yoga Inc mark badged into the bottom-right corner — distinctive, brand-neutral,
and deliberately unlike any existing PDF-reader mark. Nothing here may resemble another
vendor's product mark: the 1.0.11 artwork embedded a lookalike of Adobe Acrobat's "A"
and had to be pulled.

The badge is omitted below 32px, where it would collapse into a grey smudge and blur
the book underneath it.

Usage: python tools/make_icons.py   (from the repo root)
"""
from PIL import Image, ImageDraw
import math
import os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

BG_TOP = (34, 102, 66)      # deep green
BG_BOT = (22, 70, 45)       # darker green
PAGE = (255, 255, 255)
PAGE_SHADE = (214, 230, 220)
SPINE = (22, 70, 45)


MARK = os.path.join(ROOT, "tools", "assets", "greenyoga-mark.png")
BADGE_FRACTION = 0.30       # badge diameter, as a fraction of the icon
BADGE_MIN_SIZE = 40         # below this the badge is dropped entirely

# Tile geometry, as fractions of the icon — kept in sync with draw_icon().
PAD_FRACTION = 0.06
RADIUS_FRACTION = 0.20


def _badge(px: int) -> Image.Image:
    """The Green Yoga mark on a white disc — the mark is green, the tile is green."""
    s = 4
    W = px * s
    badge = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    d = ImageDraw.Draw(badge)
    d.ellipse([0, 0, W - 1, W - 1], fill=(255, 255, 255, 255))

    mark = Image.open(MARK).convert("RGBA")
    bbox = mark.split()[3].getbbox()
    if bbox:
        mark = mark.crop(bbox)
    inner = int(W * 0.74)                       # padding so the disc reads as a plate
    scale = inner / max(mark.size)
    mark = mark.resize((max(1, int(mark.width * scale)),
                        max(1, int(mark.height * scale))), Image.LANCZOS)
    badge.paste(mark, ((W - mark.width) // 2, (W - mark.height) // 2), mark)
    return badge.resize((px, px), Image.LANCZOS)


def draw_icon(size: int) -> Image.Image:
    s = 8  # supersample for crisp small sizes
    W = size * s
    img = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    pad = int(W * 0.06)
    radius = int(W * 0.20)

    # vertical gradient background
    grad = Image.new("RGBA", (1, W - 2 * pad), (0, 0, 0, 0))
    for y in range(W - 2 * pad):
        t = y / max(1, W - 2 * pad - 1)
        c = tuple(int(BG_TOP[i] + (BG_BOT[i] - BG_TOP[i]) * t) for i in range(3)) + (255,)
        grad.putpixel((0, y), c)
    grad = grad.resize((W - 2 * pad, W - 2 * pad))
    mask = Image.new("L", (W - 2 * pad, W - 2 * pad), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [0, 0, W - 2 * pad - 1, W - 2 * pad - 1], radius=radius, fill=255)
    img.paste(grad, (pad, pad), mask)

    # open book: two facing pages meeting at a center spine
    cx = W // 2
    top = int(W * 0.30)
    bot = int(W * 0.72)
    half = int(W * 0.26)
    lift = int(W * 0.045)   # outer edges lift up slightly

    left = [(cx, top), (cx - half, top - lift), (cx - half, bot - lift), (cx, bot)]
    right = [(cx, top), (cx + half, top - lift), (cx + half, bot - lift), (cx, bot)]
    d.polygon(left, fill=PAGE)
    d.polygon(right, fill=PAGE)

    # inner shading near the spine for depth
    shade_w = int(W * 0.045)
    d.polygon([(cx, top), (cx - shade_w, top - lift // 3),
               (cx - shade_w, bot - lift // 3), (cx, bot)], fill=PAGE_SHADE)
    d.polygon([(cx, top), (cx + shade_w, top - lift // 3),
               (cx + shade_w, bot - lift // 3), (cx, bot)], fill=PAGE_SHADE)

    # spine line
    d.line([(cx, top), (cx, bot)], fill=SPINE, width=max(s, int(W * 0.012)))

    # text lines on each page (skip at tiny sizes — they'd turn to noise)
    if size >= 32:
        lh = max(s, int(W * 0.022))
        for i in range(3):
            y = top + int(W * 0.10) + i * int(W * 0.09)
            yo = y - lift // 2
            d.rectangle([cx - half + int(W * 0.07), yo,
                         cx - shade_w - int(W * 0.05), yo + lh], fill=(120, 165, 138))
            d.rectangle([cx + shade_w + int(W * 0.05), yo,
                         cx + half - int(W * 0.07), yo + lh], fill=(120, 165, 138))

    icon = img.resize((size, size), Image.LANCZOS)

    if size >= BADGE_MIN_SIZE:
        px = max(1, int(size * BADGE_FRACTION))
        badge = _badge(px)
        # Tuck the badge inside the tile's rounded bottom-right corner rather than letting
        # it hang off the edge: the disc is white, so anything protruding past the green
        # tile vanishes against a light background. The corner arc is centred at
        # (1 - PAD - RADIUS) on both axes, so a disc of radius r stays within the tile
        # when its centre is at most (RADIUS - r) from that centre.
        r = BADGE_FRACTION / 2
        arc = 1.0 - PAD_FRACTION - RADIUS_FRACTION
        centre = arc + (RADIUS_FRACTION - r) / math.sqrt(2)
        left = int((centre - r) * size)
        icon.paste(badge, (left, left), badge)

    return icon


def main():
    assets = os.path.join(ROOT, "packaging", "Assets")
    os.makedirs(assets, exist_ok=True)

    for name, size in {
        "Square44x44Logo.png": 44,
        "Square44x44Logo.targetsize-24_altform-unplated.png": 24,
        "Square150x150Logo.png": 150,
        "Square310x310Logo.png": 310,
        "StoreLogo.png": 50,
    }.items():
        draw_icon(size).save(os.path.join(assets, name))

    wide = Image.new("RGBA", (310, 150), (0, 0, 0, 0))
    ic = draw_icon(150)
    wide.paste(ic, (80, 0), ic)
    wide.save(os.path.join(assets, "Wide310x150Logo.png"))

    sizes = [16, 24, 32, 48, 64, 128, 256]
    frames = [draw_icon(sz) for sz in sizes]
    frames[-1].save(os.path.join(ROOT, "src", "PdfLiteViewer", "app.ico"),
                    format="ICO", sizes=[(sz, sz) for sz in sizes],
                    append_images=frames[:-1])
    print("icons generated")


if __name__ == "__main__":
    main()
