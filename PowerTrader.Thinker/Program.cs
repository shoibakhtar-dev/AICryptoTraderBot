using System;
using PowerTrader.Core.Config;

namespace PowerTrader.Thinker
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
            return ThinkerApp.Run(args);
        }
    }
}
