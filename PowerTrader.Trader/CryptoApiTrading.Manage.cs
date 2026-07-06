using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using PowerTrader.Core.Config;
using PowerTrader.Core.Util;

namespace PowerTrader.Trader
{
    public sealed partial class CryptoApiTrading
    {
        public void Run()
        {
            while (true)
            {
                try { ManageTrades(); Thread.Sleep(500); }
                catch (Exception e) { Console.WriteLine(e); }
            }
        }

        private void ManageTrades()
        {
            bool tradesMade = false;

            // ---- hot-reload settings ----
            try
            {
                RefreshPathsAndSymbols();
                _dcaLevels = new List<double>(DcaLevelsGlobal);
                _maxDcaBuysPer24h = MaxDcaBuysPer24hGlobal;

                var oldSig = _lastTrailingSettingsSig;
                _trailingGapPct = TrailingGapPctGlobal;
                _pmStartPctNoDca = PmStartPctNoDcaGlobal;
                _pmStartPctWithDca = PmStartPctWithDcaGlobal;
                var newSig = Tuple.Create(_trailingGapPct, _pmStartPctNoDca, _pmStartPctWithDca);
                if (oldSig != null && !newSig.Equals(oldSig)) _trailingPm.Clear();
                _lastTrailingSettingsSig = newSig;
            }
            catch { }

            try { MaybeProcessLthProfitAllocation(0.0); } catch { }

            var account = GetAccount();
            var holdings = GetHoldings();
            var tradingPairs = GetTradingPairs();

            try { if (MaybeReloadBotOrderIds()) _needsLedgerSeedFromOrders = true; } catch { }

            var costBasis = _costBasis;

            // ---- collect symbols to price ----
            var holdingsResults = holdings?["results"] as JArray ?? new JArray();
            var symbols = new List<string>();
            foreach (var h in holdingsResults.OfType<JObject>())
                symbols.Add((Js.Str(h, "asset_code", "") ?? "") + "-USD");
            foreach (var s in CryptoSymbols)
            {
                string full = s + "-USD";
                if (!symbols.Contains(full)) symbols.Add(full);
            }

            var (currentBuyPrices, currentSellPrices, validSymbols) = GetPrice(symbols);

            // ---- account value snapshot ----
            bool snapshotOk = true;
            double buyingPower;
            try { buyingPower = Js.Double(account, "buying_power", 0.0); }
            catch { buyingPower = 0.0; snapshotOk = false; }

            JArray holdingsList = holdings?["results"] as JArray;
            if (holdingsList == null) { holdingsList = new JArray(); snapshotOk = false; }

            try
            {
                if (_needsLedgerSeedFromOrders)
                {
                    SeedOpenPositionsFromSelectedOrders(holdingsList);
                    try { InitializeDcaLevels(); } catch { }
                    _needsLedgerSeedFromOrders = false;
                }
            }
            catch { }

            double holdingsBuyValue = 0.0, holdingsSellValue = 0.0;
            double tradeHoldingsBuyValue = 0.0, tradeHoldingsSellValue = 0.0;

            foreach (var holding in holdingsList.OfType<JObject>())
            {
                try
                {
                    string asset = Js.Str(holding, "asset_code", "");
                    if (asset == "USDC") continue;
                    double qty = Js.Double(holding, "total_quantity", 0.0);
                    if (qty <= 0.0) continue;
                    string sym = asset + "-USD";
                    double bp = currentBuyPrices.TryGetValue(sym, out var b) ? b : 0.0;
                    double sp = currentSellPrices.TryGetValue(sym, out var s) ? s : 0.0;
                    if (bp <= 0.0 || sp <= 0.0) { snapshotOk = false; continue; }

                    holdingsBuyValue += qty * bp;
                    holdingsSellValue += qty * sp;

                    string basSym = (asset ?? "").Trim().ToUpperInvariant();
                    double tradableQty = (_pnlLedger.OpenPositions.TryGetValue(basSym, out var pos) && pos != null) ? pos.Qty : 0.0;
                    if (tradableQty < 0.0) tradableQty = 0.0;
                    if (tradableQty > qty) tradableQty = qty;

                    double excessQty = qty - tradableQty;
                    if (excessQty > 0.0 && (excessQty * sp) <= 0.01) tradableQty = qty;

                    tradeHoldingsBuyValue += tradableQty * bp;
                    tradeHoldingsSellValue += tradableQty * sp;
                }
                catch { snapshotOk = false; }
            }

            double totalAccountValue = buyingPower + holdingsSellValue;
            double inUse = totalAccountValue > 0 ? (tradeHoldingsSellValue / totalAccountValue) * 100.0 : 0.0;

            if (!snapshotOk || totalAccountValue <= 0.0)
            {
                var last = _lastGoodAccountSnapshot;
                if (last.TotalAccountValue != null)
                {
                    totalAccountValue = last.TotalAccountValue.Value;
                    buyingPower = last.BuyingPower ?? buyingPower;
                    holdingsSellValue = last.HoldingsSellValue ?? holdingsSellValue;
                    holdingsBuyValue = last.HoldingsBuyValue ?? holdingsBuyValue;
                    inUse = last.PercentInTrade ?? inUse;
                }
            }
            else
            {
                _lastGoodAccountSnapshot = new AccountSnapshot
                {
                    TotalAccountValue = totalAccountValue,
                    BuyingPower = buyingPower,
                    HoldingsSellValue = holdingsSellValue,
                    HoldingsBuyValue = holdingsBuyValue,
                    PercentInTrade = inUse,
                };
            }

            try { Console.Clear(); } catch { }
            Console.WriteLine("\n--- Account Summary ---");
            Console.WriteLine($"Total Account Value: ${totalAccountValue:F2}");
            Console.WriteLine($"Holdings Value: ${holdingsSellValue:F2}");
            Console.WriteLine($"Percent In Trade: {inUse:F2}%");
            Console.WriteLine($"Trailing PM: start +{_pmStartPctNoDca:F2}% (no DCA) / +{_pmStartPctWithDca:F2}% (with DCA) | gap {_trailingGapPct:F2}%");
            Console.WriteLine("\n--- Current Trades ---");

            var positions = new JObject();

            foreach (var holding in holdingsResults.OfType<JObject>())
            {
                string symbol = Js.Str(holding, "asset_code", "");
                string fullSymbol = symbol + "-USD";
                if (symbol == "USDC") continue;

                double totalQuantity = Js.Double(holding, "total_quantity", 0.0);
                string basSym = (symbol ?? "").Trim().ToUpperInvariant();
                double quantity = (_pnlLedger.OpenPositions.TryGetValue(basSym, out var lpos) && lpos != null) ? lpos.Qty : 0.0;
                if (quantity < 0.0) quantity = 0.0;
                if (quantity > totalQuantity) quantity = totalQuantity;

                double reservedQty = totalQuantity - quantity;
                if (reservedQty < 0.0) reservedQty = 0.0;

                if (quantity > 0.0 && reservedQty > 0.0)
                {
                    double dustSp = currentSellPrices.TryGetValue(fullSymbol, out var dsp) ? dsp : 0.0;
                    if (dustSp > 0.0 && (reservedQty * dustSp) <= 0.01) { quantity = totalQuantity; reservedQty = 0.0; }
                }

                if (quantity <= 0.0)
                {
                    _trailingPm.Remove(symbol);
                    _dcaLevelsTriggered.Remove(symbol);
                    double cbp = currentBuyPrices.TryGetValue(fullSymbol, out var cb0) ? cb0 : 0.0;
                    double csp = currentSellPrices.TryGetValue(fullSymbol, out var cs0) ? cs0 : 0.0;
                    TryWriteCurrentPrice(symbol, cbp);
                    positions[symbol] = EmptyPosition(cbp, csp, reservedQty);
                    continue;
                }

                double currentBuyPrice = currentBuyPrices.TryGetValue(fullSymbol, out var cbp1) ? cbp1 : 0.0;
                double currentSellPrice = currentSellPrices.TryGetValue(fullSymbol, out var csp1) ? csp1 : 0.0;

                // avg cost basis (ledger, with suspicious-value repair)
                double avgCostBasis = 0.0;
                double ledgerPq = 0.0, ledgerPc = 0.0;
                if (_pnlLedger.OpenPositions.TryGetValue(symbol, out var pos2) && pos2 != null)
                {
                    ledgerPq = pos2.Qty; ledgerPc = pos2.UsdCost;
                    if (ledgerPq > 0.0) avgCostBasis = ledgerPc / ledgerPq;
                }

                try
                {
                    bool suspicious = false;
                    if (avgCostBasis > 0.0 && currentBuyPrice > 0.0)
                    {
                        double ratio = avgCostBasis / currentBuyPrice;
                        if (ratio > 5.0 || ratio < 0.2) suspicious = true;
                    }
                    if (suspicious)
                    {
                        double freshCb = 0.0;
                        try { var fresh = CalculateCostBasis(); freshCb = fresh.TryGetValue(symbol, out var fc) ? fc : 0.0; } catch { }
                        if (freshCb > 0.0 && (avgCostBasis <= 0.0 || Math.Abs((avgCostBasis / freshCb) - 1.0) > 0.5))
                        {
                            avgCostBasis = freshCb;
                            string baseKey = (symbol ?? "").ToUpperInvariant().Split('-')[0].Trim();
                            _pnlLedger.OpenPositions[baseKey] = new OpenPosition { UsdCost = avgCostBasis * quantity, Qty = quantity };
                            SavePnlLedger();
                        }
                    }
                }
                catch { }

                if (avgCostBasis <= 0.0) avgCostBasis = costBasis.TryGetValue(symbol, out var cbm) ? cbm : 0.0;

                double gainLossBuy, gainLossSell;
                if (avgCostBasis > 0)
                {
                    gainLossBuy = ((currentBuyPrice - avgCostBasis) / avgCostBasis) * 100.0;
                    gainLossSell = ((currentSellPrice - avgCostBasis) / avgCostBasis) * 100.0;
                }
                else { gainLossBuy = 0; gainLossSell = 0; }

                double value = quantity * currentSellPrice;
                int triggeredCount = _dcaLevelsTriggered.TryGetValue(symbol, out var trg) ? trg.Count : 0;
                int triggeredLevels = triggeredCount;
                int nextStage = triggeredCount;

                double hardNext = nextStage < _dcaLevels.Count ? _dcaLevels[nextStage] : _dcaLevels[_dcaLevels.Count - 1];
                int startLevel = Math.Max(1, Math.Min(TradeStartLevel, 7));
                int neuralDcaMax = Math.Max(0, 7 - startLevel);

                string nextDcaDisplay;
                if (nextStage < neuralDcaMax) nextDcaDisplay = $"{hardNext:F2}% / N{startLevel + 1 + nextStage}";
                else nextDcaDisplay = $"{hardNext:F2}%";

                string dcaLineSource = "HARD";
                double dcaLinePrice = 0.0, dcaLinePct = 0.0;
                if (avgCostBasis > 0)
                {
                    double hardLinePrice = avgCostBasis * (1.0 + (hardNext / 100.0));
                    dcaLinePrice = hardLinePrice;
                    if (nextStage < neuralDcaMax)
                    {
                        int neuralLevelNeededDisp = startLevel + 1 + nextStage;
                        var neuralLevels = ReadLongPriceLevels(symbol);
                        double neuralLinePrice = 0.0;
                        if (neuralLevels.Count >= neuralLevelNeededDisp) neuralLinePrice = neuralLevels[neuralLevelNeededDisp - 1];
                        if (neuralLinePrice > dcaLinePrice) { dcaLinePrice = neuralLinePrice; dcaLineSource = $"NEURAL N{neuralLevelNeededDisp}"; }
                    }
                    dcaLinePct = gainLossBuy;
                }

                TryWriteCurrentPrice(symbol, currentBuyPrice);

                // trailing PM display values
                double trailLineDisp = 0.0, trailPeakDisp = 0.0;
                bool trailOn = false;
                if (avgCostBasis > 0)
                {
                    double pmStartPctDisp = triggeredLevels == 0 ? _pmStartPctNoDca : _pmStartPctWithDca;
                    double basePmLineDisp = avgCostBasis * (1.0 + (pmStartPctDisp / 100.0));
                    if (_trailingPm.TryGetValue(symbol, out var std))
                    {
                        trailLineDisp = std.Line; trailPeakDisp = std.Peak;
                        bool active = std.Active;
                        bool above = currentSellPrice >= trailLineDisp;
                        trailOn = active || above;
                    }
                    else { trailLineDisp = basePmLineDisp; trailOn = currentSellPrice >= trailLineDisp; }
                }
                double distToTrailPct = trailLineDisp > 0 ? ((currentSellPrice - trailLineDisp) / trailLineDisp) * 100.0 : 0.0;

                positions[symbol] = new JObject
                {
                    ["quantity"] = quantity,
                    ["avg_cost_basis"] = avgCostBasis,
                    ["current_buy_price"] = currentBuyPrice,
                    ["current_sell_price"] = currentSellPrice,
                    ["gain_loss_pct_buy"] = gainLossBuy,
                    ["gain_loss_pct_sell"] = gainLossSell,
                    ["value_usd"] = value,
                    ["dca_triggered_stages"] = triggeredCount,
                    ["next_dca_display"] = nextDcaDisplay,
                    ["dca_line_price"] = dcaLinePrice,
                    ["dca_line_source"] = dcaLineSource,
                    ["dca_line_pct"] = dcaLinePct,
                    ["trail_active"] = trailOn,
                    ["trail_line"] = trailLineDisp,
                    ["trail_peak"] = trailPeakDisp,
                    ["dist_to_trail_pct"] = distToTrailPct,
                    ["lth_reserved_qty"] = reservedQty,
                };

                Console.WriteLine($"\nSymbol: {symbol}  |  DCA: {dcaLinePct:+0.00;-0.00}% @ {FmtPrice(currentBuyPrice)} " +
                                  $"(Line: {(avgCostBasis > 0 ? FmtPrice(dcaLinePrice) : "N/A")} {dcaLineSource} | Next: {nextDcaDisplay})  " +
                                  $"|  Gain/Loss SELL: {gainLossSell:F2}% @ {FmtPrice(currentSellPrice)}  |  DCA Levels Triggered: {triggeredLevels}  |  Trade Value: ${value:F2}");

                // ---- A1: catastrophic stop (opt-in). Force-exit before any DCA can add to a losing position. ----
                if (avgCostBasis > 0 && CatastrophicStopHit(gainLossSell))
                {
                    Console.WriteLine($"  [CATASTROPHIC STOP] {symbol} sell PnL {gainLossSell:F2}% <= -{_catastrophicStopPct:F2}%. Force-exiting.");
                    var stopResp = PlaceSellOrder(Guid.NewGuid().ToString(), "sell", "market", fullSymbol, quantity,
                        expectedPrice: currentSellPrice, avgCostBasis: avgCostBasis, pnlPct: gainLossSell, tag: "CATASTROPHIC_STOP");
                    if (stopResp is JObject sr && !Js.Has(sr, "errors"))
                    {
                        tradesMade = true;
                        _trailingPm.Remove(symbol);
                        ResetDcaWindowForTrade(symbol, sold: true);
                        _dcaLevelsTriggered[symbol] = new List<int>();
                        Console.WriteLine($"  Catastrophic-stopped {symbol}.");
                        System.Threading.Thread.Sleep(5000);
                        continue;
                    }
                }

                // ---- trailing profit margin ----
                if (avgCostBasis > 0)
                {
                    double pmStartPct = triggeredLevels == 0 ? _pmStartPctNoDca : _pmStartPctWithDca;
                    double basePmLine = avgCostBasis * (1.0 + (pmStartPct / 100.0));
                    double trailGap = _trailingGapPct / 100.0;
                    var settingsSig = Tuple.Create(_trailingGapPct, _pmStartPctNoDca, _pmStartPctWithDca);

                    if (!_trailingPm.TryGetValue(symbol, out var state) || !settingsSig.Equals(state.SettingsSig))
                    {
                        state = new TrailState { Active = false, Line = basePmLine, Peak = 0.0, WasAbove = false, SettingsSig = settingsSig };
                        _trailingPm[symbol] = state;
                    }
                    else
                    {
                        state.SettingsSig = settingsSig;
                        if (!state.Active) state.Line = basePmLine;
                        else if (state.Line < basePmLine) state.Line = basePmLine;
                    }

                    bool aboveNow = currentSellPrice >= state.Line;
                    if (!state.Active && aboveNow) { state.Active = true; state.Peak = currentSellPrice; }

                    if (state.Active)
                    {
                        if (currentSellPrice > state.Peak) state.Peak = currentSellPrice;
                        double newLine = state.Peak * (1.0 - trailGap);
                        if (newLine < basePmLine) newLine = basePmLine;
                        if (newLine > state.Line) state.Line = newLine;

                        if (state.WasAbove && currentSellPrice < state.Line)
                        {
                            Console.WriteLine($"  Trailing PM hit for {symbol}. Sell price {currentSellPrice:F8} fell below trailing line {state.Line:F8}.");
                            var response = PlaceSellOrder(Guid.NewGuid().ToString(), "sell", "market", fullSymbol, quantity,
                                expectedPrice: currentSellPrice, avgCostBasis: avgCostBasis, pnlPct: gainLossSell, tag: "TRAIL_SELL");

                            if (response is JObject ro && !Js.Has(ro, "errors"))
                            {
                                tradesMade = true;
                                _trailingPm.Remove(symbol);
                                ResetDcaWindowForTrade(symbol, sold: true);
                                _dcaLevelsTriggered[symbol] = new List<int>();
                                Console.WriteLine($"  Successfully sold {quantity} {symbol}.");
                                Thread.Sleep(5000);
                                continue;
                            }
                        }
                    }
                    state.WasAbove = aboveNow;
                }

                // ---- DCA (neural or hardcoded %, whichever hits first) ----
                {
                    int startLvl = Math.Max(1, Math.Min(TradeStartLevel, 7));
                    int neuralMax = Math.Max(0, 7 - startLvl);
                    int currentStage = _dcaLevelsTriggered.TryGetValue(symbol, out var cs) ? cs.Count : 0;
                    double hardLevel = currentStage < _dcaLevels.Count ? _dcaLevels[currentStage] : _dcaLevels[_dcaLevels.Count - 1];
                    bool hardHit = gainLossBuy <= hardLevel;

                    bool neuralHit = false;
                    int? neuralLevelNeeded = null, neuralLevelNow = null;
                    double? neuralLinePrice = null;
                    if (currentStage < neuralMax)
                    {
                        neuralLevelNeeded = startLvl + 1 + currentStage;
                        neuralLevelNow = ReadLongDcaSignal(symbol);
                        var longLevels = ReadLongPriceLevels(symbol);
                        int idx = neuralLevelNeeded.Value - 1;
                        if (idx >= 0 && idx < longLevels.Count) neuralLinePrice = longLevels[idx];
                        // A4: neural DCA relies on the signal files; ignore them when stale.
                        neuralHit = (gainLossBuy < 0) && (neuralLevelNow.Value >= neuralLevelNeeded.Value)
                            && (neuralLinePrice != null) && (currentBuyPrice <= neuralLinePrice.Value)
                            && SignalsFresh(symbol);
                    }

                    if (hardHit || neuralHit)
                    {
                        Console.WriteLine($"  DCAing {symbol} (stage {currentStage + 1}).");
                        double dcaAmount = value * DcaMultiplier;

                        // A2: clamp the DCA to the per-coin capital cap (0 => disabled).
                        double capped = CapRoomForCoin(symbol, dcaAmount);
                        if (capped < dcaAmount)
                            Console.WriteLine($"  Capital cap ${_maxCapitalPerCoinUsd:F2}/coin: clamping DCA ${dcaAmount:F2} -> ${capped:F2}.");
                        dcaAmount = capped;

                        Console.WriteLine($"  Current Value: ${value:F2}  DCA Amount: ${dcaAmount:F2}  Buying Power: ${buyingPower:F2}");

                        int recentDca = DcaWindowCount(symbol);
                        if (dcaAmount < 0.5)
                        {
                            Console.WriteLine($"  Skipping DCA for {symbol}. Per-coin capital cap reached (no room).");
                        }
                        else if (recentDca >= _maxDcaBuysPer24h)
                        {
                            Console.WriteLine($"  Skipping DCA for {symbol}. Already placed {recentDca} DCA buys in the last 24h (max {_maxDcaBuysPer24h}).");
                        }
                        else if (dcaAmount <= buyingPower)
                        {
                            var response = PlaceBuyOrder(Guid.NewGuid().ToString(), "buy", "market", fullSymbol, dcaAmount,
                                avgCostBasis: avgCostBasis, pnlPct: gainLossBuy, tag: "DCA");
                            if (response != null && !Js.Has(response, "errors"))
                            {
                                NoteDcaBuy(symbol);
                                _trailingPm.Remove(symbol);
                                tradesMade = true;
                                Console.WriteLine($"  Successfully placed DCA buy order for {symbol}.");
                            }
                            else Console.WriteLine($"  Failed to place DCA buy order for {symbol}.");
                        }
                        else Console.WriteLine($"  Skipping DCA for {symbol}. Not enough funds.");
                    }
                }
            }

            // ---- publish bid/ask rows for tracked coins not held ----
            try
            {
                foreach (var sym in CryptoSymbols)
                {
                    if (positions[sym] != null) continue;
                    string fullSymbol = sym + "-USD";
                    if (!validSymbols.Contains(fullSymbol) || sym == "USDC") continue;
                    double cbp = currentBuyPrices.TryGetValue(fullSymbol, out var b) ? b : 0.0;
                    double csp = currentSellPrices.TryGetValue(fullSymbol, out var s) ? s : 0.0;
                    TryWriteCurrentPrice(sym, cbp);
                    var row = EmptyPosition(cbp, csp, 0.0);
                    row["dca_triggered_stages"] = _dcaLevelsTriggered.TryGetValue(sym, out var lv) ? lv.Count : 0;
                    positions[sym] = row;
                }
            }
            catch { }

            if (tradingPairs.Count == 0) return;

            double allocPct = StartAllocPct > 0 ? StartAllocPct : 0.005;
            double allocationInUsd = totalAccountValue * (allocPct / 100.0);
            if (allocationInUsd < 0.5) allocationInUsd = 0.5;

            var holdingFullSymbols = HeldBotSymbols(holdings);

            int startIndex = 0;
            while (startIndex < CryptoSymbols.Count)
            {
                string baseSymbol = CryptoSymbols[startIndex].Trim().ToUpperInvariant();
                string fullSymbol = baseSymbol + "-USD";

                if (holdingFullSymbols.Contains(fullSymbol)) { startIndex++; continue; }

                int buyCount = ReadLongDcaSignal(baseSymbol);
                int sellCount = ReadShortDcaSignal(baseSymbol);
                int startLevel = Math.Max(1, Math.Min(TradeStartLevel, 7));

                if (!(buyCount >= startLevel && sellCount == 0)) { startIndex++; continue; }

                // A4: don't start on a stale signal (thinker may have died).
                if (!SignalsFresh(baseSymbol))
                {
                    Console.WriteLine($"Skipping start for {fullSymbol}: neural signal is stale (> {_signalMaxAgeSeconds:F0}s old).");
                    startIndex++; continue;
                }

                // A3: don't commit capital when the signal venue (KuCoin) and exec venue (Robinhood) disagree.
                double rhStartPrice = currentBuyPrices.TryGetValue(fullSymbol, out var rbp) ? rbp : 0.0;
                if (!VenueDivergenceOk(baseSymbol, rhStartPrice, out double divPct))
                {
                    Console.WriteLine($"Skipping start for {fullSymbol}: KuCoin vs Robinhood price diverged {divPct:F2}% (> {_venueDivergencePct:F2}%).");
                    startIndex++; continue;
                }

                _dcaLevelsTriggered[baseSymbol] = new List<int>();
                _trailingPm.Remove(baseSymbol);

                var response = PlaceBuyOrder(Guid.NewGuid().ToString(), "buy", "market", fullSymbol, allocationInUsd);
                if (response != null && !Js.Has(response, "errors"))
                {
                    tradesMade = true;
                    _dcaLevelsTriggered[baseSymbol] = new List<int>();
                    ResetDcaWindowForTrade(baseSymbol, sold: false);
                    _trailingPm.Remove(baseSymbol);
                    Console.WriteLine($"Starting new trade for {fullSymbol} (AI start signal long={buyCount}, short={sellCount}). Allocating ${allocationInUsd:F2}.");
                    Thread.Sleep(5000);
                    holdings = GetHoldings();
                    holdingFullSymbols = HeldBotSymbols(holdings);
                }
                startIndex++;
            }

            if (tradesMade)
            {
                Thread.Sleep(5000);
                Console.WriteLine("Trades were made in this iteration. Recalculating cost basis...");
                var newCostBasis = CalculateCostBasis();
                if (newCostBasis != null && newCostBasis.Count > 0) { _costBasis = newCostBasis; Console.WriteLine("Cost basis recalculated successfully."); }
                else Console.WriteLine("Failed to recalculate cost basis.");
            }

            // ---- A5: surface ledger vs exchange drift ----
            try { WriteReconciliation(holdingsList, currentSellPrices); } catch { }

            // ---- write status ----
            try
            {
                double now = TimeUtil.UnixNow();
                var status = new JObject
                {
                    ["timestamp"] = now,
                    ["account"] = new JObject
                    {
                        ["total_account_value"] = totalAccountValue,
                        ["buying_power"] = buyingPower,
                        ["holdings_sell_value"] = holdingsSellValue,
                        ["holdings_buy_value"] = holdingsBuyValue,
                        ["percent_in_trade"] = inUse,
                        ["pm_start_pct_no_dca"] = _pmStartPctNoDca,
                        ["pm_start_pct_with_dca"] = _pmStartPctWithDca,
                        ["trailing_gap_pct"] = _trailingGapPct,
                        ["paper_mode"] = _paperMode,
                    },
                    ["positions"] = positions,
                };
                JsonStore.AppendJsonl(AppPaths.AccountValueHistoryPath, new JObject { ["ts"] = now, ["total_account_value"] = totalAccountValue });
                WriteTraderStatus(status);
            }
            catch { }
        }

