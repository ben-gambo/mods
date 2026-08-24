Two players, one board, over Steam. Shared shop, own pieces, enemy plays twice.

Unzip into your game's `Mods/` folder. **Both players need this version** — the
wire protocol changed, so 0.0.3 refuses to play with 0.0.2 rather than desyncing.

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
