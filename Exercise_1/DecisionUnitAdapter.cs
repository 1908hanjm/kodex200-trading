// DecisionUnitAdapter.cs — SDU → 기존 TradeSignal로 매핑 (Side 없음, Band는 int)
using System;
using Exercise_1.Domain;   // IStateAndDecisionUnit, BandMultiDecision, BandTradeSignal

namespace Exercise_1
{
    /// <summary>
    /// SDU(StateAndDecisionUnit)의 멀티 신호를 기존 IDecisionUnit 규격(TradeSignal)로 어댑팅
    /// </summary>
    //public sealed class DecisionUnitAdapter : IDecisionUnit
    //{
    //    //private readonly IStateAndDecisionUnit _sdu;

    //    //public DecisionUnitAdapter(IStateAndDecisionUnit sdu)
    //    //{
    //    //    _sdu = sdu ?? throw new ArgumentNullException(nameof(sdu));
    //    //}

    //    // ⚠️ 프로젝트의 실제 인터페이스 메서드명에 맞추세요.
    //    // 스크린샷 기준 이름이 Evaluate(Tick)이므로 그에 맞춰 구현합니다.
    //    //public TradeSignal Evaluate(Tick tick)
    //    //{
    //    //    // TradeSignal에 Side 속성이 없고, Band는 int(널 불가)인 환경을 가정합니다.
    //    //    if (tick == null)
    //    //    {
    //    //        return new TradeSignal
    //    //        {
    //    //            // Side 설정 없음
    //    //            Band = 0,
    //    //            Reason = "tick=null"
    //    //        };
    //    //    }

    //    //    //var md = _sdu.OnTickMulti(tick);   // 반환: BandMultiDecision
    //    //    //if (md == null || md.Signals.Count == 0)
    //    //    //{
    //    //    //    return new TradeSignal
    //    //    //    {
    //    //    //        Band = 0,
    //    //    //        Reason = "no-signal"
    //    //    //    };
    //    //    //}

    //    //    //var sig = md.Signals[0]; // BandTradeSignal

    //    //    // 타겟 밴드가 없으면 0으로(프로젝트 규칙에 맞게 바꾸셔도 됩니다)
    //    //    //int targetBand = (sig.TargetBands != null && sig.TargetBands.Count > 0)
    //    //    //                 ? sig.TargetBands[0]
    //    //    //                 : 0;

    //    //    //// Side 속성이 없으므로, 이유 문자열에 사이드 정보를 포함시켜 전달
    //    //    //string reason = $"[{sig.Side}] {sig.Reason}";

    //    //    //return new TradeSignal
    //    //    //{
    //    //    //    // Side 설정 없음 (프로젝트 TradeSignal에 해당 속성이 없음)
    //    //    //    Band = targetBand,   // int
    //    //    //    Reason = reason
    //    //    //};
    //    //}
    //}
}
