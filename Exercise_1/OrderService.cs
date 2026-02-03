// OrderService.cs  (C# 7.3)
// - XING TR 연동 주문 서비스 (인터페이스 IOrderService 제거 버전)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using XA_DATASETLib;

namespace Exercise_1
{
    // ⛔ IOrderService 제거
    public sealed class OrderService : IDisposable
    {
        // XING TR 객체
        private readonly XAQueryClass _cspat00600 = new XAQueryClass(); // 주문
        private readonly XAQueryClass _cspat00800 = new XAQueryClass(); // 취소
        private readonly XAQueryClass _t0425 = new XAQueryClass();      // 주문/체결 조회

        // 동기화/대기
        private readonly object _lock = new object();
        private TaskCompletionSource<IList<OrderRow>> _t0425Tcs;

        // 이벤트
        public event Action<OrderAck> OrderAccepted;
        public event Action<string> OrderRejected;
        public event Action<OrderRow> OrderUpdated;
        public event Action<FillEvent> FillReceived;

        public OrderService()
        {
            _cspat00600.ResFileName = @"C:\LS_SEC\xingAPI\Res\CSPAT00600.res";
            _cspat00800.ResFileName = @"C:\LS_SEC\xingAPI\Res\CSPAT00800.res";
            _t0425.ResFileName = @"C:\LS_SEC\xingAPI\Res\t0425.res"; // 소문자

            _cspat00600.ReceiveData += new _IXAQueryEvents_ReceiveDataEventHandler(Cspat00600_ReceiveData);
            _cspat00600.ReceiveMessage += new _IXAQueryEvents_ReceiveMessageEventHandler(Cspat00600_ReceiveMessage);

            _cspat00800.ReceiveData += new _IXAQueryEvents_ReceiveDataEventHandler(Cspat00800_ReceiveData);
            _cspat00800.ReceiveMessage += new _IXAQueryEvents_ReceiveMessageEventHandler(Cspat00800_ReceiveMessage);

            _t0425.ReceiveData += new _IXAQueryEvents_ReceiveDataEventHandler(T0425_ReceiveData);
        }

        public void Dispose()
        {
            _cspat00600.ReceiveData -= new _IXAQueryEvents_ReceiveDataEventHandler(Cspat00600_ReceiveData);
            _cspat00600.ReceiveMessage -= new _IXAQueryEvents_ReceiveMessageEventHandler(Cspat00600_ReceiveMessage);
            _cspat00800.ReceiveData -= new _IXAQueryEvents_ReceiveDataEventHandler(Cspat00800_ReceiveData);
            _cspat00800.ReceiveMessage -= new _IXAQueryEvents_ReceiveMessageEventHandler(Cspat00800_ReceiveMessage);
            _t0425.ReceiveData -= new _IXAQueryEvents_ReceiveDataEventHandler(T0425_ReceiveData);
        }

        // ─────────────────────────────────────────────
        // 주문
        // ─────────────────────────────────────────────
        //public Task<OrderAck> PlaceAsync(OrderRequest req)
        //{
        //    if (req == null) throw new ArgumentNullException(nameof(req));

        //    return Task.Run<OrderAck>(() =>
        //    {
        //        try
        //        {
        //            _cspat00600.SetFieldData("CSPAT00600InBlock1", "AcntNo", 0, req.AccountNo);
        //            _cspat00600.SetFieldData("CSPAT00600InBlock1", "InptPwd", 0, req.Password);
        //            _cspat00600.SetFieldData("CSPAT00600InBlock1", "IsuNo", 0, req.Symbol);
        //            _cspat00600.SetFieldData("CSPAT00600InBlock1", "OrdQty", 0,
        //                req.Qty.ToString(CultureInfo.InvariantCulture));

        //            _cspat00600.SetFieldData(
        //                "CSPAT00600InBlock1",
        //                "OrdPrc",
        //                0,
        //                (req.Type == OrderType.Market
        //                    ? "0"
        //                    : req.Price.ToString(CultureInfo.InvariantCulture))
        //            );

        //            _cspat00600.SetFieldData("CSPAT00600InBlock1", "BnsTpCode", 0,
        //                ((int)req.Side).ToString()); // 1/2
        //            _cspat00600.SetFieldData("CSPAT00600InBlock1", "OrdprcPtnCode", 0,
        //                (req.Type == OrderType.Market ? "03" : "00")); // 03=시장가, 00=지정가
        //            _cspat00600.SetFieldData("CSPAT00600InBlock1", "MgntrnCode", 0, "000");
        //            _cspat00600.SetFieldData("CSPAT00600InBlock1", "LoanDt", 0, "");
        //            _cspat00600.SetFieldData("CSPAT00600InBlock1", "OrdCndiTpCode", 0, "0");

