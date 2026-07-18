// 0530_매매_Xing.cs  (복붙용 / C# 7.3)
// ------------------------------------------------------------
// 역할:
// - 실제 주문 엔진 (XING CSPAT00600)
// - ✅ ActiveX(XAQuery)는 반드시 UI(STA) 스레드에서만 호출되도록 0530 내부에서 강제 마샬링
// ------------------------------------------------------------

using System;
using System.Globalization;
using System.IO;
using System.Text;
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
        private readonly object _requestSync = new object();
        private OrderRequestContext _activeRequest;
        private static long _requestSequence;

        private sealed class OrderRequestContext
        {
            public string RequestId;
            public string AccountNo;
            public string Symbol;
            public string Side;
            public int Qty;
            public int Price;
            public int Band;
            public DateTime SendTimeUtc;
            public int ThreadId;
            public long SequenceNo;
            public DateTime? TimeoutAtUtc;
            public bool IsTimedOut;
            public TaskCompletionSource<long> AckTcs;
        }

        private volatile bool _lastSysErr;
        private volatile string _lastMsgCode;
        private volatile string _lastMsgText;

        private bool _disposed;

        // ✅ UI 컨텍스트/스레드
        private readonly SynchronizationContext _uiCtx;
        private readonly int _uiThreadId;

        public event Action<string> Log;

        private sealed class Logger
        {
            private readonly string _module;

            public Logger(string path)
            {
                _module = "0530";
            }

            public void Info(string msg) => Write("INFO", msg);
            public void Error(string msg) => Write("ERR", msg);

            private void Write(string level, string msg)
            {
                AppLog.Write(_module, level, msg);
            }
        }

        private readonly Logger _log;

        private static string DefaultLogPath()
        {
            return AppLog.PathName;
        }

        public 매매_Xing(Func<string> getAcntNo, Func<string> getPwd4, Func<string> getShcode)
        {
            _getAcntNo = getAcntNo ?? throw new ArgumentNullException(nameof(getAcntNo));
            _getPwd4 = getPwd4 ?? throw new ArgumentNullException(nameof(getPwd4));
            _getShcode = getShcode ?? throw new ArgumentNullException(nameof(getShcode));

            _log = new Logger(DefaultLogPath());

            // ✅ 반드시 Login(UI)에서 생성되어야 uiCtx가 잡힘
            _uiCtx = SynchronizationContext.Current;
            _uiThreadId = Thread.CurrentThread.ManagedThreadId;

            _cspat00600.ResFileName = @"C:\ls_sec\xingapi\res\CSPAT00600.res";
            _cspat00600.ReceiveData += OnReceiveData;
            _cspat00600.ReceiveMessage += OnReceiveMessage;

            _log.Info("[0530] ctor OK");
            _log.Info("[0530] CSPAT00600.res = " + _cspat00600.ResFileName + " (exists=" + File.Exists(_cspat00600.ResFileName) + ")");
            _log.Info("[0530] uiThreadId=" + _uiThreadId + " uiCtx=" + (_uiCtx == null ? "null" : _uiCtx.GetType().Name));
            _log.Info("[DAY][START] PendingRecovery=Disabled");
        }

        private Task RunOnUiAsync(Action action)
        {
            if (_uiCtx == null)
            {
                // uiCtx가 null이면 안전하게 하려면 "UI에서 생성"이 선행되어야 함
                throw new InvalidOperationException("UI SynchronizationContext가 null 입니다. 매매_Xing은 UI 스레드에서 생성되어야 합니다.");
            }

            // 이미 UI면 즉시 실행
            if (Thread.CurrentThread.ManagedThreadId == _uiThreadId)
            {
                action();
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            _uiCtx.Post(_ =>
            {
                try
                {
                    action();
                    tcs.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }, null);

            return tcs.Task;
        }

        public async Task<long> SendOrderLive(string sideKor, string shcode, int price, int qty, int band)
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
            if (string.IsNullOrEmpty(acnt)) throw new InvalidOperationException("계좌번호가 비어있습니다.");
            if (string.IsNullOrEmpty(pwd4)) throw new InvalidOperationException("비밀번호 4자리가 비어있습니다.");

            string isuNo = shcode.StartsWith("A", StringComparison.OrdinalIgnoreCase) ? shcode : "A" + shcode;
            string bnsTp = sideKor == "매수" ? "2" : "1";

            await _sendLock.WaitAsync(); // ✅ ConfigureAwait(false) 금지

            OrderRequestContext request = null;
            try
            {
                long sequence = Interlocked.Increment(ref _requestSequence);
                request = new OrderRequestContext
                {
                    RequestId = "REQ_" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture) +
                                "_" + sequence.ToString("D6", CultureInfo.InvariantCulture) +
                                "_" + (sideKor == "매수" ? "BUY" : "SELL") +
                                "_" + qty.ToString(CultureInfo.InvariantCulture),
                    AccountNo = acnt,
                    Symbol = shcode,
                    Side = sideKor,
                    Qty = qty,
                    Price = price,
                    Band = band,
                    SendTimeUtc = DateTime.UtcNow,
                    ThreadId = Thread.CurrentThread.ManagedThreadId,
                    SequenceNo = sequence,
                    AckTcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously)
                };

                lock (_requestSync)
                {
                    _activeRequest = request;
                }

                _lastSysErr = false;
                _lastMsgCode = null;
                _lastMsgText = null;

                _log.Info("[0530][REQUEST] requestId=" + request.RequestId +
                          " side=" + sideKor + " qty=" + qty + " price=" + price + " band=" + band);
                try { LoginFormAccessor.TryGetLogin()?.SetOrderRecoveryStatus("주문응답 대기"); } catch { }
                _log.Info($"[0530.SEND][ENTER] side={sideKor} qty={qty} price={price} band={band} acnt='{MaskAcnt(acnt)}' pwLen={pwd4.Length} isuNo='{isuNo}' callerThread={Thread.CurrentThread.ManagedThreadId}");
                RaiseLog($"[0530.SEND] {sideKor} {qty}@{price} band={band}");

                // ✅✅ 핵심: ActiveX 호출은 무조건 UI에서 실행
                await RunOnUiAsync(() =>
                {
                    _log.Info($"[0530.UI] entering UI block thread={Thread.CurrentThread.ManagedThreadId}/{(Thread.CurrentThread.GetApartmentState())}");

                    _cspat00600.SetFieldData("CSPAT00600InBlock1", "AcntNo", 0, acnt);
                    _cspat00600.SetFieldData("CSPAT00600InBlock1", "InptPwd", 0, pwd4);
                    _cspat00600.SetFieldData("CSPAT00600InBlock1", "IsuNo", 0, isuNo);
                    _cspat00600.SetFieldData("CSPAT00600InBlock1", "BnsTpCode", 0, bnsTp);
                    _cspat00600.SetFieldData("CSPAT00600InBlock1", "OrdprcPtnCode", 0, "00"); // 지정가
                    _cspat00600.SetFieldData("CSPAT00600InBlock1", "OrdQty", 0, qty.ToString(CultureInfo.InvariantCulture));
                    _cspat00600.SetFieldData("CSPAT00600InBlock1", "OrdPrc", 0, price.ToString(CultureInfo.InvariantCulture));
                    _cspat00600.SetFieldData("CSPAT00600InBlock1", "MgntrnCode", 0, "000");
                    _cspat00600.SetFieldData("CSPAT00600InBlock1", "LoanDt", 0, "");
                    // DAY policy: unfilled order state is not persisted or recovered after market close.
                    _cspat00600.SetFieldData("CSPAT00600InBlock1", "OrdCndiTpCode", 0, "0"); // 일반주문 고정

                    int rq = _cspat00600.Request(false);
                    _log.Info("[0530.Request] ret=" + rq);
                    _log.Info("[0530.SEND] AFTER Request (still alive)");

                    if (rq < 0)
                    {
                        string msg = "Request 실패 code=" + rq + BuildLastMsgSuffix();
                        _log.Error("[0530.Request] " + msg);
                        throw new InvalidOperationException(msg);
                    }
                });

                var timeout = Task.Delay(5000);
                var done = await Task.WhenAny(request.AckTcs.Task, timeout);

                if (done != request.AckTcs.Task)
                {
                    request.IsTimedOut = true;
                    request.TimeoutAtUtc = DateTime.UtcNow;

                    string msg = "주문 응답 타임아웃(OrdNo 미수신)" + BuildLastMsgSuffix();
                    _log.Error("[0530][TIMEOUT] requestId=" + request.RequestId + " " + msg);
                    _log.Info("[DAY][START] PendingRecovery=Disabled requestId=" + request.RequestId);
                    _log.Info("[DAY][EOD] RemainIgnored=Unknown Reason=DayExpired requestId=" + request.RequestId);
                    throw new TimeoutException(msg);
                }

                long ordNo = await request.AckTcs.Task;
                _log.Info("[0530.SEND][OK] requestId=" + request.RequestId + " OrdNo=" + ordNo);
                _log.Info("[DAY][ORDER] OrderNo=" + ordNo + " DayOrder=True");
                RaiseLog("[0530.ACK.OK] OrdNo=" + ordNo);
                RaiseLog("[DAY][ORDER] OrderNo=" + ordNo + " DayOrder=True");
                return ordNo;
            }
            finally
            {
                lock (_requestSync)
                {
                    if (object.ReferenceEquals(_activeRequest, request))
                        _activeRequest = null;
                }
                _sendLock.Release();
            }
        }

        public Task<long> SendOrderLive(string sideKor, string shcode, long price, int qty, int band)
            => SendOrderLive(sideKor, shcode, checked((int)price), qty, band);

        public Task<long> SendOrderLive(string sideKor, string shcode, double price, int qty, int band)
            => SendOrderLive(sideKor, shcode, (int)Math.Round(price), qty, band);

        private void OnReceiveMessage(bool isSystemError, string code, string msg)
        {
            _lastSysErr = isSystemError;
            _lastMsgCode = code;
            _lastMsgText = msg;

            string line = $"[0530.MSG] sysErr={isSystemError} code='{code ?? ""}' msg='{msg ?? ""}' thread={Thread.CurrentThread.ManagedThreadId}";
            _log.Info(line);
            RaiseLog(line);

            if (isSystemError)
            {
                OrderRequestContext request;
                lock (_requestSync) { request = _activeRequest; }
                request?.AckTcs.TrySetException(new InvalidOperationException("CSPAT00600 SYSERR" + BuildLastMsgSuffix()));
            }
        }

        private void OnReceiveData(string trCode)
        {
            if (!string.Equals(trCode, "CSPAT00600", StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                string ordNoStr = GetFieldFirstTrim(_cspat00600,
                    "CSPAT00600OutBlock2", "OrdNo",
                    "CSPAT00600OutBlock1", "OrdNo",
                    "CSPAT00600OutBlock2", "ordno",
                    "CSPAT00600OutBlock1", "ordno"
                );

                string ackLine = "[0530.ACK] OrdNoRaw=\"" + (ordNoStr ?? "") + "\"" + BuildLastMsgSuffix();
                _log.Info(ackLine);
                RaiseLog(ackLine);

                string ackSide = NormalizeSide(GetFieldFirstTrim(_cspat00600,
                    "CSPAT00600OutBlock1", "BnsTpCode",
                    "CSPAT00600OutBlock1", "bnstpcode",
                    "CSPAT00600OutBlock2", "BnsTpCode"));
                int ackQty = ReadIntFirst(
                    "CSPAT00600OutBlock1", "OrdQty",
                    "CSPAT00600OutBlock2", "OrdQty");
                int ackPrice = ReadIntFirst(
                    "CSPAT00600OutBlock1", "OrdPrc",
                    "CSPAT00600OutBlock2", "OrdPrc");
                string ackSymbol = GetFieldFirstTrim(_cspat00600,
                    "CSPAT00600OutBlock1", "IsuNo",
                    "CSPAT00600OutBlock2", "IsuNo");

                if (string.IsNullOrEmpty(ordNoStr) || !long.TryParse(ordNoStr.Trim(), out long ordNo) || ordNo <= 0)
                {
                    string rejectMsg = BuildRejectMessage();
                    var ex = new InvalidOperationException(rejectMsg);
                    _log.Error("[0530.ACK.NACK] " + rejectMsg);
                    RaiseLog("[0530.ACK.NACK] " + rejectMsg);
                    OrderRequestContext failed;
                    lock (_requestSync) { failed = _activeRequest; }
                    failed?.AckTcs.TrySetException(ex);
                    return;
                }

                OrderRequestContext active;
                lock (_requestSync) { active = _activeRequest; }

                if (Matches(active, ackSide, ackQty, ackPrice, ackSymbol))
                {
                    _log.Info("[0530][ACK] requestId=" + active.RequestId + " ordNo=" + ordNo);
                    if (active.IsTimedOut)
                    {
                        _log.Info("[0530][LATE_ACK] ignored requestId=" + active.RequestId + " ordNo=" + ordNo);
                        _log.Info("[DAY][ORDER] OrderNo=" + ordNo + " DayOrder=True");
                        _log.Info("[DAY][START] PendingRecovery=Disabled requestId=" + active.RequestId);
                        _log.Info("[DAY][EOD] RemainIgnored=Unknown Reason=LateAckNoRecovery requestId=" + active.RequestId);
                    }
                    else
                    {
                        active.AckTcs.TrySetResult(ordNo);
                    }
                    return;
                }

                _log.Error("[0530][ACK][UNMATCHED] ordNo=" + ordNo +
                           " side=" + ackSide + " qty=" + ackQty + " price=" + ackPrice +
                           " activeRequestId=" + (active != null ? active.RequestId : "(none)"));
            }
            catch (Exception ex)
            {
                string msg = "[0530.ACK.FAIL] " + ex.Message;
                _log.Error(msg);
                RaiseLog(msg);
                OrderRequestContext request;
                lock (_requestSync) { request = _activeRequest; }
                request?.AckTcs.TrySetException(ex);
            }
        }

        private static bool Matches(OrderRequestContext request, string side, int qty, int price, string symbol)
        {
            if (request == null || qty <= 0 || price <= 0 || string.IsNullOrWhiteSpace(side))
                return false;

            return string.Equals(NormalizeSide(request.Side), NormalizeSide(side), StringComparison.Ordinal) &&
                   request.Qty == qty &&
                   Math.Abs(request.Price - price) <= 1 &&
                   (string.IsNullOrWhiteSpace(symbol) ||
                    NormalizeSymbol(request.Symbol) == NormalizeSymbol(symbol));
        }

        private int ReadIntFirst(params string[] blockFieldPairs)
        {
            string raw = GetFieldFirstTrim(_cspat00600, blockFieldPairs);
            double value;
            return double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out value)
                ? Convert.ToInt32(Math.Round(value))
                : 0;
        }

        private static string NormalizeSymbol(string symbol)
        {
            symbol = (symbol ?? "").Trim().ToUpperInvariant();
            return symbol.StartsWith("A", StringComparison.Ordinal) ? symbol.Substring(1) : symbol;
        }

        private string BuildRejectMessage()
        {
            string c = (_lastMsgCode ?? "").Trim();
            string t = (_lastMsgText ?? "").Trim();

            if (!string.IsNullOrEmpty(c) || !string.IsNullOrEmpty(t))
            {
                if (string.Equals(c, "01425", StringComparison.OrdinalIgnoreCase) ||
                    t.IndexOf("주문가능금액", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.IndexOf("부족", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return "주문 거절(주문가능금액 부족) " + BuildLastMsgSuffix();
                }
                return "주문 거절 " + BuildLastMsgSuffix();
            }

            return "OrdNo 미수신(원인 메시지 없음) " + BuildLastMsgSuffix();
        }

        private string BuildLastMsgSuffix()
        {
            string c = _lastMsgCode ?? "";
            string t = _lastMsgText ?? "";
            return $" / lastMsg=({_lastSysErr},{c},{t})";
        }

        private static string NormalizeSide(string side)
        {
            if (string.Equals(side, "BUY", StringComparison.OrdinalIgnoreCase)) return "매수";
            if (string.Equals(side, "SELL", StringComparison.OrdinalIgnoreCase)) return "매도";
            if (side == "2") return "매수";
            if (side == "1") return "매도";
            return side;
        }

        private void RaiseLog(string msg) { try { Log?.Invoke(msg); } catch { } }

        private static string MaskAcnt(string acnt)
        {
            if (string.IsNullOrEmpty(acnt)) return "";
            if (acnt.Length <= 4) return "****";
            return acnt.Substring(0, 2) + "****" + acnt.Substring(acnt.Length - 2, 2);
        }

        private static string GetFieldFirstTrim(XAQueryClass q, params string[] pairArgs)
        {
            if (q == null) return string.Empty;
            if (pairArgs == null || pairArgs.Length < 2) return string.Empty;

            for (int i = 0; i + 1 < pairArgs.Length; i += 2)
            {
                string block = pairArgs[i];
                string field = pairArgs[i + 1];
                if (string.IsNullOrEmpty(block) || string.IsNullOrEmpty(field)) continue;

                try
                {
                    string v = q.GetFieldData(block, field, 0);
                    v = string.IsNullOrEmpty(v) ? "" : v.Trim();
                    if (!string.IsNullOrEmpty(v)) return v;
                }
                catch { }
            }
            return string.Empty;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _cspat00600.ReceiveData -= OnReceiveData;
                _cspat00600.ReceiveMessage -= OnReceiveMessage;
            }
            catch { }

            try { _sendLock.Dispose(); } catch { }

            _log.Info("[0530] Dispose OK");
        }
    }
}

// 2026-03-03 69027
