using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using PowerTrader.Core.Config;
using PowerTrader.Core.Market;
using PowerTrader.Core.Util;

namespace PowerTrader.Trader
{
    public sealed partial class CryptoApiTrading
    {
        private readonly KuCoinClient _kucoin = new KuCoinClient();
        private readonly Dictionary<string, double> _lastPaperLogTs = new Dictionary<string, double>();
        private double _lastReconcileTs;

        // ------------------------------------------------------------------
        // Paper mode (#15): log intended orders instead of sending them.
        // The place_* methods short-circuit here and return null so no state changes.
        // ------------------------------------------------------------------
        public bool PaperMode => _paperMode;

        private void PaperLogBuy(string symbol, double amountInUsd, string tag)
        {
            double price = 0.0;
            try
            {
                var (buy, _, _) = GetPrice(new[] { symbol });
                buy.TryGetValue(symbol, out price);
            }
            catch { }
            double qty = price > 0 ? amountInUsd / price : 0.0;
            PaperLog(new JObject
            {
                ["ts"] = TimeUtil.UnixNow(),
                ["mode"] = "PAPER",
                ["side"] = "buy",
                ["tag"] = tag ?? JValue.CreateNull().ToString(),
                ["symbol"] = symbol,
                ["usd"] = Money.CentsHalfUp(amountInUsd),
                ["est_price"] = price,
                ["est_qty"] = qty,
            }, symbol + "|buy|" + (tag ?? ""));
        }

        private void PaperLogSell(string symbol, double assetQuantity, double? expectedPrice, string tag)
        {
            PaperLog(new JObject
            {
                ["ts"] = TimeUtil.UnixNow(),
                ["mode"] = "PAPER",
                ["side"] = "sell",
                ["tag"] = tag ?? JValue.CreateNull().ToString(),
                ["symbol"] = symbol,
                ["qty"] = assetQuantity,
                ["est_price"] = expectedPrice ?? 0.0,
                ["est_usd"] = expectedPrice.HasValue ? Money.CentsHalfUp(assetQuantity * expectedPrice.Value) : 0.0,
            }, symbol + "|sell|" + (tag ?? ""));
        }

        /// <summary>Append to paper_orders.jsonl + console, de-duping identical intents to at most once per 60s.</summary>
        private void PaperLog(JObject intent, string dedupeKey)
        {
            double now = TimeUtil.UnixNow();
            if (_lastPaperLogTs.TryGetValue(dedupeKey, out var last) && (now - last) < 60.0) return;
            _lastPaperLogTs[dedupeKey] = now;

            Console.WriteLine("[PAPER] would " + intent["side"] + " " + intent["symbol"] +
                              " (" + (intent["tag"]?.ToString() ?? "") + ")  " + intent.ToString(Newtonsoft.Json.Formatting.None));
            JsonStore.AppendJsonl(Path.Combine(AppPaths.HubDataDir, "paper_orders.jsonl"), intent);
        }

