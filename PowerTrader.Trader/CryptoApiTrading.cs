using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using PowerTrader.Core.Config;
using PowerTrader.Core.Robinhood;
using PowerTrader.Core.Util;

namespace PowerTrader.Trader
{
    /// <summary>
    /// Faithful port of pt_trader.py's CryptoAPITrading — the Robinhood execution engine.
    /// Places real spot orders. Implements: tiered DCA (neural line OR hardcoded % drawdown,
    /// whichever hits first), a rolling 24h DCA rate limit, a trailing profit-margin sell,
    /// bot-order ownership tracking (so manual/long-term holdings are ignored), a local P&L
    /// ledger, and optional auto-allocation of realized profits into long-term holdings.
    ///
    /// Split across CryptoApiTrading.cs (infrastructure) and CryptoApiTrading.Manage.cs
    /// (the manage_trades decision loop).
    /// </summary>
    public sealed partial class CryptoApiTrading
    {
        private readonly RobinhoodClient _rh;
        private readonly GuiSettingsLoader _settingsLoader = new GuiSettingsLoader();

        // ---- hot-reloaded settings globals (mirror the module-level globals) ----
        private List<string> CryptoSymbols = new List<string> { "BTC", "ETH", "XRP", "BNB", "DOGE" };
        private string MainDir = Environment.CurrentDirectory;
        private Dictionary<string, string> BasePaths;
        private int TradeStartLevel = 3;
        private double StartAllocPct = 0.005;
        private double DcaMultiplier = 2.0;
        private List<double> DcaLevelsGlobal = new List<double> { -2.5, -5.0, -10.0, -20.0, -30.0, -40.0, -50.0 };
        private int MaxDcaBuysPer24hGlobal = 2;
        private double TrailingGapPctGlobal = 0.5;
        private double PmStartPctNoDcaGlobal = 5.0;
        private double PmStartPctWithDcaGlobal = 2.5;
        private double LthProfitAllocPct = 0.0;
        private readonly HashSet<string> LongTermSymbols = new HashSet<string>();
        private long? _lastSettingsMtime;

        // opt-in safety features (default OFF => faithful 1:1 behavior)
        private bool _paperMode;
        private double _catastrophicStopPct;
        private double _maxCapitalPerCoinUsd;
        private double _venueDivergencePct;
        private double _signalMaxAgeSeconds;

        // ---- per-instance state ----
        private readonly Dictionary<string, List<int>> _dcaLevelsTriggered = new Dictionary<string, List<int>>();
        private List<double> _dcaLevels;
        private int _maxDcaBuysPer24h;
        private readonly long _dcaWindowSeconds = 24 * 60 * 60;

        private sealed class TrailState
        {
            public bool Active;
            public double Line;
            public double Peak;
            public bool WasAbove;
            public Tuple<double, double, double> SettingsSig;
        }
        private readonly Dictionary<string, TrailState> _trailingPm = new Dictionary<string, TrailState>();
        private double _trailingGapPct;
        private double _pmStartPctNoDca;
        private double _pmStartPctWithDca;
        private Tuple<double, double, double> _lastTrailingSettingsSig;

        private Dictionary<string, HashSet<string>> _botOrderIds;
        private Dictionary<string, HashSet<string>> _botOrderIdsFromHistory;
        private long? _botOrderIdsMtime;

        private PnlLedger _pnlLedger;
        private Dictionary<string, double> _costBasis = new Dictionary<string, double>();
        private bool _needsLedgerSeedFromOrders = true;

        private readonly Dictionary<string, JObject> _lastGoodBidAsk = new Dictionary<string, JObject>();
        private AccountSnapshot _lastGoodAccountSnapshot = new AccountSnapshot();

        private readonly Dictionary<string, List<double>> _dcaBuyTs = new Dictionary<string, List<double>>();
        private readonly Dictionary<string, double> _dcaLastSellTs = new Dictionary<string, double>();

        private sealed class AccountSnapshot
        {
            public double? TotalAccountValue;
            public double? BuyingPower;
            public double? HoldingsSellValue;
            public double? HoldingsBuyValue;
            public double? PercentInTrade;
        }

