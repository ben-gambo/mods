using System;
using Blukulele.Audio;
using Blukulele.CHE;
using Blukulele.Core;
using Blukulele.Module.Audio;
using UnityEngine;

namespace Gambonanza.MoonGambit
{
    /// <summary>
    /// Runtime behaviour of the Eclipse Gambit. Two halves:
    ///
    /// 1. It still contains the Sun. Every time a KING is earned, a random
    ///    blank tile turns golden - a line-for-line port of Gambit_Sun's tile
    ///    selection (same bounds, same skip rules, same Anarchist interaction),
    ///    on its own RNG occurrence keys so it doesn't disturb a real Sun's
    ///    seeded rolls if both exist.
    ///
    /// 2. The Moon crosses in front. While this card is owned, every golden
    ///    tile on the board behaves like EVERY tile: an <see cref="EclipseTilePower"/>
    ///    replaces the tile's GoldTilePower in the dispatch slot (TileBehaviour.
    ///    TilePower is a public field, and SelectionManager only ever calls
    ///    through it), and enemy pieces landing on those tiles get trapped,
    ///    hunter-style - handled here because hunter traps hang off
    ///    EnemyManager's global move/capture events rather than the landing
    ///    dispatch.
    ///
    /// The golden tiles keep their vanilla flags (IsGolden stays true, IsHunter
    /// stays false), so save/restore, the Sun's own reroll-skip, and the
    /// exhaust-strain visuals all see an ordinary golden tile. The trap is
    /// invisible to the enemy too. It's an eclipse; things hide in it.
    ///
    /// This component lives exactly as long as the card is owned, so selling
    /// the Eclipse reverts every tile the moment the card leaves.
    /// </summary>
    public sealed class GambitEclipse : BaseGambit
    {
        /// <summary>How many Eclipse cards are alive. Tiles stay wrapped while any is.</summary>
        internal static int AliveCount;

        // Trap dedup when several Eclipse cards are owned: every instance hears
        // the same EnemyManager event, but only one gets to spring the trap.
        private static int s_LastTrapFrame = -1;
        private static BasePieceBehaviour s_LastTrapPiece;

        private bool m_Subscribed;

        private void Start()
        {
            AliveCount++;
            Subscribe();
            WrapGoldenTiles();
        }

        private void OnEnable() => Subscribe();

        private void OnDestroy()
        {
            Unsubscribe();
            AliveCount = Mathf.Max(0, AliveCount - 1);
            if (AliveCount == 0)
                UnwrapAllTiles();
        }

        private void Subscribe()
        {
            if (m_Subscribed) return;
            if (!SingletonMonoBehaviour<StockManager>.IsCreated()) return;
            if (!SingletonMonoBehaviour<TileManager>.IsCreated()) return;
            if (!SingletonMonoBehaviour<EnemyManager>.IsCreated()) return;

            var stock = SingletonMonoBehaviour<StockManager>.Instance;
            stock.OnEarnPiece = (Action<PieceType>)Delegate.Combine(
                stock.OnEarnPiece, new Action<PieceType>(OnEarnPiece));

            // Fires after any TurnToX, once the tile's TilePower is already
            // assigned - the exact moment a fresh golden tile can be wrapped.
            var tiles = SingletonMonoBehaviour<TileManager>.Instance;
            tiles.OnModifyTile = (Action)Delegate.Combine(
                tiles.OnModifyTile, new Action(WrapGoldenTiles));

            var enemies = SingletonMonoBehaviour<EnemyManager>.Instance;
            enemies.OnMove = (Action<BasePieceBehaviour, TileBehaviour>)Delegate.Combine(
                enemies.OnMove, new Action<BasePieceBehaviour, TileBehaviour>(OnEnemyArrives));
            enemies.OnCapture = (Action<BasePieceBehaviour, BasePieceBehaviour, TileBehaviour>)Delegate.Combine(
                enemies.OnCapture, new Action<BasePieceBehaviour, BasePieceBehaviour, TileBehaviour>(OnEnemyCaptures));

            if (SingletonMonoBehaviour<GameManager>.IsCreated())
            {
                var game = SingletonMonoBehaviour<GameManager>.Instance;
                game.onStateChanged = (Action<State>)Delegate.Combine(
                    game.onStateChanged, new Action<State>(OnStateChanged));
            }

            m_Subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!m_Subscribed) return;
            m_Subscribed = false;

