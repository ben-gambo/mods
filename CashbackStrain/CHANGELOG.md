## Cashback 2.0.0

- Cashback is now a regular QoL mod, active whenever enabled. It works in
  preset runs, including King, without adding a strain or bonus or changing
  native difficulty and in-game gambit completion tracking.
- Removes the Cashback bonus card and the Strain Creation API dependency.
- Expiring gambits still pay their current sell value, including Collector
  and Boss Tooth, once per gambit and without triggering manual-sale effects.
- Disabling Cashback immediately restores normal expiry behavior.

Update the existing `CashbackStrain` folder with this ZIP and restart the
game. Only the GambonanzaMods framework is required. An existing Custom run
keeps its original difficulty; use a King preset for King completion marks.
The framework's separate Steam achievement setting remains unchanged.
