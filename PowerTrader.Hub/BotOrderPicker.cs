using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PowerTrader.Core.Robinhood;

namespace PowerTrader.Hub
{
    /// <summary>
    /// Startup bot-order ownership picker (port of _ensure_bot_order_ids_for_current_holdings).
    /// For each currently-held coin, lets the user select which BUY orders belong to the bot's
    /// current trade, and writes bot_order_ids.json. Returns false if the user cancels.
    /// </summary>
    internal static class BotOrderPicker
    {
        public static bool EnsureForCurrentHoldings(IWin32Window owner, string projectDir, string hubDir)
        {
            string botIdsPath = Path.Combine(hubDir, "bot_order_ids.json");
            var existing = LoadBotIds(botIdsPath);

            RobinhoodClient rh = TryClient(projectDir);
            var fromHist = FromTradeHistory(Path.Combine(hubDir, "trade_history.jsonl"));

            var heldCoins = new List<string>();
            if (rh != null)
            {
                var holdings = rh.GetHoldings();
                var results = holdings?["results"] as JArray;
                if (results != null)
                {
                    foreach (var h in results.OfType<JObject>())
                    {
                        string sym = (h["asset_code"]?.ToString() ?? "").Trim().ToUpperInvariant();
                        if (sym.Length == 0 || sym == "USD" || sym == "USDT" || sym == "USDC") continue;
                        double qty = D(h, "total_quantity");
                        if (qty <= 1e-12) qty = D(h, "quantity");
                        if (qty > 1e-12) heldCoins.Add(sym);
                    }
                }
                else
                {
                    heldCoins = existing.Keys.Union(fromHist.Keys).Distinct().ToList();
                }
            }
            else
            {
                // no creds/session -> use local hints
                heldCoins = existing.Keys.Union(fromHist.Keys).Distinct().ToList();
            }

            if (heldCoins.Count == 0) return true;

            bool changed = false;
            foreach (var sym in heldCoins.Distinct().OrderBy(x => x))
            {
                var preselect = fromHist.TryGetValue(sym, out var ph) && ph.Count > 0
                    ? new HashSet<string>(ph)
                    : (existing.TryGetValue(sym, out var pe) ? new HashSet<string>(pe) : new HashSet<string>());

                List<JObject> orders = new List<JObject>();
                if (rh != null)
                {
                    var od = rh.GetOrders(sym + "-USD");
                    var res = od?["results"] as JArray;
                    if (res != null) orders = res.OfType<JObject>().Take(50).ToList();
                }

                var selected = ShowPicker(owner, sym, orders, preselect);
                if (selected == null) return false; // cancelled

                var ids = selected.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList();
                if (!existing.TryGetValue(sym, out var cur) || !cur.SequenceEqual(ids))
                {
                    existing[sym] = ids;
                    changed = true;
                }
            }

            if (changed) SaveBotIds(botIdsPath, existing);
            return true;
        }

        private static RobinhoodClient TryClient(string projectDir)
        {
            try
            {
                string kp = Path.Combine(projectDir, "r_key.txt");
                string sp = Path.Combine(projectDir, "r_secret.txt");
                if (!File.Exists(kp) || !File.Exists(sp)) return null;
                string key = (File.ReadAllText(kp) ?? "").Trim();
                string sec = (File.ReadAllText(sp) ?? "").Trim();
                if (key.Length == 0 || sec.Length == 0) return null;
                return new RobinhoodClient(key, sec);
            }
            catch { return null; }
        }

