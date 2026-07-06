using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PowerTrader.Core.Config;
using PowerTrader.Core.Util;

namespace PowerTrader.Trader
{
    public sealed partial class CryptoApiTrading
    {
        // ==================================================================
        // Cost basis (bot-owned FILLED BUY orders only; sells ignored)
        // ==================================================================
        private Dictionary<string, double> CalculateCostBasis()
        {
            var costBasis = new Dictionary<string, double>();
            var holdings = GetHoldings();
            var results = holdings?["results"] as JArray;
            if (results == null) return costBasis;

            foreach (var holding in results.OfType<JObject>())
            {
                string asset = (Js.Str(holding, "asset_code", "") ?? "").Trim().ToUpperInvariant();
                if (asset.Length == 0) continue;

                double tradableTargetQty = 0.0;
                if (_pnlLedger.OpenPositions.TryGetValue(asset, out var pos) && pos != null) tradableTargetQty = pos.Qty;
                if (tradableTargetQty <= 1e-12) { costBasis[asset] = 0.0; continue; }

                var orders = GetOrders(asset + "-USD");
                var ores = orders?["results"] as JArray;
                if (ores == null) continue;

                var filledBotBuys = new List<JObject>();
                foreach (var o in ores.OfType<JObject>())
                {
                    if (Js.Str(o, "state") != "filled") continue;
                    if ((Js.Str(o, "side", "") ?? "").ToLowerInvariant().Trim() != "buy") continue;
                    if (!IsBotOrderId(asset, Js.Str(o, "id"))) continue;
                    filledBotBuys.Add(o);
                }
                if (filledBotBuys.Count == 0) continue;

                filledBotBuys.Sort((a, b) => string.CompareOrdinal(Js.Str(a, "created_at", "") ?? "", Js.Str(b, "created_at", "") ?? ""));

                var lots = new List<(double q, double p)>();
                foreach (var o in filledBotBuys)
                {
                    var (q, p) = ExtractFillFromOrder(o);
                    if (q > 0.0 && p != null && p.Value > 0.0) lots.Add((q, p.Value));
                }

                double botQty = lots.Sum(l => l.q);
                if (botQty <= 1e-12) { costBasis[asset] = 0.0; continue; }

                double targetQty = Math.Min(botQty, tradableTargetQty);
                double remaining = targetQty, totalCost = 0.0;
                foreach (var (q, p) in lots)
                {
                    if (remaining <= 0.0) break;
                    double useQ = Math.Min(q, remaining);
                    totalCost += useQ * p;
                    remaining -= useQ;
                }
                costBasis[asset] = targetQty > 1e-12 ? totalCost / targetQty : 0.0;
            }
            return costBasis;
        }

        // ==================================================================
        // DCA stage reconstruction + 24h window
        // ==================================================================
        private void InitializeDcaLevels()
        {
            var holdings = GetHoldings();
            var results = holdings?["results"] as JArray;
            if (results == null) { Console.WriteLine("No holdings found. Skipping DCA levels initialization."); return; }

            foreach (var holding in results.OfType<JObject>())
            {
                string symbol = (Js.Str(holding, "asset_code", "") ?? "").Trim().ToUpperInvariant();
                if (symbol.Length == 0) continue;

                double botQty = 0.0;
                if (_pnlLedger.OpenPositions.TryGetValue(symbol, out var pos) && pos != null) botQty = pos.Qty;
                if (botQty <= 1e-12) { _dcaLevelsTriggered[symbol] = new List<int>(); continue; }

                var selectedIds = _botOrderIds.TryGetValue(symbol, out var sset) ? new HashSet<string>(sset) : new HashSet<string>();
                if (selectedIds.Count == 0) { _dcaLevelsTriggered[symbol] = new List<int>(); continue; }

                var orders = GetOrders(symbol + "-USD");
                var ores = orders?["results"] as JArray;
                if (ores == null) { _dcaLevelsTriggered[symbol] = new List<int>(); continue; }

                var relevant = new List<JObject>();
                foreach (var o in ores.OfType<JObject>())
                {
                    if (Js.Str(o, "state") != "filled") continue;
                    if ((Js.Str(o, "side", "") ?? "").ToLowerInvariant().Trim() != "buy") continue;
                    string oid = (Js.Str(o, "id", "") ?? "").Trim();
                    if (oid.Length == 0 || !selectedIds.Contains(oid)) continue;
                    relevant.Add(o);
                }
                if (relevant.Count == 0) { _dcaLevelsTriggered[symbol] = new List<int>(); continue; }

                int triggered = Math.Max(0, relevant.Count - 1);
                _dcaLevelsTriggered[symbol] = Enumerable.Range(0, triggered).ToList();
                Console.WriteLine($"Initialized DCA stages for {symbol}: {triggered}");
            }
        }

