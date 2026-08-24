using System;
using Blukulele.CHE;
using Blukulele.Core;
using UnityEngine;

namespace Gambonanza.Coop
{
    /// <summary>
    /// The gachapon, shared. The capsule's rarity and its gambit choices are seeded
    /// (GACHAPON_RARITY / GACHAPON_&lt;rarity&gt; both run through GetRandomOccurrence), so
    /// both clients open the same capsule with the same cards - but the pick is local in
    /// vanilla, which put a gambit on one board and not the other.
    ///
    /// Three actions travel: take (right-click), sell (left-click), and the skip that
    /// closes the capsule with nothing chosen. First click wins; the other client replays
    /// the same call through the game's own ChoiceBehaviour, so the gambit bar, the wallet
    /// and the follow-up Close all run the vanilla path.
    ///
    /// Polling, as everywhere: the buttons are serialized UnityEvents a mod cannot
    /// cleanly unhook, and polling covers mouse, gamepad and keyboard alike.
    /// </summary>
    internal sealed class CoopGachapon
    {
        public const string OpPick = "p";
        public const string OpClose = "c";
        public const string OpPool = "o";
        public const string ModeTake = "t";
        public const string ModeSell = "l";

        private readonly Action<string> _send;
        private readonly Func<bool> _isHost;
        private bool _applyingRemote;

        private bool _net_IsHost => _isHost != null && _isHost();

        private bool _inGacha;
        private bool _picked;
        private bool _poolSent;          // host: this capsule's contents have been relayed
        private bool _poolApplied;       // guest: the host's contents are in place
        private string _pendingPool;     // guest: contents that arrived before the canvas existed
        private readonly bool[] _seenTaken = new bool[8];
        private readonly bool[] _seenSold = new bool[8];

        public CoopGachapon(Action<string> send, Func<bool> isHost)
        {
            _send = send;
            _isHost = isHost;
        }

        /// <summary>
        /// Host: relay the capsule's exact contents. The rarity roll and the gambit draw are
        /// both seeded, so in principle the guest rolls the same three - but "in principle"
        /// has been wrong twice in this mod already (strains, unlocks), and the draw filters
        /// by unlock state, which lives in runtime-only game state. Sending the indices makes
        /// the host's capsule the capsule, whatever the guest's own library thinks.
        /// </summary>
        private void TickHostPool(CanvasGachapon canvas)
        {
            if (_poolSent) return;
            var gambits = GameRefl.GetField(canvas, "m_Gambits") as System.Collections.Generic.List<SO_Gambit>;
            if (gambits == null || gambits.Count == 0) return;   // Initialize has not run yet

            var lib = SingletonMonoBehaviour<GambitLibrary>.Instance;
            if (lib == null) return;
            var idx = new System.Collections.Generic.List<string>(gambits.Count);
            foreach (var g in gambits) idx.Add(lib.GambitsInfo.IndexOf(g).ToString());

            int rarity = (int)(GameRefl.GetField(canvas, "m_CurrentRarity") ?? 0);
            _poolSent = true;
            _send(Msg.Make(Msg.Gacha, OpPool, rarity, string.Join(",", idx)));
            CoopLog.Debug($"relayed gachapon pool (rarity {rarity}): {string.Join(",", idx)}");
        }

        /// <summary>Guest: apply the host's contents as soon as both the message and the
        /// canvas are available - either order.</summary>
        private void TickGuestPool(CanvasGachapon canvas)
        {
            if (_poolApplied || _pendingPool == null) return;
            var gambits = GameRefl.GetField(canvas, "m_Gambits") as System.Collections.Generic.List<SO_Gambit>;
            if (gambits == null) return;
            ApplyPool(canvas, _pendingPool);
        }

