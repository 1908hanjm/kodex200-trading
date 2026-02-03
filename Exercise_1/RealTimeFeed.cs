using System;
using System.Globalization;
using System.IO;          // File.Exists
using XA_DATASETLib;     // XING API

namespace Exercise_1
{
    public class RealTimeFeed : IMarketFeed
    {
        public event Action<Tick> OnTick;

        private readonly XARealClass _real = new XARealClass();
        private string _symbol = "";
        private bool _running;

        public RealTimeFeed()
        {
            _real.ReceiveRealData += HandleReal;
        }

        public void Start(string symbol)
        {
            if (_running) return;
            _symbol = symbol ?? "";

            // S3 RES 경로
            string res1 = @"C:\LS_SEC\xingAPI\Res\S3_.res";
            string res2 = @"C:\LS_SEC\xingAPI\Res\S3__1.res";
            string res = File.Exists(res1) ? res1 : res2;

            _real.LoadFromResFile(res);
            _real.SetFieldData("InBlock", "shcode", _symbol);
            _real.AdviseRealData();
            _running = true;
        }

        public void Stop()
        {
            if (!_running) return;
            try { _real.UnadviseRealData(); } catch { }
            _running = false;
        }

        private void HandleReal(string trCode)
        {
            try
            {
                string hotime = _real.GetFieldData("OutBlock", "hotime")?.Trim(); // HHmmss[fff]
                string priceS = FirstNonEmpty(
                    _real.GetFieldData("OutBlock", "price"),
                    _real.GetFieldData("OutBlock", "cprice"),
                    _real.GetFieldData("OutBlock", "price2"),
                    _real.GetFieldData("OutBlock", "curprc"),
                    _real.GetFieldData("OutBlock", "close"),
                    _real.GetFieldData("OutBlock", "dancheg"),
                    _real.GetFieldData("OutBlock", "dche")
                );
                if (string.IsNullOrWhiteSpace(priceS)) return;

                string cvolS = FirstNonEmpty(_real.GetFieldData("OutBlock", "cvolume"),
                                             _real.GetFieldData("OutBlock", "chevol"));
                string bidS = FirstNonEmpty(_real.GetFieldData("OutBlock", "bidho"),
                                             _real.GetFieldData("OutBlock", "bid"));
                string askS = FirstNonEmpty(_real.GetFieldData("OutBlock", "offerho"),
                                             _real.GetFieldData("OutBlock", "offer"));

                var tick = new Tick
                {
                    Symbol = _symbol,
                    TsKst = BuildTsFromHotimeKst(hotime),
                    Price = ParseDouble(priceS),
                    CVol = ParseLong(cvolS),
                    Bid1 = ParseDouble(bidS),
                    Ask1 = ParseDouble(askS)
                };

                OnTick?.Invoke(tick);
            }
            catch
            {
                // 필요시 로깅
            }
        }

        // ---- helpers ----
        private static string FirstNonEmpty(params string[] xs)
        {
            if (xs == null) return null;
            foreach (var x in xs) if (!string.IsNullOrWhiteSpace(x)) return x.Trim();
            return null;
        }
        private static double ParseDouble(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return double.NaN;
            s = s.Replace(",", "").Replace("+", "").Trim();
            return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
        }
        private static long ParseLong(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            s = s.Replace(",", "").Trim();
            return long.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }
        private static int SafeInt(string s) => int.TryParse(s, out var v) ? v : 0;

        private static DateTimeOffset BuildTsFromHotimeKst(string hotime)
        {
            // KST 기준 타임스탬프
            var tz = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
            var nowKst = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
            var today = new DateTime(nowKst.Year, nowKst.Month, nowKst.Day);

            if (string.IsNullOrWhiteSpace(hotime) || hotime.Length < 6)
                return nowKst;

            int hh = SafeInt(hotime.Substring(0, 2));
            int mm = SafeInt(hotime.Substring(2, 2));
            int ss = SafeInt(hotime.Substring(4, 2));
            int fff = (hotime.Length >= 9) ? SafeInt(hotime.Substring(6, 3)) : 0;

            var baseKst = new DateTimeOffset(today.Year, today.Month, today.Day, hh, mm, ss, TimeSpan.FromHours(9));
            return baseKst.AddMilliseconds(fff);
        }
    }
}
