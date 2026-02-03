using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using XA_DATASETLib;

namespace Exercise_1
{
    public static class OrderInquiryService
    {
        public sealed class OrderRow
        {
            public string Time;   // ordtime/chetime
            public string Tr;     // "t0425"
            public string Side;   // BUY/SELL
            public int Qty;
            public int Price;
            public string OrdNo;
            public string MsgCode;
            public string Msg;
            public string Status;
        }

        /// <summary>
        /// t0425 기준: 당일 주문/체결 전체 조회(연속조회 포함)
        /// </summary>
        public static Task<List<OrderRow>> FetchTodayAsync(
            string resPath,
            string account,
            string accountPw,
            string shcode = null)
        {
            var tcs = new TaskCompletionSource<List<OrderRow>>();
            var result = new List<OrderRow>();

            var q = new XAQueryClass();
            q.LoadFromResFile(resPath);

            string cts_ordno = ""; // 연속조회 키
            bool first = true;

            // 이벤트 핸들러
            _IXAQueryEvents_ReceiveDataEventHandler onData = null;
            _IXAQueryEvents_ReceiveMessageEventHandler onMsg = null;

            onData = (trCode) =>
            {
                try
                {
                    // OutBlock1 반복영역 파싱
                    int count = q.GetBlockCount("t0425OutBlock1");
                    for (int i = 0; i < count; i++)
                    {
                        string ordno = q.GetFieldData("t0425OutBlock1", "ordno", i);
                        string bnstp = q.GetFieldData("t0425OutBlock1", "medosu", i);   // 1:매수, 2:매도 (환경에 따라 다를 수 있음)
                        string qtyStr = q.GetFieldData("t0425OutBlock1", "qty", i);
                        string prcStr = q.GetFieldData("t0425OutBlock1", "price", i);
                        string ordtime = q.GetFieldData("t0425OutBlock1", "ordtime", i); // 또는 chetime
                        int.TryParse(qtyStr, out int qty);
                        int.TryParse(prcStr, out int prc);

                        result.Add(new OrderRow
                        {
                            Time = ordtime,
                            Tr = "t0425",
                            Side = (bnstp == "2") ? "SELL" : (bnstp == "1") ? "BUY" : bnstp,
                            Qty = qty,
                            Price = prc,
                            OrdNo = ordno,
                            MsgCode = "",
                            Msg = ""
                        });
                    }

                    // 연속조회 키
                    cts_ordno = q.GetFieldData("t0425OutBlock", "cts_ordno", 0);

                    // 다음 루프 또는 완료
                    if (!string.IsNullOrEmpty(cts_ordno))
                    {
                        // 다음 요청
                        q.SetFieldData("t0425InBlock", "accno", 0, account);
                        q.SetFieldData("t0425InBlock", "passwd", 0, accountPw);
                        q.SetFieldData("t0425InBlock", "expcode", 0, "1");
                        q.SetFieldData("t0425InBlock", "chegb", 0, "0"); // 0:전체
                        q.SetFieldData("t0425InBlock", "sortgb", 0, "1"); // 최신순
                        q.SetFieldData("t0425InBlock", "cts_ordno", 0, cts_ordno);
                        q.SetFieldData("t0425InBlock", "", 0, shcode ?? "");

                        int r = q.Request(false);
                        if (r < 0)
                        {
                            tcs.TrySetException(new Exception("t0425 연속 Request 실패: " + r));
                        }
                    }
                    else
                    {
                        // 완료
                        q.ReceiveData -= onData;
                        q.ReceiveMessage -= onMsg;
                        tcs.TrySetResult(result);
                    }
                }
                catch (Exception ex)
                {
                    q.ReceiveData -= onData;
                    q.ReceiveMessage -= onMsg;
                    tcs.TrySetException(ex);
                }
            };

            onMsg = (isSysErr, code, msg) =>
            {
                // 필요하면 로깅
                // MessageBox.Show($"{code}: {msg}");
            };

            q.ReceiveData += onData;
            q.ReceiveMessage += onMsg;

            // 최초 요청 세팅
            try
            {
                q.SetFieldData("t0425InBlock", "accno", 0, account);
                q.SetFieldData("t0425InBlock", "passwd", 0, accountPw);
                q.SetFieldData("t0425InBlock", "expcode", 0, "");     // 종목코드(전체 조회 시 빈값)
                q.SetFieldData("t0425InBlock", "chegb", 0, "0");      // 0: 전체
                q.SetFieldData("t0425InBlock", "medosu", 0, "0");     // 0: 전체
                q.SetFieldData("t0425InBlock", "sortgb", 0, "1");     // 최신순
                q.SetFieldData("t0425InBlock", "cts_ordno", 0, "");   // 연속조회 키


                int ret = q.Request(false);
                if (ret < 0)
                {
                    q.ReceiveData -= onData;
                    q.ReceiveMessage -= onMsg;
                    tcs.TrySetException(new Exception("t0425 Request 실패: " + ret));
                }
            }
            catch (Exception ex)
            {
                q.ReceiveData -= onData;
                q.ReceiveMessage -= onMsg;
                tcs.TrySetException(ex);
            }

            return tcs.Task;
        }
    }
}