        private void ApplyPool(CanvasGachapon canvas, string payload)
        {
            var lib = SingletonMonoBehaviour<GambitLibrary>.Instance;
            if (lib == null) return;

            var parts = payload.Split('|');
            int rarity = 0;
            int.TryParse(parts[0], out rarity);

            var picked = new System.Collections.Generic.List<SO_Gambit>();
            var indices = new System.Collections.Generic.List<int>();
            foreach (var t in parts[1].Split(','))
            {
                if (!int.TryParse(t, out var i)) continue;
                if (i < 0 || i >= lib.GambitsInfo.Count)
                {
                    CoopLog.Warn($"gachapon pool: index {i} out of range - the peer has a different game build.");
                    continue;
                }
                // Straight out of the full library: no unlock filter anywhere on this path,
                // which is exactly the point - P2 gets the host's cards either way.
                picked.Add(lib.GambitsInfo[i]);
                indices.Add(i);
            }
            if (picked.Count == 0) { CoopLog.Warn("gachapon pool: nothing usable in the host's list."); return; }

            GameRefl.SetField(canvas, "m_Gambits", picked);
            GameRefl.SetField(canvas, "m_CurrentRarity", (Rarity)rarity);
            DataManager.Instance.Data.GambitsGachapon = indices.ToArray();

            _poolApplied = true;
            _pendingPool = null;
            CoopLog.Debug($"applied host gachapon pool: {picked.Count} gambits, rarity {rarity}");
        }

        private static CanvasGachapon Canvas
        {
            get
            {
                var c = UnityEngine.Object.FindAnyObjectByType<CanvasGachapon>();
                if (c == null)
                {
                    var all = Resources.FindObjectsOfTypeAll<CanvasGachapon>();
                    if (all != null && all.Length > 0) c = all[0];
                }
                return c;
            }
        }

        private static ChoiceApparition Apparition(CanvasGachapon c)
            => GameRefl.GetField(c, "m_ChoiceApparition") as ChoiceApparition;

        // The instantiation order inside m_Choices is the serialized prefab order - identical
        // on both clients, so the array index is a stable wire address.
        private static ChoiceBehaviour[] Choices(CanvasGachapon c)
        {
            var app = Apparition(c);
            return app != null
                ? (GameRefl.GetField(app, "m_Choices") as ChoiceBehaviour[]) ?? Array.Empty<ChoiceBehaviour>()
                : Array.Empty<ChoiceBehaviour>();
        }

        // m_Used and m_Icon live on BaseChoiceBehaviour; Type.GetField does not see a base
        // class's private members, so read them off the base type explicitly.
        private static bool ChoiceTaken(ChoiceBehaviour c)
            => (bool)(GameRefl.Field(typeof(BaseChoiceBehaviour), "m_Used")?.GetValue(c) ?? false);

        private static bool ChoiceSold(ChoiceBehaviour c)
        {
            // RightClick (take) sets m_Used; LeftClick (sell) only hides the icon.
            var icon = GameRefl.Field(typeof(BaseChoiceBehaviour), "m_Icon")?.GetValue(c) as UnityEngine.UI.Image;
            return icon != null && !icon.gameObject.activeSelf;
        }

        // ---- local detection ----

        public void Tick()
        {
            var gm = SingletonMonoBehaviour<GameManager>.Instance;
            if (gm == null) return;

            bool inGacha = gm.CurrentState == State.GACHAPON;
            if (!inGacha)
            {
                if (_inGacha) LeaveGachapon();
                return;
            }

            var canvas = Canvas;
            if (canvas == null) return;

            var choices = Choices(canvas);
            if (choices.Length == 0) return;

            if (_net_IsHost) TickHostPool(canvas);
            else TickGuestPool(canvas);

            if (!_inGacha)
            {
                // Baseline from CURRENT state, not false: the choices are serialized prefab
                // objects and Initialize only resets m_Used once the capsule opens (~2s in) -
                // a stale flag from the previous gachapon must not fire a phantom relay.
                _inGacha = true;
                _picked = false;
                for (int i = 0; i < _seenTaken.Length; i++)
                {
                    bool has = i < choices.Length && choices[i] != null;
                    _seenTaken[i] = has && ChoiceTaken(choices[i]);
                    _seenSold[i] = has && ChoiceSold(choices[i]);
                }
                return;
            }

            for (int i = 0; i < choices.Length && i < _seenTaken.Length; i++)
            {
                if (choices[i] == null) continue;
                bool taken = ChoiceTaken(choices[i]);
                bool sold = !taken && ChoiceSold(choices[i]);

                if (taken && !_seenTaken[i])
                {
                    _seenTaken[i] = true;
                    if (!_applyingRemote && !_picked)
                    {
                        _picked = true;
                        _send(Msg.Make(Msg.Gacha, OpPick, i, ModeTake));
                        CoopLog.Debug($"relayed local gachapon take, slot {i}");
                    }
                }
                else if (sold && !_seenSold[i])
                {
                    _seenSold[i] = true;
                    if (!_applyingRemote && !_picked)
                    {
                        _picked = true;
                        _send(Msg.Make(Msg.Gacha, OpPick, i, ModeSell));
                        CoopLog.Debug($"relayed local gachapon sell, slot {i}");
                    }
                }
            }
        }

