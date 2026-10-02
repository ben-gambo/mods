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
        public static void EndFrame()
        {
            foreach (var value in Pending.ToArray())
            {
                value.Destroyed = true;
                if (value is GameObject go)
                    foreach (var component in go.Components)
                    {
                        component.Destroyed = true;
                        component.GetType().GetMethod("OnDestroy", BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(component, null);
                    }
            }
            Pending.Clear();
        }
        internal static void ResetPending() => Pending.Clear();
    }
    public class Transform : Object { }
    public class GameObject : Object
    {
        internal readonly List<MonoBehaviour> Components = new List<MonoBehaviour>();
        public readonly Transform transform = new Transform();
        public T AddComponent<T>() where T : MonoBehaviour, new()
        {
            var component = new T { gameObject = this };
            Components.Add(component);
            return component;
        }
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
    public class BaseData { public int BossToothCoin; }
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
namespace Gambonanza.StrainApi
{
    public abstract class StrainBehaviour : UnityEngine.MonoBehaviour
    {
        public readonly List<string> Logs = new List<string>();
        protected virtual void OnGameStarted() { }
        protected void Log(string message) => Logs.Add(message);
    }
}
