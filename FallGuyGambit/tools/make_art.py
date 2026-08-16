#!/usr/bin/env python3
"""Regenerates fallguy.png, the gambit's card art.

Pure stdlib (zlib + struct) so it runs anywhere Python 3 does - no Pillow.

The subject is the rescue itself: an ivory pawn caught mid-drop above a
firefighter's rescue net stretched between two wooden poles, with a couple of
motion dashes above it so the drop reads at 24px. The net is the card in one
picture - the piece was going down, and something was waiting for it.

Sizing notes inherited from ImpatientGambit's art, learned the hard way:

  - GambitApi scales a modded sprite so its *canvas* height matches the vanilla
    template's, and copies the template's pivot as a fraction of the canvas.
    So the canvas aspect ratio alone decides how wide the card lands on the
    board. 24x28 stays inside GambitApi's 10% aspect-ratio tolerance against
    the 28x32 vanilla template, and portrait keeps the card on the rail.
  - The ink is auto-centred in the canvas and outlined as a pass over the
    finished silhouette, both stolen verbatim from the Impatient art script.

    python3 tools/make_art.py            # writes ../fallguy.png
"""

import os
import struct
import zlib

W, H = 24, 28

CLEAR = (0, 0, 0, 0)
OUTLINE = (0x16, 0x11, 0x1C, 255)
IVORY = (0xF2, 0xEA, 0xD3, 255)
IVORY_DARK = (0xC8, 0xBC, 0x9A, 255)
WOOD_LIT = (0xC9, 0x92, 0x52, 255)
WOOD = (0x93, 0x5E, 0x2C, 255)
WOOD_DARK = (0x5C, 0x37, 0x19, 255)
NET = (0xD8, 0x3A, 0x3A, 255)
NET_LIT = (0xE8, 0x6A, 0x5A, 255)
DASH = (0x9C, 0xA2, 0xB2, 255)


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

    # Row 0 is the TOP of the finished PNG, so the scene reads top-down:
    # motion dashes, then the pawn, then the net waiting under it.

    # --- motion dashes above: it is falling, not floating -------------------
    fill(7, 1, 7, 2, DASH)
    fill(12, 1, 12, 2, DASH)
    fill(17, 1, 17, 2, DASH)

    # --- the pawn, mid-drop --------------------------------------------------
    disc(12, 7, 3, IVORY)                     # head
    px[6][10] = IVORY_DARK                    # cheek shade on the head
    px[7][10] = IVORY_DARK
    fill(9, 11, 15, 12, IVORY_DARK)           # collar
    fill(10, 13, 14, 16, IVORY)               # body
    fill(9, 17, 15, 18, IVORY_DARK)           # base ring

    # --- the rescue net ------------------------------------------------------
    # Two wooden poles holding it up.
    fill(2, 20, 3, 26, WOOD)
    fill(2, 20, 2, 26, WOOD_LIT)
    fill(3, 25, 3, 26, WOOD_DARK)
    fill(20, 20, 21, 26, WOOD)
    fill(21, 20, 21, 26, WOOD_LIT)
    fill(20, 25, 20, 26, WOOD_DARK)

    # The canvas of the net, sagging a pixel where the pawn will land.
    for x in range(4, 20):
        sag = 1 if 8 <= x <= 15 else 0
        px[21 + sag][x] = NET_LIT if x % 2 == 0 else NET
        px[22 + sag][x] = NET
    # Cross-hatch below so it reads as mesh rather than a ribbon.
    for x in range(5, 19, 3):
        sag = 1 if 8 <= x <= 15 else 0
        px[24 + sag][x] = NET

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
