// -------------------------------------------------------------
// FILE: Repositories/DbHelper.cs — 패치본 (절대경로/WAL)
// -------------------------------------------------------------
using System;
using System.Data.SQLite;
using System.IO;

namespace Exercise_1
{
    public sealed class DbHelper
    {
        private readonly string _dbPath;

        public DbHelper(string dbPath)
        {
            _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        }

        private SQLiteConnection Open()
        {
            var abs = Path.GetFullPath(_dbPath);
            if (!File.Exists(abs)) throw new FileNotFoundException("DB 파일 없음", abs);

            SQLiteConnection.ClearAllPools(); // 스냅샷/핸들 제거

            var cs = new SQLiteConnectionStringBuilder
            {
                DataSource = abs,
                Version = 3,
                Pooling = true,
                JournalMode = SQLiteJournalModeEnum.Wal
            }.ToString();

            var c = new SQLiteConnection(cs);
            c.Open();
            using (var cmd = new SQLiteCommand("PRAGMA journal_mode=WAL;", c))
                cmd.ExecuteNonQuery();
            return c;
        }

        // 필요시 호출(유틸에서 막 수정 후 가시성 문제 있을 때)
        private void WalCheckpoint(SQLiteConnection conn)
        {
            using (var cmd = new SQLiteCommand("PRAGMA wal_checkpoint(TRUNCATE);", conn))
                cmd.ExecuteNonQuery();
        }

        public int GetSinaForBand(int band)
        {
            using (var conn = Open())
            using (var cmd = new SQLiteCommand(
                "SELECT COALESCE(sina,0) FROM kodex200_new WHERE band=@b;", conn))
            {
                cmd.Parameters.AddWithValue("@b", band);
                var o = cmd.ExecuteScalar();
                if (o == null || o == DBNull.Value) return 0;
                return Convert.ToInt32(o);
            }
        }

        public int GetQtyForBand(int band)
        {
            using (var conn = Open())
            using (var cmd = new SQLiteCommand(
                "SELECT COALESCE(qty,0) FROM kodex200_new WHERE band=@b;", conn))
            {
                cmd.Parameters.AddWithValue("@b", band);
                var o = cmd.ExecuteScalar();
                if (o == null || o == DBNull.Value) return 0;
                return Convert.ToInt32(o);
            }
        }

        public int ResolveFromBand(int[] priorityBands)
        {
            foreach (var b in priorityBands)
                if (GetQtyForBand(b) > 0) return b;
            return -1;
        }

        public void InsertCycle(string cycleId, string symbol, int fromBand, int toBand, string reason)
        {
            using (var conn = Open())
            using (var cmd = new SQLiteCommand(@"
                INSERT OR IGNORE INTO 자금순환_tbl
                (cycle_id, ymd, ts, from_band, to_band, action, status, reason)
                VALUES(
                    @cid,
                    strftime('%Y%m%d','now','localtime'),
                    strftime('%H%M%S','now','localtime'),
                    @fb, @tb,
                    'SELL_TO_BUY',
                    'REQUESTED',
                    @reason
                );", conn))
            {
                cmd.Parameters.AddWithValue("@cid", cycleId);
                cmd.Parameters.AddWithValue("@fb", fromBand);
                cmd.Parameters.AddWithValue("@tb", toBand);
                cmd.Parameters.AddWithValue("@reason", reason ?? "");
                cmd.ExecuteNonQuery();
            }
        }

        public void UpdateCycleStatus(
            string cycleId,
            string status,
            string ordnoSell = null,
            string ordnoBuy = null,
            int? qtyFrom = null,
            double? priceFrom = null,
            int? qtyTo = null,
            double? priceTo = null)
        {
            using (var conn = Open())
            using (var cmd = new SQLiteCommand(@"
                UPDATE 자금순환_tbl
                   SET status     = COALESCE(@st, status),
                       ordno_sell = COALESCE(@os, ordno_sell),
                       ordno_buy  = COALESCE(@ob, ordno_buy),
                       qty_from   = COALESCE(@qf, qty_from),
                       price_from = COALESCE(@pf, price_from),
                       qty_to     = COALESCE(@qt, qty_to),
                       price_to   = COALESCE(@pt, price_to),
                       updated_ts = strftime('%Y-%m-%d %H:%M:%S','now','localtime')
                 WHERE cycle_id = @cid;", conn))
            {
                cmd.Parameters.AddWithValue("@cid", cycleId);
                cmd.Parameters.AddWithValue("@st", (object)status ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@os", (object)ordnoSell ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ob", (object)ordnoBuy ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@qf", (object)qtyFrom ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@pf", (object)priceFrom ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@qt", (object)qtyTo ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@pt", (object)priceTo ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }
    }
}
