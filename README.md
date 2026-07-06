# PowerTrader AI

**Automated crypto spot-trading for Windows.** PowerTrader AI trains a lightweight price-prediction
model on each coin's history, uses it to time entries, and manages positions with a structured,
tiered dollar-cost-averaging (DCA) system and a trailing profit-margin exit — all from a single
desktop control panel. It trades real money on **Robinhood Crypto**.

> ⚠️ **This software places real trades automatically with your money.** Read the
> [Risks](#risks-read-this) section before you run it. Nothing here is financial advice, and no
> software can promise a profit. Start small.

---

## Contents
- [What it does](#what-it-does)
- [Requirements](#requirements)
- [Install](#install)
- [First-time setup](#first-time-setup)
- [Daily use](#daily-use)
- [How the strategy works](#how-the-strategy-works)
- [Safety features](#safety-features)
- [Things to keep in mind](#things-to-keep-in-mind)
- [Risks (read this)](#risks-read-this)
- [Files it creates](#files-it-creates)
- [Troubleshooting](#troubleshooting)

---

## What it does

PowerTrader AI is one app you run day-to-day — the **Hub** — plus three background engines it starts
for you:

| Component | Role |
|---|---|
| **Hub** (`pt_hub.exe`) | The window you use: buttons, live charts, current-trades table, signal tiles, settings, and the Robinhood API wizard. Starts/stops everything else. |
| **Trainer** (`pt_trainer.exe`) | Builds each coin's "memory" from its full price history on KuCoin. Run once per coin (and re-run periodically to stay fresh). |
| **Predictor** (`pt_thinker.exe`) | Continuously turns the trained memory into predicted price levels and buy/sell signals. |
| **Trader** (`pt_trader.exe`) | Reads the predictor's signals and your settings and places the actual buy/sell orders on Robinhood. |

You normally only ever click buttons in the Hub. It launches and supervises the engines.

**Market data** comes from KuCoin (public, no login). **Trades** execute on Robinhood Crypto using an
API key you create. Prices are quoted per coin against USD.

---

## Requirements

- **Windows 10 or 11** (64-bit).
- **.NET Framework 4.8 runtime** — already included in current Windows; if prompted, install it from
  Microsoft.
- A **Robinhood account with Crypto Trading API access**, and permission to place crypto trades.
- Some **USD buying power** in that Robinhood account for the bot to trade with.
- An internet connection.

> **Before you start:** if you already hold crypto in Robinhood that you do **not** want the bot to
> manage, that's fine — the bot only touches positions you explicitly assign to it (see
> [First-time setup](#first-time-setup), step 6). But the safest first run is on an account with only
> the funds you're willing to expose.

---

## Install

1. Put all the app files in one folder, e.g. `C:\PowerTraderAI\`. That folder must contain
   **`pt_hub.exe`, `pt_thinker.exe`, `pt_trader.exe`, `pt_trainer.exe`** and the DLLs shipped alongside
   them (`Newtonsoft.Json.dll`, `BouncyCastle.Cryptography.dll`, `PowerTrader.Core.dll`). The Hub
   expects the three engine `.exe` files to sit next to it.
2. Double-click **`pt_hub.exe`**. This is the only thing you launch by hand.

*(Building from source instead? Install the .NET Framework 4.8 targeting pack, then run
`dotnet build -c Release` in the `csharp` folder; copy the four `pt_*.exe` and the DLLs from
`PowerTrader.Hub/bin/Release/net48/` into one folder.)*

---

## First-time setup

Do these once, in order, from inside the Hub.

1. **Open Settings.** Click **Settings** in the top toolbar.
2. **Main neural folder.** Set this to a folder where the app can store each coin's data. The folder
   containing `pt_hub.exe` is a good default.
3. **Choose your coins.** In *Coins*, enter a comma list (e.g. `BTC,ETH,SOL`). Start with just **BTC**
   until you're comfortable.
4. **Set up Robinhood API keys.** Click **Robinhood API Setup / Update**:
   1. Click **Generate Keys** — this creates a fresh keypair.
   2. **Copy the Public Key** and, in your Robinhood account, add a new API key and paste it there.
      Grant it permission to trade.
   3. Robinhood shows you an **API key** (often starts with `rh`). Paste it into the wizard.
   4. Tick the box confirming the private key is secret, then **Save**.
   5. This writes two files next to the app: **`r_key.txt`** and **`r_secret.txt`**. Keep them private —
      anyone with them can trade your account.
5. **Review trade settings** (see [How the strategy works](#how-the-strategy-works) and
   [Safety features](#safety-features)) and **Save**.
6. **Train your coins.** Click **Train All** and wait until every coin shows **TRAINED** in the
   training status line. Training reads each coin's full history and can take several minutes per coin.
7. **Start.** Click **Start All**. The Hub will:
   - start the Predictor,
   - wait until it reports it's producing real predictions,
   - ask you (per currently-held coin) **which existing buy orders belong to the bot's current trade**
     (so your manual/long-term holdings are left alone), then
   - start the Trader.

That's it. The bot is now running.

---

## Daily use

- **Just leave the Hub running.** It keeps the Predictor and Trader alive and shows you what's happening.
- **Start All / Stop All** start or stop the whole system.
- The **Coin Chart** tab shows candles with the AI's blue (buy-side) and orange (sell-side) levels, plus
  your current bid/ask, average cost, DCA line and trailing-sell line once you're in a trade.
- The **Account Value** tab plots your total account value over time with trade markers.
- The **Current Trades** table shows each position: quantity, average cost, live PnL, DCA stage, and the
  trailing-sell line.
- The **signal tiles** show each coin's LONG/SHORT strength (0–7). A trade starts when LONG reaches your
  *Trade start level* while SHORT is 0.
- **Adding a coin later:** Settings → add it to *Coins* → Save → **Train All** (trains the new one) →
  **Start All**.
- **Re-training:** the bot treats a coin as "not trained" if its training is missing or older than
  14 days, and won't open new trades on it until you re-run **Train All**. Re-train periodically.

---

## How the strategy works

- **Prediction.** For each coin, the model stores historical candle "patterns" and what happened next.
  It predicts the current candle's high/low on every timeframe from 1 hour to 1 week, then draws those
  as horizontal levels ("neural lines").
- **Entry.** A trade starts when a coin's LONG signal reaches your **Trade start level** (default 4)
  while SHORT is 0. The first buy uses **Start allocation %** of your account value.
- **DCA (averaging down).** If price falls, the bot buys more at the next neural line **or** at a
  hardcoded drawdown %, whichever comes first — each DCA sized at **DCA multiplier** × the current
  position (default 2×). A rolling **Max DCA buys / 24h** limit (default 1) throttles how fast it adds.
- **Exit.** The bot uses a **trailing profit margin**: once price rises past a starting margin
  (default +3%), the sell line trails the peak by **Trailing gap %** (default 0.1%). It sells when price
  falls back through that trailing line — locking in gains while letting winners run.
- **By default there is no stop-loss.** The base strategy never sells at a loss; it waits or averages
  down. You can turn on a hard stop — see [Safety features](#safety-features).
- **Long-term holdings.** Optionally, a chosen % of realized profits is auto-invested into your
  long-term coins (whichever is furthest below its 200-day average).

Every trade parameter above is editable in **Settings** and takes effect without restarting.

---

## Safety features

These are **off by default** (default behavior has no stop-loss and unbounded DCA). Turn them on in
Settings to bound your risk. They apply live — no restart needed.

| Setting | Default | What it does |
|---|---|---|
| **Paper mode** | Off | Runs the full strategy against your **live** account/market data but **places no orders** — it just logs what it *would* do (to `hub_data/paper_orders.jsonl` and the Trader log). The account bar shows `[PAPER MODE]`. Use this first to confirm everything is wired up before risking money. |
| **Catastrophic stop %** | 0 (off) | A real stop-loss: force-sells a coin when its loss reaches this %, before it can DCA further. |
| **Max capital $/coin** | 0 (off) | Caps how much money the bot will put into any single coin; stops DCA once the cap is hit. |
| **Venue divergence %** | 0 (off) | Refuses to open a new trade if the KuCoin price (used for signals) and the Robinhood price (used to trade) disagree by more than this %. |
| **Signal max age (s)** | 0 (off) | Ignores buy/DCA signals if the Predictor's output files are older than this many seconds (protects against acting on stale data if the Predictor stalls). |

The Trader also always writes `hub_data/reconciliation.json` comparing what its ledger thinks it owns
vs. what Robinhood actually reports, and warns you if they drift apart.

**Recommended first run:** turn on **Paper mode**, click Start All, and watch the Trader log for a while.
When you're satisfied, turn Paper mode off and consider setting a **Catastrophic stop %** and
**Max capital $/coin** that match your risk tolerance.

---

## Things to keep in mind

- **It only manages what you assign to it.** On trader start you pick which existing buy orders belong
  to the bot per coin. Anything you don't select is treated as manual/long-term and ignored — so you can
  still trade or hold coins yourself in the same account.
- **Coins must be trained (and stay fresh).** No signals, no trades. Training older than 14 days is
  treated as untrained for opening new positions.
- **Two venues.** Signals are computed from KuCoin prices; orders fill at Robinhood prices. These can
  differ slightly. The *Venue divergence %* guard exists for exactly this.
- **Market orders.** Entries, DCAs and exits are market orders — you get the current fill price, not a
  limit price.
- **Keep the machine awake.** The Hub must keep running for trading to continue. Sleep/hibernate pauses
  everything.
- **Your keys are plaintext.** `r_key.txt` / `r_secret.txt` sit next to the app. Protect that folder.
- **Fees and rounding.** Robinhood settles to the cent and may charge fees; the bot accounts for this,
  but tiny differences vs. the app can occur.

---

## Risks (read this)

Trading crypto is risky, and **automation multiplies mistakes**. Understand these before running live:

- **You can lose money — potentially a lot.** By default the bot **does not sell at a loss** and keeps
  **doubling down** as price falls. A coin in a sustained downtrend can tie up (or, in a worst case such
  as a collapse/delisting, effectively wipe out) the capital committed to it. Enabling
  **Catastrophic stop %** and **Max capital $/coin** is strongly recommended if that's not acceptable to
  you.
- **No profit is guaranteed.** Past patterns don't predict the future. The model is deliberately simple.
- **Software and connectivity failures happen.** API outages, rate limits, a crashed Predictor, a
  sleeping PC, or a bug can cause missed exits, stale signals, or unexpected behavior. Monitor it.
- **API-key security.** Anyone who obtains `r_secret.txt` can trade your account. Never share it; keep
  backups private.
- **You are fully responsible** for the bot's actions, your funds, your account security, and doing your
  own due diligence. This project is provided for experimentation and education, with no warranty.

**Start on a small balance you can afford to lose, run in Paper mode first, and verify behavior before
scaling up.**

---

## Files it creates

Inside your main folder:
- `gui_settings.json` — your settings.
- `r_key.txt`, `r_secret.txt` — Robinhood API credentials (keep private).
- Per coin: a folder (BTC uses the main folder) holding the trained memory and the latest signal/level
  files.
- `hub_data/` — shared status the Hub reads: `trader_status.json`, `trade_history.jsonl`,
  `pnl_ledger.json`, `account_value_history.jsonl`, `bot_order_ids.json`, `runner_ready.json`,
  `reconciliation.json`, and (in Paper mode) `paper_orders.jsonl`.

Deleting `hub_data/` resets the Hub's view and the local profit ledger (it does not touch your Robinhood
account). Deleting a coin's training files forces a re-train.

---

## Troubleshooting

- **"Robinhood API credentials not found."** Run Settings → Robinhood API Setup and Save. Make sure
  `r_key.txt` and `r_secret.txt` ended up in the main folder.
- **A coin never starts trading.** Confirm it shows **TRAINED**, that its LONG signal is reaching your
  *Trade start level* with SHORT at 0, and (if enabled) that the venue-divergence / signal-age guards
  aren't blocking it — the Trader log explains skips.
- **Charts are empty / "no candles".** KuCoin may be briefly unreachable, or the coin symbol isn't a
  valid KuCoin USDT pair. It will recover on the next refresh.
- **Training seems stuck.** It downloads a coin's full history first (minutes per coin) before it writes
  anything; give it time. The training status line shows TRAINING → TRAINED when done.
- **Nothing trades but everything looks running.** Check whether **Paper mode** is on (the account bar
  shows `[PAPER MODE]`). In Paper mode the bot only logs intended orders.
