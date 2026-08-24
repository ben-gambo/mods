#!/usr/bin/env python3
"""Regenerates moon.png and eclipse.png, the two cards' art.

Pure stdlib (zlib + struct) so it runs anywhere Python 3 does - no Pillow.
The canvas/outline/centring conventions are lifted from ImpatientGambit's
make_art.py, where their reasons are documented at length; the short version:

  - 24x28 portrait stays inside GambitApi's 10% aspect tolerance against the
    28x32 vanilla template, so the card sits in the rail at vanilla width.
  - Ink is auto-centred and the one-pixel dark outline is a pass over the
    finished silhouette, both because hand-tuning them per-edit never survives
    the second edit.

The moon is a waxing crescent with a few craters and cold little stars: serene,
expensive-looking, and mechanically empty - the card art should not apologise
for that. The eclipse is the vanilla sun's opposite number: a black disc that
is clearly COVERING something, gold corona spiking out from behind and the
diamond-ring glint at the top right where the last of the sun gets through.

    python3 tools/make_art.py            # writes ../moon.png, ../eclipse.png
"""

import math
import os
import struct
import zlib

W, H = 24, 28

CLEAR = (0, 0, 0, 0)
OUTLINE = (0x16, 0x11, 0x1C, 255)

# moon palette: cold silver-blues
MOON_LIT = (0xEC, 0xEF, 0xF8, 255)
MOON = (0xC9, 0xD2, 0xE6, 255)
MOON_SHADE = (0xA6, 0xB2, 0xCE, 255)
CRATER = (0x8C, 0x98, 0xBA, 255)
STAR = (0xF4, 0xF6, 0xFC, 255)
STAR_DIM = (0xB9, 0xC3, 0xDC, 255)

# eclipse palette: the game's money-gold corona around a void
CORONA_HOT = (0xFF, 0xE7, 0xC2, 255)
CORONA = (0xFF, 0xA8, 0x00, 255)
CORONA_DEEP = (0xD8, 0x7A, 0x00, 255)
VOID = (0x12, 0x0E, 0x1E, 255)
VOID_RIM = (0x3A, 0x2A, 0x55, 255)


def blank():
    return [[CLEAR] * W for _ in range(H)]


def put(px, x, y, colour):
    if 0 <= x < W and 0 <= y < H:
        px[y][x] = colour


def disc(px, cx, cy, r, colour):
    for y in range(cy - r, cy + r + 1):
        for x in range(cx - r, cx + r + 1):
            dx, dy = x - cx, y - cy
            if dx * dx + dy * dy <= r * r + r // 2:
                put(px, x, y, colour)


def in_disc(cx, cy, r, x, y):
    dx, dy = x - cx, y - cy
    return dx * dx + dy * dy <= r * r + r // 2


def build_moon():
    px = blank()
    cx, cy, r = 11, 14, 8
    # bite: a same-size disc pushed toward the upper right eats the lit face,
    # leaving a waxing crescent that opens up-and-right
    bx, by = cx + 5, cy - 4

    for y in range(cy - r, cy + r + 1):
        for x in range(cx - r, cx + r + 1):
            if not in_disc(cx, cy, r, x, y):
                continue
            if in_disc(bx, by, r - 1, x, y):
                continue  # the bite: sky, not moon
            # inner edge of the crescent catches the earthshine
            colour = MOON
            if in_disc(bx, by, r + 1, x, y):
                colour = MOON_SHADE
            elif x - cx <= -(r - 3) or y - cy >= r - 3:
                colour = MOON_LIT  # bright outer limb, lower-left
            put(px, x, y, colour)

    # craters, hugging the lit limb
    for crx, cry in ((7, 11), (6, 16), (10, 20), (12, 17)):
        put(px, crx, cry, CRATER)
        put(px, crx + 1, cry, CRATER)
        put(px, crx, cry + 1, CRATER)

    # cold little stars in the bite and around the horns
    for sx, sy, colour in ((17, 7, STAR), (14, 12, STAR_DIM), (19, 14, STAR_DIM)):
        put(px, sx, sy, colour)
    # one four-point star, upper right
    put(px, 20, 4, STAR)
    put(px, 19, 4, STAR_DIM)
    put(px, 21, 4, STAR_DIM)
    put(px, 20, 3, STAR_DIM)
    put(px, 20, 5, STAR_DIM)

    centre(px)
    add_outline(px)
    return px


