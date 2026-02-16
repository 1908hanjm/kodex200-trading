// 1000_현금주문가능금액.cs  (복붙용 / C# 7.3)
// ------------------------------------------------------------
// 역할(개선판):
// - CSPAQ12200 조회로 "현금주문가능금액(MnyOrdAbleAmt)"을 가져온다.
// - ✅ 기존 기능 유지: Request(acnt,pwd) 호출 시 MessageBox로 표시(버튼용)
// - ✅ 신규 기능(자동매매용): RequestAsync(acnt,pwd,timeoutMs,showMessageBox)
//    -> BUY 직전 즉시 조회 후 long(주문가능금액) 반환
//
// 전제:
// - XING 로그인(세션 연결) 이후 호출
// - Res 파일 경로: @"C:\LS_SEC\xingAPI\Res\CSPAQ12200.res"
// ------------------------------------------------------------

using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using XA_DATASETLib;

namespace Exercise_1
{
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
        /// 마지막으로 수신한 주문가능금액 (성공 수신 시 갱신)
        /// </summary>
        public long LastOrderableCash { get; private set; }

        public _1000_현금주문가능금액()
        {
            _q = new XAQueryClass();
            _q.LoadFromResFile(RES_PATH);

            _q.ReceiveData += OnReceiveData;
            _q.ReceiveMessage += OnReceiveMessage;
        }

        /// <summary>
        /// ✅ 기존 버튼용: MessageBox 표시 포함
        /// </summary>
        public void Request(string acntNo, string pwd)
        {
            // 버튼용은 메시지박스 항상 표시
            _ = RequestAsync(acntNo, pwd, timeoutMs: 2000, showMessageBox: true);
        }

        /// <summary>
        /// ✅ 자동매매용: BUY 직전 "즉시 조회" 후 결과(long) 반환
        /// - showMessageBox=false로 호출하면 UI 중단 없음
        /// - timeoutMs 내에 ReceiveData가 안 오면 예외 발생
        /// </summary>
        public async Task<long> RequestAsync(string acntNo, string pwd, int timeoutMs = 1500, bool showMessageBox = false)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(_1000_현금주문가능금액));

            acntNo = (acntNo ?? "").Trim();
            pwd = (pwd ?? "").Trim();

            if (string.IsNullOrWhiteSpace(acntNo))
                throw new ArgumentException("AcntNo(계좌번호)가 비어 있습니다.", nameof(acntNo));
            if (string.IsNullOrWhiteSpace(pwd))
                throw new ArgumentException("Pwd(계좌비밀번호)가 비어 있습니다.", nameof(pwd));
            if (timeoutMs <= 0) timeoutMs = 1500;

            Task<long> task;

            lock (_lock)
            {
                if (_inFlight)
                    throw new InvalidOperationException("이미 CSPAQ12200 조회 요청이 진행 중입니다.");

                _inFlight = true;
                _showMsgForThisRequest = showMessageBox;

                _tcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
                task = _tcs.Task;

                try
                {
                    // CSPAQ12200InBlock1:
                    //   RecCnt    : 00001
                    //   AcntNo    : (Login에서 전달)
                    //   Pwd       : (Login에서 전달)
                    //   BalCreTp  : 0
                    _q.SetFieldData("CSPAQ12200InBlock1", "RecCnt", 0, "00001");
                    _q.SetFieldData("CSPAQ12200InBlock1", "AcntNo", 0, acntNo);
                    _q.SetFieldData("CSPAQ12200InBlock1", "Pwd", 0, pwd);
                    _q.SetFieldData("CSPAQ12200InBlock1", "BalCreTp", 0, "0");

                    int rc = _q.Request(false);
                    if (rc < 0)
                    {
                        // 즉시 실패
                        var ex = new Exception($"CSPAQ12200 Request 실패 (rc={rc})");
                        _tcs.TrySetException(ex);
                        _tcs = null;
                        _inFlight = false;

                        if (showMessageBox)
                        {
                            MessageBox.Show(
                                ex.Message,
                                "현금주문가능금액",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error
                            );
                        }
                    }
                }
                catch (Exception ex)
                {
                    _tcs.TrySetException(ex);
                    _tcs = null;
                    _inFlight = false;

                    if (showMessageBox)
                    {
                        MessageBox.Show(
                            "요청 중 예외:\r\n" + ex.Message,
                            "현금주문가능금액",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error
                        );
                    }
                }
            }

            // 타임아웃 처리
            using (var cts = new CancellationTokenSource())
            {
                var delayTask = Task.Delay(timeoutMs, cts.Token);

                var completed = await Task.WhenAny(task, delayTask).ConfigureAwait(false);
                if (completed == delayTask)
                {
                    lock (_lock)
                    {
                        // 진행 상태 정리
                        _inFlight = false;
                        if (_tcs != null)
                        {
                            _tcs.TrySetException(new TimeoutException($"CSPAQ12200 timeout ({timeoutMs}ms)"));
                            _tcs = null;
                        }
                    }
                }
                else
                {
                    cts.Cancel();
                }
            }

            // 결과/예외 전달
            return await task.ConfigureAwait(false);
        }

        private void OnReceiveData(string trCode)
        {
            TaskCompletionSource<long> tcsLocal = null;
            bool showMsg;

            long able = 0;

            try
            {
                // 일반적으로 OutBlock2의 MnyOrdAbleAmt
                string raw = SafeGetField("CSPAQ12200OutBlock2", "MnyOrdAbleAmt", 0);
                able = ParseLong(raw);

                LastOrderableCash = able;

                lock (_lock)
                {
                    tcsLocal = _tcs;
                    _tcs = null;
                    showMsg = _showMsgForThisRequest;
                    _inFlight = false;
                }

                tcsLocal?.TrySetResult(able);

                if (showMsg)
                {
                    MessageBox.Show(
                        $"현금주문가능금액(MnyOrdAbleAmt)\r\n\r\n{able:N0} 원",
                        "현금주문가능금액",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information
                    );
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

                tcsLocal?.TrySetException(ex);

                if (showMsg)
                {
                    MessageBox.Show(
                        "수신 처리 예외:\r\n" + ex.Message,
                        "현금주문가능금액",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error
                    );
                }
            }
        }

        private void OnReceiveMessage(bool bIsSystemError, string nMessageCode, string szMessage)
        {
            if (!bIsSystemError) return;

            TaskCompletionSource<long> tcsLocal = null;
            bool showMsg;

            lock (_lock)
            {
                tcsLocal = _tcs;
                _tcs = null;
                showMsg = _showMsgForThisRequest;
                _inFlight = false;
            }

            var ex = new Exception($"[CSPAQ12200 MSG] code={nMessageCode} msg={szMessage}");
            tcsLocal?.TrySetException(ex);

            if (showMsg)
            {
                MessageBox.Show(
                    ex.Message,
                    "현금주문가능금액",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
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

            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v))
                return v;

            if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal d))
                return (long)d;

            return 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_q != null)
                {
                    try { _q.ReceiveData -= OnReceiveData; } catch { }
                    try { _q.ReceiveMessage -= OnReceiveMessage; } catch { }

                    try { Marshal.FinalReleaseComObject(_q); } catch { }
                    _q = null;
                }
            }
            catch { }
        }
    }
}
// 2026-02-15 42671
