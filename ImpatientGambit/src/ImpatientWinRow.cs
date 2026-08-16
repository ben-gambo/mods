using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Blukulele.CHE;
using Blukulele.Core;
using Blukulele.TextHelpers;
using TMPro;
using UnityEngine;

namespace Gambonanza.ImpatientGambit
{
    /// <summary>
    /// Adds a fourth row to the win screen's cash breakdown - "IMPATIENT x4" and
    /// the bonus it is about to pay - and folds that bonus into the payout the
    /// COLLECT button actually hands over.
    ///
    /// Why this exists rather than just topping the coins up afterwards: vanilla
    /// only ever refreshes the coin counter from MoneyAnimationManager, as the
    /// flying coins land. Coins added outside that path are real but invisible
    /// until something else redraws the counter - which is why the extra gold
    /// appeared to show up "only after buying something". Editing the win
    /// screen's own total instead means the coins are paid, animated and
    /// displayed entirely by vanilla's normal route.
    ///
    /// Everything here is best-effort: any field this cannot find leaves the row
    /// out and hands the win payout back to <see cref="GambitImpatient"/>'s
    /// general-purpose multiplier, so the player is never short-changed.
    /// </summary>
    public sealed class ImpatientWinRow : MonoBehaviour
    {
        private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;

        // The three vanilla rows are laid out at a fixed vertical step. We clone
        // the third one and drop it one more step down, so the spacing is
        // whatever the game already uses rather than a number we made up.
        private static readonly string[] RowThreeFields =
        {
            "m_Text_PiecesBonus",
            "m_Text_PiecesBonus_Explanation",
            "m_Text_PiecesBonus_Dollars",
            "m_Text_PiecesBonus_DollarsCountingText",
        };

        private WinCanvas _canvas;
        private readonly List<GameObject> _clones = new List<GameObject>();
        private TMP_Text _label;      // the "x4" column
        private TMP_Text _caption;    // the explanation column
        private CountingText _amount; // the animated figure
        private CanvasRendererFade _fade;

        private void Awake()
        {
            _canvas = GetComponent<WinCanvas>();
            if (_canvas == null) Destroy(this);
        }

        private void OnEnable()
        {
            if (_canvas == null) return;
            StartCoroutine(CO_ApplyAfterVanillaLaysOut());
        }

        private void OnDisable()
        {
            HideRow();
        }

        private void OnDestroy()
        {
            foreach (var clone in _clones)
                if (clone != null) Destroy(clone);
            _clones.Clear();
        }

        /// <summary>
        /// WinCanvas.OnEnable schedules its own Apparition() on a delay, and that
        /// is where m_ValueEarned and every row's text get filled in. Mirror the
        /// same delay and add a hair, so we are reading a laid-out screen rather
        /// than last win's leftovers.
        /// </summary>
        private IEnumerator CO_ApplyAfterVanillaLaysOut()
        {
            float delay = 2f;
            try
            {
                if (SingletonMonoBehaviour<CrumbleManager>.IsCreated()
                    && SingletonMonoBehaviour<CrumbleManager>.Instance.CrumbleMode
                    && SingletonMonoBehaviour<FlowManager>.IsCreated())
                {
                    delay += SingletonMonoBehaviour<FlowManager>.Instance.UIApparitionDelay;
                }
            }
            catch { }

            yield return new WaitForSeconds(delay + 0.05f);

            try { Apply(); }
            catch (Exception ex) { Debug.Log("[ImpatientGambit] win-screen row skipped: " + ex.Message); }
        }

        private void Apply()
        {
            var earnedField = typeof(WinCanvas).GetField("m_ValueEarned", Hidden);
            if (earnedField == null) return;

            int baseTotal = (int)earnedField.GetValue(_canvas);
            if (baseTotal <= 0) return;

            int bonus = baseTotal * (GambitImpatient.IncomeMultiplier - 1);
            int total = baseTotal + bonus;

            if (!EnsureRow()) return;

            earnedField.SetValue(_canvas, total);
            RewriteButtonText(total);

            // Tell the general-purpose multiplier to keep its hands off this one
            // exact payout - it is already multiplied.
            GambitImpatient.PendingWinPayout = total;

            _label.text = "x" + GambitImpatient.IncomeMultiplier;
            _caption.text = "Impatient";
            _amount.SetValue(0.0);

            // Vanilla cascades its three rows in at 1.15s / 1.4s / 1.65s after
            // Apparition, counting each figure up as it appears. Ours is the
            // fourth, so it lands one beat later and counts up with itself.
            _fade.FadeIn(delay: 1.85f, duration: 0.2f,
                onStart: () => _amount.CountFromTo(0, bonus, 0.2f));
        }

