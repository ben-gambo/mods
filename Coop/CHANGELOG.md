Two players, one board, over Steam. Shared shop, own pieces, enemy plays twice.

Unzip into your game's `Mods/` folder. **Both players need this version** — the
wire protocol changed, so 0.0.3 refuses to play with 0.0.2 rather than desyncing.

## 0.0.8

- **The host's gambit collection is now the run's collection.** The run-start
  message always carried the host's unlocks, but the game answers every "is this
  unlocked?" from a list it builds once at startup and never re-reads - so each
  client was quietly filtering the shop *and* the gachapon by its own collection.
  P2 now plays the run with the host's gambits unlocked (and gets their own back
  when the session ends).
- **The gachapon capsule is dictated by the host** - its rarity and its three
  cards travel over the wire, so both of you open the same capsule whatever
  either library thinks.
- **Leaving kills the party.** Quitting to the main menu or closing the game now
  ends the co-op session for both players and drops the Steam lobby. Nothing
  ever announced a departure before: the message existed but nobody sent it, so
  the other player was left in a session with no one in it.

## 0.0.7

- **Sleepy Promotion works in co-op.** The wait-to-promote picker now belongs to
  the player who waited: their pawn pick and piece choice travel to the other
  client, which mirrors the wait's bookkeeping (counter, costly-wait coins) and
  applies the promotion without ever opening its own picker. Previously both
  clients rolled their own pawn and opened independent pickers.
- **Skydiver works in co-op.** A dropped pawn's promotion is now held and
  mirrored the same way a normal promotion is; the other client applies the
  choice programmatically while every other placement-triggered gambit still
  fires. Previously both clients got an interactive picker.
- Both were listed as known limits in 0.0.6 - that section is gone.

## 0.0.6

- **Waiting works.** A wait is more than a seat change - it decrements the shared
  3-per-battle counter and it is what triggers the enemy's turn - but the old
  handler mirrored none of that, so the peer's game sat waiting for an action
  that never came: the enemy never played and the round died. A remote wait now
  replays through the game's own WaitManager on the other client, so the counter,
  the costly-wait coin charge and the enemy phase all run the vanilla path on
  both sides.
- The wait button is now properly gated to your own window (it could look alive
  on the other player's turn), and a window that opened after a wait no longer
  has a dead wait button.
- A failed enemy phase can no longer leak its double-turn flag into the next
  round (which used to fire a rogue enemy move mid-window after a recovery).
- **Promotions no longer derail the turn order.** A seat-0 promotion used to let
  the interleaved enemy turn play for real, and a replayed promotion reset the
  shared seats mid-apply - both ended in a soft lock or a rogue enemy move.
- **Round counters stay in step.** The host's double enemy turn quietly advanced
  its round counter one more than the guest's every round, splitting everything
  keyed on it (Savage Mat, crumble timing). Skipped enemy turns (bribes, demons,
  trapped enemies) now also replay their side effects on the other client.
- **Strains actually sync now.** The run-start message always carried the host's
  strain list, but the game only reads it back when LOADING a save - a new co-op
  run played with whatever each client's last solo run had selected: different
  wheel counts, different strain rules on the two boards. The synced list is now
  pushed into the game's live strain state on both clients, and the "enemy plays
  first" strain's wave opener is host-authoritative like every other enemy turn.
- Promotion detection now uses the game's own signal instead of tile geometry,
  which fixes two corners: the Excalibur gambit's promote-next-to-the-king (not
  an end tile) desynced the seats, and a skipped promotion (no enemies left)
  left the other client waiting forever for a choice.

## 0.0.5

- **Soft-lock fixed.** The double enemy turn rides the game's own FinalBossSkip,
  but the game only consumes that flag when its post-turn scan finds you a legal
  move - a stalemate check could strand it, freezing both clients with the enemy
  having played once. The watchdog now detects the strand and recovers the round;
  a second net unlocks input if the game ever withholds it for 8s outside a
  stalemate.
- **The gachapon is shared.** The capsule and its cards were already identical
  (seeded); now the pick is too - take or sell, first click wins, and skipping
  closes it on both clients. Same treatment the wheels got.
- **The turn banner names the player.** "Your turn! (P1)" on your screen,
  "P1's turn!" on theirs, with the seat colour on the P1/P2 tag. The enemy
  phase keeps the vanilla banner.
- **Tile-selection badges lost their P1/P2 text** - the colours carry it.
- **The ally cursor is smooth**: 30 Hz on the wire (was 10) plus per-frame
  smoothing on the receiver.
- **Income now rounds up.** Each player banks ceil(earned/2) - two of you never
  earn less than one solo player. The WIN screen shows it: a CO-OP SPLIT row in
  the money breakdown, and the collect button shows what you actually bank.

## 0.0.4

- **The run-start piece wheel is shared.** 0.0.3 synced the token-shop wheel, but
  the wheel at the start of a run is a different component wearing the same look -
  either player's STOP now resolves both clients, hit-button re-aims included.
- **Setup-phase moves are mirrored.** Rearranging pieces between stock and board
  before pressing GO now replicates - stock↔board both ways, swaps included. This
  was the board mismatch you could create before the battle even started.
- **GO is shared.** Either player's GO sends both clients into battle, so one
  player can no longer fight while the other is still arranging.
- **Selling is mirrored.** Selling a piece (right-click) or a gambit now replays
  on the other client through the game's own sell path, so the shared wallet and
  the board stay in step. Buying, rerolls and limit upgrades already synced.
- The P1/P2 corner labels on tile selections are darker and bolded - they washed
  out against the tile art.

## 0.0.3

- **The tutorial is disabled while the mod is installed.** It drives the game
  directly — locking shop buttons, forcing gambits into the roll, taking over
  navigation — and none of that can be shared between two clients, so a
  first-time host used to come apart within the first wave.
- **The piece wheel is shared.** Either player can hit STOP, and the piece that
  gets taken (or sold) lands on both clients. Previously each player resolved
  their own wheel and the two stocks diverged for the rest of the run.
- **P2's co-op panel opens by itself** when they join, instead of leaving them
  sitting on the home menu with nothing to show that the lobby worked.

## 0.0.2

- A native CO-OP button and panel in the home menu, built from the game's own
  menu parts. The console commands still work but nobody has to use them.

## 0.0.1

- Fixed five critical desyncs, marked the mod beta.
