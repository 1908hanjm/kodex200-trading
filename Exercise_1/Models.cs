// -------------------------------------------------------------
// FILE: models.cs (통합 표준 버전)
// -------------------------------------------------------------
// 공통 정의 모음
// - IMarketFeed: 실시간/리플레이 피드 인터페이스
// - Tick: 시장 데이터 단위
// - IDecisionUnit: 틱 기반 판정 유닛 인터페이스
// - TradeSignal / TradeAction: 매매 신호 정의
// -------------------------------------------------------------

using System;

namespace Exercise_1
{
    /// <summary>
    /// 시장 데이터 피드 인터페이스
    /// (RealTimeFeed, DbReplayFeed 등에서 구현)
    /// </summary>
    public interface IMarketFeed
    {
        event Action<Tick> OnTick;     // 틱 수신 이벤트
        void Start(string symbol);     // 피드 시작
        void Stop();                   // 피드 중지
    }

    /// <summary>
    /// 시장 틱 데이터 모델
    /// </summary>
    public sealed class Tick
    {
        public string Symbol { get; set; }            // 종목 코드 (예: "069500")
        public DateTimeOffset TsKst { get; set; }     // 한국 표준시 시각
        public double Price { get; set; }             // 체결가
        public long CVol { get; set; }                // 체결량(누적)
        public double Bid1 { get; set; }              // 최우선 매수호가
        public double Ask1 { get; set; }              // 최우선 매도호가
    }

    /// <summary>
    /// 틱 기반 판정 유닛
    /// - 틱을 받아 상태 갱신 및 매매 신호 반환
    /// </summary>
    public interface IDecisionUnit
    {
        TradeSignal Evaluate(Tick tick);
    }

    /// <summary>
    /// 매매 동작 종류
    /// </summary>
    public enum TradeAction
    {
        Hold = 0,
        Buy = 1,
        Sell = 2
    }

    /// <summary>
    /// 매매 신호 데이터
    /// </summary>
    public sealed class TradeSignal
    {
        public TradeAction Action { get; set; }        // 매매 동작 (Buy / Sell / Hold)
        public int Qty { get; set; }                   // 수량
        public double? PreferredPrice { get; set; }    // 지정가 (없으면 현재가 사용)
        public string Reason { get; set; }             // 로깅/디버깅용 설명
        public int Band { get; set; }                  // 기준 밴드 (선택)
        public double GuardPrice { get; set; }         // 조건부 시장가 기준가 (선택)
    }
    public sealed class CycleRequest
    {
        public string CycleId { get; set; }       // UUID 등 식별자
        public string Symbol { get; set; }        // 종목코드 (예: "A069500")
        public int FromBand { get; set; }         // 매도 밴드 (예: 1)
        public int ToBand { get; set; }           // 매수 밴드 (예: 11)
        public double GuardPrice { get; set; }    // 조건부 시장가 기준가
        public string Reason { get; set; }        // 사유(로그용)
    }


}
