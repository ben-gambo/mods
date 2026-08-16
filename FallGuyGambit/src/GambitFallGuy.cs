using System.Collections;
using Blukulele.CHE;
using Blukulele.Core;
using UnityEngine;

namespace Gambonanza.FallGuyGambit
{
    /// <summary>
    /// Runtime behaviour of Fall Guy's Gambit.
    ///
    /// Mental model of a crumble death, from CrumbleManager.CO_MakeShakingTileFall:
    /// for each shaking tile, the manager (1) books the piece into the graveyard,
    /// (2) calls tile.Fall(), (3) fires <c>CrumbleManager.OnFall(tile)</c> - all
    /// synchronously. The piece itself is only destroyed later, by
    /// TileVisual.CO_Fall, which waits FlowManager.FallingTileDelay (0.3s) and then
    /// looks the victim up with <c>GetComponentInChildren&lt;BasePieceBehaviour&gt;()</c> -
    /// pieces are children of their tile.
    ///
    /// So the whole gambit is: on OnFall, re-parent the piece to a safe tile before
    /// that delayed lookup runs. The falling tile then has no piece-child, the
    /// destroy path finds nothing, and the piece has simply moved. The graveyard
    /// entry the manager just wrote is popped again so the save is not also a
    /// free buy-back token.
    ///
    /// Every falling piece gets this treatment (there is no per-game limit).
    /// Where the piece goes, in order:
    /// 1. The nearest intact, empty board square (nearest by world distance to the
    ///    tile that fell; shaking tiles are excluded - everything shaking falls in
    ///    this same batch). Rescues within one batch see each other's claims,
    ///    because the target tile's Piece is set synchronously.
    /// 2. A free stash slot, if the whole board is out of squares.
    /// 3. Nowhere. The vanilla death proceeds untouched.
    ///
    /// The stash branch has two wrinkles, both from the fact that vanilla never
    /// moves a piece board-to-stock mid-game:
    /// - PieceManager's WhitePieces list is what every lose-check reads. If other
    ///   pieces are still standing we unregister the stashed one, mirroring what
    ///   stock pieces look like everywhere else. But if it was the LAST piece
    ///   standing, unregistering would flip both lose-checks (CrumbleManager's and
    ///   TurnManager's) to "board wiped" - so we leave it registered, remember it,
    ///   and remove the duplicate registration SelectionManager adds when the
    ///   player later places it back. TurnManager already skips InStock entries
    ///   when it scans that list, so the anomaly is invisible to it, and
    ///   PieceManager rebuilds the list from scratch on every INGAME entry.
    /// - With no board pieces, TurnManager's turn scan never reaches a
    ///   PlayerCanPlay() and the input lock would stay on forever. A small
    ///   coroutine waits out that scan and, if the player's only pieces are
    ///   stashed, hands the turn back so the piece can be re-placed.
    ///
    /// This component exists exactly as long as the player owns the card, so
    /// buying it arms the save and selling it disarms it, with no bookkeeping.
    /// </summary>
    public class GambitFallGuy : BaseGambit
    {
        // Filled only by the last-piece stash branch: pieces we deliberately left
        // in WhitePieces while they sit in the stash (one crumble batch can stash
        // several). See OnPiecePlacedInGame.
        private readonly System.Collections.Generic.HashSet<BasePieceBehaviour> _stashedPieces =
            new System.Collections.Generic.HashSet<BasePieceBehaviour>();

        private void Start()
        {
            var cm = SingletonMonoBehaviour<CrumbleManager>.Instance;
            if (cm != null)
                cm.OnFall += OnTileFell;

            var gm = SingletonMonoBehaviour<GameManager>.Instance;
            if (gm != null)
                gm.onStateChanged += OnStateChanged;

            var sm = SingletonMonoBehaviour<SelectionManager>.Instance;
            if (sm != null)
                sm.OnPlacePieceOnBoardInGame += OnPiecePlacedInGame;
        }

        private void OnDestroy()
        {
            if (SingletonMonoBehaviour<CrumbleManager>.IsCreated())
                SingletonMonoBehaviour<CrumbleManager>.Instance.OnFall -= OnTileFell;

            if (SingletonMonoBehaviour<GameManager>.IsCreated())
                SingletonMonoBehaviour<GameManager>.Instance.onStateChanged -= OnStateChanged;

            if (SingletonMonoBehaviour<SelectionManager>.IsCreated())
                SingletonMonoBehaviour<SelectionManager>.Instance.OnPlacePieceOnBoardInGame -= OnPiecePlacedInGame;
        }

        private void OnStateChanged(State state)
        {
            if (state == State.INGAME || state == State.LOAD_RUN)
            {
                // Every INGAME entry makes PieceManager rebuild WhitePieces from
                // scratch (stock pieces excluded), which erases the
                // registered-while-stashed anomaly on its own - stop tracking it.
                _stashedPieces.Clear();
            }
        }

