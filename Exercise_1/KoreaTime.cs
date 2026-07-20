using System;
using System.Globalization;

namespace Exercise_1
{
    public static class KoreaTime
    {
        public static DateTime NowKst()
        {
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            }
            catch
            {
                return DateTime.UtcNow.AddHours(9);
            }
        }

        public static string Timestamp()
        {
            return NowKst().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }

        public static string TimeOnly()
        {
            return NowKst().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }

        public static string DateForFile()
        {
            return NowKst().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        public static string TimeForFile()
        {
            return NowKst().ToString("HHmmss", CultureInfo.InvariantCulture);
        }

        public static string KoreanDayOfWeek()
        {
            switch (NowKst().DayOfWeek)
            {
                case DayOfWeek.Monday: return "월";
                case DayOfWeek.Tuesday: return "화";
                case DayOfWeek.Wednesday: return "수";
                case DayOfWeek.Thursday: return "목";
                case DayOfWeek.Friday: return "금";
                case DayOfWeek.Saturday: return "토";
                case DayOfWeek.Sunday: return "일";
                default: return "";
            }
        }
    }
}
