#!/usr/bin/env python3
"""Regenerates impatient.png, the gambit's card art.

Pure stdlib (zlib + struct) so it runs anywhere Python 3 does - no Pillow.

The subject is a chess clock on a stone plinth: left dial still ticking, right
button already slammed flat with the "time's up" flag popped. That is the card
in one picture - it will not sit through the rest of the stage. (Deliberately
not an hourglass; vanilla's Hourglass gambit already owns that silhouette.)

Two things about sizing, learned the hard way:

  - GambitApi scales a modded sprite so its *canvas* height matches the vanilla
    template's, and copies the template's pivot as a fraction of the canvas.
    So the canvas aspect ratio alone decides how wide the card lands on the
    board, and where the ink sits inside the canvas decides where it hangs. A
    square canvas full of ink renders a card twice as wide as the vanilla ones
    and pushes it out of the gambit rail. This canvas is portrait and the ink
    is auto-centred in it, so the card hangs where a vanilla one does.
  - A chess clock is a wide object, so it only fits a portrait frame with help.
    The plinth is that help, and it is also the vanilla idiom - Axe, Banner,
    Bribe, Catapult and Cauldron all stand on one.

    python3 tools/make_art.py            # writes ../impatient.png
"""

import os
import struct
import zlib

# 24x28 keeps the card narrow, and stays inside GambitApi's 10% aspect-ratio
# tolerance against the 28x32 vanilla template so it loads without a warning.
W, H = 24, 28

CLEAR = (0, 0, 0, 0)
OUTLINE = (0x16, 0x11, 0x1C, 255)
WOOD_LIT = (0xC9, 0x92, 0x52, 255)
WOOD = (0x93, 0x5E, 0x2C, 255)
WOOD_DARK = (0x5C, 0x37, 0x19, 255)
METAL_LIT = (0xEC, 0xEE, 0xF4, 255)
METAL = (0x9C, 0xA2, 0xB2, 255)
STONE_LIT = (0x9A, 0xA0, 0xAD, 255)
STONE = (0x6E, 0x74, 0x84, 255)
STONE_DARK = (0x4A, 0x4F, 0x5C, 255)
FACE = (0xF7, 0xF0, 0xD6, 255)
HAND = (0x24, 0x1E, 0x2A, 255)
FLAG = (0xD8, 0x3A, 0x3A, 255)

BODY_X0, BODY_X1 = 1, 20
BODY_Y0, BODY_Y1 = 8, 19
DIALS = ((6, 14), (15, 14))
DIAL_R = 3


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
                    px[y][x] = colour

    # --- the wooden case ----------------------------------------------------
    fill(BODY_X0, BODY_Y0, BODY_X1, BODY_Y1, WOOD)
    fill(BODY_X0, BODY_Y0, BODY_X1, BODY_Y0, WOOD_LIT)  # lit top plate
    fill(BODY_X0, BODY_Y0 + 1, BODY_X1, BODY_Y0 + 1, WOOD_DARK)  # seam under it
    fill(BODY_X0, BODY_Y1, BODY_X1, BODY_Y1, WOOD_DARK)  # shaded skirt

    # --- buttons: left still up on its plunger, right hammered flat ---------
    fill(5, 3, 7, BODY_Y0 - 1, METAL)  # stem
    fill(4, 2, 8, 3, METAL)  # cap, overhanging the stem
    fill(4, 2, 5, 3, METAL_LIT)
    fill(5, 4, 5, BODY_Y0 - 1, METAL_LIT)
    fill(13, 6, 16, BODY_Y0 - 1, METAL)  # the one that was just slammed
    fill(13, 6, 14, 6, METAL_LIT)

    # --- the popped "time's up" flag, on a pole beside the slammed button ---
    fill(18, 3, 18, BODY_Y0 - 1, OUTLINE)
    fill(19, 3, 20, 4, FLAG)

    # --- dials --------------------------------------------------------------
    for cx, cy in DIALS:
        disc(cx, cy, DIAL_R, OUTLINE)  # dark bezel...
        disc(cx, cy, DIAL_R - 1, FACE)  # ...with the face inside it

    # Left dial ticks along normally; the right one has its hand pinned at
    # twelve - that side's time is what just ran out.
    lx, ly = DIALS[0]
    px[ly - 1][lx] = HAND
    px[ly - 2][lx] = HAND
    px[ly][lx + 1] = HAND

    rx, ry = DIALS[1]
    for d in range(0, DIAL_R):
        px[ry - d][rx] = HAND

    # --- stone plinth, the vanilla way of standing an object up -------------
    fill(4, BODY_Y1 + 1, 17, BODY_Y1 + 3, STONE)
    fill(4, BODY_Y1 + 1, 17, BODY_Y1 + 1, STONE_LIT)
    fill(2, BODY_Y1 + 4, 19, BODY_Y1 + 5, STONE)
    fill(2, BODY_Y1 + 4, 19, BODY_Y1 + 4, STONE_LIT)
    fill(2, BODY_Y1 + 5, 19, BODY_Y1 + 5, STONE_DARK)

    centre(px)
    add_outline(px)
    return px


def centre(px):
    """Centres the ink in the canvas, leaving room for the outline pass.

    The card's hanging position on the gambit rail comes from the template's
    pivot applied as a fraction of our canvas, so ink that is off-centre in the
    canvas hangs off-centre in the slot. Measuring the drawn bounds and shifting
    beats hand-tuning every coordinate whenever the art changes.
    """
    xs = [x for y in range(H) for x in range(W) if px[y][x][3] > 0]
    ys = [y for y in range(H) for x in range(W) if px[y][x][3] > 0]
    if not xs or not ys:
        return

    dx = ((W - 1) - (max(xs) + min(xs))) // 2
    dy = ((H - 1) - (max(ys) + min(ys))) // 2
    # Keep one clear pixel all round so add_outline has somewhere to draw.
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
    """Vanilla's defining trait: one solid dark line around the whole shape.

    Running it as a pass over the finished silhouette - rather than drawing the
    edges by hand - means no corner or notch can be left un-outlined.
    """
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
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "impatient.png")
    write_png(os.path.normpath(out), build())
    print("wrote", os.path.normpath(out))
