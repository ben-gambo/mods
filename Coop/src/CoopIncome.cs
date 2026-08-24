using System;
using System.Collections;
using System.Reflection;
using Blukulele.CHE;
using Blukulele.Core;
using TMPro;
using UnityEngine;

namespace Gambonanza.Coop
{
    /// <summary>
    /// Halves post-battle income only (base reward + capture bonus + interest), leaving
    /// in-battle gambit income untouched. The kept share rounds UP - each client banks
    /// ceil(earned/2), so two co-op players together earn one solo player's income plus
    /// at most a coin, never less.
    ///
    /// WinCanvas computes m_ValueEarned, then on the collect button does
    ///   IncreaseCoin(m_ValueEarned) ; SpawnMoney(...) ; OnGetMoneyButtonClicked.Invoke()
    /// That event fires from nowhere else, so we compensate on it: subtract the rounding-up
    /// half straight from m_Coins. Writing the field (rather than DecreaseCoin) avoids firing
    /// OnCoinDecreased and the count-down animation; the flying-coin ticker stops at the new
    /// value on its own because it only counts up while text < m_Coins.
    /// </summary>
    internal sealed class CoopIncome
    {
        private Action _handler;
        private bool _installed;

        public bool Enabled { get; set; }

        public void Install()
        {
            if (_installed) return;
            var im = SingletonMonoBehaviour<InterfaceManager>.Instance;
            if (im == null) return;
            _handler = OnMoneyCollected;
            im.OnGetMoneyButtonClicked = (Action)Delegate.Combine(im.OnGetMoneyButtonClicked, _handler);
            _installed = true;
            CoopLog.Debug("income halving installed");
        }

        public void Uninstall()
        {
            if (!_installed) return;
            var im = SingletonMonoBehaviour<InterfaceManager>.Instance;
            if (im != null && _handler != null)
                im.OnGetMoneyButtonClicked = (Action)Delegate.Remove(im.OnGetMoneyButtonClicked, _handler);
            _handler = null;
            _installed = false;
        }

        private void OnMoneyCollected()
        {
            if (!Enabled) return;
            try
            {
                var wc = UnityEngine.Object.FindAnyObjectByType<WinCanvas>(FindObjectsInactive.Include);
                if (wc == null)
                {
                    var all = Resources.FindObjectsOfTypeAll<WinCanvas>();
                    if (all != null && all.Length > 0) wc = all[0];
                }
                if (wc == null) { CoopLog.Warn("income: no WinCanvas found"); return; }

                var earnedField = GameRefl.Field(typeof(WinCanvas), "m_ValueEarned");
                if (earnedField == null) return;
                int earned = (int)earnedField.GetValue(wc);
                if (earned <= 0) return;

                int keep = (earned + 1) / 2;     // ceil(earned/2) - round the players' way
                int refund = earned - keep;      // what we take back

                var cdm = SingletonMonoBehaviour<ChessDataManager>.Instance;
                var coinsField = GameRefl.Field(typeof(ChessDataManager), "m_Coins");
                if (cdm == null || coinsField == null) return;

                int coins = (int)coinsField.GetValue(cdm);
                coinsField.SetValue(cdm, Mathf.Max(0, coins - refund));
                CoopLog.Info($"co-op income: {earned} -> {keep} (halved)");
            }
            catch (Exception ex) { CoopLog.Error($"income halving failed: {ex.Message}"); }
        }

        // ---- money-breakdown display ----

        /// <summary>
        /// Makes the WIN screen tell the truth about the co-op share: a fourth breakdown row
        /// ("CO-OP SPLIT  -N") cloned from the game's own bonus row, and the collect button
        /// rewritten to the amount that will actually be banked. Runs a moment after WIN so
        /// WinCanvas.Initialize has filled the rows first.
        /// </summary>
        public void ShowShareOnWinScreen(MonoBehaviour host)
        {
            if (!Enabled || host == null) return;
            host.StartCoroutine(CO_PatchWinCanvas());
        }

