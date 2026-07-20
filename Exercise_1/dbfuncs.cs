// DbFuncs.cs — kodex200_new 및 daily_balance 관리 + 자동매매 반영용 (C# 7.3)
//h  Test.cs에서 DbFuncs.UpdateKodexQty() 호출 시 즉시 kodex200_new.qty를 업데이트
//h  StateAndDecisionUnit의 매매신호에 따라 자동매매 반영 가능
//
// ✅ 이번 정합 수정:
// - KodexQtyUpdated는 기존처럼 Action<int,long> 유지
// - 외부 클래스(예: 0700)에서 직접 event Invoke 하지 않도록
//   RaiseKodexQtyUpdated(int band, long qty) 정적 메서드 추가
// - UpdateKodexQty 내부도 RaiseKodexQtyUpdated(...) 사용으로 통일
//
// ✅ 0700에서 사용할 호출:
//   DbFuncs.RaiseKodexQtyUpdated(band, 0);
//   또는
//   DbFuncs.RaiseKodexQtyUpdated(band, newQty);

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
        // isComplete: true=완전체결, false=부분체결
        // 부분체결(isComplete=false) 시 t0424 호출 금지 정책을 UI(Login_05)에서 판단
        public static event Action<int, long, bool> KodexQtyUpdated;

        // ✅ 외부 클래스는 이 메서드로만 이벤트를 발생시킨다.
        // isComplete: true=완전체결(체인 종료 후 t0424 허용), false=부분체결(t0424 금지)
        public static void RaiseKodexQtyUpdated(int band, long qty, bool isComplete = true)
        {
            try
            {
                var handler = KodexQtyUpdated;
                if (handler != null)
                    handler(band, qty, isComplete);
            }
            catch { }
        }

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
    from_band    INTEGER NOT NULL DEFAULT 0,
    from_qty     INTEGER NOT NULL DEFAULT 0,
    extra_qty    INTEGER NOT NULL DEFAULT 0,
    진짜산가격   INTEGER NOT NULL DEFAULT 0
);", conn))
            {
                cmd.ExecuteNonQuery();
                DB_Control.EnsureExtraQtyColumn(conn);
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
                    listView.Columns.Add("from_band", 80);
                    listView.Columns.Add("from_QTY", 80);
                    listView.Columns.Add("extra_QTY", 80);
                    listView.Columns.Add("진짜산가격", 70);

                    DB_Control.EnsureExtraQtyColumn(conn);
                    using (var cmd = new SQLiteCommand(
                        "SELECT * FROM kodex200_new WHERE IFNULL(qty,0) > 0 ORDER BY band;", conn))
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            int band = ReadIntForView(reader["band"]);
                            long qty = ReadLongForView(reader["qty"]);
                            int fromBand = ReadIntForView(reader["from_band"]);
                            long fromQty = ReadLongForView(reader["from_qty"]);
                            long rawExtraQty = ReadLongForView(reader["extra_qty"]);
                            long extraQty = NormalizeExtraQtyForView(band, rawExtraQty);

                            var item = new ListViewItem(reader["band"].ToString());
                            item.SubItems.Add(reader["팔가격"].ToString());
                            item.SubItems.Add(reader["산가격"].ToString());
                            item.SubItems.Add(reader["살가격"].ToString());
                            item.SubItems.Add(qty.ToString(CultureInfo.InvariantCulture));
                            item.SubItems.Add(fromBand.ToString(CultureInfo.InvariantCulture));
                            item.SubItems.Add(fromQty.ToString(CultureInfo.InvariantCulture));
                            item.SubItems.Add(extraQty.ToString(CultureInfo.InvariantCulture));
                            item.SubItems.Add(reader["진짜산가격"].ToString());
                            listView.Items.Add(item);

                            LogExtraQtyView(band, fromBand, fromQty, extraQty, qty);
                        }
                    }

                    using (var cmdSum = new SQLiteCommand(
                        "SELECT COALESCE(SUM(qty),0) FROM kodex200_new;", conn))
                    using (var r = cmdSum.ExecuteReader())
                    {
                        if (r.Read())
                        {
                            sumQty = !r.IsDBNull(0) ? r.GetInt64(0) : 0;
                        }
                    }

                    if (highlightMaxBandWithQtyNonZero)
                    {
                        using (var cmdMaxNZ = new SQLiteCommand(
                            "SELECT MAX(band) FROM kodex200_new WHERE IFNULL(qty,0) > 0;", conn))
                        {
                            var r = cmdMaxNZ.ExecuteScalar();
                            if (r != DBNull.Value && r != null)
                                targetBand = Convert.ToInt32(r);
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
        }
        // === [ADD] TOTAL QTY ===
        public long GetTotalQty()
        {
            try
            {
                using (var conn = OpenConn())
                using (var cmd = new SQLiteCommand(
                    "SELECT COALESCE(SUM(qty),0) FROM kodex200_new;", conn))
                {
                    var result = cmd.ExecuteScalar();

                    if (result == null || result == DBNull.Value)
                        return 0;

                    return Convert.ToInt64(result);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[DB][GetTotalQty ERROR] " + ex.Message);
                return 0;
            }
        }
        public static void SelectRowByBand(ListView listView, int targetBand)
        {
            if (listView == null || listView.Items.Count == 0) return;

            listView.SelectedItems.Clear();
            foreach (ListViewItem it in listView.Items)
            {
                int band;
                if (int.TryParse(it.SubItems[0].Text, out band) && band == targetBand)
                {
                    it.Selected = true;
                    it.EnsureVisible();
                    listView.FocusedItem = it;
                    break;
                }
            }
        }

        private static int ReadIntForView(object value)
        {
            if (value == null || value == DBNull.Value)
                return 0;

            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        private static long ReadLongForView(object value)
        {
            if (value == null || value == DBNull.Value)
                return 0L;

            try { return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
            catch { return 0L; }
        }

        private static long NormalizeExtraQtyForView(int band, long extraQty)
        {
            if (extraQty >= 0)
                return extraQty;

            Console.WriteLine("[UI][WARN] Band=" + band + " ExtraQtyNegative=" + extraQty);
            return 0L;
        }

        private static void LogExtraQtyView(int band, int fromBand, long fromQty, long extraQty, long qty)
        {
            Console.WriteLine("[EXTRA_QTY][VIEW] " +
                              "Band=" + band +
                              " FromBand=" + fromBand +
                              " FromQty=" + fromQty +
                              " ExtraQty=" + extraQty +
                              " Qty=" + qty);
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
                            var item = new ListViewItem(r["ymd"] == null ? "" : r["ymd"].ToString());
                            item.SubItems.Add(r["보유량"] == null ? "" : r["보유량"].ToString());
                            item.SubItems.Add(r["현금"] == null ? "" : r["현금"].ToString());
                            item.SubItems.Add(r["d2"] == null ? "" : r["d2"].ToString());
                            item.SubItems.Add(r["현금토탈"] == null ? "" : r["현금토탈"].ToString());
                            item.SubItems.Add(r["당일손익"] == null ? "" : r["당일손익"].ToString());
                            item.SubItems.Add(r["총자산"] == null ? "" : r["총자산"].ToString());
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

        public bool ApplyDailyRealizedPnlToBandCapital(long realizedPnl, int bandCount = 10)
        {
            if (bandCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(bandCount), "bandCount must be greater than zero.");

            string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            using (var conn = OpenConn())
            {
                EnsureBandCapitalBatchSchema(conn);

                double limitLeftCash = ReadTodayLimitLeftCash(conn, today);
                double perBandAmount = (realizedPnl + limitLeftCash) / (double)bandCount;

                using (var tx = conn.BeginTransaction())
                {
                    try
                    {
                        int updatedRows;
                        using (var cmd = new SQLiteCommand(@"
UPDATE 배정금
   SET 배정금액 = COALESCE(배정금액, 0) + @amount,
       최종반영일자 = @today
 WHERE band >= 1
   AND band <= @bandCount
   AND (최종반영일자 IS NULL OR 최종반영일자 <> @today);", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@amount", perBandAmount);
                            cmd.Parameters.AddWithValue("@today", today);
                            cmd.Parameters.AddWithValue("@bandCount", bandCount);
                            updatedRows = cmd.ExecuteNonQuery();
                        }

                        if (updatedRows == 0)
                        {
                            tx.Commit();
                            Console.WriteLine("[BAND_CAPITAL][SKIP] already applied today=" + today);
                            return false;
                        }

                        using (var cmd = new SQLiteCommand(@"
UPDATE 한도초과_남은현금
   SET 누적금액 = 0
 WHERE 일자 = @today;", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@today", today);
                            cmd.ExecuteNonQuery();
                        }

                        tx.Commit();

                        Console.WriteLine("[BAND_CAPITAL][OK] today=" + today +
                                          " realizedPnl=" + realizedPnl.ToString(CultureInfo.InvariantCulture) +
                                          " limitLeftCash=" + limitLeftCash.ToString(CultureInfo.InvariantCulture) +
                                          " bandCount=" + bandCount.ToString(CultureInfo.InvariantCulture) +
                                          " perBandAmount=" + perBandAmount.ToString(CultureInfo.InvariantCulture) +
                                          " updatedRows=" + updatedRows.ToString(CultureInfo.InvariantCulture));
                        return true;
                    }
                    catch
                    {
                        try { tx.Rollback(); } catch { }
                        throw;
                    }
                }
            }
        }

        private static void EnsureBandCapitalBatchSchema(SQLiteConnection conn)
        {
            using (var cmd = new SQLiteCommand(@"
CREATE TABLE IF NOT EXISTS 배정금 (
    band          INTEGER PRIMARY KEY,
    배정금액       REAL NOT NULL DEFAULT 0,
    최종반영일자    TEXT NULL
);", conn))
            {
                cmd.ExecuteNonQuery();
            }

            EnsureColumn(conn, "배정금", "배정금액",
                "ALTER TABLE 배정금 ADD COLUMN 배정금액 REAL NOT NULL DEFAULT 0;");
            EnsureColumn(conn, "배정금", "최종반영일자",
                "ALTER TABLE 배정금 ADD COLUMN 최종반영일자 TEXT NULL;");

            using (var cmd = new SQLiteCommand(@"
CREATE TABLE IF NOT EXISTS 한도초과_남은현금 (
    일자       TEXT PRIMARY KEY,
    누적금액    REAL NOT NULL DEFAULT 0
);", conn))
            {
                cmd.ExecuteNonQuery();
            }

            EnsureColumn(conn, "한도초과_남은현금", "누적금액",
                "ALTER TABLE 한도초과_남은현금 ADD COLUMN 누적금액 REAL NOT NULL DEFAULT 0;");
        }

        private static void EnsureColumn(
            SQLiteConnection conn,
            string tableName,
            string columnName,
            string alterSql)
        {
            bool exists = false;
            using (var cmd = new SQLiteCommand("PRAGMA table_info(\"" + tableName.Replace("\"", "\"\"") + "\");", conn))
            using (var rd = cmd.ExecuteReader())
            {
                while (rd.Read())
                {
                    string name = rd["name"] != DBNull.Value ? Convert.ToString(rd["name"]) : "";
                    if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
            }

            if (!exists)
            {
                using (var cmd = new SQLiteCommand(alterSql, conn))
                    cmd.ExecuteNonQuery();
            }
        }

        private static double ReadTodayLimitLeftCash(SQLiteConnection conn, string today)
        {
            using (var cmd = new SQLiteCommand(@"
SELECT COALESCE(SUM(누적금액), 0)
  FROM 한도초과_남은현금
 WHERE 일자 = @today;", conn))
            {
                cmd.Parameters.AddWithValue("@today", today);
                var result = cmd.ExecuteScalar();
                if (result == null || result == DBNull.Value)
                    return 0.0;

                try { return Convert.ToDouble(result, CultureInfo.InvariantCulture); }
                catch { return 0.0; }
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
            RaiseKodexQtyUpdated(band, newQty);
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
                "SELECT band, 팔가격, 산가격, 살가격, qty FROM kodex200_new WHERE IFNULL(qty,0) > 0 ORDER BY band ASC;", conn))
            using (var da = new SQLiteDataAdapter(cmd))
            {
                da.Fill(dt);
            }
            return dt;
        }
    }
}
// 2026-03-09 54827
