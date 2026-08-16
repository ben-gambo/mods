using System;
using System.Collections;
using Blukulele.CHE;
using Blukulele.Core;
using UnityEngine;

namespace Gambonanza.ImpatientGambit
{
    /// <summary>
    /// Runtime behaviour of the Impatient Gambit.
    ///
    /// Mental model:
    /// - A run is 5 stages of 5 games. <c>ChessDataManager.CurrentWave</c> is the
    ///   flat 0-based index into those 25 games, and vanilla considers a game a
    ///   boss fight when <c>(CurrentWave + 1) % 5 == 0</c> - i.e. waves 4, 9, 14,
    ///   19 and 24. (The long-run strain pushes the run to 30 waves; we read
    ///   <c>LastWave</c> instead of hardcoding either number.)
    /// - So "only ever fight bosses" is just: whenever we are between games, snap
    ///   CurrentWave forward to the last wave of the stage we are standing in.
    ///   Everything downstream - which boss spawns, which wave the pieces come
    ///   from, the reward tier, the save file - is derived from CurrentWave, so
    ///   moving that one number moves the whole game with it.
    ///
    /// Two things do NOT follow automatically, and are handled here:
    /// - The board only grows a column when CurrentWave lands exactly on a
    ///   multiple of 5 (BoardManager.Behave), which is a wave we now always skip
    ///   over. <see cref="SyncBoardColumns"/> catches the board up by hand.
    /// - Income. Vanilla has no multiplier hook, so we watch
    ///   <c>ChessDataManager.OnCoinIncreased</c>, measure how much was just
    ///   granted, and top it up to <see cref="IncomeMultiplier"/>x. That covers
    ///   every source - win rewards, captures, interest, gold tiles, other
    ///   gambits - because all of them funnel through IncreaseCoin.
    ///
    /// This component exists exactly as long as the player owns the card, so
    /// buying it turns the effect on and selling it turns the effect off, with
    /// no extra bookkeeping.
    /// </summary>
    public sealed class GambitImpatient : BaseGambit
    {
        /// <summary>Every coin the player earns is paid out this many times over.</summary>
        public const int IncomeMultiplier = 4;

        /// <summary>
        /// Exact size of a win payout that <see cref="ImpatientWinRow"/> has
        /// already multiplied on the win screen. The general-purpose multiplier
        /// below lets a gain of precisely this size through untouched, so the
        /// two never both take a swing at the same coins.
        /// </summary>
        internal static int PendingWinPayout;

        private bool _subscribed;
        private ImpatientWinRow _winRow;
        private Coroutine _counterRefresh;

        // Coin total as of the last time we looked. The difference between this
        // and the live total is "what the game just granted", which is the amount
        // we owe a multiplier on.
        private int _lastCoins;

        // Set while we are paying out our own top-up, so the OnCoinIncreased that
        // our IncreaseCoin call raises does not recurse.
        private bool _payingOut;

        private void Start()
        {
            Subscribe();
            AttachWinRow();

            // "Upon bought": if the card was bought between two games, the skip
            // applies right now, to the game the player is about to start.
            // Bought mid-game (the Combo strain can grant a gambit during board
            // placement), it simply waits for the next shop.
            SkipToBoss();
        }

        private void OnEnable()
        {
            Subscribe();
        }

        private void OnDestroy()
        {
            Unsubscribe();
            PendingWinPayout = 0;
            if (_winRow != null) Destroy(_winRow);
        }