        //            int ret = _cspat00600.Request(false);
        //            if (ret < 0)
        //            {
        //                RaiseOrderRejected("주문 전송 실패(code=" + ret + ")");
        //                return new OrderAck
        //                {
        //                    Accepted = false,
        //                    Message = "전송 실패 " + ret,
        //                    OrderNo = null
        //                };
        //            }

        //            return new OrderAck
        //            {
        //                Accepted = true,
        //                Message = "전송 완료(접수 대기)",
        //                OrderNo = null
        //            };
        //        }
        //        catch (Exception ex)
        //        {
        //            RaiseOrderRejected("주문 전송 예외: " + ex.Message);
        //            return new OrderAck
        //            {
        //                Accepted = false,
        //                Message = ex.Message,
        //                OrderNo = null
        //            };
        //        }
        //    });
        //}
        public Task<OrderAck> PlaceAsync(OrderRequest req)
        {
            if (req == null) throw new ArgumentNullException(nameof(req));

            // ─────────────────────────────────────────────
            // ① 먼저 디버그 로그 (원본 요청)
            // ─────────────────────────────────────────────
            System.Diagnostics.Debug.WriteLine(
                "[PlaceAsync:Request] " +
                $"AccountNo='{req.AccountNo}', " +
                $"PasswordLen={(req.Password ?? string.Empty).Length}, " +
                $"Symbol='{req.Symbol}', " +
                $"Qty={req.Qty}, Price={req.Price}, " +
                $"Type={req.Type}, Side={req.Side}"
            );

            return Task.Run<OrderAck>(() =>
            {
                try
                {
                    // ─────────────────────────────────────
                    // ② XING TR 포맷에 맞게 값 보정
                    // ─────────────────────────────────────

                    // 계좌 / 비번 공백 제거
                    string acnt = (req.AccountNo ?? string.Empty).Trim();
                    string pwd = (req.Password ?? string.Empty).Trim();

                    // 종목코드: 6자리면 "A" 붙이기 → "A069500"
                    string symbol = (req.Symbol ?? string.Empty).Trim();
                    if (symbol.Length == 6 && !symbol.StartsWith("A"))
                    {
                        symbol = "A" + symbol;
                    }

                    // 수량: int 로 변환
                    int qtyInt = (int)Convert.ToInt64(req.Qty);
                    if (qtyInt <= 0)
                    {
                        RaiseOrderRejected("수량이 0 이하입니다.");
                        return new OrderAck { Accepted = false, Message = "수량 오류", OrderNo = null };
                    }

                    // 가격: 시장가면 0, 아니면 반올림해서 int
                    int priceInt = 0;
                    if (req.Type != OrderType.Market)
                    {
                        priceInt = (int)Math.Round(req.Price);
                        if (priceInt <= 0)
                        {
                            RaiseOrderRejected("가격이 0 이하입니다.");
                            return new OrderAck { Accepted = false, Message = "가격 오류", OrderNo = null };
                        }
                    }

                    string qtyStr = qtyInt.ToString();
                    string prcStr = (req.Type == OrderType.Market) ? "0" : priceInt.ToString();

                    string bnsTpCode = ((int)req.Side).ToString();              // 1=매도, 2=매수
                    string ordPtnCode = (req.Type == OrderType.Market ? "03" : "00");
                    string mgntrnCode = "000";
                    string loanDt = "";
                    string ordCndiTpCode = "0";

                    // 디버그: 실제 InBlock1에 들어갈 최종 문자열
                    System.Diagnostics.Debug.WriteLine(
                        "[CSPAT00600 InBlock1] " +
                        $"AcntNo='{acnt}', " +
                        $"InptPwdLen={pwd.Length}, " +
                        $"IsuNo='{symbol}', " +
                        $"OrdQty='{qtyStr}', OrdPrc='{prcStr}', " +
                        $"BnsTpCode='{bnsTpCode}', OrdprcPtnCode='{ordPtnCode}', " +
                        $"MgntrnCode='{mgntrnCode}', LoanDt='{loanDt}', " +
                        $"OrdCndiTpCode='{ordCndiTpCode}'"
                    );

                    // ─────────────────────────────────────
                    // ③ XING 필드 세팅 (보정된 값 사용)
                    // ─────────────────────────────────────
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

                    int ret = _cspat00600.Request(false);
                    System.Diagnostics.Debug.WriteLine($"[CSPAT00600 Request] ret={ret}");

                    if (ret < 0)
                    {
                        RaiseOrderRejected("주문 전송 실패(code=" + ret + ")");
                        return new OrderAck { Accepted = false, Message = "전송 실패 " + ret, OrderNo = null };
                    }

                    return new OrderAck { Accepted = true, Message = "전송 완료(접수 대기)", OrderNo = null };
                }
                catch (Exception ex)
                {
                    RaiseOrderRejected("주문 전송 예외: " + ex.Message);
                    return new OrderAck { Accepted = false, Message = ex.Message, OrderNo = null };
                }
            });
        }

