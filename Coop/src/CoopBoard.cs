using System;
using System.Collections.Generic;
using Blukulele.CHE;
using Blukulele.Core;
using DG.Tweening;
using UnityEngine;

namespace Gambonanza.Coop
{
    /// <summary>Stable addressing for pieces + replication of remote actions.</summary>
    internal static class CoopBoard
    {
        public const char KindBoard = 'B';
        public const char KindStock = 'S';
        public const char KindNone = '-';

        public static BoardManager Board => SingletonMonoBehaviour<BoardManager>.Instance;
        public static StockManager Stock => SingletonMonoBehaviour<StockManager>.Instance;
        public static SelectionManager Sel => SingletonMonoBehaviour<SelectionManager>.Instance;

        // ---- addressing ----

        public static bool TryLocate(BasePieceBehaviour piece, out char kind, out int a, out int b)
        {
            kind = KindNone; a = -1; b = -1;
            if (piece == null) return false;

            var tile = piece.CurrentTile;
            if (tile != null)
            {
                if (tile.IsStock)
                {
                    var places = Stock?.Places;
                    if (places != null)
                        for (int i = 0; i < places.Length; i++)
                            if (ReferenceEquals(places[i], tile)) { kind = KindStock; a = i; b = 0; return true; }
                }
                else if (TryFindTile(tile, out int r, out int c))
                {
                    kind = KindBoard; a = r; b = c; return true;
                }
            }
            return false;
        }

        public static bool TryFindTile(TileBehaviour tile, out int row, out int col)
        {
            row = -1; col = -1;
            var b = Board?.Board;
            if (b == null || tile == null) return false;
            for (int r = 0; r < b.GetLength(0); r++)
                for (int c = 0; c < b.GetLength(1); c++)
                    if (ReferenceEquals(b[r, c], tile)) { row = r; col = c; return true; }
            return false;
        }

        public static TileBehaviour TileAt(int r, int c)
        {
            var b = Board?.Board;
            if (b == null) return null;
            if (r < 0 || c < 0 || r >= b.GetLength(0) || c >= b.GetLength(1)) return null;
            return b[r, c];
        }

        public static TileBehaviour Resolve(char kind, int a, int b)
        {
            if (kind == KindBoard) return TileAt(a, b);
            if (kind == KindStock)
            {
                var places = Stock?.Places;
                if (places != null && a >= 0 && a < places.Length) return places[a];
            }
            return null;
        }

        public static BasePieceBehaviour PieceAt(char kind, int a, int b)
        {
            var tile = Resolve(kind, a, b);
            if (tile == null) return null;
            if (tile.Piece != null) return tile.Piece;
            // stock slots can lag; fall back to hierarchy lookup
            return kind == KindStock ? tile.GetComponentInChildren<BasePieceBehaviour>() : null;
        }

        // ---- replication of a remote player's committed action ----

        /// <summary>How the sender's client committed this move - decides which events the replay fires.</summary>
        public const int MoveNormal = 0;      // OnMove + OnHasPlayed: an ordinary turn-ending move
        public const int MovePromoting = 1;   // no OnMove, no OnHasPlayed: turn held for the PROMO choice
        public const int MoveFree = 2;        // no OnMove, no OnHasPlayed: the game did NOT end the turn (Excalibur rhythm-skip)
        public const int MoveEndTileSkip = 3; // OnHasPlayed but no OnMove: end-tile move whose promotion was rhythm-skipped

