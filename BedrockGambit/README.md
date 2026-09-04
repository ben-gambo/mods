# Bedrock's Gambit

> **The board never CRUMBLES while you hold this.**

Hold the card and the floor holds. The crumble countdown under the board
stops ticking, tiles that were already shaking never fall, and the crumble
picks no new ones. Sell the card and everything resumes exactly where it left
off, the next turn.

Rare, 6 coins. It switches a whole pressure mechanic off for as long as it is
held, which is stronger than a rescue card like Fall Guy (Epic, 8) is passive,
so it sits at the top of the Rare band rather than down with the commons.

Two edges worth knowing:

- Tiles the **Mask boss** or a **Crumbler enemy** shake are not crumble mode:
  they queue up while you hold the card and fall together the turn after it
  goes. The card holds the floor; it does not undo what those enemies do.
- A run saved with the crumble already under way resumes in crumble mode -
  and then holds, since the card is in your hand again.

## Install

Unpack the zip from [Releases](../../releases?q=BedrockGambit) into your
game's `Mods/` folder:

```
Gambonanza/Mods/BedrockGambit/
├── mod.json
├── Gambonanza.BedrockGambit.dll
└── bedrock.png
```

Needs two library mods: **GambitApi** and **CrumbleApi** (the Crumble Control
API), both of which ship with the framework. The Mod Manager installs them for
you when it installs this. No framework update is required - this is an
ordinary mod DLL loaded by ModHost. Built and played against framework
**1.5.1** / game build **25059529**.

## How it works

The whole gambit is one handle from the Crumble Control API:

```csharp
private void Start()     => _freeze = Crumble.Freeze(this, "Bedrock's Gambit");
private void OnDestroy() => _freeze?.Dispose();
```

`Start` and `OnDestroy` are exactly the lifetime of "the player owns this
card", so buying it arms the freeze and selling it disarms it, with no
bookkeeping. Passing the component as the handle's owner is the belt to that
pair of braces: if the object is ever destroyed without `OnDestroy` running,
the API releases the handle on its own.

What the API does with it: the game runs one private crumble step at the start
of every player turn - tick the counter, start crumble mode when it runs out,
drop the shaking tiles, pick the next ones. CrumbleApi brackets that step and,
while a freeze is held, skips the tick, hides crumble mode from the step and
hands it an empty tile list, so the step does nothing and the state is put
back the moment it returns. Everything the rest of the game reads (the
countdown lights, the enemy AI's sense of urgency, the crumble-triggered
gambits) sees the same numbers it would have seen, just standing still.

The only other thing in the file is the flash: the card lights up on a turn
where the board would otherwise have crumbled - crumble mode on, or tiles
shaking - and stays quiet on the ordinary countdown ticks, so you see the save
when there is one to see.

## Files

| Path | |
| --- | --- |
| `src/BedrockGambitMod.cs` | Registers the card - name, art, rarity, price. Also checks CrumbleApi is present. |
| `src/GambitBedrock.cs` | The gameplay: take the freeze, release it, flash. |
| `tools/make_art.py` | Regenerates `bedrock.png` from the Minecraft bedrock texture. Pure stdlib Python 3. |
| `release/` | The built artefact, committed - see the root README for why. |

## A note on the art

The Minecraft bedrock block, as the inventory draws it: a 2:1 isometric cube,
top face lit, right face in shadow. The texture is Mojang's; it is not in this
repository. `make_art.py` reads it out of the Minecraft client jar in your
launcher folder (or a path you pass) and commits only the derived card, and
the card is fan art for a free mod - not a Minecraft product and not endorsed
by Mojang.

Card size is the whole problem. A gambit card is 28x32 and a cube face here
is 11 pixels wide, so the 16x16 texture cannot simply be pasted on:
nearest-sampling it onto the faces gives a hexagon of static. So the texture
is first averaged down to 8x8 - bedrock's blotches survive, its grain does
not - and then projected with heavy supersampling (each output pixel averages
a 12x12 grid of samples), which is how Minecraft's own GUI keeps a block
readable at 16 pixels. The three faces are lit much further apart than the
game's 1.0 / 0.8 / 0.6 (top 1.8x, left 1.2x, right 0.75x), because bedrock's
average value is dark and at this size the gentler ratios leave the faces
indistinguishable.

The canvas is the vanilla template's exact 28x32 so the texels land 1:1 on
the game's pixel grid, with the conventions every card in this repo follows:
two transparent rows on top for the highlight outline, bottom flush with the
rail baseline, and side padding that puts the ink's centre on the template's
x=0.45 pivot line. Registered with `WithVisualScale(0.9f)` like its
neighbours.
