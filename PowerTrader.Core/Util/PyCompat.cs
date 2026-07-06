using System;
using System.Globalization;

namespace PowerTrader.Core.Util
{
    /// <summary>
    /// Helpers that reproduce Python's lenient parsing / formatting semantics so the
    /// C# port stays byte-compatible with the on-disk file formats produced by the
    /// original PowerTrader Python scripts.
    /// </summary>
    public static class PyCompat
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>float(s) with a fallback (mirrors try/except float()).</summary>
        public static double ToDouble(string s, double fallback = 0.0)
        {
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            s = s.Trim();
            if (double.TryParse(s, NumberStyles.Float | NumberStyles.AllowLeadingSign, Inv, out double v))
                return v;
            return fallback;
        }

        public static bool TryDouble(string s, out double v)
        {
            v = 0.0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            return double.TryParse(s.Trim(), NumberStyles.Float | NumberStyles.AllowLeadingSign, Inv, out v);
        }

        /// <summary>int(float(s)) with a fallback (mirrors int(float(raw))).</summary>
        public static int ToIntFromFloat(string s, int fallback = 0)
        {
            if (TryDouble(s, out double v))
            {
                try { return checked((int)v); }
                catch { return fallback; }
            }
            return fallback;
        }

        /// <summary>
        /// Reproduce Python's repr(float) enough for our persisted files. Python writes
        /// floats via str(x); .NET "R"/"G17" round-trips but can differ cosmetically.
        /// For the values we persist (prices/weights/thresholds) a round-trip form is safe
        /// because every consumer parses back with float().
        /// </summary>
        public static string Repr(double x)
        {
            if (double.IsNaN(x)) return "nan";
            if (double.IsPositiveInfinity(x)) return "inf";
            if (double.IsNegativeInfinity(x)) return "-inf";
            // Prefer the shortest round-trippable representation.
            string s = x.ToString("R", Inv);
            // Python prints integral floats as "123.0"; .NET "R" prints "123".
            if (s.IndexOf('.') < 0 && s.IndexOf('e') < 0 && s.IndexOf('E') < 0 &&
                s.IndexOf("inf", StringComparison.OrdinalIgnoreCase) < 0 && s != "nan")
            {
                s += ".0";
            }
            return s;
        }

        public static string FixedFormat(double x, int decimals)
        {
            return x.ToString("F" + decimals.ToString(Inv), Inv);
        }
    }
}