        /// <summary>
        /// Puts the extra breakdown row on the win screen. WinCanvas sits in the
        /// scene disabled between games, so it has to be dug out of every loaded
        /// object rather than found among the active ones.
        /// </summary>
        private void AttachWinRow()
        {
            if (_winRow != null) return;
            try
            {
                foreach (var canvas in Resources.FindObjectsOfTypeAll<WinCanvas>())
                {
                    if (canvas == null || !canvas.gameObject.scene.IsValid()) continue; // prefab, not the live one
                    _winRow = canvas.GetComponent<ImpatientWinRow>();
                    if (_winRow == null) _winRow = canvas.gameObject.AddComponent<ImpatientWinRow>();
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[ImpatientGambit] could not attach the win-screen row: " + ex.Message);
            }
        }

        // --- wiring ---------------------------------------------------------

        private void Subscribe()
        {
            if (_subscribed) return;
            if (!SingletonMonoBehaviour<GameManager>.IsCreated()) return;
            if (!SingletonMonoBehaviour<ChessDataManager>.IsCreated()) return;

            var game = SingletonMonoBehaviour<GameManager>.Instance;
            var chess = SingletonMonoBehaviour<ChessDataManager>.Instance;
            if (game == null || chess == null) return;

            // Vanilla combines instead of using +=, because these are public
            // Action fields rather than C# events. Mirror that style.
            game.onStateChanged = (Action<State>)Delegate.Combine(
                game.onStateChanged, new Action<State>(OnStateChanged));
            chess.OnCoinIncreased = (Action)Delegate.Combine(
                chess.OnCoinIncreased, new Action(OnCoinIncreased));
            chess.OnCoinDecreased = (Action)Delegate.Combine(
                chess.OnCoinDecreased, new Action(ResyncCoins));

            _lastCoins = chess.Coins;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            _subscribed = false;

            // Always unsubscribe. A handler left on a manager would keep calling
            // into a destroyed card after the gambit is sold or the run ends.
            if (SingletonMonoBehaviour<GameManager>.IsCreated())
            {
                var game = SingletonMonoBehaviour<GameManager>.Instance;
                if (game != null)
                    game.onStateChanged = (Action<State>)Delegate.Remove(
                        game.onStateChanged, new Action<State>(OnStateChanged));
            }

            if (SingletonMonoBehaviour<ChessDataManager>.IsCreated())
            {
                var chess = SingletonMonoBehaviour<ChessDataManager>.Instance;
                if (chess != null)
                {
                    chess.OnCoinIncreased = (Action)Delegate.Remove(
                        chess.OnCoinIncreased, new Action(OnCoinIncreased));
                    chess.OnCoinDecreased = (Action)Delegate.Remove(
                        chess.OnCoinDecreased, new Action(ResyncCoins));
                }
            }
        }

        // --- boss skipping --------------------------------------------------

        private void OnStateChanged(State state)
        {
            var game = SingletonMonoBehaviour<GameManager>.Instance;
            if (game == null) return;

            // Every vanilla manager ignores transitions that come back from an
            // overlay, because the "previous" state is the pause screen rather
            // than a real step of the run. Mirror that, or opening the run info
            // panel mid-shop would look like a fresh shop to us.
            if (game.PreviousState == State.PAUSE || game.PreviousState == State.RUN_INFO) return;

            if (state == State.BOARD_PLACEMENT)
            {
                SyncBoardColumns();
                ResyncCoins();
                PendingWinPayout = 0; // the win screen is long gone; never carry a stale claim
                return;
            }

            if (IsBetweenGames(state))
            {
                SkipToBoss();
                ResyncCoins();
            }
        }

        /// <summary>
        /// States in which no game is in progress, so CurrentWave can be moved
        /// without pulling the rug out from under a board that already exists.
        ///
        /// SHOP is the one that matters - it is on the path after every single
        /// win - but the gachapon/wheel/pachinko screens can also hand out a
        /// gambit, and the card should take effect the moment it is acquired.
        /// WIN itself is deliberately absent: WinCanvas reads CurrentWave to work
        /// out the payout tier, and it does so on a delay, so moving the wave
        /// there would quietly pay the player the wrong reward for the boss they
        /// just beat.
        /// </summary>
        private static bool IsBetweenGames(State state)
        {
            return state == State.SHOP
                || state == State.TILE_PLACEMENT
                || state == State.GACHAPON
                || state == State.WHEEL_GAME
                || state == State.PACHINKO;
        }

        private void SkipToBoss()
        {
            var chess = SingletonMonoBehaviour<ChessDataManager>.Instance;
            if (chess == null) return;
            if (!IsBetweenGames(CurrentState())) return;

            int step = StepPerRound();
            if (step <= 0) return;

            int wave = chess.CurrentWave;

            // Wave 0 is the run's opening game, and vanilla never treats it as a
            // boss (IsBossLevel explicitly requires CurrentWave > 0). Owning this
            // card from the very start of a run must not turn game one into a
            // boss fight - the skip only ever begins once that game is behind us.
            if (wave <= 0) return;

            // Last wave of the stage we are currently in.
            int bossWave = wave - (wave % step) + (step - 1);

            // LastWave is 25 normally and 30 under the long-run strain; the final
            // playable index is one below it. Never skip past the end of the run.
            int finalWave = chess.LastWave - 1;
            if (bossWave > finalWave) bossWave = finalWave;

            if (bossWave <= wave) return; // already standing on this stage's boss

            chess.CurrentWave = bossWave;
            Debug.Log($"[ImpatientGambit] skipped wave {wave} -> {bossWave} (stage {bossWave / step + 1} boss).");
            Trigger();
        }

        /// <summary>
        /// Vanilla widens the board in BoardManager.Behave when CurrentWave is an
        /// exact multiple of StepPerRound - the first game of a new stage, which
        /// this gambit always skips over. Left alone the board would stay at its
        /// starting width for the whole run and boss waves would try to spawn
        /// pieces on columns that were never made visible.
        ///
        /// Runs on BOARD_PLACEMENT, after BoardManager's own handler, and well
        /// inside the 12s boss cinematic that precedes piece spawning.
        /// </summary>
        private void SyncBoardColumns()
        {
            if (!SingletonMonoBehaviour<BoardManager>.IsCreated()) return;
            var board = SingletonMonoBehaviour<BoardManager>.Instance;
            var chess = SingletonMonoBehaviour<ChessDataManager>.Instance;
            if (board == null || chess == null) return;

            int step = StepPerRound();
            if (step <= 0) return;

            int wanted = chess.CurrentWave / step;

            // AddColumn caps itself at 3 columns, so the counter guard is only a
            // belt-and-braces stop in case a game update changes that cap.
            int guard = 0;
            while (board.ColumnAdded < wanted && guard++ < 8)
            {
                int before = board.ColumnAdded;
                board.AddColumn();
                if (board.ColumnAdded == before) break; // vanilla refused: at the cap
            }
        }

        // --- income multiplier ----------------------------------------------

        private void OnCoinIncreased()
        {
            if (_payingOut) return;

            var chess = SingletonMonoBehaviour<ChessDataManager>.Instance;
            if (chess == null) return;

            int gained = chess.Coins - _lastCoins;
            if (gained <= 0)
            {
                // Coins moved without an increase we can attribute - most likely
                // the flat reset at the start of a stage. Re-baseline and pay
                // nothing; erring this way can only ever under-pay, never dupe.
                _lastCoins = chess.Coins;
                return;
            }

            if (gained == PendingWinPayout)
            {
                // The win screen already multiplied this one and showed it as its
                // own breakdown row. Match on the exact amount so unrelated income
                // arriving while the win screen is up still gets multiplied here.
                PendingWinPayout = 0;
                _lastCoins = chess.Coins;
                return;
            }

            _payingOut = true;
            try
            {
                chess.IncreaseCoin(gained * (IncomeMultiplier - 1));
            }
            finally
            {
                _payingOut = false;
            }

            _lastCoins = chess.Coins;
            RefreshCoinCounter();
            Trigger();
        }

        /// <summary>
        /// Vanilla only redraws the coin counter as MoneyAnimationManager's coins
        /// land, and it spawns one coin per unit of the *original* amount, so our
        /// top-up would otherwise sit there unshown until some later event
        /// repainted it. Snap the counter to the truth once the animation has had
        /// time to play out.
        /// </summary>
        private void RefreshCoinCounter()
        {
            if (_counterRefresh != null) StopCoroutine(_counterRefresh);
            _counterRefresh = StartCoroutine(CO_RefreshCoinCounter());
        }

        private IEnumerator CO_RefreshCoinCounter()
        {
            // Long enough for a full coin flight (spawn + travel is under ~1.6s).
            yield return new WaitForSeconds(1.8f);
            _counterRefresh = null;

            var chess = SingletonMonoBehaviour<ChessDataManager>.Instance;
            if (chess == null) yield break;
            try { chess.IncreaseTextCoin(setCorrectValue: true); } catch { }
        }

        private void ResyncCoins()
        {
            var chess = SingletonMonoBehaviour<ChessDataManager>.Instance;
            if (chess != null) _lastCoins = chess.Coins;
        }

        // --- helpers ---------------------------------------------------------

        private static State CurrentState()
        {
            var game = SingletonMonoBehaviour<GameManager>.Instance;
            return game != null ? game.CurrentState : State.MENU;
        }

        private static int StepPerRound()
        {
            if (!SingletonMonoBehaviour<Library>.IsCreated()) return 0;
            var library = SingletonMonoBehaviour<Library>.Instance;
            return library != null ? library.StepPerRound : 0;
        }

        public override void Trigger()
        {
            try { VisualEffect(); } catch { }
        }
    }
}
