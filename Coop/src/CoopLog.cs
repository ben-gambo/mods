using Gambonanza.ModSdk;

namespace Gambonanza.Coop
{
    internal static class CoopLog
    {
        public static IConsoleApi Console;
        public static bool Verbose;

        public static void Info(string msg)
        {
            UnityEngine.Debug.Log($"[Coop] {msg}");
            Console?.PrintInfo($"[coop] {msg}");
        }

        public static void Warn(string msg)
        {
            UnityEngine.Debug.LogWarning($"[Coop] {msg}");
            Console?.PrintWarn($"[coop] {msg}");
        }

        public static void Error(string msg)
        {
            UnityEngine.Debug.LogError($"[Coop] {msg}");
            Console?.PrintError($"[coop] {msg}");
        }

        /// <summary>Log-file only, and only when verbose is enabled - never spams the console.</summary>
        public static void Debug(string msg)
        {
            if (Verbose) UnityEngine.Debug.Log($"[Coop] {msg}");
        }
    }
}
