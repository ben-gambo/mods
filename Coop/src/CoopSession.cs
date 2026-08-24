using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Blukulele.CHE;
using Blukulele.Core;
using Blukulele.SaveSystem;
using UnityEngine;

namespace Gambonanza.Coop
{
    internal enum Phase { Idle, Lobby, Running }

    /// <summary>
    /// The co-op brain. Host is authoritative: it owns the seed, the enemy AI and the wallet.
    /// The guest mirrors. Turn order inside a round is P1 -> P2 -> enemy -> enemy.
    /// </summary>
    internal sealed class CoopSession
    {
        public const string ProtocolVersion = "2";

        private readonly CoopNet _net;
        private readonly CoopVisuals _vis;
        private readonly CoopShop _shop;
        private readonly CoopWheel _wheel;
        private readonly CoopIncome _income;
        private readonly MonoBehaviour _runner;

        public Phase Phase { get; private set; } = Phase.Idle;
        public int LocalSeat { get; private set; }     // 0 = P1/host, 1 = P2/guest
        public int RemoteSeat => 1 - LocalSeat;
        public int ActiveSeat { get; private set; }    // whose player-turn it is
        public bool Handshaked { get; private set; }

        private bool _applyingRemote;
        private bool _incompatible;          // sticky: peer failed the handshake
        private int _enemyTurnsDone;             // enemy turns COMPLETED this round (move or skip)
        private readonly int _enemyTurnsTarget = 2;
        private bool _moveSentThisEnemyTurn;     // host: did this enemy turn actually move a piece?
        private float _enemyPhaseClock;          // watchdog against a stranded enemy phase
        private bool _gateHoldingLock;
        private float _cursorClock;
        private float _checkClock;
        private TileBehaviour _lastLocalHover;
        private string _saveSnapshot;

        // enemy-move observation (host side)
        private Action<BasePieceBehaviour, TileBehaviour> _enemyMoveHandler;
        private Action<BasePieceBehaviour, TileBehaviour> _selMoveHandler;
        private Action<BasePieceBehaviour, BasePieceBehaviour, TileBehaviour> _selCaptureHandler;
        private Action<BasePieceBehaviour, TileBehaviour> _selPlaceHandler;
        private Action _hasPlayedHandler;
        private Action<State> _stateHandler;
        private Action _playerTurnHandler;       // fires on EVERY exit from EnemyManager._Play
        private Action _waitHandler;             // the in-battle Wait button
        private Action<PieceType> _promoteIntoHandler;

        public CoopSession(CoopNet net, CoopVisuals vis, MonoBehaviour runner)
        {
            _net = net;
            _vis = vis;
            _runner = runner;
            _income = new CoopIncome();
            _shop = new CoopShop(s => _net.Send(s));
            _wheel = new CoopWheel(s => _net.Send(s));

            _net.OnPeerJoined += HandlePeerJoined;
            _net.OnPeerLeft += HandlePeerLeft;
            _net.OnStateMessage += HandleStateMessage;
            _net.OnCursorMessage += HandleCursorMessage;
        }

        public CoopShop Shop => _shop;
        public bool IsMyTurn => Phase != Phase.Running || ActiveSeat == LocalSeat;

        // ---------- lifecycle ----------

        private void HandlePeerJoined()
        {
            LocalSeat = _net.IsHost ? 0 : 1;
            Phase = Phase.Lobby;
            _net.Send(Msg.Make(_net.IsHost ? Msg.Hello : Msg.HelloAck,
                ProtocolVersion, CoopMod.ModVersion, Application.version, SteamPersona()));
            CoopLog.Info($"peer joined - you are {(LocalSeat == 0 ? "P1 (host)" : "P2 (guest)")}");
        }

        private void HandlePeerLeft()
        {
            CoopLog.Warn("peer disconnected - co-op session ended, returning to solo rules.");
            EndSession(restoreSave: true);
        }

        private static string SteamPersona()
        {
            try { return Steamworks.SteamFriends.GetPersonaName(); }
            catch { return "player"; }
        }

        public void EndSession(bool restoreSave)
        {
            if (Phase == Phase.Idle) return;
            Phase = Phase.Idle;
            Handshaked = false;
            _income.Enabled = false;
            _income.Uninstall();
            UnhookGameEvents();
            _shop.Unhook();
            _wheel.Reset();
            _vis.ClearTints();
            _vis.HideBadges();
            _vis.HideRemoteCursor();
            UnlockInput();
            if (restoreSave) RestoreSaveSnapshot();
            CoopLog.Info("co-op session closed.");
        }

        // ---------- host: start a synced run ----------

