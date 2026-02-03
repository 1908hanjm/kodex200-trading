
// 025_BandRange.cs
// 025_BandRange.cs — 최종 버전 (High/Low 포함)

namespace Exercise_1
{
    public sealed class BandRange
    {
        public int Band { get; set; }              // 밴드번호

        // ★ 가격 구조 (DB와 1:1 매칭)
        public long 팔가격 { get; set; }           // 매도 기준선 (High 개념)
        public long 산가격 { get; set; }           // 매수 주문가 (실제 주문 넣는 가격)
        public long 살가격 { get; set; }           // 매수 기준선 (Low 개념)

        // ★ 수량/목표
        public long Qty { get; set; }             // 보유수량
        public long Sina { get; set; }             // 목표수량

        // ★ 자금 순환 추적
        public long From_Qty { get; set; }
        public int From_Band { get; set; }

        // ★ 실제 매수 체결 단가
        public long 진짜산가격 { get; set; }

        // ───────────────────────────────────────
        // ▪ 기존 코드 호환용 Alias 프로퍼티
        //   (여러 파일에서 High / Low를 그대로 써도 됨)
        // ───────────────────────────────────────
        public long High
        {
            get => 팔가격;
            set => 팔가격 = value;
        }

        public long Low
        {
            get => 살가격;
            set => 살가격 = value;
        }
    }
}
