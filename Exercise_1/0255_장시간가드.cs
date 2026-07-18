using System;

namespace Exercise_1
{
    public static class MarketTimeGuard
    {
        private static readonly TimeSpan RegularStart = new TimeSpan(9, 0, 0);
        private static readonly TimeSpan RegularEnd = new TimeSpan(15, 30, 0);
        private static volatile bool _lastNowKstUsedFallback;

        public static bool LastNowKstUsedFallback
        {
            get { return _lastNowKstUsedFallback; }
        }

        public static DateTime NowKst()
        {
            _lastNowKstUsedFallback = false;

            try
            {
                TimeZoneInfo kst = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, kst);
            }
            catch
            {
                _lastNowKstUsedFallback = true;
                return DateTime.UtcNow.AddHours(9);
            }
        }

        public static bool IsRegularOrderTime(DateTime? nowKst = null)
        {
            DateTime now = nowKst ?? NowKst();
            TimeSpan time = now.TimeOfDay;
            return time >= RegularStart && time < RegularEnd;
        }

        public static string GetRegularOrderWindowText()
        {
            return "09:00:00~15:30:00";
        }

        public static string GetBlockReason(DateTime? nowKst = null)
        {
            return IsRegularOrderTime(nowKst) ? "" : "OUT_OF_REGULAR_ORDER_TIME";
        }
    }
}
