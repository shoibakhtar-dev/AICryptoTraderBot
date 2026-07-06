using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;
using Newtonsoft.Json.Linq;
using PowerTrader.Core.Market;

namespace PowerTrader.Hub
{
    /// <summary>
    /// PowerTrader Hub (WinForms port of pt_hub.py's PowerTraderHub).
    /// Orchestrates the trainer/thinker/trader executables and visualizes their output files.
    /// </summary>
    internal sealed partial class MainForm : Form
    {
        private HubSettings _settings;
        private string _projectDir;   // where the engine exes + creds live (main neural dir)
        private string _hubDir;
        private List<string> _coins = new List<string>();
        private Dictionary<string, string> _coinFolders = new Dictionary<string, string>();

        private EngineProcess _neural;
        private EngineProcess _trader;
        private readonly Dictionary<string, EngineProcess> _trainers = new Dictionary<string, EngineProcess>();

        private bool _autoStartTraderPending;

        // engine exe paths
        private string _thinkerExe, _traderExe, _trainerExe;

        // UI
        private Label _lblAccount, _lblStatus;
        private ComboBox _coinCombo;
        private DataGridView _tradesGrid;
        private FlowLayoutPanel _tilePanel;
        private readonly Dictionary<string, NeuralTile> _tiles = new Dictionary<string, NeuralTile>();
        private TextBox _runnerLog, _traderLog, _trainerLog;
        private Chart _acctChart, _candleChart;
        private ComboBox _chartCoinCombo, _tfCombo;
        private Label _trainingStatusLbl;
        private Timer _tick;
        private readonly KuCoinClient _kucoin = new KuCoinClient();

        public MainForm()
        {
            Text = "PowerTrader - Hub";
            Width = 1400; Height = 840;
            MinimumSize = new Size(1000, 660);
            StartPosition = FormStartPosition.CenterScreen;

            LoadSettingsAndPaths();
            BuildLayout();
            Theme.Apply(this);

            _neural = new EngineProcess("Neural Runner", _thinkerExe);
            _trader = new EngineProcess("Trader", _traderExe);

            ResetRunnerReady("stopped");

            _tick = new Timer { Interval = 1000 };
            _tick.Tick += (s, e) => SafeTick();
            _tick.Start();

            FormClosing += (s, e) => OnClosingHub();
        }

        // ==============================================================
        // Settings / paths
        // ==============================================================
        private void LoadSettingsAndPaths()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string settingsPath = Path.Combine(exeDir, "gui_settings.json");
            _settings = HubSettings.Load(settingsPath);

            string mainDir = _settings.Str("main_neural_dir", "").Trim();
            if (!string.IsNullOrEmpty(mainDir) && !Path.IsPathRooted(mainDir))
                mainDir = Path.GetFullPath(Path.Combine(exeDir, mainDir));
            if (string.IsNullOrEmpty(mainDir) || !Directory.Exists(mainDir)) mainDir = exeDir;
            _projectDir = mainDir;
            _settings.Set("main_neural_dir", mainDir);

            string hubDir = _settings.Str("hub_data_dir", "");
            if (string.IsNullOrEmpty(hubDir)) hubDir = Path.Combine(_projectDir, "hub_data");
            _hubDir = Path.GetFullPath(hubDir);
            try { Directory.CreateDirectory(_hubDir); } catch { }

            _coins = _settings.Coins;
            RebuildCoinFolders();

            _thinkerExe = EngineLocator.Resolve("pt_thinker.exe");
            _traderExe = EngineLocator.Resolve("pt_trader.exe");
            _trainerExe = EngineLocator.Resolve("pt_trainer.exe");
        }

        private void RebuildCoinFolders()
        {
            _coinFolders = new Dictionary<string, string>();
            foreach (var c in _coins)
            {
                string sym = c.Trim().ToUpperInvariant();
                _coinFolders[sym] = sym == "BTC" ? _projectDir : Path.Combine(_projectDir, sym);
            }
        }