        public CryptoApiTrading(string apiKey, string base64PrivateSeed)
        {
            _rh = new RobinhoodClient(apiKey, base64PrivateSeed);

            MainDir = Environment.CurrentDirectory;
            BasePaths = BuildBasePaths(MainDir, CryptoSymbols);

            _dcaLevels = new List<double>(DcaLevelsGlobal);
            _trailingGapPct = TrailingGapPctGlobal;
            _pmStartPctNoDca = PmStartPctNoDcaGlobal;
            _pmStartPctWithDca = PmStartPctWithDcaGlobal;
            _lastTrailingSettingsSig = Tuple.Create(_trailingGapPct, _pmStartPctNoDca, _pmStartPctWithDca);

            _botOrderIds = LoadBotOrderIds();
            _botOrderIdsFromHistory = LoadBotOrderIdsFromTradeHistory();
            try { _botOrderIdsMtime = File.Exists(AppPaths.BotOrderIdsPath) ? (long?)File.GetLastWriteTimeUtc(AppPaths.BotOrderIdsPath).Ticks : null; }
            catch { _botOrderIdsMtime = null; }

            _pnlLedger = LoadPnlLedger();
            ReconcilePendingOrders();

            _costBasis = CalculateCostBasis();
            InitializeDcaLevels();

            _needsLedgerSeedFromOrders = true;

            _maxDcaBuysPer24h = MaxDcaBuysPer24hGlobal;
            SeedDcaWindowFromHistory();
        }

        // ==================================================================
        // Settings hot-reload (mirrors _refresh_paths_and_symbols)
        // ==================================================================
        private static Dictionary<string, string> BuildBasePaths(string mainDir, IEnumerable<string> coins)
        {
            var outMap = new Dictionary<string, string> { ["BTC"] = mainDir };
            try
            {
                foreach (var raw in coins)
                {
                    string sym = (raw ?? "").Trim().ToUpperInvariant();
                    if (sym.Length == 0) continue;
                    if (sym == "BTC") { outMap["BTC"] = mainDir; continue; }
                    string sub = Path.Combine(mainDir, sym);
                    if (Directory.Exists(sub)) outMap[sym] = sub;
                }
            }
            catch { }
            return outMap;
        }

        private void RefreshPathsAndSymbols()
        {
            var s = _settingsLoader.Load();
            long? mtime = s.Mtime;
            if (mtime == null) return;
            if (_lastSettingsMtime == mtime) return;
            _lastSettingsMtime = mtime;

            var coins = (s.Coins != null && s.Coins.Count > 0) ? s.Coins : CryptoSymbols;
            string mndir = string.IsNullOrEmpty(s.MainNeuralDir) ? MainDir : s.MainNeuralDir;

            TradeStartLevel = Math.Max(1, Math.Min(s.TradeStartLevel, 7));
            StartAllocPct = Math.Max(0.0, s.StartAllocationPct);
            DcaMultiplier = Math.Max(0.0, s.DcaMultiplier);
            DcaLevelsGlobal = (s.DcaLevels != null && s.DcaLevels.Count > 0) ? s.DcaLevels : DcaLevelsGlobal;
            MaxDcaBuysPer24hGlobal = Math.Max(0, s.MaxDcaBuysPer24h);

            TrailingGapPctGlobal = Math.Max(0.0, s.TrailingGapPct);
            PmStartPctNoDcaGlobal = Math.Max(0.0, s.PmStartPctNoDca);
            PmStartPctWithDcaGlobal = Math.Max(0.0, s.PmStartPctWithDca);
            LthProfitAllocPct = Math.Max(0.0, Math.Min(100.0, s.LthProfitAllocPct));

            LongTermSymbols.Clear();
            if (s.LongTermHoldings != null)
                foreach (var v in s.LongTermHoldings)
                {
                    string sym = (v ?? "").Trim().ToUpperInvariant();
                    if (sym.Length > 0) LongTermSymbols.Add(sym);
                }

            // opt-in safety features
            bool wasPaper = _paperMode;
            _paperMode = s.PaperMode;
            _catastrophicStopPct = Math.Max(0.0, s.CatastrophicStopPct);
            _maxCapitalPerCoinUsd = Math.Max(0.0, s.MaxCapitalPerCoinUsd);
            _venueDivergencePct = Math.Max(0.0, s.VenueDivergencePct);
            _signalMaxAgeSeconds = Math.Max(0.0, s.SignalMaxAgeSeconds);
            if (_paperMode && !wasPaper)
                Console.WriteLine("*** PAPER MODE ENABLED — intended orders are logged to hub_data/paper_orders.jsonl, nothing is sent. ***");

            if (!Directory.Exists(mndir)) mndir = Environment.CurrentDirectory;

            CryptoSymbols = new List<string>(coins);
            MainDir = mndir;
            BasePaths = BuildBasePaths(MainDir, CryptoSymbols);
        }

