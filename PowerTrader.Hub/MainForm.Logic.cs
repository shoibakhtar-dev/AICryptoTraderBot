using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;
using Newtonsoft.Json.Linq;

namespace PowerTrader.Hub
{
    internal sealed partial class MainForm
    {
        private int _tickCount;

        private void SafeTick()
        {
            try { Tick(); } catch { /* keep the UI alive */ }
        }

        private void Tick()
        {
            _tickCount++;

            // drain logs
            AppendLog(_runnerLog, _neural?.DrainNew());
            AppendLog(_traderLog, _trader?.DrainNew());
            foreach (var t in _trainers.Values.ToList()) AppendLog(_trainerLog, t.DrainNew());

            // auto-start trader gate
            if (_autoStartTraderPending)
            {
                if (_neural == null || !_neural.IsRunning) _autoStartTraderPending = false;
                else
                {
                    var rr = ReadRunnerReady();
                    if (rr != null && rr["ready"] != null && (bool)rr["ready"])
                    {
                        _autoStartTraderPending = false;
                        if (_trader == null || !_trader.IsRunning) StartTrader();
                    }
                }
            }

            RefreshAccountAndTrades();
            RefreshTiles();
            RefreshTrainingStatus();

            if (_tickCount % 4 == 0) RefreshAccountChart();
            if (_tickCount % 4 == 0) RefreshCandleChart();

            UpdateStatusLabel();
        }

        private void UpdateStatusLabel()
        {
            string ns = _neural != null && _neural.IsRunning ? "Neural:ON" : "Neural:off";
            string ts = _trader != null && _trader.IsRunning ? "Trader:ON" : "Trader:off";
            int trn = _trainers.Values.Count(t => t.IsRunning);
            _lblStatus.Text = $"{ns}  {ts}  Trainers:{trn}" + (_autoStartTraderPending ? "  (waiting for runner ready)" : "");
        }

        private static void AppendLog(TextBox box, string text)
        {
            if (string.IsNullOrEmpty(text) || box == null) return;
            if (box.TextLength > 200_000) box.Clear();
            box.AppendText(text);
        }

        // ==============================================================
        // Account + trades
        // ==============================================================
        private void RefreshAccountAndTrades()
        {
            var status = ReadJson(HubFile("trader_status.json"));
            var ledger = ReadJson(HubFile("pnl_ledger.json"));

            double realized = ledger?["total_realized_profit_usd"] != null ? (double)ledger["total_realized_profit_usd"] : 0.0;
            double lthBucket = ledger?["lth_profit_bucket_usd"] != null ? (double)ledger["lth_profit_bucket_usd"] : 0.0;

            if (status?["account"] is JObject acct)
            {
                double tv = D(acct, "total_account_value");
                double bp = D(acct, "buying_power");
                double hv = D(acct, "holdings_sell_value");
                double pit = D(acct, "percent_in_trade");
                bool paper = acct["paper_mode"] != null && acct["paper_mode"].Type == JTokenType.Boolean && (bool)acct["paper_mode"];
                string paperTag = paper ? "  [PAPER MODE]" : "";
                _lblAccount.Text = $"Account: {Money(tv)}   Buying Power: {Money(bp)}   Holdings: {Money(hv)}   In Trade: {pit:F2}%   Realized PnL: {Money(realized)}   LTH bucket: {Money(lthBucket)}{paperTag}";
                _lblAccount.ForeColor = paper ? Theme.Accent : Theme.Fg;
            }
            else
            {
                _lblAccount.Text = "Account: N/A (trader not running or no status yet)   Realized PnL: " + Money(realized);
            }

            // trades grid from positions
            var positions = status?["positions"] as JObject;
            _tradesGrid.SuspendLayout();
            _tradesGrid.Rows.Clear();
            if (positions != null)
            {
                foreach (var p in positions.Properties())
                {
                    if (!(p.Value is JObject pos)) continue;
                    double qty = D(pos, "quantity");
                    double reserved = D(pos, "lth_reserved_qty");
                    if (qty <= 0.0 && reserved <= 0.0) continue; // skip idle tracked coins with nothing

                    _tradesGrid.Rows.Add(
                        p.Name,
                        qty > 0 ? qty.ToString("0.########", CultureInfo.InvariantCulture) : "-",
                        Price(D(pos, "avg_cost_basis")),
                        Price(D(pos, "current_buy_price")),
                        Price(D(pos, "current_sell_price")),
                        Pct(D(pos, "gain_loss_pct_buy")),
                        Pct(D(pos, "gain_loss_pct_sell")),
                        Money(D(pos, "value_usd")),
                        ((int)D(pos, "dca_triggered_stages")).ToString(),
                        S(pos, "next_dca_display"),
                        Price(D(pos, "trail_line")),
                        reserved > 0 ? reserved.ToString("0.########", CultureInfo.InvariantCulture) : "-");
                }
            }
            _tradesGrid.ResumeLayout();
        }