        /// <summary>Leaving GACHAPON with nothing chosen is the Skip button; only that travels.
        /// After a pick, ChoiceBehaviour invokes Close itself on both clients.</summary>
        private void LeaveGachapon()
        {
            bool skipped = !_picked;
            _inGacha = false;
            _picked = false;
            _poolSent = false;
            _poolApplied = false;
            _pendingPool = null;
            if (skipped && !_applyingRemote)
            {
                _send(Msg.Make(Msg.Gacha, OpClose));
                CoopLog.Debug("relayed local gachapon skip");
            }
        }

        // ---- apply remote ----

        public void Apply(string[] p)
        {
            var canvas = Canvas;
            if (canvas == null) { CoopLog.Warn("gachapon: no CanvasGachapon"); return; }

            string op = Msg.S(p, 1);
            _applyingRemote = true;
            try
            {
                switch (op)
                {
                    case OpPool:
                        // Arrives within a frame or two of the state flip and always well
                        // before ShowContainerAnimation (2s) reads the contents.
                        if (!_net_IsHost) { _pendingPool = Msg.S(p, 2) + "|" + Msg.S(p, 3); TickGuestPool(canvas); }
                        break;
                    case OpPick: ApplyPick(canvas, Msg.I(p, 2), Msg.S(p, 3)); break;
                    case OpClose: ApplyClose(canvas); break;
                }
            }
            catch (Exception ex) { CoopLog.Error($"gachapon '{op}' failed: {ex.Message}"); }
            finally { _applyingRemote = false; }
        }

        private void ApplyPick(CanvasGachapon canvas, int slot, string mode)
        {
            if (_picked) return;   // our own pick got there first
            var choices = Choices(canvas);
            if (slot < 0 || slot >= choices.Length || choices[slot] == null)
            {
                CoopLog.Warn($"gachapon pick: slot {slot} unavailable ({choices.Length} choices) - possible desync.");
                return;
            }

            // The capsule intro may still be running here; both clicks bail while
            // CanSelect is false, and the peer could only click after ITS intro.
            var app = Apparition(canvas);
            if (app != null && !app.CanSelect) app.CanSelect = true;

            var choice = choices[slot];
            if (mode == ModeSell) choice.LeftClick();
            else choice.RightClick();

            _picked = true;
            if (slot < _seenTaken.Length) { _seenTaken[slot] = ChoiceTaken(choice); _seenSold[slot] = ChoiceSold(choice); }

            if (!ChoiceTaken(choice) && !ChoiceSold(choice))
            {
                // Refused locally (gambit bar full here but not there) - the clients already
                // disagree; say so and leave together rather than stranding this one.
                CoopLog.Warn($"DESYNC RISK: peer picked gachapon slot {slot} but it was refused here (gambit bar full).");
                canvas.Close();
            }
            else CoopLog.Debug($"applied remote gachapon {(mode == ModeSell ? "sell" : "take")}, slot {slot}");
        }

        private void ApplyClose(CanvasGachapon canvas)
        {
            var gm = SingletonMonoBehaviour<GameManager>.Instance;
            if (gm == null || gm.CurrentState != State.GACHAPON) return;
            _picked = true;      // suppress our own skip relay when we leave
            canvas.Close();
            CoopLog.Debug("applied remote gachapon skip");
        }

        public void Reset()
        {
            _inGacha = false;
            _picked = false;
            _poolSent = false;
            _poolApplied = false;
            _pendingPool = null;
        }
    }
}
