
////////////////////////////////////////////////////////////////////////////////////////////////////////////////
//               for  Real
////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// 0600_주문번호_매핑.cs  (복붙용 / C# 7.3)
// ------------------------------------------------------------
// 역할:
// - 주문전송확인 성공(OrdNo 존재) 시:
//   OrdNo -> (side, band, 등록시각) 매핑 저장
// - SC1 수신 시:
//   OrdNo로 side/band 조회
//
// 주의:
// - side는 반드시 "매수"/"매도"로 저장/리턴한다.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace Exercise_1
{
    public sealed class _0600_주문번호_매핑
    {
        private readonly object _lock = new object();

        // ordNo -> map
        private readonly Dictionary<long, OrdMap> _ordNoToMap
            = new Dictionary<long, OrdMap>();

        /// <summary>
        /// 주문전송확인 성공 시 호출 (ordNoRaw는 trim/parse 후 저장)
        /// </summary>
        public void Register(string side, int band, string ordNoRaw)
        {
            long ordNo = ParseOrdNo(ordNoRaw);
            Register(side, band, ordNo);
        }

        /// <summary>
        /// 주문전송확인 성공 시 호출
        /// </summary>
        public void Register(string side, int band, long ordNo)
        {
            side = NormalizeSideKorOnly(side);
            if (band <= 0) throw new ArgumentOutOfRangeException(nameof(band));
            if (ordNo <= 0) throw new ArgumentOutOfRangeException(nameof(ordNo), $"ordNo must be > 0. ordNo={ordNo}");

            lock (_lock)
            {
                _ordNoToMap[ordNo] = new OrdMap
                {
                    Side = side,
                    Band = band,
                    MappedAt = DateTime.Now
                };
            }

            Debug.WriteLine($"[0600] REGISTER ordNo={ordNo} -> side={side}, band={band}");
        }

        /// <summary>
        /// SC1 수신 시 ordNo로 조회
        /// </summary>
        public bool TryGet(long ordNo, out string side, out int band)
        {
            lock (_lock)
            {
                OrdMap m;
                if (_ordNoToMap.TryGetValue(ordNo, out m))
                {
                    side = m.Side;
                    band = m.Band;
                    return true;
                }
            }

            side = "";
            band = -1;
            return false;
        }

        /// <summary>
        /// (선택) 매핑 제거 (전량 체결 처리 완료 후 호출 가능)
        /// </summary>
        public bool Remove(long ordNo)
        {
            lock (_lock)
            {
                return _ordNoToMap.Remove(ordNo);
            }
        }

        /// <summary>
        /// 디버그 덤프
        /// </summary>
        public string Dump()
        {
            lock (_lock)
            {
                var lines = new List<string>();
                lines.Add($"[0600] ordMapCount={_ordNoToMap.Count}");

                foreach (var kv in _ordNoToMap.OrderBy(x => x.Key))
                {
                    lines.Add($"  ordNo={kv.Key} side={kv.Value.Side} band={kv.Value.Band} at={kv.Value.MappedAt:HH:mm:ss}");
                }
                return string.Join(Environment.NewLine, lines);
            }
        }

        private static long ParseOrdNo(string ordNoRaw)
        {
            if (string.IsNullOrWhiteSpace(ordNoRaw)) return 0;
            ordNoRaw = ordNoRaw.Trim();

            long x;
            if (long.TryParse(ordNoRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out x))
                return x;

            return 0;
        }

        private static string NormalizeSideKorOnly(string side)
        {
            side = (side ?? "").Trim();
            if (side == "매수" || side == "매도") return side;
            throw new ArgumentException($"side는 반드시 '매수' 또는 '매도'여야 합니다. side='{side}'");
        }

        private struct OrdMap
        {
            public string Side;
            public int Band;
            public DateTime MappedAt;
        }
    }
}
//2026-01-17-00-00-00
