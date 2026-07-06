using System;
using System.IO;

namespace PowerTrader.Core.Config
{
    /// <summary>
    /// Centralized path resolution mirroring the Python scripts' conventions:
    ///  - BTC uses the main folder directly; other coins use &lt;main&gt;/&lt;SYM&gt;.
    ///  - gui_settings.json lives next to the app (overridable via POWERTRADER_GUI_SETTINGS).
    ///  - hub_data/ holds cross-process status files (overridable via POWERTRADER_HUB_DIR).
    /// </summary>
    public static class AppPaths
    {
        /// <summary>Directory the running executable lives in (Python: dir of the .py file).</summary>
        public static string BaseDir { get; set; } = ResolveBaseDir();

        private static string ResolveBaseDir()
        {
            try
            {
                string b = AppDomain.CurrentDomain.BaseDirectory;
                if (!string.IsNullOrEmpty(b)) return b.TrimEnd(Path.DirectorySeparatorChar);
            }
            catch { }
            return Directory.GetCurrentDirectory();
        }

        public static string GuiSettingsPath
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("POWERTRADER_GUI_SETTINGS");
                if (!string.IsNullOrWhiteSpace(env)) return env;
                return Path.Combine(BaseDir, "gui_settings.json");
            }
        }

        public static string HubDataDir
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("POWERTRADER_HUB_DIR");
                string dir = !string.IsNullOrWhiteSpace(env) ? env : Path.Combine(BaseDir, "hub_data");
                try { Directory.CreateDirectory(dir); } catch { }
                return dir;
            }
        }

        // --- hub_data cross-process files ---
        public static string TraderStatusPath => Path.Combine(HubDataDir, "trader_status.json");
        public static string TradeHistoryPath => Path.Combine(HubDataDir, "trade_history.jsonl");
        public static string PnlLedgerPath => Path.Combine(HubDataDir, "pnl_ledger.json");
        public static string AccountValueHistoryPath => Path.Combine(HubDataDir, "account_value_history.jsonl");
        public static string BotOrderIdsPath => Path.Combine(HubDataDir, "bot_order_ids.json");
        public static string LthEma200Path => Path.Combine(HubDataDir, "lth_daily_ema200.json");
        public static string RunnerReadyPath => Path.Combine(HubDataDir, "runner_ready.json");

        /// <summary>BTC -> mainDir, everything else -> mainDir/SYM (Python coin_folder()).</summary>
        public static string CoinFolder(string mainDir, string sym)
        {
            sym = (sym ?? string.Empty).Trim().ToUpperInvariant();
            if (sym == "BTC") return mainDir;
            return Path.Combine(mainDir, sym);
        }

        public static string RobinhoodPair(string baseSymbol)
        {
            return (baseSymbol ?? string.Empty).Trim().ToUpperInvariant() + "-USD";
        }

        public static string KuCoinPair(string baseSymbol)
        {
            return (baseSymbol ?? string.Empty).Trim().ToUpperInvariant() + "-USDT";
        }
    }
}
