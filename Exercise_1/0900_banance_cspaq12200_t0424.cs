// 0900_banance_cspaq12200_t0424.cs  (복붙용 / C# 7.3)
// 역할:
// - CSPAQ12200: 주문가능금액/예수금(D2 등) 조회
// - t0424: 보유수량 합계(qtySum) + 당일손익 합계(pnlSum) 조회(연속조회 IsNext 처리)
// - Login.cs에서 잔고/손익 TR 코드를 분리하기 위한 전용 모듈
//
// ✅ 2026-05-14 수정 핵심:
// - QueryT0424SumAsync / QueryCspaq12200Async 내부를 Task.Run(MTA) → RunOnStaThread(STA)로 변경
// - Xing XAQueryClass 는 STA COM 객체이므로 MTA에서 사용하면
//   ReceiveData 이벤트가 발화하지 않거나 GetBlockCount/GetFieldData 가 0/빈값을 반환함
//   → janqty=0 오류의 근본 원인
// - 상세 로그 추가: Request ret, ReceiveData 진입, cnt, janqtyRaw 등
//
// ✅ 2026-05-14 v2 추가:
// - QueryT0424SumAsync: janqty=0 방어 재조회 로직 추가
//   → 조회 결과 qtySum=0 이고 재조회 시도 횟수(maxRetry)가 남아있으면 1~2초 후 재조회
//   → 완전히 보유수량이 없는 경우(실제 0)와 간헐적 0을 구분하기 위해
//      retryCount와 retryDelay를 파라미터로 노출 (기본: 2회, 1.5초 간격)

using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using XA_DATASETLib;

namespace Exercise_1
{
    public sealed class _0900_banance_cspaq12200_t0424
    {
        private readonly Action<string> _log;

        public _0900_banance_cspaq12200_t0424(Action<string> log = null)
        {
            _log = log ?? (s => Debug.WriteLine("[0900_BAL] " + s));
        }