        private IEnumerator CO_PatchWinCanvas()
        {
            yield return new WaitForSeconds(0.6f);   // let Initialize + its DOKill pass settle
            try { PatchWinCanvas(); }
            catch (Exception ex) { CoopLog.Warn($"win-screen share display failed: {ex.Message}"); }
        }

        private void PatchWinCanvas()
        {
            var gm = SingletonMonoBehaviour<GameManager>.Instance;
            if (gm == null || gm.CurrentState != State.WIN) return;

            var wc = UnityEngine.Object.FindAnyObjectByType<WinCanvas>(FindObjectsInactive.Include);
            if (wc == null) return;

            int earned = (int)(GameRefl.Field(typeof(WinCanvas), "m_ValueEarned")?.GetValue(wc) ?? 0);
            if (earned <= 0) return;
            int keep = (earned + 1) / 2;
            int refund = earned - keep;

            // The collect button promises "&$" with the full total - rewrite it to the share.
            foreach (var name in new[] { "m_ButtonText", "m_ButtonTextGraveyard" })
            {
                var t = GameRefl.Field(typeof(WinCanvas), name)?.GetValue(wc) as TMP_Text;
                if (t != null && t.text.Contains(earned.ToString()))
                    t.text = t.text.Replace(earned.ToString(), keep.ToString());
            }

            if (refund <= 0) return;

            // A fourth row under the breakdown, cloned from the bonus row so it inherits the
            // exact font, size and colours. Row pitch = bonus row minus captured row.
            var bonusExpl = GameRefl.Field(typeof(WinCanvas), "m_Text_PiecesBonus_Explanation")?.GetValue(wc) as TMP_Text;
            var bonusVal = GameRefl.Field(typeof(WinCanvas), "m_Text_PiecesBonus")?.GetValue(wc) as TMP_Text;
            var capExpl = GameRefl.Field(typeof(WinCanvas), "m_Text_PiecesCaptured_Explanation")?.GetValue(wc) as TMP_Text;
            if (bonusExpl == null || bonusVal == null || capExpl == null) return;

            MakeRowLabel(bonusExpl, capExpl, "__CoopSplitLabel", $"CO-OP SPLIT ({(refund == keep ? "50%" : "P1+P2")})");
            var val = MakeRowLabel(bonusVal, capExpl, "__CoopSplitValue", $"-{refund}");
            if (val != null) val.color = CoopVisuals.P2;
            CoopLog.Debug($"win screen: earned {earned}, keeping {keep} (split row shown)");
        }

        private static TMP_Text MakeRowLabel(TMP_Text source, TMP_Text pitchRef, string name, string text)
        {
            var parent = source.transform.parent;
            if (parent == null) return null;

            var existing = parent.Find(name);
            if (existing != null) UnityEngine.Object.Destroy(existing.gameObject);

            var go = UnityEngine.Object.Instantiate(source.gameObject, parent);
            go.name = name;
            // A CountingText on the clone would animate the value straight back to the
            // source's number; the clone is static text.
            foreach (var mb in go.GetComponents<MonoBehaviour>())
                if (mb != null && mb.GetType().Name == "CountingText") UnityEngine.Object.Destroy(mb);

            var pitch = source.transform.localPosition - SameRowPosition(pitchRef, source);
            go.transform.localPosition = source.transform.localPosition + pitch;

            var t = go.GetComponent<TMP_Text>();
            if (t == null) { UnityEngine.Object.Destroy(go); return null; }
            t.text = text;
            var c = t.color; c.a = 1f; t.color = c;
            return t;
        }

        /// <summary>The pitch reference's position projected into the source's column: rows
        /// differ in Y, columns in X, so only Y is taken from the reference.</summary>
        private static Vector3 SameRowPosition(TMP_Text pitchRef, TMP_Text source)
        {
            var p = source.transform.localPosition;
            p.y = pitchRef.transform.localPosition.y;
            return p;
        }
    }
}
