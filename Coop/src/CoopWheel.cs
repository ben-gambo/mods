using System;
using Blukulele.CHE;
using Blukulele.Core;
using UnityEngine;

namespace Gambonanza.Coop
{
    /// <summary>
    /// The piece wheel, shared. Both clients roll the same faces (PieceManager.GetPieceWheel
    /// runs off the synced seed), but every interaction with it is local-only in vanilla: one
    /// player hitting STOP leaves the other watching a wheel that never stops, and the reward
    /// pick adds a piece to one stock and not the other - a hard desync for the rest of the run.
    ///
    /// So all three actions travel: STOP, the reward choice (take or sell, by slot), and the
    /// skip that closes the wheel without taking anything. Whoever clicks first wins; the
    /// other client replays the same call through the game's own methods.
    ///
    /// Detection is by polling, for the same reason CoopShop polls: the buttons are serialized
    /// UnityEvents a mod cannot cleanly unhook, and polling covers mouse, gamepad and keyboard.
    /// </summary>
    internal sealed class CoopWheel
    {
        public const string OpStop = "s";
        public const string OpPick = "p";
        public const string OpClose = "c";
        public const string ModeTake = "t";
        public const string ModeSell = "l";

        private readonly Action<string> _send;
        private bool _applyingRemote;

        private bool _inWheel;
        private bool _prevCanStop;
        private bool _stopped;        // this wheel has been resolved on this client
        private bool _prevUsed;       // reward panel consumed
        private bool _picked;         // a choice was relayed or applied
        private bool _prevRewardUp;   // reward panel on screen

        public CoopWheel(Action<string> send)
        {
            _send = send;
        }

        private static CanvasChessPieceWheel Canvas
        {
            get
            {
                var c = UnityEngine.Object.FindAnyObjectByType<CanvasChessPieceWheel>();
                if (c == null)
                {
                    var all = Resources.FindObjectsOfTypeAll<CanvasChessPieceWheel>();
                    if (all != null && all.Length > 0) c = all[0];
                }
                return c;
            }
        }

        private static ChessPieceRewardWheelBehaviour Reward(CanvasChessPieceWheel c)
            => GameRefl.GetField(c, "m_ChessPieceRewardWheelBehaviour") as ChessPieceRewardWheelBehaviour;

        private static ChoiceBehaviourChessPiece[] Choices(ChessPieceRewardWheelBehaviour r)
            => r == null ? Array.Empty<ChoiceBehaviourChessPiece>()
                         : r.GetComponentsInChildren<ChoiceBehaviourChessPiece>(true);

        // m_Used / m_Icon / m_Selected are declared on the base type, and Type.GetField does not
        // reach a base class's non-public members - so they have to be read off BaseChoiceBehaviour.
        private static bool ChoiceTaken(ChoiceBehaviourChessPiece c)
            => (bool)(GameRefl.Field(typeof(BaseChoiceBehaviour), "m_Used")?.GetValue(c) ?? false);

        private static bool ChoiceSold(ChoiceBehaviourChessPiece c)
        {
            // RightClick (take) only disables the Image; LeftClick (sell) deactivates its object.
            var icon = GameRefl.Field(typeof(BaseChoiceBehaviour), "m_Icon")?.GetValue(c) as UnityEngine.UI.Image;
            return icon != null && !icon.gameObject.activeSelf;
        }

        // ---- local detection ----

        public void Tick()
        {
            var gm = SingletonMonoBehaviour<GameManager>.Instance;
            if (gm == null) return;

            bool inWheel = gm.CurrentState == State.WHEEL_GAME;
            if (!inWheel)
            {
                if (_inWheel) LeaveWheel();
                return;
            }

            var canvas = Canvas;
            if (canvas == null) return;

            if (!_inWheel)
            {
                _inWheel = true;
                _stopped = false;
                _picked = false;
                _prevCanStop = CanStop(canvas);
                var r0 = Reward(canvas);
                _prevUsed = r0 != null && r0.Used;
                _prevRewardUp = RewardUp(r0);
            }

            TickStop(canvas);
            TickPick(canvas);
        }

        private static bool CanStop(CanvasChessPieceWheel c)
            => (bool)(GameRefl.GetField(c, "m_CanStopWheel") ?? false);

        private static bool RewardUp(ChessPieceRewardWheelBehaviour r)
            => r != null && r.gameObject.activeSelf;

        private void TickStop(CanvasChessPieceWheel canvas)
        {
            bool canStop = CanStop(canvas);

            if (_stopped)
            {
                // A client that was stopped remotely may still have CO_CanStopWheel pending from
                // its own intro; letting it re-arm would allow a second StopWheels() and a second
                // reward roll. Hold it shut for the rest of this wheel.
                if (canStop) GameRefl.SetField(canvas, "m_CanStopWheel", false);
                _prevCanStop = false;
                return;
            }

            // true -> false is the STOP button. The reward panel coming up covers the other
            // route to the same place: Skip() resolves the wheel to the same faces without ever
            // arming m_CanStopWheel, and the "Skip animations" setting fires it automatically -
            // a setting that is per-client and so cannot be assumed equal on both sides.
            bool rewardUp = RewardUp(Reward(canvas));
            if ((_prevCanStop && !canStop) || (rewardUp && !_prevRewardUp))
            {
                _stopped = true;
                if (!_applyingRemote)
                {
                    _send(Msg.Make(Msg.Wheel, OpStop));
                    CoopLog.Debug("relayed local wheel stop");
                }
            }
            _prevCanStop = canStop;
            _prevRewardUp = rewardUp;
        }