        public void HostStartRun()
        {
            if (!_net.Connected) { CoopLog.Warn("no peer connected."); return; }
            if (!_net.IsHost) { CoopLog.Warn("only the host can start the run."); return; }
            if (!Handshaked || _incompatible) { CoopLog.Warn("peer has not completed a compatible handshake yet."); return; }

            var data = DataManager.Instance?.Data;
            if (data == null) { CoopLog.Error("no save data."); return; }

            uint seed = (uint)UnityEngine.Random.Range(1f, 4.2949673E+09f);
            SnapshotSave();

            var settings = DataManager.Instance.SettingData;
            var payload = Msg.Make(Msg.RunStart,
                seed,
                (int)data.CurrentDifficulty,
                data.CurrentStrain,
                Msg.EncodeBools(data.ActiveStrains),
                Msg.EncodeBools(data.ActiveBonus),
                settings != null && settings.BetterAI ? 1 : 0,
                string.Join(",", data.GambitUnlocked ?? new List<string>()));
            _net.Send(payload);

            BeginRun(seed, data.CurrentDifficulty, data.CurrentStrain,
                     data.ActiveStrains, data.ActiveBonus,
                     settings != null && settings.BetterAI,
                     data.GambitUnlocked);
        }

        private void BeginRun(uint seed, DIFFICULTY difficulty, int strain,
                              bool[] activeStrains, bool[] activeBonus, bool betterAI,
                              List<string> unlockedGambits)
        {
            var data = DataManager.Instance.Data;
            data.CurrentDifficulty = difficulty;
            data.CurrentStrain = strain;
            if (activeStrains != null && activeStrains.Length > 0) data.ActiveStrains = activeStrains;
            if (activeBonus != null && activeBonus.Length > 0) data.ActiveBonus = activeBonus;
            if (unlockedGambits != null) data.GambitUnlocked = new List<string>(unlockedGambits);

            // BetterAI changes both the enemy AI branch and crumble timing - force it equal.
            if (DataManager.Instance.SettingData != null)
                DataManager.Instance.SettingData.BetterAI = betterAI;

            Phase = Phase.Running;
            Handshaked = true;
            ActiveSeat = 0;
            _enemyTurnsDone = 0;
            _vis.ClearOwners();
            _wheel.Reset();

            _income.Install();
            _income.Enabled = true;
            HookGameEvents();
            _shop.Hook();

            CoopLog.Info($"starting co-op run (seed {seed}) - you are {(LocalSeat == 0 ? "P1" : "P2")}");
            SingletonMonoBehaviour<GameManager>.Instance.StartNewSeededRun(seed);
        }

        // ---------- game event wiring ----------

        private void HookGameEvents()
        {
            UnhookGameEvents();
            var sel = SingletonMonoBehaviour<SelectionManager>.Instance;
            var em = SingletonMonoBehaviour<EnemyManager>.Instance;
            var gm = SingletonMonoBehaviour<GameManager>.Instance;

            if (sel != null)
            {
                // OnPlayerMadeAnActionThatEndsItsTurn is the ONLY signal that fires for every
                // committed action. OnMove sits in an else-branch that promotion skips
                // (SelectionManager.cs:823-881), so hooking OnMove would silently drop every
                // promoting pawn move.
                _hasPlayedHandler = OnLocalTurnEnded;
                sel.OnPlayerMadeAnActionThatEndsItsTurn = (Action)Delegate.Combine(sel.OnPlayerMadeAnActionThatEndsItsTurn, _hasPlayedHandler);
            }
            if (em != null)
            {
                _enemyMoveHandler = OnEnemyMoved;
                em.OnMove = (Action<BasePieceBehaviour, TileBehaviour>)Delegate.Combine(em.OnMove, _enemyMoveHandler);
            }
            var tm = SingletonMonoBehaviour<TurnManager>.Instance;
            if (tm != null)
            {
                // Fires once per completed enemy turn on every path out of _Play - real move,
                // gambit skip, bribe, demon, or can't-play. Counting OnMove instead would
                // strand the enemy phase forever whenever a turn was skipped.
                _playerTurnHandler = OnEnemyTurnCompleted;
                tm.OnPlayerTurn = (Action)Delegate.Combine(tm.OnPlayerTurn, _playerTurnHandler);
            }
            var wm = SingletonMonoBehaviour<WaitManager>.Instance;
            if (wm != null)
            {
                // The Wait button calls TurnManager.EnemyTurn() directly, firing none of the
                // SelectionManager turn events - without this it bypasses the director.
                _waitHandler = OnLocalWait;
                wm.OnWait = (Action)Delegate.Combine(wm.OnWait, _waitHandler);
            }
            var pm = SingletonMonoBehaviour<PromotionManager>.Instance;
            if (pm != null)
            {
                _promoteIntoHandler = OnLocalPromoteInto;
                pm.PromotePlayerInto = (Action<PieceType>)Delegate.Combine(pm.PromotePlayerInto, _promoteIntoHandler);
            }
            if (gm != null)
            {
                _stateHandler = OnGameStateChanged;
                gm.onStateChanged = (Action<State>)Delegate.Combine(gm.onStateChanged, _stateHandler);
            }
        }