        private string FolderFor(string sym)
        {
            sym = (sym ?? "").Trim().ToUpperInvariant();
            if (BasePaths != null && BasePaths.TryGetValue(sym, out var f)) return f;
            return sym == "BTC" ? MainDir : Path.Combine(MainDir, sym);
        }

        // ==================================================================
        // Bot order ownership
        // ==================================================================
        private Dictionary<string, HashSet<string>> LoadBotOrderIds()
        {
            var outMap = new Dictionary<string, HashSet<string>>();
            try
            {
                var raw = JsonStore.ReadJObject(AppPaths.BotOrderIdsPath);
                if (raw == null) return outMap;
                foreach (var prop in raw.Properties())
                {
                    string sym = prop.Name.Trim().ToUpperInvariant();
                    if (sym.Length == 0) continue;
                    if (prop.Value is JArray arr)
                    {
                        var set = new HashSet<string>();
                        foreach (var x in arr) { string s = (x?.ToString() ?? "").Trim(); if (s.Length > 0) set.Add(s); }
                        outMap[sym] = set;
                    }
                }
            }
            catch { }
            return outMap;
        }

        private void SaveBotOrderIds()
        {
            try
            {
                var data = new JObject();
                foreach (var kv in _botOrderIds)
                {
                    if (string.IsNullOrEmpty(kv.Key) || kv.Value == null || kv.Value.Count == 0) continue;
                    var arr = new JArray(kv.Value.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct().OrderBy(x => x));
                    data[kv.Key.Trim().ToUpperInvariant()] = arr;
                }
                JsonStore.AtomicWriteJsonWithBackup(AppPaths.BotOrderIdsPath, data);
            }
            catch { }
        }

        /// <summary>trade_history.jsonl is bot-only; keep order_ids after the most recent bot SELL per coin.</summary>
        private Dictionary<string, HashSet<string>> LoadBotOrderIdsFromTradeHistory()
        {
            var outMap = new Dictionary<string, HashSet<string>>();
            try
            {
                if (!File.Exists(AppPaths.TradeHistoryPath)) return outMap;

                var lastSellTs = new Dictionary<string, double>();
                var rows = new List<JObject>();
                foreach (var line in File.ReadLines(AppPaths.TradeHistoryPath))
                {
                    string l = (line ?? "").Trim();
                    if (l.Length == 0) continue;
                    JObject obj;
                    try { obj = JObject.Parse(l); } catch { continue; }

                    string side = (Js.Str(obj, "side", "") ?? "").ToLowerInvariant().Trim();
                    string symFull = (Js.Str(obj, "symbol", "") ?? "").Trim().ToUpperInvariant();
                    string bas = symFull.Length > 0 ? symFull.Split('-')[0].Trim() : "";
                    if (bas.Length == 0) continue;

                    rows.Add(obj);
                    if (side != "sell") continue;
                    double tsF = Js.Double(obj, "ts", 0.0);
                    double prev = lastSellTs.TryGetValue(bas, out var pv) ? pv : 0.0;
                    if (tsF > prev) lastSellTs[bas] = tsF;
                }

                foreach (var obj in rows)
                {
                    string oid = (Js.Str(obj, "order_id", "") ?? "").Trim();
                    if (oid.Length == 0) continue;
                    string symFull = (Js.Str(obj, "symbol", "") ?? "").Trim().ToUpperInvariant();
                    string bas = symFull.Length > 0 ? symFull.Split('-')[0].Trim() : "";
                    if (bas.Length == 0) continue;
                    double tsF = Js.Double(obj, "ts", 0.0);
                    double lastSell = lastSellTs.TryGetValue(bas, out var ls) ? ls : 0.0;
                    if (tsF > lastSell)
                    {
                        if (!outMap.TryGetValue(bas, out var set)) { set = new HashSet<string>(); outMap[bas] = set; }
                        set.Add(oid);
                    }
                }
            }
            catch { }
            return outMap;
        }

        private void MarkBotOrderId(string baseSymbol, string orderId)
        {
            try
            {
                string bas = (baseSymbol ?? "").Trim().ToUpperInvariant();
                string oid = (orderId ?? "").Trim();
                if (bas.Length == 0 || oid.Length == 0) return;
                if (!_botOrderIds.TryGetValue(bas, out var set)) { set = new HashSet<string>(); _botOrderIds[bas] = set; }
                set.Add(oid);
                SaveBotOrderIds();
            }
            catch { }
        }

