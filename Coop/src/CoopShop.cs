using System;
using System.Collections.Generic;
using Blukulele.CHE;
using Blukulele.Core;
using UnityEngine;

namespace Gambonanza.Coop
{
    /// <summary>
    /// Shared shop. Both clients roll an identical shop from the synced seed (every shop roll
    /// goes through ChessDataManager.GetRandomOccurrence, a pure function of seed + wave +
    /// occurrence counter), so a purchase only has to travel as a slot index.
    ///
    /// Local buys are detected by polling each slot's public Bought/Used flag rather than by
    /// intercepting clicks: the buttons are wired through serialized UnityEvents that a mod
    /// cannot cleanly unhook, and polling works for mouse, gamepad and keyboard alike.
    /// Applying a remote buy re-runs the very same OnClick(), so both wallets stay in step.
    /// </summary>
    internal sealed class CoopShop
    {
        private readonly Action<string> _send;
        private bool _applyingRemote;

        private readonly bool[] _seenGambit = new bool[4];
        private readonly bool[] _seenPiece = new bool[4];
        private readonly bool[] _seenToken = new bool[4];
        private bool _tracking;

        private Action _rerollHandler;
        private Action _limitHandler;
        private bool _hooked;

        public CoopShop(Action<string> send)
        {
            _send = send;
        }

        public bool ApplyingRemote => _applyingRemote;

        private static ShopCanvas Canvas
        {
            get
            {
                var c = UnityEngine.Object.FindAnyObjectByType<ShopCanvas>();
                if (c == null)
                {
                    var all = Resources.FindObjectsOfTypeAll<ShopCanvas>();
                    if (all != null && all.Length > 0) c = all[0];
                }
                return c;
            }
        }

        private static List<T> PrivateList<T>(ShopCanvas canvas, string field) where T : class
        {
            var v = GameRefl.GetField(canvas, field);
            return v as List<T>;
        }

        private static List<GambitToBuy> Gambits(ShopCanvas c) => c != null ? c.GambitToBuyInstances : null;
        private static List<PieceToBuyButton> Pieces(ShopCanvas c) => PrivateList<PieceToBuyButton>(c, "m_PieceToBuyInstances");
        private static List<TokenToBuy> Tokens(ShopCanvas c) => PrivateList<TokenToBuy>(c, "m_TokenToBuyInstances");

        // ---- reroll / limit relays ----

        public void Hook()
        {
            if (_hooked) return;
            var cdm = SingletonMonoBehaviour<ChessDataManager>.Instance;
            if (cdm == null) return;
            _rerollHandler = () => { if (!_applyingRemote) _send(Msg.Make(Msg.Reroll)); };
            _limitHandler = () => { if (!_applyingRemote) _send(Msg.Make(Msg.Limit)); };
            cdm.OnReroll = (Action)Delegate.Combine(cdm.OnReroll, _rerollHandler);
            cdm.OnIncreasePieceLimit = (Action)Delegate.Combine(cdm.OnIncreasePieceLimit, _limitHandler);
            _hooked = true;
        }

        public void Unhook()
        {
            var cdm = SingletonMonoBehaviour<ChessDataManager>.Instance;
            if (cdm != null)
            {
                if (_rerollHandler != null) cdm.OnReroll = (Action)Delegate.Remove(cdm.OnReroll, _rerollHandler);
                if (_limitHandler != null) cdm.OnIncreasePieceLimit = (Action)Delegate.Remove(cdm.OnIncreasePieceLimit, _limitHandler);
            }
            _rerollHandler = null;
            _limitHandler = null;
            _hooked = false;
        }

        // ---- local buy detection ----

        /// <summary>Polls slot state each frame while the shop is open and relays local buys.</summary>
        public void Tick()
        {
            var gm = SingletonMonoBehaviour<GameManager>.Instance;
            if (gm == null) return;
            // TokenToBuy.OnClick switches to GACHAPON/WHEEL_GAME/PACHINKO *before* it sets
            // m_Used (TokenToBuy.cs:200-219), so a SHOP-only gate would miss every token buy.
            bool shopContext = gm.CurrentState == State.SHOP
                            || gm.CurrentState == State.GACHAPON
                            || gm.CurrentState == State.WHEEL_GAME
                            || gm.CurrentState == State.PACHINKO;
            if (!shopContext)
            {
                if (_tracking) ResetTracking();
                return;
            }

            var canvas = Canvas;
            if (canvas == null) return;

            var gambits = Gambits(canvas);
            var pieces = Pieces(canvas);
            var tokens = Tokens(canvas);

            if (!_tracking)
            {
                // Prime the baseline so slots already bought before we started don't fire.
                Snapshot(gambits, _seenGambit, g => g != null && g.Bought);
                Snapshot(pieces, _seenPiece, p => p != null && p.Bought);
                Snapshot(tokens, _seenToken, t => t != null && t.Used);
                _tracking = true;
                return;
            }

            Detect(gambits, _seenGambit, g => g != null && g.Bought, i => i);       // slots 0..1
            Detect(pieces, _seenPiece, p => p != null && p.Bought, i => i + 2);     // slots 2..3
            Detect(tokens, _seenToken, t => t != null && t.Used, i => i + 10);      // slots 10+
        }

