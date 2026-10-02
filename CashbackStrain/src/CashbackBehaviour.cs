using System;
using System.Collections.Generic;
using Blukulele.Audio;
using Blukulele.CHE;
using Blukulele.Core;
using Blukulele.Module.Audio;
using Gambonanza.StrainApi;
using UnityEngine;

namespace Gambonanza.CashbackStrain
{
    /// <summary>Pay an expiring gambit's monetary sell value without selling it.</summary>
    public sealed class CashbackBehaviour : StrainBehaviour
    {
        public const string StrainId = "cashback";

        private readonly HashSet<GambitBehaviour> _settled = new HashSet<GambitBehaviour>();
        private OrderedDelegateHooks<State> _hooks;
        private GameManager _game;
        private SellManager _sell;
        private bool _active;
        private string _lastBindError;

        private void Awake()
        {
            _hooks = new OrderedDelegateHooks<State>(
                callback => callback.Target is GambitStrainExpire && callback.Method.Name == "Behave",
                callback => (GambitStrainExpire)callback.Target,
                WrapExpiry);
        }

        private void OnEnable()
        {
            _active = true;
            Reconcile();
        }

        private void Start() => Reconcile();

        private void Update() => Reconcile();

        protected override void OnGameStarted() => Reconcile();

        private void OnDisable() => Detach();

        private void OnDestroy() => Detach();

        private void Reconcile()
        {
            if (!_active || _hooks == null) return;
            try
            {
                var game = SingletonMonoBehaviour<GameManager>.IsCreated()
                    ? SingletonMonoBehaviour<GameManager>.Instance : null;
                if (!ReferenceEquals(_game, game))
                {
                    if (_game) _game.onStateChanged = _hooks.Restore(_game.onStateChanged);
                    else _hooks.Restore(null);
                    _game = game;
                }
                if (_game) _game.onStateChanged = _hooks.Reconcile(_game.onStateChanged);

                var sell = SingletonMonoBehaviour<SellManager>.IsCreated()
                    ? SingletonMonoBehaviour<SellManager>.Instance : null;
                if (!ReferenceEquals(_sell, sell))
                {
                    if (_sell) _sell.OnSellGambit -= RememberSale;
                    _sell = sell;
                    if (_sell) _sell.OnSellGambit += RememberSale;
                }
                _settled.RemoveWhere(gambit => !gambit);
                _lastBindError = null;
            }
            catch (Exception ex)
            {
                // Unity Update runs every frame; report a persistent failure once.
                if (_lastBindError != ex.Message) Log("could not bind expiry callbacks: " + ex);
                _lastBindError = ex.Message;
            }
        }

        private Action<State> WrapExpiry(Action<State> original)
        {
            var expiry = (GambitStrainExpire)original.Target;
            return state =>
            {
                // Destroy is deferred, and another state can arrive before Update
                // removes the wrapper that native OnDestroy could not unsubscribe.
                if (!expiry) return;
                GambitBehaviour gambit = null;
                int amount = 0;
                int counterBefore = 0;
                int limit = 0;
                bool eligible = false;
                try
                {
                    eligible = TrySnapshot(state, expiry, out gambit, out amount, out counterBefore, out limit);
                }
                catch (Exception ex) { Log("could not read expiry sell value: " + ex); }

                // Exactly once, in its existing position. Native expiration and its
                // cleanup still run even when this strain is disabled or errors.
                original(state);

                if (!eligible || !_active || !expiry || !gambit ||
                    expiry.Counter <= counterBefore || expiry.Counter < limit ||
                    !_settled.Add(gambit)) return;

                // The snapshot precedes native cleanup of Collector/Boss Tooth values.
                // Calling Sell or OnSellGambit would also trigger sale-only effects.
                try { Pay(gambit, amount); }
                catch (Exception ex) { Log("cashback feedback failed: " + ex); }
            };
        }

        private bool TrySnapshot(State state, GambitStrainExpire expiry,
            out GambitBehaviour gambit, out int amount, out int counter, out int limit)
        {
            gambit = null;
            amount = counter = limit = 0;
            if (!_active || state != State.WIN || !expiry ||
                !SingletonMonoBehaviour<StrainManager>.IsCreated() ||
                !SingletonMonoBehaviour<GambitManager>.IsCreated() ||
                !SingletonMonoBehaviour<ChessDataManager>.IsCreated() || !_sell) return false;

            var strains = SingletonMonoBehaviour<StrainManager>.Instance;
            if (!strains || !strains.ActivatedStrain[Strain.GAMBIT_EXPIRE]) return false;
            counter = expiry.Counter;
            limit = strains.ExpireLimit;
            // Match native Counter++ without overflowing at int.MaxValue.
            if (counter == int.MaxValue || counter + 1 < limit) return false;

            gambit = expiry.GetComponent<GambitBehaviour>();
            if (!gambit || !gambit.Info || _settled.Contains(gambit) || !IsOwned(gambit)) return false;

            if (gambit is ToothGambitBehaviour)
            {
                if (DataManager.Instance == null || DataManager.Instance.Data == null) return false;
                amount = Math.Max(0, DataManager.Instance.Data.BossToothCoin);
            }
            else
            {
                long value = _sell.GetSellPriceGambit(gambit.Info);
                var collector = gambit.GetComponent<GambitCollector>();
                if (collector) value += collector.SellValue;
                amount = (int)Math.Max(0L, Math.Min(int.MaxValue, value));
            }
            return true;
        }

        private static bool IsOwned(GambitBehaviour gambit)
        {
            var manager = SingletonMonoBehaviour<GambitManager>.Instance;
            if (!manager || manager.GambitPlaces == null) return false;
            foreach (var place in manager.GambitPlaces)
                if (place && place.CurrentGambit == gambit) return true;
            return false;
        }

        private void Pay(GambitBehaviour gambit, int amount)
        {
            if (amount <= 0) return;
            try
            {
                // Commit the reward before cosmetic feedback. Failed feedback must
                // never prevent coins arriving or permit another payout.
                SingletonMonoBehaviour<ChessDataManager>.Instance.IncreaseCoin(amount);
            }
            catch (Exception ex)
            {
                Log("could not credit cashback: " + ex);
                return;
            }
            try
            {
                if (SingletonMonoBehaviour<MoneyAnimationManager>.IsCreated() &&
                    SingletonMonoBehaviour<MoneyAnimationManager>.Instance)
                    SingletonMonoBehaviour<MoneyAnimationManager>.Instance.SpawnMoney(gambit.transform, amount);
                else SingletonMonoBehaviour<ChessDataManager>.Instance.IncreaseTextCoin(true);
                AudioManager.Play(AudioEvents.Sell, loop: false, UnityEngine.Random.Range(0.9f, 1.1f));
            }
            catch (Exception ex)
            {
                SingletonMonoBehaviour<ChessDataManager>.Instance.IncreaseTextCoin(true);
                Log("cashback credited, but feedback failed: " + ex);
            }
            Log("expired " + gambit.Info.ID + ": +$" + amount + ".");
        }

        private void RememberSale(GambitBehaviour gambit)
        {
            if (_active && gambit) _settled.Add(gambit);
        }

        private void Detach()
        {
            _active = false;
            if (_game && _hooks != null) _game.onStateChanged = _hooks.Restore(_game.onStateChanged);
            else _hooks?.Restore(null);
            if (_sell) _sell.OnSellGambit -= RememberSale;
            _game = null;
            _sell = null;
            _settled.Clear();
        }
    }
}