def build_eclipse():
    px = blank()
    cx, cy, r = 11, 14, 7

    # corona spikes first, so the void disc overwrites their roots: eight rays,
    # long on the cardinals, short on the diagonals
    for i in range(8):
        ang = i * math.pi / 4.0
        length = r + 4 if i % 2 == 0 else r + 2
        for d in range(r, length + 1):
            x = cx + int(round(math.cos(ang) * d))
            y = cy + int(round(math.sin(ang) * d))
            put(px, x, y, CORONA if d < length else CORONA_DEEP)

    # the glow ring squeezed out around the rim
    disc(px, cx, cy, r + 1, CORONA)
    for y in range(cy - r - 1, cy + r + 2):
        for x in range(cx - r - 1, cx + r + 2):
            if px[y % H][x % W] is CORONA and in_disc(cx, cy, r + 1, x, y) \
                    and not in_disc(cx, cy, r, x, y) and (x + y) % 2 == 0:
                put(px, x, y, CORONA_HOT)

    # the moon itself: a void with a bruised purple rim
    disc(px, cx, cy, r, VOID_RIM)
    disc(px, cx, cy, r - 1, VOID)

    # diamond-ring glint, upper right, where the last sunlight escapes
    put(px, cx + r - 1, cy - r + 1, CORONA_HOT)
    put(px, cx + r, cy - r + 1, CORONA_HOT)
    put(px, cx + r - 1, cy - r, CORONA_HOT)
    put(px, cx + r, cy - r, STAR)

    centre(px)
    add_outline(px)
    return px


def centre(px):
    """Centres the ink in the canvas, leaving room for the outline pass."""
    xs = [x for y in range(H) for x in range(W) if px[y][x][3] > 0]
    ys = [y for y in range(H) for x in range(W) if px[y][x][3] > 0]
    if not xs or not ys:
        return

    dx = ((W - 1) - (max(xs) + min(xs))) // 2
    dy = ((H - 1) - (max(ys) + min(ys))) // 2
    dx = max(1 - min(xs), min(dx, W - 2 - max(xs)))
    dy = max(1 - min(ys), min(dy, H - 2 - max(ys)))
    if dx == 0 and dy == 0:
        return

    shifted = blank()
    for y in range(H):
        for x in range(W):
            if px[y][x][3] == 0:
                continue
            nx, ny = x + dx, y + dy
            if 0 <= nx < W and 0 <= ny < H:
                shifted[ny][nx] = px[y][x]
    for y in range(H):
        px[y][:] = shifted[y]


def add_outline(px):
    """One solid dark line around the whole silhouette, vanilla-style."""
    solid = [[px[y][x][3] > 0 for x in range(W)] for y in range(H)]
    for y in range(H):
        for x in range(W):
            if solid[y][x]:
                continue
            for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                nx, ny = x + dx, y + dy
                if 0 <= nx < W and 0 <= ny < H and solid[ny][nx]:
                    px[y][x] = OUTLINE
                    break


def write_png(path, px):
    raw = bytearray()
    for row in px:
        raw.append(0)  # filter type 0 (None)
        for r, g, b, a in row:
            raw += bytes((r, g, b, a))

    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", W, H, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(bytes(raw), 9))
    png += chunk(b"IEND", b"")

    with open(path, "wb") as fh:
        fh.write(png)


if __name__ == "__main__":
    root = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
    for name, build in (("moon.png", build_moon), ("eclipse.png", build_eclipse)):
        out = os.path.join(root, name)
        write_png(out, build())
        print("wrote", out)