        /// <summary>Replicates an INGAME move/capture exactly as SelectionManager would.
        /// kind comes from the WIRE, not from tile geometry: only the sender's client knows which
        /// commit path actually ran - Excalibur promotes off non-end tiles, the rhythm skip moves
        /// onto an end tile without promoting, and some gambit combinations commit a move without
        /// ending the turn at all. The replay must fire exactly the events the sender's did.</summary>
        public static bool ApplyInGameMove(BasePieceBehaviour piece, TileBehaviour target, int kind)
        {
            if (piece == null || target == null) return false;
            var prev = piece.CurrentTile;
            var sel = Sel;

            bool captured = false;
            var victim = target.Piece;
            if (victim != null && !ReferenceEquals(victim, piece))
            {
                captured = true;
                victim.IsDead = true;
                SingletonMonoBehaviour<PieceManager>.Instance?.UnregisterPiece(victim);
                sel.OnCapture?.Invoke(piece, victim, target);
                SingletonMonoBehaviour<EnemyManager>.Instance?.EnemyPieces?.Remove(victim);
                victim.VisualEffect?.Disappear(0.25f);
                victim.enabled = false;
                piece.VisualEffect?.CaptureEffect();
                var wave = target.GetWaveBehaviour();
                if (wave != null) SingletonMonoBehaviour<ShockWaveManager>.Instance?.StartWave(wave);
                victim.CaptureEffect();
                UnityEngine.Object.Destroy(victim.gameObject, 0.6f);
                var cdm = SingletonMonoBehaviour<ChessDataManager>.Instance;
                if (cdm != null) cdm.PiecesCaptured++;
            }

            if (prev != null) prev.Piece = null;
            piece.transform.parent = target.PlaceToPutPieces;
            piece.CurrentTile = target;
            target.Piece = piece;

            piece.transform.DOKill();
            piece.transform.DOFollow(target.PlaceToPutPieces, 0.1f);
            piece.MoveAnimation();
            piece.transform.DORotate(Vector3.zero, 0.1f);

            if (target.IsModified()) sel.OnMoveOnModifiedTile?.Invoke(target);

            // The sender's commit only fired OnMove on the ordinary path - the end-tile and
            // Excalibur branches are mutually exclusive with it (SelectionManager.cs:823-881).
            if (kind == MoveNormal)
            {
                sel.OnMove?.Invoke(piece, target);
                piece.OnMove?.Invoke();
            }

            try { target.TilePower?.TriggerPower(target, prev, piece); }
            catch (Exception ex) { Debug.LogWarning($"[Coop] tile power threw: {ex.Message}"); }

            if (kind == MoveEndTileSkip)
            {
                // The sender's client ran SkipPromotion for this commit; its cosmetic gambit
                // listeners (Finish Line and friends) fire off this event.
                SingletonMonoBehaviour<PromotionManager>.Instance?.OnSkipPromotionForRythm?.Invoke();
            }

            if (kind == MoveNormal || kind == MoveEndTileSkip)
                sel.OnHasPlayed?.Invoke();
            // OnPlayerMadeAnActionThatEndsItsTurn fired on the sender for EVERY commit kind
            // (SelectionManager.cs:893) - counter gambits hook it, so the replay must match.
            sel.OnPlayerMadeAnActionThatEndsItsTurn?.Invoke();

            CoopLog.Debug($"applied move -> capture={captured} kind={kind}");
            return true;
        }

        /// <summary>Replicates dropping a stock piece onto the board during INGAME.</summary>
        public static bool ApplyStockDrop(BasePieceBehaviour piece, TileBehaviour target, bool fireTurnEvents)
        {
            if (piece == null || target == null || target.IsStock || target.Piece != null) return false;
            var sel = Sel;
            var prev = piece.CurrentTile;
            if (prev != null) prev.Piece = null;

            if (target.StartingPiece == null) target.StartingPiece = piece;
            else piece.ShouldFindItsOwnStartingPiece = true;

            piece.InStock = false;
            piece.transform.parent = target.PlaceToPutPieces;
            piece.StartingTile = target;
            piece.CurrentTile = target;
            target.Piece = piece;
            SingletonMonoBehaviour<PieceManager>.Instance?.RegisterPiece(piece);

            sel.OnPlacePieceOnBoardInGame?.Invoke(piece, target);
            piece.OnPlaceOnBoardInGame?.Invoke();
            piece.ShinyEffect();

            piece.transform.DOKill();
            piece.transform.DOFollow(target.PlaceToPutPieces, 0.1f);
            piece.transform.DORotate(Vector3.zero, 0.1f);
            var wave = target.GetWaveBehaviour();
            if (wave != null) SingletonMonoBehaviour<ShockWaveManager>.Instance?.StartWave(wave, 0.4f, 0.15f);

            if (fireTurnEvents)
            {
                sel.OnHasPlayed?.Invoke();
                sel.OnPlayerMadeAnActionThatEndsItsTurn?.Invoke();
            }
            CoopLog.Debug("applied stock drop");
            return true;
        }

