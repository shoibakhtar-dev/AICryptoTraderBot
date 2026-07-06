using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using PowerTrader.Core.Config;
using PowerTrader.Core.Market;
using PowerTrader.Core.Robinhood;
using PowerTrader.Core.Util;

namespace PowerTrader.Thinker
{
    /// <summary>
    /// Faithful port of pt_thinker.py — the multi-timeframe price-prediction engine.
    ///
    /// For each coin it cycles through 7 timeframes (1h..1w), one per step. On each step it
    /// predicts that timeframe's next high/low from the trained memories (weighted kNN). Once all
    /// 7 timeframes have been refreshed it runs a full sweep: compares the current Robinhood ask to
    /// the previous sweep's bounds to emit LONG/SHORT/WITHIN per timeframe, rebuilds+spaces the
    /// bound lines, and publishes the trade-critical files:
    ///   low_bound_prices.html / high_bound_prices.html  (neural lines N1..N7)
    ///   long_dca_signal.txt / short_dca_signal.txt       (# of LONG / SHORT timeframes)
    ///   futures_long_profit_margin.txt / futures_short_profit_margin.txt
    /// It also publishes hub_data/lth_daily_ema200.json and hub_data/runner_ready.json.
    /// </summary>
    public static class ThinkerApp
    {
        private static readonly string[] TfChoices = { "1hour", "2hour", "4hour", "8hour", "12hour", "1day", "1week" };
        private const int NTf = 7;
        private const double Distance = 0.5;
        private const double LowPlaceholder = 0.01;
        private const double HighPlaceholder = 99999999999999999.0;
        private const long TrainingStaleSeconds = 14L * 24 * 60 * 60;

        private static readonly KuCoinClient Market = new KuCoinClient();
        private static readonly GuiSettingsLoader SettingsLoader = new GuiSettingsLoader();

        private static string _mainDir;
        private static RobinhoodClient _rh;
        private static double _lastLthEmaWriteTs;

        private static readonly Dictionary<string, CoinState> States = new Dictionary<string, CoinState>();
        private static readonly Dictionary<string, string> DisplayCache = new Dictionary<string, string>();
        private static readonly HashSet<string> ReadyCoins = new HashSet<string>();
        private static List<string> _currentCoins = new List<string>();
        private static List<string> _coinSymbols = new List<string>();

