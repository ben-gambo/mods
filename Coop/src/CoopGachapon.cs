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
        public const string ModeTake = "t";
        public const string ModeSell = "l";

        private readonly Action<string> _send;
        private bool _applyingRemote;

        private bool _inGacha;
        private bool _picked;
        private readonly bool[] _seenTaken = new bool[8];
        private readonly bool[] _seenSold = new bool[8];

        public CoopGachapon(Action<string> send)
        {
            _send = send;
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
        }
    }
}
