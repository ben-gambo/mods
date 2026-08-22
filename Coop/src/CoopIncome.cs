using System;
using System.Reflection;
using Blukulele.CHE;
using Blukulele.Core;
using UnityEngine;

namespace Gambonanza.Coop
{
    /// <summary>
    /// Halves post-battle income only (base reward + capture bonus + interest), leaving
    /// in-battle gambit income untouched.
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

                int keep = earned / 2;           // halved, rounded down
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
    }
}
