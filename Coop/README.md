# Co-op

Two players, one board, over Steam. No server, no port forwarding, no accounts —
it rides the Steam relay network the game is already connected to.

One player hosts, the other joins. You share the board and the shop; you each
own your own pieces. **P1 is red, P2 is blue.**

## Playing

Both players need the mod installed and the game launched **through Steam**.

Open the mod console (`` ` `` or `F10`) and:

| Command | What it does |
| --- | --- |
| `coop host` | create a friends-only Steam lobby |
| `coop invite` | open the Steam invite overlay |
| `coop join <lobbyId>` | join by id — accepting a Steam invite works too |
| `coop start` | **host only:** begin a synced run |
| `coop status` | show seat, peer, whose turn it is |
| `coop leave` | end the session and restore your solo save |
| `coop verbose` | toggle detailed logging |

The usual flow is: host runs `coop host`, then `coop invite` and picks a friend
in the Steam overlay. Once they accept, both see `peer connected`, and the host
runs `coop start`.

## How a round plays

**P1 moves → P2 moves → the enemy moves twice.**

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

One wallet, spent by both of you. Rerolls and piece-limit upgrades sync too.

## Income

Post-battle income is **halved** — base reward, capture bonus and interest.
Two players clearing a stage would otherwise earn a solo player's income twice
over and snowball.

In-battle gambit income is **not** touched: Investor, Billionaire, Finish Line
and friends pay out in full.

## What the host owns

The host is authoritative for the things that cannot be reproduced remotely:

- **The seed.** Sent at run start along with the difficulty, strains and the
  host's unlocked-gambit list, so both clients generate the same waves and the
  same shops. If the host hasn't unlocked a gambit, neither of you will see it.
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

## Limits

- Two players. The lobby is capped at 2.
- Both sides need the same mod version and the same game build; mismatches are
  reported at handshake.
- Twitch chaos mode is not supported.
- Gambit *placement* is not mirrored yet — buying is, and buying is what
  activates a gambit, so this only affects rearranging slots by hand.

## Diagnostics

Dropping an empty file named `autohost` into the mod folder makes it create a
lobby at boot and log the result — useful for checking the Steam path without
touching the console.
