using System;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json.Linq;
using PowerTrader.Core.Util;

namespace PowerTrader.Core.Robinhood
{
    /// <summary>
    /// Robinhood Crypto REST client. Reproduces pt_trader.py's CryptoAPITrading transport:
    ///  - Ed25519 request signing over "{api_key}{timestamp}{path}{method}{body}"
    ///  - make_api_request semantics: 2xx -> parsed JSON; 4xx/5xx -> error JSON (for "errors"
    ///    inspection); network/parse failure -> null.
    /// Also exposes the thinker's throwing get_current_ask.
    /// </summary>
    public sealed class RobinhoodClient
    {
        private const string DefaultBaseUrl = "https://trading.robinhood.com";
        private static readonly HttpClient Http = CreateHttp();

        private static HttpClient CreateHttp()
        {
            PowerTrader.Core.Util.NetInit.EnsureTls();
            return new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        }

        private readonly string _apiKey;
        private readonly byte[] _privateSeed;
        private readonly string _baseUrl;

        public RobinhoodClient(string apiKey, string base64PrivateSeed, string baseUrl = DefaultBaseUrl)
        {
            _apiKey = (apiKey ?? string.Empty).Trim();
            if (_apiKey.Length == 0)
                throw new InvalidOperationException("Robinhood API key is empty (r_key.txt).");

            try
            {
                _privateSeed = Convert.FromBase64String((base64PrivateSeed ?? string.Empty).Trim());
            }
            catch (Exception e)
            {
                throw new InvalidOperationException("Failed to decode Robinhood private key (r_secret.txt): " + e.Message);
            }
            if (_privateSeed.Length != 32)
                throw new InvalidOperationException("Robinhood private key must decode to a 32-byte Ed25519 seed.");

            _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        }

        private long CurrentTimestamp() => TimeUtil.UnixNowSeconds();

        private void AddAuthHeaders(HttpRequestMessage req, string method, string path, string body, long ts)
        {
            string message = _apiKey + ts.ToString(System.Globalization.CultureInfo.InvariantCulture) + path + method + (body ?? string.Empty);
            string sig = Ed25519Util.SignBase64(_privateSeed, message);
            req.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
            req.Headers.TryAddWithoutValidation("x-signature", sig);
            req.Headers.TryAddWithoutValidation("x-timestamp", ts.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// make_api_request(method, path, body). Returns JToken (object/array) or null.
        /// GET ignores body. POST sends the exact <paramref name="body"/> string that was signed.
        /// </summary>
        public JToken Request(string method, string path, string body = "")
        {
            method = (method ?? "GET").ToUpperInvariant();
            long ts = CurrentTimestamp();
            string url = _baseUrl + path;

            try
            {
                using (var req = new HttpRequestMessage(new HttpMethod(method), url))
                {
                    if (method == "POST")
                    {
                        req.Content = new StringContent(body ?? string.Empty, Encoding.UTF8, "application/json");
                    }
                    AddAuthHeaders(req, method, path, body, ts);

                    using (var resp = Http.SendAsync(req).GetAwaiter().GetResult())
                    {
                        string respBody = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        if (resp.IsSuccessStatusCode)
                        {
                            try { return JToken.Parse(respBody); }
                            catch { return null; }
                        }
                        // HTTP error -> return the error JSON so callers can inspect "errors".
                        try { return JToken.Parse(respBody); }
                        catch { return null; }
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        public JObject RequestObject(string method, string path, string body = "")
        {
            return Request(method, path, body) as JObject;
        }

        // ---- Convenience endpoints (mirror the Python method names) ----

        public JObject GetAccount() => RequestObject("GET", "/api/v1/crypto/trading/accounts/");

        public JObject GetHoldings() => RequestObject("GET", "/api/v1/crypto/trading/holdings/");

        public JObject GetTradingPairs() => RequestObject("GET", "/api/v1/crypto/trading/trading_pairs/");

        public JToken GetBestBidAsk(string symbol)
        {
            return Request("GET", "/api/v1/crypto/marketdata/best_bid_ask/?symbol=" + symbol);
        }

        /// <summary>
        /// Throwing variant used by the thinker: returns ask_inclusive_of_buy_spread or throws.
        /// The thinker loops until this succeeds.
        /// </summary>
        public double GetCurrentAsk(string symbol)
        {
            symbol = (symbol ?? string.Empty).Trim().ToUpperInvariant();
            var data = Request("GET", "/api/v1/crypto/marketdata/best_bid_ask/?symbol=" + symbol) as JObject;
            var results = data?["results"] as JArray;
            if (results == null || results.Count == 0)
                throw new InvalidOperationException("Robinhood best_bid_ask returned no results for " + symbol);
            var result = results[0] as JObject;
            var ask = result?["ask_inclusive_of_buy_spread"];
            if (ask == null)
                throw new InvalidOperationException("Robinhood best_bid_ask missing ask for " + symbol);
            return PyCompat.ToDouble(ask.ToString());
        }

        /// <summary>
        /// get_orders with pagination following (mirrors get_orders(max_pages=25)).
        /// Returns a single object with an aggregated "results" array and next=null.
        /// </summary>
        public JObject GetOrders(string symbol, int maxPages = 25)
        {
            string path = "/api/v1/crypto/trading/orders/?symbol=" + symbol;
            var first = RequestObject("GET", path);
            if (first == null) return null;

            var results = new JArray();
            if (first["results"] is JArray fr) foreach (var r in fr) results.Add(r);

            string nextUrl = first["next"]?.Type == JTokenType.String ? (string)first["next"] : null;
            int pages = 1;
            while (!string.IsNullOrEmpty(nextUrl) && pages < maxPages)
            {
                string nxt = nextUrl.Trim();
                string nxtPath;
                if (nxt.StartsWith(_baseUrl)) nxtPath = nxt.Substring(_baseUrl.Length);
                else if (nxt.StartsWith("/")) nxtPath = nxt;
                else if (nxt.Contains("://"))
                {
                    try { nxtPath = "/" + nxt.Split(new[] { "://" }, StringSplitOptions.None)[1].Split(new[] { '/' }, 2)[1]; }
                    catch { break; }
                }
                else nxtPath = "/" + nxt;

                var resp = RequestObject("GET", nxtPath);
                if (resp == null) break;
                if (resp["results"] is JArray rr) foreach (var r in rr) results.Add(r);
                nextUrl = resp["next"]?.Type == JTokenType.String ? (string)resp["next"] : null;
                pages++;
            }

            var outObj = (JObject)first.DeepClone();
            outObj["results"] = results;
            outObj["next"] = JValue.CreateNull();
            return outObj;
        }

        public JToken PlaceOrder(string bodyJson)
        {
            return Request("POST", "/api/v1/crypto/trading/orders/", bodyJson);
        }
    }
}
