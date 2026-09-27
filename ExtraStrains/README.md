# Extra Strains

> **Two more strains for players who find the game's own too gentle.**

- **Taxman** (heat 1): every game costs **$1** to start - nothing if you are
  broke.
- **Short Fuse** (heat 2): every game, the **crumble** countdown starts 2 turns
  in, so the board starts falling that much sooner.

Pick them before a run on the game's Custom strain screen: the arrows either
side of the STRAINS title page over to **MOD STRAINS**. Their heat counts on the
gauge like the game's own strains. They come with Custom runs only, and a run
keeps them through quitting and continuing.

## Install

Unpack the zip from [Releases](../../releases?q=ExtraStrains) into your game's
`Mods/` folder:

```
Gambonanza/Mods/ExtraStrains/
├── mod.json
├── Gambonanza.ExtraStrains.dll
├── taxman.png
└── short-fuse.png
```

Needs two library mods: **StrainApi** (the Strain Creation API) and
**CrumbleApi** (the Crumble Control API), both of which ship with the
framework. The Mod Manager installs them for you when it installs this. Built
and played against framework **1.5.3** / game build **25386882**.

## How it works

This is also the reference example for the Strain Creation API, so the two
strains are written the two ways a strain can be:

```csharp
StrainBuilder.Create("taxman")
    .WithName("Taxman")
    .WithDescription("Every game costs <color=*>$1</color> to start.")
    .WithHeat(1)
    .WithIconFile("taxman.png")
    .OnGameStart(_ => PayTheTaxman())
    .Register();
```

**Taxman** is builder hooks only: stateless, a few lines. `OnGameStart` fires
once per game, and never again when a run is continued in the middle of one.

**Short Fuse** is a `StrainBehaviour` subclass, because it keeps state. The
countdown can only be pushed once the game has reset it for the new game, which
it has by the player's first turn - so `OnGameStarted` arms a flag and the first
`OnPlayerTurn` spends it, pushing the countdown through CrumbleApi (which keeps
the lights under the board in step). One behaviour lives exactly as long as the
strain is on the run being played, so the flag needs no other bookkeeping.

Both register in `OnEnable` and unregister in `OnDisable`, which is all it takes
to support `mods disable ExtraStrains` mid-session.

## Files

| Path | |
| --- | --- |
| `src/ExtraStrainsMod.cs` | Registers both strains; Taxman's whole logic. |
| `src/ShortFuseStrain.cs` | Short Fuse's `StrainBehaviour`. |
| `taxman.png`, `short-fuse.png` | 33x27 card icons in the game's strain palette. |
| `release/` | The built artefact, committed - see the root README for why. |
