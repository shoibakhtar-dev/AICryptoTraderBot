namespace PowerTrader.Trainer
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            // Force stdout to flush each line even when redirected to a pipe/file.
            var stdout = new System.IO.StreamWriter(System.Console.OpenStandardOutput()) { AutoFlush = true };
            System.Console.SetOut(stdout);

            if (args != null && args.Length > 0 && args[0] == "probe")
            {
                try
                {
                    var m = new PowerTrader.Core.Market.KuCoinClient();
                    var rows = m.GetKline("BTC-USDT", "1hour");
                    System.Console.WriteLine("PROBE OK: fetched " + rows.Count + " candles; first ts=" + (rows.Count > 0 ? rows[0][0] : "-"));
                    return 0;
                }
                catch (System.Exception ex)
                {
                    System.Console.WriteLine("PROBE FAIL: " + ex.GetType().Name + ": " + ex.Message);
                    return 2;
                }
            }

            return TrainerApp.Run(args);
        }
    }
}
