// 0600_주문번호_매핑.cs  (복붙용 / C# 7.3)  [A안: orderQty + cumFill 추적 + ✅OrdRegistered 이벤트]
// ------------------------------------------------------------
// 역할(확정):
// - 주문전송확인 성공(OrdNo 존재) 시:
//   OrdNo -> (sideKor, band, orderQty, cumFill=0, 등록시각) 저장
// - SC1 수신 시:
//   ordNo로 레코드 조회 후 cumFill 누적
//   -> partial / complete 판정에 필요한 정보 제공
//
// ✅ 이번 수정 핵심:
// - SC1이 Register보다 먼저 도착(모의서버 즉시체결)할 수 있으므로
//   Register 완료 시점을 알리는 OrdRegistered 이벤트를 추가한다.
//   -> 0650이 pending SC1을 flush 처리 가능
//
// 주의:
// - sideKor는 반드시 "매수"/"매도"로 저장/리턴한다.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace Exercise_1
{
    public sealed class _0600_주문번호_매핑
    {
        private readonly object _lock = new object();

        private readonly Dictionary<long, OrdState> _ordNoToState = new Dictionary<long, OrdState>();

        // ✅ Register 완료 이벤트 (ordNo)
        public event Action<long> OrdRegistered;

        private sealed class OrdState
        {
            public string SideKor;
            public int Band;
            public int OrderQty;
            public int CumFill;
            public DateTime CreatedAt;
            public bool CanceledConfirmed;
        }

        public void Register(string sideKor, int band, string ordNoRaw, int orderQty)
        {
            long ordNo = ParseOrdNo(ordNoRaw);
            Register(sideKor, band, ordNo, orderQty);
        }

        public void Register(string sideKor, int band, long ordNo, int orderQty)
        {
            if (ordNo <= 0) throw new ArgumentOutOfRangeException(nameof(ordNo));
            if (orderQty <= 0) throw new ArgumentOutOfRangeException(nameof(orderQty));
            if (string.IsNullOrWhiteSpace(sideKor)) throw new ArgumentNullException(nameof(sideKor));

            Action<long> ev = null;

            lock (_lock)
            {
                _ordNoToState[ordNo] = new OrdState
                {
                    SideKor = sideKor.Trim(),
                    Band = band,
                    OrderQty = orderQty,
                    CumFill = 0,
                    CreatedAt = DateTime.Now,
                    CanceledConfirmed = false
                };

                Console.WriteLine($"[0600][REG] ordNo={ordNo} side={sideKor} band={band} orderQty={orderQty}");
                Debug.WriteLine($"[0600][REG] ordNo={ordNo} side={sideKor} band={band} orderQty={orderQty}");

                ev = OrdRegistered; // lock 밖에서 호출
            }

            try
            {
                // ✅ Register 직후 알림(0650 pending flush용)
                ev?.Invoke(ordNo);
            }
            catch { }
        }

        public bool TryGetSideBand(long ordNo, out string sideKor, out int band)
        {
            lock (_lock)
            {
                if (_ordNoToState.TryGetValue(ordNo, out var st))
                {
                    sideKor = st.SideKor;
                    band = st.Band;
                    return true;
                }
            }

            sideKor = null;
            band = 0;
            return false;
        }

        public bool TryGetOrderQty(long ordNo, out int orderQty)
        {
            lock (_lock)
            {
                if (_ordNoToState.TryGetValue(ordNo, out var st))
                {
                    orderQty = st.OrderQty;
                    return true;
                }
            }
            orderQty = 0;
            return false;
        }

        /// <summary>
        /// SC1 체결 수량을 누적 반영
        /// </summary>
        public bool AddFill(long ordNo, int filledQty, out string sideKor, out int band, out int orderQty, out int cumFill, out int remain, out bool isComplete)
        {
            if (filledQty <= 0) throw new ArgumentOutOfRangeException(nameof(filledQty));

            lock (_lock)
            {
                if (!_ordNoToState.TryGetValue(ordNo, out var st))
                {
                    sideKor = null;
                    band = 0;
                    orderQty = 0;
                    cumFill = 0;
                    remain = 0;
                    isComplete = false;
                    return false;
                }

                st.CumFill += filledQty;

                // 안전: 과체결 방지(로그만)
                if (st.CumFill > st.OrderQty)
                {
                    Console.WriteLine($"[0600][WARN] cumFill > orderQty ordNo={ordNo} cumFill={st.CumFill} orderQty={st.OrderQty} (clamp)");
                    st.CumFill = st.OrderQty;
                }

                sideKor = st.SideKor;
                band = st.Band;
                orderQty = st.OrderQty;
                cumFill = st.CumFill;
                remain = st.OrderQty - st.CumFill;
                isComplete = (remain == 0);

                Console.WriteLine($"[0600][FILL] ordNo={ordNo} +{filledQty} -> cumFill={cumFill}/{orderQty} remain={remain} complete={isComplete}");
                return true;
            }
        }

        /// <summary>
        /// 전량취소 확정 표시(취소모듈/T0425가 호출)
        /// </summary>
        public void MarkCancelConfirmed(long ordNo)
        {
            lock (_lock)
            {
                if (_ordNoToState.TryGetValue(ordNo, out var st))
                {
                    st.CanceledConfirmed = true;
                    Console.WriteLine($"[0600][CANCEL.CONFIRMED] ordNo={ordNo}");
                }
            }
        }

        public static long ParseOrdNo(string ordNoRaw)
        {
            ordNoRaw = (ordNoRaw ?? "").Trim();
            if (string.IsNullOrWhiteSpace(ordNoRaw))
                throw new Exception("OrdNo is empty.");

            if (!long.TryParse(ordNoRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ordNo))
                throw new Exception($"OrdNo parse fail raw='{ordNoRaw}'");

            if (ordNo <= 0) throw new Exception($"OrdNo invalid ordNo={ordNo}");
            return ordNo;
        }

        // 기존 코드가 Register(side, band, ordNoRaw)로 부르는 경우 호환용
        public void Register(string sideKor, int band, string ordNoRaw)
        {
            // 주문수량은 0550(잠금)에 들어있는 값으로 보완
            int qty = 0;
            try { qty = Login.TradeWait != null ? Login.TradeWait.LockedOrderQty : 0; } catch { qty = 0; }

            long ordNo = ParseOrdNo(ordNoRaw);

            if (qty <= 0)
            {
                // qty가 없으면 완전체결 판정이 불가능하므로 경고만 남김
                Console.WriteLine($"[0600][WARN] Register(3args) qty unknown ordNo={ordNo} -> A안 complete 판단 불가");
            }

            Register(sideKor, band, ordNo, (qty > 0 ? qty : 1)); // 최소 1로라도 저장(죽는 것 방지)
        }

        // 2026-02-09 48273
    }
}
// 2026-02-09 61504
