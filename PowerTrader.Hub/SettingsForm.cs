using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;

namespace PowerTrader.Hub
{
    /// <summary>Settings editor for gui_settings.json + entry to the Robinhood API wizard.</summary>
    internal sealed class SettingsForm : Form
    {
        private readonly HubSettings _s;
        private readonly string _projectDir;
        private readonly Dictionary<string, TextBox> _fields = new Dictionary<string, TextBox>();
        private CheckBox _autoStart;
        private CheckBox _paperMode;

        public SettingsForm(HubSettings s, string projectDir)
        {
            _s = s; _projectDir = projectDir;
            Text = "PowerTrader - Settings";
            Width = 640; Height = 640;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;

            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Padding = new Padding(10) };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            AddField(panel, "Main neural folder", "main_neural_dir", _s.Str("main_neural_dir", _projectDir));
            AddField(panel, "Coins (comma list)", "coins", string.Join(",", _s.Coins));
            AddField(panel, "Long-term holdings", "long_term_holdings", string.Join(",", _s.StrList("long_term_holdings")));
            AddField(panel, "LTH profit alloc %", "lth_profit_alloc_pct", _s.Dbl("lth_profit_alloc_pct", 50).ToString(CultureInfo.InvariantCulture));
            AddField(panel, "Trade start level (1-7)", "trade_start_level", _s.Int("trade_start_level", 4).ToString());
            AddField(panel, "Start allocation %", "start_allocation_pct", _s.Dbl("start_allocation_pct", 0.5).ToString(CultureInfo.InvariantCulture));
            AddField(panel, "DCA multiplier", "dca_multiplier", _s.Dbl("dca_multiplier", 2).ToString(CultureInfo.InvariantCulture));
            AddField(panel, "DCA levels (comma %)", "dca_levels", string.Join(",", DcaLevels()));
            AddField(panel, "Max DCA buys / 24h", "max_dca_buys_per_24h", _s.Int("max_dca_buys_per_24h", 1).ToString());
            AddField(panel, "PM start % (no DCA)", "pm_start_pct_no_dca", _s.Dbl("pm_start_pct_no_dca", 3).ToString(CultureInfo.InvariantCulture));
            AddField(panel, "PM start % (with DCA)", "pm_start_pct_with_dca", _s.Dbl("pm_start_pct_with_dca", 3).ToString(CultureInfo.InvariantCulture));
            AddField(panel, "Trailing gap %", "trailing_gap_pct", _s.Dbl("trailing_gap_pct", 0.1).ToString(CultureInfo.InvariantCulture));
            AddField(panel, "Default timeframe", "default_timeframe", _s.Str("default_timeframe", "1hour"));
            AddField(panel, "Candles limit", "candles_limit", _s.Int("candles_limit", 250).ToString());

            // --- opt-in safety features (0 / off = disabled) ---
            AddField(panel, "Catastrophic stop % (0=off)", "catastrophic_stop_pct", _s.Dbl("catastrophic_stop_pct", 0).ToString(CultureInfo.InvariantCulture));
            AddField(panel, "Max capital $/coin (0=off)", "max_capital_per_coin_usd", _s.Dbl("max_capital_per_coin_usd", 0).ToString(CultureInfo.InvariantCulture));
            AddField(panel, "Venue divergence % (0=off)", "venue_divergence_pct", _s.Dbl("venue_divergence_pct", 0).ToString(CultureInfo.InvariantCulture));
            AddField(panel, "Signal max age sec (0=off)", "signal_max_age_seconds", _s.Dbl("signal_max_age_seconds", 0).ToString(CultureInfo.InvariantCulture));

            _paperMode = new CheckBox { Text = "PAPER MODE (log intended orders, send nothing)", Checked = _s.Bool("paper_mode", false), AutoSize = true, ForeColor = Theme.Accent };
            panel.Controls.Add(new Label { Text = "Paper mode" });
            panel.Controls.Add(_paperMode);

