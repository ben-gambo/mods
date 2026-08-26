# Drunkard

> **After CAPTURING, the piece staggers to a RANDOM SAFE empty tile.**

Every time one of your pieces captures, it doesn't stay to gloat: it staggers
on to a uniformly random intact empty tile, anywhere on the board - but
drunkard's luck holds, and it never stumbles onto a square an enemy threatens.
It might wander deep behind enemy lines or wobble two squares sideways; it
just won't fall into a capture doing it.

Common, 3 coins - the stagger is safe, but where it lands is nobody's plan,
and a piece flung across the board is as often a nuisance as a blessing.
Two sober exceptions: a capture that triggers a promotion is
left alone (drunk pawns sober up at the finish line), and if every intact
empty tile is threatened - or the board has none at all - the piece simply
stays put. The card only relocates, it never kills.

## Install

Unpack the zip from [Releases](../../releases?q=DrunkardGambit) into your
game's `Mods/` folder:

```
Gambonanza/Mods/DrunkardGambit/
├── mod.json
├── Gambonanza.DrunkardGambit.dll
└── drunkard.png
```

Needs the **GambitApi** mod, which ships with the framework. No framework
update is required - this is an ordinary mod DLL loaded by ModHost. Built and
played against framework **1.5.1** / game build **24858528**.

## How it works

A player capture in `SelectionManager` is one synchronous pointer-up frame:
the victim is marked dead, `OnCapture(capturer, victim, tile)` fires, and
*then* vanilla finishes the move - parents the capturer to the tile, starts a
0.1s landing tween, and runs the promotion checks. So the gambit never moves
anything from inside the event. It waits 0.3s: after the landing tween, and
comfortably before the enemy turn that `TurnManager` schedules 0.5s after the
move.

At that point it re-checks that the piece is still alive, still standing where
it captured (another gambit may have moved it first), and that no promotion
was triggered - `PromotionManager` remembers (piece, tile) from the capture
frame and later instantiates the promoted piece into that remembered tile, so
staggering a promoting pawn would strand the promotion. Then it picks a random
board tile that is intact (not fallen, not shaking, not mid-transformation),
empty, and unthreatened, re-parents the piece there the same way the game's
own moves do, and lets the game's DOFollow tween plus a punch-rotation wobble
sell the stumble.

"Unthreatened" is judged exactly the way the game's own scare-scan judges it
(`ThreatEffectBehaviour` / `TileBehaviour.IsThreatenedBy`): every living,
un-trapped enemy piece contributes its attack squares - a pawn's diagonal
`GetEatPlaces()` (its forward walk is not an attack), everyone else's
`GetTilesAvailable()` (their moves are their attacks).

One flag the tile filter deliberately avoids: `TileBehaviour.CanBeLandedOn`.
Despite the name it does not mean "a piece may stand here" - it is the static
stock-deployment marker, true only on your bottom row or two. Filtering on it
is why 1.0.0's stagger always walked the piece back to its home rows.

`StartingTile` is deliberately untouched: when the wave ends, the reset walks
the drunkard back to its own post like any other survivor.

## Building

```
./build.sh DrunkardGambit            # from the repo root
./build.sh DrunkardGambit --install  # also copy into your game's Mods/
```

Regenerate the card art with `python3 tools/make_art.py`.
