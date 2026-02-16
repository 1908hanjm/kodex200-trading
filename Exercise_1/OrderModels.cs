using System;

namespace Exercise_1
{
    // ───────────── ENUMS ─────────────
    //public enum TradeSide
    //{
    //    Buy = 2,  // XING 기준: 1=매도, 2=매수
    //    Sell = 1
    //}

    //public enum OrderType
    //{
    //    Market = 0,
    //    Limit = 1
    //}

    // ───────────── REQUEST ─────────────
    public sealed class OrderRequest
    {
        public string AccountNo { get; set; }   // 계좌번호
        public string Password { get; set; }    // 주문 비밀번호
        public string Symbol { get; set; }      // 종목코드 ("A069500" 등)
        public long Qty { get; set; }           // 수량(주)
        public double Price { get; set; }       // 지정가일 때만 사용, 시장가는 0
        public TradeSide Side { get; set; }     // 매수/매도
        public OrderType Type { get; set; }     // Market / Limit
    }

    // ───────────── RESPONSE / STATUS ─────────────
    public sealed class OrderAck
    {
        public bool Accepted { get; set; }      // 주문 접수 여부
        public string OrderNo { get; set; }     // 증권사 주문번호
        public string Message { get; set; }     // 메시지(예외, 사유 등)
    }

    public sealed class OrderRow
    {
        public string OrderNo { get; set; }     // 주문번호
        public string Symbol { get; set; }      // 종목
        public TradeSide Side { get; set; }     // 매수/매도
        public long Qty { get; set; }           // 원주문 수량
        public long RemainQty { get; set; }     // 미체결 수량
        public double Price { get; set; }       // 주문가
        public string Status { get; set; }      // 상태 문자열
        public DateTime Ts { get; set; }        // 주문 시간
    }

    // ───────────── FILL EVENT ─────────────
    public sealed class FillEvent
    {
        public string OrderNo { get; set; }
        public string Symbol { get; set; }
        public TradeSide Side { get; set; }
        public long FillQty { get; set; }
        public int FillPrice { get; set; }
        public long CumulativeQty { get; set; }
        public long LeavesQty { get; set; }
        public string ClientTag { get; set; }
        public DateTime FillTime { get; set; }
        public bool IsFinal { get; set; }   // 마지막 체결 여부(선택)
    }
}
