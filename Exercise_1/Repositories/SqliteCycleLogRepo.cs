// -------------------------------------------------------------
// FILE: Repositories/SqliteCycleLogRepo.cs
// -------------------------------------------------------------
using System;
using System.Data.SQLite;

namespace Exercise_1
{
    /// <summary>
    /// 자금순환_tbl에 기록하는 ICycleLogRepo의 SQLite 구현
    /// </summary>
    public sealed class SqliteCycleLogRepo : ICycleLogRepo
    {
        private readonly string _dbPath;
        private string ConnStr => $"Data Source={_dbPath};Version=3;";

        public SqliteCycleLogRepo(string dbPath)
        {
            _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
            EnsureTable();
        }

        private SQLiteConnection Open()
        {
            var c = new SQLiteConnection(ConnStr);
            c.Open();
            return c;
        }

        private void EnsureTable()
        {
            using (var conn = Open())
            using (var cmd = new SQLiteCommand(@"
                CREATE TABLE IF NOT EXISTS 자금순환_tbl (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    ymd           TEXT,
                    ts            TEXT,
                    from_band     INTEGER,
                    to_band       INTEGER,
                    action        TEXT,
                    qty_from      INTEGER,
                    price_from    REAL,
                    amount_from   REAL,
                    qty_to        INTEGER,
                    price_to      REAL,
                    amount_to     REAL,
                    diff_amount   REAL,
                    balance_after REAL,
                    reason        TEXT,
                    updated_ts    TEXT,
                    client_tag    TEXT
                );
            ", conn))
            {
                cmd.ExecuteNonQuery();
            }
        }

        public void InsertCycle(
            string ymd, string ts,
            int fromBand, int toBand, string action,
            int qtyFrom, double priceFrom, double amountFrom,
            int qtyTo, double priceTo, double amountTo,
            double diffAmount, double balanceAfter, string reason,
            string updatedTs, string clientTag)
        {
            using (var conn = Open())
            using (var cmd = new SQLiteCommand(@"
                INSERT INTO 자금순환_tbl
                (ymd, ts, from_band, to_band, action,
                 qty_from, price_from, amount_from,
                 qty_to, price_to, amount_to,
                 diff_amount, balance_after, reason, updated_ts, client_tag)
                VALUES
                (@ymd, @ts, @fb, @tb, @act,
                 @qf, @pf, @af,
                 @qt, @pt, @at,
                 @diff, @bal, @rs, @uts, @tag);
            ", conn))
            {
                cmd.Parameters.AddWithValue("@ymd", ymd ?? "");
                cmd.Parameters.AddWithValue("@ts", ts ?? "");
                cmd.Parameters.AddWithValue("@fb", fromBand);
                cmd.Parameters.AddWithValue("@tb", toBand);
                cmd.Parameters.AddWithValue("@act", action ?? "");
                cmd.Parameters.AddWithValue("@qf", qtyFrom);
                cmd.Parameters.AddWithValue("@pf", priceFrom);
                cmd.Parameters.AddWithValue("@af", amountFrom);
                cmd.Parameters.AddWithValue("@qt", qtyTo);
                cmd.Parameters.AddWithValue("@pt", priceTo);
                cmd.Parameters.AddWithValue("@at", amountTo);
                cmd.Parameters.AddWithValue("@diff", diffAmount);
                cmd.Parameters.AddWithValue("@bal", balanceAfter);
                cmd.Parameters.AddWithValue("@rs", reason ?? "");
                cmd.Parameters.AddWithValue("@uts", updatedTs ?? "");
                cmd.Parameters.AddWithValue("@tag", clientTag ?? "");
                cmd.ExecuteNonQuery();
            }
        }
    }
}
