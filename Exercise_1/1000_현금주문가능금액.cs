// 1000_현금주문가능금액.cs  (복붙용 / C# 7.3)
// ------------------------------------------------------------
// 역할(최종 안정판):
// - CSPAQ12200 조회로 "현금주문가능금액(MnyOrdAbleAmt)"을 가져온다.
// - 버튼용: Request(acnt,pwd) 호출 시 MessageBox 표시
// - 자동매매용: RequestAsync(acnt,pwd,timeoutMs,showMessageBox)
//   -> BUY 직전 즉시 조회 후 long(주문가능금액) 반환
//
// 핵심 수정:
// - XAQuery SetFieldData / Request 를 반드시 UI thread에서 실행
// - rc<0 즉시 실패를 예외로 반환
// - timeout / system error / parse error 를 명확히 분리
// - 원본 public API 유지
//
// 전제:
// - XING 로그인(세션 연결) 이후 호출
// - Res 파일 경로: @"C:\LS_SEC\xingAPI\Res\CSPAQ12200.res"
// ------------------------------------------------------------

using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using XA_DATASETLib;

namespace Exercise_1
{
    public enum OrderableCashResultKind
    {
        Success = 0,
        ActualZeroCash = 1,
        QueryFailed = 2,
        Timeout = 3,
        RateLimited = 4,
        NotLoggedIn = 5,
        ParseFailed = 6,
        Exception = 7
    }

    public sealed class OrderableCashQueryResult
    {
        public OrderableCashResultKind Kind { get; private set; }
        public long OrderableCash { get; private set; }
        public long RcvblUablOrdAbleAmt { get; private set; }
        public long MgnRat100OrdAbleAmt { get; private set; }
        public long MgnRat100pctOrdAbleAmt { get; private set; }
        public long Dps { get; private set; }
        public long D1Dps { get; private set; }
        public long D2Dps { get; private set; }
        public int Rc { get; private set; }
        public string Message { get; private set; }

        public bool CanCalculateBuyQty
        {
            get { return Kind == OrderableCashResultKind.Success || Kind == OrderableCashResultKind.ActualZeroCash; }
        }

        public static OrderableCashQueryResult From(
            OrderableCashResultKind kind,
            long orderableCash,
            int rc = 0,
            string message = "",
            long dps = 0,
            long d1Dps = 0,
            long d2Dps = 0,
            long rcvblUablOrdAbleAmt = 0,
            long mgnRat100OrdAbleAmt = 0,
            long mgnRat100pctOrdAbleAmt = 0)
        {
            return new OrderableCashQueryResult
            {
                Kind = kind,
                OrderableCash = orderableCash < 0 ? 0 : orderableCash,
                RcvblUablOrdAbleAmt = rcvblUablOrdAbleAmt,
                MgnRat100OrdAbleAmt = mgnRat100OrdAbleAmt,
                MgnRat100pctOrdAbleAmt = mgnRat100pctOrdAbleAmt,
                Dps = dps,
                D1Dps = d1Dps,
                D2Dps = d2Dps,
                Rc = rc,
                Message = message ?? ""
            };
        }
    }

    public sealed class _1000_현금주문가능금액 : IDisposable
    {
        private const string RES_PATH = @"C:\LS_SEC\xingAPI\Res\CSPAQ12200.res";

        private readonly object _lock = new object();

        private XAQueryClass _q;
        private bool _inFlight;
        private bool _disposed;

        private TaskCompletionSource<long> _tcs;
        private bool _showMsgForThisRequest;

        /// <summary>
        /// 마지막으로 성공 수신한 주문가능금액
        /// </summary>
        public long LastOrderableCash { get; private set; }
        public long LastRcvblUablOrdAbleAmt { get; private set; }
        public long LastMgnRat100OrdAbleAmt { get; private set; }
        public long LastMgnRat100pctOrdAbleAmt { get; private set; }
        public long LastDps { get; private set; }
        public long LastD1Dps { get; private set; }
        public long LastD2Dps { get; private set; }

        /// <summary>
        /// 마지막 성공 조회 결과 전체 (0500 캐시 재사용용)
        /// </summary>
        public OrderableCashQueryResult LastResult { get; private set; }

