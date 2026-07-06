using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using PowerTrader.Core.Config;
using PowerTrader.Core.Market;
using PowerTrader.Core.Util;

namespace PowerTrader.Trainer
{
    /// <summary>
    /// Faithful port of pt_trainer.py's kNN memory builder.
    ///
    /// The trainer walks a coin's candle history forward one candle at a time. For each step it
    /// builds a 1-candle "pattern" (the candle body % move), finds all stored memories whose body %
    /// is within perfect_threshold, and predicts the next candle's close/high/low as the weighted
    /// average of those memories' recorded next moves. It then observes the actual next candle and
    /// either (a) nudges the weights of the matched memories up/down, or (b) if nothing matched,
    /// stores the pattern + observed outcome as a NEW memory.
    ///
    /// Outputs (byte-compatible with the Python, consumed by the thinker):
    ///   memories_{tf}.txt              entries joined by '~': "&lt;body%&gt; &lt;nextMove%&gt;{}&lt;high%&gt;{}&lt;low%&gt;"
    ///   memory_weights_{tf}.txt        space-separated close weights
    ///   memory_weights_high_{tf}.txt   space-separated high weights
    ///   memory_weights_low_{tf}.txt    space-separated low weights
    ///   neural_perfect_threshold_{tf}.txt
    ///   trainer_last_start_time.txt, trainer_last_training_time.txt, trainer_status.json
    ///
    /// All file paths are relative to the current working directory (the Hub launches the trainer
    /// with the coin folder as its working directory, exactly like the Python).
    /// </summary>
    public static class TrainerApp
    {
        private static readonly string[] TfChoices = { "1hour", "2hour", "4hour", "8hour", "12hour", "1day", "1week" };
        private static readonly int[] TfMinutes = { 60, 120, 240, 480, 720, 1440, 10080 };

        private const int NumberOfCandles = 2;   // number_of_candles[0]
        private const int CandlesToPredict = 1;

        private static readonly KuCoinClient Market = new KuCoinClient();

        // Per-timeframe RAM cache of memory + weights (mirrors _memory_cache).
        private sealed class MemCache
        {
            public List<string> Memory = new List<string>();
            public List<string> Weight = new List<string>();
            public List<string> HighWeight = new List<string>();
            public List<string> LowWeight = new List<string>();
            public bool Dirty;
        }

        private static readonly Dictionary<string, MemCache> Cache = new Dictionary<string, MemCache>();
        private static readonly Dictionary<string, double> LastThresholdWritten = new Dictionary<string, double>();

        private static string _argCoin = "BTC";
        private static long _trainerStartedAt;

