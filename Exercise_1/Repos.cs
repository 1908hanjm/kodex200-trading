// Repos.cs  (C# 7.3)
// - FundCycleManager가 필요로 하는 최소 Repo 계약
// - 이미 동일한 계약이 있으면 네임스페이스/이름을 이 파일과 일치시키거나, 이 파일은 생략하세요.

namespace Exercise_1
{
    public interface IBandRepo
    {
        BandRow Get(int band);                // kodex200_new 조회
        void UpdateQty(int band, int newQty); // 보유 수량 갱신
    }

    public sealed class BandRow
    {
        public int Band { get; set; }
        public int High { get; set; }
        public int Low { get; set; }
        public int Qty { get; set; }
    }

    public interface ICycleLogRepo
    {
        void InsertCycle(
            string ymd, string ts, int fromBand, int toBand, string action,
            int qtyFrom, double priceFrom, double amountFrom,
            int qtyTo, double priceTo, double amountTo,
            double diffAmount, double balanceAfter, string reason, string updatedTs, string clientTag);
    }
}
