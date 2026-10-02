using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Blukulele.CHE;
using Blukulele.Core;
using Blukulele.Module.Audio;
using Gambonanza.CashbackStrain;
using UnityEngine;

internal static class Program
{
    private static int _checks;
    private static void Main(string[] args)
    {
        Run("expires in original order and pays after native cleanup", ExpirationOrder);
        Run("under threshold remains unpaid until its expiry", Threshold);
        Run("disabled vanilla expiry and non-WIN states do not pay", NoExpiry);
        Run("Collector uses the value after earlier WIN callbacks", CollectorOrdering);
        Run("Boss Tooth keeps its value before native zeroing", ToothValue);
        Run("cashback invokes no sell-only effects and works in Chaos", NotASale);
        Run("same-frame manual sale receives no second payment", ManualSale);
        Run("duplicate native callbacks pay once per object", DuplicateCallbacks);
        Run("late native subscriptions are intercepted", LateSubscription);
        Run("destroyed wrappers are pruned after native unsubscribe misses", DestroyedPruning);
        Run("a stale captured callback skips a destroyed native target", DestroyedStaleDispatch);
        Run("deactivation restores original callbacks and stale dispatch stays harmless", Deactivation);
        Run("externally removed callbacks are never resurrected", ExternalRemoval);
        Run("callbacks added by other mods remain in their positions", GraphChanges);
        Run("unowned gambits receive no cashback", Ownership);
        Run("simultaneous owned expiries each receive cashback", MultipleExpiries);
        Run("loaded counter pays on its natural next expiry", ContinuedRun);
        Run("cosmetic failure cannot cancel credit or later callbacks", FeedbackFailure);
        Run("native exceptions propagate without retrying or paying", NativeFailure);
        Run("manager replacement restores the previous event field", ManagerReplacement);
        Run("zero-value expiry creates no coin event", ZeroValue);
        Run("enabled mod pays on King without a bonus selection or API", EnabledModOnKing);
        Run("mod hot toggles detach immediately and never duplicate hosts", ModHotToggle);
        Run("persistent host binds when native managers appear after startup", ModEarlyStartup);
        Run("one enabled host pays across successive runs", ModSuccessiveRuns);
        Run("mod follows replacement game and sell managers", ModManagerReplacement);
        if (args.Length == 2 && args[0] == "--assembly")
            Run("production assembly keeps its identity without a strain API dependency", () =>
            {
                var assembly = Assembly.LoadFile(System.IO.Path.GetFullPath(args[1]));
                Equal("Gambonanza.CashbackStrain", assembly.GetName().Name);
                False(assembly.GetReferencedAssemblies().Any(reference => reference.Name == "Gambonanza.StrainApi"));
            });
        Console.WriteLine($"PASS: {_checks} Cashback checks.");
    }

