# Moon Gambit

Adds the **Moon Gambit**, a legendary card that does nothing.

> *Surely this does something... right?*

It has no effect. It has no trigger. It costs 8. It is, pound for pound, the
worst purchase in the game, and it is priced like a legendary because paying a
fortune for nothing is the whole bit.

That's everything. Enjoy.

---

<details>
<summary><b>Spoilers</b> — if you'd rather solve it yourself, don't open this.</summary>

## The Eclipse

The Moon does do something — just not on its own. If you own the Moon and the
vanilla **Sun** gambit in the same run, drag one onto the other in the gambit
tray. When the two celestial bodies align, both cards are consumed and the
**Eclipse Gambit** takes their place — which is also how it gets unlocked in
your collection. After the first merge it can show up in shops like any other
legendary, or you can keep making it the hard way.

The Eclipse still contains the Sun: earning a **KING** turns a random blank
tile golden. But the Moon is crossing in front, so while you hold the card,
**golden tiles behave like EVERY tile**:

- a piece landing on one is gilded (gold), blessed (benediction), protected
  (shield), and duplicated into your reserve as a phantom — all in one landing;
- an **enemy** piece landing on one is trapped, hunter-style. The tile never
  shows it. It's an eclipse; things hide in it.

The cursed tile is deliberately not part of the deal. "EVERY tile" is a
promise, not a threat.

Balance sheet: to get it you spend two legendary purchases and two tray slots,
give up the Sun's steady golden-tile income to a single fused card, and it all
still respects the tile-exhaust strain (one landing spends the tile). Selling
the Eclipse reverts every golden tile on the spot — it's gone like any other
gambit.

## How the merge works (for the curious)

No patching. The game's own drag handler swaps two cards when one is dropped
on an occupied slot; the mod watches `SelectionManager`'s public drag state,
recognises the Moon↔Sun swap signature after the drop settles, and runs the
merge: both cards spiral into the slot they met in, an Eclipse is instantiated
there the same way the shop and the Dragon Egg grant cards, and
`GambitUnlockManager` handles the unlock notification.

</details>

## Building

```
./build.sh MoonGambit             # from the repo root
./build.sh MoonGambit --install   # also copy into your game's Mods/ folder
```

Card art is generated: `python3 tools/make_art.py` rewrites `moon.png` and
`eclipse.png`.
