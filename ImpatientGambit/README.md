# Impatient Gambit

> **Only fight BOSSES from now on.**
> **All gold earned is x4.**

Buy it and the next game is your current stage's boss. Every stage after that
goes straight to its own boss too, so a run collapses from 25 games to 5. The
x4 gold is what keeps that survivable - five payouts instead of twenty-five,
and the win screen gets a fourth breakdown row showing the bonus.

Legendary, 10 coins. The run's opening game is never skipped - it is never a
boss fight.

## Install

Unpack the zip from [Releases](../../releases?q=ImpatientGambit) into your
game's `Mods/` folder:

```
Gambonanza/Mods/ImpatientGambit/
├── mod.json
├── Gambonanza.ImpatientGambit.dll
└── impatient.png
```

Needs the **GambitApi** mod, which ships with the framework. No framework
update is required - this is an ordinary mod DLL loaded by ModHost. Built and
played against framework **1.3.3** / game build **24648699**.

## How it works

A run is five stages of five games, and `ChessDataManager.CurrentWave` is the
flat index into them. Vanilla calls a game a boss fight when
`(CurrentWave + 1) % 5 == 0`. So "only fight bosses" is one move: whenever the
player is between games, snap `CurrentWave` forward to the last wave of the
stage they are standing in. Which boss appears, which wave the pieces come
from, the reward tier and the save file are all derived from that one number.

Two things do not follow automatically:

- **Board width.** Vanilla widens the board only when `CurrentWave` lands
  exactly on a multiple of five - a wave this mod always skips over - so the
  board is caught up by hand at board-placement time.
- **Income.** There is no multiplier hook, so the mod watches
  `OnCoinIncreased`, measures what was just granted and tops it up. The win
  payout is handled separately by the win-screen row, because vanilla only
  redraws the coin counter as `MoneyAnimationManager`'s coins land - money
  added outside that path is real but invisible until something else repaints
  the counter, which reads as "the gold only shows up after I buy something".

The skip is taken on entering the shop, never on the win screen itself:
`WinCanvas` reads `CurrentWave` on a delay to work out the payout tier, so
moving the wave there would quietly pay the wrong reward for the boss just
beaten.

## Files

| Path | |
| --- | --- |
| `src/ImpatientGambitMod.cs` | Registers the card - name, art, rarity, price. |
| `src/GambitImpatient.cs` | The gameplay: wave skipping, board width, the x4. |
| `src/ImpatientWinRow.cs` | The extra "Impatient x4" row on the win screen. |
| `tools/make_art.py` | Regenerates `impatient.png`. Pure stdlib Python 3. |
| `release/` | The built artefact, committed - see the root README for why. |

## A note on the art

GambitApi scales a modded sprite so its *canvas* height matches the vanilla
template's, and copies the template's pivot as a fraction of that canvas. So
the canvas aspect ratio alone decides how wide the card lands on the board, and
where the ink sits inside the canvas decides where it hangs in the gambit rail.
A square canvas produces a card twice the width of the vanilla ones no matter
what is drawn in it. `make_art.py` therefore works on a portrait canvas and
auto-centres the ink, and the card is registered with `WithVisualScale(0.9f)`
because the template is at the large end of real vanilla cards.