        /// <summary>
        /// LastResult 가 저장된 시각 (UTC)
        /// </summary>
        public DateTime LastResultAt { get; private set; } = DateTime.MinValue;

        public _1000_현금주문가능금액()
        {
            CreateQueryOnUiThread();
        }

        /// <summary>
        /// 버튼용 호출
        /// </summary>
        public void Request(string acntNo, string pwd)
        {
            _ = RequestAsync(acntNo, pwd, timeoutMs: 2000, showMessageBox: true);
        }

        /// <summary>
        /// 자동매매용 호출
        /// </summary>
        public async Task<long> RequestAsync(string acntNo, string pwd, int timeoutMs = 1500, bool showMessageBox = false)
        {
            return await RequestAsync(acntNo, pwd, timeoutMs, showMessageBox, "RequestAsync", "1000").ConfigureAwait(false);
        }

        public async Task<long> RequestAsync(string acntNo, string pwd, int timeoutMs, bool showMessageBox, string reason, string caller)
        {
            var result = await RequestDetailedAsync(acntNo, pwd, timeoutMs, showMessageBox, reason, caller).ConfigureAwait(false);
            if (result.Kind == OrderableCashResultKind.Success ||
                result.Kind == OrderableCashResultKind.ActualZeroCash)
            {
                return result.OrderableCash;
            }

            throw new Exception(result.Message);
        }

        public async Task<OrderableCashQueryResult> RequestDetailedAsync(string acntNo, string pwd, int timeoutMs = 1500, bool showMessageBox = false)
        {
            return await RequestDetailedAsync(acntNo, pwd, timeoutMs, showMessageBox, "RequestDetailedAsync", "1000").ConfigureAwait(false);
        }

        public async Task<OrderableCashQueryResult> RequestDetailedAsync(string acntNo, string pwd, int timeoutMs, bool showMessageBox, string reason, string caller)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(_1000_현금주문가능금액));

            acntNo = SafeTrim(acntNo);
            pwd = SafeTrim(pwd);

            if (string.IsNullOrWhiteSpace(acntNo))
                throw new ArgumentException("AcntNo(계좌번호)가 비어 있습니다.", nameof(acntNo));

            if (string.IsNullOrWhiteSpace(pwd))
                throw new ArgumentException("Pwd(계좌비밀번호)가 비어 있습니다.", nameof(pwd));

            if (timeoutMs <= 0)
                timeoutMs = 1500;

            bool bypassThrottle = string.Equals((reason ?? "").Trim(), "BUTTON7_MANUAL", StringComparison.OrdinalIgnoreCase);
            var gate = Cspaq12200GlobalGate.TryEnter(reason, caller, bypassThrottle);
            if (!gate.Allowed)
            {
                if (Cspaq12200GlobalGate.IsRateLimitSkip(gate))
                    return OrderableCashQueryResult.From(
                        OrderableCashResultKind.RateLimited,
                        0,
                        -21,
                        Cspaq12200GlobalGate.SkipMessage(gate));

                return OrderableCashQueryResult.From(
                    OrderableCashResultKind.QueryFailed,
                    0,
                    0,
                    Cspaq12200GlobalGate.SkipMessage(gate));
            }

            Task<long> task;