    private static void Run(string name, Action test)
    {
        try { test(); _checks++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + name + ": " + ex); Environment.Exit(1); }
    }

    private static void ExpirationOrder()
    {
        using var f = new Fixture();
        var order = new List<string>();
        f.Game.onStateChanged += _ => order.Add("before");
        var g = f.Add();
        g.Expiry.Counter = 2;
        g.Expiry.OnNativeExpiry = () => order.Add("native");
        f.Coins.OnCredit = _ => order.Add("credit");
        f.Game.onStateChanged += _ => order.Add("after");
        f.Enable(); f.Win();
        Equal("before,native,credit,after", string.Join(",", order));
        Equal(6, f.Coins.Coins); Equal(1, g.Expiry.Calls); Equal(3, g.Expiry.Counter);
        Equal(6, f.Animation.Amounts.Single());
        True(g.Gambit); UnityEngine.Object.EndFrame(); False(g.Gambit);
    }

    private static void Threshold()
    {
        using var f = new Fixture(); var g = f.Add(); f.Enable();
        f.Win(); Equal(0, f.Coins.Coins); Equal(1, g.Expiry.Counter);
        f.Win(); Equal(0, f.Coins.Coins); Equal(2, g.Expiry.Counter);
        f.Win(); Equal(6, f.Coins.Coins); Equal(3, g.Expiry.Calls);
    }

    private static void NoExpiry()
    {
        using var f = new Fixture(); var g = f.Add(); g.Expiry.Counter = 2; f.Enable();
        foreach (var state in new[] { State.RESULT, State.LOSE, State.MENU, State.PAUSE })
            f.Game.onStateChanged(state);
        Equal(2, g.Expiry.Counter); Equal(0, f.Coins.Coins);
        f.Strains.ActivatedStrain[Strain.GAMBIT_EXPIRE] = false;
        f.Win(); Equal(2, g.Expiry.Counter); Equal(0, f.Coins.Coins);
    }

    private static void CollectorOrdering()
    {
        using var f = new Fixture();
        var go = new GameObject(); var gambit = go.AddComponent<GambitBehaviour>();
        var collector = go.AddComponent<GambitCollector>(); collector.SellValue = 10;
        var expiry = go.AddComponent<GambitStrainExpire>(); expiry.Counter = 2;
        f.Own(gambit);
        f.Game.onStateChanged += _ => { collector.SellValue += 3; f.Coins.CollectorCount = collector.SellValue; };
        expiry.Subscribe();
        f.Game.onStateChanged += _ => collector.SellValue += 100;
        f.Enable(); f.Win();
        Equal(19, f.Coins.Coins); Equal(0, f.Coins.CollectorCount); Equal(113, collector.SellValue);
    }

    private static void ToothValue()
    {
        using var f = new Fixture(); var g = f.Add(true); g.Expiry.Counter = 2;
        DataManager.Instance.Data.BossToothCoin = 45;
        f.Enable(); f.Win(); Equal(45, f.Coins.Coins); Equal(0, DataManager.Instance.Data.BossToothCoin);
    }

    private static void NotASale()
    {
        using var f = new Fixture(); var g = f.Add(); g.Expiry.Counter = 2;
        int sales = 0; f.Sell.OnSellGambit += _ => sales++; f.Sell.ChaosModeActive = true;
        f.Enable(); f.Win(); Equal(6, f.Coins.Coins); Equal(0, sales); Equal(0, g.Gambit.SellCalls);
    }

    private static void ManualSale()
    {
        using var f = new Fixture(); var g = f.Add(); g.Expiry.Counter = 2; f.Enable();
        f.Coins.IncreaseCoin(6); f.Sell.OnSellGambit(g.Gambit);
        UnityEngine.Object.Destroy(g.Gambit.gameObject);
        f.Win(); Equal(6, f.Coins.Coins); Equal(1, f.Coins.Credits.Count);
    }

    private static void DuplicateCallbacks()
    {
        using var f = new Fixture(); var g = f.Add(); g.Expiry.Counter = 2; g.Expiry.Subscribe();
        f.Enable(); f.Win(); Equal(6, f.Coins.Coins); Equal(2, g.Expiry.Calls);
    }

    private static void LateSubscription()
    {
        using var f = new Fixture(); f.Enable(); var g = f.Add(); g.Expiry.Counter = 2;
        f.Tick(); f.Win(); Equal(6, f.Coins.Coins); Equal(1, g.Expiry.Calls);
    }

    private static void DestroyedPruning()
    {
        using var f = new Fixture(); var g = f.Add(); g.Expiry.Counter = 2; f.Enable(); f.Win();
        UnityEngine.Object.EndFrame(); True(f.Game.onStateChanged != null);
        f.Tick(); True(f.Game.onStateChanged == null);
        f.Disable(); True(f.Game.onStateChanged == null);
    }

    private static void DestroyedStaleDispatch()
    {
        using var f = new Fixture(); var g = f.Add(); g.Expiry.Counter = 2;
        f.Enable(); var stale = f.Game.onStateChanged;
        UnityEngine.Object.Destroy(g.Gambit.gameObject); UnityEngine.Object.EndFrame();
        False(g.Expiry);
        // A dispatch captured the multicast chain before native OnDestroy tried to
        // unsubscribe. Update has not pruned that wrapper yet.
        stale(State.WIN);
        Equal(0, g.Expiry.Calls); Equal(2, g.Expiry.Counter); Equal(0, f.Coins.Coins);
        f.Tick(); True(f.Game.onStateChanged == null);
    }

    private static void Deactivation()
    {
        using var f = new Fixture(); var g = f.Add(); g.Expiry.Counter = 2;
        var original = f.Game.onStateChanged; f.Enable(); var stale = f.Game.onStateChanged;
        True(original != stale); f.Disable(); True(original == f.Game.onStateChanged);
        True(f.Sell.OnSellGambit == null);
        stale(State.WIN); Equal(1, g.Expiry.Calls); Equal(0, f.Coins.Coins);
    }

    private static void ExternalRemoval()
    {
        using var f = new Fixture(); f.Add(); f.Enable(); f.Game.onStateChanged = null;
        f.Tick(); f.Disable(); True(f.Game.onStateChanged == null);
    }

    private static void GraphChanges()
    {
        using var f = new Fixture(); var order = new List<int>();
        f.Game.onStateChanged += _ => order.Add(1);
        var g = f.Add(); g.Expiry.Counter = 2; g.Expiry.OnNativeExpiry = () => order.Add(2);
        f.Enable(); f.Game.onStateChanged += _ => order.Add(3); f.Tick(); f.Win();
        Equal("1,2,3", string.Join(",", order)); f.Disable();
        Equal(3, f.Game.onStateChanged.GetInvocationList().Length);
        True(f.Game.onStateChanged.GetInvocationList()[1].Target == g.Expiry);
    }

    private static void Ownership()
    {
        using var f = new Fixture(); var g = f.Add(owned: false); g.Expiry.Counter = 2;
        f.Enable(); f.Win(); Equal(0, f.Coins.Coins); Equal(3, g.Expiry.Counter);
    }

    private static void MultipleExpiries()
    {
        using var f = new Fixture(); var a = f.Add(); var b = f.Add();
        a.Expiry.Counter = b.Expiry.Counter = 2; b.Gambit.Info.PriceCost = 20;
        f.Enable(); f.Win(); Equal(16, f.Coins.Coins); Equal(2, f.Coins.Credits.Count);
        Equal(2, f.Animation.Amounts.Count);
    }

    private static void ContinuedRun()
    {
        using var f = new Fixture(); var g = f.Add(); g.Expiry.Counter = 2;
        f.Enable(); f.Tick(); f.Tick(); Equal(0, f.Coins.Coins); Equal(0, g.Expiry.Calls);
        f.Win(); Equal(6, f.Coins.Coins);
    }

    private static void FeedbackFailure()
    {
        using var f = new Fixture(); var g = f.Add(); g.Expiry.Counter = 2; f.Animation.Fail = true;
        bool after = false; f.Game.onStateChanged += _ => after = true;
        f.Enable(); f.Win(); Equal(6, f.Coins.Coins); True(after); Equal(1, f.Coins.TextSyncs);
        f.Win(); Equal(6, f.Coins.Coins);
    }

    private static void NativeFailure()
    {
        using var f = new Fixture(); var g = f.Add(); g.Expiry.Counter = 2;
        var error = new InvalidOperationException("native error"); g.Expiry.NativeError = error; f.Enable();
        try { f.Win(); throw new Exception("native exception was swallowed"); }
        catch (InvalidOperationException observed) { True(ReferenceEquals(error, observed)); }
        Equal(1, g.Expiry.Calls); Equal(0, f.Coins.Coins);
    }

    private static void ManagerReplacement()
    {
        using var f = new Fixture(); var g = f.Add(); var original = f.Game.onStateChanged;
        f.Enable(); var replacement = Fixture.Manager<GameManager>();
        SingletonMonoBehaviour<GameManager>.Instance = replacement;
        f.Tick(); True(original == f.Game.onStateChanged); True(replacement.onStateChanged == null);
        f.Disable(); True(original == f.Game.onStateChanged);
    }

    private static void ZeroValue()
    {
        using var f = new Fixture(); var g = f.Add(true); g.Expiry.Counter = 2;
        f.Enable(); f.Win(); Equal(0, f.Coins.Credits.Count); Equal(0, f.Animation.Amounts.Count);
    }

    private static void EnabledModOnKing()
    {
        using var f = new Fixture(); var g = f.Add(); g.Expiry.Counter = 2;
        var data = DataManager.Instance.Data;
        data.CurrentDifficulty = DIFFICULTY.KING;
        data.RunInProgress = true; data.WinCounter = 4;
        data.MaxDifficultyReached = 4; data.Difficulty_King_Unlocked = true;
        using var mod = new ModFixture(); mod.Entry.OnEnable();
        True(mod.Runner.gameObject.Persistent); f.Win();
        Equal(6, f.Coins.Coins); Equal(DIFFICULTY.KING, data.CurrentDifficulty);
        True(data.RunInProgress); Equal(4, data.WinCounter);
        Equal(4, data.MaxDifficultyReached); True(data.Difficulty_King_Unlocked);
        True(mod.Context.Lines.Any(line => line.Contains("+$6")));
        // This source-linked program has no third-party strain namespace or DLL.
        True(typeof(CashbackBehaviour).BaseType == typeof(MonoBehaviour));
    }

    private static void ModHotToggle()
    {
        using var f = new Fixture(); var g = f.Add(); g.Expiry.Counter = 1;
        var original = f.Game.onStateChanged;
        using var mod = new ModFixture(); mod.Entry.OnEnable();
        var first = mod.Runner; var captured = f.Game.onStateChanged;
        mod.Entry.OnEnable(); True(ReferenceEquals(first, mod.Runner));
        True(ReferenceEquals(captured, f.Game.onStateChanged));
        Equal(1, f.Sell.OnSellGambit.GetInvocationList().Length);
        mod.Entry.OnDisable(); mod.Entry.OnDisable();
        True(first); False(first.gameObject.ActiveSelf);
        True(original == f.Game.onStateChanged); True(f.Sell.OnSellGambit == null);
        // Pending destruction must not let the old host pay via a captured graph.
        captured(State.WIN); Equal(0, f.Coins.Coins); Equal(2, g.Expiry.Counter);
        mod.Entry.OnEnable(); False(ReferenceEquals(first, mod.Runner));
        captured(State.INGAME); Equal(0, f.Coins.Coins);
        f.Win(); Equal(6, f.Coins.Coins); Equal(1, f.Coins.Credits.Count);
    }

    private static void ModEarlyStartup()
    {
        using var f = new Fixture(); var g = f.Add(); g.Expiry.Counter = 2;
        var original = f.Game.onStateChanged;
        SingletonMonoBehaviour<GameManager>.Instance = null;
        SingletonMonoBehaviour<SellManager>.Instance = null;
        using var mod = new ModFixture(); mod.Entry.OnEnable();
        True(mod.Runner); True(original == f.Game.onStateChanged);
        SingletonMonoBehaviour<GameManager>.Instance = f.Game;
        SingletonMonoBehaviour<SellManager>.Instance = f.Sell;
        Tick(mod.Runner); True(original != f.Game.onStateChanged);
        f.Win(); Equal(6, f.Coins.Coins);
    }

    private static void ModSuccessiveRuns()
    {
        using var f = new Fixture(); var first = f.Add(); first.Expiry.Counter = 2;
        using var mod = new ModFixture(); mod.Entry.OnEnable(); var host = mod.Runner;
        f.Win(); Equal(6, f.Coins.Coins);
        UnityEngine.Object.EndFrame(); Tick(host);
        f.Game.onStateChanged?.Invoke(State.MENU);
        var next = f.Add(); next.Expiry.Counter = 2; Tick(host); f.Win();
        Equal(12, f.Coins.Coins); True(ReferenceEquals(host, mod.Runner));
        True(host.gameObject.Persistent);
    }

    private static void ModManagerReplacement()
    {
        using var f = new Fixture(); var first = f.Add(); first.Expiry.Counter = 1;
        var original = f.Game.onStateChanged;
        using var mod = new ModFixture(); mod.Entry.OnEnable();
        var replacementGame = Fixture.Manager<GameManager>();
        var replacementSell = Fixture.Manager<SellManager>();
        SingletonMonoBehaviour<GameManager>.Instance = replacementGame;
        SingletonMonoBehaviour<SellManager>.Instance = replacementSell;
        var next = f.Add(); next.Expiry.Counter = 2;
        var replacementOriginal = replacementGame.onStateChanged;
        Tick(mod.Runner);
        True(original == f.Game.onStateChanged); True(f.Sell.OnSellGambit == null);
        Equal(1, replacementSell.OnSellGambit.GetInvocationList().Length);
        replacementGame.onStateChanged(State.WIN);
        Equal(6, f.Coins.Coins); Equal(1, first.Expiry.Counter);
        mod.Entry.OnDisable(); True(replacementOriginal == replacementGame.onStateChanged);
        True(replacementSell.OnSellGambit == null);
        // Keep the simulated native destruction callback attached to its manager.
        UnityEngine.Object.EndFrame();
        SingletonMonoBehaviour<GameManager>.Instance = f.Game;
        SingletonMonoBehaviour<SellManager>.Instance = f.Sell;
    }

    private static void Tick(CashbackBehaviour runner) => typeof(CashbackBehaviour)
        .GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(runner, null);

    private sealed class FakeContext : Gambonanza.ModSdk.IModContext
    {
        public readonly List<string> Lines = new List<string>();
        public void LogLine(string message) => Lines.Add(message);
    }

    private sealed class ModFixture : IDisposable
    {
        public readonly CashbackStrainMod Entry = new CashbackStrainMod();
        public readonly FakeContext Context = new FakeContext();
        public CashbackBehaviour Runner => (CashbackBehaviour)typeof(CashbackStrainMod)
            .GetField("_runner", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Entry);
        public ModFixture() { GameObject.SimulateLifecycle = true; Entry.OnLoad(Context); }
        public void Dispose()
        {
            Entry.OnDisable(); UnityEngine.Object.EndFrame(); GameObject.SimulateLifecycle = false;
        }
    }

    private static void True(bool condition) { if (!condition) throw new Exception("expected true"); }
    private static void False(bool condition) => True(!condition);
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"expected {expected}, got {actual}");
    }

    private sealed class Fixture : IDisposable
    {
        public readonly GameManager Game = Manager<GameManager>();
        public readonly StrainManager Strains = Manager<StrainManager>();
        public readonly GambitManager Gambits = Manager<GambitManager>();
        public readonly SellManager Sell = Manager<SellManager>();
        public readonly ChessDataManager Coins = Manager<ChessDataManager>();
        public readonly MoneyAnimationManager Animation = Manager<MoneyAnimationManager>();
        private readonly CashbackBehaviour _behaviour = new GameObject().AddComponent<CashbackBehaviour>();
        private bool _enabled;

        public Fixture()
        {
            UnityEngine.Object.ResetPending();
            SingletonMonoBehaviour<GameManager>.Instance = Game;
            SingletonMonoBehaviour<StrainManager>.Instance = Strains;
            SingletonMonoBehaviour<GambitManager>.Instance = Gambits;
            SingletonMonoBehaviour<SellManager>.Instance = Sell;
            SingletonMonoBehaviour<ChessDataManager>.Instance = Coins;
            SingletonMonoBehaviour<MoneyAnimationManager>.Instance = Animation;
            DataManager.Instance = new DataManager(); AudioManager.Calls = 0;
            Lifecycle("Awake");
        }
        public static T Manager<T>() where T : MonoBehaviour, new() => new GameObject().AddComponent<T>();
        public (GambitBehaviour Gambit, GambitStrainExpire Expiry) Add(bool tooth = false, bool owned = true)
        {
            var go = new GameObject();
            GambitBehaviour gambit = tooth ? go.AddComponent<ToothGambitBehaviour>() : go.AddComponent<GambitBehaviour>();
            var expiry = go.AddComponent<GambitStrainExpire>();
            if (owned) Own(gambit);
            expiry.Subscribe();
            return (gambit, expiry);
        }
        public void Own(GambitBehaviour gambit)
        {
            var place = Manager<GambitPlaceBehaviour>(); place.CurrentGambit = gambit;
            Gambits.GambitPlaces = Gambits.GambitPlaces.Concat(new[] { place }).ToArray();
        }
        public void Enable() { _enabled = true; Lifecycle("OnEnable"); Lifecycle("Start"); }
        public void Disable() { _enabled = false; Lifecycle("OnDisable"); }
        public void Tick() => Lifecycle("Update");
        public void Win() => Game.onStateChanged?.Invoke(State.WIN);
        private void Lifecycle(string method) => typeof(CashbackBehaviour)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(_behaviour, null);
        public void Dispose() { if (_enabled) Disable(); Lifecycle("OnDestroy"); UnityEngine.Object.ResetPending(); }
    }
}
