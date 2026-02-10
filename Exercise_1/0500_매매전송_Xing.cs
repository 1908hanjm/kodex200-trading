// 매매_Xing.cs  (복붙용 / C# 7.3 / 0550+0600 연동 / "주문전송확인" 통합 / SC1 제거 버전)

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
        private readonly XAQueryClass _qOrder = new XAQueryClass();
        private const string RES_ORDER = @"C:\LS_SEC\xingAPI\Res\CSPAT00600.res";

        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        private readonly Func<string> _getAcntNo;
        private readonly Func<string> _getPwd4;
        private readonly Func<string> _getShcode;

        private string _lastSideKor = "";
        private int _lastBand = 0;

        public event Action<string> Log;

        public 매매_Xing(Func<string> getAcntNo, Func<string> getPwd4, Func<string> getShcode)
        {
            _getAcntNo = getAcntNo ?? throw new ArgumentNullException(nameof(getAcntNo));
            _getPwd4 = getPwd4 ?? throw new ArgumentNullException(nameof(getPwd4));
            _getShcode = getShcode ?? throw new ArgumentNullException(nameof(getShcode));

            _qOrder.LoadFromResFile(RES_ORDER);
            _qOrder.ReceiveMessage += OnOrderMessage;

            Write("[매매_Xing] 초기화 완료 (주문 전송 + 주문전송확인 / SC1 제거 버전)");
        }

        public async Task SendOrderLive(string sideKor, string shcode, double price, int qty, int band)
        {
            if (string.IsNullOrWhiteSpace(sideKor)) throw new ArgumentNullException(nameof(sideKor));
            if (qty <= 0) throw new ArgumentOutOfRangeException(nameof(qty));
            if (price <= 0) throw new ArgumentOutOfRangeException(nameof(price));
            if (band <= 0) throw new ArgumentOutOfRangeException(nameof(band));

            sideKor = NormalizeSideKorOnly(sideKor);

            shcode = (shcode ?? "").Trim();
            if (string.IsNullOrEmpty(shcode))
                shcode = (_getShcode() ?? "").Trim();

            if (string.IsNullOrEmpty(shcode))
                throw new InvalidOperationException("shcode(종목코드)가 비었습니다.");

            string bnsTpCode = (sideKor == "매도") ? "1" : "2";

            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                _lastSideKor = sideKor;
                _lastBand = band;

                string acntNo = (_getAcntNo() ?? "").Trim();
                string pwd4 = (_getPwd4() ?? "").Trim();

                if (string.IsNullOrEmpty(acntNo)) throw new InvalidOperationException("계좌번호(getAcntNo)가 비었습니다.");
                if (string.IsNullOrEmpty(pwd4)) throw new InvalidOperationException("비번4자리(getPwd4)가 비었습니다.");

                string isuNo = shcode.Trim();
                if (!isuNo.StartsWith("A", StringComparison.OrdinalIgnoreCase))
                    isuNo = "A" + isuNo;

                string sQty = qty.ToString(CultureInfo.InvariantCulture);
                string sPrc = ((int)price).ToString(CultureInfo.InvariantCulture);

                Write("======================================");
                Write($"[ORDER SEND] side={sideKor}, band={band}, qty={qty}, price={price}, shcode={shcode}");

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                void OnReceiveData(string tr)
                {
                    try
                    {
                        string ordNoRaw2 = _qOrder.GetFieldData("CSPAT00600OutBlock2", "OrdNo", 0);
                        string ordNoRaw1 = _qOrder.GetFieldData("CSPAT00600OutBlock1", "OrdNo", 0);

                        string ordNoTrim = (ordNoRaw2 ?? "").Trim();
                        if (string.IsNullOrEmpty(ordNoTrim))
                            ordNoTrim = (ordNoRaw1 ?? "").Trim();

                        string ordTime2 = SafeGetOrder("CSPAT00600OutBlock2", "OrdTime");
                        string ordTime1 = SafeGetOrder("CSPAT00600OutBlock1", "OrdTime");
                        string ordTime = !string.IsNullOrEmpty(ordTime2) ? ordTime2 : ordTime1;

                        Write($"[CSPAT00600 주문전송확인] OrdNo2 RAW='{ordNoRaw2}' OrdNo1 RAW='{ordNoRaw1}' -> USE='{ordNoTrim}', Time='{ordTime}'");

                        if (!string.IsNullOrEmpty(ordNoTrim))
                        {
                            if (Login.OrdMap == null)
                            {
                                Write("[CSPAT00600 주문전송확인] Login.OrdMap is null -> REGISTER FAIL");
                                Login.TradeWait?.OnOrderSendConfirmFailed(sideKor, band, "ORDMAP_NULL");
                                return;
                            }

                            try
                            {
                                Login.OrdMap.Register(sideKor, band, ordNoTrim);
                                Write($"[CSPAT00600 주문전송확인.OK] ordNo='{ordNoTrim}' mapped side={sideKor} band={band}");
                            }
                            catch (Exception exReg)
                            {
                                Write("[CSPAT00600 주문전송확인] REGISTER EX: " + exReg.Message);
                                Login.TradeWait?.OnOrderSendConfirmFailed(sideKor, band, "ORDMAP_REGISTER_EX");
                            }
                        }
                        else
                        {
                            Write("[CSPAT00600 주문전송확인.FAIL] OrdNo EMPTY");
                            Login.TradeWait?.OnOrderSendConfirmFailed(sideKor, band, "ORDNO_EMPTY");
                        }
                    }
                    catch (Exception ex)
                    {
                        Write("[CSPAT00600 주문전송확인.EX] " + ex);
                        Login.TradeWait?.OnOrderSendConfirmFailed(sideKor, band, "CONFIRM_EXCEPTION");
                    }
                    finally
                    {
                        tcs.TrySetResult(true);
                    }
                }

                _qOrder.ReceiveData += OnReceiveData;

                try
                {
                    Write(
                        $"[CSPAT00600 IN] AcntNo='{acntNo}', PwdLen={pwd4.Length}, " +
                        $"IsuNo='{isuNo}', BnsTpCode={bnsTpCode}, OrdQty={sQty}, OrdPrc={sPrc}, " +
                        $"OrdprcPtnCode=00, MgntrnCode=000, LoanDt='', OrdCndiTpCode=0"
                    );

                    _qOrder.SetFieldData("CSPAT00600InBlock1", "AcntNo", 0, acntNo);
                    _qOrder.SetFieldData("CSPAT00600InBlock1", "InptPwd", 0, pwd4);
                    _qOrder.SetFieldData("CSPAT00600InBlock1", "IsuNo", 0, isuNo);
                    _qOrder.SetFieldData("CSPAT00600InBlock1", "OrdQty", 0, sQty);
                    _qOrder.SetFieldData("CSPAT00600InBlock1", "OrdPrc", 0, sPrc);
                    _qOrder.SetFieldData("CSPAT00600InBlock1", "BnsTpCode", 0, bnsTpCode);

                    _qOrder.SetFieldData("CSPAT00600InBlock1", "OrdprcPtnCode", 0, "00");
                    _qOrder.SetFieldData("CSPAT00600InBlock1", "MgntrnCode", 0, "000");
                    _qOrder.SetFieldData("CSPAT00600InBlock1", "LoanDt", 0, "");
                    _qOrder.SetFieldData("CSPAT00600InBlock1", "OrdCndiTpCode", 0, "0");

                    int r = _qOrder.Request(false);
                    if (r < 0)
                    {
                        Write($"[CSPAT00600] Request failed r={r}");
                        Login.TradeWait?.OnRequestFailed(sideKor, band, "REQ_FAIL:" + r);
                    }

                    var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000)).ConfigureAwait(false);
                    if (completed != tcs.Task)
                    {
                        Write("[CSPAT00600] 주문전송확인 TIMEOUT");
                        Login.TradeWait?.OnOrderSendConfirmFailed(sideKor, band, "CONFIRM_TIMEOUT");
                    }
                }
                finally
                {
                    _qOrder.ReceiveData -= OnReceiveData;
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private void OnOrderMessage(bool isSysErr, string code, string msg)
        {
            Write($"[ORDER MSG] sysErr={isSysErr}, code={code}, msg={msg}");

            bool looksFail =
                isSysErr ||
                (!string.IsNullOrEmpty(msg) && msg.Contains("종료"));

            if (looksFail && !string.IsNullOrEmpty(_lastSideKor) && _lastBand > 0)
            {
                Login.TradeWait?.OnOrderMessageFail(_lastSideKor, _lastBand, code, msg);
            }
        }

        private string SafeGetOrder(string block, string field)
        {
            try { return _qOrder.GetFieldData(block, field, 0)?.Trim(); }
            catch { return ""; }
        }

        private static string NormalizeSideKorOnly(string sideKor)
        {
            sideKor = (sideKor ?? "").Trim();

            if (string.Equals(sideKor, "BUY", StringComparison.OrdinalIgnoreCase)) return "매수";
            if (string.Equals(sideKor, "SELL", StringComparison.OrdinalIgnoreCase)) return "매도";

            if (sideKor == "매수" || sideKor == "매도") return sideKor;

            throw new ArgumentException($"sideKor는 '매수' 또는 '매도'만 허용됩니다. sideKor='{sideKor}'", nameof(sideKor));
        }

        private void Write(string msg)
        {
            try
            {
                Console.WriteLine(msg);
                Debug.WriteLine(msg);
                Log?.Invoke(msg);
            }
            catch { }
        }

        public void Dispose()
        {
            try { _qOrder.ReceiveMessage -= OnOrderMessage; } catch { }
            try { _sendLock.Dispose(); } catch { }
        }
    }
}
// 2026-02-09 69055