        private void ClearBotOrderIdsForCoin(string baseSymbol)
        {
            try
            {
                string bas = (baseSymbol ?? "").Trim().ToUpperInvariant();
                if (bas.Length == 0) return;
                _botOrderIds.Remove(bas);
                SaveBotOrderIds();
            }
            catch { }
        }

        private bool IsBotOrderId(string baseSymbol, string orderId)
        {
            string bas = (baseSymbol ?? "").Trim().ToUpperInvariant();
            string oid = (orderId ?? "").Trim();
            if (bas.Length == 0 || oid.Length == 0) return false;
            if (_botOrderIds.TryGetValue(bas, out var a) && a.Contains(oid)) return true;
            if (_botOrderIdsFromHistory.TryGetValue(bas, out var b) && b.Contains(oid)) return true;
            return false;
        }

        private bool MaybeReloadBotOrderIds()
        {
            long? mtime;
            try { mtime = File.Exists(AppPaths.BotOrderIdsPath) ? (long?)File.GetLastWriteTimeUtc(AppPaths.BotOrderIdsPath).Ticks : null; }
            catch { mtime = null; }
            if (mtime == _botOrderIdsMtime) return false;
            _botOrderIdsMtime = mtime;

            try { _botOrderIds = LoadBotOrderIds(); } catch { _botOrderIds = new Dictionary<string, HashSet<string>>(); }
            try { _botOrderIdsFromHistory = LoadBotOrderIdsFromTradeHistory(); } catch { _botOrderIdsFromHistory = new Dictionary<string, HashSet<string>>(); }
            try { _costBasis = CalculateCostBasis(); } catch { }
            try { InitializeDcaLevels(); } catch { }
            return true;
        }

        // ==================================================================
        // PnL ledger (with .bak / .tmp recovery)
        // ==================================================================
        private PnlLedger LoadPnlLedger()
        {
            var data = JsonStore.ReadJObject(AppPaths.PnlLedgerPath);
            if (data != null) return PnlLedger.Upgrade(data);

            var bak = JsonStore.ReadJObject(AppPaths.PnlLedgerPath + ".bak");
            if (bak != null)
            {
                var upgraded = PnlLedger.Upgrade(bak);
                try { JsonStore.AtomicWriteJsonWithBackup(AppPaths.PnlLedgerPath, upgraded); } catch { }
                return upgraded;
            }

            var tmp = JsonStore.ReadJObject(AppPaths.PnlLedgerPath + ".tmp");
            if (tmp != null)
            {
                var upgraded = PnlLedger.Upgrade(tmp);
                try { JsonStore.AtomicWriteJsonWithBackup(AppPaths.PnlLedgerPath, upgraded); } catch { }
                return upgraded;
            }

            return new PnlLedger { LastUpdatedTs = TimeUtil.UnixNow() };
        }

        private void SavePnlLedger()
        {
            try
            {
                _pnlLedger.LastUpdatedTs = TimeUtil.UnixNow();
                JsonStore.AtomicWriteJsonWithBackup(AppPaths.PnlLedgerPath, _pnlLedger);
            }
            catch { }
        }

        // ==================================================================
        // Order fill / fee extraction
        // ==================================================================
        /// <summary>Returns (filledQty, avgFillPrice?) — avg price may be null.</summary>
        private (double qty, double? avgPrice) ExtractFillFromOrder(JObject order)
        {
            try
            {
                var execs = order["executions"] as JArray ?? new JArray();
                double totalQty = 0.0, totalNotional = 0.0;
                foreach (var ex in execs)
                {
                    double q = Js.Double(ex, "quantity", 0.0);
                    double p = Js.Double(ex, "effective_price", 0.0);
                    if (q > 0.0 && p > 0.0) { totalQty += q; totalNotional += q * p; }
                }
                double? avg = (totalQty > 0.0 && totalNotional > 0.0) ? (double?)(totalNotional / totalQty) : null;

                if (totalQty <= 0.0)
                {
                    foreach (var k in new[] { "filled_asset_quantity", "filled_quantity", "asset_quantity", "quantity" })
                        if (Js.Has(order, k)) { double v = Js.Double(order, k, 0.0); if (v > 0.0) { totalQty = v; break; } }
                }
                if (avg == null)
                {
                    foreach (var k in new[] { "average_price", "avg_price", "price", "effective_price" })
                        if (Js.Has(order, k)) { double v = Js.Double(order, k, 0.0); if (v > 0.0) { avg = v; break; } }
                }
                return (totalQty, avg);
            }
            catch { return (0.0, null); }
        }

