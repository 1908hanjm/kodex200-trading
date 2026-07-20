// -------------------------------------------------------------
// FILE: Repositories/SqliteBandRepo.cs — 패치본 (절대경로/WAL)
// -------------------------------------------------------------
using System;
using System.Data.SQLite;
using System.IO;

namespace Exercise_1
{
    public sealed class SqliteBandRepo : IBandRepo
    {
        private readonly string _dbPath;

        public SqliteBandRepo(string dbPath)
        {
            _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
            EnsureTable();
        }

        private SQLiteConnection Open()
        {
            var abs = Path.GetFullPath(_dbPath);
            SQLiteConnection.ClearAllPools();

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

        private void EnsureTable()
        {
            using (var conn = Open())
            using (var cmd = new SQLiteCommand(@"
                CREATE TABLE IF NOT EXISTS kodex200_new (
                    band INTEGER PRIMARY KEY,
                    high INTEGER,
                    low  INTEGER,
                    qty  INTEGER,
                    extra_qty INTEGER NOT NULL DEFAULT 0
                );", conn))
            {
                cmd.ExecuteNonQuery();
                DB_Control.EnsureExtraQtyColumn(conn);
            }
        }

        public BandRow Get(int band)
        {
            using (var conn = Open())
            using (var cmd = new SQLiteCommand(@"
                SELECT band, COALESCE(high,0), COALESCE(low,0),
                       COALESCE(qty,0)
                  FROM kodex200_new
                 WHERE band = @b;", conn))
            {
                cmd.Parameters.AddWithValue("@b", band);
                using (var rd = cmd.ExecuteReader())
                {
                    if (!rd.Read()) return null;
                    return new BandRow
                    {
                        Band = rd.GetInt32(0),
                        High = rd.GetInt32(1),
                        Low = rd.GetInt32(2),
                        Qty = rd.GetInt32(3),
                    };
                }
            }
        }

        public void UpdateQty(int band, int newQty)
        {
            using (var conn = Open())
            using (var cmd = new SQLiteCommand(@"
                INSERT INTO kodex200_new(band, high, low, qty)
                VALUES(@b, 0, 0, @q)
                ON CONFLICT(band) DO UPDATE SET qty = excluded.Qty;", conn))
            {
                cmd.Parameters.AddWithValue("@b", band);
                cmd.Parameters.AddWithValue("@q", newQty);
                cmd.ExecuteNonQuery();
            }
        }
    }
}
