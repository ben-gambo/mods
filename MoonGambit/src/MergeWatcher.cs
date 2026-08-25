using System.Collections;
using Blukulele.Audio;
using Blukulele.CHE;
using Blukulele.Core;
using Blukulele.Module.Audio;
using DG.Tweening;
using Gambonanza.GambitApi;
using UnityEngine;

namespace Gambonanza.MoonGambit
{
    /// <summary>
    /// Watches the gambit tray for the merge gesture: the Moon dropped onto the
    /// vanilla Sun, or the Sun dropped onto the Moon. When it happens, both
    /// cards are consumed and an Eclipse appears in the slot they met in.
    ///
    /// How the gesture is recognised, without patching anything:
    /// - SelectionManager fires OnGambitSelection when a card is picked up, and
    ///   exposes the dragged transform (CurrentGambit) and the slot it came
    ///   from (PreviousGambitPlace). We note both and start watching.
    /// - Vanilla's drop handler runs entirely in the PointerUp frame. Dropping
    ///   a card onto an OCCUPIED slot swaps the two cards between slots, so
    ///   "Moon dropped onto Sun" leaves exactly one signature behind: the
    ///   dragged card sits in a NEW slot, and the slot it came from now holds
    ///   the counterpart. A drop on an empty slot leaves the source empty, a
    ///   snap-back or same-slot drop leaves the dragged card at the source -
    ///   neither can be confused with the swap.
    /// - So when CurrentGambit goes back to null we look at where the two cards
    ///   ended up, and if the signature matches, we merge.
    ///
    /// A reorder-swap of Moon and Sun IS the merge gesture, on purpose: if you
    /// bring the two celestial bodies into alignment, you get an eclipse.
    /// That's the discovery.
    /// </summary>
    public sealed class MergeWatcher : MonoBehaviour
    {
        public const string MoonId = "moon";
        public const string SunId = "sun";       // vanilla ID (Scheme_SolCesto checks the same string)
        public const string EclipseId = "eclipse";

        private SelectionManager m_Subscribed;   // instance we are currently hooked to
        private Transform m_Dragged;
        private GambitPlaceBehaviour m_SourceSlot;
        private bool m_WatchingDrop;
        private bool m_Merging;

        private void Update()
        {
            // SelectionManager is scene-owned and this object is not; re-hook
            // whenever a new instance appears (new run, back to menu, ...).
            var sel = SingletonMonoBehaviour<SelectionManager>.IsCreated()
                ? SingletonMonoBehaviour<SelectionManager>.Instance : null;
            if (sel != m_Subscribed)
            {
                Unsubscribe();
                if (sel != null)
                {
                    sel.OnGambitSelection = (System.Action)System.Delegate.Combine(
                        sel.OnGambitSelection, new System.Action(OnGambitPickedUp));
                    m_Subscribed = sel;
                }
                m_WatchingDrop = false;
            }

            if (!m_WatchingDrop || m_Merging || sel == null) return;

            // Still mid-drag. The drop handler nulls CurrentGambit in the
            // PointerUp frame, after it has already reassigned every slot - so
            // once we see null here, the tray is in its settled state.
            if (sel.CurrentGambit != null) return;
            m_WatchingDrop = false;
            EvaluateDrop();
        }

        private void OnDestroy() => Unsubscribe();

        private void Unsubscribe()
        {
            if (m_Subscribed == null) { m_Subscribed = null; return; }
            m_Subscribed.OnGambitSelection = (System.Action)System.Delegate.Remove(
                m_Subscribed.OnGambitSelection, new System.Action(OnGambitPickedUp));
            m_Subscribed = null;
        }

        private void OnGambitPickedUp()
        {
            if (m_Merging || m_Subscribed == null) return;
            m_Dragged = m_Subscribed.CurrentGambit;
            m_SourceSlot = m_Subscribed.PreviousGambitPlace;
            m_WatchingDrop = m_Dragged != null && m_SourceSlot != null;
        }

        private void EvaluateDrop()
        {
            if (m_Dragged == null || m_SourceSlot == null) return;
            var dragged = m_Dragged.GetComponent<GambitBehaviour>();
            m_Dragged = null;
            if (dragged == null || dragged.Info == null) return;

            string draggedId = dragged.Info.ID;
            string wantedInSource;
            if (draggedId == MoonId) wantedInSource = SunId;
            else if (draggedId == SunId) wantedInSource = MoonId;
            else return;

            // The swap signature: dragged card now lives in a different slot,
            // and its old slot holds the counterpart it displaced.
            var targetSlot = FindSlotHolding(dragged);
            if (targetSlot == null || targetSlot == m_SourceSlot) return;

            var displaced = m_SourceSlot.CurrentGambit;
            if (displaced == null || displaced.Info == null || displaced.Info.ID != wantedInSource) return;

            StartCoroutine(CO_Merge(dragged, displaced, targetSlot, m_SourceSlot));
        }

