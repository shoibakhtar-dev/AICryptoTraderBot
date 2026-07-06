using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PowerTrader.Core.Util
{
    /// <summary>
    /// File persistence helpers mirroring the Python scripts' JSON/JSONL/text IO,
    /// including atomic writes (.tmp + replace) and best-effort backups (.bak).
    /// All failures are swallowed to match the original best-effort behavior.
    /// </summary>
    public static class JsonStore
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        public static string ReadText(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return File.ReadAllText(path, Encoding.UTF8);
            }
            catch { return null; }
        }

        public static void WriteText(string path, string content)
        {
            try { File.WriteAllText(path, content ?? string.Empty, Utf8NoBom); }
            catch { /* best-effort */ }
        }

        /// <summary>Read a JSON object; returns null on any failure or non-object payload.</summary>
        public static JObject ReadJObject(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string raw = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(raw)) return null;
                var tok = JToken.Parse(raw);
                return tok as JObject;
            }
            catch { return null; }
        }

        public static JToken ReadJToken(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string raw = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(raw)) return null;
                return JToken.Parse(raw);
            }
            catch { return null; }
        }

        /// <summary>Atomic write with indent=2 semantics (matches _atomic_write_json).</summary>
        public static void AtomicWriteJson(string path, object data, bool indent = true)
        {
            try
            {
                string tmp = path + ".tmp";
                string json = JsonConvert.SerializeObject(data, indent ? Formatting.Indented : Formatting.None);
                File.WriteAllText(tmp, json, Utf8NoBom);
                if (File.Exists(path))
                {
                    try { File.Replace(tmp, path, null); return; }
                    catch { /* fall through to delete+move */ }
                    try { File.Delete(path); } catch { }
                }
                File.Move(tmp, path);
            }
            catch { /* best-effort: never delete the old file */ }
        }

        /// <summary>
        /// Safer persistence with a .bak backup before replace (mirrors the trader's
        /// _atomic_write_json for pnl_ledger etc.).
        /// </summary>
        public static void AtomicWriteJsonWithBackup(string path, object data)
        {
            try
            {
                string tmp = path + ".tmp";
                string bak = path + ".bak";

                string json = JsonConvert.SerializeObject(data, Formatting.Indented);
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var sw = new StreamWriter(fs, Utf8NoBom))
                {
                    sw.Write(json);
                    sw.Flush();
                    fs.Flush(true);
                }

                try
                {
                    if (File.Exists(path) && new FileInfo(path).Length > 0)
                        File.Copy(path, bak, true);
                }
                catch { }

                if (File.Exists(path))
                {
                    try { File.Replace(tmp, path, null); return; }
                    catch { }
                    try { File.Delete(path); } catch { }
                }
                File.Move(tmp, path);
            }
            catch { }
        }

        public static void AppendJsonl(string path, object obj)
        {
            try
            {
                string line = JsonConvert.SerializeObject(obj, Formatting.None) + "\n";
                File.AppendAllText(path, line, Utf8NoBom);
            }
            catch { }
        }

        public static void EnsureDir(string path)
        {
            try { if (!string.IsNullOrEmpty(path)) Directory.CreateDirectory(path); }
            catch { }
        }
    }
}
