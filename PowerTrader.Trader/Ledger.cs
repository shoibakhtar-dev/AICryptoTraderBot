using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PowerTrader.Core.Util;

namespace PowerTrader.Trader
{
    /// <summary>Money helpers reproducing pt_trader.py's Decimal rounding to cents.</summary>
    public static class Money
    {
        /// <summary>Robinhood settlement rounding: BUY rounds up, SELL rounds down, else half-up.</summary>
        public static double RoundUsdToCents(double amount, string sideLower)
        {
            try
            {
                decimal d = (decimal)amount;
                if (sideLower == "buy") return (double)(Math.Ceiling(d * 100m) / 100m);
                if (sideLower == "sell") return (double)(Math.Floor(d * 100m) / 100m);
                return (double)Math.Round(d, 2, MidpointRounding.AwayFromZero);
            }
            catch { return amount; }
        }

        public static double CentsHalfUp(double amount)
        {
            try { return (double)Math.Round((decimal)amount, 2, MidpointRounding.AwayFromZero); }
            catch { return amount; }
        }
    }

    public sealed class OpenPosition
    {
        [JsonProperty("usd_cost")] public double UsdCost { get; set; }
        [JsonProperty("qty")] public double Qty { get; set; }
    }

    /// <summary>
    /// pnl_ledger.json model. Extra/unknown keys in pending_orders are preserved via JObject.
    /// </summary>
    public sealed class PnlLedger
    {
        [JsonProperty("total_realized_profit_usd")] public double TotalRealizedProfitUsd { get; set; }
        [JsonProperty("last_updated_ts")] public double LastUpdatedTs { get; set; }
        [JsonProperty("open_positions")] public Dictionary<string, OpenPosition> OpenPositions { get; set; } = new Dictionary<string, OpenPosition>();
        [JsonProperty("pending_orders")] public Dictionary<string, JObject> PendingOrders { get; set; } = new Dictionary<string, JObject>();
        [JsonProperty("lth_profit_bucket_usd")] public double LthProfitBucketUsd { get; set; }
        [JsonProperty("lth_last_buy")] public JObject LthLastBuy { get; set; }

        public static PnlLedger Upgrade(JObject o)
        {
            var d = new PnlLedger();
            if (o != null)
            {
                try { d = o.ToObject<PnlLedger>() ?? new PnlLedger(); }
                catch { d = new PnlLedger(); }
            }
            if (d.OpenPositions == null) d.OpenPositions = new Dictionary<string, OpenPosition>();
            if (d.PendingOrders == null) d.PendingOrders = new Dictionary<string, JObject>();
            return d;
        }

        public OpenPosition PosFor(string baseSym, bool create)
        {
            if (OpenPositions.TryGetValue(baseSym, out var p) && p != null) return p;
            if (!create) return null;
            var np = new OpenPosition();
            OpenPositions[baseSym] = np;
            return np;
        }
    }
}
