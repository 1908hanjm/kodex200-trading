// 0900_banance_cspaq12200_t0424.cs  (복붙용 / C# 7.3)
// 역할:
// - CSPAQ12200: 주문가능금액/예수금(D2 등) 조회
// - t0424: 보유수량 합계(qtySum) + 당일손익 합계(pnlSum) 조회(연속조회 IsNext 처리)
// - Login.cs에서 잔고/손익 TR 코드를 분리하기 위한 전용 모듈
//
// ✅ 중요(수정 포인트):
// - 이벤트 핸들러는 XA_DATASETLib._IXAQueryEvents_... 델리게이트 타입으로 "명시"해서 구독한다.
// - 구독한 동일 delegate 인스턴스로 반드시 unsubscribe 한다.
// - t0424는 OutBlock1 다건이므로 row 전체 합산한다.
// - 연속조회 시 cts_expcode를 OutBlock에서 읽어 다음 요청 InBlock에 반영한다.
//
// 주의:
// - XING 로그인/세션은 0100_Xing_connect.cs에서 완료되어 있어야 합니다.
// - 이 모듈은 TR 호출만 담당(세션 관리/로그인 책임 없음).
// - RES 경로는 기존 프로젝트 경로를 그대로 사용합니다.

using System;
using System.Diagnostics;
using System.Globalization;
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

        /// <summary>
        /// CSPAQ12200: 주문가능금액/예수금(D2 등) 조회
        /// </summary>
        public Task<(double cash, double d2)> QueryCspaq12200Async(
            string actNo,
            string pwd,
            TimeSpan timeout)
        {
            if (string.IsNullOrWhiteSpace(actNo))
                throw new ArgumentException("actNo is empty", nameof(actNo));

            if (pwd == null) pwd = "";

            return Task.Run(() =>
            {
                var q = new XAQueryClass();
                var done = new AutoResetEvent(false);

                string lastErr = null;

                // ✅ 반드시 이 타입으로 구독해야 함 (Action<> 금지)
                _IXAQueryEvents_ReceiveDataEventHandler onData = null;
                _IXAQueryEvents_ReceiveMessageEventHandler onMsg = null;

                try
                {
                    q.LoadFromResFile(@"C:\LS_SEC\xingAPI\res\CSPAQ12200.res");

                    onData = (string trCode) =>
                    {
                        done.Set();
                    };

                    onMsg = (bool isSysErr, string code, string msg) =>
                    {
                        if (isSysErr)
                            lastErr = $"[{code}] {msg}";
                    };

                    q.ReceiveData += onData;
                    q.ReceiveMessage += onMsg;

                    // inblock
                    q.SetFieldData("CSPAQ12200InBlock1", "RecCnt", 0, "1");
                    q.SetFieldData("CSPAQ12200InBlock1", "MgmtBrnNo", 0, "");
                    q.SetFieldData("CSPAQ12200InBlock1", "AcntNo", 0, actNo);
                    q.SetFieldData("CSPAQ12200InBlock1", "Pwd", 0, pwd);
                    q.SetFieldData("CSPAQ12200InBlock1", "BalCreTp", 0, "0");

                    int r = q.Request(false);
                    if (r < 0) throw new Exception("CSPAQ12200 TR 요청 실패: " + r);

                    if (!done.WaitOne(timeout)) throw new TimeoutException("CSPAQ12200 응답 타임아웃");
                    if (!string.IsNullOrEmpty(lastErr)) throw new Exception("CSPAQ12200 오류: " + lastErr);

                    double cash = ToDouble(q.GetFieldData("CSPAQ12200OutBlock2", "MnyOrdAbleAmt", 0));
                    double d2 = ToDouble(q.GetFieldData("CSPAQ12200OutBlock2", "DpsastTotamt", 0));

                    _log($"CSPAQ12200 OK cash={cash} d2={d2}");
                    return (cash, d2);
                }
                finally
                {
                    try { if (onData != null) q.ReceiveData -= onData; } catch { }
                    try { if (onMsg != null) q.ReceiveMessage -= onMsg; } catch { }
                    try { done.Dispose(); } catch { }
                }

                double ToDouble(string s)
                {
                    if (string.IsNullOrWhiteSpace(s)) return 0;
                    double.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v);
                    return v;
                }
            });
        }

        /// <summary>
        /// t0424: 보유수량 합계(qtySum) + 당일손익 합계(pnlSum) 조회
        /// - 연속조회(q.IsNext) 지원
        /// </summary>
        public Task<(long qtySum, double pnlSum)> QueryT0424SumAsync(
            string actNo,
            string pwd,
            TimeSpan timeout)
        {
            if (string.IsNullOrWhiteSpace(actNo))
                throw new ArgumentException("actNo is empty", nameof(actNo));

            if (pwd == null) pwd = "";

            return Task.Run<(long qtySum, double pnlSum)>(() =>
            {
                var q = new XAQueryClass();
                var done = new AutoResetEvent(false);

                string lastErr = null;
                long qtySum = 0;
                double pnlSum = 0.0;

                // 연속조회 커서
                string cts = "";

                _IXAQueryEvents_ReceiveDataEventHandler onData = null;
                _IXAQueryEvents_ReceiveMessageEventHandler onMsg = null;

                try
                {
                    q.LoadFromResFile(@"C:\LS_SEC\xingAPI\res\t0424.res");

                    onData = (string trCode) =>
                    {
                        try
                        {
                            // ✅ OutBlock1 다건 합산
                            int cnt = 0;
                            try { cnt = q.GetBlockCount("t0424OutBlock1"); } catch { cnt = 0; }
                            if (cnt <= 0) cnt = 1;

                            for (int i = 0; i < cnt; i++)
                            {
                                string janqtyStr = q.GetFieldData("t0424OutBlock1", "janqty", i);
                                string dtsunikStr = q.GetFieldData("t0424OutBlock1", "dtsunik", i);

                                if (long.TryParse((janqtyStr ?? "").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var jq))
                                    qtySum += jq;

                                if (double.TryParse((dtsunikStr ?? "").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var ds))
                                    pnlSum += ds;
                            }

                            // ✅ 연속조회 커서(cts_expcode) 갱신
                            try { cts = (q.GetFieldData("t0424OutBlock", "cts_expcode", 0) ?? "").Trim(); } catch { }
                        }
                        catch (Exception ex)
                        {
                            lastErr = ex.Message;
                        }
                        finally
                        {
                            done.Set();
                        }
                    };

                    onMsg = (bool isSysErr, string code, string msg) =>
                    {
                        if (isSysErr)
                            lastErr = $"[{code}] {msg}";
                    };

                    q.ReceiveData += onData;
                    q.ReceiveMessage += onMsg;

                    // 최초 요청
                    SetT0424InBlock(q, actNo, pwd, cts_expcode: "");

                    int r = q.Request(false);
                    if (r < 0) throw new Exception("t0424 요청 실패: " + r);

                    if (!done.WaitOne(timeout)) throw new TimeoutException("t0424 첫 응답 타임아웃");
                    if (!string.IsNullOrEmpty(lastErr)) throw new Exception("t0424 오류: " + lastErr);

                    // 연속조회
                    while (q.IsNext)
                    {
                        done.Reset();

                        // ✅ 반드시 커서 반영
                        SetT0424InBlock(q, actNo, pwd, cts_expcode: cts);

                        int r2 = q.Request(true);
                        if (r2 < 0) throw new Exception("t0424 연속요청 실패: " + r2);

                        if (!done.WaitOne(timeout)) throw new TimeoutException("t0424 연속 응답 타임아웃");
                        if (!string.IsNullOrEmpty(lastErr)) throw new Exception("t0424 오류: " + lastErr);
                    }

                    _log($"t0424 OK qtySum={qtySum} pnlSum={pnlSum}");
                    return (qtySum, pnlSum);
                }
                finally
                {
                    try { if (onData != null) q.ReceiveData -= onData; } catch { }
                    try { if (onMsg != null) q.ReceiveMessage -= onMsg; } catch { }
                    try { done.Dispose(); } catch { }
                }
            });
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
        public async Task<(double cash, double d2, long qtySum, double pnlSum)> QueryAllAsync(
            string actNo,
            string pwd,
            TimeSpan timeoutCspaq12200,
            TimeSpan timeoutT0424)
        {
            var (cash, d2) = await QueryCspaq12200Async(actNo, pwd, timeoutCspaq12200).ConfigureAwait(false);
            var (qtySum, pnlSum) = await QueryT0424SumAsync(actNo, pwd, timeoutT0424).ConfigureAwait(false);
            return (cash, d2, qtySum, pnlSum);
        }
    }
}

// 2026-01-30 27461
