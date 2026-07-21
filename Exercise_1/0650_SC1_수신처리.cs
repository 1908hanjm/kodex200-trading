// 0650_SC1_수신처리.cs  (복붙용 / C# 7.3)
// ------------------------------------------------------------
// ✅ 목적
// - SC1 체결 수신 처리
// - 0003_Pending복구실행이 보낸 주문의 체결 결과를 받아
//   Pending 테이블의 qty / stage / delete 를 관리한다.
// - SELL 완전체결 후 다음 BUY 주문을 자동 재실행한다.
// - BUY 완전체결 후 Pending 삭제 시 자동매매를 재개한다.
//
// ✅ 새 Pending 규칙
// - DOWN_SELLING / UP_SELLING
//   -> 부분체결: qty = remain
//   -> 완전체결: BUYING 단계로 전환, qty = cumFill, price = target_band 살가격
//   -> 이후 즉시 0003 재호출
//
// - DOWN_BUYING / UP_BUYING
//   -> 부분체결: qty = remain
//   -> 완전체결: Pending 삭제
//   -> 자동매매 재개
//
// ✅ 주의
// - 0700은 band qty/sina만 처리
// - Pending 생명주기는 0650에서만 처리
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.Threading.Tasks;
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

        // ✅ [FIX][LATE_REG_RACE] 2026-07-21 로그 분석(ordNo=816) 대응
        // 신규 라이브 주문 전송(0500) 직후 SC1 체결통지가 0600.TryRegister 완료보다
        // 먼저 도착하는 레이스가 발생하면, 진짜 재기동 복구 판정(MAX_BAND_BLOCKED/
        // sell_max_band_fallback_disabled)으로 즉시 넘어가지 않고 이 시간만큼 먼저
        // 대기(Pending)시켜 0600 등록을 기다린다. 이 시간 안에 등록되면 자동 flush되고,
        // 넘기면 그때 기존 차단 로직을 그대로 태운다(안전장치 자체는 그대로 유지).
        private const int LATE_REGISTRATION_GRACE_MS = 1500;

        private readonly Dictionary<long, List<PendingFill>> _pendingByOrdNo = new Dictionary<long, List<PendingFill>>();

        private long _mockExecSeed = DateTime.Now.Ticks % 1000000000L;

        private static readonly object _pendingKickSync = new object();
        private static bool _pendingKickRunning = false;

        private sealed class PendingFill
        {
            public long OrdNo;
            public long ExecNo;
            public int FilledQty;
            public double Price;
            public DateTime ArrivedAt;
        }

        private sealed class PendingRow
        {
            public string Side { get; set; }          // BUY / SELL
            public int TargetBand { get; set; }      // 목표 band
            public long Qty { get; set; }            // 남은 수량
            public int Price { get; set; }           // 조건 가격
            public int FromBand { get; set; }        // 출발 band
            public string Stage { get; set; }        // DOWN_SELLING / DOWN_BUYING / UP_SELLING / UP_BUYING
            public string UpdatedAt { get; set; }
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

        public void SimulateFill(string ordNoRaw, string sideKor, int band, int fillQty, double fillPrice)
        {
            try
            {
                sideKor = (sideKor ?? "").Trim();

                if (string.IsNullOrWhiteSpace(ordNoRaw))
                    throw new ArgumentNullException(nameof(ordNoRaw));
                if (fillQty <= 0)
                    throw new ArgumentOutOfRangeException(nameof(fillQty));
                if (fillPrice <= 0)
                    throw new ArgumentOutOfRangeException(nameof(fillPrice));

                if (!long.TryParse(ordNoRaw.Trim(), out long ordNo) || ordNo <= 0)
                    throw new InvalidOperationException("mock ordNoRaw parse 실패: " + ordNoRaw);

                lock (_lock)
                {
                    TryHookOrdMapRegisterEvent_NoLock();
                }

                long execNo = NextMockExecNo();

                if (IsDupExecNo(execNo))
                {
                    execNo = NextMockExecNo();
                }

                Console.WriteLine(
                    $"[0650][REPLAY][SIM] ordNo={ordNo} execNo={execNo} side={sideKor} band={band} qty={fillQty} price={fillPrice}");

                if (!Login.RestartRecoveryBootScanDone)
                {
                    Console.WriteLine("[RESTART_RECOVERY][SC_BEFORE_BOOT_SCAN_BLOCKED] ordNo=" + ordNo +
                                      " reason=boot_scan_not_done");
                    StopRestartRecovery(
                        "boot_scan_not_done", ordNo, band,
                        "simulate fill arrived before BOOT_SCAN_DONE",
                        sideKor, fillQty, fillPrice);
                    return;
                }

                TryProcessOrPend(ordNo, execNo, fillQty, fillPrice, sideKor, fillQty);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[0650][REPLAY][SIM] EX: " + ex.Message);
                throw;
            }
        }

        private long NextMockExecNo()
        {
            lock (_lock)
            {
                _mockExecSeed++;
                if (_mockExecSeed <= 0) _mockExecSeed = 1;
                return _mockExecSeed;
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
                Login.CurrentActiveOrderNo = ordNo;
                FlushPendingForOrdNo(ordNo);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[0650][PENDING-FLUSH] OnOrdRegistered EX: " + ex);
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

            Console.WriteLine($"[0650][FLUSH] ordNo={ordNo} count={list.Count}");

            list.Sort((a, b) => a.ArrivedAt.CompareTo(b.ArrivedAt));

            foreach (var pf in list)
            {
                ProcessOneFill_WithMap(pf.OrdNo, pf.ExecNo, pf.FilledQty, pf.Price, fromPending: true);
            }

            Console.WriteLine($"[0650][FLUSH] DONE ordNo={ordNo} count={list.Count}");
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
                int scOrderQty = (int)ReadLongAny("ordqty", "ordqty1", "orderqty", "ord_qty");
                string sideHint = NormalizeScSide(ReadStringAny(
                    "bnstp", "bnstpcode", "BnsTpCode", "medosu", "medosu_gb"));

                if (!Login.RestartRecoveryBootScanDone)
                {
                    Console.WriteLine("[RESTART_RECOVERY][SC_BEFORE_BOOT_SCAN_BLOCKED] ordNo=" + ordNo +
                                      " reason=boot_scan_not_done");
                    StopRestartRecovery(
                        "boot_scan_not_done", ordNo, 0,
                        "SC1 arrived before BOOT_SCAN_DONE",
                        sideHint, filledQty, filledPrice);
                    return;
                }

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

                TryProcessOrPend(ordNo, execNo, filledQty, filledPrice, sideHint, scOrderQty);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[0650][SC1] EX: " + ex);
            }
        }

        private void TryProcessOrPend(
            long ordNo,
            long execNo,
            int filledQty,
            double filledPrice,
            string sideHint,
            int scOrderQty)
        {
            if (!EnsureExecutionIsNew(ordNo, execNo, filledQty))
                return;

            var ordMap = Login.OrdMap;
            if (ordMap == null)
            {
                StopRestartRecovery("ordmap_null", ordNo);
                return;
            }

            bool mapped = ordMap.AddFill(
                    ordNo: ordNo,
                    filledQty: filledQty,
                    out string sideKor,
                    out int band,
                    out int orderQty,
                    out int cumFill,
                    out int remain,
                    out bool isComplete,
                    out bool alreadyComplete);

            if (!mapped)
            {
                // ✅ [FIX][LATE_REG_RACE] ordNo=816 사건 대응
                // AddFill 실패가 "진짜 재기동 복구 대상(과거 주문)"인지, 아니면
                // "이번 세션에서 방금 나간 라이브 주문인데 0600 등록이 SC1 체결통지보다
                // 늦게 끝난 것뿐"인지부터 구분한다.
                // - 복구테이블에 실제 행이 있으면(과거 주문) -> 기존 로직 그대로 진행
                // - 복구테이블에 행이 전혀 없으면(no_row_in_recovery_table) -> 즉시
                //   MAX_BAND_BLOCKED/BUY_BLOCK으로 차단하지 않고, 짧게 대기(Pending)
                //   시켜 0600.TryRegister가 뒤늦게 끝나는지부터 확인한다.
                bool hasRecoveryRow = RestartExecutionRecovery.TryGetTodayOrder(
                    ordNo, out _, out string peekReason);

                if (!hasRecoveryRow && peekReason == "no_row_in_recovery_table")
                {
                    QueuePendingWithLateRegistrationFallback(
                        ordNo, execNo, filledQty, filledPrice, sideHint, scOrderQty);
                    return;
                }

                if (!TryRestoreMissingMapping(
                    ordMap, ordNo, filledQty, filledPrice, sideHint, scOrderQty))
                    return;

                mapped = ordMap.AddFill(
                    ordNo: ordNo,
                    filledQty: filledQty,
                    out sideKor,
                    out band,
                    out orderQty,
                    out cumFill,
                    out remain,
                    out isComplete,
                    out alreadyComplete);

                if (!mapped)
                {
                    Console.WriteLine("[RESTART_RECOVERY][RESTORE_FAIL] reason=fallback_retry_mapping_failed ordNo=" + ordNo);
                    StopRestartRecovery("fallback_retry_mapping_failed", ordNo);
                    return;
                }

                Console.WriteLine("[RESTART_RECOVERY][FALLBACK_RETRY_OK] ordNo=" + ordNo);
            }

            // ✅ [BUG-FIX] 이미 완전체결 처리된 주문의 뒤늦은 SC1 이벤트 → 중복처리 방지
            if (alreadyComplete)
            {
                Console.WriteLine($"[0650][SC1][SKIP] ordNo={ordNo} execNo={execNo} -> AlreadyCompleted -> 무시");
                return;
            }

            ProcessMappedResult(
                ordNo, execNo, filledQty, filledPrice,
                sideKor, band, orderQty, cumFill, remain, isComplete,
                fromPending: false);
        }

        // ✅ [FIX][LATE_REG_RACE] 2026-07-21 로그 분석(ordNo=816) 대응 신규 메서드
        // AddFill이 실패했지만 복구테이블에도 행이 없는 경우, 이번 세션에서 방금
        // 나간 라이브 주문의 등록 레이스일 가능성을 먼저 검증한다.
        // 1) 즉시 AddPending 큐에 넣는다 (0600.TryRegister -> OrdRegistered 이벤트가
        //    발생하면 기존 FlushPendingForOrdNo/ProcessOneFill_WithMap 경로가 자동으로
        //    이 체결을 정상 처리한다).
        // 2) LATE_REGISTRATION_GRACE_MS 만큼만 기다렸다가, 그때까지도 등록되지 않아
        //    큐에 그대로 남아있으면 그제서야 기존 TryRestoreMissingMapping(복구테이블
        //    조회 -> MAX_BAND_BLOCKED/BUY_BLOCK 차단) 로직을 원래대로 실행한다.
        // 즉, 원래 있던 안전장치(모호하면 자동매매 정지)는 그대로 유지하되,
        // "방금 보낸 라이브 주문"을 오판하지 않도록 최종 판단만 살짝 늦춘다.
        private void QueuePendingWithLateRegistrationFallback(
            long ordNo,
            long execNo,
            int filledQty,
            double filledPrice,
            string sideHint,
            int scOrderQty)
        {
            AddPending(ordNo, execNo, filledQty, filledPrice);

            Console.WriteLine(
                $"[0650][PENDING][LATE_REG_WAIT] ordNo={ordNo} execNo={execNo} fillQty={filledQty} " +
                $"price={filledPrice} -> 0600 등록 대기 시작 (grace={LATE_REGISTRATION_GRACE_MS}ms)");

            Task.Delay(LATE_REGISTRATION_GRACE_MS).ContinueWith(_ =>
            {
                try
                {
                    List<PendingFill> stillPending = null;

                    lock (_lock)
                    {
                        if (_pendingByOrdNo.TryGetValue(ordNo, out var list) && list.Count > 0)
                        {
                            stillPending = new List<PendingFill>(list);
                            _pendingByOrdNo.Remove(ordNo);
                        }
                    }

                    if (stillPending == null)
                    {
                        // 유예시간 안에 0600 등록 -> OrdRegistered -> FlushPendingForOrdNo로
                        // 이미 정상 처리 완료된 경우. 더 할 일 없음.
                        return;
                    }

                    Console.WriteLine(
                        $"[0650][PENDING][LATE_REG_TIMEOUT] ordNo={ordNo} count={stillPending.Count} " +
                        "-> 유예시간 내 등록 안됨, 기존 재기동 복구 판정 로직으로 폴백");

                    var ordMap = Login.OrdMap;

                    foreach (var pf in stillPending)
                    {
                        try
                        {
                            if (ordMap != null && ordMap.Contains(pf.OrdNo))
                            {
                                // 타임아웃 직전 아슬아슬하게 등록됐을 경우를 위한 마지막 확인
                                ProcessOneFill_WithMap(pf.OrdNo, pf.ExecNo, pf.FilledQty, pf.Price, fromPending: true);
                                continue;
                            }

                            if (!TryRestoreMissingMapping(
                                ordMap, pf.OrdNo, pf.FilledQty, pf.Price, sideHint, scOrderQty))
                                continue;

                            if (ordMap != null && ordMap.AddFill(
                                    pf.OrdNo, pf.FilledQty,
                                    out string sideKor, out int band, out int orderQty,
                                    out int cumFill, out int remain, out bool isComplete, out bool alreadyComplete))
                            {
                                if (!alreadyComplete)
                                {
                                    ProcessMappedResult(
                                        pf.OrdNo, pf.ExecNo, pf.FilledQty, pf.Price,
                                        sideKor, band, orderQty, cumFill, remain, isComplete,
                                        fromPending: true);
                                }
                            }
                        }
                        catch (Exception exOne)
                        {
                            Console.WriteLine("[0650][PENDING][LATE_REG_TIMEOUT] EX ordNo=" + pf.OrdNo + " " + exOne.Message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[0650][PENDING][LATE_REG_TIMEOUT] outer EX ordNo=" + ordNo + " " + ex.Message);
                }
            });
        }

        private bool TryRestoreMissingMapping(
            _0600_주문번호_매핑 ordMap,
            long ordNo,
            int filledQty,
            double filledPrice,
            string sideHint,
            int scOrderQty)
        {
            RestartRecoveryOrder row;
            string lookupReason;
            if (RestartExecutionRecovery.TryGetTodayOrder(ordNo, out row, out lookupReason))
            {
                Console.WriteLine("[RESTART_RECOVERY][FALLBACK_HIT] ordNo=" + ordNo);
                if (!TryRegisterRecoveryRow(ordMap, row, "SC_FALLBACK", out string restoreReason))
                {
                    Console.WriteLine("[RESTART_RECOVERY][RESTORE_FAIL] reason=" + restoreReason + " ordNo=" + ordNo);
                    StopRestartRecovery(
                        restoreReason, ordNo, row.Band,
                        "tradeType=" + row.TradeType + " recoverySource=" + row.RecoverySource,
                        row.Side, filledQty, filledPrice);
                    return false;
                }

                Console.WriteLine("[RESTART_RECOVERY][FALLBACK_REGISTER] ordNo=" + ordNo +
                                  " side=" + row.Side + " band=" + row.Band + " qty=" + row.OrderQty);
                return true;
            }

            if (lookupReason != "no_row_in_recovery_table")
            {
                Console.WriteLine("[RESTART_RECOVERY][RESTORE_FAIL] reason=recovery_lookup_error:" + lookupReason +
                                  " ordNo=" + ordNo);
                StopRestartRecovery(
                    "restore_failed", ordNo, 0,
                    "recovery_lookup_error:" + lookupReason,
                    sideHint, filledQty, filledPrice);
                return false;
            }

            string side = NormalizeScSide(sideHint);
            if (side == "매수")
            {
                Console.WriteLine("[RESTART_RECOVERY][BUY_BLOCK] ordNo=" + ordNo + " qty=" + filledQty);
                StopRestartRecovery(
                    "buy_without_recovery_row", ordNo, 0,
                    "OrdMap and recovery row are both missing",
                    "BUY", filledQty, filledPrice);
                return false;
            }

            if (side == "매도")
            {
                Console.WriteLine("[RESTART_RECOVERY][MAX_BAND_BLOCKED] " +
                                  "ordNo=" + ordNo + " side=SELL fillQty=" + filledQty +
                                  " price=" + filledPrice +
                                  " reason=missing_recovery_row_band_uncertain");
                StopRestartRecovery(
                    "sell_max_band_fallback_disabled", ordNo, 0,
                    "missing_recovery_row_band_uncertain scOrderQty=" + scOrderQty,
                    "SELL", filledQty, filledPrice);
                return false;
            }

            StopRestartRecovery(
                "unknown_side_without_recovery_row", ordNo, 0,
                "SC side field is empty or unsupported",
                sideHint, filledQty, filledPrice);
            return false;
        }

        private static bool TryRegisterRecoveryRow(
            _0600_주문번호_매핑 ordMap,
            RestartRecoveryOrder row,
            string source,
            out string reason)
        {
            reason = "invalid_recovery_row";
            if (ordMap == null || row == null || row.OrdNo <= 0 ||
                string.IsNullOrWhiteSpace(row.Side) || row.Band <= 0 || row.OrderQty <= 0)
                return false;

            return ordMap.TryRestoreFromRecovery(
                sideRaw: row.Side,
                executeBand: row.Band,
                ordNo: row.OrdNo,
                orderQty: row.OrderQty,
                cumFill: row.CumFill,
                fromBand: row.FromBand,
                fromQty: row.FromQty,
                extraQty: row.ExtraQty,
                tradeType: row.TradeType,
                source: source,
                reason: out reason);
        }

        private bool EnsureExecutionIsNew(long ordNo, long execNo, int filledQty)
        {
            bool exists;
            string reason;
            if (!RestartExecutionRecovery.TryHasExecution(ordNo, execNo, out exists, out reason))
            {
                StopRestartRecovery("exec_dedup_check_failed:" + reason, ordNo);
                return false;
            }

            if (!exists) return true;

            Console.WriteLine("[RESTART_RECOVERY][DUP_EXEC_SKIP] ordNo=" + ordNo +
                              " execNo=" + execNo + " fillQty=" + filledQty);
            return false;
        }

        private static void StopRestartRecovery(
            string reason,
            long ordNo,
            int band = 0,
            string detail = "",
            string side = "",
            int qty = 0,
            double price = 0,
            bool showPopup = true)
        {
            Login.StopAutoTradingForRestartRecovery(
                reason, ordNo, band, detail, showPopup, side, qty, price);
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

                if (list.Count > 50)
                {
                    list.RemoveRange(0, list.Count - 50);
                }

                if (_pendingByOrdNo.Count > 30)
                {
                    long oldestKey = 0;
                    DateTime oldestTime = DateTime.MaxValue;

                    foreach (var kv in _pendingByOrdNo)
                    {
                        var v = kv.Value;
                        if (v == null || v.Count == 0) continue;
                        var t0 = v[0].ArrivedAt;
                        if (t0 < oldestTime)
                        {
                            oldestTime = t0;
                            oldestKey = kv.Key;
                        }
                    }

                    if (oldestKey != 0 && oldestKey != ordNo)
                    {
                        _pendingByOrdNo.Remove(oldestKey);
                    }
                }
            }
        }

        private void ProcessOneFill_WithMap(long ordNo, long execNo, int filledQty, double filledPrice, bool fromPending)
        {
            if (!EnsureExecutionIsNew(ordNo, execNo, filledQty))
                return;

            var ordMap = Login.OrdMap;
            if (ordMap == null)
            {
                Console.WriteLine($"[0650][PENDING-FLUSH] OrdMap null while flushing -> re-PEND ordNo={ordNo} execNo={execNo}");
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
                    out bool isComplete,
                    out bool alreadyComplete))
            {
                Console.WriteLine($"[0650][PENDING-FLUSH] mapping still not found -> re-PEND ordNo={ordNo} execNo={execNo}");
                AddPending(ordNo, execNo, filledQty, filledPrice);
                return;
            }

            // ✅ [BUG-FIX] 이미 완전체결 처리된 주문의 뒤늦은 Pending flush → 중복처리 방지
            if (alreadyComplete)
            {
                Console.WriteLine($"[0650][PENDING-FLUSH][SKIP] ordNo={ordNo} execNo={execNo} -> AlreadyCompleted -> 무시");
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

            // ✅ [LOG-REDUCE] 틱 단위 부분체결 로그는 Debug로 내리고, 완전체결만 Console(로그파일)에 남긴다.
            if (!isComplete)
                Debug.WriteLine($"[0650][SC1] band={band} ordNo={ordNo} execNo={execNo} cum={cumFill} remain={remain} (부분체결...)");
            else
                Console.WriteLine($"[0650][SC1] band={band} ordNo={ordNo} execNo={execNo} cum={cumFill} remain={remain} (완전체결...)");

            if (fromPending)
                Console.WriteLine($"[0650][PENDING-FLUSH] APPLY ordNo={ordNo} execNo={execNo} side={sideKor} band={band} fill={filledQty} price={filledPrice} cumFill={cumFill}/{orderQty} remain={remain}");
            else if (isComplete)
                Console.WriteLine($"[0650][SC1] ordNo={ordNo} execNo={execNo} side={sideKor} band={band} fill={filledQty} price={filledPrice} cumFill={cumFill}/{orderQty} remain={remain}");
            else
                Debug.WriteLine($"[0650][SC1] ordNo={ordNo} execNo={execNo} side={sideKor} band={band} fill={filledQty} price={filledPrice} cumFill={cumFill}/{orderQty} remain={remain}");

            if (ordNo == 1302)
            {
                Console.WriteLine("[CHECK][1302][0650] ordNo=1302 fill=" + filledQty +
                                  " cum=" + cumFill +
                                  " remain=" + remain +
                                  " complete=" + isComplete +
                                  " next=process_mapped_result");
            }

            var up0700 = Login.AfterFillUpdate70;
            int downSlideFromBand = 0;
            int downSlideFromQty = 0;
            int downSlideRecordBand = 0;
            string upSlideChainType = "";
            int upSlideSourceBand = 0;
            int upSlideTargetBand = 0;
            long upSlideFromQty = 0;
            long upSlideExtraQty = 0;
            int upSlideRecordBand = 0;
            int chainExtendFromBand = 0;
            int chainExtendTargetBand = 0;
            int chainExtendRecordBand = 0;
            bool isRebuildBuy = false;
            int rebuildLastSoldBand = 0;
            int rebuildSellDecisionBand = 0;
            int rebuildStartBand = 0;
            List<int> rebuildTargetBands = new List<int>();
            string rebuildDistributionMode = "";
            try
            {
                var ordMapForSlide = Login.OrdMap;
                if (ordMapForSlide != null &&
                    ordMapForSlide.TryGetDownSlideLink(ordNo, out downSlideFromBand, out downSlideFromQty, out downSlideRecordBand))
                {
                    Console.WriteLine($"[0650][DOWN-LINK] ordNo={ordNo} fromBand={downSlideFromBand} fromQty={downSlideFromQty} recordBand={downSlideRecordBand}");
                }
            }
            catch { }

            try
            {
                var ordMapForChainExtend = Login.OrdMap;
                if (ordMapForChainExtend != null &&
                    ordMapForChainExtend.TryGetChainExtendLink(
                        ordNo,
                        out chainExtendFromBand,
                        out chainExtendTargetBand,
                        out chainExtendRecordBand))
                {
                    Console.WriteLine("[0650][10전슬라이딩BUY][META] ordNo=" + ordNo +
                                      " targetBand=" + chainExtendTargetBand +
                                      " fromBand=" + chainExtendFromBand +
                                      " recordBand=" + chainExtendRecordBand);
                }
            }
            catch { }

            try
            {
                var ordMapForUpSlide = Login.OrdMap;
                if (ordMapForUpSlide != null &&
                    ordMapForUpSlide.TryGetUpSlideLink(
                        ordNo,
                        out upSlideChainType,
                        out upSlideSourceBand,
                        out upSlideTargetBand,
                        out upSlideFromQty,
                        out upSlideExtraQty,
                        out upSlideRecordBand))
                {
                    Console.WriteLine("[0650][UP][CHAIN_META] ordNo=" + ordNo +
                                      " chainType=" + upSlideChainType +
                                      " sourceBand=" + upSlideSourceBand +
                                      " targetBand=" + upSlideTargetBand +
                                      " fromQty=" + upSlideFromQty +
                                      " extraQty=" + upSlideExtraQty +
                                      " recordBand=" + upSlideRecordBand);
                }
            }
            catch { }

            try
            {
                var ordMapForRebuild = Login.OrdMap;
                if (ordMapForRebuild != null &&
                    ordMapForRebuild.TryGetRebuildBuyInfo(
                        ordNo,
                        out rebuildLastSoldBand,
                        out rebuildSellDecisionBand,
                        out rebuildStartBand,
                        out rebuildTargetBands,
                        out rebuildDistributionMode))
                {
                    isRebuildBuy = true;
                    Console.WriteLine("[0650][REBUILD] ordNo=" + ordNo +
                                      " IsRebuildBuy=true lastSoldBand=" + rebuildLastSoldBand +
                                      " sellDecisionBand=" + rebuildSellDecisionBand +
                                      " rebuildStartBand=" + rebuildStartBand +
                                      " distributionMode=" + rebuildDistributionMode +
                                      " targetBands=" + string.Join(",", rebuildTargetBands));
                }
            }
            catch (Exception exRebuild)
            {
                Console.WriteLine("[0650][REBUILD][ERR] " + exRebuild.Message);
            }

            // OrdMap 매핑 성공 흐름에서만 영속 원장을 갱신한다.
            // 실패해도 기존 SC/DB 처리는 계속된다.
            RestartExecutionRecovery.AppendExecution(
                ordNo: ordNo,
                execNo: execNo,
                fillQty: filledQty,
                fillPrice: filledPrice,
                side: sideKor,
                band: band,
                orderQty: orderQty,
                ordMapCumFill: cumFill,
                ordMapRemain: remain,
                complete: isComplete,
                chainExtendFromBand: chainExtendFromBand,
                chainExtendTargetBand: chainExtendTargetBand);

            if (remain > 0)
            {
                try { LoginFormAccessor.TryGetLogin()?.SetScWaitStatus(true, ordNo, remain, "0650_PARTIAL"); } catch { }
                Console.WriteLine("[0800][SC_WAIT] remain=" + remain);
                if (sideKor == "매수" &&
                    (downSlideRecordBand > 0 || Login.UpSwapInProgress || Login.SwapInProgress))
                {
                    Console.WriteLine("[SLIDE][UNFILLED_IGNORE] " +
                                      "OrderNo=" + ordNo +
                                      " RemainQty=" + remain +
                                      " Reason=NotSourceOfTruth");
                }
            }
            else
            {
                try { LoginFormAccessor.TryGetLogin()?.SetScWaitStatus(false, ordNo, 0, "0650_COMPLETE"); } catch { }
                Console.WriteLine("[0800][SC_WAIT] textbox15 cleared");
            }

            // ✅ [BUG-FIX] 부분체결 처리 경로 (기존과 동일)
            // 완전체결 경로는 아래에서 UNLOCK → DB/UI → FINALIZE/FOCUS 순으로 별도 처리한다.
            if (!isComplete)
            {
                if (ordNo == 1302)
                {
                    Console.WriteLine("[CHECK][1302][0650] ordNo=1302 fill=" + filledQty +
                                      " cum=" + cumFill +
                                      " remain=" + remain +
                                      " complete=False" +
                                      " next=partial_keep_lock_return");
                }

                bool allowPartialUi = remain > 0 && ordNo == Login.CurrentActiveOrderNo;
                if (!allowPartialUi)
                {
                    Console.WriteLine($"[0650][SC1][UI-SKIP] ordNo={ordNo} active={Login.CurrentActiveOrderNo} remain={remain}");
                }
                else
                {
                    try { LoginFormAccessor.TryGetLogin()?.SetPartialFillStatusForOrder(true, ordNo, remain); } catch { }
                }

                // 부분체결: DB qty 업데이트 (FOCUS/REFRESH 없음)
                if (up0700 == null)
                    Console.WriteLine("[0650][SC1] Login.AfterFillUpdate70 is null -> KEEP LOCK (no update)");
                else
                    up0700.ApplyFillOnly(band: band, deltaQty: filledQty, price: filledPrice,
                                         side: sideKor, execNo: execNo, isComplete: false, ordNo: ordNo,
                                          downSlideFromBand: downSlideFromBand,
                                          downSlideFromQty: downSlideFromQty,
                                          downSlideRecordBand: downSlideRecordBand,
                                          upSlideSourceBand: upSlideSourceBand,
                                          upSlideFromQty: upSlideFromQty,
                                          upSlideExtraQty: upSlideExtraQty,
                                          upSlideTargetBand: upSlideTargetBand,
                                          chainExtendFromBand: chainExtendFromBand,
                                          chainExtendTargetBand: chainExtendTargetBand,
                                          isRebuildBuy: isRebuildBuy);

                TryApplyPendingAfterFill(sideKor: sideKor, band: band, filledQty: filledQty,
                                          orderQty: orderQty, cumFill: cumFill, remain: remain,
                                          isComplete: false);

                try { Filled?.Invoke(sideKor, band, filledQty, filledPrice, execNo); } catch { }

                try
                {
                    var chaser0 = Login.PendingChaser04;
                    if (chaser0 != null && ordNo > 0) chaser0.NotifyFill(ordNo.ToString(), filledQty);
                }
                catch (Exception exC) { Console.WriteLine("[0650][0004][NotifyFill][EX] " + exC.Message); }

                var gate0 = Login.TradeWait;
                if (gate0 == null) { Console.WriteLine("[0650][SC1] Login.TradeWait is null (gate missing)"); return; }
                gate0.MarkPartialFill(ordNo, cumFill, remain);
                return;
            }

            // ✅ [BUG-FIX] 완전체결 처리 순서 수정:
            //   구 순서: ApplyFillOnly → MarkCompleteFill(UNLOCK) → FinalizeAfterUnlock(FOCUS/REFRESH)
            //   이전 잘못된 수정: isComplete=false 강제 → ApplyFillOnly 내부 완료처리 누락 위험
            //   올바른 수정: ApplyFillOnly(isComplete=true 유지) → MarkCompleteFill(UNLOCK) → FinalizeAfterUnlock(FOCUS/REFRESH)
            //   핵심: FinalizeAfterUnlock(FOCUS/REFRESH)이 MarkCompleteFill(UNLOCK) 이후에 실행됨을 보장한다.

            // Step 1: DB qty 업데이트 + 완료처리 (isComplete=true 유지 — 의미 변경 없음)
            bool dbApplySucceeded = false;
            if (up0700 == null)
                Console.WriteLine("[0650][SC1] Login.AfterFillUpdate70 is null -> KEEP LOCK (no update)");
            else
            {
                if (ordNo == 1302)
                {
                    try { _0700_매매후update.MarkCheck1302Exec(execNo); } catch { }
                    Console.WriteLine("[CHECK][1302][0700] enter=true side=" +
                                      (sideKor == "매도" ? "SELL" : sideKor == "매수" ? "BUY" : sideKor) +
                                      " qty=" + filledQty +
                                      " reload=false chain=before_apply_fill_only");
                }

                dbApplySucceeded = up0700.ApplyFillOnly(
                    band: band, deltaQty: filledQty, price: filledPrice,
                    side: sideKor, execNo: execNo, isComplete: true, ordNo: ordNo,
                    downSlideFromBand: downSlideFromBand,
                    downSlideFromQty: downSlideFromQty,
                    downSlideRecordBand: downSlideRecordBand,
                    upSlideSourceBand: upSlideSourceBand,
                    upSlideFromQty: upSlideFromQty,
                    upSlideExtraQty: upSlideExtraQty,
                    upSlideTargetBand: upSlideTargetBand,
                    chainExtendFromBand: chainExtendFromBand,
                    chainExtendTargetBand: chainExtendTargetBand,
                    isRebuildBuy: isRebuildBuy);
            }

            if (dbApplySucceeded)
            {
                RestartExecutionRecovery.MarkDbApplied(
                    ordNo: ordNo,
                    execNo: execNo,
                    band: band,
                    side: sideKor,
                    fillQty: filledQty,
                    cumFill: cumFill,
                    remain: remain,
                    source: "ORDMAP");
            }

            var pendingAction = TryApplyPendingAfterFill(
                sideKor: sideKor, band: band, filledQty: filledQty,
                orderQty: orderQty, cumFill: cumFill, remain: remain, isComplete: true);

            try { Filled?.Invoke(sideKor, band, filledQty, filledPrice, execNo); } catch { }
            try
            {
                var login = LoginFormAccessor.TryGetLogin();
                string cashReason = (sideKor == "매수" ? "BUY" : sideKor == "매도" ? "SELL" : "FILL") + "_COMPLETE";
                // ✅ [FIX-E] SELL_COMPLETE_DELAY 현금조회는 PostFillRefreshAsync 경로에서 처리.
                // 여기서는 CSPAQ12200 throttle gate를 우회하는 직접 호출을 하지 않는다.
                // (PostFillRefreshAsync → RefreshOrderableCashTextBox6WithRetryAsync 에서 처리)
                Console.WriteLine("[POST_FILL_REFRESH][START] trigger=SC1_FILL_COMPLETE side=" + sideKor + " band=" + band + " ordNo=" + ordNo);
                login?.RequestOrderableCashTextBox6Refresh(cashReason + "_DELAY", delayMs: 1000);
            }
            catch { }

            try
            {
                var chaser = Login.PendingChaser04;
                if (chaser != null && ordNo > 0)
                    chaser.NotifyFill(ordNo.ToString(), filledQty);
            }
            catch (Exception exChaser) { Console.WriteLine("[0650][0004][NotifyFill][EX] " + exChaser.Message); }

            var gate = Login.TradeWait;
            if (gate == null) { Console.WriteLine("[0650][SC1] Login.TradeWait is null (gate missing)"); return; }

            // Step 2: UNLOCK
            if (ordNo == 1302)
            {
                bool beforeLocked = false;
                try { beforeLocked = gate.IsLocked; } catch { }
                Console.WriteLine("[CHECK][1302][0650] ordNo=1302 fill=" + filledQty +
                                  " cum=" + cumFill +
                                  " remain=" + remain +
                                  " complete=True" +
                                  " next=0550_mark_complete beforeLocked=" + beforeLocked);
            }
            gate.MarkCompleteFill(ordNo, cumFill);
            Login.CurrentActiveOrderNo = 0;
            try { LoginFormAccessor.TryGetLogin()?.SetPartialFillStatus(false); } catch { }

            // Step 3: FINALIZE + FOCUS/REFRESH (UNLOCK 이후에 실행)
            if (up0700 != null)
                up0700.FinalizeAfterUnlock(
                    sideKor,
                    band,
                    filledPrice,
                    downSlideFromBand,
                    downSlideFromQty,
                    downSlideRecordBand,
                    ordNo);

            if (Login.FullClearAfter80 != null && Login.FullClearAfter80.IsRestorePendingOrActive)
            {
                Console.WriteLine("[0650][0800][FULL-CLEAR] restore pending/active -> skip NEXT");
                Console.WriteLine("[0650][0800][LV3] reload request before skip NEXT");
                try
                {
                    LoginFormAccessor.TryGetLogin()?.RequestListView3ReloadFrom0800("FULL_CLEAR_REBUILD_FILL");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[0650][0800][LV3][ERR] " + ex.Message);
                }

                return;
            }

            if (pendingAction == PendingAction.NeedRunNextPendingOrder)
            {
                TryStartPendingContinuation("SELL_COMPLETE");
                Console.WriteLine($"[0650][PENDING] SELL complete band={band} -> NEXT PENDING ORDER");
                return;
            }

            if (pendingAction == PendingAction.PendingFinished)
            {
                ResumeAutoTradingAfterPendingComplete();
                Console.WriteLine($"[0650][PENDING] BUY complete band={band} -> AUTO TRADING RESUME");
                return;
            }

            Console.WriteLine($"[0650] COMPLETE band={band} -> UNLOCK -> FINALIZE -> NEXT");

            if (ordNo == 1302)
            {
                try { 밴드매칭.MarkCheck1302ChainPending("0650_complete_ord1302"); } catch { }
                Console.WriteLine("[CHECK][1302][CHAIN] raised=pending caller=0650_before_OnTradeCompleted band=" + band);
            }

            밴드매칭.OnTradeCompleted_ThenSendNextOrFinish(band);
        }

        private enum PendingAction
        {
            None = 0,
            NeedRunNextPendingOrder = 1,
            PendingFinished = 2
        }

        // =========================================================
        // ✅ 새 Pending 테이블 갱신
        // =========================================================
        private PendingAction TryApplyPendingAfterFill(
            string sideKor,
            int band,
            int filledQty,
            int orderQty,
            int cumFill,
            int remain,
            bool isComplete)
        {
            Console.WriteLine("[PENDING][DISABLED] Pending logic is disabled by policy.");
            return PendingAction.None;
#pragma warning disable CS0162
            try
            {
                var p = ReadPendingRow();
                if (p == null) return PendingAction.None;

                if (!IsMatchingPending(p, sideKor, band))
                    return PendingAction.None;

                string stage = SafeUpper(p.Stage);

                if (stage == "DOWN_SELLING" || stage == "UP_SELLING" || stage == "NORMAL_SELLING")
                {
                    if (!isComplete)
                    {
                        Console.WriteLine($"[0650][PENDING] partial SELL -> Pending qty 갱신 안 함, stage={p.Stage}, actualRemain={remain}");
                        return PendingAction.None;
                    }

                    string nextStage =
                        stage == "DOWN_SELLING" ? "DOWN_BUYING" :
                        stage == "UP_SELLING" ? "UP_BUYING" :
                        "NORMAL_BUYING";

                    int nextPrice = ResolveBuyPriceByBand(p.TargetBand);

                    if (nextPrice <= 0)
                    {
                        Console.WriteLine($"[0650][PENDING] SELL complete but next buy price invalid targetBand={p.TargetBand}");
                        return PendingAction.None;
                    }

                    UpdatePendingAfterSellComplete(
                        nextStage: nextStage,
                        nextQty: cumFill,
                        nextPrice: nextPrice);

                    Console.WriteLine($"[0650][PENDING] SELL complete -> {nextStage}, qty={cumFill}, price={nextPrice}");
                    return PendingAction.NeedRunNextPendingOrder;
                }

                if (stage == "DOWN_BUYING" || stage == "UP_BUYING" || stage == "NORMAL_BUYING")
                {
                    if (!isComplete)
                    {
                        Console.WriteLine($"[0650][PENDING] partial BUY -> Pending qty 갱신 안 함, stage={p.Stage}, actualRemain={remain}");
                        return PendingAction.None;
                    }

                    DeletePending();
                    Console.WriteLine("[0650][PENDING] BUY complete -> Pending 삭제");
                    return PendingAction.PendingFinished;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[0650][PENDING] TryApplyPendingAfterFill EX: " + ex.Message);
            }

            return PendingAction.None;
#pragma warning restore CS0162
        }

        private void TryStartPendingContinuation(string why)
        {
            try
            {
                lock (_pendingKickSync)
                {
                    if (_pendingKickRunning)
                    {
                        Console.WriteLine("[0650][PENDING] continuation already running -> skip");
                        return;
                    }

                    _pendingKickRunning = true;
                }

                Task.Run(async () =>
                {
                    try
                    {
                        await ContinuePendingAsync(why).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[0650][PENDING] ContinuePendingAsync EX: " + ex.Message);
                    }
                    finally
                    {
                        lock (_pendingKickSync)
                        {
                            _pendingKickRunning = false;
                        }
                    }
                });

                Console.WriteLine("[0650][PENDING] continuation START why=" + why);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[0650][PENDING] TryStartPendingContinuation EX: " + ex.Message);
            }
        }

        private async Task ContinuePendingAsync(string why)
        {
            Console.WriteLine("[PENDING][DISABLED] Pending logic is disabled by policy.");
            await Task.CompletedTask.ConfigureAwait(false);
            return;
#pragma warning disable CS0162
            try
            {
                var login = Login.Instance;
                if (login == null || login.IsDisposed)
                {
                    Console.WriteLine("[0650][PENDING] login null/disposed -> stop");
                    return;
                }

                int currentPrice = 0;

                try
                {
                    if (login.InvokeRequired)
                    {
                        login.Invoke(new Action(() =>
                        {
                            try { currentPrice = Convert.ToInt32(login.CurrentPrice); }
                            catch { currentPrice = 0; }
                        }));
                    }
                    else
                    {
                        try { currentPrice = Convert.ToInt32(login.CurrentPrice); }
                        catch { currentPrice = 0; }
                    }
                }
                catch
                {
                    currentPrice = 0;
                }

                var runner = new _0003_Pending복구실행(
                    Login.ConnStr,
                    async (sideKor, band, qty, price) =>
                    {
                        try
                        {
                            if (login == null || login.IsDisposed)
                            {
                                return new _0003_Pending복구실행.SendOrderAck
                                {
                                    Success = false,
                                    OrdNo = "",
                                    Message = "login null/disposed"
                                };
                            }

                            return await login.SendPendingOrderWithAckAsync(sideKor, band, qty, price).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("[0650][PENDING][EXEC][EX] " + ex.Message);
                            return new _0003_Pending복구실행.SendOrderAck
                            {
                                Success = false,
                                OrdNo = "",
                                Message = ex.Message
                            };
                        }
                    },
                    s => Console.WriteLine(s)
                );

                var result = await runner.RunOnceAsync(currentPrice).ConfigureAwait(false);
                Console.WriteLine("[0650][PENDING] ContinuePendingAsync result=" + result.Message);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[0650][PENDING] ContinuePendingAsync ERROR " + ex.Message);
            }
#pragma warning restore CS0162
        }

        private void ResumeAutoTradingAfterPendingComplete()
        {
            try
            {
                Login.AutoTradingBlocked = false;
            }
            catch { }

            try
            {
                var login = Login.Instance;
                if (login == null || login.IsDisposed) return;

                if (login.InvokeRequired)
                {
                    login.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            login.UpdateStatus("[PENDING] 복구 완료 / 자동매매 재개");
                            login.SetPanel2Color(login.GetSessionColor());
                        }
                        catch (Exception exUi)
                        {
                            Console.WriteLine("[0650][PENDING][UI] " + exUi.Message);
                        }
                    }));
                }
                else
                {
                    login.UpdateStatus("[PENDING] 복구 완료 / 자동매매 재개");
                    login.SetPanel2Color(login.GetSessionColor());
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[0650][PENDING] ResumeAutoTradingAfterPendingComplete EX " + ex.Message);
            }
        }

        private PendingRow ReadPendingRow()
        {
            return null;
        }

        private bool IsMatchingPending(PendingRow p, string sideKor, int band)
        {
            if (p == null) return false;

            string stage = SafeUpper(p.Stage);
            sideKor = (sideKor ?? "").Trim();

            // 중요:
            // 0650은 체결 시점의 Login.시작밴드변수를 다시 읽어서 Pending을 추정 매칭하지 않는다.
            // 여기서 전달되는 band는 0600 OrdMap에 등록된 실제 주문 band이다.
            // 따라서 Pending 매칭도 OrdMap band와 Pending row의 명시적 band만 기준으로 판단한다.

            if (stage == "DOWN_SELLING")
            {
                int sellBand = p.FromBand > 0 ? p.FromBand : p.TargetBand;
                return sideKor == "매도" && band == sellBand;
            }

            if (stage == "DOWN_BUYING")
            {
                int orderBandK = p.TargetBand > 1 ? (p.TargetBand - 1) : 1;
                return sideKor == "매수" && band == orderBandK;
            }

            if (stage == "UP_SELLING")
            {
                int sellBand = p.FromBand > 0 ? p.FromBand : p.TargetBand;
                return sideKor == "매도" && band == sellBand;
            }

            if (stage == "UP_BUYING")
            {
                return sideKor == "매수" && band == p.TargetBand;
            }

            if (stage == "NORMAL_SELLING")
            {
                int sellBand = p.FromBand > 0 ? p.FromBand : p.TargetBand;
                return sideKor == "매도" && band == sellBand;
            }

            if (stage == "NORMAL_BUYING")
            {
                return sideKor == "매수" && band == p.TargetBand;
            }

            return false;
        }

        private void UpdatePendingQty(long remainQty)
        {
            return;
        }

        private void UpdatePendingAfterSellComplete(string nextStage, long nextQty, int nextPrice)
        {
            return;
        }

        private void DeletePending()
        {
            return;
        }

        private int ResolveBuyPriceByBand(int band)
        {
            try
            {
                if (band <= 0) return 0;

                using (var conn = new SQLiteConnection(Login.ConnStr))
                {
                    conn.Open();

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            "SELECT 살가격 " +
                            "FROM kodex200_new " +
                            "WHERE band = @b";
                        cmd.Parameters.AddWithValue("@b", band);

                        object v = cmd.ExecuteScalar();
                        if (v == null || v == DBNull.Value)
                            return 0;

                        return Convert.ToInt32(v);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[0650][PENDING] ResolveBuyPriceByBand EX: " + ex.Message);
                return 0;
            }
        }

        private static string SafeUpper(string s)
        {
            return string.IsNullOrWhiteSpace(s)
                ? ""
                : s.Trim().ToUpperInvariant();
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

        private string ReadStringAny(params string[] fieldNames)
        {
            if (_real == null) return "";
            foreach (string field in fieldNames)
            {
                try
                {
                    string value = (_real.GetFieldData("OutBlock", field) ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
                catch { }
            }
            return "";
        }

        private static string NormalizeScSide(string raw)
        {
            string value = (raw ?? "").Trim();
            if (value == "2" || value == "매수" || value.Equals("BUY", StringComparison.OrdinalIgnoreCase)) return "매수";
            if (value == "1" || value == "매도" || value.Equals("SELL", StringComparison.OrdinalIgnoreCase)) return "매도";
            return "";
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
// 2026-04-10 28461
// 2026-05-08 63841
