#!/usr/bin/env python3
"""Regenerates fallguy.png, the gambit's card art.

Pure stdlib (zlib + struct) so it runs anywhere Python 3 does - no Pillow.

The subject: a pawn that should be dead, and visibly isn't. Gold halo, stubby
white wings flared from the collar, hovering serenely - the guardian-angel
save the card performs, with nothing underneath it because the tile it stood
on is already gone.

Geometry notes, learned by reading the game's assets with UnityPy:

  - Vanilla gambit sprites are bottom-pivoted (pivot y=0) at PPU 32, and their
    ink fills the canvas edge-to-edge; canvases themselves vary per card
    (17x25, 21x27, 22x30, 28x32...). GambitApi rescales a modded sprite so its
    canvas height matches the template's (SPR_Addiction, 28x32) and copies the
    template's pivot as a canvas fraction - so what matters is that the canvas
    hugs the ink, and that its aspect stays within the API's 10% tolerance of
    28/32 = 0.875. This script therefore draws the angel, outlines it, then
    CROPS the canvas to the ink's bounding box; the drawing's proportions are
    chosen so the cropped aspect lands inside the tolerance.

    python3 tools/make_art.py            # writes ../fallguy.png
"""

import os
import struct
import zlib

# Working canvas; the emitted PNG is cropped to the ink.
W, H = 28, 32

CLEAR = (0, 0, 0, 0)
OUTLINE = (0x16, 0x11, 0x1C, 255)
IVORY = (0xF2, 0xEA, 0xD3, 255)
IVORY_DARK = (0xC8, 0xBC, 0x9A, 255)
GOLD = (0xE8, 0xB8, 0x3A, 255)
GOLD_LIT = (0xF4, 0xD4, 0x8A, 255)
WHITE = (0xF7, 0xF3, 0xE8, 255)
DASH = (0x9C, 0xA2, 0xB2, 255)


def build():
    px = [[CLEAR] * W for _ in range(H)]

    def fill(x0, y0, x1, y1, colour):
        for y in range(y0, y1 + 1):
            for x in range(x0, x1 + 1):
                if 0 <= x < W and 0 <= y < H:
                    px[y][x] = colour

    def put(x, y, colour):
        if 0 <= x < W and 0 <= y < H:
            px[y][x] = colour

    def disc(cx, cy, r, colour):
        for y in range(int(cy - r), int(cy + r) + 2):
            for x in range(int(cx - r), int(cx + r) + 2):
                if (x - cx) ** 2 + (y - cy) ** 2 <= r * r + r / 2:
                    put(x, y, colour)

    # --- the halo, floating on its own above the head -----------------------
    fill(11, 1, 16, 1, GOLD)
    put(10, 2, GOLD)
    put(17, 2, GOLD)
    fill(12, 2, 15, 2, GOLD_LIT)
    fill(11, 3, 16, 3, GOLD)

    # --- the pawn -----------------------------------------------------------
    disc(13.5, 8, 3, IVORY)                  # head (a gap below the halo)
    put(11, 7, IVORY_DARK)                   # cheek shade
    put(11, 8, IVORY_DARK)
    fill(10, 12, 17, 13, IVORY_DARK)         # collar
    fill(12, 14, 15, 20, IVORY)              # body, narrower than the head
    put(12, 14, IVORY_DARK)
    put(12, 15, IVORY_DARK)
    put(12, 16, IVORY_DARK)
    fill(9, 21, 18, 24, IVORY)               # base
    fill(9, 24, 18, 24, IVORY_DARK)

    # --- the wings: chunky slabs flared up-and-out, one clear pixel away ----
    # from the body so the outline pass draws a dark separator between them.
    for i in range(4):
        fill(7 - i, 11 + i, 8 - i, 12 + i, WHITE)
        fill(19 + i, 11 + i, 20 + i, 12 + i, WHITE)
    fill(3, 15, 8, 17, WHITE)
    fill(19, 15, 24, 17, WHITE)
    fill(4, 16, 8, 16, (0xE4, 0xDD, 0xC8, 255))   # feather seam
    fill(19, 16, 23, 16, (0xE4, 0xDD, 0xC8, 255))
    fill(4, 18, 7, 18, DASH)                 # feathered underside
    fill(20, 18, 23, 18, DASH)

    centre(px)
    add_outline(px)
    return crop(px)


def centre(px):
    """Centres the ink in the working canvas before outlining."""
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


def crop(px):
    """Trim the canvas to the ink so the sprite is bottom-anchored correctly
    (bottom-pivoted like vanilla, and no dead margin to float on)."""
    xs = [x for y in range(H) for x in range(W) if px[y][x][3] > 0]
    ys = [y for y in range(H) for x in range(W) if px[y][x][3] > 0]
    return [row[min(xs):max(xs) + 1] for row in px[min(ys):max(ys) + 1]]


def write_png(path, px):
    h = len(px)
    w = len(px[0])
    aspect = w / h
    template = 28 / 32
    delta = abs(aspect - template) / template
    print(f"canvas {w}x{h}, aspect {aspect:.3f} (template {template:.3f}, delta {delta:.0%})")
    assert delta <= 0.10, "aspect drifted outside GambitApi's 10% tolerance - reshape the drawing"

    raw = bytearray()
    for row in px:
        raw.append(0)  # filter type 0 (None)
        for r, g, b, a in row:
            raw += bytes((r, g, b, a))

    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(bytes(raw), 9))
    png += chunk(b"IEND", b"")

    with open(path, "wb") as fh:
        fh.write(png)


if __name__ == "__main__":
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "fallguy.png")
    write_png(os.path.normpath(out), build())
    print("wrote", os.path.normpath(out))