            _autoStart = new CheckBox { Text = "Auto-start scripts on launch", Checked = _s.Bool("auto_start_scripts", false), AutoSize = true };
            panel.Controls.Add(new Label { Text = "" });
            panel.Controls.Add(_autoStart);

            var apiBtn = new Button { Text = "Robinhood API Setup / Update", AutoSize = true };
            apiBtn.Click += (a, b) => { using (var w = new ApiWizardForm(_projectDir)) w.ShowDialog(this); };
            panel.Controls.Add(new Label { Text = "Credentials" });
            panel.Controls.Add(apiBtn);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(8) };
            var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            ok.Click += (a, b) => Commit();
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);

            Controls.Add(panel);
            Controls.Add(buttons);
            AcceptButton = ok; CancelButton = cancel;
            Theme.Apply(this);
        }

        private List<string> DcaLevels()
        {
            if (_s.Raw["dca_levels"] is JArray arr)
                return arr.Select(x => x.ToString()).ToList();
            return new List<string> { "-5", "-10", "-20", "-30", "-40", "-50", "-50" };
        }

        private void AddField(TableLayoutPanel panel, string label, string key, string value)
        {
            panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) });
            var tb = new TextBox { Text = value, Dock = DockStyle.Fill };
            _fields[key] = tb;
            panel.Controls.Add(tb);
        }

        private void Commit()
        {
            _s.Set("main_neural_dir", _fields["main_neural_dir"].Text.Trim());
            _s.Set("coins", SymArray(_fields["coins"].Text));
            _s.Set("long_term_holdings", SymArray(_fields["long_term_holdings"].Text));
            _s.Set("lth_profit_alloc_pct", ParseD(_fields["lth_profit_alloc_pct"].Text, 50));
            _s.Set("trade_start_level", ParseI(_fields["trade_start_level"].Text, 4));
            _s.Set("start_allocation_pct", ParseD(_fields["start_allocation_pct"].Text, 0.5));
            _s.Set("dca_multiplier", ParseD(_fields["dca_multiplier"].Text, 2));
            _s.Set("dca_levels", DblArray(_fields["dca_levels"].Text));
            _s.Set("max_dca_buys_per_24h", ParseI(_fields["max_dca_buys_per_24h"].Text, 1));
            _s.Set("pm_start_pct_no_dca", ParseD(_fields["pm_start_pct_no_dca"].Text, 3));
            _s.Set("pm_start_pct_with_dca", ParseD(_fields["pm_start_pct_with_dca"].Text, 3));
            _s.Set("trailing_gap_pct", ParseD(_fields["trailing_gap_pct"].Text, 0.1));
            _s.Set("default_timeframe", _fields["default_timeframe"].Text.Trim());
            _s.Set("candles_limit", ParseI(_fields["candles_limit"].Text, 250));
            _s.Set("auto_start_scripts", _autoStart.Checked);

            _s.Set("paper_mode", _paperMode.Checked);
            _s.Set("catastrophic_stop_pct", ParseD(_fields["catastrophic_stop_pct"].Text, 0));
            _s.Set("max_capital_per_coin_usd", ParseD(_fields["max_capital_per_coin_usd"].Text, 0));
            _s.Set("venue_divergence_pct", ParseD(_fields["venue_divergence_pct"].Text, 0));
            _s.Set("signal_max_age_seconds", ParseD(_fields["signal_max_age_seconds"].Text, 0));
        }

        private static JArray SymArray(string csv)
        {
            var arr = new JArray();
            foreach (var p in (csv ?? "").Replace("\n", ",").Split(','))
            { string s = p.Trim().ToUpperInvariant(); if (s.Length > 0) arr.Add(s); }
            return arr;
        }

        private static JArray DblArray(string csv)
        {
            var arr = new JArray();
            foreach (var p in (csv ?? "").Split(','))
                if (double.TryParse(p.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) arr.Add(d);
            return arr;
        }

        private static double ParseD(string s, double dflt) =>
            double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : dflt;

        private static int ParseI(string s, int dflt) =>
            int.TryParse((s ?? "").Trim(), out int i) ? i : dflt;
    }
}
