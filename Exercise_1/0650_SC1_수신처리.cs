// 0650_SC1_수신처리.cs  (복붙용 / C# 7.3)  [A안: 부분체결이면 잠금 유지 + ✅pending flush로 레이스 해결 + ✅Finalize는 UNLOCK 후 1회만]
// ------------------------------------------------------------
// ✅ 핵심(이번 수정):
// - 0700을 "체결반영(ApplyFillOnly)" 과 "밴드이동/리셋(FinalizeAfterUnlock)"으로 분리
// - 부분체결: ApplyFillOnly만 호출 + LOCK 유지 (밴드 이동 금지)
// - 완전체결: ApplyFillOnly 호출 → 0550.MarkCompleteFill(UNLOCK) → 0700.FinalizeAfterUnlock 1회 호출
//
// ✅ pending flush:
// - SC1이 0600.Register보다 먼저 들어오는 레이스(모의서버 즉시체결)를 pending으로 저장
// - 0600 OrdRegistered 이벤트에서 즉시 flush
//
// ✅ 로그:
// [0650][SC1] ... (부분체결...)
// [0650][SC1] ... (완전체결...)
// [0650] COMPLETE band=43 -> UNLOCK -> FINALIZE -> NEXT
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
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
                    try { _subscribedMap.OrdRegistered -= OnOrdRegistered; } catch { }
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
                    _pendingByOrdNo.Remove(ordNo);
                    list = new List<PendingFill>(list);
                }
            }

            if (list == null || list.Count == 0) return;

            Console.WriteLine($"[0650][PENDING] flush START ordNo={ordNo} count={list.Count}");

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

                // mapping 있으면 즉시 처리, 없으면 pending
                TryProcessOrPend(ordNo, execNo, filledQty, filledPrice);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[0650][SC1] EX: " + ex);
            }
        }

        private void TryProcessOrPend(long ordNo, long execNo, int filledQty, double filledPrice)
        {
            var ordMap = Login.OrdMap;
            if (ordMap == null)
            {
                Console.WriteLine("[0650][SC1] Login.OrdMap is null -> PEND ordNo=" + ordNo);
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
                Console.WriteLine($"[0650][SC1] ordNo mapping not found -> PENDING ordNo={ordNo} execNo={execNo} qty={filledQty} price={filledPrice}");
                AddPending(ordNo, execNo, filledQty, filledPrice);
                return;
            }

            ProcessMappedResult(
                ordNo, execNo, filledQty, filledPrice,
                sideKor, band, orderQty, cumFill, remain, isComplete,
                fromPending: false);
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

                // 안전 제한
                if (list.Count > 50)
                {
                    list.RemoveRange(0, list.Count - 50);
                }
            }
        }

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
            sideKor = (sideKor ?? "").Trim();

            // ✅ 한글로 명확화
            if (!isComplete)
                Console.WriteLine($"[0650][SC1] band={band} ordNo={ordNo} execNo={execNo} cum={cumFill} remain={remain} (부분체결...)");
            else
                Console.WriteLine($"[0650][SC1] band={band} ordNo={ordNo} execNo={execNo} cum={cumFill} remain={remain} (완전체결...)");

            if (fromPending)
                Console.WriteLine($"[0650][PENDING] APPLY ordNo={ordNo} execNo={execNo} side={sideKor} band={band} fill={filledQty} price={filledPrice} cumFill={cumFill}/{orderQty} remain={remain}");
            else
                Console.WriteLine($"[0650][SC1] ordNo={ordNo} execNo={execNo} side={sideKor} band={band} fill={filledQty} price={filledPrice} cumFill={cumFill}/{orderQty} remain={remain}");

            // ------------------------------------------------------------
            // ✅ 0700: 부분체결이라도 "현실 포지션"은 반영(ApplyFillOnly)
            //    단, StartBand/FocusBand 이동은 "UNLOCK 후 Finalize"에서만!
            // ------------------------------------------------------------
            var up0700 = Login.AfterFillUpdate70;
            if (up0700 == null)
            {
                Console.WriteLine("[0650][SC1] Login.AfterFillUpdate70 is null -> KEEP LOCK (no update)");
            }
            else
            {
                up0700.ApplyFillOnly(
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
                // ✅ A안: 부분체결이면 잠금 유지 + 다음 밴드 진행 금지
                gate.MarkPartialFill(ordNo, cumFill, remain);
                return;
            }

            // ✅ 완전체결: 여기서 UNLOCK
            gate.MarkCompleteFill(ordNo, cumFill);

            // ✅ UNLOCK 이후에만 1회 Finalize (밴드 이동/리셋/UI)
            if (up0700 != null)
            {
                up0700.FinalizeAfterUnlock(sideKor, filledPrice);
            }

            Console.WriteLine($"[0650] COMPLETE band={band} -> UNLOCK -> FINALIZE -> NEXT");

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
    }
}
// 2026-02-11 48319
