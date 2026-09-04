using System.Collections;
using Blukulele.CHE;
using Gambonanza.CrumbleApi;
using UnityEngine;

namespace Gambonanza.BedrockGambit
{
    /// <summary>
    /// Runtime behaviour of Bedrock's Gambit: the board never crumbles while the
    /// card is held.
    ///
    /// The whole effect is one <see cref="Crumble.Freeze"/> handle from the Crumble
    /// Control API, taken in Start and released in OnDestroy - which is exactly the
    /// lifetime of "the player owns this card", so buying it arms the freeze and
    /// selling it disarms it with no bookkeeping. The handle is also tied to this
    /// component as its owner, so if the object is destroyed without OnDestroy ever
    /// running (a run torn down mid-frame, say) the API releases it on its own.
    ///
    /// While frozen: the crumble countdown does not tick, tiles that are already
    /// shaking never fall, and the crumble picks no new tiles. Everything resumes
    /// where it left off the turn after the card goes. Tiles the Mask boss or a
    /// Crumbler enemy shake outside the per-turn step still queue up while frozen
    /// and fall together once the freeze ends - the card holds the floor, it does
    /// not undo what those enemies do.
    ///
    /// The only other thing here is the card's flash: it lights up on a turn where
    /// the board would otherwise have crumbled (crumble mode on, or tiles shaking),
    /// not on every quiet countdown tick, so the player sees the save when there is
    /// one to see.
    /// </summary>
    public class GambitBedrock : BaseGambit
    {
        private CrumbleHandle _freeze;
        private bool _pulseQueued;

        private void Start()
        {
            _freeze = Crumble.Freeze(this, "Bedrock's Gambit");
            Crumble.OnBeforeStep += OnCrumbleStep;
        }

        private void OnDestroy()
        {
            Crumble.OnBeforeStep -= OnCrumbleStep;
            _freeze?.Dispose();
            _freeze = null;
        }

        // Fires just before the game's per-turn crumble step, before the API applies
        // the freeze. The freeze is what stops the step; this only decides whether
        // the card should visibly react.
        private void OnCrumbleStep()
        {
            if (!Crumble.IsBound || _pulseQueued) return;
            if (!Crumble.IsActive && Crumble.ShakingTiles.Count == 0) return;
            _pulseQueued = true;
            StartCoroutine(CO_Pulse(0.15f));
        }

        private IEnumerator CO_Pulse(float delay)
        {
            yield return new WaitForSeconds(delay);
            _pulseQueued = false;
            Trigger();
        }

        public override void Trigger()
        {
            VisualEffect();
        }
    }
}