        private void UnhookGameEvents()
        {
            var sel = SingletonMonoBehaviour<SelectionManager>.Instance;
            var em = SingletonMonoBehaviour<EnemyManager>.Instance;
            var gm = SingletonMonoBehaviour<GameManager>.Instance;
            if (sel != null && _hasPlayedHandler != null)
                sel.OnPlayerMadeAnActionThatEndsItsTurn = (Action)Delegate.Remove(sel.OnPlayerMadeAnActionThatEndsItsTurn, _hasPlayedHandler);
            if (em != null && _enemyMoveHandler != null)
                em.OnMove = (Action<BasePieceBehaviour, TileBehaviour>)Delegate.Remove(em.OnMove, _enemyMoveHandler);
            if (gm != null && _stateHandler != null)
                gm.onStateChanged = (Action<State>)Delegate.Remove(gm.onStateChanged, _stateHandler);
            var tmU = SingletonMonoBehaviour<TurnManager>.Instance;
            if (tmU != null && _playerTurnHandler != null)
                tmU.OnPlayerTurn = (Action)Delegate.Remove(tmU.OnPlayerTurn, _playerTurnHandler);
            var wmU = SingletonMonoBehaviour<WaitManager>.Instance;
            if (wmU != null && _waitHandler != null)
                wmU.OnWait = (Action)Delegate.Remove(wmU.OnWait, _waitHandler);
            var pmU = SingletonMonoBehaviour<PromotionManager>.Instance;
            if (pmU != null && _promoteIntoHandler != null)
                pmU.PromotePlayerInto = (Action<PieceType>)Delegate.Remove(pmU.PromotePlayerInto, _promoteIntoHandler);

            _selMoveHandler = null; _selCaptureHandler = null; _selPlaceHandler = null;
            _hasPlayedHandler = null; _enemyMoveHandler = null; _stateHandler = null;
            _playerTurnHandler = null; _waitHandler = null; _promoteIntoHandler = null;
        }

        private void OnGameStateChanged(State state)
        {
            if (Phase != Phase.Running) return;

            // The game re-enters INGAME on pause/settings/run-info close and after promotion,
            // none of which are replicated. Every game-side listener guards against exactly
            // this (TurnManager.cs:80, EnemyManager.cs:97); without the guard, opening the
            // pause menu would silently rewrite one client's idea of whose turn it is.
            var gmS = SingletonMonoBehaviour<GameManager>.Instance;
            if (gmS != null && (gmS.PreviousState == State.PAUSE
                             || gmS.PreviousState == State.RUN_INFO
                             || gmS.PreviousState == State.SETTINGS
                             || gmS.PreviousState == State.PROMOTION)) return;

            if (state == State.INGAME)
            {
                _enemyTurnsDone = 0;
                ActiveSeat = 0;
                ClearStaleTurnFlags();
            }
            else if (state == State.WIN || state == State.RESULT || state == State.LOSE)
            {
                ClearStaleTurnFlags();
                if (state != State.WIN) _vis.HideBadges();
            }
            else if (state == State.MENU)
            {
                _vis.HideBadges();
            }
        }

        /// <summary>
        /// Clears the turn flags this mod sets, because the game can strand them.
        /// EnemyManager._Play returns on its wave-clear check (EnemyManager.cs:123-134)
        /// BEFORE it consumes the skip flags, and Behave only resets m_SkipTurn on
        /// WIN/LOSE/RESULT (EnemyManager.cs:97-100) - never m_SkipTurnSilent or
        /// m_SkipTurnSilentNoEvents. Capturing the last enemy piece would otherwise leave
        /// them set and silently eat the enemy's first turn of the next wave. FinalBossSkip
        /// can likewise survive a wave that ends mid enemy-phase and grant a spurious turn.
        /// </summary>
        private void ClearStaleTurnFlags()
        {
            var em = SingletonMonoBehaviour<EnemyManager>.Instance;
            if (em != null)
            {
                GameRefl.SetField(em, "m_SkipTurnSilent", false);
                GameRefl.SetField(em, "m_SkipTurnSilentNoEvents", false);
                GameRefl.SetField(em, "m_SkipTurn", false);
            }
            var tm = SingletonMonoBehaviour<TurnManager>.Instance;
            if (tm != null) tm.FinalBossSkip = false;
        }

