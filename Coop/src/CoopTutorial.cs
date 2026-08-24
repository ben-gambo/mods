using Blukulele.CHE;
using Blukulele.Core;
using UnityEngine;

namespace Gambonanza.Coop
{
    /// <summary>
    /// Turns the game's tutorial off for as long as the mod is loaded.
    ///
    /// The tutorial drives the game directly - it locks the shop's Quit button, forces
    /// specific gambits into the shop roll (GambitLibrary.cs:302-317), tutorializes tiles and
    /// takes over gamepad navigation. None of that is replicated over the wire, so a host who
    /// has never finished the tutorial desyncs the run within the first wave. It also cannot
    /// be steered from one side: the two clients run their own copies of every step.
    ///
    /// Suppression is by the game's own "already done" flags rather than by destroying
    /// TutorialManager, because AchievementCheckerManager.Start dereferences
    /// SingletonMonoBehaviour&lt;TutorialManager&gt;.Instance without a null check
    /// (AchievementCheckerManager.cs:88) and would throw if the component were gone.
    /// </summary>
    internal static class CoopTutorial
    {
        private static float _sweepClock;
        private static bool _announced;

        public static void Tick()
        {
            // Every frame: ChessDataManager re-reads these from the save on START_RUN and
            // LOAD_RUN (ChessDataManager.cs:470-473), and TutorialManager.Behave is on the same
            // onStateChanged list - a periodic write could lose that race by a frame.
            var cdm = SingletonMonoBehaviour<ChessDataManager>.Instance;
            if (cdm != null)
            {
                cdm.TutorialDonePlaytest = true;
                cdm.TutorialPart1Done = true;
                cdm.ShopTutorialDone = true;
                cdm.SecondShopDone = true;
            }

            _sweepClock -= Time.unscaledDeltaTime;
            if (_sweepClock > 0f) return;
            _sweepClock = 0.5f;

            var tm = SingletonMonoBehaviour<TutorialManager>.Instance;
            if (tm == null) return;

            // The stock-to-board hint is the one step NOT gated by TutorialDonePlaytest being
            // false - it needs it TRUE and fires off TurnManager.OnPlayerTurn, so forcing the
            // flags above would arm it rather than silence it (TutorialManager.cs:281-289).
            tm.UnsubscribeToStockToBoardTutorial();

            // Backstop: anything that slipped through before the flags were forced gets
            // removed rather than left half-driving the board.
            var live = Object.FindObjectsByType<BaseTutorialBehaviourStep>(FindObjectsInactive.Include);
            for (int i = 0; i < live.Length; i++)
            {
                if (live[i] == null) continue;
                CoopLog.Debug($"removing live tutorial step {live[i].GetType().Name}");
                Object.Destroy(live[i].gameObject);
            }

            var canvas = tm.CanvasTutorial;
            if (canvas != null && canvas.activeSelf) canvas.SetActive(false);

            if (!_announced)
            {
                _announced = true;
                CoopLog.Info("tutorial disabled - it cannot be shared between two clients.");
            }
        }
    }
}