        // =========================================================
        // ✅ STA 전용 스레드 헬퍼
        // XAQueryClass 는 STA COM 객체 → Task.Run(MTA) 에서 사용하면
        // ReceiveData 이벤트가 발화하지 않는다.
        // =========================================================
        private static Task<T> RunOnStaThread<T>(Func<T> func)
        {
            var tcs = new TaskCompletionSource<T>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            var thread = new Thread(() =>
            {
                try
                {
                    tcs.SetResult(func());
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            return tcs.Task;
        }

        // =========================================================
        // CSPAQ12200: 주문가능금액/예수금(D2 등) 조회
        // =========================================================
        public Task<(double cash, double d2)> QueryCspaq12200Async(
            string actNo,
            string pwd,
            TimeSpan timeout)
        {
            return QueryCspaq12200Async(
                actNo,
                pwd,
                timeout,
                "QueryCspaq12200Async",
                "0900");
        }

        public Task<(double cash, double d2)> QueryCspaq12200Async(
            string actNo,
            string pwd,
            TimeSpan timeout,
            string reason,
            string caller)
        {
            if (string.IsNullOrWhiteSpace(actNo))
                throw new ArgumentException("actNo is empty", nameof(actNo));

            if (pwd == null) pwd = "";

            var gate = Cspaq12200GlobalGate.TryEnter(reason, caller);
            if (!gate.Allowed)
            {
                _log("[0900][CSPAQ12200][SKIP] reason=" + reason +
                     " caller=" + caller +
                     " skipReason=" + gate.SkipReason);
                return Task.FromResult((0.0, 0.0));
            }

            return RunOnStaThread(() =>
            {
                XAQueryClass q = null;
                AutoResetEvent done = null;

                string lastErr = null;

                _IXAQueryEvents_ReceiveDataEventHandler onData = null;
                _IXAQueryEvents_ReceiveMessageEventHandler onMsg = null;

                try
                {
                    q = new XAQueryClass();
                    done = new AutoResetEvent(false);

                    q.LoadFromResFile(@"C:\LS_SEC\xingAPI\res\CSPAQ12200.res");

                    onData = (string trCode) =>
                    {
                        try { done.Set(); } catch { }
                    };

                    onMsg = (bool isSysErr, string code, string msg) =>
                    {
                        try { _log($"[CSPAQ12200 MSG] sysErr={isSysErr} code={code} msg={msg}"); }
                        catch { }

                        if (isSysErr)
                            lastErr = $"[{code}] {msg}";
                    };

                    q.ReceiveData += onData;
                    q.ReceiveMessage += onMsg;

                    q.SetFieldData("CSPAQ12200InBlock1", "RecCnt", 0, "00001");
                    q.SetFieldData("CSPAQ12200InBlock1", "MgmtBrnNo", 0, "");
                    q.SetFieldData("CSPAQ12200InBlock1", "AcntNo", 0, actNo);
                    q.SetFieldData("CSPAQ12200InBlock1", "Pwd", 0, pwd);
                    q.SetFieldData("CSPAQ12200InBlock1", "BalCreTp", 0, "0");

                    _log("[0900][CSPAQ12200][REQ] reason=" + reason + " caller=" + caller);
                    _log("[CSPAQ12200] Request START");
                    int r = q.Request(false);
                    _log($"[CSPAQ12200] Request ret={r}");
                    gate.ReportResult(r);

                    if (r < 0)
                    {
                        if (r == -21)
                        {
                            _log("[0900][CSPAQ12200][RESULT] rc=-21 cash=N/A msg=RateLimited");
                            _log("CSPAQ12200 THROTTLED rc=-21 (전송제한) -> return (0,0)");
                            return (0.0, 0.0);
                        }
                        throw new Exception("CSPAQ12200 TR 요청 실패: " + r);
                    }

                    bool signaled = PumpWait(done, timeout);
                    if (!signaled)
                        throw new TimeoutException("CSPAQ12200 응답 타임아웃");

                    if (!string.IsNullOrEmpty(lastErr))
                        throw new Exception("CSPAQ12200 오류: " + lastErr);

                    double cash = ToDouble(q.GetFieldData("CSPAQ12200OutBlock2", "MnyOrdAbleAmt", 0));
                    double d2 = ToDouble(q.GetFieldData("CSPAQ12200OutBlock2", "DpsastTotamt", 0));

                    _log("[0900][CSPAQ12200][RESULT] rc=0 cash=" + cash + " d2=" + d2 + " msg=OK");
                    _log($"CSPAQ12200 OK cash={cash} d2={d2}");
                    return (cash, d2);
                }
                finally
                {
                    try { if (q != null && onData != null) q.ReceiveData -= onData; } catch { }
                    try { if (q != null && onMsg != null) q.ReceiveMessage -= onMsg; } catch { }
                    try { done?.Dispose(); } catch { }
                    try { if (q != null) Marshal.FinalReleaseComObject(q); } catch { }
                    try { gate.Dispose(); } catch { }
                }
            });
        }

        // =========================================================
        // t0424: 보유수량 합계(qtySum) + 당일손익 합계(pnlSum) 조회
        //
        // ✅ [추가 2026-05-14 v2] janqty=0 방어 재조회
        // - 조회 결과 qtySum=0 이면 간헐적 COM 이벤트 지연 가능성이 있으므로
        //   retryCount 횟수만큼 retryDelayMs 간격으로 재조회한다.
        // - 재조회 후에도 0이면 그대로 0을 반환한다. (실제 보유 없음 케이스)
        // - 호출자에서 재조회 여부를 판단하는 것보다 이 메서드 내에서 처리하는 것이 안전하다.
        //
        // 파라미터:
        //   retryIfZero   : true이면 qtySum=0 결과에 대해 재조회 시도 (기본 true)
        //   maxRetry      : 재조회 최대 횟수 (기본 2)
        //   retryDelayMs  : 재조회 사이 대기 시간 ms (기본 1500)
        // =========================================================
        // ✅ [2026-07-16 수정] THROTTLED(rc=-21)로 요청 자체가 거부되어 값을 못 받은 경우와
        // 실제로 조회에 성공해서 0(보유 없음)이 나온 경우를 confirmed 플래그로 구분한다.
        // confirmed=false 는 "실제 값을 확인하지 못했다"는 뜻이므로, 호출측(Login_08)은
        // 이 결과로 textBox5/DB 값을 덮어쓰지 말고 기존 값을 유지해야 한다.
        // (기존 버그: THROTTLED로 인한 qtySum=0 을 "실제 보유 없음"으로 오판하여
        //  잔고가 있는데도 UI에 0으로 표시되는 문제 발생 - 2026-07-16 확인)
        public async Task<(long qtySum, double pnlSum, double mamtSum, bool confirmed)> QueryT0424SumAsync(
            string actNo,
            string pwd,
            TimeSpan timeout,
            bool retryIfZero = true,
            int maxRetry = 2,
            int retryDelayMs = 1500)
        {
            if (string.IsNullOrWhiteSpace(actNo))
                throw new ArgumentException("actNo is empty", nameof(actNo));

            if (pwd == null) pwd = "";

            var (qtySum, pnlSum, mamtSum, throttled) = await QueryT0424SumOnceAsync(actNo, pwd, timeout)
                .ConfigureAwait(false);

            bool everConfirmed = !throttled;

            // ✅ [핵심] janqty=0 방어 재조회
            // qtySum=0 이 나왔을 때, 실제 보유가 있을 수 있으므로 재조회 시도
            // (THROTTLED로 qtySum=0이 나온 경우도 동일하게 재조회 대상에 포함)
            if (retryIfZero && qtySum == 0 && maxRetry > 0)
            {
                for (int attempt = 1; attempt <= maxRetry; attempt++)
                {
                    _log($"[T0424][RETRY] qtySum=0 (throttled={throttled}) → {retryDelayMs}ms 후 재조회 attempt={attempt}/{maxRetry}");
                    await Task.Delay(retryDelayMs).ConfigureAwait(false);

                    var (retryQty, retryPnl, retryMamt, retryThrottled) = await QueryT0424SumOnceAsync(actNo, pwd, timeout)
                        .ConfigureAwait(false);

                    _log($"[T0424][RETRY] attempt={attempt} qtySum={retryQty} pnlSum={retryPnl} mamtSum={retryMamt} throttled={retryThrottled}");

                    if (!retryThrottled)
                    {
                        everConfirmed = true;
                        if (retryQty > 0)
                        {
                            // 재조회에서 0이 아닌 값을 얻었으므로 이것을 사용
                            _log($"[T0424][RETRY] OK qtySum={retryQty} -> 재조회 성공");
                            return (retryQty, retryPnl, retryMamt, confirmed: true);
                        }

                        // 실제로 조회에 성공했는데 0 -> 신뢰 가능한 0으로 갱신하고 계속 진행
                        qtySum = retryQty; pnlSum = retryPnl; mamtSum = retryMamt; throttled = false;
                        continue;
                    }
                    // retryThrottled == true -> 이번 시도 값은 버리고 다음 재시도로
                }

                if (!everConfirmed)
                {
                    // 최초 호출 + 모든 재조회가 전부 THROTTLED -> 실제 보유 여부를 전혀 확인하지 못함
                    // (기존 코드는 여기서 "실제 보유 없음"으로 단정하고 0을 반환하던 부분 - 버그)
                    _log($"[T0424][RETRY] {maxRetry}회 재조회 모두 THROTTLED -> confirmed=false (실제 값 확인 불가, 호출측 기존 값 유지 필요)");
                    return (0, 0, 0, confirmed: false);
                }

                // 실제 조회는 됐지만(throttled 아님) 계속 0 -> 진짜 보유 없음
                _log($"[T0424][RETRY] {maxRetry}회 재조회 후에도 qtySum=0 (실제 조회 결과) -> 0 반환 (실제 보유 없음으로 판단)");
                return (qtySum, pnlSum, mamtSum, confirmed: true);
            }

            return (qtySum, pnlSum, mamtSum, confirmed: everConfirmed);
        }

        // =========================================================
        // t0424 1회 단독 조회 (내부 헬퍼)
        // ✅ [2026-07-16 수정] 반환값에 throttled 플래그 추가.
        //    throttled=true  → rc=-21(전송제한)로 요청 자체가 거부되어 값이 없음 (qtySum 등은 무의미한 0)
        //    throttled=false → 정상 응답을 받아 완료 (qtySum=0이어도 실제 조회된 값)
        // =========================================================
        private Task<(long qtySum, double pnlSum, double mamtSum, bool throttled)> QueryT0424SumOnceAsync(
            string actNo,
            string pwd,
            TimeSpan timeout)
        {
            return RunOnStaThread<(long, double, double, bool)>(() =>
            {
                XAQueryClass q = null;
                AutoResetEvent done = null;

                string lastErr = null;
                long qtySum = 0;
                double pnlSum = 0.0;
                double mamtSum = 0.0;
                string cts = "";

                _IXAQueryEvents_ReceiveDataEventHandler onData = null;
                _IXAQueryEvents_ReceiveMessageEventHandler onMsg = null;

                try
                {
                    q = new XAQueryClass();
                    done = new AutoResetEvent(false);

                    q.LoadFromResFile(@"C:\LS_SEC\xingAPI\res\t0424.res");

                    onData = (string trCode) =>
                    {
                        try
                        {
                            _log("[T0424] ReceiveData 진입");

                            int cnt = 0;
                            try { cnt = q.GetBlockCount("t0424OutBlock1"); } catch { cnt = 0; }
                            _log($"[T0424] GetBlockCount={cnt}");

                            string accountDtsunikRaw = "";
                            try { accountDtsunikRaw = q.GetFieldData("t0424OutBlock", "dtsunik", 0); } catch { }
                            _log($"[T0424] account dtsunikRaw='{accountDtsunikRaw}'");
                            if (TryParseDouble(accountDtsunikRaw, out var accountDtsunik))
                                pnlSum = accountDtsunik;

                            for (int i = 0; i < cnt; i++)
                            {
                                string janqtyRaw = q.GetFieldData("t0424OutBlock1", "janqty", i);
                                _log($"[T0424] row[{i}] janqtyRaw='{janqtyRaw}'");

                                if (TryParseLong(janqtyRaw, out var jq))
                                    qtySum += jq;

                                // ✅ [2026-07-14 추가] mamt(매입금액) 합산
                                // 종목별 총 매입원가. UI의 "밴드 잔여 배정금" 계산에
                                // 우리 DB(qty × 진짜산가격) 대신 Xing 원본값을 그대로 쓰기 위함.
                                string mamtRaw = q.GetFieldData("t0424OutBlock1", "mamt", i);
                                _log($"[T0424] row[{i}] mamtRaw='{mamtRaw}'");

                                if (TryParseDouble(mamtRaw, out var mamt))
                                    mamtSum += mamt;
                            }

                            try { cts = (q.GetFieldData("t0424OutBlock", "cts_expcode", 0) ?? "").Trim(); } catch { }
                            _log($"[T0424] cts_expcode='{cts}' qtySum_sofar={qtySum}");
                        }
                        catch (Exception ex)
                        {
                            lastErr = ex.Message;
                            _log("[T0424] ReceiveData ERROR: " + ex.Message);
                        }
                        finally
                        {
                            try { done.Set(); } catch { }
                        }
                    };

                    onMsg = (bool isSysErr, string code, string msg) =>
                    {
                        try { _log($"[t0424 MSG] sysErr={isSysErr} code={code} msg={msg}"); }
                        catch { }

                        if (isSysErr)
                            lastErr = $"[{code}] {msg}";
                    };

                    q.ReceiveData += onData;
                    q.ReceiveMessage += onMsg;

                    SetT0424InBlock(q, actNo, pwd, cts_expcode: "");

                    _log("[T0424] Request START");
                    int r = q.Request(false);
                    _log($"[T0424] Request ret={r}");

                    if (r < 0)
                    {
                        if (r == -21)
                        {
                            _log("t0424 THROTTLED rc=-21 (전송제한) -> 값 없음 (throttled=true)");
                            return (qtySum, pnlSum, mamtSum, true);
                        }
                        throw new Exception("t0424 요청 실패: " + r);
                    }

                    bool signaled = PumpWait(done, timeout);
                    if (!signaled)
                        throw new TimeoutException("t0424 첫 응답 타임아웃");

                    if (!string.IsNullOrEmpty(lastErr))
                        throw new Exception("t0424 오류: " + lastErr);

                    // 연속조회
                    while (q.IsNext)
                    {
                        done.Reset();
                        SetT0424InBlock(q, actNo, pwd, cts_expcode: cts);

                        _log("[T0424] 연속조회 Request START");
                        int r2 = q.Request(true);
                        _log($"[T0424] 연속조회 Request ret={r2}");

                        if (r2 < 0)
                        {
                            if (r2 == -21)
                            {
                                _log("t0424 NEXT THROTTLED rc=-21 (전송제한) -> stop loop");
                                break;
                            }
                            throw new Exception("t0424 연속요청 실패: " + r2);
                        }

                        signaled = PumpWait(done, timeout);
                        if (!signaled)
                            throw new TimeoutException("t0424 연속 응답 타임아웃");

                        if (!string.IsNullOrEmpty(lastErr))
                            throw new Exception("t0424 오류: " + lastErr);
                    }

                    _log($"t0424 OK qtySum={qtySum} pnlSum={pnlSum} mamtSum={mamtSum}");
                    return (qtySum, pnlSum, mamtSum, false);
                }
                finally
                {
                    try { if (q != null && onData != null) q.ReceiveData -= onData; } catch { }
                    try { if (q != null && onMsg != null) q.ReceiveMessage -= onMsg; } catch { }
                    try { done?.Dispose(); } catch { }
                    try { if (q != null) Marshal.FinalReleaseComObject(q); } catch { }
                }
            });
        }

        public Task<long?> GetTodayRealizedPnlAsync(
            string actNo,
            string pwd,
            TimeSpan timeout)
        {
            if (string.IsNullOrWhiteSpace(actNo))
            {
                _log("[T0424][DTSUNIK] skip: actNo is empty");
                return Task.FromResult<long?>(null);
            }

            if (pwd == null) pwd = "";

            return QueryT0424TodayRealizedPnlOnceAsync(actNo, pwd, timeout);
        }

        public long? GetTodayRealizedPnl(
            string actNo,
            string pwd,
            TimeSpan timeout)
        {
            try
            {
                return GetTodayRealizedPnlAsync(actNo, pwd, timeout)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                _log("[T0424][DTSUNIK] sync wrapper failed: " + ex.Message);
                return null;
            }
        }

        private Task<long?> QueryT0424TodayRealizedPnlOnceAsync(
            string actNo,
            string pwd,
            TimeSpan timeout)
        {
            return RunOnStaThread<long?>(() =>
            {
                XAQueryClass q = null;
                AutoResetEvent done = null;

                string lastErr = null;
                long dtsunik = 0;
                bool parsed = false;

                _IXAQueryEvents_ReceiveDataEventHandler onData = null;
                _IXAQueryEvents_ReceiveMessageEventHandler onMsg = null;

                try
                {
                    q = new XAQueryClass();
                    done = new AutoResetEvent(false);

                    q.LoadFromResFile(@"C:\LS_SEC\xingAPI\res\t0424.res");

                    onData = (string trCode) =>
                    {
                        try
                        {
                            string raw = q.GetFieldData("t0424OutBlock", "dtsunik", 0);
                            _log($"[T0424][DTSUNIK] OutBlock.dtsunik raw='{raw}'");

                            if (TryParseLong(raw, out var value))
                            {
                                dtsunik = value;
                                parsed = true;
                            }
                            else
                            {
                                lastErr = "t0424 OutBlock.dtsunik parse failed";
                                _log("[T0424][DTSUNIK] parse failed");
                            }
                        }
                        catch (Exception ex)
                        {
                            lastErr = ex.Message;
                            _log("[T0424][DTSUNIK] ReceiveData ERROR: " + ex.Message);
                        }
                        finally
                        {
                            try { done.Set(); } catch { }
                        }
                    };

                    onMsg = (bool isSysErr, string code, string msg) =>
                    {
                        try { _log($"[t0424 DTSUNIK MSG] sysErr={isSysErr} code={code} msg={msg}"); }
                        catch { }

                        if (isSysErr)
                            lastErr = $"[{code}] {msg}";
                    };

                    q.ReceiveData += onData;
                    q.ReceiveMessage += onMsg;

                    SetT0424InBlock(q, actNo, pwd, cts_expcode: "");

                    _log("[T0424][DTSUNIK] Request START");
                    int r = q.Request(false);
                    _log($"[T0424][DTSUNIK] Request ret={r}");

                    if (r < 0)
                    {
                        _log("[T0424][DTSUNIK] request failed ret=" + r);
                        return null;
                    }

                    bool signaled = PumpWait(done, timeout);
                    if (!signaled)
                    {
                        _log("[T0424][DTSUNIK] timeout");
                        return null;
                    }

                    if (!string.IsNullOrEmpty(lastErr))
                    {
                        _log("[T0424][DTSUNIK] failed: " + lastErr);
                        return null;
                    }

                    if (!parsed)
                    {
                        _log("[T0424][DTSUNIK] failed: empty result");
                        return null;
                    }

                    _log("[T0424][DTSUNIK] OK dtsunik=" + dtsunik);
                    return dtsunik;
                }
                catch (Exception ex)
                {
                    _log("[T0424][DTSUNIK] exception: " + ex.Message);
                    return null;
                }
                finally
                {
                    try { if (q != null && onData != null) q.ReceiveData -= onData; } catch { }
                    try { if (q != null && onMsg != null) q.ReceiveMessage -= onMsg; } catch { }
                    try { done?.Dispose(); } catch { }
                    try { if (q != null) Marshal.FinalReleaseComObject(q); } catch { }
                }
            });
        }

        // =========================================================
        // ✅ STA 스레드에서 메시지를 펌핑하며 AutoResetEvent 를 기다린다.
        // =========================================================
        private static bool PumpWait(AutoResetEvent ev, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                if (ev.WaitOne(0))
                    return true;

                try { System.Windows.Forms.Application.DoEvents(); } catch { }
                Thread.Sleep(5);
            }
            return ev.WaitOne(0);
        }

        private static void SetT0424InBlock(XAQueryClass q, string actNo, string pwd, string cts_expcode)
        {
            q.SetFieldData("t0424InBlock", "accno", 0, actNo);
            q.SetFieldData("t0424InBlock", "passwd", 0, pwd);
            q.SetFieldData("t0424InBlock", "prcgb", 0, "1");
            q.SetFieldData("t0424InBlock", "chegb", 0, "0");
            q.SetFieldData("t0424InBlock", "dangb", 0, "0");
            q.SetFieldData("t0424InBlock", "charge", 0, "1");
            q.SetFieldData("t0424InBlock", "cts_expcode", 0, cts_expcode ?? "");
        }

        /// <summary>
        /// 편의 메서드: CSPAQ12200 + t0424 를 순서대로 한 번에 호출
        /// </summary>
        public async Task<(double cash, double d2, long qtySum, double pnlSum, double mamtSum, bool confirmed)> QueryAllAsync(
            string actNo,
            string pwd,
            TimeSpan timeoutCspaq12200,
            TimeSpan timeoutT0424,
            bool retryT0424IfZero = true,
            int maxRetry = 2,
            int retryDelayMs = 1500)
        {
            var (cash, d2) = await QueryCspaq12200Async(
                actNo,
                pwd,
                timeoutCspaq12200,
                "0900_QUERY_ALL",
                "0900.QueryAllAsync").ConfigureAwait(false);
            var (qtySum, pnlSum, mamtSum, confirmed) = await QueryT0424SumAsync(
                actNo, pwd, timeoutT0424,
                retryIfZero: retryT0424IfZero,
                maxRetry: maxRetry,
                retryDelayMs: retryDelayMs
            ).ConfigureAwait(false);
            return (cash, d2, qtySum, pnlSum, mamtSum, confirmed);
        }

        public Task<(long qtySum, double pnlSum, double mamtSum, bool confirmed)> QueryT0424OnlyAsync(
            string actNo,
            string pwd,
            TimeSpan timeout,
            bool retryT0424IfZero = true,
            int maxRetry = 2,
            int retryDelayMs = 1500)
        {
            return QueryT0424SumAsync(
                actNo,
                pwd,
                timeout,
                retryIfZero: retryT0424IfZero,
                maxRetry: maxRetry,
                retryDelayMs: retryDelayMs);
        }

        private double ToDouble(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            var t = (s ?? "").Trim().Replace(",", "");
            double.TryParse(t, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v);
            return v;
        }

        private static bool TryParseLong(string s, out long v)
        {
            v = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var t = (s ?? "").Trim().Replace(",", "");
            return long.TryParse(t, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out v);
        }

        private static bool TryParseDouble(string s, out double v)
        {
            v = 0.0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var t = (s ?? "").Trim().Replace(",", "");
            return double.TryParse(t, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out v);
        }
    }
}

// 2026-05-14 fix: Task.Run(MTA) -> RunOnStaThread(STA) + PumpWait + 상세로그
// 2026-05-14 v2: janqty=0 방어 재조회 로직 추가
//   - QueryT0424SumAsync: retryIfZero=true 이면 qtySum=0 결과에서 최대 2회 재조회
//   - QueryT0424SumOnceAsync: 단독 1회 조회 내부 헬퍼로 분리
//   - QueryAllAsync: retryT0424IfZero / maxRetry / retryDelayMs 파라미터 추가
