// OrderService.cs  (C# 7.3)
// - XING TR 연동 주문 서비스
// - CSPAT00600 주문전송확인(OrdNo) 수신까지 PlaceAsync가 대기 후 반환
// - CSPAT00800 취소확인(OrgOrdNo) 수신까지 CancelAsync가 대기 후 반환
// - Console + File 로그 항상 기록
// - ActiveX/COM(XAQueryClass) 호출은 반드시 UI(STA) 스레드에서 실행되도록 마샬링
//
// [이번 수정 핵심]
// 1) CancelAsync가 단순 Request 성공 여부가 아니라 "취소확인 ReceiveData"까지 대기
// 2) CSPAT00800 ReceiveData에서 OrgOrdNo / OrdNo / 응답코드 파싱
// 3) 취소완료 시 CancelConfirmed 이벤트 발생
// 4) 0004_미체결추격관리와 연결하기 쉽게 원주문번호(OrgOrdNo)를 그대로 전달

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using XA_DATASETLib;

namespace Exercise_1
{
    public sealed class OrderService : IDisposable
    {
        // ─────────────────────────────────────────────
        // Logger (Console + File, always-on)
        // ─────────────────────────────────────────────
        private sealed class Logger
        {
            private readonly string _module;

            public Logger(string path)
            {
                _module = "OrderService";
            }

            public void Info(string msg) => Write("INFO", msg);
            public void Warn(string msg) => Write("WARN", msg);
            public void Error(string msg) => Write("ERR", msg);

            private void Write(string level, string msg)
            {
                string module = _module;
                string text = msg ?? "";
                if (text.StartsWith("[t0425]", StringComparison.OrdinalIgnoreCase) ||
                    text.StartsWith("[T0425", StringComparison.OrdinalIgnoreCase))
                {
                    module = "t0425";
                }

                AppLog.Write(module, level, text);
            }
        }

        // ─────────────────────────────────────────────
        // 취소확인 Ack 모델
        // ─────────────────────────────────────────────
        public sealed class CancelAck
        {
            public bool Accepted { get; set; }
            public string OrgOrderNo { get; set; }     // 취소 대상 원주문번호
            public string CancelOrderNo { get; set; }  // 취소주문 자체의 번호(있으면)
            public string Message { get; set; }
        }

        // ─────────────────────────────────────────────
        // UI/STA marshal (ActiveX 안전 호출)
        // ─────────────────────────────────────────────
        private readonly Control _ui;
        private readonly SynchronizationContext _uiCtx;
        private readonly int _ownerThreadId;
        private readonly ApartmentState _ownerApt;

        // ─────────────────────────────────────────────
        // XING TR objects
        // ─────────────────────────────────────────────
        private readonly XAQueryClass _cspat00600 = new XAQueryClass(); // 주문
        private readonly XAQueryClass _cspat00800 = new XAQueryClass(); // 취소
        private readonly XAQueryClass _t0425 = new XAQueryClass();      // 주문/체결 조회
        private readonly XAQueryClass _t0425Recovery = new XAQueryClass(); // timeout OrdNo 복구 전용

        private readonly object _lock = new object();
        private TaskCompletionSource<IList<OrderRow>> _t0425Tcs;
        private readonly object _recoveryQueryLock = new object();
        private readonly SemaphoreSlim _recoveryQueryGate = new SemaphoreSlim(1, 1);
        private TaskCompletionSource<IList<OrderRow>> _t0425RecoveryTcs;

        // ✅ CSPAT00600 주문전송확인 대기용
        private readonly object _ackLock = new object();
        private TaskCompletionSource<OrderAck> _cspat00600AckTcs;

        // ✅ CSPAT00800 취소확인 대기용
        private readonly object _cancelAckLock = new object();
        private TaskCompletionSource<CancelAck> _cspat00800AckTcs;
        private string _pendingCancelOrgOrdNo;

        // ✅ 마지막 ReceiveMessage 보관(주문)
        private volatile bool _lastSysErr;
        private volatile string _lastMsgCode;
        private volatile string _lastMsgText;

        // ✅ 마지막 ReceiveMessage 보관(취소)
        private volatile bool _lastCancelSysErr;
        private volatile string _lastCancelMsgCode;
        private volatile string _lastCancelMsgText;

        private readonly Logger _log;

        public event Action<OrderAck> OrderAccepted;
        public event Action<string> OrderRejected;
        public event Action<OrderRow> OrderUpdated;
        public event Action<FillEvent> FillReceived;

        // ✅ 추가: 취소확인 이벤트
        // 원주문번호(OrgOrdNo)를 그대로 넘긴다.
        public event Action<string> CancelConfirmed;
        public event Action<string> CancelRejected;

        private static string DefaultLogPath()
        {
            return AppLog.PathName;
        }

        public OrderService() : this(null, null) { }

        public OrderService(string logFilePath) : this(null, logFilePath) { }

        public OrderService(Control ui, string logFilePath)
        {
            _ui = ui;
            _uiCtx = SynchronizationContext.Current;
            _ownerThreadId = Thread.CurrentThread.ManagedThreadId;
            _ownerApt = Thread.CurrentThread.GetApartmentState();

            _log = new Logger(string.IsNullOrWhiteSpace(logFilePath) ? DefaultLogPath() : logFilePath);

            _cspat00600.ResFileName = @"C:\LS_SEC\xingAPI\Res\CSPAT00600.res";
            _cspat00800.ResFileName = @"C:\LS_SEC\xingAPI\Res\CSPAT00800.res";
            _t0425.ResFileName = @"C:\LS_SEC\xingAPI\Res\t0425.res";
            _t0425Recovery.ResFileName = @"C:\LS_SEC\xingAPI\Res\t0425.res";

            _cspat00600.ReceiveData += new _IXAQueryEvents_ReceiveDataEventHandler(Cspat00600_ReceiveData);
            _cspat00600.ReceiveMessage += new _IXAQueryEvents_ReceiveMessageEventHandler(Cspat00600_ReceiveMessage);

            _cspat00800.ReceiveData += new _IXAQueryEvents_ReceiveDataEventHandler(Cspat00800_ReceiveData);
            _cspat00800.ReceiveMessage += new _IXAQueryEvents_ReceiveMessageEventHandler(Cspat00800_ReceiveMessage);

            _t0425.ReceiveData += new _IXAQueryEvents_ReceiveDataEventHandler(T0425_ReceiveData);
            _t0425Recovery.ReceiveData += new _IXAQueryEvents_ReceiveDataEventHandler(T0425Recovery_ReceiveData);

            _log.Info("[OrderService] ctor OK");
            _log.Info("[Res] CSPAT00600=" + _cspat00600.ResFileName);
            _log.Info("[Res] CSPAT00800=" + _cspat00800.ResFileName);
            _log.Info("[Res] t0425     =" + _t0425.ResFileName);
            _log.Info($"[OrderService] ownerThread id={_ownerThreadId} apt={_ownerApt} ui={(_ui == null ? "null" : _ui.GetType().Name)} uiCtx={(_uiCtx == null ? "null" : _uiCtx.GetType().Name)}");
        }

