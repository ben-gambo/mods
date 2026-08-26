using System.Collections;
using System.Collections.Generic;
using Blukulele.CHE;
using Blukulele.Core;
using DG.Tweening;
using UnityEngine;

namespace Gambonanza.DrunkardGambit
{
    /// <summary>
    /// Runtime behaviour of Drunkard's Gambit: after one of your pieces
    /// captures, it staggers on to a random empty tile.
    ///
    /// Mental model of a player capture, from SelectionManager's pointer-up
    /// handler: the whole capture is synchronous within one frame - the victim
    /// is marked dead and unregistered, <c>OnCapture(capturer, victim, tile)</c>
    /// fires, and THEN vanilla finishes the move (parents the capturer to the
    /// tile, sets <c>tile.Piece</c>, starts a 0.1s DOFollow landing tween, and
    /// runs the promotion checks). So nothing can be moved from inside the
    /// event; the stagger waits 0.3s - after the landing tween, and safely
    /// before the enemy turn that TurnManager schedules 0.5s after the move.
    ///
    /// Where the piece goes: a uniformly random board tile that is intact
    /// (not fallen, not shaking, not mid-transformation), empty, and NOT
    /// threatened by any enemy piece. Drunkard's luck: it staggers anywhere,
    /// but never into a capture. If no such tile exists the piece just stays
    /// where it captured - the gambit only relocates, it never kills.
    ///
    /// "Threatened" is computed the way the game's own scare-scan does it
    /// (ThreatEffectBehaviour + TileBehaviour.IsThreatenedBy): every living,
    /// un-trapped black piece contributes its attack squares - a pawn's
    /// GetEatPlaces() (diagonals only; its forward walk is not an attack),
    /// everyone else's GetTilesAvailable() (their moves ARE their attacks).
    /// Those lists are re-used member buffers on the piece, so the results
    /// are copied into the set immediately.
    ///
    /// Deliberately NOT in the tile filter: TileBehaviour.CanBeLandedOn.
    /// Despite the name it is not "a piece may stand here" - it is the static
    /// stock-deployment marker, true only on the player's bottom row or two
    /// and never anywhere else, so filtering on it sends every stagger
    /// straight back to the home rows (the 1.0.0 bug).
    ///
    /// Two deliberate non-moves:
    /// - A capture that triggers a promotion is left alone. PromotionManager
    ///   holds (piece, tile) from the pointer-up frame and later instantiates
    ///   the promoted piece into that remembered tile - staggering the pawn
    ///   out from under the promotion UI would leave the new piece on one
    ///   tile and a destroyed pawn registered on another. Drunk pawns sober
    ///   up at the finish line.
    /// - StartingTile is untouched, so the wave-end reset walks the piece
    ///   back to its own post like any other survivor.
    ///
    /// This component exists exactly as long as the player owns the card, so
    /// buying it arms the stagger and selling it disarms it, no bookkeeping.
    /// </summary>
    public class GambitDrunkard : BaseGambit
    {
        private void Start()
        {
            var sm = SingletonMonoBehaviour<SelectionManager>.Instance;
            if (sm != null)
                sm.OnCapture += OnPlayerCaptured;
        }

        private void OnDestroy()
        {
            if (SingletonMonoBehaviour<SelectionManager>.IsCreated())
                SingletonMonoBehaviour<SelectionManager>.Instance.OnCapture -= OnPlayerCaptured;
        }

        private void OnPlayerCaptured(BasePieceBehaviour piece, BasePieceBehaviour victim, TileBehaviour tile)
        {
            if (piece == null || tile == null) return;
            if (piece.PieceColor != PieceColor.WHITE) return;
            if (WillPromoteHere(piece, tile)) return;

            StartCoroutine(CO_Stagger(piece, tile));
        }

        /// <summary>
        /// Mirrors the two promotion triggers in SelectionManager's pointer-up:
        /// a pawn-hierarchy piece on the white end row, and (with Excalibur) a
        /// pawn capturing next to a white king - or next to anyone, under
        /// Anarchist. Erring toward "would promote" only costs a stagger.
        /// </summary>
        private static bool WillPromoteHere(BasePieceBehaviour piece, TileBehaviour tile)
        {
            if (tile.IsEnd && tile.PromoteColor == PieceColor.WHITE
                && piece.PieceHierarchy == PieceHierarchy.PAWN)
                return true;

            if (piece.GetPieceType() != PieceType.PAWN) return false;
            var promo = SingletonMonoBehaviour<PromotionManager>.IsCreated()
                ? SingletonMonoBehaviour<PromotionManager>.Instance : null;
            if (promo == null || !promo.ExcaliburGambitActivated) return false;

            var anarchist = SingletonMonoBehaviour<GambitManager>.IsCreated()
                && SingletonMonoBehaviour<GambitManager>.Instance.AnarchistEnable;
            foreach (var neighbour in tile.GetNeighbourTiles())
            {
                if (neighbour == null || neighbour.Piece == null) continue;
                if (neighbour.Piece.PieceColor != PieceColor.WHITE) continue;
                if (neighbour.Piece.GetPieceType() == PieceType.KING || anarchist)
                    return true;
            }
            return false;
        }

