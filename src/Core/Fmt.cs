using System;
using System.Globalization;

namespace VidFlow.Core
{
    // Formatting/parsing helpers shared by the UI. Invariant culture throughout.
    public static class Fmt
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // 75.5 -> "1:15" (or "1:15.500" with millis); >= 1 h -> "1:02:03".
        public static string Time(double seconds, bool millis = false, bool forceHours = false)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds)) seconds = 0;
            bool neg = seconds < 0;
            if (neg) seconds = -seconds;
            long totalMs = (long)Math.Round(seconds * 1000.0, MidpointRounding.AwayFromZero);
            if (!millis) totalMs = (totalMs / 1000) * 1000; // truncate so the clock never runs ahead
            long h = totalMs / 3600000, m = totalMs / 60000 % 60, s = totalMs / 1000 % 60, ms = totalMs % 1000;
            string core = (h > 0 || forceHours)
                ? string.Format(Inv, "{0}:{1:00}:{2:00}", h, m, s)
                : string.Format(Inv, "{0}:{1:00}", m, s);
            if (millis) core += string.Format(Inv, ".{0:000}", ms);
            return neg ? "-" + core : core;
        }

        // Accepts "90", "90.5", "1:30", "1:02:03.25", "-5" (negative = from end, caller decides).
        public static bool TryParseTime(string text, out double seconds)
        {
            seconds = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.Trim();
            bool neg = s.StartsWith("-");
            if (neg) s = s.Substring(1).Trim();
            string[] parts = s.Split(':');
            if (parts.Length > 3) return false;
            double total = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                double v;
                bool last = i == parts.Length - 1;
                if (!double.TryParse(parts[i], last ? NumberStyles.Float : NumberStyles.Integer, Inv, out v) || v < 0
                    || double.IsNaN(v) || double.IsInfinity(v)) return false;
                if (i > 0 && v >= 60) return false;
                total = total * 60 + v;
            }
            seconds = neg ? -total : total;
            return true;
        }

        // 1 -> "1×", 1.25 -> "1.25×", 0.5 -> "0.5×"
        public static string Speed(double speed)
        {
            return Math.Round(speed, 2).ToString("0.##", Inv) + "×";
        }

        public static string Bytes(long n)
        {
            if (n < 1024) return n.ToString(Inv) + " B";
            double v = n;
            string[] units = { "KB", "MB", "GB", "TB" };
            int u = -1;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return v.ToString(v >= 100 ? "0" : v >= 10 ? "0.0" : "0.00", Inv) + " " + units[u];
        }

        public static string Num(double v, string format = "0.###")
        {
            return v.ToString(format, Inv);
        }
    }
}
