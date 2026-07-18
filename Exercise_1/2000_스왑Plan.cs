// 2000_스왑Plan.cs  (복붙용 / C# 7.3)
// ------------------------------------------------------------
// 역할(간단형 / 하락스왑 전용):
// - "매수 필요하지만 현금 부족" 상황에서
//   ✅ 상위밴드(보유 중 최상단: qty>0 중 band가 가장 작은 것) 전량 매도
//   ✅ 하위밴드(결정밴드 K) 매수로 전환하기 위한 "스왑 계획"을 만든다.
//
// 설계 원칙(확정):
// - 1:1 밴드 페어(하락 스왑): 상단밴드 매도 -> 하단밴드 매수
// - 상단은 항상 보유 중 최상위 밴드(가격대 가장 높은 밴드)
// - 수량은 상단밴드 전량(qty)
// - 현금 판단은 0500/2100에서 CSPAQ12200으로 한다(여긴 계획만)
// ------------------------------------------------------------

using System;
using System.Data.SQLite;

namespace Exercise_1
{
    public sealed class _2000_스왑Plan
    {
        public sealed class SwapPlan
        {
            public int SellBand;      // 상단밴드
            public int SellQty;       // 전량
            public int BuyBandK;      // 하단(결정밴드 K)
            public int BuyPrice;      // 매수 기준가(현재가)
            public int RequestedQty;  // 원래 요청 수량(참고용)
            public long RequiredCash; // qty*price (원래 요청 기준)
            public string Reason;     // 로그용
        }

        /// <summary>
        /// 하락 스왑 계획 생성:
        /// - sellBand = qty>0 중 band 최소(최상단)
        /// - sellQty  = 그 밴드 qty 전량
        /// - buyBandK = 입력값(결정밴드)
        /// </summary>
        public bool TryBuildDownSwapPlan(
            int buyBandK,
            int buyPrice,
            int requestedQty,
            out SwapPlan plan,
            out string why)
        {
            plan = null;
            why = "";

            if (buyBandK <= 0) { why = "buyBandK<=0"; return false; }
            if (buyPrice <= 0) { why = "buyPrice<=0"; return false; }
            if (requestedQty <= 0) { why = "requestedQty<=0"; return false; }

            int sellBand;
            int sellQty;

            if (!TryGetTopHoldingBand(out sellBand, out sellQty, out why))
            {
                return false;
            }

            if (sellBand <= 0 || sellQty <= 0)
            {
                why = "top holding band not found";
                return false;
            }

            long requiredCash = (long)requestedQty * (long)buyPrice;

            plan = new SwapPlan
            {
                SellBand = sellBand,
                SellQty = sellQty,
                BuyBandK = buyBandK,
                BuyPrice = buyPrice,
                RequestedQty = requestedQty,
                RequiredCash = requiredCash,
                Reason = "CASH_SHORT_DOWN_SWAP"
            };

            why = "ok sellBand=" + sellBand + " sellQty=" + sellQty + " buyBandK=" + buyBandK;
            return true;
        }

        private bool TryGetTopHoldingBand(out int band, out int qty, out string why)
        {
            band = 0;
            qty = 0;
            why = "";

            try
            {
                using (var conn = new SQLiteConnection(Login.ConnStr))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        // ✅ "상위밴드" 정의: qty>0 중 band가 가장 작은 것 (가격대 최상단)
                        cmd.CommandText =
                            "SELECT band, qty " +
                            "FROM kodex200_new " +
                            "WHERE qty > 0 " +
                            "ORDER BY band ASC " +
                            "LIMIT 1";

                        using (var rd = cmd.ExecuteReader())
                        {
                            if (!rd.Read())
                            {
                                why = "no holding band(qty>0) in kodex200_new";
                                return false;
                            }

                            band = SafeGetInt(rd, 0);
                            qty = SafeGetInt(rd, 1);

                            if (band <= 0 || qty <= 0)
                            {
                                why = "invalid top holding band/qty band=" + band + " qty=" + qty;
                                return false;
                            }

                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                why = "db ex: " + ex.Message;
                return false;
            }
        }

        private static int SafeGetInt(SQLiteDataReader rd, int idx)
        {
            try
            {
                if (rd.IsDBNull(idx)) return 0;
                return Convert.ToInt32(rd.GetValue(idx));
            }
            catch
            {
                return 0;
            }
        }
    }
}

// 2026-02-16 83374
