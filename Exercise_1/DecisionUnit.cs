using System;

namespace Exercise_1
{
    // ==== 공용 enum ====
    public enum DecisionSide { None, Buy, Sell }
    public enum TrendState { Unknown, Up, Down, Flat }

    // ==== 공용 모델 ====
    /// <summary>가격 밴드(한 칸)</summary>


    /// <summary>판정 시점의 내부 상태 스냅샷</summary>
    public sealed class StateSnapshot
    {
        public string Symbol { get; set; }
        public DateTimeOffset Ts { get; set; }
        public double Price { get; set; }
        public TrendState Trend { get; set; }
        public double Sma { get; set; }
        public double SmaSlope { get; set; }
        public int? CurrentBand { get; set; }
        //public BandRange HitBand { get; set; }
        public double Cash { get; set; }
        public long Position { get; set; }
    }

    /// <summary>의사결정 결과</summary>
    public sealed class Decision
    {
        public DecisionSide Side { get; set; }   // Buy/Sell/None
        public string Reason { get; set; }   // 판정 이유
        public int? TargetBand { get; set; }   // 해당 밴드 번호
        public long Qty { get; set; }   // 제안 수량(옵션)
        public double? PriceHint { get; set; }   // 주문가 힌트(옵션)
        public StateSnapshot Snapshot { get; set; }   // 상태 스냅샷
    }
}