        // SelectionManager registers a piece when it is placed from the stash
        // during a game. Our last-piece rescue left it registered on purpose, so
        // that vanilla call is the duplicate - take one of the two back out.
        private void OnPiecePlacedInGame(BasePieceBehaviour piece, TileBehaviour _)
        {
            if (piece == null || !_stashedPieces.Remove(piece)) return;
            var pm = SingletonMonoBehaviour<PieceManager>.IsCreated() ? SingletonMonoBehaviour<PieceManager>.Instance : null;
            if (pm != null)
                pm.UnregisterPiece(piece);
        }

        private void OnTileFell(TileBehaviour fallenTile)
        {
            if (fallenTile == null) return;

            var gm = SingletonMonoBehaviour<GameManager>.IsCreated() ? SingletonMonoBehaviour<GameManager>.Instance : null;
            if (gm == null || gm.CurrentState != State.INGAME) return;

            // Enemy pieces on a falling tile are flagged IsDead before Fall();
            // a live white piece here is exactly "one of ours, about to fall".
            var piece = fallenTile.Piece;
            if (piece == null || piece.IsDead) return;
            if (piece.PieceColor != PieceColor.WHITE) return;

            var target = FindNearestFreeTile(fallenTile);
            if (target != null)
            {
                RescueToTile(piece, fallenTile, target);
                return;
            }

            int slot;
            var place = FindFreeStashSlot(out slot);
            if (place != null)
            {
                RescueToStash(piece, fallenTile, place, slot);
                return;
            }

            // No square, no stash room. Vanilla's fall pipeline is already
            // mid-swing, so "do nothing" is the death.
            Debug.Log("[FallGuy] No free square, no stash room. It dies lol.");
        }