        private void RewriteButtonText(int total)
        {
            try
            {
                var text = SingletonMonoBehaviour<LocalizationManager>.Instance
                    .GetTraduction()["result"]["button-text"];
                var rendered = ((string)text).Replace("&", total.ToString());
                SetTextField("m_ButtonText", rendered);
                SetTextField("m_ButtonTextGraveyard", rendered);
            }
            catch { }
        }

        private void SetTextField(string field, string value)
        {
            var label = typeof(WinCanvas).GetField(field, Hidden)?.GetValue(_canvas) as TMP_Text;
            if (label != null) label.text = value;
        }

        /// <summary>
        /// Clones the third breakdown row into a fourth, one step further down.
        /// The step is measured between rows two and three rather than hardcoded,
        /// so it stays correct if the panel is ever re-laid-out.
        /// </summary>
        private bool EnsureRow()
        {
            if (_label != null && _caption != null && _amount != null) return true;

            var anchorTwo = Field<TMP_Text>("m_Text_PiecesCaptured");
            var anchorThree = Field<TMP_Text>("m_Text_PiecesBonus");
            if (anchorTwo == null || anchorThree == null) return false;

            Vector3 step = anchorThree.transform.localPosition - anchorTwo.transform.localPosition;
            if (step.sqrMagnitude <= 0.0001f) return false;

            // One source object can carry several of the four roles (the counting
            // text lives on the dollars label), so clone per GameObject and read
            // the components back off the clone.
            var cloneBySource = new Dictionary<GameObject, GameObject>();
            foreach (var name in RowThreeFields)
            {
                var source = FieldObject(name);
                if (source == null) return false;
                if (cloneBySource.ContainsKey(source)) continue;

                var clone = Instantiate(source, source.transform.parent);
                clone.name = "ImpatientGambit_" + source.name;
                clone.transform.localPosition = source.transform.localPosition + step;
                clone.transform.localScale = source.transform.localScale;
                cloneBySource[source] = clone;
                _clones.Add(clone);
            }

            _label = ComponentOn<TMP_Text>(cloneBySource, "m_Text_PiecesBonus");
            _caption = ComponentOn<TMP_Text>(cloneBySource, "m_Text_PiecesBonus_Explanation");
            _amount = ComponentOn<CountingText>(cloneBySource, "m_Text_PiecesBonus_DollarsCountingText");
            if (_label == null || _caption == null || _amount == null) return false;

            var dollars = ComponentOn<TMP_Text>(cloneBySource, "m_Text_PiecesBonus_Dollars");
            _fade = new CanvasRendererFade(this, _label, _caption, dollars);
            HideRow();
            return true;
        }

        private void HideRow()
        {
            _fade?.SetAlpha(0f);
        }

        private T Field<T>(string name) where T : Component
            => typeof(WinCanvas).GetField(name, Hidden)?.GetValue(_canvas) as T;

        private GameObject FieldObject(string name)
        {
            var value = typeof(WinCanvas).GetField(name, Hidden)?.GetValue(_canvas) as Component;
            return value == null ? null : value.gameObject;
        }

        private T ComponentOn<T>(Dictionary<GameObject, GameObject> clones, string sourceField) where T : Component
        {
            var source = FieldObject(sourceField);
            if (source == null || !clones.TryGetValue(source, out var clone)) return null;
            return clone.GetComponent<T>();
        }

        /// <summary>
        /// Fades a handful of TMP labels together. Hand-rolled rather than using
        /// DOTween so the mod does not have to carry that reference just for one
        /// four-line animation.
        /// </summary>
        private sealed class CanvasRendererFade
        {
            private readonly MonoBehaviour _host;
            private readonly List<TMP_Text> _labels = new List<TMP_Text>();

            public CanvasRendererFade(MonoBehaviour host, params TMP_Text[] labels)
            {
                _host = host;
                foreach (var label in labels)
                    if (label != null) _labels.Add(label);
            }

            public void SetAlpha(float alpha)
            {
                foreach (var label in _labels)
                {
                    if (label == null) continue;
                    var c = label.color;
                    c.a = alpha;
                    label.color = c;
                }
            }

            public void FadeIn(float delay, float duration, Action onStart = null)
            {
                _host.StartCoroutine(CO_Fade(delay, duration, onStart));
            }

            private IEnumerator CO_Fade(float delay, float duration, Action onStart)
            {
                SetAlpha(0f);
                yield return new WaitForSeconds(delay);
                onStart?.Invoke();
                for (float t = 0f; t < duration; t += Time.deltaTime)
                {
                    SetAlpha(Mathf.Clamp01(t / duration));
                    yield return null;
                }
                SetAlpha(1f);
            }
        }
    }
}
