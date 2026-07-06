using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;
using PowerTrader.Core.Util;

namespace PowerTrader.Core.Config
{
    /// <summary>
    /// Strongly-typed view of gui_settings.json. Field parsing / clamping mirrors
    /// pt_trader.py's _load_gui_settings() exactly (including defaults and bounds).
    /// </summary>
    public sealed class GuiSettings
    {
        public List<string> Coins { get; set; } = new List<string> { "BTC", "ETH", "BNB", "PAXG", "SOL", "XRP", "DOGE" };
        public string MainNeuralDir { get; set; }
        public int TradeStartLevel { get; set; } = 4;
        public double StartAllocationPct { get; set; } = 0.5;
        public double DcaMultiplier { get; set; } = 2.0;
        public List<double> DcaLevels { get; set; } = new List<double> { -5.0, -10.0, -20.0, -30.0, -40.0, -50.0, -50.0 };
        public int MaxDcaBuysPer24h { get; set; } = 1;
        public List<string> LongTermHoldings { get; set; } = new List<string> { "BTC", "ETH", "BNB", "PAXG", "SOL", "XRP", "DOGE" };
        public double LthProfitAllocPct { get; set; } = 50.0;
        public double PmStartPctNoDca { get; set; } = 3.0;
        public double PmStartPctWithDca { get; set; } = 3.0;
        public double TrailingGapPct { get; set; } = 0.1;

        // --- Opt-in safety features (all default OFF => behavior stays a faithful 1:1 port) ---
        /// <summary>Log intended orders instead of sending them (no real trades).</summary>
        public bool PaperMode { get; set; } = false;
        /// <summary>Force-exit a coin when sell-side PnL &lt;= -this%. 0 disables.</summary>
        public double CatastrophicStopPct { get; set; } = 0.0;
        /// <summary>Cap total USD cost basis per coin (blocks/clamps DCA past it). 0 disables.</summary>
        public double MaxCapitalPerCoinUsd { get; set; } = 0.0;
        /// <summary>Skip a START buy if KuCoin(signal venue) vs Robinhood(exec venue) price differ by more than this%. 0 disables.</summary>
        public double VenueDivergencePct { get; set; } = 0.0;
        /// <summary>Ignore neural start/DCA signals whose files are older than this many seconds. 0 disables.</summary>
        public double SignalMaxAgeSeconds { get; set; } = 0.0;

        /// <summary>Last-known file mtime (ticks) used for change detection; null if file absent.</summary>
        public long? Mtime { get; set; }

        public GuiSettings Clone()
        {
            return new GuiSettings
            {
                Coins = new List<string>(Coins),
                MainNeuralDir = MainNeuralDir,
                TradeStartLevel = TradeStartLevel,
                StartAllocationPct = StartAllocationPct,
                DcaMultiplier = DcaMultiplier,
                DcaLevels = new List<double>(DcaLevels),
                MaxDcaBuysPer24h = MaxDcaBuysPer24h,
                LongTermHoldings = new List<string>(LongTermHoldings),
                LthProfitAllocPct = LthProfitAllocPct,
                PmStartPctNoDca = PmStartPctNoDca,
                PmStartPctWithDca = PmStartPctWithDca,
                TrailingGapPct = TrailingGapPct,
                PaperMode = PaperMode,
                CatastrophicStopPct = CatastrophicStopPct,
                MaxCapitalPerCoinUsd = MaxCapitalPerCoinUsd,
                VenueDivergencePct = VenueDivergencePct,
                SignalMaxAgeSeconds = SignalMaxAgeSeconds,
                Mtime = Mtime,
            };
        }
    }

    /// <summary>
    /// mtime-cached loader for gui_settings.json (mirrors the _gui_settings_cache in
    /// pt_trader.py / pt_thinker.py). Thread-safe; cheap to call frequently.
    /// </summary>
    public sealed class GuiSettingsLoader
    {
        private readonly object _lock = new object();
        private readonly string _path;
        private GuiSettings _cache = new GuiSettings();
        private long? _cachedMtime;

        public GuiSettingsLoader(string path = null)
        {
            _path = path ?? AppPaths.GuiSettingsPath;
        }

        public string Path => _path;

        /// <summary>Returns a snapshot clone; Mtime is null when the file is absent.</summary>
        public GuiSettings Load()
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(_path))
                    {
                        var c = _cache.Clone();
                        c.Mtime = null;
                        return c;
                    }

                    long mtime = File.GetLastWriteTimeUtc(_path).Ticks;
                    if (_cachedMtime == mtime)
                        return _cache.Clone();

                    var raw = JsonStore.ReadJObject(_path) ?? new JObject();
                    var s = Parse(raw, _cache);
                    s.Mtime = mtime;

