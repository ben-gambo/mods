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
        public const string ProtocolVersion = "6";

        private readonly CoopNet _net;
        private readonly CoopVisuals _vis;
        private readonly CoopShop _shop;
        private readonly CoopWheel _wheel;
        private readonly CoopStartWheel _startWheel;
        private readonly CoopGachapon _gacha;
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
        private string _enemySkipKind = "plain"; // host: why the enemy turn had no move
        private float _enemyPhaseClock;          // watchdog against a stranded enemy phase
        private float _inputStallClock;          // watchdog against a round that never unlocks
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
        private Action<BasePieceBehaviour> _placementHandler;
        private Action<BasePieceBehaviour> _sellPieceHandler;
        private Action<GambitBehaviour> _sellGambitHandler;
        private Action _bribeSkipHandler;
        private Action _demonSkipHandler;
        private Action<BasePieceBehaviour, TileBehaviour> _promoteSignalHandler;
        private bool _localPromoteArmed;   // set by OnPromote, consumed by OnLocalTurnEnded
        private bool _suppressGoRelay;   // set while a remote GO is being applied

        public CoopSession(CoopNet net, CoopVisuals vis, MonoBehaviour runner)
        {
            _net = net;
            _vis = vis;
            _runner = runner;
            _income = new CoopIncome();
            _shop = new CoopShop(s => _net.Send(s));
            _wheel = new CoopWheel(s => _net.Send(s));
            _startWheel = new CoopStartWheel(s => _net.Send(s));
            _gacha = new CoopGachapon(s => _net.Send(s));

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
            _startWheel.Reset();
            _gacha.Reset();
            _vis.ClearTints();
            _vis.HideBadges();
            _vis.HideRemoteCursor();
            UnlockInput();
            _turnBannerText = null;
            _localWaitPending = false;
            _pendingRemotePromotion = null;
            _pendingRemotePromotionTile = null;
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

            // Writing Data.ActiveStrains is NOT enough: on a new run the game never reads it
            // back. StrainManager's runtime state comes from TemporaryStrain/TemporaryBonus,
            // which only the difficulty UI writes (the Data copy happens solely on the LOAD
            // path, StrainManager.CO_LoadStrainsFromSave) - and co-op skips the difficulty
            // UI on both clients. Without this, each client played with whatever its LAST
            // SOLO run had selected: different wheel counts, different strain rules,
            // different everything the strains touch.
            var strainMgr = SingletonMonoBehaviour<StrainManager>.Instance;
            if (strainMgr != null)
            {
                if (activeStrains != null)
                    for (int i = 0; i < strainMgr.TemporaryStrain.Length; i++)
                        strainMgr.TemporaryStrain[i] = i < activeStrains.Length && activeStrains[i];
                if (activeBonus != null)
                    for (int i = 0; i < strainMgr.TemporaryBonus.Length; i++)
                        strainMgr.TemporaryBonus[i] = i < activeBonus.Length && activeBonus[i];
                strainMgr.SetUpStrains();   // Temporary* -> Activated*, the game's own copy
            }
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
            _startWheel.Reset();
            _gacha.Reset();

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

                // Fires on every committed placement rearrangement - stock<->board, both
                // directions, swaps included (SelectionManager.cs:663-679, 1081-1114). The
                // destination is already committed when it fires; the origin is the pickup
                // address NotePickup recorded, because placement pickups go through the same
                // SelectPiece path as battle ones.
                _placementHandler = OnLocalPlacementMove;
                sel.OnMoveInBoardPlacement = (Action<BasePieceBehaviour>)Delegate.Combine(sel.OnMoveInBoardPlacement, _placementHandler);

                // OnPromote is the game's own "this commit opens the promotion picker"
                // signal, and the ONLY reliable one: geometry (dest.IsEnd && pawn) misses the
                // Excalibur gambit's promote-next-to-the-king (not an end tile) and falsely
                // matches the rhythm-skip case, where the game promotes nothing and a peer
                // holding the turn for a choice would wait forever.
                _promoteSignalHandler = (piece, tile) => { if (!_applyingRemote) _localPromoteArmed = true; };
                sel.OnPromote = (Action<BasePieceBehaviour, TileBehaviour>)Delegate.Combine(sel.OnPromote, _promoteSignalHandler);
            }
            var sm = SingletonMonoBehaviour<SellManager>.Instance;
            if (sm != null)
            {
                // Both fire AFTER the vanilla gates passed and the wallet was paid, right
                // before the object is destroyed - the address is still resolvable.
                _sellPieceHandler = OnLocalSellPiece;
                sm.OnSellPiece = (Action<BasePieceBehaviour>)Delegate.Combine(sm.OnSellPiece, _sellPieceHandler);
                _sellGambitHandler = OnLocalSellGambit;
                sm.OnSellGambit = (Action<GambitBehaviour>)Delegate.Combine(sm.OnSellGambit, _sellGambitHandler);
            }
            if (em != null)
            {
                _enemyMoveHandler = OnEnemyMoved;
                em.OnMove = (Action<BasePieceBehaviour, TileBehaviour>)Delegate.Combine(em.OnMove, _enemyMoveHandler);
                // A moveless enemy turn still has side effects the guest must replay - a
                // bribe decrements BribeCount, a demon consumes Demon_Used (EnemyManager
                // _Play's skip branches) - so the host records WHY the turn was empty and
                // ships it with the ESKIP.
                _bribeSkipHandler = () => _enemySkipKind = "bribe";
                em.OnSkipThanksToBribe = (Action)Delegate.Combine(em.OnSkipThanksToBribe, _bribeSkipHandler);
                _demonSkipHandler = () => _enemySkipKind = "demon";
                em.OnDemonSkip = (Action)Delegate.Combine(em.OnDemonSkip, _demonSkipHandler);
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
            if (sel != null && _placementHandler != null)
                sel.OnMoveInBoardPlacement = (Action<BasePieceBehaviour>)Delegate.Remove(sel.OnMoveInBoardPlacement, _placementHandler);
            if (sel != null && _promoteSignalHandler != null)
                sel.OnPromote = (Action<BasePieceBehaviour, TileBehaviour>)Delegate.Remove(sel.OnPromote, _promoteSignalHandler);
            var smU = SingletonMonoBehaviour<SellManager>.Instance;
            if (smU != null)
            {
                if (_sellPieceHandler != null)
                    smU.OnSellPiece = (Action<BasePieceBehaviour>)Delegate.Remove(smU.OnSellPiece, _sellPieceHandler);
                if (_sellGambitHandler != null)
                    smU.OnSellGambit = (Action<GambitBehaviour>)Delegate.Remove(smU.OnSellGambit, _sellGambitHandler);
            }
            if (em != null && _enemyMoveHandler != null)
                em.OnMove = (Action<BasePieceBehaviour, TileBehaviour>)Delegate.Remove(em.OnMove, _enemyMoveHandler);
            if (em != null && _bribeSkipHandler != null)
                em.OnSkipThanksToBribe = (Action)Delegate.Remove(em.OnSkipThanksToBribe, _bribeSkipHandler);
            if (em != null && _demonSkipHandler != null)
                em.OnDemonSkip = (Action)Delegate.Remove(em.OnDemonSkip, _demonSkipHandler);
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
            _placementHandler = null; _sellPieceHandler = null; _sellGambitHandler = null;
            _bribeSkipHandler = null; _demonSkipHandler = null;
            _promoteSignalHandler = null; _localPromoteArmed = false;
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
                // Entering battle from placement is the GO button. Relay it so the peer's
                // client leaves placement too - otherwise one player fights while the other
                // is still rearranging, and every in-battle message lands in the wrong state.
                if (gmS != null && gmS.PreviousState == State.BOARD_PLACEMENT)
                {
                    if (_suppressGoRelay) _suppressGoRelay = false;   // this INGAME is the remote GO
                    else _net.Send(Msg.Make(Msg.Go));
                }
                // A REPLAYED promotion re-enters INGAME from INGAME: this client never showed
                // the PROMOTION state, so the PreviousState guard above cannot catch it the
                // way it catches the promoting player's own client. Resetting the seats here
                // mid-replay handed the round back to P1 and orphaned the enemy phase.
                if (_applyingRemote) return;
                _enemyTurnsDone = 0;
                ActiveSeat = 0;
                ClearStaleTurnFlags();

                // The PLAY_FIRST strain opens every battle with an enemy turn (TurnManager.
                // Reset). On the host that turn is authoritative and its move travels as a
                // normal EMOVE; the guest must not run its own unseeded copy on top of it.
                if (!_net.IsHost && gmS != null && gmS.PreviousState == State.BOARD_PLACEMENT)
                {
                    var strainsPF = SingletonMonoBehaviour<StrainManager>.Instance;
                    var emPF = SingletonMonoBehaviour<EnemyManager>.Instance;
                    if (strainsPF != null && emPF != null && strainsPF.ActivatedStrain[Strain.PLAY_FIRST])
                    {
                        emPF.SkipTurnSilentNoEvents();
                        CoopLog.Debug("PLAY_FIRST opener suppressed (host will send it)");
                    }
                }
            }
            else if (state == State.WIN || state == State.RESULT || state == State.LOSE)
            {
                ClearStaleTurnFlags();
                if (state != State.WIN) _vis.HideBadges();
                if (state == State.WIN) _income.ShowShareOnWinScreen(_runner);
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

                // Classify how the game committed this action - the replay must mirror it.
                //  1: OnPromote fired - the picker is opening, the turn is held for the choice.
                //  2: the game did NOT end the turn (OnHasPlayed suppressed, TurnManager.CanPlay
                //     still true - the Excalibur+rhythm-skip combo does this): the player keeps
                //     playing, so the seats must not move and the peer must not run NextTurn.
                //     Without this, the replay invented an enemy turn the sender never ran and
                //     one client could WIN a wave the other was still playing.
                //  3: an end-tile pawn move whose promotion the game rhythm-skipped - a normal
                //     turn, but the sender never fired OnMove and did fire the skip event.
                //  0: everything else.
                bool promoting = _localPromoteArmed;
                _localPromoteArmed = false;
                var tmK = SingletonMonoBehaviour<TurnManager>.Instance;
                int kind = promoting ? CoopBoard.MovePromoting
                    : (tmK != null && tmK.CanPlay) ? CoopBoard.MoveFree
                    : (dest.IsEnd && dest.PromoteColor == PieceColor.WHITE && piece.PieceHierarchy == PieceHierarchy.PAWN)
                        ? CoopBoard.MoveEndTileSkip
                    : CoopBoard.MoveNormal;

                if (from.kind == CoopBoard.KindStock)
                {
                    // Skydiver opens a promotion for a dropped pawn from inside the placement
                    // event, which runs before this handler - so the state is already
                    // PROMOTION here and the turn is held for the choice, exactly like a
                    // promoting move.
                    var gmD = SingletonMonoBehaviour<GameManager>.Instance;
                    int dropKind = gmD != null && gmD.CurrentState == State.PROMOTION ? CoopBoard.DropPromoting
                        : (tmK != null && tmK.CanPlay) ? CoopBoard.DropFree
                        : CoopBoard.DropNormal;
                    _net.Send(Msg.Make(Msg.Drop, LocalSeat, from.a, toR, toC, dropKind));
                    CoopLog.Debug($"sent drop {from.a} -> {toR},{toC} kind={dropKind}");
                    if (dropKind == CoopBoard.DropPromoting) { _awaitingLocalPromotion = true; return; }
                    if (dropKind == CoopBoard.DropFree) return;
                    AdvanceTurnAfterPlayerAction();
                    return;
                }

                _net.Send(Msg.Make(Msg.Move, LocalSeat, from.kind, from.a, from.b, toR, toC, kind));
                CoopLog.Debug($"sent action {from.kind}{from.a},{from.b} -> {toR},{toC} kind={kind}");

                if (kind == CoopBoard.MovePromoting)
                {
                    // The promotion choice arrives separately via PromotePlayerInto. The peer
                    // holds the turn until it lands, because PromotionManager.Promote runs its
                    // own GameManager.InGame() + TurnManager.EnemyTurn() on both sides.
                    _awaitingLocalPromotion = true;
                    return;
                }
                if (kind == CoopBoard.MoveFree) return;   // the turn is still the sender's
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
        private bool _localWaitPending;

        private void OnLocalWait()
        {
            if (_applyingRemote || Phase != Phase.Running) return;
            // The CanMove gate keeps the wait button dead outside your window, but guard the
            // protocol anyway: an out-of-turn wait must never shift the shared seats.
            if (ActiveSeat != LocalSeat) { CoopLog.Warn("local wait outside your window - not relayed."); return; }
            // Resolution is deferred one tick: Gambit_SleepyPromotion's OnWait handler runs
            // AFTER this one in the multicast and can turn the wait into a held promotion
            // (enemy turn cancelled, picker open). Only once the multicast has finished do we
            // know which kind of wait this actually was.
            _localWaitPending = true;
        }

        /// <summary>Runs the tick after a local wait, once every OnWait listener has spoken.</summary>
        private void ResolveLocalWait()
        {
            _localWaitPending = false;
            var gm = SingletonMonoBehaviour<GameManager>.Instance;

            if (gm == null || gm.CurrentState != State.PROMOTION)
            {
                // An ordinary wait: relay and advance, exactly as before, one frame later -
                // well inside the 0.5s before the wait's enemy turn consumes the armed skip.
                _net.Send(Msg.Make(Msg.Wait, LocalSeat));
                CoopLog.Debug("sent wait");
                AdvanceTurnAfterPlayerAction();
                return;
            }

            // Sleepy Promotion fired: the wait scheduled no enemy turn, a picker is open for
            // a pawn the gambit chose (from an unordered FindObjectsOfType, so the pick CANNOT
            // be reproduced remotely - the address must travel), and the turn is held until
            // the choice. PromotePlayerInto will advance, like any promotion.
            var pm = SingletonMonoBehaviour<PromotionManager>.Instance;
            var pawn = pm != null ? GameRefl.GetField(pm, "m_PieceToPromotePlayer") as BasePieceBehaviour : null;
            if (pawn != null && CoopBoard.TryLocate(pawn, out var k, out var a, out var b))
            {
                _net.Send(Msg.Make(Msg.SleepyWait, LocalSeat, k, a, b));
                _awaitingLocalPromotion = true;
                CoopLog.Debug($"sent sleepy wait, pawn {k}{a},{b}");
            }
            else
            {
                // Should be unreachable (the pawn is on the board by construction). Relay a
                // plain wait so the peer at least keeps its counter; the boards will drift
                // and the checksum will say so.
                CoopLog.Warn("sleepy wait: promoted pawn unaddressable - relaying a plain wait.");
                _net.Send(Msg.Make(Msg.Wait, LocalSeat));
            }
        }

        /// <summary>
        /// A committed placement rearrangement. No turn bookkeeping - placement is free-form
        /// and both players may shuffle pieces at once. First write wins per tile; the 5s
        /// checksum flags the (rare) case of both grabbing the same piece simultaneously.
        /// </summary>
        private void OnLocalPlacementMove(BasePieceBehaviour piece)
        {
            if (_applyingRemote || Phase != Phase.Running || piece == null) return;

            var from = _lastPickupAddress;
            if (from.kind == CoopBoard.KindNone) { CoopLog.Warn("placement move with no pickup address - not relayed."); return; }
            if (!CoopBoard.TryLocate(piece, out var toK, out var toA, out var toB)) return;
            if (from.kind == toK && from.a == toA && from.b == toB) return;   // dropped back in place

            _net.Send(Msg.Make(Msg.Place, LocalSeat, from.kind, from.a, from.b, toK, toA, toB));
            CoopLog.Debug($"sent placement {from.kind}{from.a},{from.b} -> {toK}{toA},{toB}");
        }

        private void OnLocalSellPiece(BasePieceBehaviour piece)
        {
            if (_applyingRemote || Phase != Phase.Running || piece == null) return;
            if (!CoopBoard.TryLocate(piece, out var k, out var a, out var b))
            {
                CoopLog.Warn("sold a piece with no resolvable address - peer will drift.");
                return;
            }
            _net.Send(Msg.Make(Msg.Sell, k, a, b));
            CoopLog.Debug($"sent piece sell {k}{a},{b}");
        }

        private void OnLocalSellGambit(GambitBehaviour gambit)
        {
            if (_applyingRemote || Phase != Phase.Running || gambit == null) return;
            var places = SingletonMonoBehaviour<GambitManager>.Instance?.GambitPlaces;
            if (places == null) return;
            for (int i = 0; i < places.Length; i++)
            {
                if (places[i] != null && ReferenceEquals(places[i].CurrentGambit, gambit))
                {
                    _net.Send(Msg.Make(Msg.SellGambit, i));
                    CoopLog.Debug($"sent gambit sell, slot {i}");
                    return;
                }
            }
            CoopLog.Warn("sold a gambit not found in any slot - peer will drift.");
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
                // ALWAYS arm, promotion included: Promote() calls TurnManager.EnemyTurn()
                // itself, and on a seat-0 promotion that enemy turn is the interleaved one
                // that must NOT play - P2 still gets a window first. (The old promotionDriven
                // exemption assumed promotion always ended the round; a P1 promotion does
                // not, and the unsuppressed turn ran a real enemy move mid-handoff.)
                em.SkipTurnSilentNoEvents();
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
                    // so it cannot be reproduced remotely. The double turn itself is armed in
                    // OnEnemyTurnCompleted, NOT here: FinalBossSkip's only consumer is the
                    // post-turn scan (TurnManager.PlayerCanPlay), and the P1->P2 handoff's
                    // silent scan can still be pending at this moment - on a stall/alt-tab
                    // frame it would resume after this line, consume a flag armed for the
                    // phase, and launch two overlapping real enemy turns.
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
                    _enemyPhaseClock = 0f;
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
            // Capture-and-reset BEFORE the guards: the PLAY_FIRST opener completes at seat 0,
            // and returning early with a "bribe"/"demon" label still loaded would mislabel the
            // next moveless phase turn and make the guest burn a resource it never spent.
            string skipKind = _enemySkipKind;
            _enemySkipKind = "plain";

            if (Phase != Phase.Running || ActiveSeat != -1) return;

            if (_net.IsHost)
            {
                // Arm the second enemy turn HERE, at turn-1 completion: this OnPlayerTurn
                // comes from the turn's own exit scan, whose PlayerCanPlay runs ~0.25s later
                // and is the flag's consumer. Arming this late means no stale handoff scan
                // can still be pending to steal it - and if two scans somehow race, only one
                // can consume the single flag, so exactly one second turn results either way.
                if (_enemyTurnsDone == 0)
                {
                    var tmA = SingletonMonoBehaviour<TurnManager>.Instance;
                    if (tmA != null) tmA.FinalBossSkip = true;
                }
                if (!_moveSentThisEnemyTurn)
                    _net.Send(Msg.Make(Msg.EnemySkip, skipKind));
            }
            _moveSentThisEnemyTurn = false;

            CountEnemyTurn();
        }

        private void CountEnemyTurn()
        {
            _enemyTurnsDone++;
            _enemyPhaseClock = 0f;

            if (_enemyTurnsDone >= _enemyTurnsTarget)
            {
                // The host's PlayerCanPlay consumed FinalBossSkip into NextTurn between the
                // two turns (TurnManager.cs:216-227), incrementing Data.RoundCount - a path
                // the guest never runs (its second turn is just a replay). Mirror it at
                // phase COMPLETION, not after turn 1: a phase that dies mid-way (pat
                // stranding the scan) never ran the host's NextTurn either, and an early
                // increment would drift the counters exactly in those broken corners.
                if (!_net.IsHost) DataManager.Instance.Data.RoundCount++;
                ActiveSeat = 0;
                CoopLog.Debug("enemy phase done -> P1");
            }
        }

        /// <summary>
        /// Last-resort recovery, two flavours.
        ///
        /// CanPlay==true while ActiveSeat==-1 for 5s: the game handed input back but our
        /// count never reached two - restore the round to P1. (5s, not 3: a replayed enemy
        /// promotion legitimately runs seconds of animation with CanPlay already true.)
        ///
        /// CanPlay==false for 8s with no enemy activity: FinalBossSkip got stranded. The game
        /// consumes it ONLY inside TurnManager.PlayerCanPlay (TurnManager.cs:217-227), and the
        /// post-turn scan can exit through OnPat - or through the everything-blocked-but-stock
        /// dead end - without ever reaching it. CanPlay then stays false forever, and the old
        /// watchdog treated that as "game busy" and reset its own clock: the exact soft lock
        /// seen in testing, with no recovery and nothing in the log. Vanilla never hits this
        /// because FinalBossSkip only exists on the final boss; we ride it every round.
        /// The 8s clock is reset by every enemy move and every completed enemy turn, so a slow
        /// animation cannot trip it - only genuine silence can.
        /// </summary>
        private void TickEnemyPhaseWatchdog()
        {
            if (Phase != Phase.Running) return;
            var tm = SingletonMonoBehaviour<TurnManager>.Instance;
            var gm = SingletonMonoBehaviour<GameManager>.Instance;
            if (tm == null || gm == null || gm.CurrentState != State.INGAME)
            {
                _enemyPhaseClock = 0f;
                _inputStallClock = 0f;
                return;
            }

            if (ActiveSeat == -1)
            {
                _inputStallClock = 0f;
                _enemyPhaseClock += Time.unscaledDeltaTime;

                if (tm.CanPlay && _enemyPhaseClock > 5f)
                {
                    _enemyPhaseClock = 0f;
                    // FinalBossSkip may still be armed from the failed phase; left set, the
                    // next silently-skipped handoff turn would consume it and run a rogue
                    // enemy move in the middle of a player's window.
                    ClearStaleTurnFlags();
                    ActiveSeat = 0;
                    CoopLog.Warn("enemy phase stalled - restoring the round to P1.");
                }
                else if (!tm.CanPlay && _enemyPhaseClock > 8f)
                {
                    _enemyPhaseClock = 0f;
                    tm.FinalBossSkip = false;
                    ClearStaleTurnFlags();
                    ActiveSeat = 0;
                    var cdm = SingletonMonoBehaviour<ChessDataManager>.Instance;
                    if (cdm == null || !cdm.Pat)
                        tm.PlayerTurnSilent();   // vanilla re-check; restores CanPlay if a move exists
                    CoopLog.Warn("enemy phase dead (stranded FinalBossSkip) - recovering the round to P1.");
                }
                return;
            }

            _enemyPhaseClock = 0f;

            // A player's window, but the game never gave input back and no pat flow owns the
            // board. After 8s of that, unlock bluntly - a locked client helps nobody.
            if (!tm.CanPlay)
            {
                var cdm = SingletonMonoBehaviour<ChessDataManager>.Instance;
                if (cdm != null && cdm.Pat) { _inputStallClock = 0f; return; }
                _inputStallClock += Time.unscaledDeltaTime;
                if (_inputStallClock > 8f)
                {
                    _inputStallClock = 0f;
                    tm.CanPlay = true;
                    CoopLog.Warn("input never returned for this round - forcing it back.");
                }
            }
            else _inputStallClock = 0f;
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

            var wm = SingletonMonoBehaviour<WaitManager>.Instance;

            if (inBattle && !mine)
            {
                if (sel.CurrentPiece != null) sel.ForceRelease();
                sel.CanMove = false;
                // WaitManager keeps its own can-play flag and WaitButton checks it - without
                // this, the wait button is live on the peer's client during YOUR window.
                if (wm != null) GameRefl.SetField(wm, "m_CanPlay", false);
                _gateHoldingLock = true;
            }
            else
            {
                if (_gateHoldingLock)
                {
                    // Only release OUR CanMove lock, and only once. Writing it true every frame
                    // would stomp the game's own mid-battle lockouts (ComputerPowerGlitch,
                    // chaos mode), which legitimately hold input away for over a second.
                    sel.CanMove = true;
                    _gateHoldingLock = false;
                }
                // The wait gate is enforced EVERY frame while my window is open, not once at
                // release: a replayed wait's EnemyTurn arrives on a 0.1s coroutine
                // (WaitManager.cs:164-174) and its OnEnemyTurn re-falses the flag AFTER a
                // one-shot restore has already fired - which left the seat owner with a dead
                // wait button for the whole window. Re-asserting is safe: WaitButton also
                // checks TurnManager.CanPlay, which stays false while any enemy turn is
                // actually in flight, and chaos mode uses its own separate flag.
                if (inBattle && mine && wm != null)
                    GameRefl.SetField(wm, "m_CanPlay", true);
            }
        }

        private void UnlockInput()
        {
            var sel = SingletonMonoBehaviour<SelectionManager>.Instance;
            if (sel != null) sel.CanMove = true;
            var wm = SingletonMonoBehaviour<WaitManager>.Instance;
            if (wm != null) GameRefl.SetField(wm, "m_CanPlay", true);
        }

        // ---------- per-frame ----------

        public void Tick()
        {
            if (Phase == Phase.Idle) return;
            if (_localWaitPending) ResolveLocalWait();
            TickInputGate();
            TickEnemyPhaseWatchdog();
            TickCursor();
            TickChecksum();
            TickLocalBadge();
            TickTurnBanner();
            if (Phase == Phase.Running) { _shop.Tick(); _wheel.Tick(); _startWheel.Tick(); _gacha.Tick(); }
        }

        // The vanilla banner only knows "Your turn!" / enemy - in co-op it must say WHOSE
        // turn. The game rewrites its text from its own events (TurnIndicator.cs:118,134,151),
        // so ours is enforced per frame while a player window is open; the enemy phase keeps
        // the vanilla enemy text and colours.
        private TMPro.TMP_Text _turnBannerText;

        private void TickTurnBanner()
        {
            if (Phase != Phase.Running || ActiveSeat < 0) return;
            var gm = SingletonMonoBehaviour<GameManager>.Instance;
            if (gm == null || gm.CurrentState != State.INGAME) return;

            if (_turnBannerText == null)
            {
                var ti = UnityEngine.Object.FindAnyObjectByType<TurnIndicator>();
                if (ti == null) return;
                _turnBannerText = GameRefl.GetField(ti, "m_TextIndicator") as TMPro.TMP_Text;
                if (_turnBannerText == null) return;
            }

            string tag = ActiveSeat == 0 ? "<color=#F25555>P1</color>" : "<color=#5B9BFF>P2</color>";
            string want = ActiveSeat == LocalSeat ? $"Your turn! ({tag})" : $"{tag}'s turn!";
            if (_turnBannerText.text != want) _turnBannerText.text = want;
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
            if (_cursorClock < 1f / 30f) return;   // 30 Hz; smoothing on the receiver fills the rest
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
                        HandleRemoteEnemySkip(p);
                        break;
                    case Msg.Promo:
                        HandleRemotePromotion(p);
                        break;
                    case Msg.Wait:
                        HandleRemoteWait();
                        break;
                    case Msg.SleepyWait:
                        HandleRemoteSleepyWait(p);
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
                    case Msg.StartWheel:
                        _startWheel.Apply(p);
                        break;
                    case Msg.Gacha:
                        _gacha.Apply(p);
                        break;
                    case Msg.Place:
                        HandleRemotePlace(p);
                        break;
                    case Msg.Go:
                        HandleRemoteGo();
                        break;
                    case Msg.Sell:
                        HandleRemoteSellPiece(p);
                        break;
                    case Msg.SellGambit:
                        HandleRemoteSellGambit(p);
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
            char fromKind = Msg.S(p, 2).Length > 0 ? Msg.S(p, 2)[0] : CoopBoard.KindBoard;
            int a = Msg.I(p, 3), b = Msg.I(p, 4);
            int toR = Msg.I(p, 5), toC = Msg.I(p, 6);

            var piece = CoopBoard.PieceAt(fromKind, a, b);
            var target = CoopBoard.TileAt(toR, toC);
            if (piece == null || target == null)
            {
                CoopLog.Warn($"remote move unresolved ({fromKind}{a},{b} -> {toR},{toC}) - possible desync.");
                return;
            }

            int moveKind = Msg.I(p, 7);

            _applyingRemote = true;
            try
            {
                _vis.SetOwner(piece, seat);
                // The sender's commit fired OnPromote before its move events; Chronobreak,
                // Benediction and friends listen there and must charge on both clients.
                if (moveKind == CoopBoard.MovePromoting)
                    SingletonMonoBehaviour<SelectionManager>.Instance?.OnPromote?.Invoke(piece, target);
                CoopBoard.ApplyInGameMove(piece, target, moveKind);
            }
            finally { _applyingRemote = false; }

            if (moveKind == CoopBoard.MovePromoting)
            {
                // Hold the round until the peer's chosen piece type arrives.
                _pendingRemotePromotion = piece;
                _pendingRemotePromotionTile = target;
                CoopLog.Debug("remote pawn promoting - awaiting choice");
                return;
            }
            if (moveKind == CoopBoard.MoveFree) return;   // the sender's turn is not over

            AdvanceTurnAfterPlayerAction();
        }

        private void HandleRemoteDrop(string[] p)
        {
            int seat = Msg.I(p, 1);
            int stockIdx = Msg.I(p, 2);
            int toR = Msg.I(p, 3), toC = Msg.I(p, 4);
            int dropKind = Msg.I(p, 5);
            var piece = CoopBoard.PieceAt(CoopBoard.KindStock, stockIdx, 0);
            var target = CoopBoard.TileAt(toR, toC);
            if (piece == null || target == null)
            {
                CoopLog.Warn($"remote drop unresolved (stock {stockIdx} -> {toR},{toC})");
                return;
            }

            // A promoting drop means Skydiver: the sender's picker is open, the choice will
            // arrive as PROMO. The placement event must still fire here - dozens of gambits
            // listen to it - but OUR copy of Skydiver must not react and open a second
            // picker, so its handler is lifted off the event for the duration of the apply.
            var skydiverHooks = dropKind == CoopBoard.DropPromoting ? UnhookSkydiver() : null;
            _applyingRemote = true;
            try
            {
                _vis.SetOwner(piece, seat);
                CoopBoard.ApplyStockDrop(piece, target, dropKind);
            }
            finally
            {
                _applyingRemote = false;
                RehookSkydiver(skydiverHooks);
            }

            if (dropKind == CoopBoard.DropPromoting)
            {
                _pendingRemotePromotion = piece;
                _pendingRemotePromotionTile = target;
                CoopLog.Debug("remote skydiver drop - awaiting promotion choice");
                return;
            }
            if (dropKind == CoopBoard.DropFree) return;

            AdvanceTurnAfterPlayerAction();
        }

        /// <summary>Removes every live GambitSkydiver.Effect from the placement event; returns
        /// the delegates so RehookSkydiver can restore them. Delegate.Remove matches on
        /// target+method, so a reconstructed delegate removes the gambit's own subscription.</summary>
        private static System.Collections.Generic.List<Delegate> UnhookSkydiver()
        {
            var sel = SingletonMonoBehaviour<SelectionManager>.Instance;
            if (sel == null) return null;
            var mi = GameRefl.Method(typeof(GambitSkydiver), "Effect",
                typeof(BasePieceBehaviour), typeof(TileBehaviour));
            if (mi == null) return null;

            var removed = new System.Collections.Generic.List<Delegate>();
            var instances = UnityEngine.Object.FindObjectsByType<GambitSkydiver>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var g in instances)
            {
                if (g == null) continue;
                var del = Delegate.CreateDelegate(typeof(Action<BasePieceBehaviour, TileBehaviour>), g, mi, false);
                if (del == null) continue;
                sel.OnPlacePieceOnBoardInGame = (Action<BasePieceBehaviour, TileBehaviour>)Delegate.Remove(sel.OnPlacePieceOnBoardInGame, del);
                removed.Add(del);
            }
            return removed;
        }

        private static void RehookSkydiver(System.Collections.Generic.List<Delegate> hooks)
        {
            if (hooks == null || hooks.Count == 0) return;
            var sel = SingletonMonoBehaviour<SelectionManager>.Instance;
            if (sel == null) return;
            foreach (var del in hooks)
                sel.OnPlacePieceOnBoardInGame = (Action<BasePieceBehaviour, TileBehaviour>)Delegate.Combine(sel.OnPlacePieceOnBoardInGame, del);
        }

        private void HandleRemotePlace(string[] p)
        {
            char fromK = Msg.S(p, 2).Length > 0 ? Msg.S(p, 2)[0] : CoopBoard.KindBoard;
            int fromA = Msg.I(p, 3), fromB = Msg.I(p, 4);
            char toK = Msg.S(p, 5).Length > 0 ? Msg.S(p, 5)[0] : CoopBoard.KindBoard;
            int toA = Msg.I(p, 6), toB = Msg.I(p, 7);

            var piece = CoopBoard.PieceAt(fromK, fromA, fromB);
            var target = CoopBoard.Resolve(toK, toA, toB);
            if (piece == null || target == null)
            {
                CoopLog.Warn($"remote placement unresolved ({fromK}{fromA},{fromB} -> {toK}{toA},{toB}) - possible desync.");
                return;
            }

            _applyingRemote = true;
            try { CoopBoard.ApplyPlacement(piece, target); }
            finally { _applyingRemote = false; }
        }

        /// <summary>
        /// The peer pressed GO. Mirror it through CanvasPreparation.Ready() - the vanilla
        /// route into battle - forcing its private m_Ready gate, which on the peer's client
        /// was legitimately open. A locally held piece is released first so the transition
        /// does not tear a drag in half.
        /// </summary>
        private void HandleRemoteGo()
        {
            var gm = SingletonMonoBehaviour<GameManager>.Instance;
            if (gm == null || gm.CurrentState != State.BOARD_PLACEMENT) return;   // already in battle

            var sel = SingletonMonoBehaviour<SelectionManager>.Instance;
            if (sel != null && sel.CurrentPiece != null) sel.ForceRelease();

            var prep = SingletonMonoBehaviour<CanvasPreparation>.Instance;
            if (prep == null) { CoopLog.Warn("GO arrived but no CanvasPreparation"); return; }

            _suppressGoRelay = true;
            GameRefl.SetField(prep, "m_Ready", true);
            prep.Ready();
            CoopLog.Debug("applied remote GO");
        }

        private void HandleRemoteSellPiece(string[] p)
        {
            char k = Msg.S(p, 1).Length > 0 ? Msg.S(p, 1)[0] : CoopBoard.KindBoard;
            int a = Msg.I(p, 2), b = Msg.I(p, 3);
            var piece = CoopBoard.PieceAt(k, a, b);
            if (piece == null)
            {
                CoopLog.Warn($"remote piece sell unresolved ({k}{a},{b}) - possible desync.");
                return;
            }
            var smS = SingletonMonoBehaviour<SellManager>.Instance;
            if (smS != null && !smS.CanSell())
                CoopLog.Warn("DESYNC RISK: peer sold a piece but selling is refused here (last piece).");

            _applyingRemote = true;
            try { piece.Sell(); }   // the vanilla path: wallet, effects, destroy
            catch (Exception ex) { CoopLog.Error($"piece sell replay failed: {ex.Message}"); }
            finally { _applyingRemote = false; }
        }

        private void HandleRemoteSellGambit(string[] p)
        {
            int slot = Msg.I(p, 1, -1);
            var places = SingletonMonoBehaviour<GambitManager>.Instance?.GambitPlaces;
            var gambit = places != null && slot >= 0 && slot < places.Length && places[slot] != null
                ? places[slot].CurrentGambit : null;
            if (gambit == null)
            {
                CoopLog.Warn($"remote gambit sell unresolved (slot {slot}) - possible desync.");
                return;
            }
            _applyingRemote = true;
            try { gambit.Sell(); }
            catch (Exception ex) { CoopLog.Error($"gambit sell replay failed: {ex.Message}"); }
            finally { _applyingRemote = false; }
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

        /// <summary>
        /// Replays the peer's Wait through WaitManager.Wait() itself, because a wait is not
        /// just a seat change: Wait() decrements the shared 3-per-battle counter, feeds the
        /// wait-gambit influence, and - critically - is what triggers TurnManager.EnemyTurn()
        /// via its own coroutine (WaitManager.cs:143-174). The old handler skipped all of
        /// that, so a P2 wait left the host's game waiting for an action that never came: no
        /// enemy turn ran, FinalBossSkip sat armed, and the round was dead.
        ///
        /// On the host (peer waited at seat 1) the replayed wait's enemy turn is the real,
        /// authoritative one - the double turn arms at turn-1 completion as always. On the
        /// guest (peer waited at seat 0) AdvanceTurnAfterPlayerAction arms the silent skip
        /// that the replayed wait's enemy turn consumes, exactly like a local wait. Both
        /// clients run the identical vanilla path.
        /// </summary>
        private void HandleRemoteWait()
        {
            var gm = SingletonMonoBehaviour<GameManager>.Instance;
            var wm = SingletonMonoBehaviour<WaitManager>.Instance;
            if (gm == null || wm == null || gm.CurrentState != State.INGAME)
            {
                CoopLog.Warn("remote wait arrived outside battle - possible desync.");
                return;
            }

            // The costly-wait strain charges coins in WaitButton, upstream of WaitManager
            // (WaitButton.cs:145-155) - mirror the charge or the wallets drift. The waiter's
            // client paid exactly when it had the coins; ours agrees because coins are synced.
            var strains = SingletonMonoBehaviour<StrainManager>.Instance;
            var cdm = SingletonMonoBehaviour<ChessDataManager>.Instance;
            if (strains != null && cdm != null && strains.ActivatedStrain[Strain.COSTLY_WAIT]
                && cdm.Coins >= strains.WaitCost && !wm.CannotWaitBecauseOfChaosMode)
                cdm.DecreaseCoin(strains.WaitCost);

            // Wait() is gated on WaitManager's private m_CanPlay, which on this client can
            // legitimately be false (it only returns true on OnPlayerTurn, and the co-op
            // handoff turns are silent). The peer's gate was open or it could not have waited.
            GameRefl.SetField(wm, "m_CanPlay", true);

            _applyingRemote = true;
            try { wm.Wait(); }
            finally { _applyingRemote = false; }

            // Wait() only locks TurnManager.CanPlay from its 0.1s coroutine - close that
            // sliver now, or a very fast local action lands between two queued enemy turns
            // and the second one runs unsuppressed. The suppressed turn's own scan restores
            // CanPlay ~0.85s from now, same pacing as a move handoff.
            var tmW = SingletonMonoBehaviour<TurnManager>.Instance;
            if (tmW != null) tmW.CanPlay = false;

            AdvanceTurnAfterPlayerAction();
            CoopLog.Debug("applied remote wait");
        }

        /// <summary>
        /// A moveless enemy turn, replayed. The host's _Play exited through a skip branch
        /// that decremented a bribe or consumed a demon and then called PlayerTurn
        /// (EnemyManager.cs:149-170) - which also ticks the crumble counter through
        /// OnPlayerCheckIfCanPlay. The guest replays the same exit: mirror the resource,
        /// then run TurnManager.PlayerTurn(), whose OnPlayerTurn is what counts the turn -
        /// counting here as well would double-count.
        /// </summary>
        private void HandleRemoteEnemySkip(string[] p)
        {
            if (_net.IsHost || ActiveSeat != -1) return;

            string kind = Msg.S(p, 1);
            var cdm = SingletonMonoBehaviour<ChessDataManager>.Instance;
            if (kind == "bribe" && cdm != null && cdm.BribeCount > 0)
                cdm.BribeCount--;
            else if (kind == "demon")
                DataManager.Instance.Data.Demon_Used = false;

            var tm = SingletonMonoBehaviour<TurnManager>.Instance;
            if (tm != null) tm.PlayerTurn();
            else CountEnemyTurn();   // never expected; keep the count alive regardless
        }

        /// <summary>
        /// The peer's wait triggered Sleepy Promotion: no enemy turn, a picker open on THEIR
        /// screen for the pawn this message names, their turn held until the choice. This
        /// client must NOT replay WaitManager.Wait() - our own copy of the gambit would fire
        /// off its OnWait and open a second, independently-random picker - so the wait's
        /// gameplay bookkeeping is mirrored by hand and the pawn is staged as the pending
        /// promotion for the PROMO that follows, the same path a promoting move uses.
        /// </summary>
        private void HandleRemoteSleepyWait(string[] p)
        {
            var gm = SingletonMonoBehaviour<GameManager>.Instance;
            var wm = SingletonMonoBehaviour<WaitManager>.Instance;
            if (gm == null || wm == null || gm.CurrentState != State.INGAME)
            {
                CoopLog.Warn("sleepy wait arrived outside battle - possible desync.");
                return;
            }

            // The same wallet mirror a plain remote wait gets.
            var strains = SingletonMonoBehaviour<StrainManager>.Instance;
            var cdm = SingletonMonoBehaviour<ChessDataManager>.Instance;
            if (strains != null && cdm != null && strains.ActivatedStrain[Strain.COSTLY_WAIT]
                && cdm.Coins >= strains.WaitCost && !wm.CannotWaitBecauseOfChaosMode)
                cdm.DecreaseCoin(strains.WaitCost);

            // WaitManager.Wait()'s gameplay effects, minus OnWait and minus the enemy turn
            // (the sender's was cancelled by the gambit): counter, label, wait influence.
            if (!wm.CoconutJuiceGambit) wm.CurrentWait--;
            wm.OnUpdateText?.Invoke(wm.CurrentWait);
            SingletonMonoBehaviour<BuildBalanceManager>.Instance?.IncreaseGambitInfluence(Gambit_Focus.WAIT, 0.03f);

            char k = Msg.S(p, 2).Length > 0 ? Msg.S(p, 2)[0] : CoopBoard.KindBoard;
            var pawn = CoopBoard.PieceAt(k, Msg.I(p, 3), Msg.I(p, 4));
            if (pawn == null)
            {
                CoopLog.Warn("sleepy wait: pawn unresolved - the coming promotion will be dropped.");
                return;
            }
            pawn.HighlightEffect();   // the sender's client highlights it too
            _pendingRemotePromotion = pawn;
            _pendingRemotePromotionTile = pawn.CurrentTile;
            CoopLog.Debug($"remote sleepy wait staged pawn {k}{Msg.I(p, 3)},{Msg.I(p, 4)} - awaiting choice");
            // No seat advance: HandleRemotePromotion advances when the choice lands.
        }

        private void HandleRemoteEnemyMove(string[] p)
        {
            if (_net.IsHost) return;    // host generated it locally
            int fr = Msg.I(p, 1), fc = Msg.I(p, 2), tr = Msg.I(p, 3), tc = Msg.I(p, 4);
            _applyingRemote = true;
            try { CoopBoard.ApplyEnemyMove(fr, fc, tr, tc); }
            finally { _applyingRemote = false; }
            _enemyPhaseClock = 0f;
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
            // The host legitimately runs ahead by one mid-enemy-phase (its FinalBossSkip
            // NextTurn lands before the guest's phase-completion mirror), so only compare
            // between phases.
            else if (ActiveSeat != -1 && DataManager.Instance.Data.RoundCount != round)
                CoopLog.Warn($"round drift: host {round}, local {DataManager.Instance.Data.RoundCount}");
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
