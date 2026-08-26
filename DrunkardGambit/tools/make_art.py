#!/usr/bin/env python3
"""Regenerates drunkard.png, the gambit's card art.

Pure stdlib (zlib + struct) so it runs anywhere Python 3 does - no Pillow.

The subject: a pawn several drinks in. The head lolls off the body's axis,
the body slants the other way, one cheek is flushed, and a tall green bottle
stands on the ground beside it with fizz bubbles climbing away - the random
stagger the card performs, caught between two steps.

Geometry notes, learned by reading the game's assets with UnityPy:

  - Vanilla gambit sprites are bottom-pivoted (pivot y=0) at PPU 32, and their
    ink fills the canvas edge-to-edge; canvases themselves vary per card
    (17x25, 21x27, 22x30, 28x32...). GambitApi rescales a modded sprite so its
    canvas height matches the template's (SPR_Addiction, 28x32) and copies the
    template's pivot as a canvas fraction - so what matters is that the canvas
    hugs the ink, and that its aspect stays within the API's 10% tolerance of
    28/32 = 0.875. This script therefore draws the drunkard, outlines it, then
    CROPS the canvas to the ink's bounding box; the drawing's proportions are
    chosen so the cropped aspect lands inside the tolerance.

    python3 tools/make_art.py            # writes ../drunkard.png
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
ROSY = (0xE0, 0x7A, 0x6E, 255)
GREEN = (0x3F, 0x8F, 0x4C, 255)
GREEN_DARK = (0x2C, 0x6B, 0x38, 255)
GREEN_LIT = (0x9E, 0xD9, 0xA6, 255)
CORK = (0xB0, 0x7B, 0x40, 255)
BUBBLE = (0xF7, 0xF3, 0xE8, 255)


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

    # --- the pawn, listing to starboard ------------------------------------
    fill(5, 20, 16, 22, IVORY)               # base
    fill(5, 23, 16, 23, IVORY_DARK)

    fill(10, 19, 13, 19, IVORY)              # body, slanting as it rises
    fill(11, 17, 14, 18, IVORY)
    fill(12, 15, 15, 16, IVORY)
    put(10, 17, IVORY_DARK)                  # shadowed trailing edge
    put(11, 15, IVORY_DARK)

    fill(10, 13, 17, 13, IVORY_DARK)         # collar, slid off-centre
    fill(9, 14, 16, 14, IVORY_DARK)

    disc(14.5, 9, 3, IVORY)                  # head, lolling right of the body
    put(13, 7, IVORY_DARK)                   # brow shade, up on the curve
    put(16, 10, ROSY)                        # the flush
    put(17, 10, ROSY)

    # --- the bottle, standing on the same ground ---------------------------
    fill(20, 12, 21, 12, CORK)
    fill(20, 13, 21, 16, GREEN)              # neck
    fill(19, 17, 22, 23, GREEN)              # body
    fill(19, 22, 22, 23, GREEN_DARK)
    put(20, 18, GREEN_LIT)                   # glint
    put(20, 19, GREEN_LIT)

    # --- fizz, climbing out of the bottle and away -------------------------
    put(21, 2, BUBBLE)
    put(19, 4, BUBBLE)
    put(22, 6, BUBBLE)
    put(20, 9, BUBBLE)

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


# GambitApi copies the template sprite's pivot onto the rebuilt mod sprite as
# a fraction of the canvas, and the current template (SPR_Addiction, first in
# the library) carries a hand-tuned pivot of x=0.45 - NOT 0.5. The engine puts
# that pivot at the rail slot's centre, so ink centred in its canvas hangs
# visibly right of where vanilla cards sit. The crop below places the ink's
# centre on the 0.45 line instead.
TEMPLATE_PIVOT_X = 0.45


def crop(px):
    """Trim the canvas to the ink, then pad: 2 transparent rows on top,
    asymmetric columns on the sides, bottom flush.

    Vanilla sprites live in a packed atlas whose padding gives the green
    highlight-outline shader room to draw outside the ink on every edge; a
    standalone texture has no such slack, and ink flush to the texture top
    visibly clips that outline in-game. The bottom stays flush because the
    bottom-pivoted sprite stands on the rail baseline - padding there would
    float the card. The side padding is asymmetric so the ink's centre lands
    on the template's x-pivot line (see TEMPLATE_PIVOT_X above)."""
    xs = [x for y in range(H) for x in range(W) if px[y][x][3] > 0]
    ys = [y for y in range(H) for x in range(W) if px[y][x][3] > 0]
    tight = [row[min(xs):max(xs) + 1] for row in px[min(ys):max(ys) + 1]]
    w = len(tight[0])

    left = 1
    ink_centre = left + (w - 1) / 2
    best_right, best_err = 1, float("inf")
    for right in range(1, 5):
        err = abs(ink_centre - TEMPLATE_PIVOT_X * (w + left + right))
        if err < best_err:
            best_right, best_err = right, err
    print(f"side padding: left {left}, right {best_right} "
          f"(ink centre {best_err:+.2f}px off the pivot line)")

    padded = [[CLEAR] * (w + left + best_right) for _ in range(2)]
    for row in tight:
        padded.append([CLEAR] * left + row + [CLEAR] * best_right)
    return padded


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
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "drunkard.png")
    write_png(os.path.normpath(out), build())
    print("wrote", os.path.normpath(out))