        private static void Snapshot<T>(List<T> list, bool[] seen, Func<T, bool> bought) where T : class
        {
            for (int i = 0; i < seen.Length; i++)
                seen[i] = list != null && i < list.Count && bought(list[i]);
        }

        private void Detect<T>(List<T> list, bool[] seen, Func<T, bool> bought, Func<int, int> toSlot) where T : class
        {
            if (list == null) return;
            for (int i = 0; i < seen.Length && i < list.Count; i++)
            {
                bool now = bought(list[i]);
                if (now && !seen[i])
                {
                    seen[i] = true;
                    if (!_applyingRemote)
                    {
                        _send(Msg.Make(Msg.Buy, toSlot(i)));
                        CoopLog.Debug($"relayed local shop buy, slot {toSlot(i)}");
                    }
                }
                else if (!now && seen[i])
                {
                    seen[i] = false;   // a reroll refreshed this slot
                }
            }
        }

        private void ResetTracking()
        {
            _tracking = false;
            Array.Clear(_seenGambit, 0, _seenGambit.Length);
            Array.Clear(_seenPiece, 0, _seenPiece.Length);
            Array.Clear(_seenToken, 0, _seenToken.Length);
        }

        // ---- apply remote ----

        public void ApplyBuy(int slotIndex)
        {
            var canvas = Canvas;
            if (canvas == null) { CoopLog.Warn("shop buy: no ShopCanvas"); return; }

            _applyingRemote = true;
            try
            {
                if (slotIndex >= 10)
                {
                    var list = Tokens(canvas);
                    int idx = slotIndex - 10;
                    if (list != null && idx >= 0 && idx < list.Count && list[idx] != null)
                    {
                        var item = list[idx];
                        item.OnClick();
                        if (item.Used) { if (idx < _seenToken.Length) _seenToken[idx] = true; }
                        else CoopLog.Warn($"DESYNC RISK: peer bought token slot {idx} but it was refused here (coins).");
                    }
                    else CoopLog.Warn($"shop buy: token slot {idx} unavailable");
                }
                else if (slotIndex <= 1)
                {
                    var list = Gambits(canvas);
                    if (list != null && slotIndex >= 0 && slotIndex < list.Count && list[slotIndex] != null)
                    {
                        var item = list[slotIndex];
                        item.OnClick();
                        // OnClick no-ops silently when the wallet is short or the gambit bar is
                        // full (GambitToBuy.cs:353,358). Latching the slot regardless would
                        // hide a permanent inventory/coin divergence.
                        if (item.Bought) { if (slotIndex < _seenGambit.Length) _seenGambit[slotIndex] = true; }
                        else CoopLog.Warn($"DESYNC RISK: peer bought gambit slot {slotIndex} but it was refused here (coins/space).");
                    }
                    else CoopLog.Warn($"shop buy: gambit slot {slotIndex} unavailable");
                }
                else
                {
                    var list = Pieces(canvas);
                    int idx = slotIndex - 2;
                    if (list != null && idx >= 0 && idx < list.Count && list[idx] != null)
                    {
                        var item = list[idx];
                        item.OnClick();
                        if (item.Bought) { if (idx < _seenPiece.Length) _seenPiece[idx] = true; }
                        else CoopLog.Warn($"DESYNC RISK: peer bought piece slot {idx} but it was refused here (coins/piece cap).");
                    }
                    else CoopLog.Warn($"shop buy: piece slot {idx} unavailable");
                }
            }
            catch (Exception ex) { CoopLog.Error($"shop buy failed: {ex.Message}"); }
            finally { _applyingRemote = false; }
        }

        public void ApplyReroll()
        {
            var canvas = Canvas;
            if (canvas == null) return;
            _applyingRemote = true;
            try
            {
                canvas.BuyRerollShop();
                Array.Clear(_seenGambit, 0, _seenGambit.Length);
                Array.Clear(_seenPiece, 0, _seenPiece.Length);
            }
            catch (Exception ex) { CoopLog.Error($"shop reroll failed: {ex.Message}"); }
            finally { _applyingRemote = false; }
        }

        public void ApplyLimit()
        {
            var canvas = Canvas;
            if (canvas == null) return;
            _applyingRemote = true;
            try { canvas.IncreaseLimit(); }
            catch (Exception ex) { CoopLog.Error($"shop limit failed: {ex.Message}"); }
            finally { _applyingRemote = false; }
        }

        public string Describe()
        {
            var canvas = Canvas;
            if (canvas == null) return "no shop open";
            var g = Gambits(canvas);
            var p = Pieces(canvas);
            return $"gambits={(g == null ? 0 : g.Count)} pieces={(p == null ? 0 : p.Count)} coins={SingletonMonoBehaviour<ChessDataManager>.Instance?.Coins}";
        }
    }
}
