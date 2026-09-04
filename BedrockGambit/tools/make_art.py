#!/usr/bin/env python3
"""Regenerates bedrock.png, the gambit's card art.

Pure stdlib (zlib + struct + zipfile) so it runs anywhere Python 3 does - no
Pillow. The one input it needs is Minecraft's own bedrock texture, which is
NOT in this repository: the script reads it straight out of the Minecraft
client jar in your launcher folder (or a path you pass in). Only the derived
card, bedrock.png, is committed.

The subject: the bedrock block as the Minecraft inventory draws it - a 2:1
isometric cube, top face lit, right face in shadow. Card size is the problem
to solve: a gambit card is 28x32 and a face here is 11 pixels wide, so the
16x16 texture cannot be shown as-is (nearest-sampling it gives a hexagon of
static). So the texture is first averaged down to 8x8 - the blotches survive,
the grain does not - and then projected onto the cube with heavy supersampling
(each output pixel averages a 12x12 grid of samples), which is how Minecraft's
own GUI renders keep a block readable at 16 pixels. The three faces are then
lit far apart (top 1.8x, left 1.2x, right 0.75x) because bedrock's average
value is dark and Minecraft's gentler 1.0/0.8/0.6 ratios leave the faces
indistinguishable at this size.

Geometry notes, the same ones every card in this repo follows: vanilla gambit
sprites are bottom-pivoted at PPU 32 with ink filling the canvas, and the
vanilla template GambitApi rescales against is 28x32. This card is drawn to
that exact canvas - 2 transparent rows on top for the highlight outline,
bottom flush with the baseline, side padding placing the ink's centre on the
template's x=0.45 pivot - so its texels land 1:1 on the game's pixel grid.

    python3 tools/make_art.py                   # newest jar in the launcher folder
    python3 tools/make_art.py path/to/1.21.5.jar
    python3 tools/make_art.py path/to/bedrock.png
"""

import glob
import os
import struct
import sys
import zipfile
import zlib

W, H = 28, 32          # the vanilla template canvas
HALF, SIDE = 11, 17    # half width of the top rhombus; height of the vertical faces
CX, TY = 14.0, 3.0     # front vertical edge; top vertex row (2 pad rows + 1 outline row)
TOP, LEFT, RIGHT = 1.8, 1.2, 0.75
SUPERSAMPLE = 12

CLEAR = (0, 0, 0, 0)
OUTLINE = (0x16, 0x11, 0x1C, 255)
TEMPLATE_PIVOT_X = 0.45

TEXTURE_PATHS = (
    "assets/minecraft/textures/block/bedrock.png",   # 1.13+
    "assets/minecraft/textures/blocks/bedrock.png",  # older
)


# --- PNG in/out -------------------------------------------------------------