        private void SeedDcaWindowFromHistory()
        {
            double now = TimeUtil.UnixNow();
            double cutoff = now - _dcaWindowSeconds;
            _dcaBuyTs.Clear();
            _dcaLastSellTs.Clear();
            if (!File.Exists(AppPaths.TradeHistoryPath)) return;

            try
            {
                foreach (var line in File.ReadLines(AppPaths.TradeHistoryPath))
                {
                    string l = (line ?? "").Trim();
                    if (l.Length == 0) continue;
                    JObject obj;
                    try { obj = JObject.Parse(l); } catch { continue; }

                    string side = (Js.Str(obj, "side", "") ?? "").ToLowerInvariant();
                    string tag = Js.Str(obj, "tag", null);
                    string symFull = (Js.Str(obj, "symbol", "") ?? "").Trim().ToUpperInvariant();
                    string bas = symFull.Length > 0 ? symFull.Split('-')[0].Trim() : "";
                    if (bas.Length == 0) continue;
                    if (obj["ts"] == null || !PyCompat.TryDouble(obj["ts"].ToString(), out double tsF)) continue;

                    if (side == "sell")
                    {
                        double prev = _dcaLastSellTs.TryGetValue(bas, out var pv) ? pv : 0.0;
                        if (tsF > prev) _dcaLastSellTs[bas] = tsF;
                    }
                    else if (side == "buy" && tag == "DCA")
                    {
                        if (!_dcaBuyTs.TryGetValue(bas, out var lst)) { lst = new List<double>(); _dcaBuyTs[bas] = lst; }
                        lst.Add(tsF);
                    }
                }
            }
            catch { return; }

            foreach (var bas in _dcaBuyTs.Keys.ToList())
            {
                double lastSell = _dcaLastSellTs.TryGetValue(bas, out var ls) ? ls : 0.0;
                var kept = _dcaBuyTs[bas].Where(t => t > lastSell && t >= cutoff).OrderBy(t => t).ToList();
                _dcaBuyTs[bas] = kept;
            }
        }

        private int DcaWindowCount(string baseSymbol, double? nowTs = null)
        {
            string bas = (baseSymbol ?? "").Trim().ToUpperInvariant();
            if (bas.Length == 0) return 0;
            double now = nowTs ?? TimeUtil.UnixNow();
            double cutoff = now - _dcaWindowSeconds;
            double lastSell = _dcaLastSellTs.TryGetValue(bas, out var ls) ? ls : 0.0;
            var list = (_dcaBuyTs.TryGetValue(bas, out var l) ? l : new List<double>())
                .Where(t => t > lastSell && t >= cutoff).ToList();
            _dcaBuyTs[bas] = list;
            return list.Count;
        }

        private void NoteDcaBuy(string baseSymbol, double? ts = null)
        {
            string bas = (baseSymbol ?? "").Trim().ToUpperInvariant();
            if (bas.Length == 0) return;
            double t = ts ?? TimeUtil.UnixNow();
            if (!_dcaBuyTs.TryGetValue(bas, out var lst)) { lst = new List<double>(); _dcaBuyTs[bas] = lst; }
            lst.Add(t);
            DcaWindowCount(bas, t);
        }

        private void ResetDcaWindowForTrade(string baseSymbol, bool sold = false, double? ts = null)
        {
            string bas = (baseSymbol ?? "").Trim().ToUpperInvariant();
            if (bas.Length == 0) return;
            if (sold) _dcaLastSellTs[bas] = ts ?? TimeUtil.UnixNow();
            _dcaBuyTs[bas] = new List<double>();
        }

