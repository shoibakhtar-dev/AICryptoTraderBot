using Newtonsoft.Json.Linq;

namespace PowerTrader.Core.Util
{
    /// <summary>Small helpers for lenient navigation of Newtonsoft JToken API payloads
    /// (mirrors Python's dict.get(...) with float()/str() coercion).</summary>
    public static class Js
    {
        public static JToken Get(JToken tok, string key)
        {
            if (tok is JObject o) return o[key];
            return null;
        }

        public static string Str(JToken tok, string key, string fallback = null)
        {
            var v = Get(tok, key);
            if (v == null || v.Type == JTokenType.Null) return fallback;
            return v.ToString();
        }

        public static string AsStr(JToken tok, string fallback = null)
        {
            if (tok == null || tok.Type == JTokenType.Null) return fallback;
            return tok.ToString();
        }

        public static double Double(JToken tok, string key, double fallback = 0.0)
        {
            var v = Get(tok, key);
            if (v == null || v.Type == JTokenType.Null) return fallback;
            return PyCompat.ToDouble(v.ToString(), fallback);
        }

        public static double AsDouble(JToken tok, double fallback = 0.0)
        {
            if (tok == null || tok.Type == JTokenType.Null) return fallback;
            return PyCompat.ToDouble(tok.ToString(), fallback);
        }

        public static bool Has(JToken tok, string key)
        {
            return tok is JObject o && o[key] != null;
        }

        public static JArray Array(JToken tok, string key)
        {
            return Get(tok, key) as JArray;
        }
    }
}