        /// <summary>
        /// Nearest intact empty square by world distance to the fallen tile.
        /// Shaking tiles are excluded: OnFall fires mid-batch, and every tile
        /// still shaking at that moment either falls later in this same batch or
        /// is queued for the next one - neither is anywhere to leave a piece we
        /// just went to the trouble of saving.
        /// </summary>
        private static TileBehaviour FindNearestFreeTile(TileBehaviour fallenTile)
        {
            var bm = SingletonMonoBehaviour<BoardManager>.IsCreated() ? SingletonMonoBehaviour<BoardManager>.Instance : null;
            var board = bm != null ? bm.Board : null;
            if (board == null) return null;

            TileBehaviour best = null;
            float bestDist = float.MaxValue;
            int rows = board.GetLength(0);
            int cols = board.GetLength(1);
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    var tile = board[r, c];
                    if (tile == null || tile == fallenTile) continue;
                    if (!tile.gameObject.activeInHierarchy) continue;
                    if (tile.HasFell || tile.IsShaking) continue;
                    if (tile.Piece != null) continue;

                    float dist = Vector3.Distance(fallenTile.transform.position, tile.transform.position);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        best = tile;
                    }
                }
            }
            return best;
        }

        /// <summary>
        /// First open stash slot, or null. Occupancy is judged the way vanilla
        /// judges it (a piece-child under the place), and slots locked by the
        /// LOCKS_STOCK strain's block are not open.
        /// </summary>
        private static TileBehaviour FindFreeStashSlot(out int slot)
        {
            slot = -1;
            var stock = SingletonMonoBehaviour<StockManager>.IsCreated() ? SingletonMonoBehaviour<StockManager>.Instance : null;
            var places = stock != null ? stock.Places : null;
            if (places == null) return null;

            for (int i = 0; i < places.Length; i++)
            {
                var place = places[i];
                if (place == null) continue;
                if (place.GetComponentInChildren<BasePieceBehaviour>() != null) continue;
                var block = place.GetComponent<StockBlockBehaviour>();
                if (block != null && block.IsActive) continue;
                slot = i;
                return place;
            }
            return null;
        }

        private void RescueToTile(BasePieceBehaviour piece, TileBehaviour from, TileBehaviour to)
        {
            PopGraveyardEntry(piece);

            // Re-parenting is the actual save: TileVisual.CO_Fall's delayed
            // GetComponentInChildren on the fallen tile now comes up empty.
            from.Piece = null;
            piece.transform.parent = to.PlaceToPutPieces;
            piece.CurrentTile = to;
            to.Piece = piece;
            piece.InStock = false;
            // StartingTile is left alone on purpose: the fallen tile re-appears
            // when the wave ends, and the wave-end reset walks pieces back to
            // their own posts - including this one.

            piece.transform.DOFollow(to.PlaceToPutPieces, 0.25f);
            piece.ShinyEffect();
            var waves = SingletonMonoBehaviour<ShockWaveManager>.IsCreated() ? SingletonMonoBehaviour<ShockWaveManager>.Instance : null;
            if (waves != null)
                waves.StartWave(to.GetWaveBehaviour(), 0.4f, 0.15f);

            Debug.Log($"[FallGuy] Saved {piece.name} from the fall -> {to.name}.");
            Trigger();
        }

        private void RescueToStash(BasePieceBehaviour piece, TileBehaviour from, TileBehaviour place, int slot)
        {
            PopGraveyardEntry(piece);

            from.Piece = null;
            piece.transform.parent = place.PlaceToPutPieces;
            piece.CurrentTile = place;
            place.Piece = piece;
            piece.InStock = true;

            var stock = SingletonMonoBehaviour<StockManager>.Instance;
            stock.Pieces[slot] = piece;

            if (AnotherPieceStillStanding(piece))
            {
                // Normal case: mirror what a stock piece looks like everywhere
                // else in vanilla - not registered. The lose-checks still see the
                // other standing pieces.
                SingletonMonoBehaviour<PieceManager>.Instance.UnregisterPiece(piece);
            }
            else
            {
                // Last piece on the board. An unregistered piece here would empty
                // WhitePieces and both lose-checks would call the run dead while
                // the player is holding a perfectly good piece. Leave it
                // registered (TurnManager skips InStock entries), dedupe on
                // re-placement, and hand the turn back once vanilla's scan is done.
                _stashedPieces.Add(piece);
                StartCoroutine(CO_UnstickTurnIfNeeded());
            }

            piece.transform.DOFollow(place.PlaceToPutPieces, 0.35f);
            Debug.Log($"[FallGuy] No free square - stashed {piece.name}.");
            Trigger();
        }

        /// <summary>
        /// True if any other white piece is standing on ground that survives this
        /// crumble batch. Shaking tiles count as gone - at OnFall time everything
        /// shaking is doomed or next in line - so this can only undercount, which
        /// errs toward the safe (keep-registered) branch.
        /// </summary>
        private static bool AnotherPieceStillStanding(BasePieceBehaviour rescued)
        {
            var pm = SingletonMonoBehaviour<PieceManager>.IsCreated() ? SingletonMonoBehaviour<PieceManager>.Instance : null;
            if (pm == null) return false;
            foreach (var p in pm.GetWhitePieces())
            {
                if (p == null || p == rescued) continue;
                if (p.IsDead || p.InStock) continue;
                var tile = p.CurrentTile;
                if (tile == null || tile.IsStock) continue;
                if (tile.HasFell || tile.IsShaking) continue;
                return true;
            }
            return false;
        }

        /// <summary>
        /// TurnManager's turn scan only re-opens input via a piece that can move
        /// on the board. If the rescue just stashed the player's last one, wait
        /// for that scan to finish (it runs ~0.3s after the crumble) and unlock
        /// the turn ourselves so the stashed piece can be placed.
        /// </summary>
        private IEnumerator CO_UnstickTurnIfNeeded()
        {
            yield return new WaitForSeconds(0.75f);

            var gm = SingletonMonoBehaviour<GameManager>.IsCreated() ? SingletonMonoBehaviour<GameManager>.Instance : null;
            if (gm == null || gm.CurrentState != State.INGAME) yield break;

            var tm = SingletonMonoBehaviour<TurnManager>.IsCreated() ? SingletonMonoBehaviour<TurnManager>.Instance : null;
            if (tm == null || tm.CanPlay) yield break;

            var pm = SingletonMonoBehaviour<PieceManager>.IsCreated() ? SingletonMonoBehaviour<PieceManager>.Instance : null;
            if (pm == null) yield break;
            foreach (var p in pm.GetWhitePieces())
            {
                if (p != null && !p.InStock)
                    yield break; // a board piece exists; vanilla owns the turn
            }

            Debug.Log("[FallGuy] Only pieces left are stashed - unlocking the turn.");
            tm.CanPlay = true;
        }

        /// <summary>
        /// CrumbleManager booked the piece into the buy-back graveyard right
        /// before OnFall, so for a saved piece the newest entry is that booking -
        /// pop it, or the save would also mint a free graveyard copy. Phantom
        /// pieces are never booked, so there is nothing to pop for them.
        /// </summary>
        private static void PopGraveyardEntry(BasePieceBehaviour piece)
        {
            if (piece.Modifier != null && piece.Modifier.IsPhantom) return;

            string letter;
            switch (piece.GetPieceType(realType: true))
            {
                case PieceType.PAWN: letter = "P"; break;
                case PieceType.ROOK: letter = "R"; break;
                case PieceType.KNIGHT: letter = "N"; break;
                case PieceType.BISHOP: letter = "B"; break;
                case PieceType.QUEEN: letter = "Q"; break;
                case PieceType.KING: letter = "K"; break;
                default: letter = ""; break;
            }

            var data = DataManager.Instance != null ? DataManager.Instance.Data : null;
            var graveyard = data != null ? data.Graveyard : null;
            if (graveyard == null || graveyard.Count == 0) return;
            if (graveyard[graveyard.Count - 1] == letter)
                graveyard.RemoveAt(graveyard.Count - 1);
        }

        public override void Trigger()
        {
            VisualEffect();
        }
    }
}