        // ==================================================================
        // Long-term holdings auto-buy from realized profit
        // ==================================================================
        private Dictionary<string, double> ReadLthEma200Snapshot()
        {
            var outMap = new Dictionary<string, double>();
            try
            {
                var payload = JsonStore.ReadJObject(AppPaths.LthEma200Path);
                var coins = payload?["coins"] as JObject;
                if (coins == null) return outMap;
                foreach (var prop in coins.Properties())
                {
                    string s = prop.Name.Trim().ToUpperInvariant();
                    if (s.Length == 0 || !(prop.Value is JObject row)) continue;
                    if (row["pct_from_ema200"] == null || row["pct_from_ema200"].Type == JTokenType.Null) continue;
                    if (PyCompat.TryDouble(row["pct_from_ema200"].ToString(), out double pct)) outMap[s] = pct;
                }
            }
            catch { }
            return outMap;
        }

        private string PickLthSymbolToBuy()
        {
            try
            {
                var syms = LongTermSymbols.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim().ToUpperInvariant())
                    .Distinct().OrderBy(x => x).ToList();
                if (syms.Count == 0) return null;

                var pctMap = ReadLthEma200Snapshot();
                var scored = syms.Where(pctMap.ContainsKey).Select(s => (pct: pctMap[s], s)).ToList();
                if (scored.Count == 0) return syms[0];

                var below = scored.Where(t => t.pct < 0.0).OrderBy(t => t.pct).ToList();
                if (below.Count > 0) return below[0].s;
                return scored.OrderBy(t => Math.Abs(t.pct)).First().s;
            }
            catch { return null; }
        }

        private bool LthMarketBuyForUsd(string baseSymbol, double usdAmount)
        {
            try
            {
                string sym = (baseSymbol ?? "").Trim().ToUpperInvariant();
                if (sym.Length == 0) return false;
                if (usdAmount < 0.50) return false;
                string full = sym + "-USD";
                var order = PlaceBuyOrder(Guid.NewGuid().ToString(), "buy", "market", full, usdAmount, tag: "LTH");
                return order is JObject o && !string.IsNullOrEmpty((Js.Str(o, "id", "") ?? "").Trim());
            }
            catch { return false; }
        }

        private void MaybeProcessLthProfitAllocation(double realizedProfitUsd)
        {
            double pct = LthProfitAllocPct;
            if (pct <= 0.0) return;
            if (LongTermSymbols.Count == 0) return;

            double rp = realizedProfitUsd;
            double alloc = rp * (pct / 100.0);
            double bucket = _pnlLedger.LthProfitBucketUsd;
            double prevBucket = bucket;
            bucket += alloc;
            if (bucket < 0.0) bucket = 0.0;

            double spendNow = 0.0;
            if (alloc >= 0.50) { spendNow = alloc + prevBucket; bucket = 0.0; }
            else if (bucket >= 0.50) { spendNow = bucket; bucket = 0.0; }

            _pnlLedger.LthProfitBucketUsd = bucket;
            SavePnlLedger();

            if (spendNow < 0.50) return;

            string pick = PickLthSymbolToBuy();
            if (pick == null)
            {
                _pnlLedger.LthProfitBucketUsd = bucket + spendNow;
                SavePnlLedger();
                return;
            }

            var pctMap = ReadLthEma200Snapshot();
            double? pctFromEma = pctMap.TryGetValue(pick, out var pv) ? (double?)pv : null;

            bool ok = LthMarketBuyForUsd(pick, spendNow);
            if (ok)
            {
                _pnlLedger.LthProfitBucketUsd = 0.0;
                _pnlLedger.LthLastBuy = new JObject
                {
                    ["ts"] = TimeUtil.UnixNow(),
                    ["symbol"] = pick,
                    ["usd"] = spendNow,
                    ["pct_from_ema200"] = pctFromEma.HasValue ? (JToken)pctFromEma.Value : JValue.CreateNull(),
                };
                SavePnlLedger();
            }
            else
            {
                _pnlLedger.LthProfitBucketUsd = bucket + spendNow;
                SavePnlLedger();
            }
        }