        private void TickPick(CanvasChessPieceWheel canvas)
        {
            var reward = Reward(canvas);
            if (reward == null) return;

            bool used = reward.Used;
            if (used && !_prevUsed && !_applyingRemote && !_picked)
            {
                _picked = true;
                var choices = Choices(reward);
                for (int i = 0; i < choices.Length; i++)
                {
                    if (ChoiceTaken(choices[i]))
                    {
                        _send(Msg.Make(Msg.Wheel, OpPick, i, ModeTake));
                        CoopLog.Debug($"relayed local wheel take, slot {i}");
                        break;
                    }
                    if (ChoiceSold(choices[i]))
                    {
                        _send(Msg.Make(Msg.Wheel, OpPick, i, ModeSell));
                        CoopLog.Debug($"relayed local wheel sell, slot {i}");
                        break;
                    }
                }
            }
            _prevUsed = used;
        }

        /// <summary>
        /// Leaving WHEEL_GAME with nothing taken is the Skip button. After a pick the game
        /// closes itself on both clients (ChessPieceRewardWheelBehaviour.Take invokes Close
        /// after 0.5s locally), so only the skip has to travel.
        /// </summary>
        private void LeaveWheel()
        {
            bool skipped = !_picked;
            _inWheel = false;
            _stopped = false;
            _picked = false;
            _prevCanStop = false;
            _prevUsed = false;
            _prevRewardUp = false;
            if (skipped && !_applyingRemote)
            {
                _send(Msg.Make(Msg.Wheel, OpClose));
                CoopLog.Debug("relayed local wheel skip");
            }
        }

        // ---- apply remote ----

        public void Apply(string[] p)
        {
            var canvas = Canvas;
            if (canvas == null) { CoopLog.Warn("wheel: no CanvasChessPieceWheel"); return; }

            string op = Msg.S(p, 1);
            _applyingRemote = true;
            try
            {
                switch (op)
                {
                    case OpStop: ApplyStop(canvas); break;
                    case OpPick: ApplyPick(canvas, Msg.I(p, 2), Msg.S(p, 3)); break;
                    case OpClose: ApplyClose(canvas); break;
                }
            }
            catch (Exception ex) { CoopLog.Error($"wheel '{op}' failed: {ex.Message}"); }
            finally { _applyingRemote = false; }
        }

        private void ApplyStop(CanvasChessPieceWheel canvas)
        {
            if (_stopped) return;
            var r = Reward(canvas);
            if (r != null && (r.Used || r.gameObject.activeSelf))
            {
                // Already resolved here (our own Skip beat the peer's STOP). Re-running
                // StopWheels would rebuild the reward cards under the player's cursor.
                _stopped = true;
                _prevRewardUp = true;
                return;
            }
            // The peer's intro animation may have finished a few frames before ours; StopWheels
            // is a no-op while m_CanStopWheel is false, so open the gate before calling it.
            GameRefl.SetField(canvas, "m_CanStopWheel", true);
            canvas.StopWheels();
            _stopped = true;
            _prevCanStop = false;
            _prevRewardUp = RewardUp(Reward(canvas));
            CoopLog.Debug("applied remote wheel stop");
        }

        private void ApplyPick(CanvasChessPieceWheel canvas, int slot, string mode)
        {
            var reward = Reward(canvas);
            if (reward == null) { CoopLog.Warn("wheel pick: no reward panel"); return; }
            if (reward.Used) return;

            var choices = Choices(reward);
            if (choices.Length == 0)
            {
                // The peer picked before our wheel resolved (their STOP message can be lost to
                // a skipped frame, or their client had "Skip animations" on). Resolve ours now
                // so the choice has something to land on.
                ApplyStop(canvas);
                reward = Reward(canvas);
                choices = Choices(reward);
            }
            if (slot < 0 || slot >= choices.Length || choices[slot] == null)
            {
                CoopLog.Warn($"wheel pick: slot {slot} unavailable ({choices.Length} choices) - possible desync.");
                return;
            }

            var choice = choices[slot];
            // Both click paths bail unless the card is the hovered one, which it is not here.
            var selected = GameRefl.Field(typeof(BaseChoiceBehaviour), "m_Selected");
            object was = selected?.GetValue(choice);
            selected?.SetValue(choice, true);
            try
            {
                if (mode == ModeSell) choice.LeftClick();
                else choice.RightClick();
            }
            finally { if (selected != null && was != null) selected.SetValue(choice, was); }

            _picked = true;
            _prevUsed = reward.Used;

            if (!reward.Used)
            {
                // Refused locally (stock or piece cap) - the two clients already disagree about
                // the stock, so say so and still leave the wheel together rather than stranding
                // this client on a panel the peer has closed.
                CoopLog.Warn($"DESYNC RISK: peer picked wheel slot {slot} but it was refused here (stock cap).");
                canvas.Close();
            }
            else CoopLog.Debug($"applied remote wheel {(mode == ModeSell ? "sell" : "take")}, slot {slot}");
        }

        private void ApplyClose(CanvasChessPieceWheel canvas)
        {
            var gm = SingletonMonoBehaviour<GameManager>.Instance;
            if (gm == null || gm.CurrentState != State.WHEEL_GAME) return;
            _picked = true;      // suppress our own skip relay when we leave
            canvas.Close();
            CoopLog.Debug("applied remote wheel skip");
        }

        public void Reset()
        {
            _inWheel = false;
            _stopped = false;
            _picked = false;
            _prevCanStop = false;
            _prevUsed = false;
            _prevRewardUp = false;
        }
    }
}