        private void Cspat00600_ReceiveMessage(bool sysErr, string code, string msg)
        {
            if (sysErr) RaiseOrderRejected("[CSPAT00600:SYS " + code + "] " + msg);
        }


        private void Cspat00600_ReceiveData(string tr)
        {
            // OutBlock1 에서 OrdNo 읽기 (없을 수 있음)
            string ordNo = _cspat00600.GetFieldData("CSPAT00600OutBlock1", "OrdNo", 0);
            ordNo = string.IsNullOrWhiteSpace(ordNo) ? null : ordNo.Trim();

            System.Diagnostics.Debug.WriteLine($"[CSPAT00600 OutBlock1] OrdNo='{ordNo ?? "(null)"}'");

            // ★ LS 는 OrdNo 를 안 줄 수도 있으므로,
            //    OutBlock 이 왔다는 것 자체를 "전송 성공"으로 본다.
            var ack = new OrderAck
            {
                Accepted = true,   // ★ 무조건 true 로 간주
                OrderNo = ordNo,  // 번호가 없으면 null
                Message = (ordNo == null) ? "주문 전송(번호 미수신)" : "주문 접수"
            };

            // 디버그 로그 및 이벤트 통지
            System.Diagnostics.Debug.WriteLine($"[ACCEPT] {ack.OrderNo} {ack.Message}");
            RaiseOrderAccepted(ack);

            // ⚠ 여기서는 _orderAckTcs 같은 건 사용하지 않습니다.
            //    현재 PlaceAsync 는 Task.Run 내부에서 바로 Ack 를 만들어
            //    리턴하기 때문에, 이벤트를 기다리는 별도 TCS 가 없습니다.
        }




        // ─────────────────────────────────────────────
        // 취소
        // ─────────────────────────────────────────────
        public Task<bool> CancelAsync(string accountNo, string pwd, string orderNo, string symbol)
        {
            return Task.Run<bool>(() =>
            {
                try
                {
                    _cspat00800.SetFieldData("CSPAT00800InBlock1", "AcntNo", 0, accountNo);
                    _cspat00800.SetFieldData("CSPAT00800InBlock1", "InptPwd", 0, pwd);
                    _cspat00800.SetFieldData("CSPAT00800InBlock1", "IsuNo", 0, symbol);
                    _cspat00800.SetFieldData("CSPAT00800InBlock1", "OrgOrdNo", 0, orderNo);
                    int ret = _cspat00800.Request(false);
                    return ret >= 0;
                }
                catch (Exception ex)
                {
                    RaiseOrderRejected("취소 전송 예외: " + ex.Message);
                    return false;
                }
            });
        }

        private void Cspat00800_ReceiveMessage(bool sysErr, string code, string msg)
        {
            if (sysErr) RaiseOrderRejected("[CSPAT00800:SYS " + code + "] " + msg);
        }

        private void Cspat00800_ReceiveData(string tr)
        {
            // 필요 시 OutBlock 파싱 → OrderUpdated/FillReceived 호출
        }

