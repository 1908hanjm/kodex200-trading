// 3000_주문모델.cs  (복붙용 / C# 7.3)
// ------------------------------------------------------------
// 목적(동결):
// - 프로젝트 공용 enum(TradeSide/OrderType)만 "단일 소스"로 고정한다.
// - OrderRequest/OrderAck/OrderRow/FillEvent 클래스는 기존 ordermodels.cs에 유지한다.
// ------------------------------------------------------------

using System;

namespace Exercise_1
{
    /// <summary>
    /// 주문 방향 (XING 기준값 유지)
    /// - Sell = 1 (매도)
    /// - Buy  = 2 (매수)
    /// </summary>
    public enum TradeSide
    {
        Sell = 1,
        Buy = 2
    }

    public enum OrderType
    {
        Market = 0,
        Limit = 1
    }

    public static class TradeSideExt
    {
        public static string ToKor(this TradeSide side)
        {
            return side == TradeSide.Buy ? "매수" : "매도";
        }

        public static TradeSide FromKor(string sideKor)
        {
            if (string.Equals(sideKor, "매수", StringComparison.OrdinalIgnoreCase)) return TradeSide.Buy;
            if (string.Equals(sideKor, "매도", StringComparison.OrdinalIgnoreCase)) return TradeSide.Sell;
            return TradeSide.Buy; // 보수적 기본값
        }
    }
}
// 2026-02-15 14073