                    _cache = s;
                    _cachedMtime = mtime;
                    return s.Clone();
                }
                catch
                {
                    var c = _cache.Clone();
                    return c;
                }
            }
        }

        private static GuiSettings Parse(JObject data, GuiSettings prev)
        {
            var s = new GuiSettings();

            // coins
            var coins = ParseSymbolList(data["coins"]);
            s.Coins = coins.Count > 0 ? coins : new List<string>(prev.Coins);

            // main_neural_dir
            string mnd = data["main_neural_dir"]?.Type == JTokenType.String ? (string)data["main_neural_dir"] : null;
            if (mnd != null) mnd = mnd.Trim();
            s.MainNeuralDir = string.IsNullOrEmpty(mnd) ? null : mnd;

            // trade_start_level (1..7)
            s.TradeStartLevel = ClampInt(ParseIntFromAny(data["trade_start_level"], prev.TradeStartLevel), 1, 7);

            // start_allocation_pct (>=0)
            s.StartAllocationPct = Math.Max(0.0, ParsePctLike(data["start_allocation_pct"], prev.StartAllocationPct));

            // dca_multiplier (>=0)
            s.DcaMultiplier = Math.Max(0.0, ParseDoubleFromAny(data["dca_multiplier"], prev.DcaMultiplier));

            // dca_levels
            var dca = ParseDoubleList(data["dca_levels"]);
            s.DcaLevels = dca.Count > 0 ? dca : new List<double>(prev.DcaLevels);

            // max_dca_buys_per_24h (>=0)
            s.MaxDcaBuysPer24h = Math.Max(0, ParseIntFromAny(data["max_dca_buys_per_24h"], prev.MaxDcaBuysPer24h));

            // pm settings
            s.PmStartPctNoDca = Math.Max(0.0, ParsePctLike(data["pm_start_pct_no_dca"], prev.PmStartPctNoDca));
            s.PmStartPctWithDca = Math.Max(0.0, ParsePctLike(data["pm_start_pct_with_dca"], prev.PmStartPctWithDca));
            s.TrailingGapPct = Math.Max(0.0, ParsePctLike(data["trailing_gap_pct"], prev.TrailingGapPct));

            // opt-in safety features (all default OFF)
            s.PaperMode = ParseBool(data["paper_mode"], prev.PaperMode);
            s.CatastrophicStopPct = Math.Max(0.0, ParsePctLike(data["catastrophic_stop_pct"], prev.CatastrophicStopPct));
            s.MaxCapitalPerCoinUsd = Math.Max(0.0, ParseDoubleFromAny(data["max_capital_per_coin_usd"], prev.MaxCapitalPerCoinUsd));
            s.VenueDivergencePct = Math.Max(0.0, ParsePctLike(data["venue_divergence_pct"], prev.VenueDivergencePct));
            s.SignalMaxAgeSeconds = Math.Max(0.0, ParseDoubleFromAny(data["signal_max_age_seconds"], prev.SignalMaxAgeSeconds));

            // lth_profit_alloc_pct (0..100)
            double lthPct = ParsePctLike(data["lth_profit_alloc_pct"], prev.LthProfitAllocPct);
            s.LthProfitAllocPct = ClampDouble(lthPct, 0.0, 100.0);

            // long_term_holdings (list / csv / dict-keys)
            s.LongTermHoldings = ParseLongTermHoldings(data["long_term_holdings"]);

            return s;
        }

        private static List<string> ParseSymbolList(JToken tok)
        {
            var outList = new List<string>();
            if (tok is JArray arr)
            {
                foreach (var v in arr)
                {
                    string sym = (v?.ToString() ?? string.Empty).Trim().ToUpperInvariant();
                    if (!string.IsNullOrEmpty(sym)) outList.Add(sym);
                }
            }
            return outList;
        }

        private static List<string> ParseLongTermHoldings(JToken tok)
        {
            var raw = new List<string>();
            if (tok is JObject obj)
            {
                foreach (var p in obj.Properties()) raw.Add(p.Name);
            }
            else if (tok is JArray arr)
            {
                foreach (var v in arr) raw.Add(v?.ToString() ?? string.Empty);
            }
            else if (tok != null && tok.Type == JTokenType.String)
            {
                string str = (string)tok;
                foreach (var part in str.Replace("\n", ",").Split(','))
                    raw.Add(part.Trim());
            }

            var outList = new List<string>();
            var seen = new HashSet<string>();
            foreach (var v in raw)
            {
                string sstr = (v ?? string.Empty).Trim();
                if (sstr.Length == 0) continue;
                if (sstr.Contains(":")) sstr = sstr.Split(new[] { ':' }, 2)[0].Trim();
                else if (sstr.Contains("=")) sstr = sstr.Split(new[] { '=' }, 2)[0].Trim();
                string sym = sstr.ToUpperInvariant().Trim();
                if (sym.Length == 0 || seen.Contains(sym)) continue;
                seen.Add(sym);
                outList.Add(sym);
            }
            return outList;
        }

        private static List<double> ParseDoubleList(JToken tok)
        {
            var outList = new List<double>();
            if (tok is JArray arr)
            {
                foreach (var v in arr)
                {
                    if (PyCompat.TryDouble(v?.ToString(), out double d)) outList.Add(d);
                }
            }
            return outList;
        }

        private static int ParseIntFromAny(JToken tok, int fallback)
        {
            if (tok == null) return fallback;
            if (PyCompat.TryDouble(tok.ToString(), out double d))
            {
                try { return checked((int)d); } catch { return fallback; }
            }
            return fallback;
        }

        private static double ParseDoubleFromAny(JToken tok, double fallback)
        {
            if (tok == null) return fallback;
            return PyCompat.ToDouble(tok.ToString(), fallback);
        }

        private static bool ParseBool(JToken tok, bool fallback)
        {
            if (tok == null || tok.Type == JTokenType.Null) return fallback;
            if (tok.Type == JTokenType.Boolean) return (bool)tok;
            string s = tok.ToString().Trim().ToLowerInvariant();
            if (s == "true" || s == "1" || s == "yes") return true;
            if (s == "false" || s == "0" || s == "no") return false;
            return fallback;
        }

        /// <summary>Parse a value that may carry a trailing '%' (mirrors str(x).replace("%","")).</summary>
        private static double ParsePctLike(JToken tok, double fallback)
        {
            if (tok == null) return fallback;
            string str = tok.ToString().Replace("%", "").Trim();
            return PyCompat.ToDouble(str, fallback);
        }

        private static int ClampInt(int v, int lo, int hi) => Math.Max(lo, Math.Min(v, hi));
        private static double ClampDouble(double v, double lo, double hi) => Math.Max(lo, Math.Min(v, hi));
    }
}
