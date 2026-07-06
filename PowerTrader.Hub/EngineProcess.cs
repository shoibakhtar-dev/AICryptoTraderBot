using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace PowerTrader.Hub
{
    /// <summary>
    /// Wraps a launched engine executable (thinker/trader/trainer) with bounded stdout capture.
    /// Mirrors pt_hub.py's _start_process/_reader_thread + LogProc.
    /// </summary>
    internal sealed class EngineProcess
    {
        private Process _proc;
        private readonly ConcurrentQueue<string> _log = new ConcurrentQueue<string>();
        private const int MaxLines = 4000;

        public string Name { get; }
        public string ExePath { get; }
        public string Coin { get; }

        public EngineProcess(string name, string exePath, string coin = null)
        {
            Name = name; ExePath = exePath; Coin = coin;
        }

        public bool IsRunning
        {
            get { try { return _proc != null && !_proc.HasExited; } catch { return false; } }
        }

        public void Start(string arguments, string workingDir, string hubDir, string prefix)
        {
            if (IsRunning) return;
            if (!File.Exists(ExePath))
            {
                Push(prefix + "[missing executable: " + ExePath + "]");
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = ExePath,
                Arguments = arguments ?? "",
                WorkingDirectory = Directory.Exists(workingDir) ? workingDir : Path.GetDirectoryName(ExePath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.EnvironmentVariables["POWERTRADER_HUB_DIR"] = hubDir;

            try
            {
                _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _proc.OutputDataReceived += (s, e) => { if (e.Data != null) Push(prefix + e.Data); };
                _proc.ErrorDataReceived += (s, e) => { if (e.Data != null) Push(prefix + e.Data); };
                _proc.Exited += (s, e) => Push(prefix + "[process exited]");
                _proc.Start();
                _proc.BeginOutputReadLine();
                _proc.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                Push(prefix + "[failed to start: " + ex.Message + "]");
            }
        }

        public void Stop()
        {
            try { if (_proc != null && !_proc.HasExited) _proc.Kill(); }
            catch { }
        }

        private void Push(string line)
        {
            _log.Enqueue(line);
            while (_log.Count > MaxLines && _log.TryDequeue(out _)) { }
        }

        /// <summary>Drain new lines into a single string (caller appends to its text box).</summary>
        public string DrainNew()
        {
            if (_log.IsEmpty) return null;
            var sb = new System.Text.StringBuilder();
            while (_log.TryDequeue(out var line)) sb.AppendLine(line);
            return sb.Length > 0 ? sb.ToString() : null;
        }
    }

    internal static class EngineLocator
    {
        /// <summary>
        /// Resolve an engine exe by assembly name (pt_thinker/pt_trader/pt_trainer). Looks next to
        /// the Hub exe first, then common dev output locations.
        /// </summary>
        public static string Resolve(string exeName)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string local = Path.Combine(baseDir, exeName);
            if (File.Exists(local)) return local;

            // dev fallback: ..\..\..\PowerTrader.<Proj>\bin\<cfg>\net48\<exe>
            string project = null;
            if (exeName.IndexOf("thinker", StringComparison.OrdinalIgnoreCase) >= 0) project = "PowerTrader.Thinker";
            else if (exeName.IndexOf("trader", StringComparison.OrdinalIgnoreCase) >= 0) project = "PowerTrader.Trader";
            else if (exeName.IndexOf("trainer", StringComparison.OrdinalIgnoreCase) >= 0) project = "PowerTrader.Trainer";

            if (project != null)
            {
                try
                {
                    var solutionDir = new DirectoryInfo(baseDir).Parent?.Parent?.Parent?.Parent; // net48\bin\<proj>\csharp
                    if (solutionDir != null)
                    {
                        foreach (var cfg in new[] { "Debug", "Release" })
                        {
                            string cand = Path.Combine(solutionDir.FullName, project, "bin", cfg, "net48", exeName);
                            if (File.Exists(cand)) return cand;
                        }
                    }
                }
                catch { }
            }
            return local; // return the expected local path even if missing (error surfaces on Start)
        }
    }
}