        // ---------- local action capture ----------

        private (char kind, int a, int b) _lastPickupAddress = (CoopBoard.KindNone, -1, -1);
        private bool _awaitingLocalPromotion;

        public void NotePickup(BasePieceBehaviour piece)
        {
            if (piece == null) return;
            if (CoopBoard.TryLocate(piece, out var k, out var a, out var b))
                _lastPickupAddress = (k, a, b);
        }

        /// <summary>
        /// The single detection point for a committed local action.
        /// SelectionManager fires OnPlayerMadeAnActionThatEndsItsTurn (SelectionManager.cs:893)
        /// for EVERY committed action, whereas OnMove (:879) sits in an else-branch that a
        /// promoting pawn skips entirely - hooking OnMove would silently drop those moves and
        /// desync the boards for the rest of the run.
        /// </summary>
        private void OnLocalTurnEnded()
        {
            if (_applyingRemote || Phase != Phase.Running) return;

            var sel = SingletonMonoBehaviour<SelectionManager>.Instance;
            var piece = sel != null ? sel.CurrentPiece : null;      // cleanup runs after this event
            var dest = piece != null ? piece.CurrentTile : null;

            if (piece != null && dest != null && CoopBoard.TryFindTile(dest, out int toR, out int toC))
            {
                _vis.SetOwner(piece, LocalSeat);
                var from = _lastPickupAddress;

                bool promoting = dest.IsEnd
                                 && dest.PromoteColor == PieceColor.WHITE
                                 && piece.PieceHierarchy == PieceHierarchy.PAWN;

                if (from.kind == CoopBoard.KindStock)
                    _net.Send(Msg.Make(Msg.Drop, LocalSeat, from.a, toR, toC));
                else
                    _net.Send(Msg.Make(Msg.Move, LocalSeat, from.kind, from.a, from.b, toR, toC, promoting ? 1 : 0));

                CoopLog.Debug($"sent action {from.kind}{from.a},{from.b} -> {toR},{toC} promo={promoting}");

                if (promoting)
                {
                    // The promotion choice arrives separately via PromotePlayerInto. The peer
                    // holds the turn until it lands, because PromotionManager.Promote runs its
                    // own GameManager.InGame() + TurnManager.EnemyTurn() on both sides.
                    _awaitingLocalPromotion = true;
                    return;
                }
            }

            AdvanceTurnAfterPlayerAction();
        }

        /// <summary>The local player picked a promotion piece; mirror the choice.</summary>
        private void OnLocalPromoteInto(PieceType type)
        {
            if (_applyingRemote || Phase != Phase.Running) return;
            _net.Send(Msg.Make(Msg.Promo, (int)type));
            CoopLog.Debug($"sent promotion choice {type}");
            _awaitingLocalPromotion = false;
            // Promote() itself calls TurnManager.EnemyTurn(), so the enemy phase is already
            // under way; just record whose round it is.
            AdvanceTurnAfterPlayerAction(promotionDriven: true);
        }

        /// <summary>
        /// The Wait button calls TurnManager.EnemyTurn() directly (WaitManager.cs:143) and
        /// fires none of the SelectionManager turn events, so without this hook it would
        /// bypass the director entirely and let the guest run its own enemy AI.
        /// </summary>
        private void OnLocalWait()
        {
            if (_applyingRemote || Phase != Phase.Running) return;
            _net.Send(Msg.Make(Msg.Wait, LocalSeat));
            CoopLog.Debug("sent wait");
            AdvanceTurnAfterPlayerAction();
        }

        // ---------- turn director ----------