        private static HashSet<string> ShowPicker(IWin32Window owner, string coin, List<JObject> orders, HashSet<string> preselected)
        {
            using (var form = new Form())
            {
                form.Text = "Select bot orders - " + coin;
                form.Width = 900; form.Height = 560; form.StartPosition = FormStartPosition.CenterParent;

                var help = new Label
                {
                    Dock = DockStyle.Top, Height = 64, Padding = new Padding(8),
                    Text = "Select the BUY orders for this coin's current auto-trade only. Do NOT select sell orders.\n" +
                           "Anything not selected is treated as manual/long-term. If you select none, the bot ignores this coin's holdings.",
                };

                var list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
                var ids = new List<string>();
                foreach (var o in orders)
                {
                    string oid = (o["id"]?.ToString() ?? "").Trim();
                    string created = (o["created_at"]?.ToString() ?? "").Replace("T", " ").Replace("Z", "");
                    string side = (o["side"]?.ToString() ?? "").ToUpperInvariant();
                    string state = o["state"]?.ToString() ?? "";
                    double qty = 0; double? px = null;
                    if (o["executions"] is JArray ex)
                        foreach (var e in ex.OfType<JObject>())
                        {
                            qty += D(e, "quantity");
                            if (px == null) px = D(e, "effective_price");
                        }
                    string row = $"{created,-19}  {side,-4}  qty={qty:0.##########}  px={(px.HasValue ? px.Value.ToString(CultureInfo.InvariantCulture) : "-")}  state={state}  id={oid}";
                    int idx = list.Items.Add(row);
                    ids.Add(oid);
                    if (oid.Length > 0 && preselected.Contains(oid)) list.SetItemChecked(idx, true);
                }

                var btns = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(8) };
                var save = new Button { Text = "Save", AutoSize = true };
                var cancel = new Button { Text = "Cancel", AutoSize = true };
                var selAll = new Button { Text = "Select All", AutoSize = true };
                var selNone = new Button { Text = "Select None", AutoSize = true };
                HashSet<string> result = null;
                save.Click += (s, e) =>
                {
                    result = new HashSet<string>();
                    for (int i = 0; i < list.Items.Count; i++)
                        if (list.GetItemChecked(i) && ids[i].Length > 0) result.Add(ids[i]);
                    form.DialogResult = DialogResult.OK; form.Close();
                };
                cancel.Click += (s, e) => { result = null; form.DialogResult = DialogResult.Cancel; form.Close(); };
                selAll.Click += (s, e) => { for (int i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, true); };
                selNone.Click += (s, e) => { for (int i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, false); };
                btns.Controls.Add(save); btns.Controls.Add(cancel); btns.Controls.Add(selNone); btns.Controls.Add(selAll);

                form.Controls.Add(list);
                form.Controls.Add(btns);
                form.Controls.Add(help);
                Theme.Apply(form);

                form.ShowDialog(owner);
                return result;
            }
        }

        private static Dictionary<string, List<string>> LoadBotIds(string path)
        {
            var outMap = new Dictionary<string, List<string>>();
            try
            {
                if (!File.Exists(path)) return outMap;
                var data = JObject.Parse(File.ReadAllText(path));
                foreach (var p in data.Properties())
                {
                    string sym = p.Name.Trim().ToUpperInvariant();
                    if (sym.Length == 0) continue;
                    if (p.Value is JArray arr)
                        outMap[sym] = arr.Select(x => (x?.ToString() ?? "").Trim()).Where(x => x.Length > 0).ToList();
                }
            }
            catch { }
            return outMap;
        }

        private static void SaveBotIds(string path, Dictionary<string, List<string>> data)
        {
            try
            {
                var o = new JObject();
                foreach (var kv in data)
                {
                    if (kv.Value == null) continue;
                    o[kv.Key.Trim().ToUpperInvariant()] = new JArray(kv.Value.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct().OrderBy(x => x));
                }
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(o, Formatting.Indented));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch { }
        }

        private static Dictionary<string, HashSet<string>> FromTradeHistory(string path)
        {
            var outMap = new Dictionary<string, HashSet<string>>();
            try
            {
                if (!File.Exists(path)) return outMap;
                var lastSell = new Dictionary<string, double>();
                var rows = new List<JObject>();
                foreach (var line in File.ReadLines(path))
                {
                    string l = (line ?? "").Trim();
                    if (l.Length == 0) continue;
                    JObject o;
                    try { o = JObject.Parse(l); } catch { continue; }
                    string symFull = (o["symbol"]?.ToString() ?? "").Trim().ToUpperInvariant();
                    string bas = symFull.Length > 0 ? symFull.Split('-')[0] : "";
                    if (bas.Length == 0) continue;
                    rows.Add(o);
                    if ((o["side"]?.ToString() ?? "").ToLowerInvariant().Trim() != "sell") continue;
                    double ts = D(o, "ts");
                    if (!lastSell.ContainsKey(bas) || ts > lastSell[bas]) lastSell[bas] = ts;
                }
                foreach (var o in rows)
                {
                    string oid = (o["order_id"]?.ToString() ?? "").Trim();
                    if (oid.Length == 0) continue;
                    string symFull = (o["symbol"]?.ToString() ?? "").Trim().ToUpperInvariant();
                    string bas = symFull.Length > 0 ? symFull.Split('-')[0] : "";
                    if (bas.Length == 0) continue;
                    double ts = D(o, "ts");
                    if (ts > (lastSell.TryGetValue(bas, out var ls) ? ls : 0.0))
                    {
                        if (!outMap.TryGetValue(bas, out var set)) { set = new HashSet<string>(); outMap[bas] = set; }
                        set.Add(oid);
                    }
                }
            }
            catch { }
            return outMap;
        }

        private static double D(JObject o, string key)
        {
            var v = o?[key];
            if (v == null || v.Type == JTokenType.Null) return 0.0;
            return double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0.0;
        }
    }
}
