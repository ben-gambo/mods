using System;
using System.Collections.Generic;
using Blukulele.CHE;
using Blukulele.Core;
using UnityEngine;

namespace Gambonanza.Coop
{
    /// <summary>
    /// The run-start piece wheel (CanvasPieceSelector, State.PIECE_SELECTION), shared.
    /// This is a different component from the token-shop wheel CoopWheel handles - same
    /// look, separate code - which is why syncing one did nothing for the other.
    ///
    /// The faces are seeded (GivePieceAtStart runs off GetRandomOccurrence), so both
    /// clients roll the same pieces; only the STOP has to travel. The message still
    /// carries the resolved pieces, because the hit-button bonus (TemporaryBonus[0])
    /// lets a player manually re-aim wheel 0 - a local choice the seed knows nothing
    /// about - and the sender's array is the truth either way.
    ///
    /// First STOP wins; the other client replays it through the game's own StopWheels(),
    /// which also runs PrepareGameData + the BoardPlacement transition, so both clients
    /// leave the wheel together on the vanilla path.
    /// </summary>
    internal sealed class CoopStartWheel
    {
        private readonly Action<string> _send;
        private bool _applyingRemote;

        private bool _inSelection;
        private bool _prevCanStop;
        private bool _stopped;

        public CoopStartWheel(Action<string> send)
        {
            _send = send;
        }

        private static CanvasPieceSelector Canvas
        {
            get
            {
                var c = UnityEngine.Object.FindAnyObjectByType<CanvasPieceSelector>();
                if (c == null)
                {
                    var all = Resources.FindObjectsOfTypeAll<CanvasPieceSelector>();
                    if (all != null && all.Length > 0) c = all[0];
                }
                return c;
            }
        }

        // ---- local detection ----

        public void Tick()
        {
            var gm = SingletonMonoBehaviour<GameManager>.Instance;
            if (gm == null || gm.CurrentState != State.PIECE_SELECTION)
            {
                if (_inSelection) Reset();
                return;
            }

            var canvas = Canvas;
            if (canvas == null) return;

            if (!_inSelection)
            {
                _inSelection = true;
                _stopped = false;
                _prevCanStop = false;
            }

            bool canStop = (bool)(GameRefl.GetField(canvas, "m_CanStopWheel") ?? false);

            if (_stopped)
            {
                // Nothing on this canvas re-arms after a stop, but hold the gate shut anyway.
                if (canStop) GameRefl.SetField(canvas, "m_CanStopWheel", false);
                return;
            }

            // Unlike the token wheel there is no skip path here: the only way from
            // armed (true) to disarmed (false) is StopWheels() - the STOP button.
            if (_prevCanStop && !canStop)
            {
                _stopped = true;
                if (!_applyingRemote)
                {
                    var pieces = GameRefl.GetField(canvas, "m_Pieces") as PieceType[];
                    _send(Msg.Make(Msg.StartWheel, EncodePieces(pieces)));
                    CoopLog.Debug("relayed local start-wheel stop");
                }
            }
            _prevCanStop = canStop;
        }

        private static string EncodePieces(PieceType[] pieces)
        {
            if (pieces == null || pieces.Length == 0) return "";
            var parts = new string[pieces.Length];
            for (int i = 0; i < pieces.Length; i++) parts[i] = ((int)pieces[i]).ToString();
            return string.Join(",", parts);
        }

        // ---- apply remote ----

        public void Apply(string[] p)
        {
            if (_stopped) return;   // our own stop got there first
            var gm = SingletonMonoBehaviour<GameManager>.Instance;
            if (gm == null || gm.CurrentState != State.PIECE_SELECTION)
            {
                CoopLog.Warn("start-wheel stop arrived outside piece selection - possible desync.");
                return;
            }
            var canvas = Canvas;
            if (canvas == null) { CoopLog.Warn("start wheel: no CanvasPieceSelector"); return; }

            var pieces = DecodePieces(Msg.S(p, 1));
            if (pieces.Length == 0) { CoopLog.Warn("start wheel: empty piece list"); return; }

            _applyingRemote = true;
            try
            {
                GameRefl.SetField(canvas, "m_Pieces", pieces);

                // With the bonus strain, StopWheels overwrites m_Pieces[0] from wheel 0's own
                // pointer position - which on this client is wherever OUR hit buttons left it,
                // not the sender's. Aim the wheel at the sender's piece first so the overwrite
                // is a no-op.
                var strains = SingletonMonoBehaviour<StrainManager>.Instance;
                if (strains != null && strains.TemporaryBonus[0])
                {
                    var wheels = GameRefl.GetField(canvas, "m_Wheels") as WheelSlotBehaviour[];
                    if (wheels != null && wheels.Length > 0 && wheels[0] != null)
                        wheels[0].Type = pieces[0];
                }

                // The peer could only stop after ITS arming delay, but ours may still be a few
                // frames out - StopWheels silently no-ops while the gate is shut.
                GameRefl.SetField(canvas, "m_CanStopWheel", true);
                canvas.StopWheels();
                _stopped = true;
                _prevCanStop = false;
                CoopLog.Debug($"applied remote start-wheel stop ({pieces.Length} pieces)");
            }
            catch (Exception ex) { CoopLog.Error($"start-wheel stop failed: {ex.Message}"); }
            finally { _applyingRemote = false; }
        }

        private static PieceType[] DecodePieces(string csv)
        {
            if (string.IsNullOrEmpty(csv)) return Array.Empty<PieceType>();
            var parts = csv.Split(',');
            var list = new List<PieceType>(parts.Length);
            foreach (var s in parts)
                if (int.TryParse(s, out var v)) list.Add((PieceType)v);
            return list.ToArray();
        }

        public void Reset()
        {
            _inSelection = false;
            _prevCanStop = false;
            _stopped = false;
        }
    }
}
