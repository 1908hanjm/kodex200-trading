// 0650_SC1_수신처리.cs  (복붙용 / C# 7.3)  [A안: 부분체결이면 잠금 유지 + ✅pending flush로 레이스 해결]
// ------------------------------------------------------------
// ✅ 이번 수정 핵심(확정):
// - SC1이 Register(0600)보다 먼저 들어오는 레이스가 발생한다(모의서버 즉시체결)
// - 기존: ordNo mapping not found -> skip  ❌ => 체결 누락(HTS와 불일치)
// - 수정: mapping 없으면 pending 저장 ✅
// - 0600.Register 완료(OrdRegistered 이벤트) 시 즉시 pending flush ✅
//
// 로그(요청 포맷 유지):
// [0650][SC1] ... (partial...)
// [0650] COMPLETE band=43 -> UNLOCK -> NEXT
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using XA_DATASETLib;

namespace Exercise_1
{
    public sealed class _0650_SC1_수신처리 : IDisposable
    {
        private XARealClass _real;
        private bool _started;

        private readonly object _lock = new object();
        private readonly HashSet<long> _execNoSeen = new HashSet<long>();
        private const int EXECNO_CACHE_LIMIT = 5000;

        // ✅ pending: ordNo -> fills
        private readonly Dictionary<long, List<PendingFill>> _pendingByOrdNo = new Dictionary<long, List<PendingFill>>();

        private sealed class PendingFill
        {
            public long OrdNo;
            public long ExecNo;
            public int FilledQty;
            public double Price;
            public DateTime ArrivedAt;
        }

        private _0600_주문번호_매핑 _subscribedMap;

        public event Action<string, int, int, double, long> Filled; // sideKor, band, deltaQty, price, execNo

        public void Start(XARealClass realSC1)
        {
            if (realSC1 == null) throw new ArgumentNullException(nameof(realSC1));

            lock (_lock)
            {
                if (_started)
                {
                    Console.WriteLine("[0650] Start called but already started -> skip");
                    return;
                }

                _real = realSC1;

                _real.ReceiveRealData -= OnReceiveRealData;
                _real.ReceiveRealData += OnReceiveRealData;

                _real.AdviseRealData();

                // ✅ 0600 OrdRegistered 이벤트 구독(1회)
                TryHookOrdMapRegisterEvent_NoLock();

                _started = true;

                Console.WriteLine("==================================================");
                Console.WriteLine("[0650][SC1 INIT] START");
                Console.WriteLine($"[0650][SC1 INIT] real hash={_real.GetHashCode()}");
                Console.WriteLine("[0650] SC1 수신처리 초기화 완료");
                Console.WriteLine("[0650] SC1 AdviseRealData 호출");
                Console.WriteLine("[0650][SC1 INIT] END");
                Console.WriteLine("==================================================");
            }
        }

        private void TryHookOrdMapRegisterEvent_NoLock()
        {
            try
            {
                var ordMap = Login.OrdMap;
                if (ordMap == null) return;

                if (!object.ReferenceEquals(_subscribedMap, ordMap))
                {
                    if (_subscribedMap != null)
                    {
                        try { _subscribedMap.OrdRegistered -= OnOrdRegistered; } catch { }
                    }

                    _subscribedMap = ordMap;
                    try { _subscribedMap.OrdRegistered -= OnOrdRegistered; } catch { } // 중복 방지
                    _subscribedMap.OrdRegistered += OnOrdRegistered;

                    Console.WriteLine("[0650] hooked OrdMap.OrdRegistered");
                }
            }
            catch { }
        }

        private void OnOrdRegistered(long ordNo)
        {
            try
            {
                // ✅ Register 직후: pending 있으면 flush
                FlushPendingForOrdNo(ordNo);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[0650][PENDING] OnOrdRegistered EX: " + ex);
            }
        }

        private void FlushPendingForOrdNo(long ordNo)
        {
            List<PendingFill> list = null;

            lock (_lock)
            {
                if (_pendingByOrdNo.TryGetValue(ordNo, out list))
                {
                    // 복사해서 lock 밖에서 처리
                    _pendingByOrdNo.Remove(ordNo);
                    list = new List<PendingFill>(list);
                }
            }

            if (list == null || list.Count == 0) return;

            Console.WriteLine($"[0650][PENDING] flush START ordNo={ordNo} count={list.Count}");

            // 도착 순서대로 처리
            list.Sort((a, b) => a.ArrivedAt.CompareTo(b.ArrivedAt));

            foreach (var pf in list)
            {
                ProcessOneFill_WithMap(pf.OrdNo, pf.ExecNo, pf.FilledQty, pf.Price, fromPending: true);
            }

            Console.WriteLine($"[0650][PENDING] flush END ordNo={ordNo}");
        }