        /// <summary>Returns (filledQty, avgPrice?, notionalUsd?, feesUsd?) with cent-accurate notional.</summary>
        private (double qty, double? avgPrice, double? notionalUsd, double? feesUsd) ExtractAmountsAndFees(JObject order)
        {
            try
            {
                var execs = order["executions"] as JArray ?? new JArray();
                double feeTotal = 0.0; bool feeFound = false;
                foreach (var ex in execs)
                    foreach (var fk in new[] { "fee", "fees", "fee_amount", "fee_usd", "fee_in_usd" })
                        if (Js.Has(ex, fk)) { feeFound = true; feeTotal += FeeToFloat(ex[fk]); }
                foreach (var fk in new[] { "fee", "fees", "fee_amount", "fee_usd", "fee_in_usd" })
                    if (Js.Has(order, fk)) { feeFound = true; feeTotal += FeeToFloat(order[fk]); }
                double? feesUsd = feeFound ? (double?)feeTotal : null;

                decimal avgPd = ToDecimal(order["average_price"]);
                decimal filledQd = ToDecimal(order["filled_asset_quantity"]);
                double? avgFillPrice = avgPd > 0 ? (double?)(double)avgPd : null;
                double filledQty = filledQd > 0 ? (double)filledQd : 0.0;

                double? notionalUsd = null;
                if (avgPd > 0 && filledQd > 0)
                    notionalUsd = (double)UsdCents(avgPd * filledQd);

                if (notionalUsd == null)
                {
                    decimal totalNotionalD = 0m, totalQtyD = 0m;
                    foreach (var ex in execs)
                    {
                        decimal qd = ToDecimal(ex["quantity"]);
                        decimal pd = ToDecimal(ex["effective_price"]);
                        if (qd > 0 && pd > 0) { totalQtyD += qd; totalNotionalD += qd * pd; }
                    }
                    if (totalQtyD > 0 && avgFillPrice == null) avgFillPrice = (double)(totalNotionalD / totalQtyD);
                    if (totalNotionalD > 0) notionalUsd = (double)UsdCents(totalNotionalD);
                    if (filledQty <= 0.0 && totalQtyD > 0) filledQty = (double)totalQtyD;
                }

                return (filledQty, avgFillPrice, notionalUsd, feesUsd);
            }
            catch { return (0.0, null, null, null); }
        }

        private static double FeeToFloat(JToken v)
        {
            try
            {
                if (v == null || v.Type == JTokenType.Null) return 0.0;
                if (v.Type == JTokenType.Integer || v.Type == JTokenType.Float) return (double)v;
                if (v.Type == JTokenType.String) return PyCompat.ToDouble((string)v);
                if (v is JArray arr) { double s = 0.0; foreach (var x in arr) s += FeeToFloat(x); return s; }
                if (v is JObject o)
                    foreach (var k in new[] { "usd_amount", "amount", "value", "fee", "quantity" })
                        if (o[k] != null) return PyCompat.ToDouble(o[k].ToString());
                return 0.0;
            }
            catch { return 0.0; }
        }