        /// <summary>
        /// The game runs an enemy turn after every player action. We ride that: after P1 acts
        /// we make the enemy skip silently so P2 gets the next window; after P2 acts the enemy
        /// really plays, and FinalBossSkip buys it a second move.
        /// </summary>
        private void AdvanceTurnAfterPlayerAction(bool promotionDriven = false)
        {
            var em = SingletonMonoBehaviour<EnemyManager>.Instance;
            var tm = SingletonMonoBehaviour<TurnManager>.Instance;
            if (em == null || tm == null) return;

            if (ActiveSeat == 0)
            {
                ActiveSeat = 1;
                if (!promotionDriven) em.SkipTurnSilentNoEvents();
                CoopLog.Debug("turn -> P2 (enemy turn suppressed)");
            }
            else
            {
                ActiveSeat = -1;                 // nobody's turn while the enemy acts
                _enemyTurnsDone = 0;
                _moveSentThisEnemyTurn = false;
                _enemyPhaseClock = 0f;

                if (_net.IsHost)
                {
                    // Host owns the AI - its move selection uses unseeded UnityEngine.Random,
                    // so it cannot be reproduced remotely. FinalBossSkip makes PlayerCanPlay
                    // re-enter NextTurn once: the game's own double-enemy-turn mechanism.
                    tm.FinalBossSkip = true;
                    CoopLog.Debug("turn -> enemy x2 (host authoritative)");
                }
                else
                {
                    em.SkipTurnSilentNoEvents();
                    CoopLog.Debug("turn -> enemy x2 (guest awaits host moves)");
                }
            }
        }

        private void OnEnemyMoved(BasePieceBehaviour piece, TileBehaviour tile)
        {
            if (Phase != Phase.Running) return;

            if (_net.IsHost && !_applyingRemote)
            {
                // OnMove fires before the board is mutated, so CurrentTile is still the origin.
                if (CoopBoard.TryFindTile(piece.CurrentTile, out int fr, out int fc) &&
                    CoopBoard.TryFindTile(tile, out int tr, out int tc))
                {
                    _net.Send(Msg.Make(Msg.EnemyMove, fr, fc, tr, tc));
                    _moveSentThisEnemyTurn = true;
                    CoopLog.Debug($"sent enemy move {fr},{fc} -> {tr},{tc}");
                }
            }
        }

        /// <summary>
        /// Fires once per COMPLETED enemy turn, on every path out of EnemyManager._Play -
        /// a real move, a gambit skip, a bribe, a demon skip, or can't-play. Counting
        /// EnemyManager.OnMove instead would strand the enemy phase forever the moment a turn
        /// was skipped (gambits like Banana Peel, Fork and Mime all call SkipTurn), holding
        /// CanMove false on both clients - an unrecoverable soft-lock.
        /// </summary>
        private void OnEnemyTurnCompleted()
        {
            if (Phase != Phase.Running || ActiveSeat != -1) return;

            if (_net.IsHost && !_moveSentThisEnemyTurn)
                _net.Send(Msg.Make(Msg.EnemySkip, "skip"));   // keep the guest's count in step
            _moveSentThisEnemyTurn = false;

            CountEnemyTurn();
        }

        private void CountEnemyTurn()
        {
            _enemyTurnsDone++;
            _enemyPhaseClock = 0f;
            if (_enemyTurnsDone >= _enemyTurnsTarget)
            {
                ActiveSeat = 0;
                CoopLog.Debug("enemy phase done -> P1");
            }
        }

        /// <summary>
        /// Last-resort recovery. If the enemy phase somehow never completes but the game has
        /// already handed input back, restore the round rather than leave both players locked
        /// out with no way forward.
        /// </summary>
        private void TickEnemyPhaseWatchdog()
        {
            if (Phase != Phase.Running || ActiveSeat != -1) return;
            var tm = SingletonMonoBehaviour<TurnManager>.Instance;
            var gm = SingletonMonoBehaviour<GameManager>.Instance;
            if (tm == null || gm == null || gm.CurrentState != State.INGAME) { _enemyPhaseClock = 0f; return; }
            if (!tm.CanPlay) { _enemyPhaseClock = 0f; return; }

            _enemyPhaseClock += Time.unscaledDeltaTime;
            if (_enemyPhaseClock > 3f)
            {
                _enemyPhaseClock = 0f;
                ActiveSeat = 0;
                CoopLog.Warn("enemy phase stalled - restoring the round to P1.");
            }
        }

        // ---------- input gating ----------

        public void TickInputGate()
        {
            if (Phase != Phase.Running) return;
            var sel = SingletonMonoBehaviour<SelectionManager>.Instance;
            if (sel == null) return;

            bool mine = ActiveSeat == LocalSeat;
            var gm = SingletonMonoBehaviour<GameManager>.Instance;
            bool inBattle = gm != null && gm.CurrentState == State.INGAME;

            if (inBattle && !mine)
            {
                if (sel.CurrentPiece != null) sel.ForceRelease();
                sel.CanMove = false;
                _gateHoldingLock = true;
            }
            else if (_gateHoldingLock)
            {
                // Only release OUR lock, and only once. Writing CanMove=true every frame would
                // stomp the game's own mid-battle lockouts (ComputerPowerGlitch, chaos mode),
                // which legitimately hold input away for over a second.
                sel.CanMove = true;
                _gateHoldingLock = false;
            }
        }

