// 0530_매매_Xing.cs  (복붙용 / C# 7.3)
// ------------------------------------------------------------
// 역할:
// - 실제 주문 엔진 (XING CSPAT00600)
// - price 타입: int (KRW 정수)
// - long/double 호환 오버로드 제공
// ------------------------------------------------------------

using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using XA_DATASETLib;

namespace Exercise_1
{
    public sealed class 매매_Xing : IDisposable
    {
        private readonly Func<string> _getAcntNo;
        private readonly Func<string> _getPwd4;
        private readonly Func<string> _getShcode;

        private readonly XAQueryClass _cspat00600 = new XAQueryClass();

        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        private TaskCompletionSource<long> _tcsOrdNo;
        private string _lastReceiveMessage;
        private bool _disposed;

        public event Action<string> Log;

        public 매매_Xing(
            Func<string> getAcntNo,
            Func<string> getPwd4,
            Func<string> getShcode)
        {
            _getAcntNo = getAcntNo ?? throw new ArgumentNullException(nameof(getAcntNo));
            _getPwd4 = getPwd4 ?? throw new ArgumentNullException(nameof(getPwd4));
            _getShcode = getShcode ?? throw new ArgumentNullException(nameof(getShcode));

            // ✅ 올바른 RES 경로
            _cspat00600.ResFileName = @"C:\ls_sec\xingapi\res\CSPAT00600.res";

            _cspat00600.ReceiveData += OnReceiveData;
            _cspat00600.ReceiveMessage += OnReceiveMessage;
        }

        // ------------------------------------------------------------
        // ✅ 메인 주문 함수 (price=int)
        // ------------------------------------------------------------
        public async Task<long> SendOrderLive(
            string sideKor,
            string shcode,
            int price,
            int qty,
            int band)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(매매_Xing));

            if (string.IsNullOrWhiteSpace(shcode))
                shcode = _getShcode?.Invoke();

            if (string.IsNullOrWhiteSpace(sideKor))
                throw new ArgumentNullException(nameof(sideKor));

            if (string.IsNullOrWhiteSpace(shcode))
                throw new InvalidOperationException("shcode가 비어있습니다.");

            if (price <= 0) throw new ArgumentOutOfRangeException(nameof(price));
            if (qty <= 0) throw new ArgumentOutOfRangeException(nameof(qty));

            sideKor = NormalizeSide(sideKor);

            string acnt = _getAcntNo()?.Trim();
            string pwd4 = _getPwd4()?.Trim();

            if (string.IsNullOrEmpty(acnt))
                throw new InvalidOperationException("계좌번호가 비어있습니다.");
            if (string.IsNullOrEmpty(pwd4))
                throw new InvalidOperationException("비밀번호 4자리가 비어있습니다.");

            string isuNo = shcode.StartsWith("A") ? shcode : "A" + shcode;

            await _sendLock.WaitAsync().ConfigureAwait(false);

            try
            {
                _tcsOrdNo = new TaskCompletionSource<long>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                _lastReceiveMessage = null;

                _cspat00600.SetFieldData("CSPAT00600InBlock1", "AcntNo", 0, acnt);
                _cspat00600.SetFieldData("CSPAT00600InBlock1", "InptPwd", 0, pwd4);
                _cspat00600.SetFieldData("CSPAT00600InBlock1", "IsuNo", 0, isuNo);

                string bnsTp = sideKor == "매수" ? "2" : "1";
                _cspat00600.SetFieldData("CSPAT00600InBlock1", "BnsTp", 0, bnsTp);

                _cspat00600.SetFieldData("CSPAT00600InBlock1", "OrdprcPtnCode", 0, "00");
                _cspat00600.SetFieldData("CSPAT00600InBlock1", "OrdQty", 0, qty.ToString());
                _cspat00600.SetFieldData("CSPAT00600InBlock1", "OrdPrc", 0, price.ToString());

                _cspat00600.SetFieldData("CSPAT00600InBlock1", "MgntrnCode", 0, "000");
                _cspat00600.SetFieldData("CSPAT00600InBlock1", "LoanDt", 0, "");

                WriteLog($"[0530.SEND] {sideKor} {qty}@{price} band={band}");

                int rq = _cspat00600.Request(false);
                if (rq < 0)
                    throw new InvalidOperationException($"Request 실패 code={rq}");

                var timeout = Task.Delay(5000);
                var done = await Task.WhenAny(_tcsOrdNo.Task, timeout);

                if (done != _tcsOrdNo.Task)
                    throw new TimeoutException("주문 응답 타임아웃");

                return await _tcsOrdNo.Task;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        // ------------------------------------------------------------
        // 호환 오버로드
        // ------------------------------------------------------------
        public Task<long> SendOrderLive(string sideKor, string shcode, long price, int qty, int band)
            => SendOrderLive(sideKor, shcode, (int)price, qty, band);

        public Task<long> SendOrderLive(string sideKor, string shcode, double price, int qty, int band)
            => SendOrderLive(sideKor, shcode, (int)Math.Round(price), qty, band);

        // ------------------------------------------------------------
        // XING 이벤트
        // ------------------------------------------------------------
        private void OnReceiveMessage(bool isSystemError, string code, string msg)
        {
            _lastReceiveMessage = msg;
            WriteLog($"[0530.MSG] {code} {msg}");
        }

        private void OnReceiveData(string trCode)
        {
            if (!string.Equals(trCode, "CSPAT00600", StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                string ordNoStr =
                    _cspat00600.GetFieldData("CSPAT00600OutBlock2", "OrdNo", 0)?.Trim();

                if (!long.TryParse(ordNoStr, out long ordNo))
                    throw new Exception("OrdNo 파싱 실패");

                _tcsOrdNo?.TrySetResult(ordNo);
            }
            catch (Exception ex)
            {
                _tcsOrdNo?.TrySetException(ex);
            }
        }

        private static string NormalizeSide(string side)
        {
            if (string.Equals(side, "BUY", StringComparison.OrdinalIgnoreCase)) return "매수";
            if (string.Equals(side, "SELL", StringComparison.OrdinalIgnoreCase)) return "매도";
            return side;
        }

        private void WriteLog(string msg)
        {
            Debug.WriteLine(msg);
            Log?.Invoke(msg);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _cspat00600.ReceiveData -= OnReceiveData;
            _cspat00600.ReceiveMessage -= OnReceiveMessage;
            _sendLock.Dispose();
        }
    }
}

// 2026-02-15 77126