        // ==================================================================
        // Ledger seeding from selected bot order IDs
        // ==================================================================
        private void RebuildOpenPositionFromSelectedBotBuys(string baseSymbol, double tradableQty)
        {
            try
            {
                string sym = (baseSymbol ?? "").Trim().ToUpperInvariant();
                if (sym.Length == 0 || sym == "USDC") return;
                if (tradableQty < 0.0) tradableQty = 0.0;

                bool hasAnyIds = (_botOrderIds.TryGetValue(sym, out var a) && a.Count > 0)
                    || (_botOrderIdsFromHistory.TryGetValue(sym, out var b) && b.Count > 0);
                if (!hasAnyIds) return;

                var orders = GetOrders(sym + "-USD");
                var results = orders?["results"] as JArray;
                if (results == null || results.Count == 0) return;

                var buys = new List<(string created, double qty, double cost)>();
                foreach (var o in results.OfType<JObject>())
                {
                    if (Js.Str(o, "state", "").ToLowerInvariant() != "filled") continue;
                    if (Js.Str(o, "side", "").ToLowerInvariant() != "buy") continue;
                    string oid = (Js.Str(o, "id", "") ?? "").Trim();
                    if (oid.Length == 0 || !IsBotOrderId(sym, oid)) continue;

                    var (qty, avgPx, notionalUsd, feesUsd) = ExtractAmountsAndFees(o);
                    if (qty <= 0.0) continue;
                    if (notionalUsd == null || notionalUsd.Value <= 0.0)
                    {
                        if (avgPx == null || avgPx.Value <= 0.0) continue;
                        notionalUsd = avgPx.Value * qty;
                    }
                    double cost = notionalUsd.Value + (feesUsd ?? 0.0);
                    buys.Add((Js.Str(o, "created_at", "") ?? "", qty, cost));
                }
                if (buys.Count == 0) return;

                buys.Sort((x, y) => string.CompareOrdinal(x.created ?? "", y.created ?? ""));

                if (tradableQty <= 0.0)
                {
                    _pnlLedger.OpenPositions.Remove(sym);
                    SavePnlLedger();
                    return;
                }

                double qtyUsed = 0.0, costUsed = 0.0;
                foreach (var (_, q, c) in buys)
                {
                    if (qtyUsed >= tradableQty - 1e-12) break;
                    double remaining = tradableQty - qtyUsed;
                    if (q <= remaining + 1e-12) { qtyUsed += q; costUsed += c; }
                    else { double ratio = q > 0 ? remaining / q : 0.0; qtyUsed += remaining; costUsed += c * ratio; }
                }

                if (qtyUsed <= 0.0)
                {
                    _pnlLedger.OpenPositions.Remove(sym);
                    SavePnlLedger();
                    return;
                }

                _pnlLedger.OpenPositions[sym] = new OpenPosition { Qty = qtyUsed, UsdCost = costUsed };
                SavePnlLedger();
            }
            catch { }
        }

        private double BotNetQtyFromSelectedOrders(string baseSymbol)
        {
            try
            {
                string sym = (baseSymbol ?? "").Trim().ToUpperInvariant();
                if (sym.Length == 0 || sym == "USDC") return 0.0;

                var orders = GetOrders(sym + "-USD");
                var results = orders?["results"] as JArray;
                if (results == null || results.Count == 0) return 0.0;

                var selectedIds = _botOrderIds.TryGetValue(sym, out var si) ? new HashSet<string>(si) : new HashSet<string>();
                var histIds = _botOrderIdsFromHistory.TryGetValue(sym, out var hi) ? new HashSet<string>(hi) : new HashSet<string>();

                double selectedBuyQty = 0.0;
                string earliestSelectedBuyCreated = null;
                var filledBotSells = new List<(string created, double qty)>();

                foreach (var o in results.OfType<JObject>())
                {
                    if (Js.Str(o, "state", "").ToLowerInvariant() != "filled") continue;
                    string oid = (Js.Str(o, "id", "") ?? "").Trim();
                    if (oid.Length == 0) continue;
                    string side = (Js.Str(o, "side", "") ?? "").ToLowerInvariant().Trim();
                    var (qty, _) = ExtractFillFromOrder(o);
                    if (qty <= 0.0) continue;
                    string created = Js.Str(o, "created_at", "") ?? "";

                    if (side == "buy" && selectedIds.Contains(oid))
                    {
                        selectedBuyQty += qty;
                        if (created.Length > 0 && (earliestSelectedBuyCreated == null || string.CompareOrdinal(created, earliestSelectedBuyCreated) < 0))
                            earliestSelectedBuyCreated = created;
                    }
                    if (side == "sell" && histIds.Contains(oid)) filledBotSells.Add((created, qty));
                }

                if (selectedBuyQty <= 0.0) return 0.0;

                double botSellQty = 0.0;
                string cutoff = earliestSelectedBuyCreated;
                foreach (var (created, qty) in filledBotSells)
                {
                    if (!string.IsNullOrEmpty(cutoff) && !string.IsNullOrEmpty(created) && string.CompareOrdinal(created, cutoff) < 0) continue;
                    botSellQty += qty;
                }

                double net = selectedBuyQty - botSellQty;
                return net < 0.0 ? 0.0 : net;
            }
            catch { return 0.0; }
        }