        private void UnlockInput()
        {
            var sel = SingletonMonoBehaviour<SelectionManager>.Instance;
            if (sel != null) sel.CanMove = true;
        }

        // ---------- per-frame ----------

        public void Tick()
        {
            if (Phase == Phase.Idle) return;
            TickInputGate();
            TickEnemyPhaseWatchdog();
            TickCursor();
            TickChecksum();
            TickLocalBadge();
            if (Phase == Phase.Running) { _shop.Tick(); _wheel.Tick(); }
        }

        private void TickLocalBadge()
        {
            if (Phase != Phase.Running) return;
            var sel = SingletonMonoBehaviour<SelectionManager>.Instance;
            if (sel == null) return;
            var piece = sel.CurrentPiece;
            var tile = piece != null ? piece.CurrentTile : null;
            if (tile != _lastLocalHover)
            {
                _lastLocalHover = tile;
                _vis.ShowBadge(false, tile, LocalSeat);
            }
        }

        private void TickCursor()
        {
            if (Phase != Phase.Running || !_net.Connected) return;
            _cursorClock += Time.unscaledDeltaTime;
            if (_cursorClock < 0.1f) return;      // 10 Hz
            _cursorClock = 0f;

            var sel = SingletonMonoBehaviour<SelectionManager>.Instance;
            if (sel == null) return;
            var p = sel.PointerPosition;

            int hr = -1, hc = -1;
            var held = sel.CurrentPiece;
            if (held != null && held.CurrentTile != null)
                CoopBoard.TryFindTile(held.CurrentTile, out hr, out hc);

            _net.Send(Msg.Make(Msg.Cursor, LocalSeat,
                p.x.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                p.y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                hr, hc), reliable: false, channel: CoopNet.ChannelCursor);
        }

        private void TickChecksum()
        {
            if (Phase != Phase.Running || !_net.IsHost || !_net.Connected) return;
            _checkClock += Time.unscaledDeltaTime;
            if (_checkClock < 5f) return;
            _checkClock = 0f;
            var cdm = SingletonMonoBehaviour<ChessDataManager>.Instance;
            if (cdm == null) return;
            _net.Send(Msg.Make(Msg.Check, cdm.CurrentWave, DataManager.Instance.Data.RoundCount,
                cdm.Coins, CoopBoard.Digest()));
        }

        // ---------- inbound ----------

        private void HandleCursorMessage(string payload)
        {
            var p = Msg.Split(payload);
            if (p.Length == 0 || p[0] != Msg.Cursor) return;
            if (Phase != Phase.Running) return;
            int seat = Msg.I(p, 1);
            float x = Msg.F(p, 2), y = Msg.F(p, 3);
            int hr = Msg.I(p, 4, -1), hc = Msg.I(p, 5, -1);

            _vis.ShowRemoteCursor(new Vector2(x, y), seat);
            var tile = hr >= 0 ? CoopBoard.TileAt(hr, hc) : null;
            _vis.ShowBadge(true, tile, seat);
        }

        private void HandleStateMessage(string payload)
        {
            var p = Msg.Split(payload);
            if (p.Length == 0) return;
            // A peer that failed the handshake must not be able to drive this client.
            if (_incompatible && p[0] != Msg.Bye) return;
            if (!Handshaked && p[0] != Msg.Hello && p[0] != Msg.HelloAck && p[0] != Msg.Bye) return;
            try
            {
                switch (p[0])
                {
                    case Msg.Hello:
                    case Msg.HelloAck:
                        HandleHello(p);
                        break;
                    case Msg.RunStart:
                        HandleRunStart(p);
                        break;
                    case Msg.Move:
                        HandleRemoteMove(p);
                        break;
                    case Msg.Drop:
                        HandleRemoteDrop(p);
                        break;
                    case Msg.EnemyMove:
                        HandleRemoteEnemyMove(p);
                        break;
                    case Msg.EnemySkip:
                        if (!_net.IsHost && ActiveSeat == -1) CountEnemyTurn();
                        break;
                    case Msg.Promo:
                        HandleRemotePromotion(p);
                        break;
                    case Msg.Wait:
                        HandleRemoteWait();
                        break;
                    case Msg.Buy:
                        _shop.ApplyBuy(Msg.I(p, 1));
                        break;
                    case Msg.Reroll:
                        _shop.ApplyReroll();
                        break;
                    case Msg.Limit:
                        _shop.ApplyLimit();
                        break;
                    case Msg.Wheel:
                        _wheel.Apply(p);
                        break;
                    case Msg.Check:
                        HandleCheck(p);
                        break;
                    case Msg.Bye:
                        CoopLog.Warn("peer ended the co-op session.");
                        EndSession(restoreSave: true);
                        break;
                }
            }
            catch (Exception ex) { CoopLog.Error($"message '{p[0]}' failed: {ex.Message}"); }
        }

