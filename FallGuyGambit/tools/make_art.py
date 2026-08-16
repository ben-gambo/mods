#!/usr/bin/env python3
"""Regenerates fallguy.png, the gambit's card art.

Pure stdlib (zlib + struct) so it runs anywhere Python 3 does - no Pillow.

The subject is the rescue at the moment it works: an ivory pawn sitting in the
sag of a firefighter's rescue net strung between two wooden poles, a couple of
motion dashes above where it just dropped from. One dense object, not a scene.

Geometry notes, learned by reading the game's own assets with UnityPy:

  - Vanilla gambit sprites are bottom-pivoted (pivot y=0) at PPU 32, so on the
    gambit rail every card STANDS on a shared baseline and rises from it. Art
    whose visual mass floats high in the canvas (a first draft had the pawn at
    the top and the net at the bottom, air in between) reads as a levitating
    speck next to the dense vanilla objects.
  - GambitApi rescales a modded sprite so its canvas height matches the
    template's canvas (SPR_Addiction, 28x32, ink flush to every edge), and
    copies the template's pivot as a fraction of the canvas. So the closer the
    canvas geometry is to 28x32 with edge-to-edge ink, the closer the placement
    math is to the identity - this canvas IS 28x32 with edge-to-edge ink, and
    the card is registered at 0.9 scale to sit mid-pack among its neighbours
    (vanilla arts span roughly 25 to 32 rows of ink).

    python3 tools/make_art.py            # writes ../fallguy.png
"""

import os
import struct
import zlib

# Same canvas as the vanilla template sprite (SPR_Addiction).
W, H = 28, 32

CLEAR = (0, 0, 0, 0)
OUTLINE = (0x16, 0x11, 0x1C, 255)
IVORY = (0xF2, 0xEA, 0xD3, 255)
IVORY_DARK = (0xC8, 0xBC, 0x9A, 255)
WOOD_LIT = (0xC9, 0x92, 0x52, 255)
WOOD = (0x93, 0x5E, 0x2C, 255)
WOOD_DARK = (0x5C, 0x37, 0x19, 255)
NET = (0xD8, 0x3A, 0x3A, 255)
NET_DARK = (0xA8, 0x26, 0x26, 255)
NET_LIT = (0xE8, 0x6A, 0x5A, 255)
DASH = (0x9C, 0xA2, 0xB2, 255)

# Centre-line of the net's sag between the two pole tops (image coords, row 0
# is the TOP of the finished PNG). Parabola: rests at the pole tops, dips in
# the middle - where the pawn sits.
POLE_TOP_Y = 15
SAG_DEPTH = 7
NET_X0, NET_X1 = 4, 23


def net_y(x):
    t = (x - 13.5) / 9.5
    return POLE_TOP_Y + round(SAG_DEPTH * max(0.0, 1.0 - t * t))


def build():
    px = [[CLEAR] * W for _ in range(H)]

    def fill(x0, y0, x1, y1, colour):
        for y in range(y0, y1 + 1):
            for x in range(x0, x1 + 1):
                if 0 <= x < W and 0 <= y < H:
                    px[y][x] = colour

    def disc(cx, cy, r, colour):
        for y in range(cy - r, cy + r + 1):
            for x in range(cx - r, cx + r + 1):
                dx, dy = x - cx, y - cy
                if dx * dx + dy * dy <= r * r + r // 2:
                    if 0 <= x < W and 0 <= y < H:
                        px[y][x] = colour

    # --- motion dashes: it just dropped in from up there --------------------
    fill(6, 3, 6, 5, DASH)
    fill(13, 1, 13, 3, DASH)
    fill(14, 1, 14, 3, DASH)
    fill(21, 3, 21, 5, DASH)

    # --- the poles, planted wide so the net is the card's full width --------
    for x0 in (1, 24):
        fill(x0, POLE_TOP_Y - 1, x0 + 2, 29, WOOD)
        fill(x0, POLE_TOP_Y - 1, x0, 29, WOOD_LIT)
        fill(x0 + 2, POLE_TOP_Y - 1, x0 + 2, 29, WOOD_DARK)
        fill(x0 - 1, 30, x0 + 3, 30, WOOD_DARK)  # flared foot on the baseline

    # --- the net, slung between the pole tops with a deep sag ---------------
    for x in range(NET_X0, NET_X1 + 1):
        y = net_y(x)
        px[y][x] = NET_LIT if x % 2 == 0 else NET
        px[y + 1][x] = NET
        px[y + 2][x] = NET_DARK if (x + y) % 2 == 0 else NET
        # Loose mesh trailing under the band so it reads as netting.
        if x % 3 == 1:
            px[y + 3][x] = NET_DARK

    # --- the pawn, caught: base buried in the sag, head below the pole tops -
    disc(14, 10, 3, IVORY)                     # head
    px[9][12] = IVORY_DARK                     # cheek shade
    px[10][12] = IVORY_DARK
    fill(10, 14, 17, 15, IVORY_DARK)           # collar
    fill(11, 16, 16, 19, IVORY)                # body
    px[16][11] = IVORY_DARK
    px[17][11] = IVORY_DARK
    fill(9, 20, 18, 22, IVORY)                 # base, sunk into the net's dip
    fill(9, 22, 18, 22, IVORY_DARK)

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

    shifted = [[CLEAR] * W for _ in range(H)]
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
    """Vanilla's defining trait: one solid dark line around the whole shape."""
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
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "fallguy.png")
    write_png(os.path.normpath(out), build())
    print("wrote", os.path.normpath(out))
