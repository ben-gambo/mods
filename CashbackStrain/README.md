# Cashback

**A bonus strain: expiring gambits pay their sell value.**

On the Custom strain screen, use the arrows by **STRAINS** to open **MOD STRAINS**,
then pick **Cashback** in its blue bonus column before starting a run.
It adds **no heat** and stays on the run through quitting and
continuing. It only pays when the run's **Gambit Expiry** strain actually makes
a gambit expire; with expiry off, it has no effect.

The gambit disappears at its usual time. Cashback grants the amount you would
get for selling it then, including Collector's accumulated value and Boss
Tooth's current value. Ordinary sales pay normally. Cashback does not trigger
sale effects, and it works when Chaos prevents manual selling.

## Install

Download `CashbackStrain.zip` from [Releases](https://github.com/ben-gambo/mods/releases/tag/CashbackStrain-v1.0.0)
and unpack it into your game's `Mods/` folder:

```
Gambonanza/Mods/CashbackStrain/
├── mod.json
└── Gambonanza.CashbackStrain.dll
```

Requires the [Strain Creation API 1.1.0 or newer](https://github.com/bentrd/GambonanzaMods/releases/tag/StrainApi-v1.1.0) (`StrainApi`) and the
[GambonanzaMods framework](https://github.com/bentrd/GambonanzaMods). Uses the
game's own lucky coin icon at runtime; no game assets are redistributed.

For console testing, `strain on cashback` picks it for your next Custom run;
`strain apply cashback` adds it to an existing run, including a preset run.
The screen selection applies to Custom runs, like other mod strains.

## Build and check

From the repository root:

```sh
./build.sh CashbackStrain
dotnet run --project CashbackStrain/Tests -c Release
```

The source and committed `release/` folder are published together. The payout
intercepts the game's expiry callbacks in their existing order, preserving
Collector's end-of-game growth before its expiry when the game orders them
that way. It grants money without calling a gambit's sale method.

The card uses the API's `.AsBonus()` support: vanilla bonuses stay on the
vanilla page, and mod pages show custom bonuses, so the columns do not overlap.