        private static decimal ToDecimal(JToken x)
        {
            try
            {
                if (x == null || x.Type == JTokenType.Null) return 0m;
                return decimal.Parse(x.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            catch { return 0m; }
        }

        private static decimal UsdCents(decimal d) => Math.Round(d, 2, MidpointRounding.AwayFromZero);

        // ==================================================================
        // API wrappers
        // ==================================================================
        private JObject GetAccount() => _rh.GetAccount();
        private JObject GetHoldings() => _rh.GetHoldings();

        private JArray GetTradingPairs()
        {
            var resp = _rh.GetTradingPairs();
            var results = resp?["results"] as JArray;
            return results ?? new JArray();
        }

        private JObject GetOrders(string symbol) => _rh.GetOrders(symbol);

        /// <summary>get_price -> (buyPrices, sellPrices, validSymbols). Falls back to cached bid/ask.</summary>
        private (Dictionary<string, double> buy, Dictionary<string, double> sell, List<string> valid) GetPrice(IEnumerable<string> symbols)
        {
            var buy = new Dictionary<string, double>();
            var sell = new Dictionary<string, double>();
            var valid = new List<string>();

            foreach (var symbol in symbols)
            {
                if (symbol == "USDC-USD") continue;
                var response = _rh.GetBestBidAsk(symbol) as JObject;
                var results = response?["results"] as JArray;
                if (results != null && results.Count > 0)
                {
                    var result = results[0] as JObject;
                    double ask = Js.Double(result, "ask_inclusive_of_buy_spread", 0.0);
                    double bid = Js.Double(result, "bid_inclusive_of_sell_spread", 0.0);
                    buy[symbol] = ask; sell[symbol] = bid; valid.Add(symbol);
                    try { _lastGoodBidAsk[symbol] = new JObject { ["ask"] = ask, ["bid"] = bid, ["ts"] = TimeUtil.UnixNow() }; } catch { }
                }
                else if (_lastGoodBidAsk.TryGetValue(symbol, out var cached))
                {
                    double ask = Js.Double(cached, "ask", 0.0);
                    double bid = Js.Double(cached, "bid", 0.0);
                    if (ask > 0.0 && bid > 0.0) { buy[symbol] = ask; sell[symbol] = bid; valid.Add(symbol); }
                }
            }
            return (buy, sell, valid);
        }

        // ==================================================================
        // Signal file reads (per-coin folder)
        // ==================================================================
        private int ReadLongDcaSignal(string symbol) => ReadIntSignal(symbol, "long_dca_signal.txt");
        private int ReadShortDcaSignal(string symbol) => ReadIntSignal(symbol, "short_dca_signal.txt");

        private int ReadIntSignal(string symbol, string file)
        {
            try
            {
                string path = Path.Combine(FolderFor(symbol), file);
                string raw = File.ReadAllText(path).Trim();
                return (int)PyCompat.ToDouble(raw, 0.0);
            }
            catch { return 0; }
        }

        /// <summary>Reads low_bound_prices.html and returns LONG (blue) levels highest-&gt;lowest (N1..N7).</summary>
        private List<double> ReadLongPriceLevels(string symbol)
        {
            try
            {
                string path = Path.Combine(FolderFor(symbol), "low_bound_prices.html");
                string raw = (File.ReadAllText(path) ?? "").Trim();
                if (raw.Length == 0) return new List<double>();
                raw = raw.Trim().Trim('[', ']', '(', ')');
                raw = raw.Replace(",", " ").Replace(";", " ").Replace("|", " ").Replace("\n", " ").Replace("\t", " ");
                var vals = new List<double>();
                foreach (var p in raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    if (PyCompat.TryDouble(p, out double v)) vals.Add(v);

                var outList = new List<double>();
                var seen = new HashSet<double>();
                foreach (var v in vals)
                {
                    double k = Math.Round(v, 12);
                    if (seen.Contains(k)) continue;
                    seen.Add(k); outList.Add(v);
                }
                outList.Sort(); outList.Reverse();
                return outList;
            }
            catch { return new List<double>(); }
        }

        // ==================================================================
        // Misc helpers
        // ==================================================================
        private bool TradeHistoryHasOrderId(string orderId)
        {
            try
            {
                if (string.IsNullOrEmpty(orderId) || !File.Exists(AppPaths.TradeHistoryPath)) return false;
                foreach (var line in File.ReadLines(AppPaths.TradeHistoryPath))
                {
                    string l = (line ?? "").Trim();
                    if (l.Length == 0) continue;
                    JObject obj;
                    try { obj = JObject.Parse(l); } catch { continue; }
                    if ((Js.Str(obj, "order_id", "") ?? "").Trim() == orderId.Trim()) return true;
                }
            }
            catch { return false; }
            return false;
        }

        private JObject GetOrderById(string symbol, string orderId)
        {
            try
            {
                var orders = GetOrders(symbol);
                var results = orders?["results"] as JArray;
                if (results == null) return null;
                foreach (var o in results)
                    if (o is JObject oo && (Js.Str(oo, "id") == orderId)) return oo;
            }
            catch { }
            return null;
        }

        public static string FmtPrice(double price)
        {
            double p = price;
            if (p == 0) return "0";
            double ap = Math.Abs(p);
            int decimals;
            if (ap >= 1.0) decimals = 2;
            else { decimals = (int)(-Math.Floor(Math.Log10(ap))) + 3; decimals = Math.Max(2, Math.Min(12, decimals)); }
            string s = p.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            if (s.Contains(".")) s = s.TrimEnd('0').TrimEnd('.');
            return s;
        }
    }
}
