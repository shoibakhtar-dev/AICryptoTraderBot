using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PowerTrader.Hub
{
    /// <summary>
    /// gui_settings.json wrapper for the Hub. Preserves ALL keys (including engine-only ones)
    /// on round-trip, and seeds defaults matching pt_hub.py's DEFAULT_SETTINGS.
    /// </summary>
    internal sealed class HubSettings
    {
        private JObject _o;
        private readonly string _path;

        private HubSettings(JObject o, string path) { _o = o; _path = path; }

        public static JObject Defaults()
        {
            return new JObject
            {
                ["main_neural_dir"] = "",
                ["coins"] = new JArray("BTC", "ETH", "BNB", "PAXG", "SOL", "XRP", "DOGE"),
                ["long_term_holdings"] = new JArray("BTC", "ETH", "BNB", "PAXG", "SOL", "XRP", "DOGE"),
                ["lth_profit_alloc_pct"] = 50.0,
                ["trade_start_level"] = 4,
                ["start_allocation_pct"] = 0.5,
                ["dca_multiplier"] = 2.0,
                ["dca_levels"] = new JArray(-5.0, -10.0, -20.0, -30.0, -40.0, -50.0, -50.0),
                ["max_dca_buys_per_24h"] = 1,
                ["pm_start_pct_no_dca"] = 3.0,
                ["pm_start_pct_with_dca"] = 3.0,
                ["trailing_gap_pct"] = 0.1,
                ["hub_data_dir"] = "",
                ["timeframes"] = new JArray("1min", "5min", "15min", "30min", "1hour", "2hour", "4hour", "8hour", "12hour", "1day", "1week"),
                ["default_timeframe"] = "1hour",
                ["ui_refresh_seconds"] = 1.0,
                ["chart_refresh_seconds"] = 4.0,
                ["candles_limit"] = 250,
                ["auto_start_scripts"] = false,

                // opt-in safety features (all default OFF => faithful 1:1 behavior)
                ["paper_mode"] = false,
                ["catastrophic_stop_pct"] = 0.0,
                ["max_capital_per_coin_usd"] = 0.0,
                ["venue_divergence_pct"] = 0.0,
                ["signal_max_age_seconds"] = 0.0,
            };
        }

        public static HubSettings Load(string path)
        {
            JObject data = null;
            try
            {
                if (File.Exists(path))
                {
                    string raw = File.ReadAllText(path);
                    if (!string.IsNullOrWhiteSpace(raw)) data = JToken.Parse(raw) as JObject;
                }
            }
            catch { data = null; }

            var merged = Defaults();
            if (data != null)
                foreach (var p in data.Properties()) merged[p.Name] = p.Value;

            return new HubSettings(merged, path);
        }

        public JObject Raw => _o;

        public void Save()
        {
            try
            {
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(_o, Formatting.Indented));
                if (File.Exists(_path)) File.Delete(_path);
                File.Move(tmp, _path);
            }
            catch { }
        }

        public string Str(string key, string dflt = "")
        {
            var v = _o[key];
            return (v == null || v.Type == JTokenType.Null) ? dflt : v.ToString();
        }

        public double Dbl(string key, double dflt)
        {
            var v = _o[key];
            if (v == null || v.Type == JTokenType.Null) return dflt;
            return double.TryParse(v.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : dflt;
        }

        public int Int(string key, int dflt)
        {
            var v = _o[key];
            if (v == null || v.Type == JTokenType.Null) return dflt;
            return int.TryParse(((int)Dbl(key, dflt)).ToString(), out int i) ? (int)Dbl(key, dflt) : dflt;
        }

        public bool Bool(string key, bool dflt)
        {
            var v = _o[key];
            if (v == null || v.Type == JTokenType.Null) return dflt;
            if (v.Type == JTokenType.Boolean) return (bool)v;
            return bool.TryParse(v.ToString(), out bool b) ? b : dflt;
        }

        public List<string> StrList(string key)
        {
            var outList = new List<string>();
            if (_o[key] is JArray arr)
                foreach (var x in arr) { string s = (x?.ToString() ?? "").Trim(); if (s.Length > 0) outList.Add(s); }
            else if (_o[key]?.Type == JTokenType.String)
                foreach (var part in ((string)_o[key]).Replace("\n", ",").Split(','))
                { string s = part.Trim(); if (s.Length > 0) outList.Add(s); }
            return outList;
        }

        public void Set(string key, JToken value) => _o[key] = value;

        public List<string> Coins => StrList("coins").Select(c => c.ToUpperInvariant()).ToList();
    }
}
