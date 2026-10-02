# Cashback

**QoL: expiring gambits pay their sell value.**

Cashback works whenever the mod is enabled, including in preset **King** runs.
It adds no strain or bonus and leaves the chosen difficulty and the game's
in-game gambit completion tracking unchanged. There is nothing to select on
the strain screen.

It only pays when the game's **Gambit Expiry** rule actually makes a gambit
expire; with expiry off, it has no effect. The gambit disappears at its usual
time. Cashback grants the amount you would get for selling it then, including
Collector's accumulated value and Boss Tooth's current value. Ordinary sales
pay normally. Cashback does not trigger sale effects, and it works when Chaos
prevents manual selling.

## Install or update

Download `CashbackStrain.zip` from [Releases](https://github.com/ben-gambo/mods/releases/tag/CashbackStrain-v2.0.0)
and unpack it into your game's `Mods/` folder, replacing the old folder:

```
Gambonanza/Mods/CashbackStrain/
├── mod.json
└── Gambonanza.CashbackStrain.dll
```

Requires only the [GambonanzaMods framework](https://github.com/bentrd/GambonanzaMods).
Strain Creation API is no longer required. The install folder keeps its old
name so existing installations update in place.

Restart the game after updating. Version 2.0 replaces the former selectable
bonus with a regular mod: enable or disable **Cashback** in the mod manager.
An existing Custom run keeps its original difficulty; start a King preset run
to earn King completion marks on played gambits. The framework's separate
Steam achievement setting remains unchanged.

## Build and check

From the repository root:

```sh
./build.sh CashbackStrain
dotnet run --project CashbackStrain/Tests -c Release -- --assembly CashbackStrain/release/Gambonanza.CashbackStrain.dll
```

The source and committed `release/` folder are published together. The payout
intercepts the game's expiry callbacks in their existing order, preserving
Collector's end-of-game growth before its expiry when the game orders them
that way. It grants money without calling a gambit's sale method. Disabling
the mod restores the original callbacks.