        private void RefreshTiles()
        {
            foreach (var c in _coins)
            {
                if (!_tiles.TryGetValue(c, out var tile)) continue;
                string folder = CoinFolder(c);
                int lng = ReadIntFile(Path.Combine(folder, "long_dca_signal.txt"));
                int sht = ReadIntFile(Path.Combine(folder, "short_dca_signal.txt"));
                tile.SetValues(lng, sht);
            }
        }

        private void RefreshTrainingStatus()
        {
            var map = TrainingStatusMap();
            _trainingStatusLbl.Text = "Training: " + string.Join("  ", _coins.Select(c => c + "=" + (map.TryGetValue(c, out var v) ? v : "?")));
        }

        private Dictionary<string, string> TrainingStatusMap()
        {
            var running = new HashSet<string>(RunningTrainers());
            var outMap = new Dictionary<string, string>();
            foreach (var c in _coins)
            {
                if (running.Contains(c)) outMap[c] = "TRAINING";
                else outMap[c] = CoinIsTrained(c) ? "TRAINED" : "NOT TRAINED";
            }
            return outMap;
        }

        private List<string> RunningTrainers()
        {
            var outList = new List<string>();
            foreach (var kv in _trainers) if (kv.Value.IsRunning) outList.Add(kv.Key);
            foreach (var c in _coins)
            {
                string folder = CoinFolder(c);
                if (!Directory.Exists(folder)) continue;
                var st = ReadJson(Path.Combine(folder, "trainer_status.json"));
                if (st != null && string.Equals(st["state"]?.ToString(), "TRAINING", StringComparison.OrdinalIgnoreCase))
                {
                    string stamp = Path.Combine(folder, "trainer_last_training_time.txt");
                    string statusPath = Path.Combine(folder, "trainer_status.json");
                    try
                    {
                        if (File.Exists(stamp) && File.Exists(statusPath) &&
                            File.GetLastWriteTimeUtc(stamp) >= File.GetLastWriteTimeUtc(statusPath))
                            continue;
                    }
                    catch { }
                    if (!outList.Contains(c)) outList.Add(c);
                }
            }
            return outList.Distinct().ToList();
        }