        // ─────────────────────────────────────────────
        // 미체결/주문 조회
        // ─────────────────────────────────────────────
        public async Task<IList<OrderRow>> LoadOpenOrdersAsync(string accountNo, string pwd, string symbol = null)
        {
            var tcs = new TaskCompletionSource<IList<OrderRow>>();
            lock (_lock) { _t0425Tcs = tcs; }

            try
            {
                _t0425.SetFieldData("t0425InBlock", "accno", 0, accountNo);
                _t0425.SetFieldData("t0425InBlock", "passwd", 0, pwd);
                _t0425.SetFieldData("t0425InBlock", "expcode", 0,
                    string.IsNullOrEmpty(symbol) ? "" : symbol);
                _t0425.SetFieldData("t0425InBlock", "chegb", 0, "0");
                _t0425.SetFieldData("t0425InBlock", "medosu", 0, "0");
                _t0425.SetFieldData("t0425InBlock", "sortgb", 0, "1");
                _t0425.SetFieldData("t0425InBlock", "cts_ordno", 0, "");

                int ret = _t0425.Request(false);
                if (ret < 0) return new List<OrderRow>();
            }
            catch (Exception ex)
            {
                RaiseOrderRejected("t0425 전송 예외: " + ex.Message);
                return new List<OrderRow>();
            }

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(2000)).ConfigureAwait(false);
            return completed == tcs.Task
                ? (tcs.Task.Result ?? new List<OrderRow>())
                : new List<OrderRow>();
        }

        private void T0425_ReceiveData(string tr)
        {
            try
            {
                Console.WriteLine($"[T0425] ReceiveData called. tr={tr}");

                var rows = new List<OrderRow>();
                int cnt = _t0425.GetBlockCount("t0425OutBlock1");
                Console.WriteLine($"[T0425] t0425OutBlock1 count = {cnt}");

                for (int i = 0; i < cnt; i++)
                {
                    string ordNo = SafeTrim(_t0425.GetFieldData("t0425OutBlock1", "ordno", i));
                    string isuNo = SafeTrim(_t0425.GetFieldData("t0425OutBlock1", "expcode", i));

                    // Try both fields: bnstp and medosu
                    string bsRaw = SafeTrim(_t0425.GetFieldData("t0425OutBlock1", "bnstp", i));
                    string msRaw = SafeTrim(_t0425.GetFieldData("t0425OutBlock1", "medosu", i));

                    string bs = bsRaw?.Trim();
                    string ms = msRaw?.Trim();

                    string qtyStr = SafeTrim(_t0425.GetFieldData("t0425OutBlock1", "qty", i));
                    string rmStr = SafeTrim(_t0425.GetFieldData("t0425OutBlock1", "unercqty", i));
                    string prcStr = SafeTrim(_t0425.GetFieldData("t0425OutBlock1", "price", i));
                    string st = SafeTrim(_t0425.GetFieldData("t0425OutBlock1", "status", i));
                    string time = SafeTrim(_t0425.GetFieldData("t0425OutBlock1", "ordtime", i));

                    // Decide side string from medosu or bnstp
                    string sideSource = !string.IsNullOrEmpty(ms) ? ms : bs;

                    TradeSide side;
                    if (sideSource == "1" || sideSource == "매도")
                        side = TradeSide.Sell;
                    else if (sideSource == "2" || sideSource == "매수")
                        side = TradeSide.Buy;
                    else
                    {
                        Console.WriteLine(
                            $"[T0425][WARN] ordNo={ordNo} unknown sideSource='{sideSource}' (bnstp='{bsRaw}', medosu='{msRaw}') → default Buy");
                        side = TradeSide.Buy;
                    }

                    Console.WriteLine(
                        $"[T0425] i={i} ordNo={ordNo} expcode={isuNo} bnstp='{bsRaw}' medosu='{msRaw}' mappedSide={side} qty={qtyStr} price={prcStr} time={time}");

                    rows.Add(new OrderRow
                    {
                        OrderNo = ordNo,
                        Symbol = isuNo,
                        Side = side,
                        Qty = ParseLong(qtyStr),
                        RemainQty = ParseLong(rmStr),
                        Price = ParseDouble(prcStr),
                        Status = st,
                        Ts = ParseTime(time)
                    });
                }

                TaskCompletionSource<IList<OrderRow>> tcs;
                lock (_lock) { tcs = _t0425Tcs; }
                if (tcs != null) tcs.TrySetResult(rows);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[T0425][ERROR] " + ex.Message);
                RaiseOrderRejected("t0425 parse exception: " + ex.Message);

                TaskCompletionSource<IList<OrderRow>> tcs;
                lock (_lock) { tcs = _t0425Tcs; }
                if (tcs != null) tcs.TrySetResult(new List<OrderRow>());
            }
        }

        // ─────────────────────────────────────────────
        // 유틸
        // ─────────────────────────────────────────────
        private static string SafeTrim(string s)
            => string.IsNullOrEmpty(s) ? string.Empty : s.Trim();

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
    }
}