        private IEnumerator CO_Stagger(BasePieceBehaviour piece, TileBehaviour from)
        {
            // Past the pointer-up frame and the 0.1s landing tween, ahead of
            // the enemy turn at 0.5s.
            yield return new WaitForSeconds(0.3f);

            var gm = SingletonMonoBehaviour<GameManager>.IsCreated() ? SingletonMonoBehaviour<GameManager>.Instance : null;
            if (gm == null || gm.CurrentState != State.INGAME) yield break;
            if (piece == null || piece.IsDead || piece.InStock) yield break;
            // Another gambit (or a second capture) may have moved it first.
            if (piece.CurrentTile != from || from.Piece != piece) yield break;

            var target = PickRandomSafeEmptyTile(from);
            if (target == null)
            {
                Debug.Log("[Drunkard] No safe empty tile to stagger to. Steady as she goes.");
                yield break;
            }

            from.Piece = null;
            piece.transform.parent = target.PlaceToPutPieces;
            piece.CurrentTile = target;
            target.Piece = piece;

            piece.transform.DOKill();
            piece.transform.DOFollow(target.PlaceToPutPieces, 0.35f);
            piece.transform.DOPunchRotation(new Vector3(0f, 0f, 14f), 0.45f, 6, 0.7f);
            piece.ShinyEffect();
            var waves = SingletonMonoBehaviour<ShockWaveManager>.IsCreated() ? SingletonMonoBehaviour<ShockWaveManager>.Instance : null;
            if (waves != null)
                waves.StartWave(target.GetWaveBehaviour(), 0.4f, 0.15f);

            Debug.Log($"[Drunkard] {piece.name} captured on {from.name} and staggered off to {target.name}.");
            Trigger();
        }

        /// <summary>
        /// A uniformly random intact, empty, unthreatened board square.
        /// Shaking tiles are excluded - depositing a drunk on ground that is
        /// about to fall is a joke, but not this card's joke - and so are
        /// tiles any enemy could capture on next turn.
        /// </summary>
        private static TileBehaviour PickRandomSafeEmptyTile(TileBehaviour except)
        {
            var bm = SingletonMonoBehaviour<BoardManager>.IsCreated() ? SingletonMonoBehaviour<BoardManager>.Instance : null;
            var board = bm != null ? bm.Board : null;
            if (board == null) return null;

            var threatened = GetEnemyThreatenedTiles();
            var safe = new List<TileBehaviour>();
            int rows = board.GetLength(0);
            int cols = board.GetLength(1);
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    var tile = board[r, c];
                    if (tile == null || tile == except) continue;
                    if (!tile.gameObject.activeInHierarchy) continue;
                    if (tile.HasFell || tile.IsShaking) continue;
                    if (tile.TileVisual != null && tile.TileVisual.IsModifying) continue;
                    if (tile.Piece != null) continue;
                    if (threatened.Contains(tile)) continue;
                    safe.Add(tile);
                }
            }
            if (safe.Count == 0) return null;
            return safe[Random.Range(0, safe.Count)];
        }

        /// <summary>
        /// Every tile an enemy could capture on next turn, gathered the way
        /// the game's own scare-scan gathers them: living un-trapped black
        /// pieces, a pawn's diagonal GetEatPlaces(), everyone else's
        /// GetTilesAvailable(). GetAllPieces() first, so freshly-captured
        /// corpses (disabled, destroyed 0.6s later) are already filtered out.
        /// </summary>
        private static HashSet<TileBehaviour> GetEnemyThreatenedTiles()
        {
            var threatened = new HashSet<TileBehaviour>();
            var em = SingletonMonoBehaviour<EnemyManager>.IsCreated() ? SingletonMonoBehaviour<EnemyManager>.Instance : null;
            if (em == null) return threatened;

            em.GetAllPieces();
            foreach (var enemy in em.EnemyPieces)
            {
                if (enemy == null || enemy.IsDead) continue;
                if (enemy.Modifier != null && enemy.Modifier.IsTrapped) continue;

                var pawn = enemy.GetComponent<PawnPieceBehaviour>();
                var tiles = pawn != null ? pawn.GetEatPlaces() : enemy.GetTilesAvailable();
                if (tiles == null) continue;
                // The non-pawn lists are re-used member buffers on the piece -
                // copy the contents before anything else calls into it.
                foreach (var tile in tiles)
                {
                    if (tile != null) threatened.Add(tile);
                }
            }
            return threatened;
        }

        public override void Trigger()
        {
            VisualEffect();
        }
    }
}