        public static int Run(string[] args)
        {
            try
            {
                _argCoin = "BTC";
                if (args != null && args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
                    _argCoin = args[0].Trim().ToUpperInvariant();

                string coinChoice = _argCoin + "-USDT";
                _trainerStartedAt = TimeUtil.UnixNowSeconds();
                WriteStatus("TRAINING", null);

                RunTraining(coinChoice);
                return 0;
            }
            catch (TrainerExit ex)
            {
                return ex.Code;
            }
            catch (Exception ex)
            {
                Console.WriteLine("FATAL: " + ex);
                return 1;
            }
        }

        private sealed class TrainerExit : Exception { public int Code; public TrainerExit(int c) { Code = c; } }

        // ------------------------------------------------------------------
        // Memory file IO (mirrors load_memory / flush_memory / thresholds)
        // ------------------------------------------------------------------
        private static MemCache LoadMemory(string tf)
        {
            if (Cache.TryGetValue(tf, out var c)) return c;
            c = new MemCache
            {
                Memory = SplitClean(ReadTextSafe($"memories_{tf}.txt"), '~'),
                Weight = SplitClean(ReadTextSafe($"memory_weights_{tf}.txt"), ' '),
                HighWeight = SplitClean(ReadTextSafe($"memory_weights_high_{tf}.txt"), ' '),
                LowWeight = SplitClean(ReadTextSafe($"memory_weights_low_{tf}.txt"), ' '),
            };
            Cache[tf] = c;
            return c;
        }

        private static void FlushMemory(string tf, bool force = false)
        {
            if (!Cache.TryGetValue(tf, out var c)) return;
            if (!c.Dirty && !force) return;
            TryWrite($"memories_{tf}.txt", string.Join("~", NonEmpty(c.Memory)));
            TryWrite($"memory_weights_{tf}.txt", string.Join(" ", NonEmpty(c.Weight)));
            TryWrite($"memory_weights_high_{tf}.txt", string.Join(" ", NonEmpty(c.HighWeight)));
            TryWrite($"memory_weights_low_{tf}.txt", string.Join(" ", NonEmpty(c.LowWeight)));
            c.Dirty = false;
        }

        private static void WriteThresholdSometimes(string tf, double threshold, long loopI, int every = 200)
        {
            bool have = LastThresholdWritten.TryGetValue(tf, out double last);
            if ((loopI % every != 0) && have && Math.Abs(threshold - last) < 0.05) return;
            TryWrite($"neural_perfect_threshold_{tf}.txt", PyCompat.Repr(threshold));
            LastThresholdWritten[tf] = threshold;
        }

        private static bool ShouldStopTraining(long loopI, int every = 50)
        {
            if (loopI % every != 0) return false;
            try
            {
                string raw = ReadTextSafe("killer.txt");
                return (raw ?? string.Empty).Trim().ToLowerInvariant() == "yes";
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------
        // Main training loop (per timeframe, 3-pass walk-forward)
        // ------------------------------------------------------------------
        private static void RunTraining(string coinChoice)
        {
            int theBigIndex = 0;
            int restartedYet = 0;

            while (true) // OUTER: per timeframe / per restart
            {
                string tf = TfChoices[theBigIndex];
                MemCache mem = LoadMemory(tf);

                // timeframe selection: pass 0 warms up on 1hour, passes 1/2 use tf.
                string[] tfList = { "1hour", tf, tf };
                int choiceIndex = Array.IndexOf(TfChoices, tf);
                int[] minutesList = { 60, TfMinutes[choiceIndex], TfMinutes[choiceIndex] };

                string timeframe = restartedYet < 2 ? tfList[restartedYet] : tfList[2];
                int timeframeMinutes = restartedYet < 2 ? minutesList[restartedYet] : minutesList[2];

                // ---- gather full history (paging backwards; reprocess mode is always "yes") ----
                var historyRows = GatherHistory(coinChoice, timeframe, timeframeMinutes);
                if (historyRows.Count == 0)
                {
                    // Nothing to train on; advance like a completed pass.
                    if (AdvanceTimeframe(ref theBigIndex, ref restartedYet, tf)) return;
                    continue;
                }

                // ---- parse candle series (older half onward), oldest -> newest ----
                int index;
                if (timeframe == "1day" || timeframe == "1week")
                    index = restartedYet == 0 ? historyRows.Count / 2 : 1;
                else
                    index = historyRows.Count / 2;

                var openList = new List<double>();
                var closeList = new List<double>();
                var highList = new List<double>();
                var lowList = new List<double>();
                for (int i = index; i < historyRows.Count; i++)
                {
                    var row = historyRows[i];
                    if (row.Length < 5) continue;
                    if (!PyCompat.TryDouble(row[1], out double o)) continue;
                    if (!PyCompat.TryDouble(row[2], out double cl)) continue;
                    if (!PyCompat.TryDouble(row[3], out double h)) continue;
                    if (!PyCompat.TryDouble(row[4], out double l)) continue;
                    openList.Add(o); closeList.Add(cl); highList.Add(h); lowList.Add(l);
                }
                openList.Reverse(); closeList.Reverse(); highList.Reverse(); lowList.Reverse();

                if (closeList.Count < NumberOfCandles + 2)
                {
                    if (AdvanceTimeframe(ref theBigIndex, ref restartedYet, tf)) return;
                    continue;
                }

                double perfectThreshold = 1.0;
                long loopI = 0;
                int priceListLength = restartedYet < 2 ? 10 : (int)(closeList.Count * 0.5);
                if (priceListLength < NumberOfCandles) priceListLength = NumberOfCandles;

                bool restarting = false;

                while (!restarting) // (B) training iterations
                {
                    loopI++;

                    // Prediction window is close2[0 .. priceListLength). predLen is what we predict from.
                    int predLen = priceListLength;
                    if (predLen > closeList.Count || predLen < NumberOfCandles)
                    {
                        // Can't build the window -> end this pass and advance the timeframe.
                        theBigIndex += 1; restartedYet = 0; restarting = true;
                        if (AdvanceCheckExit(theBigIndex)) return;
                        continue;
                    }

                    var open2 = Slice(openList, predLen);
                    var close2 = Slice(closeList, predLen);
                    var change = PctChange(close2, open2);

                    if (ShouldStopTraining(loopI))
                        FinishAndExit(tf, lastStartTimeStamp: _gatherStartTimeStamp);

                    // ---- matching pattern (length NumberOfCandles-1 == 1) ----
                    var currentPattern = TailChanges(change, NumberOfCandles - 1);

                    // ---- match against memories ----
                    MatchResult match = MatchMemories(mem, currentPattern, perfectThreshold);

                    // ---- adapt threshold ----
                    if (match.Unweighted.Count > 20)
                    {
                        perfectThreshold -= (perfectThreshold < 0.1) ? 0.001 : 0.01;
                        if (perfectThreshold < 0.0) perfectThreshold = 0.0;
                    }
                    else
                    {
                        perfectThreshold += (perfectThreshold < 0.1) ? 0.001 : 0.01;
                        if (perfectThreshold > 100.0) perfectThreshold = 100.0;
                    }
                    WriteThresholdSometimes(tf, perfectThreshold, loopI);

                    // ---- prediction ----
                    double startPrice = close2[close2.Count - 1];
                    var storedPattern = new List<double>(currentPattern);

                    // ---- walk forward one candle (price_list_length += 1) ----
                    priceListLength += 1;

                    // Pass-boundary checks use the PRE-increment window length (predLen), matching Python.
                    if (restartedYet < 2 && predLen >= (int)(closeList.Count * 0.25))
                    {
                        restartedYet += 1; restarting = true; continue;
                    }
                    if (predLen >= closeList.Count)
                    {
                        theBigIndex += 1; restartedYet = 0; restarting = true;
                        if (AdvanceCheckExit(theBigIndex)) return;
                        continue;
                    }

                    // Observe the newly-included candle at index predLen (0-based).
                    int actualIdx = predLen;
                    double price2 = closeList[actualIdx];
                    double highPrice2 = highList[actualIdx];
                    double lowPrice2 = lowList[actualIdx];

                    double absStart = Math.Abs(startPrice);
                    if (absStart == 0.0) continue;

                    double percDiffNowActual = (price2 - startPrice) / absStart * 100.0;
                    double highPercDiffNowActual = (highPrice2 - startPrice) / absStart * 100.0;
                    double lowPercDiffNowActual = (lowPrice2 - startPrice) / absStart * 100.0;

                    double thisDiff = (price2 - startPrice) / absStart * 100.0;
                    double highThisDiff = (highPrice2 - startPrice) / absStart * 100.0;
                    double lowThisDiff = (lowPrice2 - startPrice) / absStart * 100.0;

                    if (match.Matched.Count > 0)
                    {
                        // update the weights of matched memories
                        for (int indy = 0; indy < match.Unweighted.Count; indy++)
                        {
                            double var3 = match.Moves[indy] * 100.0;
                            double highVar3 = match.HighMoves[indy] * 100.0;
                            double lowVar3 = match.LowMoves[indy] * 100.0;

                            double highNewWeight = AdjustHigh(highPercDiffNowActual, highVar3, match.HighMoveWeights[indy]);
                            double lowNewWeight = AdjustLow(lowPercDiffNowActual, lowVar3, match.LowMoveWeights[indy]);
                            double newWeight = AdjustClose(percDiffNowActual, var3, match.MoveWeights[indy]);

                            int dex = match.PerfectDexs[indy];
                            SetAt(mem.Weight, dex, PyCompat.Repr(newWeight));
                            SetAt(mem.HighWeight, dex, PyCompat.Repr(highNewWeight));
                            SetAt(mem.LowWeight, dex, PyCompat.Repr(lowNewWeight));
                            mem.Dirty = true;
                        }
                        if (loopI % 200 == 0) FlushMemory(tf);
                    }
                    else
                    {
                        // no matches -> store a new memory entry
                        storedPattern.Add(thisDiff);
                        string entry = JoinPattern(storedPattern) + "{}" + PyCompat.Repr(highThisDiff) + "{}" + PyCompat.Repr(lowThisDiff);
                        mem.Memory.Add(entry);
                        mem.Weight.Add("1.0");
                        mem.HighWeight.Add("1.0");
                        mem.LowWeight.Add("1.0");
                        mem.Dirty = true;
                        if (loopI % 200 == 0) FlushMemory(tf);
                    }
                }

                // restarted -> loop OUTER again (possibly next timeframe)
            }
        }

        private static long _gatherStartTimeStamp;

        private static List<string[]> GatherHistory(string coinChoice, string timeframe, int timeframeMinutes)
        {
            var historyList = new List<string[]>();
            long startTime = TimeUtil.UnixNowSeconds();
            _gatherStartTimeStamp = startTime;
            long endTime = startTime - (1500L * timeframeMinutes) * 60L;
            int listLen = 0;

            while (true)
            {
                Thread.Sleep(500); // match pt_trainer.py's per-page pacing; avoids KuCoin rate limiting

                List<string[]> history;
                try
                {
                    history = Market.GetKline(coinChoice, timeframe, endTime, startTime);
                }
                catch (Exception)
                {
                    Thread.Sleep(3500);
                    continue;
                }

                foreach (var row in history) historyList.Add(row);

                int currentChange = historyList.Count - listLen;
                Console.WriteLine("gathering history");
                if (currentChange < 1000) break;

                listLen = historyList.Count;
                startTime = endTime;
                endTime = startTime - (1500L * timeframeMinutes) * 60L;
            }
            return historyList;
        }

        // ------------------------------------------------------------------
        // Matching + weight adjustment
        // ------------------------------------------------------------------
        private sealed class MatchResult
        {
            public List<int> Matched = new List<int>();
            public List<int> PerfectDexs = new List<int>();
            public List<double> Unweighted = new List<double>();
            public List<double> Moves = new List<double>();
            public List<double> HighMoves = new List<double>();
            public List<double> LowMoves = new List<double>();
            public List<double> MoveWeights = new List<double>();
            public List<double> HighMoveWeights = new List<double>();
            public List<double> LowMoveWeights = new List<double>();
            public double FinalMoves;
            public double HighFinalMoves;
            public double LowFinalMoves;
        }

        private static MatchResult MatchMemories(MemCache mem, List<double> currentPattern, double perfectThreshold)
        {
            var r = new MatchResult();
            int n = mem.Memory.Count;
            for (int memInd = 0; memInd < n; memInd++)
            {
                string entry = mem.Memory[memInd];
                string[] parts = entry.Split(new[] { "{}" }, StringSplitOptions.None);
                var patternTokens = SplitClean(parts[0], ' ');
                if (patternTokens.Count == 0) continue;

                // compare each candle in the (length-1) pattern
                var checks = new List<double>();
                for (int checkDex = 0; checkDex < currentPattern.Count; checkDex++)
                {
                    if (checkDex >= patternTokens.Count) break;
                    double cur = currentPattern[checkDex];
                    double memCandle = PyCompat.ToDouble(patternTokens[checkDex]);
                    double difference;
                    if (cur + memCandle == 0.0) difference = 0.0;
                    else
                    {
                        double denom = (cur + memCandle) / 2.0;
                        difference = denom == 0.0 ? 0.0 : Math.Abs((Math.Abs(cur - memCandle) / denom) * 100.0);
                    }
                    checks.Add(difference);
                }
                if (checks.Count == 0) continue;
                double diffAvg = Mean(checks);

                if (diffAvg <= perfectThreshold)
                {
                    double highDiff = parts.Length > 1 ? PyCompat.ToDouble(parts[1]) / 100.0 : 0.0;
                    double lowDiff = parts.Length > 2 ? PyCompat.ToDouble(parts[2]) / 100.0 : 0.0;
                    double nextMove = PyCompat.ToDouble(patternTokens[patternTokens.Count - 1]);

                    double w = ToDoubleAt(mem.Weight, memInd);
                    double hw = ToDoubleAt(mem.HighWeight, memInd);
                    double lw = ToDoubleAt(mem.LowWeight, memInd);

                    r.Unweighted.Add(nextMove);
                    r.MoveWeights.Add(w);
                    r.HighMoveWeights.Add(hw);
                    r.LowMoveWeights.Add(lw);
                    r.Moves.Add(nextMove * w);
                    r.HighMoves.Add(highDiff * hw);
                    r.LowMoves.Add(lowDiff * lw);
                    r.PerfectDexs.Add(memInd);
                    r.Matched.Add(memInd);
                }
            }

            if (r.Matched.Count == 0)
            {
                r.FinalMoves = 0.0; r.HighFinalMoves = 0.0; r.LowFinalMoves = 0.0;
            }
            else
            {
                r.FinalMoves = Mean(r.Moves);
                r.HighFinalMoves = Mean(r.HighMoves);
                r.LowFinalMoves = Mean(r.LowMoves);
            }
            return r;
        }

        private static double AdjustHigh(double actual, double var3, double weight)
        {
            if (actual > var3 + var3 * 0.1) return Math.Min(2.0, weight + 0.25);
            if (actual < var3 - var3 * 0.1) return Math.Max(0.0, weight - 0.25);
            return weight;
        }

        private static double AdjustLow(double actual, double var3, double weight)
        {
            if (actual < var3 - var3 * 0.1) return Math.Min(2.0, weight + 0.25);
            if (actual > var3 + var3 * 0.1) return Math.Max(0.0, weight - 0.25);
            return weight;
        }

        private static double AdjustClose(double actual, double var3, double weight)
        {
            if (actual > var3 + var3 * 0.1) return Math.Min(2.0, weight + 0.25);
            if (actual < var3 - var3 * 0.1) return Math.Max(-2.0, weight - 0.25);
            return weight;
        }

        // ------------------------------------------------------------------
        // Completion / status
        // ------------------------------------------------------------------
        private static bool AdvanceTimeframe(ref int theBigIndex, ref int restartedYet, string tf)
        {
            FlushMemory(tf, true);
            theBigIndex += 1;
            restartedYet = 0;
            return AdvanceCheckExit(theBigIndex);
        }

        private static bool AdvanceCheckExit(int theBigIndex)
        {
            if (theBigIndex >= TfChoices.Length)
            {
                // number_of_candles has a single entry -> finished all timeframes.
                FinishAll();
                return true;
            }
            return false;
        }

        private static void FinishAll()
        {
            for (int i = 0; i < TfChoices.Length; i++) FlushMemory(TfChoices[i], true);
            TryWrite("trainer_last_start_time.txt", _gatherStartTimeStamp.ToString(CultureInfo.InvariantCulture));
            long finished = TimeUtil.UnixNowSeconds();
            TryWrite("trainer_last_training_time.txt", finished.ToString(CultureInfo.InvariantCulture));
            WriteStatus("FINISHED", finished);
            Console.WriteLine("Finished processing all timeframes. Exiting.");
        }

        private static void FinishAndExit(string tf, long lastStartTimeStamp)
        {
            Console.WriteLine("finished processing");
            TryWrite("trainer_last_start_time.txt", lastStartTimeStamp.ToString(CultureInfo.InvariantCulture));
            long finished = TimeUtil.UnixNowSeconds();
            TryWrite("trainer_last_training_time.txt", finished.ToString(CultureInfo.InvariantCulture));
            WriteStatus("FINISHED", finished);
            FlushMemory(tf, true);
            throw new TrainerExit(0);
        }

        private static void WriteStatus(string state, long? finishedAt)
        {
            try
            {
                var obj = new Dictionary<string, object>
                {
                    ["coin"] = _argCoin,
                    ["state"] = state,
                    ["started_at"] = _trainerStartedAt,
                    ["timestamp"] = finishedAt ?? _trainerStartedAt,
                };
                if (finishedAt.HasValue) obj["finished_at"] = finishedAt.Value;
                File.WriteAllText("trainer_status.json", JsonConvert.SerializeObject(obj), new UTF8Encoding(false));
            }
            catch { }
        }

        // ------------------------------------------------------------------
        // small helpers
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

        private static IEnumerable<string> NonEmpty(List<string> src)
        {
            foreach (var x in src) if (!string.IsNullOrEmpty(x) && x.Trim().Length > 0) yield return x;
        }

        private static List<double> Slice(List<double> src, int length)
        {
            var outList = new List<double>(Math.Max(0, Math.Min(length, src.Count)));
            for (int i = 0; i < length && i < src.Count; i++) outList.Add(src[i]);
            return outList;
        }

        private static List<double> PctChange(List<double> series, List<double> opens)
        {
            var outList = new List<double>(series.Count);
            for (int i = 0; i < series.Count && i < opens.Count; i++)
            {
                double o = opens[i];
                outList.Add(o == 0.0 ? 0.0 : 100.0 * ((series[i] - o) / o));
            }
            return outList;
        }

        private static List<double> TailChanges(List<double> change, int count)
        {
            var outList = new List<double>();
            int start = change.Count - count;
            if (start < 0) start = 0;
            for (int i = start; i < change.Count; i++) outList.Add(change[i]);
            return outList;
        }

        private static string JoinPattern(List<double> pattern)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < pattern.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(PyCompat.Repr(pattern[i]));
            }
            return sb.ToString();
        }

        private static double Mean(List<double> xs)
        {
            if (xs.Count == 0) return 0.0;
            double s = 0.0; foreach (var x in xs) s += x; return s / xs.Count;
        }

        private static double ToDoubleAt(List<string> list, int idx)
        {
            if (idx < 0 || idx >= list.Count) return 0.0;
            return PyCompat.ToDouble(list[idx]);
        }

        private static void SetAt(List<string> list, int idx, string val)
        {
            while (list.Count <= idx) list.Add("1.0");
            list[idx] = val;
        }
    }
}
