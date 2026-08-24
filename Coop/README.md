# Co-op  *(beta)*

Two players, one board, over Steam. No server, no port forwarding, no accounts —
it rides the Steam relay network the game is already connected to.

One player hosts, the other joins. You share the board and the shop; you each
own your own pieces. **P1 is red, P2 is blue.**

> **This is a beta.** The Steam connection, the shared shop and the turn
> flow all work, but two-player sessions have had limited real-world testing.
> Expect rough edges, and see *Known limits* below.

## Playing

Both players need the mod installed and the game launched **through Steam**.

Press **CO-OP** in the main menu, next to Play. Everything lives in that panel:

1. **Host a game** — creates a friends-only Steam lobby.
2. **Invite a friend** — opens the Steam invite overlay. (They can also just
   accept an invite from their friends list; joining is automatic.)
3. **Start the run** — host only, once both of you are in.
4. **Leave** — ends the session and restores your solo save.

Quitting to the main menu or closing the game ends the party too, for both of
you — the lobby is dropped and each player goes back to their own save.

The panel shows which seat you are, who you are playing with, and what to do
next.

<details>
<summary>Console commands (optional)</summary>

`coop menu` · `coop host` · `coop invite` · `coop join <lobbyId>` ·
`coop start` · `coop status` · `coop leave` · `coop verbose`
</details>

## How a round plays

**P1 moves → P2 moves → the enemy moves twice.**

Before the battle, the setup phase is fully shared: both players can rearrange
pieces between stock and board at the same time, every move mirrors to the other
client, and either player's **GO** launches the battle for both. The run-start
piece wheel is shared too — either player can hit STOP.

Both players see each other's cursor live, and the tile a player is holding a
piece over gets their coloured badge with a `P1`/`P2` corner label. Your own
pieces carry a discreet tint in your colour, so a crowded board still reads at
a glance.

The doubled enemy turn is the game's own mechanism — the same one the final
boss uses — so it behaves exactly like a vanilla double-move, crumblers and all.

## The shop

Shared, and shared live: you watch your friend browse and buy. Both clients roll
the *identical* shop, because every shop roll in Gambonanza is a pure function
of the run seed, the wave, and a per-name counter — so a purchase only has to
travel as a slot number, not as an item.

One wallet, spent by both of you. Rerolls, piece-limit upgrades and **selling**
(pieces and gambits) sync too.

## Income

Post-battle income is **halved, rounded up** — each player banks ⌈earned/2⌉ of
the base reward, capture bonus and interest. Two players clearing a stage would
otherwise earn a solo player's income twice over and snowball. The WIN screen
shows the split as its own row in the money breakdown, and the collect button
shows the amount you actually bank.

In-battle gambit income is **not** touched: Investor, Billionaire, Finish Line
and friends pay out in full.

## What the host owns

The host is authoritative for the things that cannot be reproduced remotely:

- **The seed.** Sent at run start along with the difficulty, strains and the
  host's unlocked-gambit list, so both clients generate the same waves and the
  same shops. The host's collection *is* the run's collection: if the host
  hasn't unlocked a gambit neither of you sees it, and anything the host has
  unlocked is available to both of you for the run even if P2 hasn't earned it
  yet. P2 gets their own collection back when the session ends.
- **The gachapon capsule**, sent card by card, so an unlock difference can never
  put different gambits in front of the two of you.
- **The enemy AI.** Its move selection uses unseeded `UnityEngine.Random`, so it
  can't be replayed from a seed. The host picks each enemy move and sends the
  coordinates; the guest replays it through the game's own move routine.
- **The wallet**, as a consequence of the shared shop.

Every 5 seconds the host sends a board checksum. If the two boards ever diverge,
you get a `DESYNC` warning in the console rather than two players quietly
playing different games.

## Your solo save is protected

A co-op run would otherwise overwrite your single-player save — same file, same
slot. The mod snapshots `save.json` when a co-op run starts and restores it when
the session ends (`coop leave`, or the peer disconnecting).

Restart the game after a co-op session to reload the restored save.

## Known limits

- Two players. The lobby is capped at 2.
- Both sides need the same mod version and the same game build; mismatches are
  refused at handshake.
- **Both piece wheels and the gachapon are shared** — either player can stop,
  pick, sell or skip, and the outcome lands on both clients. **Pachinko is not
  yet mirrored**: the purchase syncs, but the ball physics is local and the
  outcomes can differ. Treat pachinko as unsupported for now.
- Twitch chaos mode is not supported.
- Gambit *placement* is not mirrored — buying is, and buying is what activates a
  gambit, so this only affects rearranging slots by hand.
- If the two boards ever diverge you get a `DESYNC` warning in the console.
  `coop leave` and restart the run.

## The tutorial is off

Gambonanza's tutorial drives the game directly — it locks shop buttons, forces
particular gambits into the roll and takes over navigation — and none of that
can be shared between two clients. With this mod installed the tutorial is
suppressed, so a first-time player can host without the run coming apart. It is
suppressed at runtime only; your save still says you haven't done it.

## Diagnostics

Dropping an empty file named `autohost` into the mod folder makes it create a
lobby at boot and log the result — useful for checking the Steam path without
touching the console.