        private bool CoinIsTrained(string coin)
        {
            coin = coin.Trim().ToUpperInvariant();
            string folder = CoinFolder(coin);
            if (!Directory.Exists(folder)) return false;
            var st = ReadJson(Path.Combine(folder, "trainer_status.json"));
            if (st != null && string.Equals(st["state"]?.ToString(), "TRAINING", StringComparison.OrdinalIgnoreCase)) return false;
            string stamp = Path.Combine(folder, "trainer_last_training_time.txt");
            try
            {
                if (!File.Exists(stamp)) return false;
                string raw = (File.ReadAllText(stamp) ?? "").Trim();
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double ts) || ts <= 0) return false;
                double ageDays = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds - ts;
                return ageDays <= 14.0 * 24 * 60 * 60;
            }
            catch { return false; }
        }

        // ==============================================================
        // Orchestration
        // ==============================================================
        private void StartAllScripts()
        {
            bool allTrained = _coins.Count > 0 && _coins.All(CoinIsTrained);
            if (!allTrained)
            {
                MessageBox.Show("All coins must be trained before starting Neural Runner.\n\nUse Train All first.",
                    "Training required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _autoStartTraderPending = true;
            StartNeural();
        }

        private void StartNeural()
        {
            ResetRunnerReady("starting");
            _neural.Start("", _projectDir, _hubDir, "[RUNNER] ");
        }

        private void StartTrader()
        {
            // bot-order ownership picker (best-effort; needs creds). Cancel => don't start.
            try
            {
                if (!BotOrderPicker.EnsureForCurrentHoldings(this, _projectDir, _hubDir))
                    return;
            }
            catch { }
            _trader.Start("", _projectDir, _hubDir, "[TRADER] ");
        }

        private void StopAllScripts()
        {
            _autoStartTraderPending = false;
            _neural?.Stop();
            _trader?.Stop();
            ResetRunnerReady("stopped");
        }

        private void TrainAllCoins()
        {
            foreach (var c in _coins) StartTrainerForCoin(c);
        }

        private void TrainSelectedCoin()
        {
            string coin = (_coinCombo.SelectedItem?.ToString() ?? "").Trim().ToUpperInvariant();
            if (coin.Length > 0) StartTrainerForCoin(coin);
        }

        private void StartTrainerForCoin(string coin)
        {
            coin = coin.Trim().ToUpperInvariant();
            if (coin.Length == 0) return;

            // stop neural before training (it reads artifacts the trainer rewrites)
            _neural?.Stop();

            string coinCwd = CoinFolder(coin);
            try { if (!Directory.Exists(coinCwd)) Directory.CreateDirectory(coinCwd); } catch { }

            if (_trainers.TryGetValue(coin, out var existing) && existing.IsRunning) return;

            // fresh training: delete prior training artifacts (mirrors the Python)
            try
            {
                var patterns = new[] { "trainer_last_training_time.txt", "trainer_status.json", "trainer_last_start_time.txt",
                    "killer.txt", "memories_*.txt", "memory_weights_*.txt", "neural_perfect_threshold_*.txt" };
                foreach (var pat in patterns)
                    foreach (var fp in Directory.EnumerateFiles(coinCwd, pat))
                        try { File.Delete(fp); } catch { }
            }
            catch { }

            var proc = new EngineProcess("Trainer-" + coin, _trainerExe, coin);
            proc.Start(coin, coinCwd, _hubDir, "[" + coin + "] ");
            _trainers[coin] = proc;
        }

        // ==============================================================
        // runner_ready
        // ==============================================================
        private void ResetRunnerReady(string stage)
        {
            try
            {
                var o = new JObject { ["timestamp"] = UnixNow(), ["ready"] = false, ["stage"] = stage };
                File.WriteAllText(HubFile("runner_ready.json"), o.ToString());
            }
            catch { }
        }

        private JObject ReadRunnerReady()
        {
            var o = ReadJson(HubFile("runner_ready.json"));
            return o ?? new JObject { ["ready"] = false };
        }

        private void OpenSettings()
        {
            using (var dlg = new SettingsForm(_settings, _projectDir))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _settings.Save();
                    // reload coins / folders / tiles
                    _coins = _settings.Coins;
                    string mainDir = _settings.Str("main_neural_dir", _projectDir).Trim();
                    if (!string.IsNullOrEmpty(mainDir) && Directory.Exists(mainDir)) _projectDir = mainDir;
                    RebuildCoinFolders();
                    PopulateCoinCombos();
                }
            }
        }

        private void OnClosingHub()
        {
            try { _tick?.Stop(); } catch { }
            try { _neural?.Stop(); } catch { }
            try { _trader?.Stop(); } catch { }
            foreach (var t in _trainers.Values) try { t.Stop(); } catch { }
        }

        // ==============================================================
        // Charts
        // ==============================================================
        private void RefreshAccountChart()
        {
            try
            {
                string path = HubFile("account_value_history.jsonl");
                if (!File.Exists(path)) return;
                var pts = new List<(double ts, double v)>();
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    JObject o;
                    try { o = JObject.Parse(line); } catch { continue; }
                    if (o["ts"] == null || o["total_account_value"] == null) continue;
                    double ts = (double)o["ts"], v = (double)o["total_account_value"];
                    if (v <= 0) continue;
                    pts.Add((ts, v));
                }
                if (pts.Count == 0) return;
                pts.Sort((a, b) => a.ts.CompareTo(b.ts));
                if (pts.Count > 250) pts = Downsample(pts, 250);

                _acctChart.Series.Clear();
                var s = new Series("acct") { ChartType = SeriesChartType.Line, Color = Theme.Accent2, BorderWidth = 2, XValueType = ChartValueType.Auto };
                for (int i = 0; i < pts.Count; i++) s.Points.AddXY(i, pts[i].v);
                _acctChart.Series.Add(s);
                _acctChart.ChartAreas[0].AxisY.LabelStyle.Format = "C2";
                _acctChart.Titles.Clear();
                _acctChart.Titles.Add(new Title($"Account Value ({Money(pts[pts.Count - 1].v)})", Docking.Top, new System.Drawing.Font(FontFamily.GenericSansSerif, 9f), Theme.Fg));
            }
            catch { }
        }

        private static List<(double ts, double v)> Downsample(List<(double ts, double v)> pts, int max)
        {
            if (pts.Count <= max) return pts;
            var outList = new List<(double, double)> { pts[0] };
            var mid = pts.GetRange(1, pts.Count - 2);
            int keepMid = max - 2;
            double bucket = mid.Count / (double)keepMid;
            for (int i = 0; i < keepMid; i++)
            {
                int start = (int)(i * bucket), end = (int)((i + 1) * bucket);
                if (end <= start) end = start + 1;
                if (start >= mid.Count) break;
                if (end > mid.Count) end = mid.Count;
                var b = mid.GetRange(start, end - start);
                if (b.Count == 0) continue;
                outList.Add((b.Average(x => x.ts), b.Average(x => x.v)));
            }
            outList.Add(pts[pts.Count - 1]);
            return outList;
        }

        private void RefreshCandleChart()
        {
            try
            {
                string coin = _chartCoinCombo.SelectedItem?.ToString();
                string tf = _tfCombo.SelectedItem?.ToString();
                if (string.IsNullOrEmpty(coin) || string.IsNullOrEmpty(tf)) return;

                int limit = _settings.Int("candles_limit", 250);
                List<string[]> rows;
                try { rows = _kucoin.GetKline(coin + "-USDT", tf); }
                catch { return; }

                var candles = new List<(long ts, double o, double h, double l, double c)>();
                foreach (var r in rows)
                {
                    if (r.Length < 5) continue;
                    if (!long.TryParse(r[0], out long ts)) continue;
                    if (!TryD(r[1], out double o) || !TryD(r[2], out double cl) || !TryD(r[3], out double h) || !TryD(r[4], out double l)) continue;
                    candles.Add((ts, o, h, l, cl));
                }
                candles.Sort((a, b) => a.ts.CompareTo(b.ts));
                if (limit > 0 && candles.Count > limit) candles = candles.GetRange(candles.Count - limit, limit);
                if (candles.Count == 0) return;

                _candleChart.Series.Clear();
                var s = new Series("candles")
                {
                    ChartType = SeriesChartType.Candlestick,
                    XValueType = ChartValueType.Int32,
                    YValuesPerPoint = 4,
                };
                s["PriceUpColor"] = "#00C85A";
                s["PriceDownColor"] = "#E64646";
                s["PointWidth"] = "0.7";
                for (int i = 0; i < candles.Count; i++)
                {
                    var c = candles[i];
                    // Candlestick YValues order: [high, low, open, close]
                    s.Points.AddXY(i, c.h, c.l, c.o, c.c);
                }
                _candleChart.Series.Add(s);

                var area = _candleChart.ChartAreas[0];
                area.AxisY.StripLines.Clear();

                string folder = CoinFolder(coin);
                foreach (var lv in ReadPriceLevels(Path.Combine(folder, "low_bound_prices.html")))
                    AddStripLine(area, lv, Theme.Long);
                foreach (var lv in ReadPriceLevels(Path.Combine(folder, "high_bound_prices.html")))
                    AddStripLine(area, lv, Theme.Short);

                // overlay current bid/ask/avg/dca/trail from trader status
                var status = ReadJson(HubFile("trader_status.json"));
                if (status?["positions"] is JObject positions && positions[coin] is JObject pos)
                {
                    AddStripLine(area, D(pos, "current_buy_price"), System.Drawing.Color.MediumPurple);
                    AddStripLine(area, D(pos, "current_sell_price"), System.Drawing.Color.Teal);
                    AddStripLine(area, D(pos, "avg_cost_basis"), System.Drawing.Color.Gold);
                    AddStripLine(area, D(pos, "dca_line_price"), Theme.Red);
                    AddStripLine(area, D(pos, "trail_line"), Theme.Green);
                }

                _candleChart.Titles.Clear();
                _candleChart.Titles.Add(new Title($"{coin} ({tf})", Docking.Top, new System.Drawing.Font(FontFamily.GenericSansSerif, 9f), Theme.Fg));
            }
            catch { }
        }

        private static void AddStripLine(ChartArea area, double y, System.Drawing.Color color)
        {
            if (y <= 0 || y >= 9e15 || double.IsNaN(y) || double.IsInfinity(y)) return;
            area.AxisY.StripLines.Add(new StripLine { IntervalOffset = y, StripWidth = 0, BorderColor = color, BorderWidth = 1, BorderDashStyle = ChartDashStyle.Solid });
        }

        // ==============================================================
        // small readers / formatters
        // ==============================================================
        private static JObject ReadJson(string path)
        {
            try { return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null; }
            catch { return null; }
        }

        private static int ReadIntFile(string path)
        {
            try { return File.Exists(path) ? (int)double.Parse(File.ReadAllText(path).Trim(), CultureInfo.InvariantCulture) : 0; }
            catch { return 0; }
        }

        private static List<double> ReadPriceLevels(string path)
        {
            var outList = new List<double>();
            try
            {
                if (!File.Exists(path)) return outList;
                string raw = (File.ReadAllText(path) ?? "").Trim();
                if (raw.Length == 0) return outList;
                raw = raw.Replace(",", " ").Replace("[", " ").Replace("]", " ").Replace("'", " ");
                foreach (var tok in raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    if (TryD(tok, out double v) && v > 0 && v < 9e15) outList.Add(v);
            }
            catch { }
            return outList;
        }

        private static bool TryD(string s, out double v) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        private static double D(JObject o, string key)
        {
            var v = o?[key];
            if (v == null || v.Type == JTokenType.Null) return 0.0;
            return TryD(v.ToString(), out double d) ? d : 0.0;
        }

        private static string S(JObject o, string key)
        {
            var v = o?[key];
            return (v == null || v.Type == JTokenType.Null) ? "" : v.ToString();
        }

        private static string Money(double x)
        {
            try { return x.ToString("C2", CultureInfo.GetCultureInfo("en-US")); } catch { return "N/A"; }
        }

        private static string Pct(double x) => x.ToString("+0.00;-0.00", CultureInfo.InvariantCulture) + "%";

        private static string Price(double x)
        {
            if (x <= 0) return "-";
            double a = Math.Abs(x);
            int dec = a >= 1000 ? 2 : a >= 100 ? 3 : a >= 1 ? 4 : a >= 0.1 ? 5 : a >= 0.01 ? 6 : a >= 0.001 ? 7 : 8;
            string s = x.ToString("N" + dec, CultureInfo.InvariantCulture);
            if (s.Contains(".")) s = s.TrimEnd('0').TrimEnd('.');
            return "$" + s;
        }

        private static double UnixNow() => (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
    }
}