        private void OnReceiveRealData(string trCode)
        {
            try
            {
                // ✅ 혹시 OrdMap 교체되는 구조면, SC1 수신 중에도 다시 hook 시도
                lock (_lock)
                {
                    TryHookOrdMapRegisterEvent_NoLock();
                }

                long ordNo = ReadLongAny("ordno", "ordno1", "ordno2", "ordno3", "ordno_");
                long execNo = ReadLongAny("execno", "chevolno", "execno1", "exec_no", "execnum");
                int filledQty = (int)ReadLongAny("cheqty", "execqty", "chevol", "qty", "volume");
                long priceLong = ReadLongAny("cheprice", "execprc", "price", "cheprc", "che_prc");
                double filledPrice = (priceLong > 0) ? priceLong : 0;

                if (ordNo <= 0 || execNo <= 0 || filledQty <= 0)
                {
                    Console.WriteLine($"[0650][SC1] invalid fields ordNo={ordNo} execNo={execNo} qty={filledQty} price={filledPrice} -> skip");
                    return;
                }

                if (IsDupExecNo(execNo))
                {
                    Console.WriteLine($"[0650][SC1] DUP execNo -> skip execNo={execNo} ordNo={ordNo}");
                    return;
                }

                // ✅ 핵심: mapping 없으면 pending 저장, 있으면 즉시 처리
                if (!TryProcessOrPend(ordNo, execNo, filledQty, filledPrice))
                {
                    // pending으로 들어간 케이스
                    return;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[0650][SC1] EX: " + ex);
            }
        }

        private bool TryProcessOrPend(long ordNo, long execNo, int filledQty, double filledPrice)
        {
            var ordMap = Login.OrdMap;
            if (ordMap == null)
            {
                Console.WriteLine("[0650][SC1] Login.OrdMap is null -> PEND ordNo=" + ordNo);
                AddPending(ordNo, execNo, filledQty, filledPrice);
                return false;
            }

            // mapping이 아직 없을 수 있으므로, 여기서 AddFill을 먼저 해보고 실패하면 pending
            if (!ordMap.AddFill(
                    ordNo: ordNo,
                    filledQty: filledQty,
                    out string sideKor,
                    out int band,
                    out int orderQty,
                    out int cumFill,
                    out int remain,
                    out bool isComplete))
            {
                Console.WriteLine($"[0650][SC1] ordNo mapping not found -> PENDING ordNo={ordNo} execNo={execNo} qty={filledQty} price={filledPrice}");
                AddPending(ordNo, execNo, filledQty, filledPrice);
                return false;
            }

            // ✅ mapping이 있으면 즉시 처리
            ProcessMappedResult(
                ordNo, execNo, filledQty, filledPrice,
                sideKor, band, orderQty, cumFill, remain, isComplete,
                fromPending: false);

            return true;
        }

        private void AddPending(long ordNo, long execNo, int filledQty, double filledPrice)
        {
            lock (_lock)
            {
                if (!_pendingByOrdNo.TryGetValue(ordNo, out var list))
                {
                    list = new List<PendingFill>(4);
                    _pendingByOrdNo[ordNo] = list;
                }

                list.Add(new PendingFill
                {
                    OrdNo = ordNo,
                    ExecNo = execNo,
                    FilledQty = filledQty,
                    Price = filledPrice,
                    ArrivedAt = DateTime.Now
                });

                // pending 폭주 방지(보수적)
                if (list.Count > 50)
                {
                    list.RemoveRange(0, list.Count - 50);
                }
            }
        }

        // pending flush에서 “다시 AddFill”을 수행해야 하므로, 공통 처리 함수 분리
        private void ProcessOneFill_WithMap(long ordNo, long execNo, int filledQty, double filledPrice, bool fromPending)
        {
            var ordMap = Login.OrdMap;
            if (ordMap == null)
            {
                Console.WriteLine($"[0650][PENDING] OrdMap null while flushing -> re-PEND ordNo={ordNo} execNo={execNo}");
                AddPending(ordNo, execNo, filledQty, filledPrice);
                return;
            }

            if (!ordMap.AddFill(
                    ordNo: ordNo,
                    filledQty: filledQty,
                    out string sideKor,
                    out int band,
                    out int orderQty,
                    out int cumFill,
                    out int remain,
                    out bool isComplete))
            {
                Console.WriteLine($"[0650][PENDING] mapping still not found -> re-PEND ordNo={ordNo} execNo={execNo}");
                AddPending(ordNo, execNo, filledQty, filledPrice);
                return;
            }

            ProcessMappedResult(
                ordNo, execNo, filledQty, filledPrice,
                sideKor, band, orderQty, cumFill, remain, isComplete,
                fromPending);
        }

        private void ProcessMappedResult(
            long ordNo,
            long execNo,
            int filledQty,
            double filledPrice,
            string sideKor,
            int band,
            int orderQty,
            int cumFill,
            int remain,
            bool isComplete,
            bool fromPending)
        {
            // ✅ 요청 포맷(부분/완료)
            if (!isComplete)
            {
                Console.WriteLine($"[0650][SC1] band={band} ordNo={ordNo} execNo={execNo} cum={cumFill} remain={remain} (partial...)");
            }
            else
            {
                Console.WriteLine($"[0650][SC1] band={band} ordNo={ordNo} execNo={execNo} cum={cumFill} remain={remain} (complete...)");
            }

            if (fromPending)
            {
                Console.WriteLine($"[0650][PENDING] APPLY ordNo={ordNo} execNo={execNo} side={sideKor} band={band} fill={filledQty} price={filledPrice} cumFill={cumFill}/{orderQty} remain={remain}");
            }
            else
            {
                // 기존 상세 로그(유지)
                Console.WriteLine($"[0650][SC1] ordNo={ordNo} execNo={execNo} side={sideKor} band={band} fill={filledQty} price={filledPrice} cumFill={cumFill}/{orderQty} remain={remain}");
            }

            // 0700 반영(체결 수량만큼)
            var up0700 = Login.AfterFillUpdate70;
            if (up0700 == null)
            {
                Console.WriteLine("[0650][SC1] Login.AfterFillUpdate70 is null -> KEEP LOCK (no update)");
            }
            else
            {
                up0700.AfterFillUpdate(
                    band: band,
                    deltaQty: filledQty,
                    price: filledPrice,
                    side: sideKor,
                    execNo: execNo
                );
            }

            try { Filled?.Invoke(sideKor, band, filledQty, filledPrice, execNo); } catch { }

            var gate = Login.TradeWait;
            if (gate == null)
            {
                Console.WriteLine("[0650][SC1] Login.TradeWait is null (gate missing)");
                return;
            }

            if (!isComplete)
            {
                gate.MarkPartialFill(ordNo, cumFill, remain);
                return; // ✅ A안: 부분체결이면 다음 밴드 절대 진행하지 않음
            }

            // ✅ 완전체결: 잠금 해제
            gate.MarkCompleteFill(ordNo, cumFill);

            // ✅ 체인 NEXT 로그 (당신 로그와 동일 포맷 유지)
            Console.WriteLine($"[0650] COMPLETE band={band} -> UNLOCK -> NEXT");

            // ✅ 다음 밴드 전송(또는 종료)
            밴드매칭.OnTradeCompleted_ThenSendNextOrFinish(band);
        }

        private bool IsDupExecNo(long execNo)
        {
            lock (_lock)
            {
                if (_execNoSeen.Contains(execNo)) return true;

                _execNoSeen.Add(execNo);
                if (_execNoSeen.Count > EXECNO_CACHE_LIMIT)
                {
                    _execNoSeen.Clear();
                    _execNoSeen.Add(execNo);
                }
                return false;
            }
        }

        private long ReadLongAny(params string[] fieldNames)
        {
            if (_real == null) return 0;

            foreach (var f in fieldNames)
            {
                try
                {
                    string s = (_real.GetFieldData("OutBlock", f) ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(s)) continue;

                    s = s.Replace(",", "");
                    if (long.TryParse(s, out long v))
                        return v;
                }
                catch { }
            }
            return 0;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                try
                {
                    if (_real != null)
                    {
                        _real.ReceiveRealData -= OnReceiveRealData;
                        try { _real.UnadviseRealData(); } catch { }
                    }
                }
                catch { }

                try
                {
                    if (_subscribedMap != null)
                    {
                        _subscribedMap.OrdRegistered -= OnOrdRegistered;
                    }
                }
                catch { }

                _real = null;
                _started = false;

                try { _execNoSeen.Clear(); } catch { }
                try { _pendingByOrdNo.Clear(); } catch { }
                _subscribedMap = null;
            }
        }

        // 2026-02-09 73410
    }
}
// 2026-02-09 98162