        private void HandleHello(string[] p)
        {
            string proto = Msg.S(p, 1), mod = Msg.S(p, 2), game = Msg.S(p, 3), who = Msg.S(p, 4);
            if (proto != ProtocolVersion)
            {
                CoopLog.Error($"protocol mismatch: peer speaks {proto}, we speak {ProtocolVersion}. Update the Co-op mod on both machines.");
                _incompatible = true;
                EndSession(restoreSave: false);
                _net.LeaveLobby();
                return;
            }
            if (mod != CoopMod.ModVersion)
                CoopLog.Warn($"co-op mod version differs (peer {mod}, local {CoopMod.ModVersion}) - desyncs are likely.");
            if (game != Application.version)
                CoopLog.Warn($"game version differs (peer {game}, local {Application.version}) - shop pools may diverge.");

            Handshaked = true;
            LocalSeat = _net.IsHost ? 0 : 1;
            if (p[0] == Msg.Hello)
                _net.Send(Msg.Make(Msg.HelloAck, ProtocolVersion, CoopMod.ModVersion, Application.version, SteamPersona()));
            CoopLog.Info($"handshake ok with {who}. {(LocalSeat == 0 ? "You are P1 (red)." : "You are P2 (blue).")}");
        }

        private void HandleRunStart(string[] p)
        {
            if (_net.IsHost) return;    // host already started locally
            uint seed = Msg.U(p, 1);
            var diff = (DIFFICULTY)Msg.I(p, 2);
            int strain = Msg.I(p, 3);
            var activeStrains = Msg.DecodeBools(Msg.S(p, 4));
            var activeBonus = Msg.DecodeBools(Msg.S(p, 5));
            bool betterAI = Msg.I(p, 6) == 1;
            var unlocks = new List<string>(Msg.S(p, 7).Split(','));
            unlocks.RemoveAll(string.IsNullOrEmpty);

            SnapshotSave();
            CoopLog.Info("host started a co-op run - syncing seed and unlocks.");
            BeginRun(seed, diff, strain, activeStrains, activeBonus, betterAI, unlocks);
        }

        private void HandleRemoteMove(string[] p)
        {
            int seat = Msg.I(p, 1);
            char kind = Msg.S(p, 2).Length > 0 ? Msg.S(p, 2)[0] : CoopBoard.KindBoard;
            int a = Msg.I(p, 3), b = Msg.I(p, 4);
            int toR = Msg.I(p, 5), toC = Msg.I(p, 6);

            var piece = CoopBoard.PieceAt(kind, a, b);
            var target = CoopBoard.TileAt(toR, toC);
            if (piece == null || target == null)
            {
                CoopLog.Warn($"remote move unresolved ({kind}{a},{b} -> {toR},{toC}) - possible desync.");
                return;
            }

            bool promoting = Msg.I(p, 7) == 1;

            _applyingRemote = true;
            try
            {
                _vis.SetOwner(piece, seat);
                CoopBoard.ApplyInGameMove(piece, target, fireTurnEvents: !promoting);
            }
            finally { _applyingRemote = false; }

            if (promoting)
            {
                // Hold the round until the peer's chosen piece type arrives.
                _pendingRemotePromotion = piece;
                _pendingRemotePromotionTile = target;
                CoopLog.Debug("remote pawn promoting - awaiting choice");
                return;
            }

            AdvanceTurnAfterPlayerAction();
        }

        private void HandleRemoteDrop(string[] p)
        {
            int seat = Msg.I(p, 1);
            int stockIdx = Msg.I(p, 2);
            int toR = Msg.I(p, 3), toC = Msg.I(p, 4);
            var piece = CoopBoard.PieceAt(CoopBoard.KindStock, stockIdx, 0);
            var target = CoopBoard.TileAt(toR, toC);
            if (piece == null || target == null)
            {
                CoopLog.Warn($"remote drop unresolved (stock {stockIdx} -> {toR},{toC})");
                return;
            }
            _applyingRemote = true;
            try
            {
                _vis.SetOwner(piece, seat);
                CoopBoard.ApplyStockDrop(piece, target, fireTurnEvents: true);
            }
            finally { _applyingRemote = false; }

            AdvanceTurnAfterPlayerAction();
        }

