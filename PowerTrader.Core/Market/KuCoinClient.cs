using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json.Linq;

namespace PowerTrader.Core.Market
{
    public sealed class KuCoinException : Exception
    {
        public KuCoinException(string message) : base(message) { }
        public KuCoinException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Minimal KuCoin spot market-data client, equivalent to the parts of
    /// kucoin.client.Market used by the Python engine (get_kline, get_ticker).
    /// Candle rows are returned exactly as KuCoin returns them (newest-first),
    /// each row a 7-field string array [time, open, close, high, low, volume, turnover].
    /// </summary>
    public sealed class KuCoinClient
    {
        private const string BaseUrl = "https://api.kucoin.com";
        private static readonly HttpClient Http = CreateHttp();

        private static HttpClient CreateHttp()
        {
            PowerTrader.Core.Util.NetInit.EnsureTls();
            var h = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            h.DefaultRequestHeaders.Add("User-Agent", "PowerTraderAI/1.0");
            return h;
        }

        /// <summary>get_kline(symbol, type) — most recent candles first.</summary>
        public List<string[]> GetKline(string symbol, string type)
        {
            return GetKline(symbol, type, null, null);
        }

        /// <summary>get_kline(symbol, type, startAt, endAt) with unix-second bounds (inclusive as KuCoin defines).</summary>
        public List<string[]> GetKline(string symbol, string type, long? startAt, long? endAt)
        {
            var sb = new StringBuilder();
            sb.Append("/api/v1/market/candles?type=").Append(Uri.EscapeDataString(type))
              .Append("&symbol=").Append(Uri.EscapeDataString(symbol));
            if (startAt.HasValue) sb.Append("&startAt=").Append(startAt.Value);
            if (endAt.HasValue) sb.Append("&endAt=").Append(endAt.Value);

            JObject root = GetJson(sb.ToString());
            var data = root["data"] as JArray;
            var outList = new List<string[]>();
            if (data == null) return outList;

            foreach (var row in data)
            {
                if (row is JArray fields)
                {
                    var arr = new string[fields.Count];
                    for (int i = 0; i < fields.Count; i++) arr[i] = fields[i]?.ToString() ?? string.Empty;
                    outList.Add(arr);
                }
            }
            return outList;
        }

        /// <summary>get_ticker(symbol) level1 — returns the last trade price.</summary>
        public double GetTickerPrice(string symbol)
        {
            JObject root = GetJson("/api/v1/market/orderbook/level1?symbol=" + Uri.EscapeDataString(symbol));
            var data = root["data"] as JObject;
            if (data == null || data["price"] == null)
                throw new KuCoinException("KuCoin ticker returned no price for " + symbol);
            string p = data["price"].ToString();
            if (!double.TryParse(p, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double price))
                throw new KuCoinException("KuCoin ticker price unparsable for " + symbol + ": " + p);
            return price;
        }

        private JObject GetJson(string path)
        {
            try
            {
                using (var resp = Http.GetAsync(BaseUrl + path).GetAwaiter().GetResult())
                {
                    string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (!resp.IsSuccessStatusCode)
                        throw new KuCoinException("KuCoin HTTP " + (int)resp.StatusCode + ": " + Trunc(body));

                    var root = JObject.Parse(body);
                    string code = root["code"]?.ToString();
                    if (code != null && code != "200000")
                        throw new KuCoinException("KuCoin API code " + code + ": " + Trunc(body));
                    return root;
                }
            }
            catch (KuCoinException) { throw; }
            catch (Exception ex)
            {
                throw new KuCoinException("KuCoin request failed: " + ex.Message, ex);
            }
        }

        private static string Trunc(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Length <= 300 ? s : s.Substring(0, 300);
        }
    }
}