        private string CoinFolder(string sym)
        {
            sym = (sym ?? "").Trim().ToUpperInvariant();
            return _coinFolders.TryGetValue(sym, out var f) ? f : (sym == "BTC" ? _projectDir : Path.Combine(_projectDir, sym));
        }

        private string HubFile(string name) => Path.Combine(_hubDir, name);

        // ==============================================================
        // Layout
        // ==============================================================
        private void BuildLayout()
        {
            // --- top toolbar ---
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(6, 6, 6, 6), WrapContents = false, AutoScroll = true };
            var btnTrainAll = MakeBtn("Train All", (s, e) => TrainAllCoins());
            var btnTrainSel = MakeBtn("Train Selected", (s, e) => TrainSelectedCoin());
            _coinCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
            var btnStartAll = MakeBtn("Start All", (s, e) => StartAllScripts());
            var btnStopAll = MakeBtn("Stop All", (s, e) => StopAllScripts());
            var btnSettings = MakeBtn("Settings", (s, e) => OpenSettings());
            _lblStatus = new Label { Text = "Idle", AutoSize = true, Padding = new Padding(10, 8, 0, 0), ForeColor = Theme.Accent2 };
            toolbar.Controls.AddRange(new Control[] { btnTrainAll, btnTrainSel, _coinCombo, btnStartAll, btnStopAll, btnSettings, _lblStatus });

            // --- account summary ---
            _lblAccount = new Label { Dock = DockStyle.Top, Height = 26, Text = "Account: N/A", Padding = new Padding(10, 4, 0, 0), Font = new Font(FontFamily.GenericMonospace, 9.5f) };