            try
            {
                try
                {
                    lock (_lock)
                    {
                        if (_inFlight)
                            return OrderableCashQueryResult.From(
                                OrderableCashResultKind.QueryFailed,
                                0,
                                0,
                                "이미 CSPAQ12200 조회 요청이 진행 중입니다.");

                        _inFlight = true;
                        _showMsgForThisRequest = showMessageBox;
                        _tcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
                        task = _tcs.Task;
                    }

                    EnsureQueryAlive();

                    int rc = InvokeOnUiThread(() =>
                    {
                        _q.SetFieldData("CSPAQ12200InBlock1", "RecCnt", 0, "00001");
                        _q.SetFieldData("CSPAQ12200InBlock1", "AcntNo", 0, acntNo);
                        _q.SetFieldData("CSPAQ12200InBlock1", "Pwd", 0, pwd);
                        _q.SetFieldData("CSPAQ12200InBlock1", "BalCreTp", 0, "0");

                        return _q.Request(false);
                    });

                    Write("[1000][REQ] rc=" + rc + " acntLen=" + acntNo.Length + " pwdLen=" + pwd.Length);
                    gate.ReportResult(rc);

                    if (rc < 0)
                    {
                        var kind = rc == -21 ? OrderableCashResultKind.RateLimited : OrderableCashResultKind.QueryFailed;
                        CompleteWithException(new Exception("CSPAQ12200 Request 실패 (rc=" + rc + ")"), showMessageBox);
                        return OrderableCashQueryResult.From(kind, 0, rc, "CSPAQ12200 Request 실패 (rc=" + rc + ")");
                    }
                }
                catch (Exception ex)
                {
                    CompleteWithException(ex, showMessageBox);
                    return OrderableCashResultFromException(ex);
                }

                using (var cts = new CancellationTokenSource())
                {
                    Task delayTask = Task.Delay(timeoutMs, cts.Token);

                    Task completed = await Task.WhenAny(task, delayTask).ConfigureAwait(false);
                    if (completed == delayTask)
                    {
                        TimeoutRequest(timeoutMs, showMessageBox);
                        return OrderableCashQueryResult.From(
                            OrderableCashResultKind.Timeout,
                            0,
                            0,
                            "CSPAQ12200 timeout (" + timeoutMs + "ms)");
                    }
                    else
                    {
                        cts.Cancel();
                    }
                }

                try
                {
                    long cash = await task.ConfigureAwait(false);
                    if (cash > 0)
                        return OrderableCashQueryResult.From(
                            OrderableCashResultKind.Success, cash, 0, "",
                            LastDps, LastD1Dps, LastD2Dps,
                            LastRcvblUablOrdAbleAmt, LastMgnRat100OrdAbleAmt, LastMgnRat100pctOrdAbleAmt);

                    return OrderableCashQueryResult.From(
                        OrderableCashResultKind.ActualZeroCash, 0, 0, "",
                        LastDps, LastD1Dps, LastD2Dps,
                        LastRcvblUablOrdAbleAmt, LastMgnRat100OrdAbleAmt, LastMgnRat100pctOrdAbleAmt);
                }
                catch (Exception ex)
                {
                    return OrderableCashResultFromException(ex);
                }
            }
            finally
            {
                gate.Dispose();
            }
        }

        private static OrderableCashQueryResult OrderableCashResultFromException(Exception ex)
        {
            string msg = ex != null ? (ex.Message ?? "") : "";
            if (ex is TimeoutException || msg.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0)
                return OrderableCashQueryResult.From(OrderableCashResultKind.Timeout, 0, 0, msg);

            if (msg.IndexOf("rc=-21", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("-21", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("전송제한", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Cspaq12200GlobalGate.ReportRateLimited(-21);
                return OrderableCashQueryResult.From(OrderableCashResultKind.RateLimited, 0, -21, msg);
            }

            if (msg.IndexOf("parse", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("파싱", StringComparison.OrdinalIgnoreCase) >= 0)
                return OrderableCashQueryResult.From(OrderableCashResultKind.ParseFailed, 0, 0, msg);

            return OrderableCashQueryResult.From(OrderableCashResultKind.Exception, 0, 0, msg);
        }

        private void OnReceiveData(string trCode)
        {
            TaskCompletionSource<long> tcsLocal = null;
            bool showMsg = false;
            long able = 0;

            try
            {
                string raw = SafeGetField("CSPAQ12200OutBlock2", "MnyOrdAbleAmt", 0);
                long parsed;
                if (!TryParseLong(raw, out parsed))
                    throw new FormatException("MnyOrdAbleAmt parse failed raw='" + raw + "'");

                able = parsed;

                LastOrderableCash = able;
                LastRcvblUablOrdAbleAmt = ParseLongOrZero(SafeGetField("CSPAQ12200OutBlock2", "RcvblUablOrdAbleAmt", 0));
                LastMgnRat100OrdAbleAmt = ParseLongOrZero(SafeGetField("CSPAQ12200OutBlock2", "MgnRat100OrdAbleAmt", 0));
                LastMgnRat100pctOrdAbleAmt = ParseLongOrZero(SafeGetField("CSPAQ12200OutBlock2", "MgnRat100pctOrdAbleAmt", 0));
                LastDps = ParseLongOrZero(SafeGetField("CSPAQ12200OutBlock2", "Dps", 0));
                LastD1Dps = ParseLongOrZero(SafeGetField("CSPAQ12200OutBlock2", "D1Dps", 0));
                LastD2Dps = ParseLongOrZero(SafeGetField("CSPAQ12200OutBlock2", "D2Dps", 0));

                // ✅ [BUG-FIX] 전역 캐시 갱신 (2160/2310 TR 실패 폴백용)
                if (able > 0)
                {
                    try { Login.LastKnownOrderableCash = able; } catch { }
                }

                // ✅ [BUG-FIX] 0500 재조회 Throttle 방지용 캐시 저장
                // OnReceiveData는 성공 수신 경로이므로 여기서 저장하면 항상 신선한 값이 보장된다.
                try
                {
                    var cached = OrderableCashQueryResult.From(
                        able > 0 ? OrderableCashResultKind.Success : OrderableCashResultKind.ActualZeroCash,
                        able, 0, "CACHED",
                        LastDps, LastD1Dps, LastD2Dps,
                        LastRcvblUablOrdAbleAmt, LastMgnRat100OrdAbleAmt, LastMgnRat100pctOrdAbleAmt);
                    LastResult   = cached;
                    LastResultAt = DateTime.UtcNow;
                }
                catch { }

                lock (_lock)
                {
                    tcsLocal = _tcs;
                    _tcs = null;
                    showMsg = _showMsgForThisRequest;
                    _inFlight = false;
                }

                Write("[1000][DATA] tr=" + trCode + " MnyOrdAbleAmtRaw='" + raw + "' parsed=" + able +
                      " RcvblUablOrdAbleAmt=" + LastRcvblUablOrdAbleAmt +
                      " MgnRat100OrdAbleAmt=" + LastMgnRat100OrdAbleAmt +
                      " MgnRat100pctOrdAbleAmt=" + LastMgnRat100pctOrdAbleAmt +
                      " Dps=" + LastDps + " D1Dps=" + LastD1Dps + " D2Dps=" + LastD2Dps);

                tcsLocal?.TrySetResult(able);

                if (showMsg)
                {
                    ShowMessageOnUiThread(
                        "현금주문가능금액(MnyOrdAbleAmt)\r\n\r\n" + able.ToString("N0") + " 원",
                        "현금주문가능금액",
                        MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    tcsLocal = _tcs;
                    _tcs = null;
                    showMsg = _showMsgForThisRequest;
                    _inFlight = false;
                }

                Write("[1000][DATA][EX] " + ex.Message);

                tcsLocal?.TrySetException(ex);

                if (showMsg)
                {
                    ShowMessageOnUiThread(
                        "수신 처리 예외:\r\n" + ex.Message,
                        "현금주문가능금액",
                        MessageBoxIcon.Error);
                }
            }
        }

        private void OnReceiveMessage(bool bIsSystemError, string nMessageCode, string szMessage)
        {
            Write("[1000][MSG] sysErr=" + bIsSystemError + " code=" + nMessageCode + " msg=" + szMessage);

            if (!bIsSystemError) return;

            TaskCompletionSource<long> tcsLocal = null;
            bool showMsg = false;

            lock (_lock)
            {
                tcsLocal = _tcs;
                _tcs = null;
                showMsg = _showMsgForThisRequest;
                _inFlight = false;
            }

            Exception ex = new Exception("[CSPAQ12200 MSG] code=" + nMessageCode + " msg=" + szMessage);
            tcsLocal?.TrySetException(ex);

            if (showMsg)
            {
                ShowMessageOnUiThread(
                    ex.Message,
                    "현금주문가능금액",
                    MessageBoxIcon.Error);
            }
        }

        private void CreateQueryOnUiThread()
        {
            InvokeOnUiThread(() =>
            {
                if (_q != null) return;

                _q = new XAQueryClass();
                _q.LoadFromResFile(RES_PATH);
                _q.ReceiveData += OnReceiveData;
                _q.ReceiveMessage += OnReceiveMessage;

                Write("[1000][INIT] XAQuery ready");
            });
        }

        private void EnsureQueryAlive()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(_1000_현금주문가능금액));

            if (_q == null)
                CreateQueryOnUiThread();
        }

        private void CompleteWithException(Exception ex, bool showMessageBox)
        {
            TaskCompletionSource<long> tcsLocal = null;

            lock (_lock)
            {
                tcsLocal = _tcs;
                _tcs = null;
                _inFlight = false;
            }

            Write("[1000][EX] " + ex.Message);

            tcsLocal?.TrySetException(ex);

            if (showMessageBox)
            {
                ShowMessageOnUiThread(
                    ex.Message,
                    "현금주문가능금액",
                    MessageBoxIcon.Error);
            }
        }

        private void TimeoutRequest(int timeoutMs, bool showMessageBox)
        {
            TaskCompletionSource<long> tcsLocal = null;

            lock (_lock)
            {
                _inFlight = false;
                tcsLocal = _tcs;
                _tcs = null;
            }

            var ex = new TimeoutException("CSPAQ12200 timeout (" + timeoutMs + "ms)");
            Write("[1000][TIMEOUT] " + ex.Message);

            tcsLocal?.TrySetException(ex);

            if (showMessageBox)
            {
                ShowMessageOnUiThread(
                    ex.Message,
                    "현금주문가능금액",
                    MessageBoxIcon.Error);
            }
        }

        private string SafeGetField(string block, string field, int index)
        {
            try
            {
                return (_q.GetFieldData(block, field, index) ?? "").Trim();
            }
            catch
            {
                return "";
            }
        }

        private static long ParseLong(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;

            s = s.Trim().Replace(",", "");

            long lv;
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out lv))
                return lv;

            decimal dv;
            if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out dv))
                return (long)dv;

            return 0;
        }

        private static bool TryParseLong(string s, out long v)
        {
            v = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;

            s = s.Trim().Replace(",", "");

            long lv;
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out lv))
            {
                v = lv;
                return true;
            }

            decimal dv;
            if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out dv))
            {
                v = (long)dv;
                return true;
            }

            return false;
        }

        private static long ParseLongOrZero(string s)
        {
            long value;
            return TryParseLong(s, out value) ? value : 0L;
        }

        private static string SafeTrim(string s)
        {
            return string.IsNullOrWhiteSpace(s) ? "" : s.Trim();
        }

        private static Control GetUiControl()
        {
            try
            {
                if (Application.OpenForms != null && Application.OpenForms.Count > 0)
                    return Application.OpenForms[0];
            }
            catch
            {
            }

            return null;
        }

        private static void InvokeOnUiThread(Action action)
        {
            if (action == null) return;

            Control ctl = GetUiControl();
            if (ctl == null || ctl.IsDisposed || !ctl.IsHandleCreated)
            {
                action();
                return;
            }

            if (ctl.InvokeRequired)
                ctl.Invoke(action);
            else
                action();
        }

        private static T InvokeOnUiThread<T>(Func<T> func)
        {
            if (func == null) return default(T);

            Control ctl = GetUiControl();
            if (ctl == null || ctl.IsDisposed || !ctl.IsHandleCreated)
                return func();

            if (ctl.InvokeRequired)
                return (T)ctl.Invoke(func);

            return func();
        }

        private static void ShowMessageOnUiThread(string text, string caption, MessageBoxIcon icon)
        {
            InvokeOnUiThread(() =>
            {
                MessageBox.Show(
                    text,
                    caption,
                    MessageBoxButtons.OK,
                    icon);
            });
        }

        private void Write(string msg)
        {
            try
            {
                Console.WriteLine(msg);
                Debug.WriteLine(msg);
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                InvokeOnUiThread(() =>
                {
                    if (_q != null)
                    {
                        try { _q.ReceiveData -= OnReceiveData; } catch { }
                        try { _q.ReceiveMessage -= OnReceiveMessage; } catch { }

                        try { Marshal.FinalReleaseComObject(_q); } catch { }
                        _q = null;
                    }
                });
            }
            catch
            {
            }
        }
    }
}
// 2026-03-26 48127
