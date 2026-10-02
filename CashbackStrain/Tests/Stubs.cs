using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace UnityEngine
{
    public class Object
    {
        internal bool Destroyed;
        private static readonly List<Object> Pending = new List<Object>();
        public static implicit operator bool(Object value) => !ReferenceEquals(value, null) && !value.Destroyed;
        public static void Destroy(Object value) { if (value) Pending.Add(value); }
        public static void DontDestroyOnLoad(Object value) { if (value is GameObject go) go.Persistent = true; }
        public static void EndFrame()
        {
            foreach (var value in Pending.ToArray())
            {
                value.Destroyed = true;
                if (value is GameObject go)
                {
                    go.SetActive(false);
                    foreach (var component in go.Components)
                    {
                        component.Destroyed = true;
                        component.GetType().GetMethod("OnDestroy", BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(component, null);
                    }
                }
            }
            Pending.Clear();
        }
        internal static void ResetPending() { Pending.Clear(); GameObject.SimulateLifecycle = false; }
    }
    public class Transform : Object { }
    public class GameObject : Object
    {
        public static bool SimulateLifecycle;
        internal readonly List<MonoBehaviour> Components = new List<MonoBehaviour>();
        private readonly HashSet<MonoBehaviour> _awoken = new HashSet<MonoBehaviour>();
        public readonly Transform transform = new Transform();
        public bool ActiveSelf = true;
        public bool Persistent;
        public HideFlags hideFlags;
        public GameObject(string name = "") { }
        public void SetActive(bool active)
        {
            if (ActiveSelf == active) return;
            ActiveSelf = active;
            if (!SimulateLifecycle) return;
            foreach (var component in Components)
            {
                if (active && _awoken.Add(component)) Lifecycle(component, "Awake");
                Lifecycle(component, active ? "OnEnable" : "OnDisable");
            }
        }
        public T AddComponent<T>() where T : MonoBehaviour, new()
        {
            var component = new T { gameObject = this };
            Components.Add(component);
            if (SimulateLifecycle && ActiveSelf)
            {
                _awoken.Add(component);
                Lifecycle(component, "Awake");
                Lifecycle(component, "OnEnable");
            }
            return component;
        }
        private static void Lifecycle(MonoBehaviour component, string method)
            => component.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(component, null);
    }
    public enum HideFlags { HideAndDontSave }
    public static class Debug
    {
        public static readonly List<string> Messages = new List<string>();
        public static void Log(string message) => Messages.Add(message);
    }
    public class MonoBehaviour : Object
    {
        public GameObject gameObject;
        public Transform transform => gameObject.transform;
        public T GetComponent<T>() where T : MonoBehaviour => gameObject.Components.OfType<T>().FirstOrDefault();
    }
    public static class Random { public static float Range(float low, float high) => low; }
}

namespace Blukulele.Core
{
    public class SingletonMonoBehaviour<T> : UnityEngine.MonoBehaviour where T : UnityEngine.MonoBehaviour
    {
        public static T Instance;
        public static bool IsCreated() => Instance;
    }
    public enum State { MENU, INGAME, WIN, RESULT, LOSE, PAUSE }
    public class GameManager : SingletonMonoBehaviour<GameManager>
    {
        public Action<State> onStateChanged;
    }
}

namespace Blukulele.CHE
{
    using Blukulele.Core;
    using UnityEngine;
    public enum Strain { GAMBIT_EXPIRE }
    public enum DIFFICULTY { CUSTOM = -1, PAWN, ROOK, KNIGHT, BISHOP, QUEEN, KING }
    public class StrainManager : SingletonMonoBehaviour<StrainManager>
    {
        public readonly Dictionary<Strain, bool> ActivatedStrain = new Dictionary<Strain, bool> { [Strain.GAMBIT_EXPIRE] = true };
        public int ExpireLimit = 3;
    }
    public class SO_Gambit : Object { public string ID = "ordinary"; public int PriceCost = 12; }
    public class GambitBehaviour : MonoBehaviour
    {
        public SO_Gambit Info = new SO_Gambit();
        public int SellCalls;
        public void Sell() { SellCalls++; }
    }
    public class ToothGambitBehaviour : GambitBehaviour { }
    public class GambitCollector : MonoBehaviour { public int SellValue; }
    public class GambitPlaceBehaviour : MonoBehaviour { public GambitBehaviour CurrentGambit; }
    public class GambitManager : SingletonMonoBehaviour<GambitManager>
    {
        public GambitPlaceBehaviour[] GambitPlaces = Array.Empty<GambitPlaceBehaviour>();
    }
    public class SellManager : SingletonMonoBehaviour<SellManager>
    {
        public Action<GambitBehaviour> OnSellGambit;
        public bool ChaosModeActive;
        public int GetSellPriceGambit(SO_Gambit info) => info.PriceCost / 2;
    }
    public class ChessDataManager : SingletonMonoBehaviour<ChessDataManager>
    {
        public int Coins;
        public int CollectorCount;
        public int TextSyncs;
        public readonly List<int> Credits = new List<int>();
        public Action<int> OnCredit;
        public void IncreaseCoin(int amount) { Coins += amount; Credits.Add(amount); OnCredit?.Invoke(amount); }
        public void IncreaseTextCoin(bool correct = false) { TextSyncs++; }
    }
    public class MoneyAnimationManager : SingletonMonoBehaviour<MoneyAnimationManager>
    {
        public readonly List<int> Amounts = new List<int>();
        public bool Fail;
        public void SpawnMoney(Transform transform, int amount)
        {
            if (Fail) throw new InvalidOperationException("cosmetic failure");
            Amounts.Add(amount);
        }
    }
    public class BaseData
    {
        public int BossToothCoin;
        public DIFFICULTY CurrentDifficulty;
        public bool RunInProgress;
        public int WinCounter;
        public int MaxDifficultyReached;
        public bool Difficulty_King_Unlocked;
    }
    public class DataManager
    {
        public static DataManager Instance;
        public BaseData Data = new BaseData();
    }
    public class GambitStrainExpire : MonoBehaviour
    {
        public int Counter;
        public int Calls;
        public Action OnNativeExpiry;
        public Exception NativeError;
        public void Subscribe() => SingletonMonoBehaviour<GameManager>.Instance.onStateChanged += Behave;
        private void OnDestroy() => SingletonMonoBehaviour<GameManager>.Instance.onStateChanged -= Behave;
        private void Behave(State state)
        {
            Calls++;
            if (NativeError != null) throw NativeError;
            var strains = SingletonMonoBehaviour<StrainManager>.Instance;
            if (!strains.ActivatedStrain[Strain.GAMBIT_EXPIRE] || state != State.WIN) return;
            Counter++;
            if (Counter < strains.ExpireLimit) return;
            OnNativeExpiry?.Invoke();
            if (GetComponent<GambitCollector>()) SingletonMonoBehaviour<ChessDataManager>.Instance.CollectorCount = 0;
            if (GetComponent<ToothGambitBehaviour>()) DataManager.Instance.Data.BossToothCoin = 0;
            Object.Destroy(gameObject);
        }
    }
}

namespace Blukulele.Audio { public enum AudioEvents { Sell } }
namespace Blukulele.Module.Audio
{
    public static class AudioManager
    {
        public static int Calls;
        public static void Play(Blukulele.Audio.AudioEvents value, bool loop = false, float pitch = 1) { Calls++; }
    }
}
namespace Gambonanza.ModSdk
{
    public interface IMod { void OnLoad(IModContext context); }
    public interface IModLifecycle { void OnEnable(); void OnDisable(); }
    public interface IModContext { void LogLine(string message); }
}