        private void SeedOpenPositionsFromSelectedOrders(JArray holdingsList)
        {
            if (holdingsList == null) return;
            foreach (var h in holdingsList.OfType<JObject>())
            {
                try
                {
                    string asset = (Js.Str(h, "asset_code", "") ?? "").Trim().ToUpperInvariant();
                    if (asset.Length == 0 || asset == "USDC") continue;
                    double totalQty = Js.Double(h, "total_quantity", 0.0);
                    if (totalQty < 0.0) totalQty = 0.0;
                    double inTradeQty = BotNetQtyFromSelectedOrders(asset);
                    double tradableQty = Math.Min(totalQty, inTradeQty);
                    if (tradableQty < 0.0) tradableQty = 0.0;
                    RebuildOpenPositionFromSelectedBotBuys(asset, tradableQty);
                }
                catch { }
            }
        }

        // ==================================================================
        // Order terminal wait + pending reconciliation
        // ==================================================================
        private static readonly HashSet<string> TerminalStates =
            new HashSet<string> { "filled", "canceled", "cancelled", "rejected", "failed", "error" };

        private JObject WaitForOrderTerminal(string symbol, string orderId)
        {
            while (true)
            {
                var o = GetOrderById(symbol, orderId);
                if (o == null) { Thread.Sleep(1000); continue; }
                string st = (Js.Str(o, "state", "") ?? "").ToLowerInvariant().Trim();
                if (TerminalStates.Contains(st)) return o;
                Thread.Sleep(1000);
            }
        }

        private void ReconcilePendingOrders()
        {
            try
            {
                if (_pnlLedger.PendingOrders == null || _pnlLedger.PendingOrders.Count == 0) return;

                while (true)
                {
                    if (_pnlLedger.PendingOrders.Count == 0) break;
                    bool progressed = false;

                    foreach (var kv in _pnlLedger.PendingOrders.ToList())
                    {
                        string orderId = kv.Key;
                        JObject info = kv.Value;
                        try
                        {
                            if (TradeHistoryHasOrderId(orderId))
                            {
                                _pnlLedger.PendingOrders.Remove(orderId); SavePnlLedger(); progressed = true; continue;
                            }
                            string symbol = (Js.Str(info, "symbol", "") ?? "").Trim();
                            string side = (Js.Str(info, "side", "") ?? "").Trim().ToLowerInvariant();
                            if (symbol.Length == 0 || side.Length == 0 || string.IsNullOrEmpty(orderId))
                            {
                                _pnlLedger.PendingOrders.Remove(orderId); SavePnlLedger(); progressed = true; continue;
                            }

                            var order = WaitForOrderTerminal(symbol, orderId);
                            if (order == null) continue;
                            if ((Js.Str(order, "state", "") ?? "").ToLowerInvariant().Trim() != "filled")
                            {
                                _pnlLedger.PendingOrders.Remove(orderId); SavePnlLedger(); progressed = true; continue;
                            }

                            var (filledQty, avgPrice, notionalUsd, feesUsd) = ExtractAmountsAndFees(order);
                            RecordTrade(side, symbol, filledQty, avgPrice, notionalUsd, feesUsd,
                                DoubleOrNull(info, "avg_cost_basis"), DoubleOrNull(info, "pnl_pct"), Js.Str(info, "tag", null), orderId);

                            _pnlLedger.PendingOrders.Remove(orderId); SavePnlLedger(); progressed = true;
                        }
                        catch { }
                    }

                    if (!progressed) Thread.Sleep(1000);
                }
            }
            catch { }
        }

        private static double? DoubleOrNull(JObject o, string key)
        {
            var v = o?[key];
            if (v == null || v.Type == JTokenType.Null) return null;
            if (PyCompat.TryDouble(v.ToString(), out double d)) return d;
            return null;
        }