            // --- main split: left (charts/trades/tiles) | right (logs) ---
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 940 };

            // left side vertical split
            var leftSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 430 };

            // charts area (tabs)
            var chartTabs = new TabControl { Dock = DockStyle.Fill };
            var candleTab = new TabPage("Coin Chart");
            var acctTab = new TabPage("Account Value");

            // candle chart controls
            var chartCtrl = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32 };
            _chartCoinCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
            _tfCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
            foreach (var tf in _settings.StrList("timeframes")) _tfCombo.Items.Add(tf);
            string dtf = _settings.Str("default_timeframe", "1hour");
            _tfCombo.SelectedItem = _tfCombo.Items.Contains(dtf) ? dtf : (_tfCombo.Items.Count > 0 ? _tfCombo.Items[0] : null);
            _chartCoinCombo.SelectedIndexChanged += (s, e) => RefreshCandleChart();
            _tfCombo.SelectedIndexChanged += (s, e) => RefreshCandleChart();
            chartCtrl.Controls.Add(new Label { Text = "Coin:", AutoSize = true, Padding = new Padding(4, 8, 0, 0) });
            chartCtrl.Controls.Add(_chartCoinCombo);
            chartCtrl.Controls.Add(new Label { Text = "TF:", AutoSize = true, Padding = new Padding(8, 8, 0, 0) });
            chartCtrl.Controls.Add(_tfCombo);

            _candleChart = MakeChart();
            candleTab.Controls.Add(_candleChart);
            candleTab.Controls.Add(chartCtrl);

            _acctChart = MakeChart();
            acctTab.Controls.Add(_acctChart);

            chartTabs.TabPages.Add(candleTab);
            chartTabs.TabPages.Add(acctTab);

            // bottom-left: tiles + trades grid
            var bottomLeft = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 120 };
            _tilePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg };
            _trainingStatusLbl = new Label { Dock = DockStyle.Bottom, Height = 22, Text = "Training: -", Padding = new Padding(6, 2, 0, 0), ForeColor = Theme.Muted };
            var tilesHost = new Panel { Dock = DockStyle.Fill };
            tilesHost.Controls.Add(_tilePanel);
            tilesHost.Controls.Add(_trainingStatusLbl);

            _tradesGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = Theme.Panel, BorderStyle = BorderStyle.None,
                EnableHeadersVisualStyles = false, AllowUserToResizeRows = false,
            };
            StyleGrid(_tradesGrid);
            SetupTradesColumns();

            bottomLeft.Panel1.Controls.Add(tilesHost);
            bottomLeft.Panel2.Controls.Add(_tradesGrid);

            leftSplit.Panel1.Controls.Add(chartTabs);
            leftSplit.Panel2.Controls.Add(bottomLeft);

            // right side: logs tabs
            var logTabs = new TabControl { Dock = DockStyle.Fill };
            _runnerLog = MakeLog();
            _traderLog = MakeLog();
            _trainerLog = MakeLog();
            AddLogTab(logTabs, "Neural Runner", _runnerLog);
            AddLogTab(logTabs, "Trader", _traderLog);
            AddLogTab(logTabs, "Trainer", _trainerLog);

            split.Panel1.Controls.Add(leftSplit);
            split.Panel2.Controls.Add(logTabs);

            Controls.Add(split);
            Controls.Add(_lblAccount);
            Controls.Add(toolbar);

            PopulateCoinCombos();
        }

        private Button MakeBtn(string text, EventHandler onClick)
        {
            var b = new Button { Text = text, AutoSize = true, Height = 30, Margin = new Padding(3) };
            b.Click += onClick;
            return b;
        }

        private static TextBox MakeLog()
        {
            return new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 8.5f), BackColor = Theme.Bg2, ForeColor = Theme.Fg, BorderStyle = BorderStyle.None };
        }

        private static void AddLogTab(TabControl tabs, string title, TextBox box)
        {
            var tab = new TabPage(title);
            tab.Controls.Add(box);
            tabs.TabPages.Add(tab);
        }

        private Chart MakeChart()
        {
            var chart = new Chart { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var area = new ChartArea("main")
            {
                BackColor = Theme.Panel,
            };
            area.AxisX.LabelStyle.ForeColor = Theme.Fg;
            area.AxisY.LabelStyle.ForeColor = Theme.Fg;
            area.AxisX.LineColor = Theme.Border;
            area.AxisY.LineColor = Theme.Border;
            area.AxisX.MajorGrid.LineColor = Theme.Border;
            area.AxisY.MajorGrid.LineColor = Theme.Border;
            area.AxisX.MajorGrid.LineDashStyle = ChartDashStyle.Dot;
            area.AxisY.MajorGrid.LineDashStyle = ChartDashStyle.Dot;
            chart.ChartAreas.Add(area);
            return chart;
        }

        private void StyleGrid(DataGridView g)
        {
            g.DefaultCellStyle.BackColor = Theme.Panel;
            g.DefaultCellStyle.ForeColor = Theme.Fg;
            g.DefaultCellStyle.SelectionBackColor = Theme.Panel2;
            g.DefaultCellStyle.SelectionForeColor = Theme.Accent2;
            g.ColumnHeadersDefaultCellStyle.BackColor = Theme.Bg2;
            g.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Fg;
            g.GridColor = Theme.Border;
        }

        private void SetupTradesColumns()
        {
            _tradesGrid.Columns.Clear();
            foreach (var col in new[] { "Coin", "Qty", "AvgCost", "Buy", "Sell", "PnL% (buy)", "PnL% (sell)", "Value", "DCA Stages", "Next DCA", "Trail Line", "LTH Qty" })
                _tradesGrid.Columns.Add(col, col);
        }

        private void PopulateCoinCombos()
        {
            _coinCombo.Items.Clear();
            _chartCoinCombo.Items.Clear();
            foreach (var c in _coins) { _coinCombo.Items.Add(c); _chartCoinCombo.Items.Add(c); }
            if (_coinCombo.Items.Count > 0) _coinCombo.SelectedIndex = 0;
            if (_chartCoinCombo.Items.Count > 0) _chartCoinCombo.SelectedIndex = 0;

            _tilePanel.Controls.Clear();
            _tiles.Clear();
            int tradeStart = _settings.Int("trade_start_level", 4);
            foreach (var c in _coins)
            {
                var tile = new NeuralTile(c, tradeStart);
                _tiles[c] = tile;
                _tilePanel.Controls.Add(tile);
            }
        }
    }
}
