using System;
using System.Collections.Generic;
using System.Text;
using Exercise_1.Domain;

namespace Exercise_1
{
    /// <summary>
    /// 상태/판정 유닛 셀프 테스트 (Button4에서 호출)
    /// - 밴드 폭 10원: CrossUp/Down이 쉽게 발생
    /// - CASE A: SELL 보장 (상승추세 → 하락 전환 + CrossDn)
    /// - CASE B: BUY  보장 (하락추세 → 상승 전환 + CrossUp)
    ///   ※ StateAndDecisionUnit.cs가 '경계선 방향 고려' 패치 버전이어야 SELL 케이스가 정확합니다.
    /// </summary>
    public static class DecisionUnitSelfTest
    {
        public static string Run()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[DecisionUnit Self Test]");
            sb.AppendLine("감도: SMA=3, slopeThreshold=0, bandWidth=10");
            sb.AppendLine();

            // 밴드: 10원 간격 (band1=[79000~79010], band2=[79010~79020], ...)
            //var bands = MakeBands(79000, 10, 8);

            //// CASE A: SELL 보장
            //sb.AppendLine(RunCase(
            //    "CASE A: Expect SELL (상승 → CrossDn + Up→Down)",
            //    bands,
            //    new[]
            //    {
            //        // 상승 추세 만들기
            //        MakeTick(0,  79024), // band3
            //        MakeTick(1,  79028), // band3
            //        MakeTick(2,  79033), // band4
            //        // 급락: band1의 High(=79010)에 터치 → (패치 적용 시) CrossDn
            //        MakeTick(3,  79010)
            //    },
            //    DecisionSide.Sell
            //));

            //// CASE B: BUY 보장
            //sb.AppendLine(RunCase(
            //    "CASE B: Expect BUY (하락 → CrossUp + Down→Up)",
            //    bands,
            //    new[]
            //    {
            //        // 하락 추세 만들기
            //        MakeTick(10, 79036), // band4
            //        MakeTick(11, 79022), // band3
            //        MakeTick(12, 79012), // band2
            //        // 반등: band3의 High(=79030) 터치 → CrossUp
            //        MakeTick(13, 79030),
            //        // 한 틱 더 상승시켜 SMA(3) 기울기 양수 전환 보장
            //        MakeTick(14, 79040)
            //    },
            //    DecisionSide.Buy
            //));

            return sb.ToString();
        }

        // ===== 내부 구현 =====

        //private static string RunCase(string title, IReadOnlyList<BandRange> bands, IEnumerable<Tick> ticks, DecisionSide expect)
        //{
        //    var sb = new StringBuilder();
        //    sb.AppendLine("— " + title);

        //    var unit = new StateAndDecisionUnit();
            
        //    unit.LoadBands(bands);
        //    unit.SetAccount(1_000_000, 0);

        //    bool hit = false;
        //    foreach (var tk in ticks)
        //    {
        //        var d = unit.OnTick(tk);
        //        sb.AppendLine(string.Format(
        //            "{0:HH:mm:ss}  {1,8:0.##}  →  {2,-4}  band={3}  {4}",
        //            tk.TsKst, tk.Price, d.Side,
        //            d.TargetBand.HasValue ? d.TargetBand.Value.ToString() : "-",
        //            d.Reason));

        //        if (d.Side == expect) hit = true;
        //    }

        //    sb.AppendLine(hit
        //        ? string.Format("✅ {0} 신호 발생", expect)
        //        : string.Format("⚠ {0} 신호 없음(밴드/파라미터 조정 필요)", expect));
        //    sb.AppendLine();

        //    return sb.ToString();
        //}

        //private static List<BandRange> MakeBands(double low0, double bandWidth, int count)
        //{
        //    var list = new List<BandRange>(count);
        //    for (int i = 0; i < count; i++)
        //    {
        //        double lo = low0 + i * bandWidth;
        //        list.Add(new BandRange
        //        {
        //            Band = i + 1,
        //            Low = lo,
        //            High = lo + bandWidth,
        //            Sina = 5,
        //            Qty = 0
        //        });
        //    }
        //    return list;
        //}

        private static Tick MakeTick(int secOffset, double price)
        {
            return new Tick
            {
                Symbol = "069500",
                TsKst = DateTimeOffset.Now.AddSeconds(secOffset),
                Price = price
            };
        }
    }
}