        // ==================================================================
        // Record trade -> trade_history.jsonl + pnl_ledger.json
        // ==================================================================
        private void RecordTrade(string side, string symbol, double qty, double? price, double? notionalUsd,
            double? feesUsd, double? avgCostBasis, double? pnlPct, string tag, string orderId)
        {
            double ts = TimeUtil.UnixNow();
            string sideL = (side ?? "").ToLowerInvariant().Trim();
            string bas = (symbol ?? "").ToUpperInvariant().Split('-')[0].Trim();
            string tagU = (tag ?? "").ToUpperInvariant().Trim();

            double? realized = null;
            double? positionCostUsed = null;
            double? positionCostAfter = null;

            bool feesMissing = feesUsd == null;
            double feeValActual = feesUsd ?? 0.0;
            double feeFallback = (feesMissing && sideL == "sell") ? 0.02 : 0.0;

            double? notional = notionalUsd;
            if (notional == null && price != null)
            {
                try { notional = Money.RoundUsdToCents(price.Value * qty, sideL); } catch { notional = null; }
            }

            double? netUsd = null;
            if (notional != null)
            {
                if (sideL == "buy") netUsd = -(notional.Value + feeValActual);
                else if (sideL == "sell") netUsd = notional.Value - feeValActual;
            }

            if (tagU != "LTH" && bas.Length > 0 && bas != "USDC")
            {
                var pos = _pnlLedger.PosFor(bas, true);

                if (sideL == "buy")
                {
                    if (netUsd != null && netUsd.Value < 0.0)
                    {
                        double usdUsed = -netUsd.Value;
                        pos.UsdCost += usdUsed;
                        pos.Qty += Math.Max(0.0, qty);
                        SavePnlLedger();
                    }
                }
                else if (sideL == "sell")
                {
                    double posQty = pos.Qty;
                    double posCost = pos.UsdCost;
                    double q = Math.Max(0.0, qty);
                    double frac = (posQty > 0.0 && q > 0.0) ? Math.Min(1.0, q / posQty) : 1.0;
                    double costUsed = posCost * frac;
                    pos.UsdCost = posCost - costUsed;
                    pos.Qty = posQty - q;
                    positionCostUsed = costUsed;
                    positionCostAfter = pos.UsdCost;

                    double? usdGot = null;
                    if (notional != null) usdGot = notional.Value - feeValActual;
                    else if (netUsd != null) usdGot = netUsd.Value;

                    if (usdGot != null)
                    {
                        realized = usdGot.Value - costUsed - feeFallback;
                        _pnlLedger.TotalRealizedProfitUsd += realized.Value;
                    }

                    if (pos.Qty <= 1e-12 || pos.UsdCost <= 1e-6) _pnlLedger.OpenPositions.Remove(bas);
                    SavePnlLedger();

                    if (_pnlLedger.OpenPositions.TryGetValue(bas, out var pos2) && pos2 != null)
                        _costBasis[bas] = pos2.Qty > 0 ? pos2.UsdCost / pos2.Qty : 0.0;
                }
            }

            // fallback realized calc (rare)
            if (tagU != "LTH" && realized == null && sideL == "sell" && price != null && avgCostBasis != null)
            {
                realized = (price.Value - avgCostBasis.Value) * qty - feeValActual - feeFallback;
                _pnlLedger.TotalRealizedProfitUsd += realized.Value;
                SavePnlLedger();
            }

            var entry = new JObject
            {
                ["ts"] = ts,
                ["side"] = side,
                ["tag"] = tag == null ? JValue.CreateNull() : (JToken)tag,
                ["symbol"] = symbol,
                ["qty"] = qty,
                ["price"] = price.HasValue ? (JToken)price.Value : JValue.CreateNull(),
                ["notional_usd"] = notional.HasValue ? (JToken)notional.Value : JValue.CreateNull(),
                ["net_usd"] = netUsd.HasValue ? (JToken)netUsd.Value : JValue.CreateNull(),
                ["avg_cost_basis"] = avgCostBasis.HasValue ? (JToken)avgCostBasis.Value : JValue.CreateNull(),
                ["pnl_pct"] = pnlPct.HasValue ? (JToken)pnlPct.Value : JValue.CreateNull(),
                ["fees_usd"] = feesUsd.HasValue ? (JToken)feesUsd.Value : JValue.CreateNull(),
                ["fees_missing"] = feesMissing,
                ["fees_fallback_applied_usd"] = feeFallback > 0.0 ? (JToken)feeFallback : 0.0,
                ["realized_profit_usd"] = realized.HasValue ? (JToken)realized.Value : JValue.CreateNull(),
                ["order_id"] = orderId == null ? JValue.CreateNull() : (JToken)orderId,
                ["position_cost_used_usd"] = positionCostUsed.HasValue ? (JToken)positionCostUsed.Value : JValue.CreateNull(),
                ["position_cost_after_usd"] = positionCostAfter.HasValue ? (JToken)positionCostAfter.Value : JValue.CreateNull(),
            };
            JsonStore.AppendJsonl(AppPaths.TradeHistoryPath, entry);

            // keep in-memory history-owned ids fresh so DCA stage doesn't reset
            if (tagU != "LTH" && bas.Length > 0 && bas != "USDC" && !string.IsNullOrEmpty(orderId))
            {
                if (!_botOrderIdsFromHistory.TryGetValue(bas, out var set)) { set = new HashSet<string>(); _botOrderIdsFromHistory[bas] = set; }
                set.Add(orderId);
            }

            if (tagU != "LTH" && sideL == "sell" && realized != null)
            {
                try { MaybeProcessLthProfitAllocation(realized.Value); } catch { }
            }
        }

