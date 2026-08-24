using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Gambonanza.Coop
{
    /// <summary>Wire protocol: pipe-separated text messages, first token is the type.</summary>
    internal static class Msg
    {
        public const string Hello = "HELLO";          // fw|mod|game|persona
        public const string HelloAck = "HELLOACK";    // fw|mod|game|persona
        public const string RunStart = "RUNSTART";    // seed|difficulty|strain|activeStrains|activeBonus|betterAI|tmpStrain|tmpBonus|actStrain|unlockCsv
        public const string State = "STATE";          // prevState|curState  (host -> guest advancement)
        public const string Move = "MOVE";            // seat|fromKind|a|b|toR|toC|kind(0 normal/1 promoting/2 free/3 end-tile-skip)
        public const string Drop = "DROP";            // seat|stockIdx|toR|toC
        public const string Place = "PLACE";          // seat|fromKind|a|b|toKind|a|b
        public const string Promo = "PROMO";          // toR|toC|pieceType|cost
        public const string EnemyMove = "EMOVE";      // fromR|fromC|toR|toC
        public const string EnemySkip = "ESKIP";      // kind: demon|bribe|plain|cant
        public const string Buy = "BUY";              // slotIndex (0..1 gambits, 2..3 pieces)
        public const string Reroll = "REROLL";
        public const string Limit = "LIMIT";
        public const string Sell = "SELL";            // kind|a|b   (kind B board / S stock)
        public const string SellGambit = "SELLG";     // slotIndex
        public const string GambitArr = "GARR";       // csv of gambit ids per slot ('-' = empty)
        public const string Check = "CHECK";          // wave|round|coins|hash
        public const string Wheel = "WHEEL";         // op(s/p/c)|slot|mode  (token piece wheel)
        public const string StartWheel = "SWHEEL";    // piecesCsv  (run-start piece selector)
        public const string Go = "GO";                // placement GO button
        public const string Gacha = "GACHA";          // op(p/c)|slot|mode  (gachapon)
        public const string Cursor = "CURSOR";        // x|y|hoverR|hoverC|selKind|a|b   (unreliable ch.1)
        public const string Wait = "WAIT";           // seat
        public const string Bye = "BYE";

        public static string Make(string type, params object[] parts)
        {
            var sb = new StringBuilder(type);
            foreach (var p in parts)
            {
                sb.Append('|');
                sb.Append(Convert.ToString(p, CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        public static string[] Split(string payload) => payload.Split('|');

        public static int I(string[] p, int idx, int fallback = 0)
            => idx < p.Length && int.TryParse(p[idx], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        public static uint U(string[] p, int idx)
            => idx < p.Length && uint.TryParse(p[idx], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0u;

        public static float F(string[] p, int idx)
            => idx < p.Length && float.TryParse(p[idx], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;

        public static string S(string[] p, int idx) => idx < p.Length ? p[idx] : "";

        public static string EncodeBools(IReadOnlyList<bool> arr)
        {
            if (arr == null) return "";
            var sb = new StringBuilder(arr.Count);
            for (int i = 0; i < arr.Count; i++) sb.Append(arr[i] ? '1' : '0');
            return sb.ToString();
        }

        public static bool[] DecodeBools(string s)
        {
            var r = new bool[s.Length];
            for (int i = 0; i < s.Length; i++) r[i] = s[i] == '1';
            return r;
        }
    }
}