        /// <summary>Mirrors the peer's promotion choice through the game's own promote path.</summary>
        private void HandleRemotePromotion(string[] p)
        {
            var type = (PieceType)Msg.I(p, 1);
            var pm = SingletonMonoBehaviour<PromotionManager>.Instance;
            if (pm == null) { CoopLog.Warn("promotion: no PromotionManager"); return; }
            if (_pendingRemotePromotion == null)
            {
                CoopLog.Warn("promotion arrived with no pawn waiting - possible desync.");
                return;
            }
            _applyingRemote = true;
            try
            {
                pm.Initialize(_pendingRemotePromotion, _pendingRemotePromotionTile);
                pm.Promote(type);   // also runs GameManager.InGame() + TurnManager.EnemyTurn()
                CoopLog.Debug($"applied remote promotion to {type}");
            }
            catch (Exception ex) { CoopLog.Error($"promotion replay failed: {ex.Message}"); }
            finally { _applyingRemote = false; _pendingRemotePromotion = null; _pendingRemotePromotionTile = null; }

            AdvanceTurnAfterPlayerAction(promotionDriven: true);
        }

        private BasePieceBehaviour _pendingRemotePromotion;
        private TileBehaviour _pendingRemotePromotionTile;

        private void HandleRemoteWait()
        {
            var em = SingletonMonoBehaviour<EnemyManager>.Instance;
            // Mirror the peer's Wait: our own client must not run its enemy AI for it.
            if (!_net.IsHost && em != null) em.SkipTurnSilentNoEvents();
            AdvanceTurnAfterPlayerAction();
        }

        private void HandleRemoteEnemyMove(string[] p)
        {
            if (_net.IsHost) return;    // host generated it locally
            int fr = Msg.I(p, 1), fc = Msg.I(p, 2), tr = Msg.I(p, 3), tc = Msg.I(p, 4);
            _applyingRemote = true;
            try { CoopBoard.ApplyEnemyMove(fr, fc, tr, tc); }
            finally { _applyingRemote = false; }
        }

        private void HandleCheck(string[] p)
        {
            if (_net.IsHost) return;
            int wave = Msg.I(p, 1), round = Msg.I(p, 2), coins = Msg.I(p, 3), hash = Msg.I(p, 4);
            var cdm = SingletonMonoBehaviour<ChessDataManager>.Instance;
            if (cdm == null) return;
            int localHash = CoopBoard.Digest();
            if (localHash != hash)
                CoopLog.Warn($"DESYNC: board differs from host (wave {wave}, host coins {coins}, local coins {cdm.Coins}).");
            else if (cdm.Coins != coins)
                CoopLog.Warn($"coin drift: host {coins}, local {cdm.Coins}");
        }

        // ---------- save protection ----------

        private void SnapshotSave()
        {
            try
            {
                var path = Application.persistentDataPath + "/save.json";
                if (System.IO.File.Exists(path))
                {
                    _saveSnapshot = System.IO.File.ReadAllText(path);
                    CoopLog.Debug("solo save snapshotted");
                }
            }
            catch (Exception ex) { CoopLog.Warn($"could not snapshot save: {ex.Message}"); }
        }

        private void RestoreSaveSnapshot()
        {
            if (string.IsNullOrEmpty(_saveSnapshot)) return;
            try
            {
                var path = Application.persistentDataPath + "/save.json";
                System.IO.File.WriteAllText(path, _saveSnapshot);

                // Restoring the file alone is not enough: DataManager still holds the Data
                // object the co-op run mutated, and the game re-Stores it after every turn and
                // on every state change (SaveManager.cs:19-24) - which would immediately
                // overwrite the file we just put back. Reload it into memory as well.
                var reloaded = Save.Load<Data>();
                if (reloaded != null)
                {
                    DataManager.Instance.Data = reloaded;
                    Save.Store(reloaded);
                    CoopLog.Info("your solo save was restored, in memory and on disk.");
                }
                else
                {
                    CoopLog.Warn("solo save file restored, but reloading it failed - restart before playing solo.");
                }
            }
            catch (Exception ex) { CoopLog.Error($"could not restore save: {ex.Message}"); }
            finally { _saveSnapshot = null; }
        }

        public string Status()
        {
            if (Phase == Phase.Idle) return "co-op: idle";
            string seat = LocalSeat == 0 ? "P1 (red, host)" : "P2 (blue, guest)";
            string turn = ActiveSeat < 0 ? "enemy" : (ActiveSeat == 0 ? "P1" : "P2");
            return $"co-op: {Phase}, you are {seat}, peer={(_net.Connected ? "connected" : "none")}, turn={turn}, enemyTurns={_enemyTurnsDone}/{_enemyTurnsTarget}";
        }
    }
}
