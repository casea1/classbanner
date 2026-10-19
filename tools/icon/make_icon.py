"""Generates the ClassBanner logo: assets/logo.svg, assets/logo.png and src/ClassBanner/ClassBanner.ico.

The mark is a dark screen framed by a top and bottom banner, each striped in the six classification colors
(U, CUI, C, S, TS, TS//SCI). Run: python3 tools/icon/make_icon.py  (needs Pillow)
"""
import os
from PIL import Image, ImageDraw

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
COLORS = ["#007A33", "#502B85", "#0033A0", "#C8102E", "#FF8C00", "#FCE83A"]
TILE = "#161D27"
EDGE = "#3A4657"
WINDOW = "#E9EEF4"
TITLE = "#8C9AAD"

# Geometry on a 256 grid.
TILE_BOX, TILE_R = (8, 8, 248, 248), 52
BAND_X0, BAND_X1, BAND_H, BAND_R = 30, 226, 40, 12
TOP_Y, BOTTOM_Y = 34, 182
WIN_BOX, WIN_R, WIN_TITLE_H = (70, 96, 186, 160), 9, 14


def draw(size):
    s = 4  # supersample
    scale = size * s / 256
    img = Image.new("RGBA", (size * s, size * s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    sc = lambda box: tuple(round(v * scale) for v in box)

    d.rounded_rectangle(sc(TILE_BOX), radius=round(TILE_R * scale), fill=TILE, outline=EDGE, width=max(s, round(5 * scale)))

    for y in (TOP_Y, BOTTOM_Y):
        band = Image.new("RGBA", img.size, (0, 0, 0, 0))
        bd = ImageDraw.Draw(band)
        seg = (BAND_X1 - BAND_X0) / len(COLORS)
        for i, c in enumerate(COLORS):
            bd.rectangle(sc((BAND_X0 + i * seg, y, BAND_X0 + (i + 1) * seg + 0.5, y + BAND_H)), fill=c)
        mask = Image.new("L", img.size, 0)
        ImageDraw.Draw(mask).rounded_rectangle(sc((BAND_X0, y, BAND_X1, y + BAND_H)), radius=round(BAND_R * scale), fill=255)
        img.paste(band, (0, 0), mask)

    d.rounded_rectangle(sc(WIN_BOX), radius=round(WIN_R * scale), fill=WINDOW)
    x0, y0, x1, _ = WIN_BOX
    d.rounded_rectangle(sc((x0, y0, x1, y0 + WIN_TITLE_H)), radius=round(WIN_R * scale), fill=TITLE)
    d.rectangle(sc((x0, y0 + WIN_TITLE_H / 2, x1, y0 + WIN_TITLE_H)), fill=TITLE)

    return img.resize((size, size), Image.LANCZOS)


def svg():
    seg = (BAND_X1 - BAND_X0) / len(COLORS)
    parts = [
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256">',
        f'  <title>ClassBanner</title>',
        '  <defs>',
    ]
    for name, y in (("top", TOP_Y), ("bottom", BOTTOM_Y)):
        parts.append(f'    <clipPath id="{name}"><rect x="{BAND_X0}" y="{y}" width="{BAND_X1 - BAND_X0}" height="{BAND_H}" rx="{BAND_R}"/></clipPath>')
    parts.append('  </defs>')
    x0, y0, x1, y1 = TILE_BOX
    parts.append(f'  <rect x="{x0}" y="{y0}" width="{x1 - x0}" height="{y1 - y0}" rx="{TILE_R}" fill="{TILE}" stroke="{EDGE}" stroke-width="5"/>')
    for name, y in (("top", TOP_Y), ("bottom", BOTTOM_Y)):
        parts.append(f'  <g clip-path="url(#{name})">')
        for i, c in enumerate(COLORS):
            parts.append(f'    <rect x="{BAND_X0 + i * seg:.2f}" y="{y}" width="{seg + 0.5:.2f}" height="{BAND_H}" fill="{c}"/>')
        parts.append('  </g>')
    x0, y0, x1, y1 = WIN_BOX
    parts.append(f'  <rect x="{x0}" y="{y0}" width="{x1 - x0}" height="{y1 - y0}" rx="{WIN_R}" fill="{WINDOW}"/>')
    parts.append(f'  <path d="M{x0} {y0 + WIN_TITLE_H} V{y0 + WIN_R} a{WIN_R} {WIN_R} 0 0 1 {WIN_R} -{WIN_R} H{x1 - WIN_R} a{WIN_R} {WIN_R} 0 0 1 {WIN_R} {WIN_R} V{y0 + WIN_TITLE_H} Z" fill="{TITLE}"/>')
    parts.append('</svg>')
    return "\n".join(parts) + "\n"


if __name__ == "__main__":
    os.makedirs(os.path.join(ROOT, "assets"), exist_ok=True)
    with open(os.path.join(ROOT, "assets", "logo.svg"), "w") as f:
        f.write(svg())
    draw(512).save(os.path.join(ROOT, "assets", "logo.png"))
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    images = [draw(n) for n in sizes]
    images[-1].save(os.path.join(ROOT, "src", "ClassBanner", "ClassBanner.ico"),
                    sizes=[(n, n) for n in sizes], append_images=images[:-1])
    print("wrote assets/logo.svg, assets/logo.png, src/ClassBanner/ClassBanner.ico")
