// TradePlanner.cs  (C# 7.3)
using System;
using System.Data.SQLite;

namespace Exercise_1
{
    public sealed class BandThresholdQtyCalculator
    {
        private readonly string _dbPath;
        public BandThresholdQtyCalculator(string dbPath) { _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath)); }

        /// <summary>
        /// 현재가(now)에 따라 즉시 매수/매도 대상 수량을 계산
        /// - 매수: now ≤ low 인 행들의 qty 합
        /// - 매도: now ≥ high 인 행들의 qty 합
        /// </summary>
        public (int buyQty, int sellQty) ComputeQty(double now)
        {
            int buy = 0, sell = 0;
            using (var conn = new SQLiteConnection($"Data Source={_dbPath};Version=3;"))
            {
                conn.Open();
                // 매수 대상 수량
                using (var cmd = new SQLiteCommand(
                    "SELECT IFNULL(SUM(qty),0) FROM kodex200_new WHERE @p <= low;", conn))
                {
                    cmd.Parameters.AddWithValue("@p", now);
                    buy = Convert.ToInt32(cmd.ExecuteScalar());
                }
                // 매도 대상 수량
                using (var cmd = new SQLiteCommand(
                    "SELECT IFNULL(SUM(qty),0) FROM kodex200_new WHERE @p >= high;", conn))
                {
                    cmd.Parameters.AddWithValue("@p", now);
                    sell = Convert.ToInt32(cmd.ExecuteScalar());
                }
            }
            return (buy, sell);
        }
    }
}