        // ==================================================================
        // Order placement
        // ==================================================================
        public JToken PlaceBuyOrder(string clientOrderId, string side, string orderType, string symbol,
            double amountInUsd, double? avgCostBasis = null, double? pnlPct = null, string tag = null)
        {
            if (_paperMode) { PaperLogBuy(symbol, amountInUsd, tag); return null; }

            var (buyPrices, _, _) = GetPrice(new[] { symbol });
            if (!buyPrices.TryGetValue(symbol, out double currentPrice) || currentPrice <= 0.0) return null;
            double assetQuantity = amountInUsd / currentPrice;

            int retries = 0;
            while (retries < 5)
            {
                retries++;
                JToken response = null;
                try
                {
                    double roundedQty = Math.Round(assetQuantity, 8, MidpointRounding.AwayFromZero);
                    var body = new JObject
                    {
                        ["client_order_id"] = clientOrderId,
                        ["side"] = side,
                        ["type"] = orderType,
                        ["symbol"] = symbol,
                        ["market_order_config"] = new JObject { ["asset_quantity"] = roundedQty.ToString("F8", CultureInfo.InvariantCulture) },
                    };
                    response = _rh.PlaceOrder(JsonConvert.SerializeObject(body));

                    if (response != null && !Js.Has(response, "errors"))
                    {
                        string orderId = Js.Str(response, "id", null);
                        if (!string.IsNullOrEmpty(orderId))
                        {
                            _pnlLedger.PendingOrders[orderId] = new JObject
                            {
                                ["symbol"] = symbol,
                                ["side"] = "buy",
                                ["avg_cost_basis"] = avgCostBasis.HasValue ? (JToken)avgCostBasis.Value : JValue.CreateNull(),
                                ["pnl_pct"] = pnlPct.HasValue ? (JToken)pnlPct.Value : JValue.CreateNull(),
                                ["tag"] = tag == null ? JValue.CreateNull() : (JToken)tag,
                                ["created_ts"] = TimeUtil.UnixNow(),
                            };
                            SavePnlLedger();

                            var order = WaitForOrderTerminal(symbol, orderId);
                            string state = order != null ? (Js.Str(order, "state", "") ?? "").ToLowerInvariant().Trim() : "";
                            if (state != "filled")
                            {
                                _pnlLedger.PendingOrders.Remove(orderId); SavePnlLedger();
                                return null;
                            }

                            var (filledQty, avgFillPrice, notionalUsd, feesUsd) = ExtractAmountsAndFees(order);
                            RecordTrade("buy", symbol, filledQty, avgFillPrice, notionalUsd, feesUsd, avgCostBasis, pnlPct, tag, orderId);

                            string baseSymbol = symbol.ToUpperInvariant().Split('-')[0].Trim();
                            MarkBotOrderId(baseSymbol, orderId);

                            if ((tag ?? "").ToUpperInvariant().Trim() == "DCA")
                            {
                                var levels = _dcaLevelsTriggered.TryGetValue(baseSymbol, out var lv) ? new List<int>(lv) : new List<int>();
                                levels.Add(levels.Count);
                                _dcaLevelsTriggered[baseSymbol] = levels;
                            }
                            else _dcaLevelsTriggered[baseSymbol] = new List<int>();

                            _pnlLedger.PendingOrders.Remove(orderId); SavePnlLedger();
                        }
                        return response;
                    }
                }
                catch { }

                // precision error handling
                if (response != null && Js.Has(response, "errors") && response["errors"] is JArray errs)
                {
                    bool broke = false;
                    foreach (var error in errs.OfType<JObject>())
                    {
                        string detail = Js.Str(error, "detail", "") ?? "";
                        if (detail.Contains("has too much precision"))
                        {
                            try
                            {
                                string nearestValue = detail.Split(new[] { "nearest " }, StringSplitOptions.None)[1].Split(' ')[0];
                                int decimalPlaces = nearestValue.Split('.')[1].TrimEnd('0').Length;
                                assetQuantity = Math.Round(assetQuantity, decimalPlaces, MidpointRounding.AwayFromZero);
                            }
                            catch { }
                            broke = true; break;
                        }
                        if (detail.Contains("must be greater than or equal to")) return null;
                    }
                    if (broke) continue;
                }
            }
            return null;
        }