        public void Dispose()
        {
            try
            {
                _cspat00600.ReceiveData -= new _IXAQueryEvents_ReceiveDataEventHandler(Cspat00600_ReceiveData);
                _cspat00600.ReceiveMessage -= new _IXAQueryEvents_ReceiveMessageEventHandler(Cspat00600_ReceiveMessage);

                _cspat00800.ReceiveData -= new _IXAQueryEvents_ReceiveDataEventHandler(Cspat00800_ReceiveData);
                _cspat00800.ReceiveMessage -= new _IXAQueryEvents_ReceiveMessageEventHandler(Cspat00800_ReceiveMessage);

                _t0425.ReceiveData -= new _IXAQueryEvents_ReceiveDataEventHandler(T0425_ReceiveData);
                _t0425Recovery.ReceiveData -= new _IXAQueryEvents_ReceiveDataEventHandler(T0425Recovery_ReceiveData);

                _log.Info("[OrderService] Dispose OK");
            }
            catch (Exception ex)
            {
                _log.Error("[OrderService] Dispose exception: " + ex.Message);
            }
        }

        // ─────────────────────────────────────────────
        // UI marshal helpers
        // ─────────────────────────────────────────────
        private Task InvokeOnUiAsync(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            if (_ui != null)
            {
                if (_ui.IsDisposed)
                    return Task.FromException(new ObjectDisposedException("OrderService UI control disposed"));

                if (!_ui.InvokeRequired)
                {
                    try
                    {
                        action();
                        return Task.CompletedTask;
                    }
                    catch (Exception ex)
                    {
                        return Task.FromException(ex);
                    }
                }

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    _ui.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            action();
                            tcs.TrySetResult(true);
                        }
                        catch (Exception ex)
                        {
                            tcs.TrySetException(ex);
                        }
                    }));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                return tcs.Task;
            }

            if (_uiCtx != null)
            {
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    _uiCtx.Post(_ =>
                    {
                        try
                        {
                            action();
                            tcs.TrySetResult(true);
                        }
                        catch (Exception ex)
                        {
                            tcs.TrySetException(ex);
                        }
                    }, null);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                return tcs.Task;
            }

            _log.Warn("[UI-MARSHAL] ui/uiCtx is null -> executing on caller thread (RISK). " +
                      $"callerThread id={Thread.CurrentThread.ManagedThreadId} apt={Thread.CurrentThread.GetApartmentState()} ownerThread={_ownerThreadId}/{_ownerApt}");

            try
            {
                action();
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }

        private async Task<T> InvokeOnUiAsync<T>(Func<T> func)
        {
            if (func == null) throw new ArgumentNullException(nameof(func));

            T result = default(T);
            Exception caught = null;

            await InvokeOnUiAsync(() =>
            {
                try { result = func(); }
                catch (Exception ex) { caught = ex; }
            }).ConfigureAwait(false);

            if (caught != null) throw caught;
            return result;
        }

        // ─────────────────────────────────────────────
        // 주문 (CSPAT00600)
        // ─────────────────────────────────────────────
        public async Task<OrderAck> PlaceAsync(OrderRequest req)
        {
            if (req == null) throw new ArgumentNullException(nameof(req));

            _log.Info("[PlaceAsync] ENTER " +
                      $"AccountNo='{req.AccountNo}', PwLen={(req.Password ?? string.Empty).Length}, " +
                      $"Symbol='{req.Symbol}', Qty={req.Qty}, Price={req.Price}, Type={req.Type}, Side={req.Side}");

            string acnt = (req.AccountNo ?? string.Empty).Trim();
            string pwd = (req.Password ?? string.Empty).Trim();

            string symbol = (req.Symbol ?? string.Empty).Trim();
            if (symbol.Length == 6 && !symbol.StartsWith("A"))
                symbol = "A" + symbol;

            int qtyInt;
            try
            {
                qtyInt = (int)Convert.ToInt64(req.Qty);
            }
            catch
            {
                string msg = "수량 변환 실패(qty=" + req.Qty.ToString() + ")";
                _log.Error("[PlaceAsync] " + msg);
                RaiseOrderRejected(msg);
                return new OrderAck { Accepted = false, Message = msg, OrderNo = null };
            }

            if (qtyInt <= 0)
            {
                string msg = "수량이 0 이하입니다.";
                _log.Warn("[PlaceAsync] " + msg);
                RaiseOrderRejected(msg);
                return new OrderAck { Accepted = false, Message = msg, OrderNo = null };
            }

            int priceInt = 0;
            if (req.Type != OrderType.Market)
            {
                priceInt = (int)Math.Round(req.Price);
                if (priceInt <= 0)
                {
                    string msg = "가격이 0 이하입니다.";
                    _log.Warn("[PlaceAsync] " + msg);
                    RaiseOrderRejected(msg);
                    return new OrderAck { Accepted = false, Message = msg, OrderNo = null };
                }
            }

            string qtyStr = qtyInt.ToString(CultureInfo.InvariantCulture);
            string prcStr = (req.Type == OrderType.Market) ? "0" : priceInt.ToString(CultureInfo.InvariantCulture);

            string bnsTpCode = ((int)req.Side).ToString(CultureInfo.InvariantCulture); // 1=매도, 2=매수
            string ordPtnCode = (req.Type == OrderType.Market ? "03" : "00");
            string mgntrnCode = "000";
            string loanDt = "";
            string ordCndiTpCode = "0";

            _log.Info("[CSPAT00600 InBlock1] " +
                      $"AcntNo='{acnt}', InptPwdLen={pwd.Length}, IsuNo='{symbol}', " +
                      $"OrdQty='{qtyStr}', OrdPrc='{prcStr}', BnsTpCode='{bnsTpCode}', OrdprcPtnCode='{ordPtnCode}'");

            TaskCompletionSource<OrderAck> tcs;
            lock (_ackLock)
            {
                _lastSysErr = false;
                _lastMsgCode = null;
                _lastMsgText = null;

                _cspat00600AckTcs = new TaskCompletionSource<OrderAck>(TaskCreationOptions.RunContinuationsAsynchronously);
                tcs = _cspat00600AckTcs;
            }

            try
            {
                int ret = await InvokeOnUiAsync(() =>
                {
                    _cspat00600.SetFieldData("CSPAT00600InBlock1", "AcntNo", 0, acnt);
                    _cspat00600.SetFieldData("CSPAT00600InBlock1", "InptPwd", 0, pwd);
                    _cspat00600.SetFieldData("CSPAT00600InBlock1", "IsuNo", 0, symbol);
                    _cspat00600.SetFieldData("CSPAT00600InBlock1", "OrdQty", 0, qtyStr);
                    _cspat00600.SetFieldData("CSPAT00600InBlock1", "OrdPrc", 0, prcStr);
                    _cspat00600.SetFieldData("CSPAT00600InBlock1", "BnsTpCode", 0, bnsTpCode);
                    _cspat00600.SetFieldData("CSPAT00600InBlock1", "OrdprcPtnCode", 0, ordPtnCode);
                    _cspat00600.SetFieldData("CSPAT00600InBlock1", "MgntrnCode", 0, mgntrnCode);
                    _cspat00600.SetFieldData("CSPAT00600InBlock1", "LoanDt", 0, loanDt);
                    _cspat00600.SetFieldData("CSPAT00600InBlock1", "OrdCndiTpCode", 0, ordCndiTpCode);

                    return _cspat00600.Request(false);
                }).ConfigureAwait(false);

                _log.Info("[CSPAT00600 Request] ret=" + ret);

                if (ret < 0)
                {
                    string msg = "주문 전송 실패(code=" + ret + ")" + BuildLastMsgSuffix();
                    _log.Error("[PlaceAsync] " + msg);
                    FailPendingAck(msg);
                    RaiseOrderRejected(msg);
                    return new OrderAck { Accepted = false, Message = msg, OrderNo = null };
                }

                var completed = await Task.WhenAny(tcs.Task, Task.Delay(2500)).ConfigureAwait(false);
                if (completed != tcs.Task)
                {
                    string msg = "주문전송확인 타임아웃(OrdNo 미수신)" + BuildLastMsgSuffix();
                    _log.Error("[PlaceAsync] " + msg);
                    FailPendingAck(msg);
                    RaiseOrderRejected(msg);
                    return new OrderAck { Accepted = false, Message = msg, OrderNo = null };
                }

                OrderAck ack = tcs.Task.Result;
                _log.Info("[PlaceAsync] RETURN Accepted=" + ack.Accepted + " OrdNo=" + (ack.OrderNo ?? "null") + " Msg=" + (ack.Message ?? ""));
                return ack;
            }
            catch (Exception ex)
            {
                string msg = "주문 전송 예외: " + ex.Message + BuildLastMsgSuffix();
                _log.Error("[PlaceAsync] " + msg);
                FailPendingAck(msg);
                RaiseOrderRejected(msg);
                return new OrderAck { Accepted = false, Message = msg, OrderNo = null };
            }
        }

        private void Cspat00600_ReceiveMessage(bool sysErr, string code, string msg)
        {
            _lastSysErr = sysErr;
            _lastMsgCode = code;
            _lastMsgText = msg;

            _log.Info($"[CSPAT00600 ReceiveMessage] sysErr={sysErr}, code='{code ?? ""}', msg='{msg ?? ""}'");

            if (sysErr)
            {
                string m = "[CSPAT00600:SYS " + (code ?? "") + "] " + (msg ?? "");
                _log.Error(m);
                FailPendingAck(m);
                RaiseOrderRejected(m);
            }
        }

        private void Cspat00600_ReceiveData(string tr)
        {
            try
            {
                string ordNoStr = GetFieldFirstTrim(_cspat00600,
                    "CSPAT00600OutBlock2", "OrdNo",
                    "CSPAT00600OutBlock1", "OrdNo",
                    "CSPAT00600OutBlock2", "ordno",
                    "CSPAT00600OutBlock1", "ordno"
                );

                string ordTime = GetFieldFirstTrim(_cspat00600,
                    "CSPAT00600OutBlock2", "OrdTime",
                    "CSPAT00600OutBlock1", "OrdTime",
                    "CSPAT00600OutBlock2", "ordtime",
                    "CSPAT00600OutBlock1", "ordtime"
                );

                string rspCode = GetFieldFirstTrim(_cspat00600,
                    "CSPAT00600OutBlock1", "RspCode",
                    "CSPAT00600OutBlock1", "rspcode",
                    "CSPAT00600OutBlock1", "MsgCode",
                    "CSPAT00600OutBlock1", "msgcode",
                    "CSPAT00600OutBlock1", "OrdRtnCode",
                    "CSPAT00600OutBlock1", "ordrtncode"
                );

                string rspMsg = GetFieldFirstTrim(_cspat00600,
                    "CSPAT00600OutBlock1", "Msg",
                    "CSPAT00600OutBlock1", "msg",
                    "CSPAT00600OutBlock1", "RspMsg",
                    "CSPAT00600OutBlock1", "rspmsg",
                    "CSPAT00600OutBlock1", "OrdRtnMsg",
                    "CSPAT00600OutBlock1", "ordrtnmsg"
                );

                string last = BuildLastMsgSuffix();
                _log.Info("[CSPAT00600 ACK] " +
                          $"tr='{tr}', OrdNoRaw=\"{ordNoStr ?? ""}\", OrdTimeRaw=\"{ordTime ?? ""}\", " +
                          $"RspCodeRaw=\"{rspCode ?? ""}\", RspMsgRaw=\"{rspMsg ?? ""}\", lastMsg={last}");

                long ordNo;
                if (string.IsNullOrEmpty(ordNoStr) || !long.TryParse(ordNoStr.Trim(), out ordNo) || ordNo <= 0)
                {
                    string msg =
                        "OrdNo 파싱 실패(raw=\"" + (ordNoStr ?? "") + "\")" +
                        (string.IsNullOrEmpty(ordTime) ? "" : " / OrdTime=\"" + ordTime + "\"") +
                        (string.IsNullOrEmpty(rspCode) ? "" : " / RspCode=\"" + rspCode + "\"") +
                        (string.IsNullOrEmpty(rspMsg) ? "" : " / RspMsg=\"" + rspMsg + "\"") +
                        last;

                    _log.Error("[CSPAT00600 ACK FAIL] " + msg);

                    var nack = new OrderAck
                    {
                        Accepted = false,
                        OrderNo = null,
                        Message = msg
                    };

                    CompletePendingAck(nack);
                    RaiseOrderRejected(msg);
                    return;
                }

                var ack = new OrderAck
                {
                    Accepted = true,
                    OrderNo = ordNoStr.Trim(),
                    Message = "주문전송확인(OrdNo 수신)" + (string.IsNullOrEmpty(ordTime) ? "" : (" t=" + ordTime))
                };

                _log.Info("[CSPAT00600 ACK OK] OrdNo=" + ack.OrderNo);

                CompletePendingAck(ack);
                RaiseOrderAccepted(ack);
            }
            catch (Exception ex)
            {
                string msg = "CSPAT00600 ReceiveData 예외: " + ex.Message + BuildLastMsgSuffix();
                _log.Error("[CSPAT00600 ReceiveData] " + msg);

                var nack = new OrderAck { Accepted = false, OrderNo = null, Message = msg };
                CompletePendingAck(nack);
                RaiseOrderRejected(msg);
            }
        }

        // ─────────────────────────────────────────────
        // 취소 (CSPAT00800)
        // ─────────────────────────────────────────────
        public async Task<bool> CancelAsync(string accountNo, string pwd, string orderNo, string symbol)
        {
            try
            {
                string acnt = (accountNo ?? "").Trim();
                string pw = (pwd ?? "").Trim();
                string ord = (orderNo ?? "").Trim();
                string sym = (symbol ?? "").Trim();

                if (sym.Length == 6 && !sym.StartsWith("A"))
                    sym = "A" + sym;

                if (string.IsNullOrEmpty(acnt))
                {
                    string msg = "취소 실패: 계좌번호 비어있음";
                    _log.Error("[CancelAsync] " + msg);
                    RaiseCancelRejected(msg);
                    return false;
                }

                if (string.IsNullOrEmpty(pw))
                {
                    string msg = "취소 실패: 비밀번호 비어있음";
                    _log.Error("[CancelAsync] " + msg);
                    RaiseCancelRejected(msg);
                    return false;
                }

                if (string.IsNullOrEmpty(ord))
                {
                    string msg = "취소 실패: 원주문번호 비어있음";
                    _log.Error("[CancelAsync] " + msg);
                    RaiseCancelRejected(msg);
                    return false;
                }

                if (string.IsNullOrEmpty(sym))
                {
                    string msg = "취소 실패: 종목코드 비어있음";
                    _log.Error("[CancelAsync] " + msg);
                    RaiseCancelRejected(msg);
                    return false;
                }

                TaskCompletionSource<CancelAck> tcs;

                lock (_cancelAckLock)
                {
                    _lastCancelSysErr = false;
                    _lastCancelMsgCode = null;
                    _lastCancelMsgText = null;

                    _pendingCancelOrgOrdNo = ord;
                    _cspat00800AckTcs = new TaskCompletionSource<CancelAck>(TaskCreationOptions.RunContinuationsAsynchronously);
                    tcs = _cspat00800AckTcs;
                }

                _log.Info($"[CancelAsync] ENTER acnt='{acnt}' ordNo='{ord}' symbol='{sym}' pwLen={pw.Length}");

                int ret = await InvokeOnUiAsync(() =>
                {
                    _cspat00800.SetFieldData("CSPAT00800InBlock1", "OrgOrdNo", 0, ord);
                    _cspat00800.SetFieldData("CSPAT00800InBlock1", "AcntNo", 0, acnt);
                    _cspat00800.SetFieldData("CSPAT00800InBlock1", "InptPwd", 0, pw);
                    _cspat00800.SetFieldData("CSPAT00800InBlock1", "IsuNo", 0, sym);

                    return _cspat00800.Request(false);
                }).ConfigureAwait(false);

                _log.Info("[CSPAT00800 Request] ret=" + ret);

                if (ret < 0)
                {
                    string msg = "취소 전송 실패(code=" + ret + ")" + BuildLastCancelMsgSuffix();
                    _log.Error("[CancelAsync] " + msg);
                    FailPendingCancelAck(msg);
                    RaiseCancelRejected(msg);
                    return false;
                }

                var completed = await Task.WhenAny(tcs.Task, Task.Delay(2500)).ConfigureAwait(false);
                if (completed != tcs.Task)
                {
                    string msg = "취소확인 타임아웃(OrgOrdNo 미수신)" + BuildLastCancelMsgSuffix();
                    _log.Error("[CancelAsync] " + msg);
                    FailPendingCancelAck(msg);
                    RaiseCancelRejected(msg);
                    return false;
                }

                CancelAck ack = tcs.Task.Result;
                _log.Info("[CancelAsync] RETURN Accepted=" + ack.Accepted +
                          " OrgOrdNo=" + (ack.OrgOrderNo ?? "null") +
                          " CancelOrdNo=" + (ack.CancelOrderNo ?? "null") +
                          " Msg=" + (ack.Message ?? ""));

                return ack.Accepted;
            }
            catch (Exception ex)
            {
                string msg = "취소 전송 예외: " + ex.Message + BuildLastCancelMsgSuffix();
                _log.Error("[CancelAsync] " + msg);
                FailPendingCancelAck(msg);
                RaiseCancelRejected(msg);
                return false;
            }
        }

        private void Cspat00800_ReceiveMessage(bool sysErr, string code, string msg)
        {
            _lastCancelSysErr = sysErr;
            _lastCancelMsgCode = code;
            _lastCancelMsgText = msg;

            _log.Info($"[CSPAT00800 ReceiveMessage] sysErr={sysErr}, code='{code ?? ""}', msg='{msg ?? ""}'");

            if (sysErr)
            {
                string m = "[CSPAT00800:SYS " + (code ?? "") + "] " + (msg ?? "");
                _log.Error(m);
                FailPendingCancelAck(m);
                RaiseCancelRejected(m);
            }
        }

        private void Cspat00800_ReceiveData(string tr)
        {
            try
            {
                string orgOrdNo = GetFieldFirstTrim(_cspat00800,
                    "CSPAT00800OutBlock2", "OrgOrdNo",
                    "CSPAT00800OutBlock1", "OrgOrdNo",
                    "CSPAT00800OutBlock2", "orgordno",
                    "CSPAT00800OutBlock1", "orgordno"
                );

                string cancelOrdNo = GetFieldFirstTrim(_cspat00800,
                    "CSPAT00800OutBlock2", "OrdNo",
                    "CSPAT00800OutBlock1", "OrdNo",
                    "CSPAT00800OutBlock2", "ordno",
                    "CSPAT00800OutBlock1", "ordno"
                );

                string rspCode = GetFieldFirstTrim(_cspat00800,
                    "CSPAT00800OutBlock1", "RspCode",
                    "CSPAT00800OutBlock1", "rspcode",
                    "CSPAT00800OutBlock1", "MsgCode",
                    "CSPAT00800OutBlock1", "msgcode",
                    "CSPAT00800OutBlock1", "OrdRtnCode",
                    "CSPAT00800OutBlock1", "ordrtncode"
                );

                string rspMsg = GetFieldFirstTrim(_cspat00800,
                    "CSPAT00800OutBlock1", "Msg",
                    "CSPAT00800OutBlock1", "msg",
                    "CSPAT00800OutBlock1", "RspMsg",
                    "CSPAT00800OutBlock1", "rspmsg",
                    "CSPAT00800OutBlock1", "OrdRtnMsg",
                    "CSPAT00800OutBlock1", "ordrtnmsg"
                );

                string pendingOrgOrdNo = "";
                lock (_cancelAckLock)
                {
                    pendingOrgOrdNo = _pendingCancelOrgOrdNo ?? "";
                }

                if (string.IsNullOrWhiteSpace(orgOrdNo))
                    orgOrdNo = pendingOrgOrdNo;

                string last = BuildLastCancelMsgSuffix();

                _log.Info("[CSPAT00800 ACK] " +
                          $"tr='{tr}', OrgOrdNoRaw=\"{orgOrdNo ?? ""}\", CancelOrdNoRaw=\"{cancelOrdNo ?? ""}\", " +
                          $"RspCodeRaw=\"{rspCode ?? ""}\", RspMsgRaw=\"{rspMsg ?? ""}\", lastMsg={last}");

                if (string.IsNullOrWhiteSpace(orgOrdNo))
                {
                    string msg =
                        "취소확인 OrgOrdNo 파싱 실패" +
                        (string.IsNullOrEmpty(cancelOrdNo) ? "" : " / CancelOrdNo=\"" + cancelOrdNo + "\"") +
                        (string.IsNullOrEmpty(rspCode) ? "" : " / RspCode=\"" + rspCode + "\"") +
                        (string.IsNullOrEmpty(rspMsg) ? "" : " / RspMsg=\"" + rspMsg + "\"") +
                        last;

                    _log.Error("[CSPAT00800 ACK FAIL] " + msg);

                    var nack = new CancelAck
                    {
                        Accepted = false,
                        OrgOrderNo = null,
                        CancelOrderNo = string.IsNullOrWhiteSpace(cancelOrdNo) ? null : cancelOrdNo.Trim(),
                        Message = msg
                    };

                    CompletePendingCancelAck(nack);
                    RaiseCancelRejected(msg);
                    return;
                }

                var ack = new CancelAck
                {
                    Accepted = true,
                    OrgOrderNo = orgOrdNo.Trim(),
                    CancelOrderNo = string.IsNullOrWhiteSpace(cancelOrdNo) ? null : cancelOrdNo.Trim(),
                    Message = "취소확인 수신"
                };

                _log.Info("[CSPAT00800 ACK OK] OrgOrdNo=" + ack.OrgOrderNo +
                          " CancelOrdNo=" + (ack.CancelOrderNo ?? ""));

                CompletePendingCancelAck(ack);
                RaiseCancelConfirmed(ack.OrgOrderNo);
            }
            catch (Exception ex)
            {
                string msg = "CSPAT00800 ReceiveData 예외: " + ex.Message + BuildLastCancelMsgSuffix();
                _log.Error("[CSPAT00800 ReceiveData] " + msg);

                var nack = new CancelAck
                {
                    Accepted = false,
                    OrgOrderNo = null,
                    CancelOrderNo = null,
                    Message = msg
                };

                CompletePendingCancelAck(nack);
                RaiseCancelRejected(msg);
            }
        }

        private string BuildLastMsgSuffix()
        {
            string c = _lastMsgCode ?? "";
            string t = _lastMsgText ?? "";
            return $" / lastMsg=({_lastSysErr},{c},{t})";
        }

        private string BuildLastCancelMsgSuffix()
        {
            string c = _lastCancelMsgCode ?? "";
            string t = _lastCancelMsgText ?? "";
            return $" / lastCancelMsg=({_lastCancelSysErr},{c},{t})";
        }

        private void CompletePendingAck(OrderAck ack)
        {
            TaskCompletionSource<OrderAck> tcs = null;
            lock (_ackLock)
            {
                tcs = _cspat00600AckTcs;
                _cspat00600AckTcs = null;
            }

            if (tcs != null)
                tcs.TrySetResult(ack);
        }

        private void FailPendingAck(string msg)
        {
            var ack = new OrderAck
            {
                Accepted = false,
                OrderNo = null,
                Message = msg
            };
            CompletePendingAck(ack);
        }

        private void CompletePendingCancelAck(CancelAck ack)
        {
            TaskCompletionSource<CancelAck> tcs = null;
            lock (_cancelAckLock)
            {
                tcs = _cspat00800AckTcs;
                _cspat00800AckTcs = null;
                _pendingCancelOrgOrdNo = null;
            }

            if (tcs != null)
                tcs.TrySetResult(ack);
        }

        private void FailPendingCancelAck(string msg)
        {
            var ack = new CancelAck
            {
                Accepted = false,
                OrgOrderNo = null,
                CancelOrderNo = null,
                Message = msg
            };
            CompletePendingCancelAck(ack);
        }

        // ─────────────────────────────────────────────
        // 미체결/주문 조회 (t0425)
        // ─────────────────────────────────────────────
        public async Task<IList<OrderRow>> LoadOpenOrdersAsync(string accountNo, string pwd, string symbol = null)
        {
            var tcs = new TaskCompletionSource<IList<OrderRow>>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock) { _t0425Tcs = tcs; }

            try
            {
                string acc = accountNo ?? "";
                string pw = pwd ?? "";
                string sym = symbol ?? "";

                _log.Info($"[t0425] Request ENTER acc='{acc}' pwLen={pw.Length} symbol='{sym}' " +
                          $"callerThread={Thread.CurrentThread.ManagedThreadId}/{Thread.CurrentThread.GetApartmentState()} owner={_ownerThreadId}/{_ownerApt}");

                int ret = await InvokeOnUiAsync(() =>
                {
                    _t0425.SetFieldData("t0425InBlock", "accno", 0, acc);
                    _t0425.SetFieldData("t0425InBlock", "passwd", 0, pw);
                    _t0425.SetFieldData("t0425InBlock", "expcode", 0, string.IsNullOrEmpty(sym) ? "" : sym);
                    _t0425.SetFieldData("t0425InBlock", "chegb", 0, "0");
                    _t0425.SetFieldData("t0425InBlock", "medosu", 0, "0");
                    _t0425.SetFieldData("t0425InBlock", "sortgb", 0, "1");
                    _t0425.SetFieldData("t0425InBlock", "cts_ordno", 0, "");

                    return _t0425.Request(false);
                }).ConfigureAwait(false);

                _log.Info("[t0425] Request ret=" + ret);
                if (ret < 0)
                {
                    lock (_lock) { if (_t0425Tcs == tcs) _t0425Tcs = null; }
                    return new List<OrderRow>();
                }
            }
            catch (Exception ex)
            {
                lock (_lock) { if (_t0425Tcs == tcs) _t0425Tcs = null; }

                string msg = "t0425 전송 예외: " + ex.Message;
                _log.Error("[t0425] " + msg);
                RaiseOrderRejected(msg);
                return new List<OrderRow>();
            }

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(2000)).ConfigureAwait(false);
            if (completed != tcs.Task)
            {
                _log.Warn("[t0425] timeout");
                lock (_lock) { if (_t0425Tcs == tcs) _t0425Tcs = null; }
                return new List<OrderRow>();
            }

            var result = tcs.Task.Result ?? new List<OrderRow>();
            lock (_lock) { if (_t0425Tcs == tcs) _t0425Tcs = null; }
            return result;
        }

        public async Task<bool> TryRecoverTimedOutOrderAsync(string requestId)
        {
            var candidate = _0540_주문복구관리.Get(requestId);
            if (candidate == null || candidate.RecoveryState != OrderRecoveryState.Pending)
                return false;

            await _recoveryQueryGate.WaitAsync().ConfigureAwait(false);
            try
            {
                candidate = _0540_주문복구관리.Get(requestId);
                if (candidate == null || candidate.RecoveryState != OrderRecoveryState.Pending)
                    return false;

                var tcs = new TaskCompletionSource<IList<OrderRow>>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_recoveryQueryLock) { _t0425RecoveryTcs = tcs; }

                _log.Info("[0540][T0425] requestId=" + requestId + " dedicated query START");
                int ret = await InvokeOnUiAsync(() =>
                {
                    _t0425Recovery.SetFieldData("t0425InBlock", "accno", 0, candidate.AccountNo ?? "");
                    _t0425Recovery.SetFieldData("t0425InBlock", "passwd", 0, Login.JMpass ?? "");
                    _t0425Recovery.SetFieldData("t0425InBlock", "expcode", 0, candidate.Symbol ?? "");
                    _t0425Recovery.SetFieldData("t0425InBlock", "chegb", 0, "0");
                    _t0425Recovery.SetFieldData("t0425InBlock", "medosu", 0, "0");
                    _t0425Recovery.SetFieldData("t0425InBlock", "sortgb", 0, "1");
                    _t0425Recovery.SetFieldData("t0425InBlock", "cts_ordno", 0, "");
                    return _t0425Recovery.Request(false);
                }).ConfigureAwait(false);

                if (ret < 0)
                {
                    _log.Warn("[0540][T0425] requestId=" + requestId + " request ret=" + ret);
                    return false;
                }

                var completed = await Task.WhenAny(tcs.Task, Task.Delay(2500)).ConfigureAwait(false);
                if (completed != tcs.Task)
                {
                    _log.Warn("[0540][T0425] requestId=" + requestId + " timeout");
                    return false;
                }

                var rows = tcs.Task.Result ?? new List<OrderRow>();
                var matches = rows
                    .Where(r => r != null)
                    .Where(r => SideMatches(candidate.Side, r.Side))
                    .Where(r => r.Qty == candidate.Qty)
                    .Where(r => r.Price > 0 && Math.Abs(r.Price - candidate.Price) <= 1.0)
                    .Where(r => SymbolMatches(candidate.Symbol, r.Symbol))
                    .Where(r => Math.Abs((r.Ts.ToUniversalTime() - candidate.SendTimeUtc).TotalSeconds) <= 60)
                    .Where(r => !string.IsNullOrWhiteSpace(r.OrderNo))
                    .GroupBy(r => r.OrderNo.Trim())
                    .Select(g => g.First())
                    .Take(2)
                    .ToList();

                foreach (var row in matches)
                {
                    _log.Info("[0540][T0425] candidate requestId=" + requestId +
                              " ordNo=" + row.OrderNo + " side=" + row.Side +
                              " qty=" + row.Qty + " price=" + row.Price + " time=" + row.Ts.ToString("HH:mm:ss"));
                }

                if (matches.Count != 1)
                {
                    _log.Warn("[0540][T0425] requestId=" + requestId + " unique candidate not found count=" + matches.Count);
                    return false;
                }

                long ordNo;
                if (!long.TryParse(matches[0].OrderNo.Trim(), out ordNo) || ordNo <= 0)
                    return false;

                return _0540_주문복구관리.TryRecoverAndRegister(requestId, ordNo, "T0425");
            }
            catch (Exception ex)
            {
                _log.Error("[0540][T0425] requestId=" + requestId + " EX " + ex.Message);
                return false;
            }
            finally
            {
                lock (_recoveryQueryLock) { _t0425RecoveryTcs = null; }
                _recoveryQueryGate.Release();
            }
        }

        private void T0425Recovery_ReceiveData(string tr)
        {
            var rows = new List<OrderRow>();
            try
            {
                int cnt = _t0425Recovery.GetBlockCount("t0425OutBlock1");
                for (int i = 0; i < cnt; i++)
                {
                    string sideRaw = SafeTrim(_t0425Recovery.GetFieldData("t0425OutBlock1", "medosu", i));
                    if (string.IsNullOrEmpty(sideRaw))
                        sideRaw = SafeTrim(_t0425Recovery.GetFieldData("t0425OutBlock1", "bnstp", i));

                    rows.Add(new OrderRow
                    {
                        OrderNo = SafeTrim(_t0425Recovery.GetFieldData("t0425OutBlock1", "ordno", i)),
                        Symbol = SafeTrim(_t0425Recovery.GetFieldData("t0425OutBlock1", "expcode", i)),
                        Side = sideRaw == "1" || sideRaw == "매도" ? TradeSide.Sell : TradeSide.Buy,
                        Qty = ParseLong(SafeTrim(_t0425Recovery.GetFieldData("t0425OutBlock1", "qty", i))),
                        Price = ParseDouble(SafeTrim(_t0425Recovery.GetFieldData("t0425OutBlock1", "price", i))),
                        Status = SafeTrim(_t0425Recovery.GetFieldData("t0425OutBlock1", "status", i)),
                        Ts = ParseTime(SafeTrim(_t0425Recovery.GetFieldData("t0425OutBlock1", "ordtime", i)))
                    });
                }
            }
            catch (Exception ex)
            {
                _log.Error("[0540][T0425] parse EX " + ex.Message);
            }

            TaskCompletionSource<IList<OrderRow>> tcs;
            lock (_recoveryQueryLock) { tcs = _t0425RecoveryTcs; }
            tcs?.TrySetResult(rows);
        }

        private static bool SideMatches(string side, TradeSide rowSide)
        {
            side = (side ?? "").Trim();
            bool sell = side == "매도" || string.Equals(side, "SELL", StringComparison.OrdinalIgnoreCase);
            return sell ? rowSide == TradeSide.Sell : rowSide == TradeSide.Buy;
        }

        private static bool SymbolMatches(string expected, string actual)
        {
            expected = NormalizeSymbolForRecovery(expected);
            actual = NormalizeSymbolForRecovery(actual);
            return string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual) || expected == actual;
        }

        private static string NormalizeSymbolForRecovery(string symbol)
        {
            symbol = (symbol ?? "").Trim().ToUpperInvariant();
            return symbol.StartsWith("A", StringComparison.Ordinal) ? symbol.Substring(1) : symbol;
        }


        private void T0425_ReceiveData(string tr)
        {
            try
            {
                _log.Info($"[T0425 ReceiveData] tr='{tr}' thread={Thread.CurrentThread.ManagedThreadId}/{Thread.CurrentThread.GetApartmentState()}");

                var rows = new List<OrderRow>();
                int cnt = _t0425.GetBlockCount("t0425OutBlock1");
                _log.Info($"[T0425] t0425OutBlock1 count={cnt}");

                for (int i = 0; i < cnt; i++)
                {
                    string ordNo = SafeTrim(_t0425.GetFieldData("t0425OutBlock1", "ordno", i));
                    string isuNo = SafeTrim(_t0425.GetFieldData("t0425OutBlock1", "expcode", i));

                    string bsRaw = SafeTrim(_t0425.GetFieldData("t0425OutBlock1", "bnstp", i));
                    string msRaw = SafeTrim(_t0425.GetFieldData("t0425OutBlock1", "medosu", i));

                    string qtyStr = SafeTrim(_t0425.GetFieldData("t0425OutBlock1", "qty", i));

                    // --------------------------------------------------
                    // 미체결 수량 후보 필드들
                    // 기존: unercqty
                    // 추가: 미체결/잔량 가능성이 있는 후보를 함께 로그 출력
                    // --------------------------------------------------
                    string rmStr_unercqty = SafeTrim(_t0425.GetFieldData("t0425OutBlock1", "unercqty", i));
                    string rmStr_ordrem = SafeTrim(_t0425.GetFieldData("t0425OutBlock1", "ordrem", i));
                    string rmStr_mdfycnfqty = SafeTrim(_t0425.GetFieldData("t0425OutBlock1", "mdfycnfqty", i));
                    string rmStr_orgrem = SafeTrim(_t0425.GetFieldData("t0425OutBlock1", "orgordrem", i));
                    string rmStr_miqty = SafeTrim(_t0425.GetFieldData("t0425OutBlock1", "miqty", i));
                    string rmStr_jqty = SafeTrim(_t0425.GetFieldData("t0425OutBlock1", "janqty", i));

                    // 실제 사용값은 일단 기존 우선순위 유지 + 후보 fallback
                    string rmStr = rmStr_unercqty;
                    if (string.IsNullOrEmpty(rmStr)) rmStr = rmStr_ordrem;
                    if (string.IsNullOrEmpty(rmStr)) rmStr = rmStr_miqty;
                    if (string.IsNullOrEmpty(rmStr)) rmStr = rmStr_jqty;
                    if (string.IsNullOrEmpty(rmStr)) rmStr = rmStr_orgrem;
                    if (string.IsNullOrEmpty(rmStr)) rmStr = rmStr_mdfycnfqty;

                    string prcStr = SafeTrim(_t0425.GetFieldData("t0425OutBlock1", "price", i));
                    string st = SafeTrim(_t0425.GetFieldData("t0425OutBlock1", "status", i));
                    string time = SafeTrim(_t0425.GetFieldData("t0425OutBlock1", "ordtime", i));

                    // 체결/확인 관련 후보 필드도 로그
                    string cheqtyStr = SafeTrim(_t0425.GetFieldData("t0425OutBlock1", "cheqty", i));
                    string execqtyStr = SafeTrim(_t0425.GetFieldData("t0425OutBlock1", "execqty", i));
                    string cfmqtyStr = SafeTrim(_t0425.GetFieldData("t0425OutBlock1", "cfmqty", i));
                    string trqtyStr = SafeTrim(_t0425.GetFieldData("t0425OutBlock1", "trqty", i));

                    string sideSource = !string.IsNullOrEmpty(msRaw) ? msRaw : bsRaw;

                    TradeSide side;
                    if (sideSource == "1" || sideSource == "매도")
                        side = TradeSide.Sell;
                    else if (sideSource == "2" || sideSource == "매수")
                        side = TradeSide.Buy;
                    else
                        side = TradeSide.Buy;

                    long qty = ParseLong(qtyStr);
                    long remainQty = ParseLong(rmStr);
                    double price = ParseDouble(prcStr);
                    DateTime ts = ParseTime(time);

                    _log.Info(
                        "[T0425 ROW] " +
                        "idx=" + i +
                        ", ordNo='" + ordNo + "'" +
                        ", expcode='" + isuNo + "'" +
                        ", bnstp='" + bsRaw + "'" +
                        ", medosu='" + msRaw + "'" +
                        ", qty='" + qtyStr + "'" +
                        ", price='" + prcStr + "'" +
                        ", status='" + st + "'" +
                        ", ordtime='" + time + "'" +
                        ", unercqty='" + rmStr_unercqty + "'" +
                        ", ordrem='" + rmStr_ordrem + "'" +
                        ", miqty='" + rmStr_miqty + "'" +
                        ", janqty='" + rmStr_jqty + "'" +
                        ", orgordrem='" + rmStr_orgrem + "'" +
                        ", mdfycnfqty='" + rmStr_mdfycnfqty + "'" +
                        ", cheqty='" + cheqtyStr + "'" +
                        ", execqty='" + execqtyStr + "'" +
                        ", cfmqty='" + cfmqtyStr + "'" +
                        ", trqty='" + trqtyStr + "'" +
                        ", parsedQty=" + qty +
                        ", parsedRemain=" + remainQty +
                        ", parsedPrice=" + price.ToString(CultureInfo.InvariantCulture) +
                        ", parsedSide=" + side.ToString()
                    );

                    rows.Add(new OrderRow
                    {
                        OrderNo = ordNo,
                        Symbol = isuNo,
                        Side = side,
                        Qty = qty,
                        RemainQty = remainQty,
                        Price = price,
                        Status = st,
                        Ts = ts
                    });
                }

                // alive 후보가 몇 개인지도 로그
                try
                {
                    int aliveCount = 0;
                    for (int i = 0; i < rows.Count; i++)
                    {
                        if (rows[i] != null && rows[i].RemainQty > 0)
                            aliveCount++;
                    }

                    _log.Info("[T0425] parsed rows=" + rows.Count + ", remainQty>0 rows=" + aliveCount);
                }
                catch { }

                TaskCompletionSource<IList<OrderRow>> tcs;
                lock (_lock) { tcs = _t0425Tcs; }
                if (tcs != null) tcs.TrySetResult(rows);
            }
            catch (Exception ex)
            {
                string msg = "t0425 parse exception: " + ex.Message;
                _log.Error("[T0425] " + msg);
                RaiseOrderRejected(msg);

                TaskCompletionSource<IList<OrderRow>> tcs;
                lock (_lock) { tcs = _t0425Tcs; }
                if (tcs != null) tcs.TrySetResult(new List<OrderRow>());
            }
        }
        // ─────────────────────────────────────────────
        // 유틸
        // ─────────────────────────────────────────────
        private static string SafeTrim(string s)
        {
            return string.IsNullOrEmpty(s) ? string.Empty : s.Trim();
        }

        private static long ParseLong(string s)
        {
            long v;
            return long.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out v) ? v : 0L;
        }

        private static double ParseDouble(string s)
        {
            double v;
            return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out v) ? v : 0.0;
        }

        private static DateTime ParseTime(string hhmmss)
        {
            if (string.IsNullOrEmpty(hhmmss) || hhmmss.Length < 6)
                return DateTime.Now;

            int h = int.Parse(hhmmss.Substring(0, 2));
            int m = int.Parse(hhmmss.Substring(2, 2));
            int s = int.Parse(hhmmss.Substring(4, 2));

            DateTime now = DateTime.Now;
            return new DateTime(now.Year, now.Month, now.Day, h, m, s);
        }

        private void RaiseOrderAccepted(OrderAck ack)
        {
            var h = OrderAccepted;
            if (h != null) h(ack);
        }

        private void RaiseOrderRejected(string msg)
        {
            var h = OrderRejected;
            if (h != null) h(msg);
        }

        private void RaiseOrderUpdated(OrderRow r)
        {
            var h = OrderUpdated;
            if (h != null) h(r);
        }

        private void RaiseFillReceived(FillEvent fe)
        {
            var h = FillReceived;
            if (h != null) h(fe);
        }

        private void RaiseCancelConfirmed(string orgOrdNo)
        {
            var h = CancelConfirmed;
            if (h != null) h(orgOrdNo);
        }

        private void RaiseCancelRejected(string msg)
        {
            var h = CancelRejected;
            if (h != null) h(msg);
        }

        // ✅ 후보들을 순서대로 시도해서 첫 번째 유효한 값을 반환
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
                    v = SafeTrim(v);
                    if (!string.IsNullOrEmpty(v))
                        return v;
                }
                catch
                {
                    // 후보 불일치면 다음 후보 진행
                }
            }

            return string.Empty;
        }
    }
}
// 2026-04-12 41827