        /// <summary>Replicates a BOARD_PLACEMENT move (board/stock in any combination, incl. swaps).</summary>
        public static bool ApplyPlacement(BasePieceBehaviour piece, TileBehaviour target)
        {
            if (piece == null || target == null) return false;
            var sel = Sel;
            var prev = piece.CurrentTile;
            if (prev == null) return false;

            var occupant = target.Piece;
            if (occupant == null || ReferenceEquals(occupant, piece))
            {
                prev.Piece = null;
                piece.transform.parent = target.PlaceToPutPieces;
                piece.CurrentTile = target;

                if (piece.InStock && !target.IsStock) sel.OnPutPieceOnBoard?.Invoke(piece);
                else if (!piece.InStock && target.IsStock) sel.OnPutPieceInStock?.Invoke(piece);
                else if (piece.InStock && target.IsStock) sel.OnMovePieceFromStockToStock?.Invoke(piece);
                sel.OnMoveInBoardPlacement?.Invoke(piece);
                piece.InStock = target.IsStock;
            }
            else
            {
                bool wasInStock = piece.InStock;
                occupant.transform.DOKill();
                occupant.transform.DOFollow(prev.PlaceToPutPieces, 0.1f);
                prev.Piece = occupant;
                piece.CurrentTile = target;
                occupant.CurrentTile = prev;

                if (piece.InStock != occupant.InStock)
                {
                    var stockPiece = piece.InStock ? piece : occupant;
                    var boardPiece = piece.InStock ? occupant : piece;
                    sel.OnSwitchPieceInStockNotInStock?.Invoke(stockPiece, boardPiece);
                }
                else if (piece.InStock && occupant.InStock)
                {
                    sel.OnSwitchPiecesInStock?.Invoke(occupant, piece);
                }
                sel.OnMoveInBoardPlacement?.Invoke(piece);

                occupant.transform.parent = prev.PlaceToPutPieces;
                piece.InStock = target.IsStock;
                occupant.InStock = wasInStock;
            }

            piece.transform.DOKill();
            piece.transform.DOFollow(target.PlaceToPutPieces, 0.1f);
            piece.ReleaseAnimation();
            piece.transform.DORotate(Vector3.zero, 0.1f);
            target.Piece = piece;
            CoopLog.Debug("applied placement");
            return true;
        }

        /// <summary>Replays an enemy move chosen by the host via EnemyManager.MovePieceToTile.</summary>
        public static bool ApplyEnemyMove(int fromR, int fromC, int toR, int toC)
        {
            var from = TileAt(fromR, fromC);
            var to = TileAt(toR, toC);
            var piece = from?.Piece;
            if (piece == null || to == null)
            {
                CoopLog.Warn($"enemy replay failed: no piece at {fromR},{fromC} or bad target");
                return false;
            }
            var em = SingletonMonoBehaviour<EnemyManager>.Instance;
            var m = GameRefl.Method(typeof(EnemyManager), "MovePieceToTile",
                typeof(BasePieceBehaviour), typeof(TileBehaviour));
            if (m == null) return false;
            GameRefl.Invoke(em, m, piece, to);
            return true;
        }

        // ---- board digest, for desync detection ----

        public static int Digest()
        {
            unchecked
            {
                int h = 17;
                var b = Board?.Board;
                if (b != null)
                {
                    for (int r = 0; r < b.GetLength(0); r++)
                        for (int c = 0; c < b.GetLength(1); c++)
                        {
                            var p = b[r, c]?.Piece;
                            int code = p == null ? 0 : ((int)p.GetPieceType(true) + 1) * ((p.PieceColor == PieceColor.WHITE) ? 1 : 31);
                            h = h * 31 + code;
                        }
                }
                var places = Stock?.Places;
                if (places != null)
                    foreach (var t in places)
                    {
                        var p = t?.Piece;
                        h = h * 31 + (p == null ? 0 : (int)p.GetPieceType(true) + 1);
                    }
                return h;
            }
        }
    }
}
