using System;

namespace PowerTrader.Core.Util
{
    public static class TimeUtil
    {
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>time.time() -> fractional seconds since epoch.</summary>
        public static double UnixNow()
        {
            return (DateTime.UtcNow - Epoch).TotalSeconds;
        }

        /// <summary>int(time.time()) -> whole seconds since epoch.</summary>
        public static long UnixNowSeconds()
        {
            return (long)(DateTime.UtcNow - Epoch).TotalSeconds;
        }

        public static DateTime FromUnix(double seconds)
        {
            return Epoch.AddSeconds(seconds);
        }
    }
}