        // ------------------------------------------------------------------
        // A4: signal freshness gate. Returns true when signals may be trusted.
        // ------------------------------------------------------------------
        private bool SignalsFresh(string symbol)
        {
            if (_signalMaxAgeSeconds <= 0.0) return true; // disabled
            try
            {
                string path = Path.Combine(FolderFor(symbol), "long_dca_signal.txt");
                if (!File.Exists(path)) return false;
                double age = (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalSeconds;
                return age <= _signalMaxAgeSeconds;
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------
        // A3: venue-divergence guard. KuCoin is the signal venue, Robinhood the exec venue.
        // Returns true when it is safe to commit capital (or the guard is disabled / unverifiable).
        // ------------------------------------------------------------------
        private bool VenueDivergenceOk(string baseSymbol, double robinhoodPrice, out double divergencePct)
        {
            divergencePct = 0.0;
            if (_venueDivergencePct <= 0.0) return true; // disabled
            if (robinhoodPrice <= 0.0) return true;      // can't compare; don't block
            try
            {
                double kucoin = _kucoin.GetTickerPrice(AppPaths.KuCoinPair(baseSymbol));
                if (kucoin <= 0.0) return true;          // unverifiable => permissive (KuCoin was reachable for the signal)
                divergencePct = Math.Abs(kucoin - robinhoodPrice) / robinhoodPrice * 100.0;
                return divergencePct <= _venueDivergencePct;
            }
            catch { return true; }                        // transient KuCoin error => permissive
        }

        // ------------------------------------------------------------------
        // A2: per-coin capital cap. Returns how many USD may still be spent on this coin
        // (0 => none). When the cap is disabled, returns the requested amount unchanged.
        // ------------------------------------------------------------------
        private double CapRoomForCoin(string baseSymbol, double requestedUsd)
        {
            if (_maxCapitalPerCoinUsd <= 0.0) return requestedUsd; // disabled
            string sym = (baseSymbol ?? "").Trim().ToUpperInvariant();
            double already = (_pnlLedger.OpenPositions.TryGetValue(sym, out var pos) && pos != null) ? pos.UsdCost : 0.0;
            double room = _maxCapitalPerCoinUsd - already;
            if (room <= 0.0) return 0.0;
            return Math.Min(requestedUsd, room);
        }

        // ------------------------------------------------------------------
        // A1: catastrophic stop. Force-exit when sell-side PnL <= -pct.
        // ------------------------------------------------------------------
        private bool CatastrophicStopHit(double gainLossSellPct)
        {
            if (_catastrophicStopPct <= 0.0) return false; // disabled
            return gainLossSellPct <= -_catastrophicStopPct;
        }

        // ------------------------------------------------------------------
        // A5: ledger reconciliation. Surfaces deltas between the local ledger and the
        // exchange's reported holdings so drift is visible. Never silently corrupts data;
        // writes hub_data/reconciliation.json and logs warnings.
        // ------------------------------------------------------------------
        private void WriteReconciliation(JArray holdingsList, Dictionary<string, double> sellPrices)
        {
            double now = TimeUtil.UnixNow();
            if ((now - _lastReconcileTs) < 30.0) return; // throttle
            _lastReconcileTs = now;

            try
            {
                var exchangeQty = new Dictionary<string, double>();
                if (holdingsList != null)
                    foreach (var h in holdingsList.OfType<JObject>())
                    {
                        string a = (Js.Str(h, "asset_code", "") ?? "").Trim().ToUpperInvariant();
                        if (a.Length == 0 || a == "USDC") continue;
                        exchangeQty[a] = Js.Double(h, "total_quantity", 0.0);
                    }

                var coins = new JArray();
                foreach (var kv in _pnlLedger.OpenPositions)
                {
                    string sym = kv.Key;
                    double ledgerQty = kv.Value?.Qty ?? 0.0;
                    double exQty = exchangeQty.TryGetValue(sym, out var e) ? e : 0.0;
                    double delta = ledgerQty - exQty;
                    bool over = delta > 1e-8; // ledger claims more than the exchange actually holds
                    if (over)
                        Console.WriteLine($"  [RECONCILE] {sym}: ledger qty {ledgerQty:0.########} exceeds exchange qty {exQty:0.########} (delta {delta:0.########}).");
                    coins.Add(new JObject
                    {
                        ["symbol"] = sym,
                        ["ledger_qty"] = ledgerQty,
                        ["exchange_qty"] = exQty,
                        ["delta_qty"] = delta,
                        ["ledger_exceeds_exchange"] = over,
                    });
                }

                var report = new JObject
                {
                    ["ts"] = now,
                    ["total_realized_profit_usd"] = _pnlLedger.TotalRealizedProfitUsd,
                    ["lth_profit_bucket_usd"] = _pnlLedger.LthProfitBucketUsd,
                    ["coins"] = coins,
                };
                JsonStore.AtomicWriteJson(Path.Combine(AppPaths.HubDataDir, "reconciliation.json"), report);
            }
            catch { }
        }
    }
}
