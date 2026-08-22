using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Blukulele.CHE;
using Blukulele.Core;
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
        public const string ProtocolVersion = "1";

        private readonly CoopNet _net;
        private readonly CoopVisuals _vis;
        private readonly CoopShop _shop;
        private readonly CoopIncome _income;
        private readonly MonoBehaviour _runner;

        public Phase Phase { get; private set; } = Phase.Idle;
        public int LocalSeat { get; private set; }     // 0 = P1/host, 1 = P2/guest
        public int RemoteSeat => 1 - LocalSeat;
        public int ActiveSeat { get; private set; }    // whose player-turn it is
        public bool Handshaked { get; private set; }

        private bool _applyingRemote;
        private int _enemyMovesThisRound;
        private readonly int _enemyMovesTarget = 2;
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

        public CoopSession(CoopNet net, CoopVisuals vis, MonoBehaviour runner)
        {
            _net = net;
            _vis = vis;
            _runner = runner;
            _income = new CoopIncome();
            _shop = new CoopShop(s => _net.Send(s));

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
            _enemyMovesThisRound = 0;
            _vis.ClearOwners();

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
                _selMoveHandler = OnLocalMove;
                sel.OnMove = (Action<BasePieceBehaviour, TileBehaviour>)Delegate.Combine(sel.OnMove, _selMoveHandler);
                _selCaptureHandler = OnLocalCapture;
                sel.OnCapture = (Action<BasePieceBehaviour, BasePieceBehaviour, TileBehaviour>)Delegate.Combine(sel.OnCapture, _selCaptureHandler);
                _selPlaceHandler = OnLocalPlaceInGame;
                sel.OnPlacePieceOnBoardInGame = (Action<BasePieceBehaviour, TileBehaviour>)Delegate.Combine(sel.OnPlacePieceOnBoardInGame, _selPlaceHandler);
                _hasPlayedHandler = OnLocalTurnEnded;
                sel.OnPlayerMadeAnActionThatEndsItsTurn = (Action)Delegate.Combine(sel.OnPlayerMadeAnActionThatEndsItsTurn, _hasPlayedHandler);
            }
            if (em != null)
            {
                _enemyMoveHandler = OnEnemyMoved;
                em.OnMove = (Action<BasePieceBehaviour, TileBehaviour>)Delegate.Combine(em.OnMove, _enemyMoveHandler);
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
            if (sel != null)
            {
                if (_selMoveHandler != null) sel.OnMove = (Action<BasePieceBehaviour, TileBehaviour>)Delegate.Remove(sel.OnMove, _selMoveHandler);
                if (_selCaptureHandler != null) sel.OnCapture = (Action<BasePieceBehaviour, BasePieceBehaviour, TileBehaviour>)Delegate.Remove(sel.OnCapture, _selCaptureHandler);
                if (_selPlaceHandler != null) sel.OnPlacePieceOnBoardInGame = (Action<BasePieceBehaviour, TileBehaviour>)Delegate.Remove(sel.OnPlacePieceOnBoardInGame, _selPlaceHandler);
                if (_hasPlayedHandler != null) sel.OnPlayerMadeAnActionThatEndsItsTurn = (Action)Delegate.Remove(sel.OnPlayerMadeAnActionThatEndsItsTurn, _hasPlayedHandler);
            }
            if (em != null && _enemyMoveHandler != null)
                em.OnMove = (Action<BasePieceBehaviour, TileBehaviour>)Delegate.Remove(em.OnMove, _enemyMoveHandler);
            if (gm != null && _stateHandler != null)
                gm.onStateChanged = (Action<State>)Delegate.Remove(gm.onStateChanged, _stateHandler);

            _selMoveHandler = null; _selCaptureHandler = null; _selPlaceHandler = null;
            _hasPlayedHandler = null; _enemyMoveHandler = null; _stateHandler = null;
        }

        private void OnGameStateChanged(State state)
        {
            if (Phase != Phase.Running) return;
            if (state == State.INGAME)
            {
                _enemyMovesThisRound = 0;
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

        private BasePieceBehaviour _pendingCaptureMover;

        private void OnLocalCapture(BasePieceBehaviour mover, BasePieceBehaviour victim, TileBehaviour tile)
        {
            if (_applyingRemote) return;
            _pendingCaptureMover = mover;
        }

        private void OnLocalMove(BasePieceBehaviour piece, TileBehaviour tile)
        {
            if (_applyingRemote || Phase != Phase.Running) return;
            if (!CoopBoard.TryFindTile(tile, out int toR, out int toC)) return;

            // piece.CurrentTile is already the destination by the time OnMove fires from our
            // own re-implementation, so send the origin we recorded at pickup time instead.
            var from = _lastPickupAddress;
            _vis.SetOwner(piece, LocalSeat);
            _net.Send(Msg.Make(Msg.Move, LocalSeat, from.kind, from.a, from.b, toR, toC, 0));
            CoopLog.Debug($"sent move {from.kind}{from.a},{from.b} -> {toR},{toC}");
        }

        private void OnLocalPlaceInGame(BasePieceBehaviour piece, TileBehaviour tile)
        {
            if (_applyingRemote || Phase != Phase.Running) return;
            if (!CoopBoard.TryFindTile(tile, out int toR, out int toC)) return;
            _vis.SetOwner(piece, LocalSeat);
            _net.Send(Msg.Make(Msg.Drop, LocalSeat, _lastPickupAddress.a, toR, toC));
        }

        private (char kind, int a, int b) _lastPickupAddress = (CoopBoard.KindNone, -1, -1);

        public void NotePickup(BasePieceBehaviour piece)
        {
            if (piece == null) return;
            if (CoopBoard.TryLocate(piece, out var k, out var a, out var b))
                _lastPickupAddress = (k, a, b);
        }

        private void OnLocalTurnEnded()
        {
            if (_applyingRemote || Phase != Phase.Running) return;
            AdvanceTurnAfterPlayerMove(local: true);
        }

        // ---------- turn director ----------

        /// <summary>
        /// The game unconditionally runs an enemy turn after every player move. We ride that:
        /// after P1's move we make the enemy skip silently so P2 gets the next window; after
        /// P2's move we let the real enemy play, and FinalBossSkip gives it its second move.
        /// </summary>
        private void AdvanceTurnAfterPlayerMove(bool local)
        {
            var em = SingletonMonoBehaviour<EnemyManager>.Instance;
            var tm = SingletonMonoBehaviour<TurnManager>.Instance;
            if (em == null || tm == null) return;

            if (ActiveSeat == 0)
            {
                // hand the round to P2: suppress this enemy turn entirely
                ActiveSeat = 1;
                em.SkipTurnSilentNoEvents();
                CoopLog.Debug("turn -> P2 (enemy turn suppressed)");
            }
            else
            {
                // P2 done: the enemy now plays twice.
                ActiveSeat = -1;                 // nobody's turn while the enemy acts
                _enemyMovesThisRound = 0;

                if (_net.IsHost)
                {
                    // Host owns the AI (it uses unseeded UnityEngine.Random, so it can't be
                    // reproduced remotely). FinalBossSkip makes PlayerCanPlay re-enter
                    // NextTurn once, which is the game's own double-enemy-turn mechanism.
                    tm.FinalBossSkip = true;
                    CoopLog.Debug("turn -> enemy x2 (host authoritative)");
                }
                else
                {
                    // Guest must NOT run its own AI or it would pick a different move.
                    // Suppress locally and wait for the host's EMOVE messages.
                    em.SkipTurnSilentNoEvents();
                    CoopLog.Debug("turn -> enemy x2 (guest awaits host moves)");
                }
            }
        }

        private void OnEnemyMoved(BasePieceBehaviour piece, TileBehaviour tile)
        {
            if (Phase != Phase.Running) return;
            _enemyMovesThisRound++;

            if (_net.IsHost && !_applyingRemote)
            {
                // OnMove fires before the board is mutated, so CurrentTile is still the origin.
                if (CoopBoard.TryFindTile(piece.CurrentTile, out int fr, out int fc) &&
                    CoopBoard.TryFindTile(tile, out int tr, out int tc))
                {
                    _net.Send(Msg.Make(Msg.EnemyMove, fr, fc, tr, tc));
                    CoopLog.Debug($"sent enemy move {fr},{fc} -> {tr},{tc}");
                }
            }

            if (_enemyMovesThisRound >= _enemyMovesTarget)
            {
                ActiveSeat = 0;                  // back to P1
                CoopLog.Debug("enemy done -> P1");
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
            }
            else if (inBattle && mine)
            {
                sel.CanMove = true;
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
            TickCursor();
            TickChecksum();
            TickLocalBadge();
            if (Phase == Phase.Running) _shop.Tick();
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
                    case Msg.Buy:
                        _shop.ApplyBuy(Msg.I(p, 1));
                        break;
                    case Msg.Reroll:
                        _shop.ApplyReroll();
                        break;
                    case Msg.Limit:
                        _shop.ApplyLimit();
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
                EndSession(restoreSave: false);
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

            _applyingRemote = true;
            try
            {
                _vis.SetOwner(piece, seat);
                CoopBoard.ApplyInGameMove(piece, target, fireTurnEvents: true);
            }
            finally { _applyingRemote = false; }

            AdvanceTurnAfterPlayerMove(local: false);
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

            AdvanceTurnAfterPlayerMove(local: false);
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
                CoopLog.Info("your solo save was restored (co-op runs never overwrite it). Restart the game to reload it.");
            }
            catch (Exception ex) { CoopLog.Error($"could not restore save: {ex.Message}"); }
            finally { _saveSnapshot = null; }
        }

        public string Status()
        {
            if (Phase == Phase.Idle) return "co-op: idle";
            string seat = LocalSeat == 0 ? "P1 (red, host)" : "P2 (blue, guest)";
            string turn = ActiveSeat < 0 ? "enemy" : (ActiveSeat == 0 ? "P1" : "P2");
            return $"co-op: {Phase}, you are {seat}, peer={(_net.Connected ? "connected" : "none")}, turn={turn}, enemyMoves={_enemyMovesThisRound}/{_enemyMovesTarget}";
        }
    }
}
