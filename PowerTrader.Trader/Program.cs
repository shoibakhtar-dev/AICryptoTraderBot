using System;
using System.IO;
using PowerTrader.Core.Config;

namespace PowerTrader.Trader
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            // Flush each line so the Hub's live log capture stays current.
            var stdout = new System.IO.StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(stdout);

            // Engine runs with its working directory = the main neural folder (the Hub sets this).
            AppPaths.BaseDir = Environment.CurrentDirectory;

            string apiKey = ReadTrim("r_key.txt");
            string priv = ReadTrim("r_secret.txt");
            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(priv))
            {
                Console.WriteLine();
                Console.WriteLine("[PowerTrader] Robinhood API credentials not found.");
                Console.WriteLine("Open the Hub and go to Settings -> Robinhood API -> Setup / Update.");
                Console.WriteLine("That wizard generates your keypair and saves r_key.txt + r_secret.txt so this trader can authenticate.");
                Console.WriteLine();
                return 1;
            }

            try
            {
                var bot = new CryptoApiTrading(apiKey, priv);
                bot.Run();
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return 1;
            }
        }

        private static string ReadTrim(string path)
        {
            try { return File.Exists(path) ? (File.ReadAllText(path) ?? "").Trim() : ""; }
            catch { return ""; }
        }
    }
}