            if (SingletonMonoBehaviour<StockManager>.IsCreated())
            {
                var stock = SingletonMonoBehaviour<StockManager>.Instance;
                if (stock != null)
                    stock.OnEarnPiece = (Action<PieceType>)Delegate.Remove(
                        stock.OnEarnPiece, new Action<PieceType>(OnEarnPiece));
            }
            if (SingletonMonoBehaviour<TileManager>.IsCreated())
            {
                var tiles = SingletonMonoBehaviour<TileManager>.Instance;
                if (tiles != null)
                    tiles.OnModifyTile = (Action)Delegate.Remove(
                        tiles.OnModifyTile, new Action(WrapGoldenTiles));
            }
            if (SingletonMonoBehaviour<EnemyManager>.IsCreated())
            {
                var enemies = SingletonMonoBehaviour<EnemyManager>.Instance;
                if (enemies != null)
                {
                    enemies.OnMove = (Action<BasePieceBehaviour, TileBehaviour>)Delegate.Remove(
                        enemies.OnMove, new Action<BasePieceBehaviour, TileBehaviour>(OnEnemyArrives));
                    enemies.OnCapture = (Action<BasePieceBehaviour, BasePieceBehaviour, TileBehaviour>)Delegate.Remove(
                        enemies.OnCapture, new Action<BasePieceBehaviour, BasePieceBehaviour, TileBehaviour>(OnEnemyCaptures));
                }
            }
            if (SingletonMonoBehaviour<GameManager>.IsCreated())
            {
                var game = SingletonMonoBehaviour<GameManager>.Instance;
                if (game != null)
                    game.onStateChanged = (Action<State>)Delegate.Remove(
                        game.onStateChanged, new Action<State>(OnStateChanged));
            }
        }

        // --- half one: the Sun lives on ---------------------------------------

        private void OnEarnPiece(PieceType pieceType)
        {
            if (pieceType == PieceType.KING || SingletonMonoBehaviour<GambitManager>.Instance.AnarchistEnable)
            {
                StartCoroutine(CO_Trigger(0.3f));
            }
        }

        public override void Trigger()
        {
            SelectTileToGild();
        }

        /// <summary>
        /// Gambit_Sun.SelectTilesToModify, ported verbatim: same reachable-board
        /// bounds derived from the wave, same rerolls past fallen, animating,
        /// already-golden or inactive tiles, capped at 100 attempts.
        /// </summary>
        private void SelectTileToGild()
        {
            var chess = SingletonMonoBehaviour<ChessDataManager>.Instance;
            TileBehaviour[,] board = SingletonMonoBehaviour<BoardManager>.Instance.Board;
            int waveOffset = 0;
            State state = SingletonMonoBehaviour<GameManager>.Instance.CurrentState;
            if (state == State.SHOP || state == State.WHEEL_GAME || state == State.PACHINKO
                || state == State.GACHAPON || state == State.TILE_PLACEMENT)
            {
                waveOffset = -1;
            }
            int maxX = Mathf.Min(8, (chess.CurrentWave - waveOffset) / 5 + 5);
            int maxY = 5;
            for (int attempt = 0; attempt < 100; attempt++)
            {
                int x = chess.GetRandomOccurrence("ECLIPSE_X", 0, maxX);
                int y = chess.GetRandomOccurrence("ECLIPSE_Y", 0, maxY);
                TileBehaviour tile = board[y, x];
                if (tile.HasFell) continue;
                if (tile.TileVisual.IsModifying) continue;
                if (tile.IsModified() && tile.IsGolden) continue;
                if (!tile.transform.gameObject.activeInHierarchy) continue;

                tile.TurnToGold(showAnimation: false);
                tile.TileVisual.SunEffect();
                VisualEffect();
                break;
            }
        }