        // ---- helpers ----
        private HashSet<string> HeldBotSymbols(JObject holdings)
        {
            var outSet = new HashSet<string>();
            var results = holdings?["results"] as JArray;
            if (results == null) return outSet;
            foreach (var h in results.OfType<JObject>())
            {
                try
                {
                    string asset = (Js.Str(h, "asset_code", "") ?? "").Trim().ToUpperInvariant();
                    if (asset.Length == 0 || asset == "USDC") continue;
                    double botQty = (_pnlLedger.OpenPositions.TryGetValue(asset, out var pos) && pos != null) ? pos.Qty : 0.0;
                    if (botQty > 1e-12) outSet.Add(asset + "-USD");
                }
                catch { }
            }
            return outSet;
        }

        private void TryWriteCurrentPrice(string symbol, double price)
        {
            try { File.WriteAllText(Path.Combine(FolderFor(symbol), symbol + "_current_price.txt"), price.ToString("R", CultureInfo.InvariantCulture)); }
            catch { }
        }

        private static JObject EmptyPosition(double buy, double sell, double reservedQty)
        {
            return new JObject
            {
                ["quantity"] = 0.0,
                ["avg_cost_basis"] = 0.0,
                ["current_buy_price"] = buy,
                ["current_sell_price"] = sell,
                ["gain_loss_pct_buy"] = 0.0,
                ["gain_loss_pct_sell"] = 0.0,
                ["value_usd"] = 0.0,
                ["dca_triggered_stages"] = 0,
                ["next_dca_display"] = "",
                ["dca_line_price"] = 0.0,
                ["dca_line_source"] = "N/A",
                ["dca_line_pct"] = 0.0,
                ["trail_active"] = false,
                ["trail_line"] = 0.0,
                ["trail_peak"] = 0.0,
                ["dist_to_trail_pct"] = 0.0,
                ["lth_reserved_qty"] = reservedQty,
            };
        }
    }
}
