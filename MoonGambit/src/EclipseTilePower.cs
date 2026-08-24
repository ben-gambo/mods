using System.Collections;
using Blukulele.Audio;
using Blukulele.CHE;
using Blukulele.Core;
using Blukulele.Module.Audio;
using UnityEngine;

namespace Gambonanza.MoonGambit
{
    /// <summary>
    /// The landing power of an eclipsed golden tile: gold, benediction, shield
    /// and phantom, all at once. Each half is a faithful copy of the matching
    /// vanilla ITilePower (GoldTilePower, BenedictionTilePower, ShieldTilePower,
    /// PhantomTilePower), fused into one dispatch so the tile still LOOKS like
    /// a plain golden tile to everything that inspects it by component or flag.
    /// The fifth face - the hunter trap for enemy pieces - lives in
    /// <see cref="GambitEclipse"/>, because hunter tiles work off EnemyManager's
    /// events rather than the landing dispatch.
    ///
    /// (The cursed tile is deliberately not invited. "EVERY tile" is a promise,
    /// not a threat.)
    ///
    /// Ordering: every piece modifier is applied synchronously, before the
    /// enemy takes its turn - only the feedback is staggered, one effect every
    /// 0.15s, so four powers read as a cascade instead of a pile-up. The
    /// phantom copy lands last at 0.45s, close kin to vanilla's own 0.2s delay.
    ///
    /// Under the tile-exhaust strain the whole cascade is a single use: one
    /// landing spends the tile, same as any vanilla power.
    /// </summary>
    public sealed class EclipseTilePower : MonoBehaviour, ITilePower
    {
        public void TriggerPower(TileBehaviour currentTile, TileBehaviour previousTile, BasePieceBehaviour currentPiece)
        {
            if (SingletonMonoBehaviour<StrainManager>.Instance.ActivatedStrain[Strain.TILE_EXHAUST] && currentTile.PowerUsed)
                return;
            if (currentPiece == null)
            {
                Debug.LogError("[MoonGambit] EclipseTilePower triggered with no piece");
                return;
            }
            currentTile.PowerUsed = true;

            // Gameplay first, all in this frame.
            currentPiece.Modifier.TurnToGold();
            currentPiece.VisualEffect.TurnToGold();
            currentPiece.Modifier.Benediction();
            currentPiece.Modifier.Protect();

            var tiles = SingletonMonoBehaviour<TileManager>.Instance;
            tiles.OnGoldenTileUsed?.Invoke(currentPiece, currentTile);
            tiles.OnBenedictionTileUsed?.Invoke(currentPiece, currentTile);
            tiles.OnProtectiveTileUsed?.Invoke(currentPiece, currentTile);
            tiles.OnPhantomTileUsed?.Invoke(currentPiece, currentTile);
            DataManager.Instance.Data.GoldenTileUsed = true;
            DataManager.Instance.Data.BlessedTileUsed = true;
            DataManager.Instance.Data.ProtectiveTileUsed = true;
            DataManager.Instance.Data.PhantomTileUsed = true;

            // Feedback cascade + the phantom copy. Capture type and position
            // now, like vanilla does - the piece may be gone by 0.45s.
            StartCoroutine(CO_Cascade(
                currentTile, currentPiece,
                currentPiece.GetPieceType(),
                currentTile.PlaceToPutPieces.position));
        }

        private IEnumerator CO_Cascade(TileBehaviour tile, BasePieceBehaviour piece, PieceType type, Vector3 position)
        {
            AudioManager.Play(AudioEvents.Shield_Activation);
            tile.TileVisual.GoldEffect();
            yield return new WaitForSeconds(0.15f);

            AudioManager.Play(AudioEvents.Benediction_Activation);
            tile.TileVisual.BenedictionEffect();
            yield return new WaitForSeconds(0.15f);

            AudioManager.Play(AudioEvents.Shield_Activation);
            tile.TileVisual.ShieldEffect();
            yield return new WaitForSeconds(0.15f);

            AudioManager.Play(AudioEvents.PhantomTile);
            tile.TileVisual.PhantomEffect();

            // PhantomTilePower's payout, resurrection-stone interplay included.
            bool isPhantom = true;
            var gambits = SingletonMonoBehaviour<GambitManager>.Instance;
            if (gambits.ResurrectionStone)
            {
                if (SingletonMonoBehaviour<ChanceManager>.Instance.ComputeChance(1f, 4f, "RESURRECTION_STONE_CHANCE"))
                {
                    isPhantom = false;
                    gambits.OnResurrectionStoneUsed?.Invoke();
                    if (piece != null)
                        piece.VisualEffect.ResurrectionStoneEffect();
                }
                else
                {
                    gambits.OnResurrectionStoneUsedNoLuck?.Invoke();
                }
            }
            var stock = SingletonMonoBehaviour<StockManager>.Instance;
            if (stock.RoomAvailable())
            {
                stock.AddPiece(type, position, isGolden: false, backFromPat: false, isPhantom);
            }
        }
    }
}
