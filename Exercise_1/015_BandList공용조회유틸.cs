// 015_BandList공용조회유틸.cs
// BandList 공용 조회 유틸 (test / real 공통)
// - Login.BandList 를 단일 진실로 사용
// - UI/DB에 의존하지 않는 조회 전용 함수만 제공

using System.Linq;

namespace Exercise_1
{
    public static class BandList공용조회유틸
    {
        /// <summary>
        /// 특정 밴드의 BandRange를 반환 (없으면 null)
        /// </summary>
        public static BandRange GetBand(int band)
        {
            return Login.BandList?.FirstOrDefault(x => x.Band == band);
        }

        /// <summary>
        /// 특정 밴드의 현재 보유 수량(Qty) 반환
        /// 없으면 -1 반환
        /// </summary>
        public static long GetBandQty(int band)
        {
            var b = GetBand(band);
            return b == null ? -1 : b.Qty;
        }

        /// <summary>
        /// 특정 밴드의 매수 주문가(산가격) 반환
        /// 산가격이 0이거나 밴드가 없으면 fallback 반환
        /// </summary>
        public static double GetBandBuyPrice(int band, double fallback = 0)
        {
            var b = GetBand(band);
            if (b == null) return fallback;
            return b.산가격 > 0 ? b.산가격 : fallback;
        }

        /// <summary>
        /// 특정 밴드의 매도 기준가(팔가격) 반환
        /// 팔가격이 0이거나 밴드가 없으면 fallback 반환
        /// </summary>
        public static double GetBandSellPrice(int band, double fallback = 0)
        {
            var b = GetBand(band);
            if (b == null) return fallback;
            return b.팔가격 > 0 ? b.팔가격 : fallback;
        }

        /// <summary>
        /// 특정 밴드의 매수 기준선(살가격) 반환
        /// 살가격이 0이거나 밴드가 없으면 fallback 반환
        /// </summary>
        public static double GetBandLowPrice(int band, double fallback = 0)
        {
            var b = GetBand(band);
            if (b == null) return fallback;
            return b.살가격 > 0 ? b.살가격 : fallback;
        }
    }
}