        // --- half two: golden tiles become everything -------------------------

        private void OnStateChanged(State state)
        {
            // A fresh board (new game) or restored tiles (loaded run) both pass
            // through BOARD_PLACEMENT; a scan there catches golden tiles that
            // appeared without an OnModifyTile, e.g. from a save.
            if (state == State.BOARD_PLACEMENT)
                WrapGoldenTiles();
        }

        private void WrapGoldenTiles()
        {
            if (!SingletonMonoBehaviour<BoardManager>.IsCreated()) return;
            var board = SingletonMonoBehaviour<BoardManager>.Instance.Board;
            if (board == null) return;

            foreach (var tile in board)
            {
                if (tile == null) continue;
                var eclipsed = tile.GetComponent<EclipseTilePower>();
                if (tile.IsGolden)
                {
                    if (eclipsed == null)
                        eclipsed = tile.gameObject.AddComponent<EclipseTilePower>();
                    // TurnToGold assigns a fresh GoldTilePower into the dispatch
                    // field, so re-point it at us every time (a tile can cycle
                    // gold -> shield -> gold and reuse our leftover component).
                    if (!ReferenceEquals(tile.TilePower, eclipsed))
                        tile.TilePower = eclipsed;
                }
                else if (eclipsed != null)
                {
                    // Tile stopped being golden: vanilla already swapped its own
                    // power component in, ours is just litter now.
                    UnityEngine.Object.Destroy(eclipsed);
                }
            }
        }

        private void UnwrapAllTiles()
        {
            if (!SingletonMonoBehaviour<BoardManager>.IsCreated()) return;
            var board = SingletonMonoBehaviour<BoardManager>.Instance.Board;
            if (board == null) return;

            foreach (var tile in board)
            {
                if (tile == null) continue;
                var eclipsed = tile.GetComponent<EclipseTilePower>();
                if (eclipsed == null) continue;
                if (ReferenceEquals(tile.TilePower, eclipsed))
                {
                    // Hand the dispatch back to the tile's own GoldTilePower
                    // (still on the GameObject - we never removed it).
                    var gold = tile.GetComponent<GoldTilePower>();
                    tile.TilePower = gold != null ? (ITilePower)gold : tile.gameObject.AddComponent<DefaultTilePower>();
                }
                UnityEngine.Object.Destroy(eclipsed);
            }
        }

        // --- the hidden trap --------------------------------------------------

        private void OnEnemyCaptures(BasePieceBehaviour piece, BasePieceBehaviour _, TileBehaviour tile)
            => OnEnemyArrives(piece, tile);

        /// <summary>
        /// HunterTilePower's trap, sprung from the same EnemyManager events, but
        /// on eclipsed golden tiles - and without IsHunter ever being set, so
        /// nothing warns the enemy the gold is a trap.
        /// </summary>
        private void OnEnemyArrives(BasePieceBehaviour piece, TileBehaviour tile)
        {
            if (piece == null || tile == null || !tile.IsGolden) return;
            if (tile.GetComponent<EclipseTilePower>() == null) return;
            if (SingletonMonoBehaviour<StrainManager>.Instance.ActivatedStrain[Strain.TILE_EXHAUST] && tile.PowerUsed) return;

            if (s_LastTrapFrame == Time.frameCount && s_LastTrapPiece == piece) return;
            s_LastTrapFrame = Time.frameCount;
            s_LastTrapPiece = piece;

            tile.PowerUsed = true;
            tile.TileVisual.HunterEffect();
            AudioManager.Play(AudioEvents.TrapTile, loop: false, null, null, 0f, 0.3f);
            piece.Modifier.Trap();
            SingletonMonoBehaviour<TileManager>.Instance.OnHunterTileUsed?.Invoke(piece, tile);
        }
    }
}
