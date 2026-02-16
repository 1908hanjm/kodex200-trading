// 3001_공용전달타입.cs  (복붙용 / C# 7.3)
// ------------------------------------------------------------
// 목적(동결):
// - 모듈 간 전달에 필요한 "최소 DTO"만 고정한다. (로직 없음)
// ------------------------------------------------------------

using System;

namespace Exercise_1
{
    /// <summary>
    /// 0300에서 생성하는 거래 계획(실행 전)
    /// </summary>
    public sealed class TradePlan
    {
        public TradeSide Side { get; set; }
        public string SideKor { get; set; }     // "매수"/"매도"

        public int DecisionBand { get; set; }   // K
        public int UpdateQtyBand { get; set; }  // (규칙에 따라 K 또는 K+1)

        public long Qty { get; set; }           // 주문수량 (sina 또는 qty)
        public int FirePrice { get; set; }      // FIRE 가격(정수)

        public int StartBandNow { get; set; }   // 로그/검증용
        public string Reason { get; set; }      // 디버그 목적
        public string ClientTag { get; set; }   // 추적 태그
    }

    /// <summary>
    /// 0600(주문번호 매핑)에 저장할 최소 엔트리
    /// </summary>
    public sealed class OrdMapEntry
    {
        public long OrdNo { get; set; }
        public string OrdNoStr { get; set; }    // 필요하면 사용

        public TradeSide Side { get; set; }
        public string SideKor { get; set; }

        public int Band { get; set; }
        public int OrderQty { get; set; }

        public DateTime CreatedAt { get; set; }
        public string ClientTag { get; set; }
    }

    /// <summary>
    /// SC1에서 0700으로 넘길 최소 체결정보
    /// </summary>
    public sealed class FillInfo
    {
        public long OrdNo { get; set; }
        public string OrdNoStr { get; set; }

        public string ExecNo { get; set; }      // dedup key

        public TradeSide Side { get; set; }
        public string SideKor { get; set; }

        public int Band { get; set; }

        public int FillQty { get; set; }
        public int FillPrice { get; set; }

        public int CumQty { get; set; }
        public int LeavesQty { get; set; }

        public DateTime FillTime { get; set; }
        public bool IsFinal { get; set; }

        public string ClientTag { get; set; }
    }
}
// 2026-02-15 90764