        public JToken PlaceSellOrder(string clientOrderId, string side, string orderType, string symbol,
            double assetQuantity, double? expectedPrice = null, double? avgCostBasis = null, double? pnlPct = null, string tag = null)
        {
            if (_paperMode) { PaperLogSell(symbol, assetQuantity, expectedPrice, tag); return null; }

            var body = new JObject
            {
                ["client_order_id"] = clientOrderId,
                ["side"] = side,
                ["type"] = orderType,
                ["symbol"] = symbol,
                ["market_order_config"] = new JObject { ["asset_quantity"] = assetQuantity.ToString("F8", CultureInfo.InvariantCulture) },
            };
            var response = _rh.PlaceOrder(JsonConvert.SerializeObject(body));

            if (response is JObject robj && !Js.Has(robj, "errors"))
            {
                string orderId = Js.Str(robj, "id", null);
                if (!string.IsNullOrEmpty(orderId))
                {
                    _pnlLedger.PendingOrders[orderId] = new JObject
                    {
                        ["symbol"] = symbol,
                        ["side"] = "sell",
                        ["avg_cost_basis"] = avgCostBasis.HasValue ? (JToken)avgCostBasis.Value : JValue.CreateNull(),
                        ["pnl_pct"] = pnlPct.HasValue ? (JToken)pnlPct.Value : JValue.CreateNull(),
                        ["tag"] = tag == null ? JValue.CreateNull() : (JToken)tag,
                        ["created_ts"] = TimeUtil.UnixNow(),
                    };
                    SavePnlLedger();

                    try
                    {
                        var match = WaitForOrderTerminal(symbol, orderId);
                        if (match == null) return response;
                        if ((Js.Str(match, "state", "") ?? "").ToLowerInvariant() != "filled")
                        {
                            _pnlLedger.PendingOrders.Remove(orderId); SavePnlLedger();
                            return response;
                        }

                        var (actualQty, actualPrice, notionalUsd, feesUsd) = ExtractAmountsAndFees(match);
                        if (avgCostBasis != null && actualPrice != null && avgCostBasis.Value > 0)
                            pnlPct = ((actualPrice.Value - avgCostBasis.Value) / avgCostBasis.Value) * 100.0;

                        RecordTrade("sell", symbol, actualQty, actualPrice, notionalUsd, feesUsd, avgCostBasis, pnlPct, tag, orderId);

                        string baseSymbol = symbol.ToUpperInvariant().Split('-')[0].Trim();
                        ClearBotOrderIdsForCoin(baseSymbol);
                        _dcaLevelsTriggered[baseSymbol] = new List<int>();
                        _trailingPm.Remove(baseSymbol);

                        _pnlLedger.PendingOrders.Remove(orderId); SavePnlLedger();
                    }
                    catch { }
                }
            }
            return response;
        }

        private void WriteTraderStatus(JObject status)
        {
            JsonStore.AtomicWriteJsonWithBackup(AppPaths.TraderStatusPath, status);
        }
    }
}