        public static int Run(string[] args)
        {
            _mainDir = AppPaths.BaseDir;

            _coinSymbols = SettingsLoader.Load().Coins;
            _currentCoins = new List<string>(_coinSymbols);
            foreach (var sym in _currentCoins) JsonStore.EnsureDir(CoinFolder(sym));

            WriteRunnerReady(false, "starting", new List<string>(), _currentCoins.Count);

            foreach (var sym in _currentCoins)
            {
                DisplayCache[sym] = sym + "  (starting.)";
                InitCoin(sym);
            }

            try
            {
                while (true)
                {
                    SyncCoinsFromSettings();
                    WriteLthEma200Snapshot();

                    foreach (var sym in _currentCoins)
                    {
                        try { StepCoin(sym); }
                        catch (Exception e) { Console.WriteLine("step " + sym + " error: " + e.Message); }
                    }

                    try { Console.Clear(); } catch { }
                    foreach (var sym in _currentCoins)
                    {
                        Console.WriteLine(DisplayCache.TryGetValue(sym, out var d) ? d : (sym + "  (no data yet)"));
                        Console.WriteLine("\n" + new string('-', 60) + "\n");
                    }

                    Thread.Sleep(150);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("FATAL: " + ex);
                return 1;
            }
        }

        // ------------------------------------------------------------------
        // Credentials / market data
        // ------------------------------------------------------------------
        private static RobinhoodClient Rh()
        {
            if (_rh != null) return _rh;
            string keyPath = Path.Combine(_mainDir, "r_key.txt");
            string secPath = Path.Combine(_mainDir, "r_secret.txt");
            if (!File.Exists(keyPath) || !File.Exists(secPath))
                throw new InvalidOperationException(
                    "Missing r_key.txt and/or r_secret.txt next to the engine. Run the trader once (or use the Hub API wizard) to create them.");
            string apiKey = File.ReadAllText(keyPath);
            string priv = File.ReadAllText(secPath);
            _rh = new RobinhoodClient(apiKey, priv);
            return _rh;
        }

        private static double RobinhoodCurrentAsk(string symbol) => Rh().GetCurrentAsk(symbol);

        // ------------------------------------------------------------------
        // Folder / training helpers
        // ------------------------------------------------------------------
        private static string CoinFolder(string sym) => AppPaths.CoinFolder(_mainDir, sym);

        private static bool CoinIsTrained(string sym)
        {
            try
            {
                string stamp = Path.Combine(CoinFolder(sym), "trainer_last_training_time.txt");
                if (!File.Exists(stamp)) return false;
                string raw = (File.ReadAllText(stamp) ?? string.Empty).Trim();
                if (raw.Length == 0) return false;
                double ts = PyCompat.ToDouble(raw, 0.0);
                if (ts <= 0) return false;
                return (TimeUtil.UnixNow() - ts) <= TrainingStaleSeconds;
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------
        // Coin state
        // ------------------------------------------------------------------
        private sealed class CoinState
        {
            public double[] LowBound = Filled(NTf, LowPlaceholder);
            public double[] HighBound = Filled(NTf, HighPlaceholder);
            public int TfChoiceIndex = 0;
            public string[] Messages = FilledS(NTf, "none");
            public double[] Margins = Filled(NTf, 0.25);
            public double[] HighTf = Filled(NTf, HighPlaceholder);
            public double[] LowTf = Filled(NTf, LowPlaceholder);
            public string[] TfSides = FilledS(NTf, "none");
            public string[] Messaged = FilledS(NTf, "no");
            public int[] Updated = new int[NTf];
            public string[] Perfects = FilledS(NTf, "active");
            public int[] TrainingIssues = new int[NTf];
            public int BoundsVersion = 0;
            public int LastDisplayBoundsVersion = -1;
        }

        private static double[] Filled(int n, double v) { var a = new double[n]; for (int i = 0; i < n; i++) a[i] = v; return a; }
        private static string[] FilledS(int n, string v) { var a = new string[n]; for (int i = 0; i < n; i++) a[i] = v; return a; }

        private static void InitCoin(string sym)
        {
            string folder = CoinFolder(sym);
            JsonStore.EnsureDir(folder);
            TryWrite(Path.Combine(folder, "alerts_version.txt"), "5/3/2022/9am");
            TryWrite(Path.Combine(folder, "futures_long_onoff.txt"), "OFF");
            TryWrite(Path.Combine(folder, "futures_short_onoff.txt"), "OFF");
            States[sym] = new CoinState();
        }

        private static void SyncCoinsFromSettings()
        {
            var newList = SettingsLoader.Load().Coins;
            if (newList.SequenceEqual(_currentCoins)) return;

            var added = newList.Where(c => !_currentCoins.Contains(c)).ToList();
            var removed = _currentCoins.Where(c => !newList.Contains(c)).ToList();

            foreach (var sym in removed)
            {
                ReadyCoins.Remove(sym);
                DisplayCache.Remove(sym);
            }
            foreach (var sym in added)
            {
                JsonStore.EnsureDir(CoinFolder(sym));
                DisplayCache[sym] = sym + "  (starting.)";
                try { InitCoin(sym); } catch { }
            }
            _currentCoins = new List<string>(newList);
        }

        // ------------------------------------------------------------------
        // Core step
        // ------------------------------------------------------------------
        private static void StepCoin(string sym)
        {
            string folder = CoinFolder(sym);
            string coin = AppPaths.KuCoinPair(sym);
            if (!States.TryGetValue(sym, out var st)) { InitCoin(sym); st = States[sym]; }

            // --- training freshness gate ---
            if (!CoinIsTrained(sym))
            {
                TryWrite(Path.Combine(folder, "futures_long_profit_margin.txt"), "0.25");
                TryWrite(Path.Combine(folder, "futures_short_profit_margin.txt"), "0.25");
                TryWrite(Path.Combine(folder, "long_dca_signal.txt"), "0");
                TryWrite(Path.Combine(folder, "short_dca_signal.txt"), "0");
                DisplayCache[sym] = sym + "  (NOT TRAINED / OUTDATED - run trainer)";
                ReadyCoins.Remove(sym);
                bool allReadyG = ReadyCoins.Count >= _currentCoins.Count;
                WriteRunnerReady(allReadyG, allReadyG ? "real_predictions" : "training_required",
                    ReadyCoins.OrderBy(x => x).ToList(), _currentCoins.Count);
                return;
            }

            int idx = st.TfChoiceIndex;

            // ---- fetch current candle for this timeframe ----
            double openPrice, closePrice;
            if (!FetchCurrentCandle(coin, TfChoices[idx], out openPrice, out closePrice)) return;
            double currentCandle = openPrice == 0.0 ? 0.0 : 100.0 * ((closePrice - openPrice) / openPrice);

            // ---- load threshold + memories/weights and compute predicted move ----
            double perfectThreshold = PyCompat.ToDouble(ReadTextSafe(Path.Combine(folder, "neural_perfect_threshold_" + TfChoices[idx] + ".txt")), 1.0);

            double finalMoves, highFinalMoves, lowFinalMoves;
            string perfectState;
            int trainingIssue;
            ComputeMove(folder, TfChoices[idx], currentCandle, perfectThreshold,
                out finalMoves, out highFinalMoves, out lowFinalMoves, out perfectState, out trainingIssue);

            st.Perfects[idx] = perfectState;
            st.TrainingIssues[idx] = trainingIssue;

            // persist threshold (original behavior re-writes the same value)
            TryWrite(Path.Combine(folder, "neural_perfect_threshold_" + TfChoices[idx] + ".txt"), PyCompat.Repr(perfectThreshold));

            // ---- predicted high/low prices ----
            double startPrice = closePrice;
            if (perfectState == "inactive")
            {
                st.HighTf[idx] = startPrice;
                st.LowTf[idx] = startPrice;
            }
            else
            {
                st.HighTf[idx] = startPrice + startPrice * highFinalMoves;
                st.LowTf[idx] = startPrice + startPrice * lowFinalMoves;
            }

            // ---- advance timeframe index; on full sweep compute signals ----
            idx += 1;
            if (idx >= NTf)
            {
                idx = 0;
                st.TfChoiceIndex = idx;
                FullSweep(sym, st, folder, coin);
                return;
            }
            st.TfChoiceIndex = idx;
        }

        private static bool FetchCurrentCandle(string coin, string tf, out double openPrice, out double closePrice)
        {
            openPrice = 0.0; closePrice = 0.0;
            int guard = 0;
            while (true)
            {
                List<string[]> hist;
                try { hist = Market.GetKline(coin, tf); }
                catch { Thread.Sleep(3500); if (++guard > 50) return false; continue; }

                // KuCoin returns newest-first; the engine uses index 1 (the second row).
                if (hist.Count < 2) { Thread.Sleep(200); if (++guard > 50) return false; continue; }
                var row = hist[1];
                if (row.Length < 3) { if (++guard > 50) return false; continue; }
                if (!PyCompat.TryDouble(row[1], out openPrice)) { if (++guard > 50) return false; continue; }
                if (!PyCompat.TryDouble(row[2], out closePrice)) { if (++guard > 50) return false; continue; }
                return true;
            }
        }

        private static void ComputeMove(string folder, string tf, double currentCandle, double perfectThreshold,
            out double finalMoves, out double highFinalMoves, out double lowFinalMoves, out string perfectState, out int trainingIssue)
        {
            finalMoves = 0.0; highFinalMoves = 0.0; lowFinalMoves = 0.0; perfectState = "inactive"; trainingIssue = 0;
            try
            {
                var memory = SplitClean(ReadTextSafe(Path.Combine(folder, "memories_" + tf + ".txt")), '~');
                var weight = SplitClean(ReadTextSafe(Path.Combine(folder, "memory_weights_" + tf + ".txt")), ' ');
                var highWeight = SplitClean(ReadTextSafe(Path.Combine(folder, "memory_weights_high_" + tf + ".txt")), ' ');
                var lowWeight = SplitClean(ReadTextSafe(Path.Combine(folder, "memory_weights_low_" + tf + ".txt")), ' ');

                bool anyPerfect = false;
                var moves = new List<double>();
                var highMoves = new List<double>();
                var lowMoves = new List<double>();

                for (int memInd = 0; memInd < memory.Count; memInd++)
                {
                    string[] parts = memory[memInd].Split(new[] { "{}" }, StringSplitOptions.None);
                    var patternTokens = SplitClean(parts[0], ' ');
                    if (patternTokens.Count == 0) continue;
                    double memCandle = PyCompat.ToDouble(patternTokens[0]);

                    double difference;
                    if (currentCandle == 0.0 && memCandle == 0.0) difference = 0.0;
                    else
                    {
                        double denom = (currentCandle + memCandle) / 2.0;
                        difference = denom == 0.0 ? 0.0 : Math.Abs((Math.Abs(currentCandle - memCandle) / denom) * 100.0);
                    }

                    if (difference <= perfectThreshold)
                    {
                        anyPerfect = true;
                        double highDiff = parts.Length > 1 ? PyCompat.ToDouble(parts[1]) / 100.0 : 0.0;
                        double lowDiff = parts.Length > 2 ? PyCompat.ToDouble(parts[2]) / 100.0 : 0.0;
                        double nextMove = PyCompat.ToDouble(patternTokens[patternTokens.Count - 1]);
                        double w = ToDoubleAt(weight, memInd);
                        double hw = ToDoubleAt(highWeight, memInd);
                        double lw = ToDoubleAt(lowWeight, memInd);

                        if (w != 0.0) moves.Add(nextMove * w);
                        if (hw != 0.0) highMoves.Add(highDiff * hw);
                        if (lw != 0.0) lowMoves.Add(lowDiff * lw);
                    }
                }

                if (!anyPerfect)
                {
                    perfectState = "inactive";
                }
                else
                {
                    // mean() throws on empty in Python's sum/len -> caught -> inactive
                    if (moves.Count == 0 || highMoves.Count == 0 || lowMoves.Count == 0)
                    {
                        perfectState = "inactive";
                    }
                    else
                    {
                        finalMoves = Mean(moves);
                        highFinalMoves = Mean(highMoves);
                        lowFinalMoves = Mean(lowMoves);
                        perfectState = "active";
                    }
                }
            }
            catch
            {
                trainingIssue = 1;
                finalMoves = 0.0; highFinalMoves = 0.0; lowFinalMoves = 0.0;
                perfectState = "inactive";
            }
        }

        // ------------------------------------------------------------------
        // Full sweep: messages, bounds rebuild, signals
        // ------------------------------------------------------------------
        private static void FullSweep(string sym, CoinState st, string folder, string coin)
        {
            double current;
            int guard = 0;
            while (true)
            {
                try { current = RobinhoodCurrentAsk(sym + "-USD"); break; }
                catch (Exception e) { Console.WriteLine(e.Message); Thread.Sleep(1000); if (++guard > 600) return; }
            }

            int boundsVersionUsed = st.BoundsVersion;

            // --- per-timeframe messages using the PREVIOUS sweep's bounds ---
            for (int inder = 0; inder < NTf; inder++)
            {
                if (current > st.HighBound[inder] && st.HighTf[inder] != st.LowTf[inder])
                {
                    string message = "SHORT on " + TfChoices[inder] + " timeframe. " +
                        (((st.HighBound[inder] - current) / Math.Abs(current)) * 100.0).ToString("F8", CultureInfo.InvariantCulture) +
                        " High Boundary: " + PyCompat.Repr(st.HighBound[inder]);
                    st.Messaged[inder] = "yes";
                    st.Margins[inder] = ((st.HighTf[inder] - current) / Math.Abs(current)) * 100.0;
                    st.Updated[inder] = st.Messages[inder].Contains("SHORT") ? 0 : 1;
                    st.Messages[inder] = message;
                    st.TfSides[inder] = "short";
                }
                else if (current < st.LowBound[inder] && st.HighTf[inder] != st.LowTf[inder])
                {
                    string message = "LONG on " + TfChoices[inder] + " timeframe. " +
                        (((st.LowBound[inder] - current) / Math.Abs(current)) * 100.0).ToString("F8", CultureInfo.InvariantCulture) +
                        " Low Boundary: " + PyCompat.Repr(st.LowBound[inder]);
                    st.Messaged[inder] = "yes";
                    st.Margins[inder] = ((st.LowTf[inder] - current) / Math.Abs(current)) * 100.0;
                    st.TfSides[inder] = "long";
                    st.Updated[inder] = st.Messages[inder].Contains("LONG") ? 0 : 1;
                    st.Messages[inder] = message;
                }
                else
                {
                    string message;
                    if (st.Perfects[inder] == "inactive")
                    {
                        message = (st.TrainingIssues[inder] == 1
                            ? "INACTIVE (training data issue) on " + TfChoices[inder] + " timeframe."
                            : "INACTIVE on " + TfChoices[inder] + " timeframe.") +
                            " Low Boundary: " + PyCompat.Repr(st.LowBound[inder]) + " High Boundary: " + PyCompat.Repr(st.HighBound[inder]);
                    }
                    else
                    {
                        message = "WITHIN on " + TfChoices[inder] + " timeframe." +
                            " Low Boundary: " + PyCompat.Repr(st.LowBound[inder]) + " High Boundary: " + PyCompat.Repr(st.HighBound[inder]);
                    }
                    st.Margins[inder] = 0.0;
                    st.Updated[inder] = (message == st.Messages[inder]) ? 0 : 1;
                    st.Messages[inder] = message;
                    st.TfSides[inder] = "none";
                    st.Messaged[inder] = "no";
                }
            }

            // --- rebuild bounds ---
            var lowBound = new List<double>();
            var highBound = new List<double>();
            for (int i = 0; i < NTf; i++)
            {
                double newLow = st.LowTf[i] - (st.LowTf[i] * (Distance / 100.0));
                double newHigh = st.HighTf[i] + (st.HighTf[i] * (Distance / 100.0));
                if (st.Perfects[i] != "inactive") { lowBound.Add(newLow); highBound.Add(newHigh); }
                else { lowBound.Add(LowPlaceholder); highBound.Add(HighPlaceholder); }
            }

            var newLowSorted = new List<double>(lowBound); newLowSorted.Sort(); newLowSorted.Reverse(); // descending
            var newHighSorted = new List<double>(highBound); newHighSorted.Sort(); // ascending

            // map sorted position -> original index (first occurrence, like list.index)
            var ogLowIndex = new List<int>();
            var ogHighIndex = new List<int>();
            for (int i = 0; i < lowBound.Count; i++)
            {
                ogLowIndex.Add(IndexOfExact(lowBound, newLowSorted[i]));
                ogHighIndex.Add(IndexOfExact(highBound, newHighSorted[i]));
            }

            SpaceBounds(newLowSorted, newHighSorted);

            // reconstruct original-order bounds for next sweep's message logic
            var recLow = new List<double>();
            var recHigh = new List<double>();
            for (int ogIndex = 0; ogIndex < newLowSorted.Count; ogIndex++)
            {
                int posL = ogLowIndex.IndexOf(ogIndex);
                if (posL >= 0) recLow.Add(newLowSorted[posL]);
                int posH = ogHighIndex.IndexOf(ogIndex);
                if (posH >= 0) recHigh.Add(newHighSorted[posH]);
            }
            st.LowBound = PadTo(recLow, NTf, LowPlaceholder);
            st.HighBound = PadTo(recHigh, NTf, HighPlaceholder);

            st.BoundsVersion = boundsVersionUsed + 1;

            // write the sorted+spaced bound lines (what the trader reads as N1..N7)
            TryWrite(Path.Combine(folder, "low_bound_prices.html"), JoinCommaSpace(newLowSorted));
            TryWrite(Path.Combine(folder, "high_bound_prices.html"), JoinCommaSpace(newHighSorted));

            // --- display + readiness ---
            DisplayCache[sym] = sym + "  " + PyCompat.Repr(current) + "\n\n" + string.Join("\n", st.Messages);
            st.LastDisplayBoundsVersion = boundsVersionUsed;
            if (st.LastDisplayBoundsVersion >= 1 && IsPrintingRealPredictions(st.Messages)) ReadyCoins.Add(sym);
            else ReadyCoins.Remove(sym);

            bool allReady = ReadyCoins.Count >= _coinSymbols.Count;
            WriteRunnerReady(allReady, allReady ? "real_predictions" : "warming_up",
                ReadyCoins.OrderBy(x => x).ToList(), _coinSymbols.Count);

            // --- PM + DCA signals ---
            int longs = st.TfSides.Count(s => s == "long");
            int shorts = st.TfSides.Count(s => s == "short");

            var currentPms = st.Margins.Where(m => m != 0.0).ToList();
            double pm = currentPms.Count > 0 ? currentPms.Average() : 0.25;
            if (pm < 0.25) pm = 0.25;

            TryWrite(Path.Combine(folder, "futures_long_profit_margin.txt"), PyCompat.Repr(pm));
            TryWrite(Path.Combine(folder, "long_dca_signal.txt"), longs.ToString(CultureInfo.InvariantCulture));
            TryWrite(Path.Combine(folder, "futures_short_profit_margin.txt"), PyCompat.Repr(Math.Abs(pm)));
            TryWrite(Path.Combine(folder, "short_dca_signal.txt"), shorts.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>Gap-spacing loop matching the Python: enforce a growing minimum % gap between adjacent lines.</summary>
        private static void SpaceBounds(List<double> newLow, List<double> newHigh)
        {
            int ogIndex = 0;
            double gapModifier = 0.0;
            long safety = 0;
            const long SafetyCap = 5_000_000;
            while (ogIndex < newLow.Count - 1)
            {
                if (++safety > SafetyCap) break; // defensive: guarantees termination
                bool placeholder = newLow[ogIndex] == LowPlaceholder || newLow[ogIndex + 1] == LowPlaceholder
                    || newHigh[ogIndex] == HighPlaceholder || newHigh[ogIndex + 1] == HighPlaceholder;
                if (!placeholder)
                {
                    double lowPerc = PercDiff(newLow[ogIndex], newLow[ogIndex + 1]);
                    double highPerc = PercDiff(newHigh[ogIndex], newHigh[ogIndex + 1]);

                    if (lowPerc < 0.25 + gapModifier || newLow[ogIndex + 1] > newLow[ogIndex])
                    {
                        newLow[ogIndex + 1] = newLow[ogIndex + 1] - (newLow[ogIndex + 1] * 0.0005);
                        continue; // redo same ogIndex, gapModifier unchanged
                    }
                    if (highPerc < 0.25 + gapModifier || newHigh[ogIndex + 1] < newHigh[ogIndex])
                    {
                        newHigh[ogIndex + 1] = newHigh[ogIndex + 1] + (newHigh[ogIndex + 1] * 0.0005);
                        continue;
                    }
                }
                ogIndex += 1;
                gapModifier += 0.25;
            }
        }

        private static double PercDiff(double a, double b)
        {
            double denom = (a + b) / 2.0;
            if (denom == 0.0) return 0.0;
            return Math.Abs((Math.Abs(a - b) / denom) * 100.0);
        }

        private static bool IsPrintingRealPredictions(string[] messages)
        {
            foreach (var m in messages)
            {
                if (m == null) continue;
                if (m.StartsWith("WITHIN") || m.StartsWith("LONG") || m.StartsWith("SHORT")) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------
        // LTH daily EMA200 snapshot
        // ------------------------------------------------------------------
        private static void WriteLthEma200Snapshot()
        {
            double now = TimeUtil.UnixNow();
            if ((now - _lastLthEmaWriteTs) < 5.0) return;
            _lastLthEmaWriteTs = now;

            var syms = SettingsLoader.Load().LongTermHoldings;
            var coins = new Dictionary<string, object>();
            foreach (var sym in syms)
            {
                if (!ComputeDailyEma200(sym, out double ema200, out double price, out double? pct)) continue;
                coins[sym] = new Dictionary<string, object>
                {
                    ["ema200"] = ema200,
                    ["price"] = price,
                    ["pct_from_ema200"] = (object)pct ?? null,
                };
            }
            var payload = new Dictionary<string, object> { ["ts"] = now, ["coins"] = coins };
            try { JsonStore.AtomicWriteJson(AppPaths.LthEma200Path, payload, indent: false); } catch { }
        }

        private static bool ComputeDailyEma200(string sym, out double ema200, out double priceUsed, out double? pct)
        {
            ema200 = 0.0; priceUsed = 0.0; pct = null;
            string coin = AppPaths.KuCoinPair(sym);
            List<string[]> history;
            try { history = Market.GetKline(coin, "1day"); }
            catch { return false; }

            var closes = new List<double>();
            foreach (var row in history)
                if (row.Length >= 3 && PyCompat.TryDouble(row[2], out double c)) closes.Add(c);
            if (closes.Count == 0) return false;

            var closesRev = new List<double>(closes); closesRev.Reverse();
            double? ema = Ema(closesRev, 200);
            if (ema == null) return false;
            ema200 = ema.Value;

            double lastClose = closesRev[closesRev.Count - 1];
            priceUsed = lastClose;
            try { priceUsed = RobinhoodCurrentAsk(sym + "-USD"); }
            catch { priceUsed = lastClose; }

            if (ema200 <= 0.0) { pct = null; return true; }
            pct = (priceUsed - ema200) / ema200 * 100.0;
            return true;
        }

        private static double? Ema(List<double> values, int period)
        {
            if (values.Count < period) return null;
            double alpha = 2.0 / (period + 1.0);
            double ema = 0.0;
            for (int i = 0; i < period; i++) ema += values[i];
            ema /= period;
            for (int i = period; i < values.Count; i++) ema = (values[i] * alpha) + (ema * (1.0 - alpha));
            return ema;
        }

        // ------------------------------------------------------------------
        // runner_ready
        // ------------------------------------------------------------------
        private static void WriteRunnerReady(bool ready, string stage, List<string> readyCoins, int totalCoins)
        {
            var obj = new Dictionary<string, object>
            {
                ["timestamp"] = TimeUtil.UnixNow(),
                ["ready"] = ready,
                ["stage"] = stage,
                ["ready_coins"] = readyCoins ?? new List<string>(),
                ["total_coins"] = totalCoins,
            };
            JsonStore.AtomicWriteJson(AppPaths.RunnerReadyPath, obj);
        }

        // ------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------
        private static string ReadTextSafe(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch { return null; }
        }

        private static void TryWrite(string path, string content)
        {
            try { File.WriteAllText(path, content ?? string.Empty, new UTF8Encoding(false)); }
            catch { }
        }

        private static List<string> SplitClean(string raw, char sep)
        {
            var outList = new List<string>();
            if (string.IsNullOrEmpty(raw)) return outList;
            string cleaned = raw.Replace("'", "").Replace(",", "").Replace("\"", "").Replace("]", "").Replace("[", "");
            foreach (var part in cleaned.Split(sep)) outList.Add(part);
            return outList;
        }

        private static double Mean(List<double> xs)
        {
            double s = 0.0; foreach (var x in xs) s += x; return s / xs.Count;
        }

        private static double ToDoubleAt(List<string> list, int idx)
        {
            if (idx < 0 || idx >= list.Count) return 0.0;
            return PyCompat.ToDouble(list[idx]);
        }

        private static int IndexOfExact(List<double> list, double value)
        {
            for (int i = 0; i < list.Count; i++) if (list[i] == value) return i;
            return -1;
        }

        private static double[] PadTo(List<double> src, int n, double fill)
        {
            var a = new double[n];
            for (int i = 0; i < n; i++) a[i] = i < src.Count ? src[i] : fill;
            return a;
        }

        private static string JoinCommaSpace(List<double> xs)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < xs.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(PyCompat.Repr(xs[i]));
            }
            return sb.ToString();
        }
    }
}
