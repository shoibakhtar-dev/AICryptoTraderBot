using System.Net;

namespace PowerTrader.Core.Util
{
    /// <summary>
    /// One-time network setup. .NET Framework 4.8 does not always negotiate TLS 1.2+ by default,
    /// but KuCoin and Robinhood require it — without this, every HTTPS request fails the handshake.
    /// </summary>
    public static class NetInit
    {
        private static bool _done;
        private static readonly object Gate = new object();

        public static void EnsureTls()
        {
            if (_done) return;
            lock (Gate)
            {
                if (_done) return;
                try
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    // Tls13 (value 12288) exists on 4.8; enable if the OS supports it.
                    try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch { }
                    ServicePointManager.DefaultConnectionLimit = 20;
                }
                catch { }
                _done = true;
            }
        }
    }
}
