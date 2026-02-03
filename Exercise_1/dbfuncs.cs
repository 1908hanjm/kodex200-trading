// DbFuncs.cs — kodex200_new 및 daily_balance 관리 + 자동매매 반영용 (C# 7.3)
//h  Test.cs에서 DbFuncs.UpdateKodexQty() 호출 시 즉시 kodex200_new.qty를 업데이트
//h  StateAndDecisionUnit의 매매신호에 따라 자동매매 반영 가능

using System;
using System.Data;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace Exercise_1
{
    public class DbFuncs
    {
        private readonly string _dbPath;
        private readonly int _parameter;

        // ✅ DB 업데이트 신호(상위 UI에서 구독)
        public static event Action<int, long> KodexQtyUpdated;

        public DbFuncs(string dbPath, int parameter = 0)
        {
            _dbPath = dbPath;
            _parameter = parameter;
        }

        // === 공통: 절대경로 + 풀 플러시 + WAL ===
        private SQLiteConnection OpenConn()
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

            var conn = new SQLiteConnection(cs);
            conn.Open();

            using (var cmd = new SQLiteCommand("PRAGMA journal_mode=WAL;", conn))
                cmd.ExecuteNonQuery();

            return conn;
        }

        private void LogDbFileInfo()
        {
            try
            {
                var abs = Path.GetFullPath(_dbPath);
                var fi = new FileInfo(abs);
                Console.WriteLine($"[DB] Path={abs}, Exists={fi.Exists}, Size={fi.Length:N0} bytes, LastWrite={fi.LastWriteTime}");
            }
            catch
            {
                // 무시
            }
        }

        // === 실행 파라미터 기반 ===
        public void Execute()
        {
            try
            {
                switch (_parameter)
                {
                    case 11:
                        CreateTable();
                        break;
                    case 14:
                        DeleteAllRows();
                        break;
                    default:
                        Console.WriteLine("⚠️ Execute(): 지원하지 않는 파라미터: " + _parameter);
                        break;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("오류: " + ex.Message, "DB 작업", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // === kodex200_new 생성 (현재 스키마 기준) ===
        private void CreateTable()
        {
            using (var conn = OpenConn())
            using (var cmd = new SQLiteCommand(@"
CREATE TABLE IF NOT EXISTS kodex200_new (
    band         INTEGER PRIMARY KEY,
    팔가격       INTEGER NOT NULL,   -- 매도 기준선 (High)
    산가격       INTEGER NOT NULL,   -- 매수 주문가
    살가격       INTEGER NOT NULL,   -- 매수 기준선 (Low)
    qty          INTEGER NOT NULL DEFAULT 0,
    sina         INTEGER NOT NULL DEFAULT 0,
    from_band    INTEGER NOT NULL DEFAULT 0,
    from_qty     INTEGER NOT NULL DEFAULT 0,
    진짜산가격   INTEGER NOT NULL DEFAULT 0
);", conn))
            {
                cmd.ExecuteNonQuery();
            }
        }

        private void DeleteAllRows()
        {
            using (var conn = OpenConn())
            using (var cmd = new SQLiteCommand("DELETE FROM kodex200_new;", conn))
                cmd.ExecuteNonQuery();
        }

        // === kodex200_new → ListView ===
        public void LoadKodexToListView(
            ListView listView,
            bool highlightMaxBandWithQtyNonZero = true,
            TextBox textBoxQtySum = null,
            TextBox textBoxSinaSum = null)
        {
            if (!File.Exists(_dbPath))
            {
                MessageBox.Show("DB 파일이 없습니다: " + _dbPath, "오류",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            int? targetBand = null;
            long sumQty = 0;
            long sumSina = 0;

            LogDbFileInfo();

            using (var conn = OpenConn())
            {
                listView.BeginUpdate();
                try
                {
                    listView.Clear();
                    listView.View = View.Details;
                    listView.FullRowSelect = true;
                    listView.GridLines = true;
                    listView.MultiSelect = false;

                    listView.Columns.Add("band", 70);
                    listView.Columns.Add("팔가격", 80);
                    listView.Columns.Add("산가격", 80);
                    listView.Columns.Add("살가격", 70);
                    listView.Columns.Add("QTY", 70);
                    listView.Columns.Add("SINA", 70);
                    listView.Columns.Add("from_band", 80);
                    listView.Columns.Add("from_QTY", 80);
                    listView.Columns.Add("진짜산가격", 70);

                    using (var cmd = new SQLiteCommand(
                        "SELECT * FROM kodex200_new ORDER BY band;", conn))
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var item = new ListViewItem(reader["band"].ToString());
                            item.SubItems.Add(reader["팔가격"].ToString());
                            item.SubItems.Add(reader["산가격"].ToString());
                            item.SubItems.Add(reader["살가격"].ToString());
                            item.SubItems.Add(reader["qty"].ToString());
                            item.SubItems.Add(reader["sina"].ToString());
                            item.SubItems.Add(reader["from_band"].ToString());
                            item.SubItems.Add(reader["from_qty"].ToString());
                            item.SubItems.Add(reader["진짜산가격"].ToString());
                            listView.Items.Add(item);
                        }
                    }

                    using (var cmdSum = new SQLiteCommand(
                        "SELECT COALESCE(SUM(qty),0), COALESCE(SUM(sina),0) FROM kodex200_new;", conn))
                    using (var r = cmdSum.ExecuteReader())
                    {
                        if (r.Read())
                        {
                            sumQty = !r.IsDBNull(0) ? r.GetInt64(0) : 0;
                            sumSina = !r.IsDBNull(1) ? r.GetInt64(1) : 0;
                        }
                    }

                    if (highlightMaxBandWithQtyNonZero)
                    {
                        using (var cmdMaxNZ = new SQLiteCommand(
                            "SELECT MAX(band) FROM kodex200_new WHERE IFNULL(qty,0) <> 0;", conn))
                        {
                            var r = cmdMaxNZ.ExecuteScalar();
                            if (r != DBNull.Value && r != null)
                                targetBand = Convert.ToInt32(r);
                        }

                        if (targetBand == null)
                        {
                            using (var cmdMaxAll = new SQLiteCommand(
                                "SELECT MAX(band) FROM kodex200_new;", conn))
                            {
                                var rAll = cmdMaxAll.ExecuteScalar();
                                if (rAll != DBNull.Value && rAll != null)
                                    targetBand = Convert.ToInt32(rAll);
                            }
                        }
                    }
                }
                finally
                {
                    listView.AutoResizeColumns(ColumnHeaderAutoResizeStyle.HeaderSize);
                    listView.EndUpdate();
                }
            }

            if (targetBand != null)
                SelectRowByBand(listView, targetBand.Value);

            if (textBoxQtySum != null)
                textBoxQtySum.Text = sumQty.ToString("N0", CultureInfo.InvariantCulture);
            if (textBoxSinaSum != null)
                textBoxSinaSum.Text = sumSina.ToString("N0", CultureInfo.InvariantCulture);
        }

        public static void SelectRowByBand(ListView listView, int targetBand)
        {
            if (listView == null || listView.Items.Count == 0) return;

            listView.SelectedItems.Clear();
            foreach (ListViewItem it in listView.Items)
            {
                if (int.TryParse(it.SubItems[0].Text, out int band) && band == targetBand)
                {
                    it.Selected = true;
                    it.EnsureVisible();
                    listView.FocusedItem = it;
                    break;
                }
            }
        }

        // === Daily Balance 관련 ===
        public void EnsureDailyBalanceTable()
        {
            using (var conn = OpenConn())
            using (var cmd = new SQLiteCommand(@"
CREATE TABLE IF NOT EXISTS daily_balance (
  ymd         TEXT PRIMARY KEY,
  보유량      INTEGER NOT NULL,
  현금        REAL NOT NULL,
  d2          REAL NOT NULL,
  현금토탈    REAL NOT NULL,
  당일손익    REAL NOT NULL,
  총자산      REAL NOT NULL,
  updated_ts  TEXT NOT NULL
);", conn))
            {
                cmd.ExecuteNonQuery();
            }
        }

        public void LoadDailyBalanceToListView(ListView listView)
        {
            if (listView == null) return;

            using (var conn = OpenConn())
            {
                listView.BeginUpdate();
                try
                {
                    listView.Clear();
                    listView.View = View.Details;
                    listView.FullRowSelect = true;
                    listView.GridLines = true;

                    listView.Columns.Add("날짜", 90);
                    listView.Columns.Add("보유량", 80);
                    listView.Columns.Add("현금", 100);
                    listView.Columns.Add("D2", 100);
                    listView.Columns.Add("현금토탈", 110);
                    listView.Columns.Add("당일손익", 100);
                    listView.Columns.Add("총자산", 110);

                    string sql = @"
SELECT substr(ymd, 1, 4) || '-' || substr(ymd, 5, 2) || '-' || substr(ymd, 7, 2) as ymd,
       보유량, 현금, d2,
       COALESCE(현금토탈, (현금 + d2)) AS 현금토탈,
       당일손익, 총자산
FROM daily_balance
ORDER BY ymd DESC;";

                    using (var cmd = new SQLiteCommand(sql, conn))
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            var item = new ListViewItem(r["ymd"]?.ToString() ?? "");
                            item.SubItems.Add(r["보유량"]?.ToString() ?? "");
                            item.SubItems.Add(r["현금"]?.ToString() ?? "");
                            item.SubItems.Add(r["d2"]?.ToString() ?? "");
                            item.SubItems.Add(r["현금토탈"]?.ToString() ?? "");
                            item.SubItems.Add(r["당일손익"]?.ToString() ?? "");
                            item.SubItems.Add(r["총자산"]?.ToString() ?? "");
                            listView.Items.Add(item);
                        }
                    }

                    listView.AutoResizeColumns(ColumnHeaderAutoResizeStyle.HeaderSize);
                }
                finally
                {
                    listView.EndUpdate();
                }
            }
        }

        public void UpsertDailyBalance(int 보유량, double 현금, double d2, double 당일손익, DateTime nowLocal)
        {
            string ymd = nowLocal.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            double 현금토탈 = 현금 + d2;
            double 총자산 = 현금토탈 + 당일손익;
            string ts = nowLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            using (var conn = OpenConn())
            using (var cmd = new SQLiteCommand(@"
INSERT INTO daily_balance(ymd, 보유량, 현금, d2, 현금토탈, 당일손익, 총자산, updated_ts)
VALUES(@ymd, @보유량, @현금, @d2, @현금토탈, @당일손익, @총자산, @ts)
ON CONFLICT(ymd) DO UPDATE SET
  보유량     = excluded.보유량,
  현금       = excluded.현금,
  d2         = excluded.d2,
  현금토탈   = excluded.현금토탈,
  당일손익   = excluded.당일손익,
  총자산     = excluded.총자산,
  updated_ts = excluded.updated_ts;", conn))
            {
                cmd.Parameters.AddWithValue("@ymd", ymd);
                cmd.Parameters.AddWithValue("@보유량", 보유량);
                cmd.Parameters.AddWithValue("@현금", 현금);
                cmd.Parameters.AddWithValue("@d2", d2);
                cmd.Parameters.AddWithValue("@현금토탈", 현금토탈);
                cmd.Parameters.AddWithValue("@당일손익", 당일손익);
                cmd.Parameters.AddWithValue("@총자산", 총자산);
                cmd.Parameters.AddWithValue("@ts", ts);
                cmd.ExecuteNonQuery();
            }
        }

        // === 자동매매용 Kodex 수량 업데이트 ===
        //h 예: UpdateKodexQty(6, 6) → band 6번 qty=6, 진짜산가격=0으로 업데이트
        public void UpdateKodexQty(int band, long newQty)
        {
            using (var conn = OpenConn())
            using (var cmd = new SQLiteCommand(
                "UPDATE kodex200_new SET qty = @qty, 진짜산가격 = 0 WHERE band = @band;", conn))
            {
                cmd.Parameters.AddWithValue("@band", band);
                cmd.Parameters.AddWithValue("@qty", newQty);

                int rows = cmd.ExecuteNonQuery();

                if (rows == 0)
                    Console.WriteLine($"[WARN] band={band} 업데이트 실패 (행 없음)");
                else
                    Console.WriteLine($"[OK] band={band} qty → {newQty}");
            }
            // ✅ DB 업데이트 완료 신호
            try { KodexQtyUpdated?.Invoke(band, newQty); } catch { }
        }

        // === kodex200_new → DataTable ===
        //h 외부에서 kodex200_new 전체를 DataTable 형태로 읽을 때 사용
        public DataTable GetKodexBandsDataTable()
        {
            if (!File.Exists(_dbPath))
                throw new FileNotFoundException("DB 파일이 없습니다.", _dbPath);

            var dt = new DataTable();
            using (var conn = OpenConn())
            using (var cmd = new SQLiteCommand(
                "SELECT band, 팔가격, 산가격, 살가격, qty, sina FROM kodex200_new ORDER BY band ASC;", conn))
            using (var da = new SQLiteDataAdapter(cmd))
            {
                da.Fill(dt);
            }
            return dt;
        }
    }
}
