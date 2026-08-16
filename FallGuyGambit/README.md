# Fall Guy

> **Pieces about to FALL are saved to the nearest free square, or the stash.**

When the crumble takes a tile out from under one of your pieces, the piece
does not die: it hops to the nearest intact empty square. If the whole board
is out of squares it is dropped into your stash instead, ready to be placed
again. Only when the stash is full too does the fall play out as vanilla
wrote it.

Epic, 8 coins. Every fall is covered - there is no per-game limit.

## Install

Unpack the zip from [Releases](../../releases?q=FallGuyGambit) into your
game's `Mods/` folder:

```
Gambonanza/Mods/FallGuyGambit/
├── mod.json
├── Gambonanza.FallGuyGambit.dll
└── fallguy.png
```

Needs the **GambitApi** mod, which ships with the framework. No framework
update is required - this is an ordinary mod DLL loaded by ModHost. Built and
played against framework **1.3.3** / game build **24648699**.

## How it works

A crumble death is a race the mod is allowed to win. When a shaking tile
falls, `CrumbleManager` books the piece into the buy-back graveyard, drops the
tile, and fires `OnFall` - all in the same frame, once per falling tile, so
every endangered piece gets its own rescue. The piece itself is only
destroyed later: `TileVisual.CO_Fall` waits 0.3s and then looks for a victim
with `GetComponentInChildren<BasePieceBehaviour>()`, because pieces are
children of their tile.

So the rescue is one move: on `OnFall`, re-parent the piece to safety before
that delayed lookup runs. The falling tile ends up with no piece-child, the
destroy path finds nothing, and the piece has simply moved. The graveyard
entry written a moment earlier is popped again, so a save is not also a free
buy-back token.

The board branch picks the nearest (world distance) intact, empty,
non-shaking square - everything still shaking when `OnFall` fires belongs to
this same crumble batch or the next one, so it is nowhere to leave a piece we
just saved. The piece's `StartingTile` is left alone: fallen tiles re-appear
at wave end and the vanilla reset walks the piece back to its own post.

The stash branch is the delicate one, because vanilla never moves a piece
board-to-stock mid-game:

- **Registration.** `PieceManager`'s white-pieces list is what every
  lose-check reads, and stock pieces normally are not in it. So the stashed
  piece is unregistered - unless it was the *last* piece on the board, where
  an empty list would make both lose-checks call the run dead while the
  player is holding a perfectly good piece. In that case it stays registered
  (`TurnManager` skips `InStock` entries anyway), and the duplicate
  registration vanilla adds when the piece is placed back is removed again.
- **The turn lock.** With nothing on the board, `TurnManager`'s scan never
  re-opens input. A short coroutine waits out that scan and, if the player's
  only pieces are stashed, sets `CanPlay` itself so the piece can be
  re-placed.

If neither a square nor a stash slot exists, the mod does nothing at all -
vanilla's fall pipeline is already mid-swing, and doing nothing *is* the
death.

## Files

| Path | |
| --- | --- |
| `src/FallGuyGambitMod.cs` | Registers the card - name, art, rarity, price. |
| `src/GambitFallGuy.cs` | The gameplay: the rescue, in board/stash/lol order. |
| `tools/make_art.py` | Regenerates `fallguy.png`. Pure stdlib Python 3. |
| `release/` | The built artefact, committed - see the root README for why. |

## A note on the art

A guardian-angel pawn: gold halo, stubby white wings, and pointedly nothing
underneath it - the tile it stood on is already gone. The geometry comes from
reading the game's own assets: vanilla gambit sprites are bottom-pivoted
(pivot y=0, PPU 32), so cards STAND on a shared baseline in the gambit rail,
and vanilla ink fills its canvas edge-to-edge; the canvases themselves vary
per card (17x25 up to the 28x32 template). Art whose mass floats in a sparse
canvas reads as a levitating speck next to those dense objects, which is
exactly what the first draft of this card did.

So `make_art.py` draws the angel slim (the rail's vanilla cards run 17-22px
wide, not the 28px template), outlines the silhouette in a pass, crops the
canvas to the ink, then pads it: two transparent rows on top, asymmetric
columns on the sides, bottom flush. Vanilla sprites sit in a packed atlas
whose padding gives the green highlight-outline room on every edge; a
standalone texture has none, and ink flush to the texture top visibly clips
that outline in-game. The bottom stays flush because the bottom-pivoted
sprite stands on the rail baseline. The side padding is asymmetric because
GambitApi copies the template's pivot onto the rebuilt sprite, and the
template's pivot is x=0.45 - centred ink therefore hangs visibly right of
the slot, so the crop puts the ink's centre on the 0.45 line instead.
Registered with `WithVisualScale(0.9f)`, the visible ink lands at
Warlock-class world size (~1.0 x 1.25 units).
