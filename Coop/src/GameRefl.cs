using System;
using System.Reflection;
using UnityEngine;

namespace Gambonanza.Coop
{
    /// <summary>Small reflection helpers for the few private game members co-op needs.</summary>
    internal static class GameRefl
    {
        public const BindingFlags Any =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        public static FieldInfo Field(Type type, string name)
        {
            var f = type.GetField(name, Any);
            if (f == null) Debug.LogWarning($"[Coop] reflection: field {type.Name}.{name} not found");
            return f;
        }

        public static object GetField(object target, string name)
            => Field(target.GetType(), name)?.GetValue(target);

        public static void SetField(object target, string name, object value)
            => Field(target.GetType(), name)?.SetValue(target, value);

        public static MethodInfo Method(Type type, string name, params Type[] args)
        {
            MethodInfo m = args.Length > 0
                ? type.GetMethod(name, Any, null, args, null)
                : type.GetMethod(name, Any);
            if (m == null) Debug.LogWarning($"[Coop] reflection: method {type.Name}.{name} not found");
            return m;
        }

        public static object Invoke(object target, MethodInfo m, params object[] args)
        {
            if (m == null) return null;
            try { return m.Invoke(target, args); }
            catch (TargetInvocationException tie)
            {
                Debug.LogError($"[Coop] reflection invoke {m.Name} threw: {tie.InnerException}");
                return null;
            }
        }
    }
}