def read_png(data):
    """Minimal PNG decoder: 8-bit gray/RGB/palette/gray+alpha/RGBA, filters 0-4."""
    assert data[:8] == b"\x89PNG\r\n\x1a\n", "not a PNG"
    pos, idat, plte = 8, b"", None
    while pos < len(data):
        (length,) = struct.unpack(">I", data[pos:pos + 4])
        tag = data[pos + 4:pos + 8]
        body = data[pos + 8:pos + 8 + length]
        if tag == b"IHDR":
            w, h, depth, ctype = struct.unpack(">IIBB", body[:10])
        elif tag == b"PLTE":
            plte = [tuple(body[i:i + 3]) for i in range(0, len(body), 3)]
        elif tag == b"IDAT":
            idat += body
        pos += 12 + length
    assert depth == 8, "8-bit PNGs only"
    channels = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[ctype]
    raw = zlib.decompress(idat)
    stride = w * channels
    rows, prev, p = [], bytearray(stride), 0
    for _ in range(h):
        filt = raw[p]
        line = bytearray(raw[p + 1:p + 1 + stride])
        p += 1 + stride
        for i in range(stride):
            a = line[i - channels] if i >= channels else 0
            b = prev[i]
            c = prev[i - channels] if i >= channels else 0
            if filt == 1:
                line[i] = (line[i] + a) & 255
            elif filt == 2:
                line[i] = (line[i] + b) & 255
            elif filt == 3:
                line[i] = (line[i] + (a + b) // 2) & 255
            elif filt == 4:
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                line[i] = (line[i] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        prev = line
        row = []
        for x in range(w):
            v = line[x * channels:(x + 1) * channels]
            if ctype == 0:
                row.append((v[0], v[0], v[0], 255))
            elif ctype == 2:
                row.append((v[0], v[1], v[2], 255))
            elif ctype == 3:
                row.append(plte[v[0]] + (255,))
            elif ctype == 4:
                row.append((v[0], v[0], v[0], v[1]))
            else:
                row.append(tuple(v))
        rows.append(row)
    return rows


def write_png(path, px):
    h, w = len(px), len(px[0])
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
    return w, h


# --- the texture --------------------------------------------------------------

def load_texture(arg=None):
    """The 16x16 bedrock texture, from a PNG, a client jar, or the newest jar
    in the Minecraft launcher folder."""
    if arg and arg.lower().endswith(".png"):
        return read_png(open(arg, "rb").read())
    if arg:
        jars = [arg]
    else:
        home = os.path.expanduser("~")
        jars = sorted(
            glob.glob(os.path.join(home, "Library", "Application Support", "minecraft", "versions", "*", "*.jar"))
            + glob.glob(os.path.join(home, ".minecraft", "versions", "*", "*.jar"))
            + glob.glob(os.path.join(home, "AppData", "Roaming", ".minecraft", "versions", "*", "*.jar"))
        )
    for jar in reversed(jars):
        try:
            with zipfile.ZipFile(jar) as z:
                for name in TEXTURE_PATHS:
                    if name in z.namelist():
                        print(f"texture: {name} from {jar}")
                        return read_png(z.read(name))
        except zipfile.BadZipFile:
            continue
    raise SystemExit("no bedrock texture found - pass a client jar or the PNG as the first argument")


def downsample(tex, k=2):
    """Average kxk texel blocks. Keeps bedrock's blotches, drops its grain."""
    n = len(tex) // k
    out = []
    for y in range(n):
        row = []
        for x in range(n):
            cells = [tex[y * k + j][x * k + i] for j in range(k) for i in range(k)]
            row.append(tuple(sum(c[i] for c in cells) // len(cells) for i in range(3)) + (255,))
        out.append(row)
    return out


# --- the cube ---------------------------------------------------------------------

def render(tex):
    """Supersampled 2:1 isometric cube. Every output pixel averages a
    SUPERSAMPLE x SUPERSAMPLE grid of samples, each inverse-mapped to a face
    and a texel; alpha is the fraction of samples that hit the cube."""
    n = len(tex)
    px = [[CLEAR] * W for _ in range(H)]
    ss = SUPERSAMPLE
    for y in range(H):
        for x in range(W):
            r = g = b = 0.0
            hits = 0
            for j in range(ss):
                for i in range(ss):
                    X = x + (i + 0.5) / ss - CX
                    Y = y + (j + 0.5) / ss - TY
                    ax = abs(X)
                    if ax <= HALF and ax / 2 <= Y <= HALF - ax / 2:
                        # top face: the rhombus, u/v along its two diagonal axes
                        u = (X * n / HALF + Y * 2 * n / HALF) / 2
                        v = (Y * 2 * n / HALF - X * n / HALF) / 2
                        f = TOP
                    elif -HALF <= X < 0 and HALF + X / 2 <= Y < HALF + X / 2 + SIDE:
                        u = (X + HALF) / HALF * n
                        v = (Y - (HALF + X / 2)) / SIDE * n
                        f = LEFT
                    elif 0 <= X <= HALF and HALF - X / 2 <= Y < HALF - X / 2 + SIDE:
                        u = X / HALF * n
                        v = (Y - (HALF - X / 2)) / SIDE * n
                        f = RIGHT
                    else:
                        continue
                    c = tex[max(0, min(n - 1, int(v)))][max(0, min(n - 1, int(u)))]
                    r += c[0] * f
                    g += c[1] * f
                    b += c[2] * f
                    hits += 1
            if hits * 2 >= ss * ss:  # a pixel is ink when at least half of it is cube
                px[y][x] = (min(255, int(r / hits)), min(255, int(g / hits)), min(255, int(b / hits)), 255)
    return px


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
    """Trim to the ink, then pad: 2 transparent rows on top (room for the green
    highlight outline, which a standalone texture otherwise clips), bottom
    flush (the bottom-pivoted card stands on the rail baseline), and side
    padding chosen so the ink's centre sits on the template's x-pivot line
    (GambitApi copies the template's 0.45 pivot onto the rebuilt sprite)."""
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
    print(f"side padding: left {left}, right {best_right} (ink centre {best_err:+.2f}px off the pivot line)")
    padded = [[CLEAR] * (w + left + best_right) for _ in range(2)]
    for row in tight:
        padded.append([CLEAR] * left + row + [CLEAR] * best_right)
    return padded


def build(tex):
    px = render(downsample(tex))
    add_outline(px)
    return crop(px)


if __name__ == "__main__":
    tex = load_texture(sys.argv[1] if len(sys.argv) > 1 else None)
    out = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "bedrock.png"))
    w, h = write_png(out, build(tex))
    aspect = w / h
    delta = abs(aspect - 28 / 32) / (28 / 32)
    print(f"canvas {w}x{h}, aspect {aspect:.3f} (template 0.875, delta {delta:.0%})")
    assert delta <= 0.10, "aspect drifted outside GambitApi's 10% tolerance - reshape the cube"
    print("wrote", out)