        private static GambitPlaceBehaviour FindSlotHolding(GambitBehaviour gambit)
        {
#pragma warning disable CS0618
            var slots = Object.FindObjectsOfType<GambitPlaceBehaviour>();
#pragma warning restore CS0618
            foreach (var slot in slots)
                if (slot.CurrentGambit == gambit)
                    return slot;
            return null;
        }

        /// <summary>
        /// The ceremony: both cards drift to the slot they met in, shrink into
        /// alignment, and the Eclipse pops out of the overlap - using the same
        /// scale-in the shop uses so it reads as "you received a gambit".
        /// First time through, the game's own unlock notification announces the
        /// new card; after that UnlockGambit is a no-op.
        /// </summary>
        private IEnumerator CO_Merge(
            GambitBehaviour a, GambitBehaviour b,
            GambitPlaceBehaviour targetSlot, GambitPlaceBehaviour sourceSlot)
        {
            m_Merging = true;

            // Nobody gets to grab a card that is busy transcending.
            SetGrabbable(a, false);
            SetGrabbable(b, false);

            // Let the vanilla 0.1s drop tween settle before taking over.
            yield return new WaitForSeconds(0.12f);

            Vector3 meetPoint = targetSlot.GambitParent.position;
            foreach (var card in new[] { a, b })
            {
                if (card == null) continue;
                card.transform.DOKill();
                card.transform.DOMove(meetPoint, 0.22f).SetEase(Ease.InBack);
                card.transform.DOScale(Vector3.zero, 0.26f).SetEase(Ease.InBack);
                card.transform.DORotate(new Vector3(0f, 0f, 180f), 0.26f, RotateMode.FastBeyond360);
            }
            AudioManager.Play(AudioEvents.TakePiece, loop: false, Random.Range(0.6f, 0.7f));
            yield return new WaitForSeconds(0.28f);

            // Consume both. Slots first so nothing observes a slot pointing at
            // a destroyed card, then the objects themselves.
            targetSlot.CurrentGambit = null;
            sourceSlot.CurrentGambit = null;
            if (a != null) { a.transform.DOKill(); Object.Destroy(a.gameObject); }
            if (b != null) { b.transform.DOKill(); Object.Destroy(b.gameObject); }

            var library = SingletonMonoBehaviour<GambitLibrary>.Instance;
            var eclipseInfo = library != null ? library.GetGambitPerId(EclipseId) : null;
            if (eclipseInfo == null)
            {
                Debug.LogError("[MoonGambit] merge fired but 'eclipse' is not in the library - cards were consumed with nothing to give back!");
                m_Merging = false;
                yield break;
            }

            // If the player managed to drop some other card into the meeting
            // slot during the ceremony, cede it and take a free slot instead.
            if (targetSlot.CurrentGambit != null)
            {
                var manager = SingletonMonoBehaviour<GambitManager>.Instance;
                if (manager != null && !manager.IsFull())
                    targetSlot = manager.GetGambitPlace();
            }

            // Same instantiation the shop and the Dragon Egg use, into the slot
            // where the two cards met.
            var prefab = library.Gambits[library.GambitsInfo.IndexOf(eclipseInfo)];
            var eclipse = Object.Instantiate(prefab, meetPoint, Quaternion.identity, targetSlot.GambitParent);
            targetSlot.CurrentGambit = eclipse;
            eclipse.transform.localScale = Vector3.zero;
            eclipse.transform.DOScale(Vector3.one, 0.35f).SetEase(Ease.OutBack);
            AudioManager.Play(AudioEvents.PhantomTile);

            // Mirror the buy path's build-influence bookkeeping for the card
            // the player just gained. (Vanilla never subtracts influence for
            // consumed cards, so the Sun's and Moon's stays - same as a sell.)
            var balance = SingletonMonoBehaviour<BuildBalanceManager>.Instance;
            if (balance != null && eclipseInfo.Focus != null)
                foreach (var focus in eclipseInfo.Focus)
                    balance.IncreaseGambitInfluence(focus);

            SingletonMonoBehaviour<GambitManager>.Instance?.OnGetGambit?.Invoke();

            // The discovery IS the unlock.
            ModGambitApi.Unlock(EclipseId);

            Debug.Log("[MoonGambit] the Moon crossed the Sun: merged into the Eclipse's Gambit.");
            m_Merging = false;
        }

        private static void SetGrabbable(GambitBehaviour card, bool grabbable)
        {
            if (card == null) return;
            var collider = card.GetComponent<Collider2D>();
            if (collider != null) collider.enabled = grabbable;
        }
    }
}
