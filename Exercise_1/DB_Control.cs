using Exercise_1;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Windows.Forms;
//using static System.Net.WebRequestMethods;

namespace Exercise_1
{
    // =========================================================
    // ✅ DB_Control 폼
    // =========================================================
    public partial class DB_Control : Form
    {
        private const string TICKS_TS_TABLE = "ticks_ts";

        private string DbPath => Login.DbPath;
        private string ConnStr => Login.ConnStr;

        public DB_Control()
        {
            InitializeComponent();

            // ListView 기본 옵션
            listView1.FullRowSelect = true;
            listView1.HideSelection = false;
            listView1.View = View.Details;

            listView1.SelectedIndexChanged += listView1_SelectedIndexChanged;
            listView1.ItemSelectionChanged += listView1_ItemSelectionChanged;

            //this.Load += (s, e) => MessageBox.Show($"DbPath = {Login.DbPath}");
        }

        // ─────────────────────────────────────────────────────────
        // 공용 유틸
        // ─────────────────────────────────────────────────────────
        private void EnsureDbExists()
        {
            if (!File.Exists(DbPath))
                throw new FileNotFoundException("DB 파일이 없습니다.", DbPath);
        }

        private static void EnsureTable(SQLiteConnection conn)
        {
            using (var cmdCreate = new SQLiteCommand(@"
CREATE TABLE IF NOT EXISTS kodex200_new (
    band        INTEGER PRIMARY KEY,
    팔가격      INTEGER NOT NULL,
    산가격      INTEGER NOT NULL,
    살가격      INTEGER NOT NULL,
    qty         INTEGER NOT NULL DEFAULT 0,
    from_band   INTEGER NOT NULL DEFAULT 0,
    from_qty    INTEGER NOT NULL DEFAULT 0,
    extra_qty   INTEGER NOT NULL DEFAULT 0,
    진짜산가격  INTEGER NOT NULL DEFAULT 0
);", conn))
            {
                cmdCreate.ExecuteNonQuery();
            }

            EnsureExtraQtyColumn(conn);
        }

        public static void EnsureExtraQtyColumn(SQLiteConnection conn)
        {
            bool exists = false;
            using (var cmd = new SQLiteCommand("PRAGMA table_info(kodex200_new);", conn))
            using (var rd = cmd.ExecuteReader())
            {
                while (rd.Read())
                {
                    string name = rd["name"] != DBNull.Value ? Convert.ToString(rd["name"]) : "";
                    if (string.Equals(name, "extra_qty", StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
            }

            if (!exists)
            {
                using (var cmd = new SQLiteCommand(
                    "ALTER TABLE kodex200_new ADD COLUMN extra_qty INTEGER NOT NULL DEFAULT 0;", conn))
                {
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // ─────────────────────────────────────────────────────────
        // 공용 Core: 시드 입력
        // ─────────────────────────────────────────────────────────
        private void Seed_Core(bool useAltSeed)
        {
            int[,] seedDefault =
            {
                {1, 52000, 51701, 1, 1, 0, 0, 0},
                {2, 51700, 51401, 2, 2, 0, 0, 0},
                {3, 51400, 51101, 3, 3, 0, 0, 0},
                {4, 51100, 50801, 4, 4, 0, 0, 0},
                {5, 50800, 50501, 5, 5, 0, 0, 0},
                {6, 50500, 50201, 0, 6, 0, 0, 0},
                {7, 50200, 49901, 0, 7, 0, 0, 0},
                {8, 49900, 49601, 0, 8, 0, 0, 0},
                {9, 49600, 49301, 0, 9, 0, 0, 0},
                {10, 49300, 49001, 0, 10, 0, 0, 0}
            };

            int[,] seedAlt =
            {
                { 1, 61000, 60701, 1, 1, 0, 0, 0},
                {121, 25000, 24701, 0, 1, 0, 0, 0},
                {122, 24700, 24401, 0, 1, 0, 0, 0}
            };

            int[,] seed = useAltSeed ? seedAlt : seedDefault;

            EnsureDbExists();
            using (var conn = new SQLiteConnection(ConnStr))
            {
                conn.Open();
                EnsureTable(conn);

                using (var cmdDel = new SQLiteCommand("DELETE FROM kodex200_new;", conn))
                    cmdDel.ExecuteNonQuery();

                using (var tx = conn.BeginTransaction())
                using (var cmdIns = new SQLiteCommand(
                    @"INSERT OR REPLACE INTO kodex200_new 
      (band, 팔가격, 산가격, 살가격, qty, from_qty, from_band, extra_qty, 진짜산가격)
      VALUES (@band, @팔가격, @산가격, @살가격, @qty, @from_qty, @from_band, @extra_qty, @진짜산가격);",
                    conn, tx))
                {
                    cmdIns.Parameters.Add("@band", DbType.Int32);
                    cmdIns.Parameters.Add("@팔가격", DbType.Int32);
                    cmdIns.Parameters.Add("@산가격", DbType.Int32);
                    cmdIns.Parameters.Add("@살가격", DbType.Int32);
                    cmdIns.Parameters.Add("@qty", DbType.Int32);
                    cmdIns.Parameters.Add("@from_qty", DbType.Int32);
                    cmdIns.Parameters.Add("@from_band", DbType.Int32);
                    cmdIns.Parameters.Add("@extra_qty", DbType.Int32);
                    cmdIns.Parameters.Add("@진짜산가격", DbType.Int32);

                    for (int i = 0; i < seed.GetLength(0); i++)
                    {
                        cmdIns.Parameters["@band"].Value = seed[i, 0];
                        cmdIns.Parameters["@팔가격"].Value = seed[i, 1]; // 팔가격(High)
                        cmdIns.Parameters["@산가격"].Value = seed[i, 2]; // 산가격(매수주문가)
                        cmdIns.Parameters["@살가격"].Value = seed[i, 2]; // 필요하면 다른 값으로 변경
                        cmdIns.Parameters["@qty"].Value = seed[i, 3];
                        cmdIns.Parameters["@from_qty"].Value = seed[i, 5];
                        cmdIns.Parameters["@from_band"].Value = seed[i, 6];
                        cmdIns.Parameters["@extra_qty"].Value = 0;
                        cmdIns.Parameters["@진짜산가격"].Value = 0;        // 초기값 0

                        cmdIns.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
            }

            // ListView 갱신
            사용밴드.LoadIntoListView(ConnStr, listView1);
        }

        // ─────────────────────────────────────────────────────────
        // 공용 Core: ticks_ts 패턴
        // ─────────────────────────────────────────────────────────
        private void InsertTicksPattern_Core(string parameter)
        {
            EnsureDbExists();
            using (var conn = new SQLiteConnection(ConnStr))
            {
                conn.Open();
                Tick_ins(conn, parameter);
            }
        }

        private void Tick_ins(SQLiteConnection conn, string parameter)
        {
            using (var cmd = new SQLiteCommand($"DELETE FROM {TICKS_TS_TABLE};", conn))
                cmd.ExecuteNonQuery();

            string sql;
            switch ((parameter ?? "").Trim())
            {
                case "단매수":
                    sql = @"
delete from ticks_ts;

INSERT INTO ticks_ts (ts, price) VALUES
(datetime('now', '+1 minute', 'localtime'), 70015),  -- start (band17)
(datetime('now', '+2 minute', 'localtime'), 69950),  -- band17
(datetime('now', '+3 minute', 'localtime'), 69790),  -- band17 살가격(69800) 하향돌파 -> 매수돌파(K=17)
(datetime('now', '+4 minute', 'localtime'), 69700),  -- segMin 갱신
(datetime('now', '+5 minute', 'localtime'), 69815);  -- segMin(69700)+kk(100)=69800 이상 반등 -> [FIRE-BUY";
                    break;

                case "단매도":
                    sql = @"
delete from ticks_ts;

INSERT INTO ticks_ts (ts, price) VALUES
(datetime('now', '+1 minute', 'localtime'), 70015),  -- start (band17)
(datetime('now', '+2 minute', 'localtime'), 70380),  -- band17 (팔가격 70399 아래)
(datetime('now', '+3 minute', 'localtime'), 70410),  -- band17 팔가격(70399) 상향돌파 -> 매도돌파(K=17)
(datetime('now', '+4 minute', 'localtime'), 70520),  -- segMax 갱신
(datetime('now', '+5 minute', 'localtime'), 70410);  -- segMax(70520)-kk(100)=70420 이하 되돌림 -> [FIRE-SELL]";

                    break;

                case "다매수":
                    sql = @"
delete from ticks_ts;

INSERT INTO ticks_ts (ts, price) VALUES
(datetime('now', '+1 minute', 'localtime'), 70015),  -- start (startBand=17)
(datetime('now', '+2 minute', 'localtime'), 69950),  -- band17
(datetime('now', '+3 minute', 'localtime'), 69790),  -- 69800 하향돌파 -> K=17 (매수돌파)
(datetime('now', '+4 minute', 'localtime'), 69190),  -- 69200 하향돌파 -> 더 하락(현재밴드=19 진입)
(datetime('now', '+5 minute', 'localtime'), 68890),  -- band19 안에서 segMin 형성(예: 69100보다 더 아래)
(datetime('now', '+6 minute', 'localtime'), 69010);
";
                    break;

                case "다매도":
                    sql = @"
delete from ticks_ts;

INSERT INTO ticks_ts (ts, price) VALUES
(datetime('now', '+1 minute', 'localtime'), 70015),  -- start (startBand=17)
(datetime('now', '+2 minute', 'localtime'), 70380),  -- band17 (70399 아래)
(datetime('now', '+3 minute', 'localtime'), 70410),  -- 70399 상향돌파 -> K=17 (매도돌파)
(datetime('now', '+4 minute', 'localtime'), 70990),  -- band16 상단 근처(70999 아래)
(datetime('now', '+5 minute', 'localtime'), 71250),  -- band15 진입(현재밴드=15), segMax 형성
(datetime('now', '+6 minute', 'localtime'), 71120);  -- segMax(71250)-100=71150 이하 되돌림, 여전히 band15 -> FIRE(현재밴드=15) => endBand=16

";
                    break;

                default:
                    MessageBox.Show($"알 수 없는 parameter=\"{parameter}\" → INSERT 생략",
                        "알림", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
            }

            using (var cmd2 = new SQLiteCommand(sql, conn))
                cmd2.ExecuteNonQuery();

            using (var cmd3 = new SQLiteCommand(
                $"SELECT ts, price FROM {TICKS_TS_TABLE} ORDER BY ts ASC;", conn))
            using (var da = new SQLiteDataAdapter(cmd3))
            {
                var dt = new DataTable();
                da.Fill(dt);

                dataGridView1.DataSource = dt;
                dataGridView1.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
                dataGridView1.ReadOnly = true;
                dataGridView1.AllowUserToAddRows = false;
                dataGridView1.AllowUserToDeleteRows = false;
            }
        }

        // ─────────────────────────────────────────────────────────
        // 공용 Core: 기타 버튼
        // ─────────────────────────────────────────────────────────
        private void CreateDailyBalance_Core()
        {
            EnsureDbExists();
            using (var conn = new SQLiteConnection(ConnStr))
            {
                conn.Open();

                string createSql = @"
CREATE TABLE IF NOT EXISTS daily_balance (
    ymd        TEXT    NOT NULL,
    hold_qty   INTEGER NOT NULL,
    buy_amount INTEGER NOT NULL,
    cash_in    INTEGER NOT NULL,
    dplus2     INTEGER NOT NULL,
    total      INTEGER NOT NULL,
    pl_today   INTEGER NOT NULL,
    CONSTRAINT pk_daily_balance PRIMARY KEY (ymd)
);";
                using (var cmd = new SQLiteCommand(createSql, conn))
                    cmd.ExecuteNonQuery();

                string insertSql = @"
INSERT INTO daily_balance
(ymd, hold_qty, buy_amount, cash_in, dplus2, total, pl_today)
VALUES
(@ymd, @hold, @buy, @cash, @d2, @total, @pl);";
                using (var cmd2 = new SQLiteCommand(insertSql, conn))
                {
                    cmd2.Parameters.AddWithValue("@ymd", "1999-09-09");
                    cmd2.Parameters.AddWithValue("@hold", 10);
                    cmd2.Parameters.AddWithValue("@buy", 99);
                    cmd2.Parameters.AddWithValue("@cash", 9999);
                    cmd2.Parameters.AddWithValue("@d2", 88);
                    cmd2.Parameters.AddWithValue("@total", 4333);
                    cmd2.Parameters.AddWithValue("@pl", 399);
                    cmd2.ExecuteNonQuery();
                }
            }
            //MessageBox.Show("daily_balance 생성/입력 완료");
        }

        private void CreateSignalLog_Core()
        {
            EnsureDbExists();
            using (var conn = new SQLiteConnection(ConnStr))
            {
                conn.Open();
                using (var cmd = new SQLiteCommand(@"
CREATE TABLE IF NOT EXISTS 신호로그_tbl (
    ts          TEXT,
    symbol      TEXT,
    band        INTEGER,
    action      TEXT,
    reason      TEXT,
    qty_plan    INTEGER,
    price_ref   REAL,
    state       TEXT,
    updated_ts  TEXT
);", conn))
                    cmd.ExecuteNonQuery();

                using (var idx1 = new SQLiteCommand("CREATE INDEX IF NOT EXISTS idx_신호로그_ts ON 신호로그_tbl(ts);", conn)) idx1.ExecuteNonQuery();
                using (var idx2 = new SQLiteCommand("CREATE INDEX IF NOT EXISTS idx_신호로그_band ON 신호로그_tbl(band);", conn)) idx2.ExecuteNonQuery();
                using (var idx3 = new SQLiteCommand("CREATE INDEX IF NOT EXISTS idx_신호로그_symbol ON 신호로그_tbl(symbol);", conn)) idx3.ExecuteNonQuery();
            }
            //MessageBox.Show("신호로그_tbl 생성 완료");
        }

        private void CreateFundCycle_Core()
        {
            EnsureDbExists();
            using (var conn = new SQLiteConnection(ConnStr))
            {
                conn.Open();
                using (var cmd = new SQLiteCommand(@"
CREATE TABLE IF NOT EXISTS 자금순환_tbl (
    ymd            TEXT,
    ts             TEXT,
    from_band      INTEGER,
    to_band        INTEGER,
    action         TEXT,
    qty_from       INTEGER,
    price_from     REAL,
    amount_from    REAL,
    qty_to         INTEGER,
    price_to       REAL,
    amount_to      REAL,
    diff_amount    REAL,
    balance_after  REAL,
    reason         TEXT,
    updated_ts     TEXT
);", conn))
                    cmd.ExecuteNonQuery();

                using (var idx1 = new SQLiteCommand("CREATE INDEX IF NOT EXISTS idx_자금순환_ymd_ts ON 자금순환_tbl(ymd, ts);", conn)) idx1.ExecuteNonQuery();
                using (var idx2 = new SQLiteCommand("CREATE INDEX IF NOT EXISTS idx_자금순환_from_to ON 자금순환_tbl(from_band, to_band);", conn)) idx2.ExecuteNonQuery();
            }
            MessageBox.Show("자금순환_tbl 생성 완료");
        }

        private void LoadTicksToGrid_Core()
        {
            EnsureDbExists();
            using (var conn = new SQLiteConnection(ConnStr))
            using (var cmd = new SQLiteCommand(
                $"SELECT ts, price FROM {TICKS_TS_TABLE} ORDER BY ts ASC;", conn))
            using (var da = new SQLiteDataAdapter(cmd))
            {
                conn.Open();
                var dt = new DataTable();
                da.Fill(dt);
                dataGridView1.DataSource = dt;
                dataGridView1.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
                dataGridView1.ReadOnly = true;
                dataGridView1.AllowUserToAddRows = false;
                dataGridView1.AllowUserToDeleteRows = false;
            }
        }

        private void RefreshKodexList_Core()
        {
            사용밴드.LoadIntoListView(ConnStr, listView1);
        }

        // ─────────────────────────────────────────────────────────
        // ListView 보조
        // ─────────────────────────────────────────────────────────
        private void listView1_SelectedIndexChanged(object sender, EventArgs e)
            => FillTextBoxesFromSelectedRow();

        private void listView1_ItemSelectionChanged(object sender, ListViewItemSelectionChangedEventArgs e)
            => FillTextBoxesFromSelectedRow();

        private void FillTextBoxesFromSelectedRow()
        {
            if (listView1.SelectedItems.Count == 0)
            {
                SafeSetTextBox("textBoxBand", "textBox1", string.Empty);
                SafeSetTextBox("textBoxHigh", "textBox2", string.Empty);
                SafeSetTextBox("textBoxLow", "textBox3", string.Empty);
                SafeSetTextBox("textBoxQty", "textBox4", string.Empty);
                SafeSetTextBox("textBoxSina", "textBox5", string.Empty);
                return;
            }

            var item = listView1.SelectedItems[0];

            int idxBand = GetColumnIndexByHeader("band");
            int idxHigh = GetColumnIndexByHeader("팔가격");
            int idxLow = GetColumnIndexByHeader("살가격");
            int idxQty = GetColumnIndexByHeader("qty");
            int idxSina = GetColumnIndexByHeader("sina");

            SafeSetTextBox("textBoxBand", "textBox1", GetSubItemText(item, idxBand));
            SafeSetTextBox("textBoxHigh", "textBox2", GetSubItemText(item, idxHigh));
            SafeSetTextBox("textBoxLow", "textBox3", GetSubItemText(item, idxLow));
            SafeSetTextBox("textBoxQty", "textBox4", GetSubItemText(item, idxQty));
            SafeSetTextBox("textBoxSina", "textBox5", GetSubItemText(item, idxSina));
        }

        private int GetColumnIndexByHeader(string headerName)
        {
            for (int i = 0; i < listView1.Columns.Count; i++)
                if (string.Equals(listView1.Columns[i].Text?.Trim(), headerName, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        private static string GetSubItemText(ListViewItem item, int index)
        {
            if (index < 0) return string.Empty;
            if (index >= item.SubItems.Count) return string.Empty;
            return item.SubItems[index]?.Text ?? string.Empty;
        }

        private void SafeSetTextBox(string preferredName, string fallbackName, string text)
        {
            var c1 = this.Controls.Find(preferredName, true).FirstOrDefault() as TextBox;
            if (c1 != null) { c1.Text = text; return; }

            var c2 = this.Controls.Find(fallbackName, true).FirstOrDefault() as TextBox;
            if (c2 != null) { c2.Text = text; return; }
        }

        // ─────────────────────────────────────────────────────────
        // ======== Designer Bridge (모든 이름 변형을 수용) =========
        // ─────────────────────────────────────────────────────────
        // 버튼2/9: Seed
        private void button2_Click(object sender, EventArgs e) => Seed_Core(false);
        //private void button2_Click_1(object sender, EventArgs e)
        //    => Seed_Core((sender as Control)?.Name?.ToLowerInvariant() == "button9");


        // 버튼1: 테이블 생성(DbFuncs 11)
        private void button1_Click(object sender, EventArgs e)
        {
            try { EnsureDbExists(); new DbFuncs(DbPath, 11).Execute(); MessageBox.Show("테이블 생성(또는 유지) 완료."); }
            catch (Exception ex) { MessageBox.Show("에러: " + ex.Message); }
        }
        private void button1_Click_1(object sender, EventArgs e) => button1_Click(sender, e);

        // 버튼3: kodex200_new 조회
        private void button3_Click(object sender, EventArgs e)
        {
            try { EnsureDbExists(); RefreshKodexList_Core(); }
            catch (Exception ex) { MessageBox.Show("에러: " + ex.Message); }
        }
        private void button3_Click_1(object sender, EventArgs e) => button3_Click(sender, e);

        // 버튼4: 전체 삭제(DbFuncs 14)
        private void button4_Click(object sender, EventArgs e)
        {
            try { EnsureDbExists(); new DbFuncs(DbPath, 14).Execute(); MessageBox.Show("전체 삭제 완료"); }
            catch (Exception ex) { MessageBox.Show("에러: " + ex.Message); }
        }
        private void button4_Click_1(object sender, EventArgs e) => button4_Click(sender, e);

        // 버튼6: daily_balance 생성 + 샘플
        private void button6_Click(object sender, EventArgs e)
        {
            try { CreateDailyBalance_Core(); }
            catch (Exception ex) { MessageBox.Show("오류: " + ex.Message); }
        }
        private void button6_Click_1(object sender, EventArgs e) => button6_Click(sender, e);


        // 버튼8: 자금순환_tbl 생성
        private void button8_Click(object sender, EventArgs e)
        {
            try { CreateFundCycle_Core(); }
            catch (Exception ex) { MessageBox.Show("오류: " + ex.Message); }
        }
        private void button8_Click_1(object sender, EventArgs e) => button8_Click(sender, e);

        // 버튼10~11~17~18: 틱 패턴
        private void button10_Click(object sender, EventArgs e)
        {
            try { InsertTicksPattern_Core("단매도"); }
            catch (Exception ex) { MessageBox.Show("INSERT 오류: " + ex.Message); }
        }
        private void button10_Click_1(object sender, EventArgs e) => button10_Click(sender, e);

        private void button11_Click(object sender, EventArgs e)
        {
            try { InsertTicksPattern_Core("단매수"); }
            catch (Exception ex) { MessageBox.Show("INSERT 오류: " + ex.Message); }
        }
        private void button11_Click_1(object sender, EventArgs e) => button11_Click(sender, e);

        private void button17_Click(object sender, EventArgs e)
        {
            try { InsertTicksPattern_Core("다매도"); }
            catch (Exception ex) { MessageBox.Show("INSERT 오류: " + ex.Message); }
        }
        private void button17_Click_1(object sender, EventArgs e) => button17_Click(sender, e);

        private void button18_Click(object sender, EventArgs e)
        {
            try { InsertTicksPattern_Core("다매수"); }
            catch (Exception ex) { MessageBox.Show("INSERT 오류: " + ex.Message); }
        }
        private void button18_Click_1(object sender, EventArgs e) => button18_Click(sender, e);

        // 버튼14: ticks_ts → dataGridView 로드
        private void button14_Click(object sender, EventArgs e)
        {
            try { LoadTicksToGrid_Core(); }
            catch (Exception ex)
            {
                MessageBox.Show("ticks_ts 조회 오류: " + ex.Message,
                    "DB 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private void button14_Click_1(object sender, EventArgs e) => button14_Click(sender, e);

        // === [ADD] 최대 보유(또는 최대 band) 행을 배열로 반환 ===
        // 반환: [0]=band, [1]=max(high), [2]=min(low)
        public static int[] GetMaxBandRowArray()
        {
            var connStr = Login.ConnStr; // 전역 연결 문자열 사용
            using (var conn = new SQLiteConnection(connStr))
            {
                conn.Open();

                // 1) qty>0인 가장 큰 band 우선
                const string sqlQtyPos = @"
SELECT band, 팔가격 AS high, 살가격 AS low
FROM kodex200_new
WHERE IFNULL(qty,0) > 0
ORDER BY band DESC
LIMIT 1;";
                using (var cmd = new SQLiteCommand(sqlQtyPos, conn))
                using (var r = cmd.ExecuteReader())
                {
                    if (r.Read())
                    {
                        return new[]
                        {
                            Convert.ToInt32(r["band"]),
                            Convert.ToInt32(r["high"]), // max
                            Convert.ToInt32(r["low"])   // min
                        };
                    }
                }

                // 2) qty>0이 없으면 테이블의 최대 band
                const string sqlMaxBand = @"
SELECT band, 팔가격 AS high, 살가격 AS low
FROM kodex200_new
ORDER BY band DESC
LIMIT 1;";
                using (var cmd2 = new SQLiteCommand(sqlMaxBand, conn))
                using (var r2 = cmd2.ExecuteReader())
                {
                    if (r2.Read())
                    {
                        return new[]
                        {
                            Convert.ToInt32(r2["band"]),
                            Convert.ToInt32(r2["high"]), // max
                            Convert.ToInt32(r2["low"])   // min
                        };
                    }
                }
            }
            // 못 찾은 경우 빈 배열
            return Array.Empty<int>();
        }

        // === [ADD] 배열 내용을 RichTextBox에 출력( max/min 은 빨간색 ) ===
        // triple: [0]=band, [1]=max(high), [2]=min(low)
        public static void WriteTripleToRichTextBox(System.Windows.Forms.RichTextBox rtb, int[] triple)
        {
            if (rtb == null) return;

            rtb.Clear();
            if (triple == null || triple.Length < 3)
            {
                rtb.AppendText("데이터가 없습니다.\r\n");
                return;
            }

            // band (기본색)
            AppendPlain(rtb, "band: ");
            AppendPlain(rtb, triple[0].ToString());
            AppendPlain(rtb, "\r\n");

            // max (빨강)
            AppendPlain(rtb, "max: ");
            AppendRed(rtb, triple[1].ToString());
            AppendPlain(rtb, "\r\n");

            // min (빨강)
            AppendPlain(rtb, "min: ");
            AppendRed(rtb, triple[2].ToString());
            AppendPlain(rtb, "\r\n");

            // === 내부 도우미 ===
            void AppendPlain(System.Windows.Forms.RichTextBox box, string text)
            {
                box.SelectionColor = box.ForeColor;
                box.AppendText(text);
            }
            void AppendRed(System.Windows.Forms.RichTextBox box, string text)
            {
                box.SelectionColor = System.Drawing.Color.Red;
                box.AppendText(text);
                box.SelectionColor = box.ForeColor; // 원복
            }
        }
    }

    // =========================================================
    // ✅ 공용 리포지토리: 사용밴드
    // =========================================================
    public static class 사용밴드
    {
        // DB에서 모든 밴드를 읽어서 List<BandRange>로 반환
        public static List<BandRange> ReadAll(string connStr)
        {
            var list = new List<BandRange>();

            using (var conn = new SQLiteConnection(connStr))
            using (var cmd = new SQLiteCommand(
                @"SELECT band, 팔가격, 산가격, 살가격, qty, from_band, from_qty, extra_qty, 진짜산가격
          FROM kodex200_new
          ORDER BY band ASC;", conn))
            {
                conn.Open();
                DB_Control.EnsureExtraQtyColumn(conn);
                using (var rd = cmd.ExecuteReader())
                {
                    while (rd.Read())
                    {
                        list.Add(new BandRange
                        {
                            Band = Convert.ToInt32(rd["band"]),
                            팔가격 = Convert.ToInt64(rd["팔가격"]),   // == High
                            산가격 = Convert.ToInt64(rd["산가격"]),   // ★ 이 줄 추가
                            살가격 = Convert.ToInt64(rd["살가격"]),   // == Low
                            Qty = Convert.ToInt64(rd["qty"]),
                            From_Band = Convert.ToInt32(rd["from_band"]),
                            From_Qty = Convert.ToInt64(rd["from_qty"]),
                            Extra_Qty = Convert.ToInt64(rd["extra_qty"]),
                            진짜산가격 = Convert.ToInt64(rd["진짜산가격"])
                        });
                    }
                }
            }

            return list;
        }

        // DB에서 특정 band 1행만 읽는다 (from_band/from_qty 포함)
        public static BandRange ReadOne(string connStr, int band)
        {
            if (band <= 0) return null;

            using (var conn = new SQLiteConnection(connStr))
            using (var cmd = new SQLiteCommand(
                @"SELECT band, 팔가격, 산가격, 살가격, qty, from_band, from_qty, extra_qty, 진짜산가격
          FROM kodex200_new
          WHERE band=@b
          LIMIT 1;", conn))
            {
                cmd.Parameters.AddWithValue("@b", band);
                conn.Open();
                DB_Control.EnsureExtraQtyColumn(conn);
                using (var rd = cmd.ExecuteReader())
                {
                    if (!rd.Read()) return null;

                    return new BandRange
                    {
                        Band = Convert.ToInt32(rd["band"]),
                        팔가격 = Convert.ToInt64(rd["팔가격"]),
                        산가격 = Convert.ToInt64(rd["산가격"]),
                        살가격 = Convert.ToInt64(rd["살가격"]),
                        Qty = Convert.ToInt64(rd["qty"]),
                        From_Band = Convert.ToInt32(rd["from_band"]),
                        From_Qty = Convert.ToInt64(rd["from_qty"]),
                        Extra_Qty = Convert.ToInt64(rd["extra_qty"]),
                        진짜산가격 = Convert.ToInt64(rd["진짜산가격"])
                    };
                }
            }
        }




        // ListView에 kodex200_new 내용 표시
        public static void LoadIntoListView(string connStr, ListView listView)
        {
            if (listView == null) return;

            listView.BeginUpdate();
            try
            {
                // ✅ 핵심: 표 형태로 강제
                listView.View = View.Details;
                listView.FullRowSelect = true;
                listView.HideSelection = false;
                listView.GridLines = true;

                listView.Items.Clear();
                listView.Columns.Clear();

                // 컬럼 헤더
                listView.Columns.Add("band", 60);
                listView.Columns.Add("팔가격", 80);
                listView.Columns.Add("산가격", 80);
                listView.Columns.Add("살가격", 80);
                listView.Columns.Add("qty", 60);
                listView.Columns.Add("from_band", 70);
                listView.Columns.Add("from_qty", 70);
                listView.Columns.Add("extra_qty", 70);
                listView.Columns.Add("진짜산가격", 90);

                using (var conn = new SQLiteConnection(connStr))
                using (var cmd = new SQLiteCommand(
                    @"SELECT band, 팔가격, 산가격, 살가격, qty, from_band, from_qty, extra_qty, 진짜산가격
              FROM kodex200_new
              WHERE IFNULL(qty,0) > 0
              ORDER BY band ASC;", conn))
                {
                    conn.Open();
                    DB_Control.EnsureExtraQtyColumn(conn);
                    using (var rd = cmd.ExecuteReader())
                    {
                        while (rd.Read())
                        {
                            int band = ReadIntForView(rd["band"]);
                            long qty = ReadLongForView(rd["qty"]);
                            int fromBand = ReadIntForView(rd["from_band"]);
                            long fromQty = ReadLongForView(rd["from_qty"]);
                            long rawExtraQty = ReadLongForView(rd["extra_qty"]);
                            long extraQty = NormalizeExtraQtyForView(band, rawExtraQty);

                            var item = new ListViewItem(rd["band"].ToString());
                            item.SubItems.Add(rd["팔가격"].ToString());
                            item.SubItems.Add(rd["산가격"].ToString());
                            item.SubItems.Add(rd["살가격"].ToString());
                            item.SubItems.Add(qty.ToString());
                            item.SubItems.Add(fromBand.ToString());
                            item.SubItems.Add(fromQty.ToString());
                            item.SubItems.Add(extraQty.ToString());
                            item.SubItems.Add(rd["진짜산가격"].ToString());
                            listView.Items.Add(item);

                            LogExtraQtyView(band, fromBand, fromQty, extraQty, qty);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("LoadIntoListView 오류: " + ex.Message);
            }
            finally
            {
                listView.EndUpdate();
            }
        }

        private static int ReadIntForView(object value)
        {
            if (value == null || value == DBNull.Value)
                return 0;

            try { return Convert.ToInt32(value); }
            catch { return 0; }
        }

        private static long ReadLongForView(object value)
        {
            if (value == null || value == DBNull.Value)
                return 0L;

            try { return Convert.ToInt64(value); }
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
    }
}


//CREATE TABLE IF NOT EXISTS kodex200_new (
//    band        INTEGER PRIMARY KEY,
//    팔가격      INTEGER NOT NULL,
//    산가격      INTEGER NOT NULL,
//    살가격      INTEGER NOT NULL,
//    qty         INTEGER NOT NULL DEFAULT 0,
//    sina        INTEGER NOT NULL DEFAULT 0,
//    from_band   INTEGER NOT NULL DEFAULT 0,
//    from_qty    INTEGER NOT NULL DEFAULT 0,
//    진짜산가격  INTEGER NOT NULL DEFAULT 0
//);

//delete from  kodex200_new;
//INSERT INTO kodex200_new
//(band, 팔가격, 산가격, 살가격, qty, sina, from_band, from_qty, 진짜산가격)
//VALUES
//(1, 79999, 79410, 79400, 1, 1, 0, 0, 0),
//(2, 79399, 78810, 78800, 1, 1, 0, 0, 0),
//(3, 78799, 78210, 78200, 1, 1, 0, 0, 0),
//(4, 78199, 77610, 77600, 1, 1, 0, 0, 0),
//(5, 77599, 77010, 77000, 1, 1, 0, 0, 0),
//(6, 76999, 76410, 76400, 1, 1, 0, 0, 0),
//(7, 76399, 75810, 75800, 1, 1, 0, 0, 0),
//(8, 75799, 75210, 75200, 1, 1, 0, 0, 0),
//(9, 75199, 74610, 74600, 1, 1, 0, 0, 0),
//(10, 74599, 74010, 74000, 1, 1, 0, 0, 0),
//(11, 73999, 73410, 73400, 1, 1, 0, 0, 0),
//(12, 73399, 72810, 72800, 1, 1, 0, 0, 0),
//(13, 72799, 72210, 72200, 1, 1, 0, 0, 0),
//(14, 72199, 71610, 71600, 1, 1, 0, 0, 0),
//(15, 71599, 71010, 71000, 1, 1, 0, 0, 0),
//(16, 70999, 70410, 70400, 1, 1, 0, 0, 0),
//(17, 70399, 69810, 69800, 1, 1, 0, 0, 0),
//(18, 69799, 69210, 69200, 1, 1, 0, 0, 0),
//(19, 69199, 68840, 68600, 1, 1, 0, 0, 0),
//(20, 68599, 0, 68000, 0, 1, 0, 0, 0),
//(21, 67999, 0, 67400, 0, 1, 0, 0, 0),
//(22, 67399, 0, 66800, 0, 1, 0, 0, 0),
//(23, 66799, 0, 66200, 0, 1, 0, 0, 0),
//(24, 66199, 0, 65600, 0, 1, 0, 0, 0),
//(25, 65599, 0, 65000, 0, 1, 0, 0, 0),
//(26, 64999, 0, 64400, 0, 1, 0, 0, 0),
//(27, 64399, 0, 63800, 0, 1, 0, 0, 0),
//(28, 63799, 0, 63200, 0, 1, 0, 0, 0),
//(29, 63199, 0, 62600, 0, 1, 0, 0, 0),
//(30, 62599, 0, 62000, 0, 1, 0, 0, 0),
//(31, 61999, 0, 61400, 0, 1, 0, 0, 0),
//(32, 61399, 0, 60800, 0, 1, 0, 0, 0),
//(33, 60799, 0, 60200, 0, 1, 0, 0, 0),
//(34, 60199, 0, 59600, 0, 1, 0, 0, 0),
//(35, 59599, 0, 59000, 0, 1, 0, 0, 0),
//(36, 58999, 0, 58400, 0, 1, 0, 0, 0),
//(37, 58399, 0, 57800, 0, 1, 0, 0, 0),
//(38, 57799, 0, 57200, 0, 1, 0, 0, 0),
//(39, 57199, 0, 56600, 0, 1, 0, 0, 0),
//(40, 56599, 0, 56000, 0, 1, 0, 0, 0),
//(41, 55999, 0, 55400, 0, 1, 0, 0, 0),
//(42, 55399, 0, 54800, 0, 1, 0, 0, 0),
//(43, 54799, 0, 54200, 0, 1, 0, 0, 0),
//(44, 54199, 0, 53600, 0, 1, 0, 0, 0),
//(45, 53599, 0, 53000, 0, 1, 0, 0, 0),
//(46, 52999, 0, 52400, 0, 1, 0, 0, 0),
//(47, 52399, 0, 51800, 0, 1, 0, 0, 0),
//(48, 51799, 0, 51200, 0, 1, 0, 0, 0),
//(49, 51199, 0, 50600, 0, 1, 0, 0, 0),
//(50, 50599, 0, 50000, 0, 1, 0, 0, 0),
//(51, 49999, 0, 49400, 0, 1, 0, 0, 0),
//(52, 49399, 0, 48800, 0, 1, 0, 0, 0),
//(53, 48799, 0, 48200, 0, 1, 0, 0, 0),
//(54, 48199, 0, 47600, 0, 1, 0, 0, 0),
//(55, 47599, 0, 47000, 0, 1, 0, 0, 0),
//(56, 46999, 0, 46400, 0, 1, 0, 0, 0),
//(57, 46399, 0, 45800, 0, 1, 0, 0, 0),
//(58, 45799, 0, 45200, 0, 1, 0, 0, 0),
//(59, 45199, 0, 44600, 0, 1, 0, 0, 0),
//(60, 44599, 0, 44000, 0, 1, 0, 0, 0),
//(61, 43999, 0, 43400, 0, 1, 0, 0, 0),
//(62, 43399, 0, 42800, 0, 1, 0, 0, 0),
//(63, 42799, 0, 42200, 0, 1, 0, 0, 0),
//(64, 42199, 0, 41600, 0, 1, 0, 0, 0),
//(65, 41599, 0, 41000, 0, 1, 0, 0, 0),
//(66, 40999, 0, 40400, 0, 1, 0, 0, 0),
//(67, 40399, 0, 39800, 0, 1, 0, 0, 0),
//(68, 39799, 0, 39200, 0, 1, 0, 0, 0),
//(69, 39199, 0, 38600, 0, 1, 0, 0, 0),
//(70, 38599, 0, 38000, 0, 1, 0, 0, 0),
//(71, 37999, 0, 37400, 0, 1, 0, 0, 0),
//(72, 37399, 0, 36800, 0, 1, 0, 0, 0),
//(73, 36799, 0, 36200, 0, 1, 0, 0, 0),
//(74, 36199, 0, 35600, 0, 1, 0, 0, 0),
//(75, 35599, 0, 35000, 0, 1, 0, 0, 0),
//(76, 34999, 0, 34400, 0, 1, 0, 0, 0),
//(77, 34399, 0, 33800, 0, 1, 0, 0, 0),
//(78, 33799, 0, 33200, 0, 1, 0, 0, 0),
//(79, 33199, 0, 32600, 0, 1, 0, 0, 0),
//(80, 32599, 0, 32000, 0, 1, 0, 0, 0),
//(81, 31999, 0, 31400, 0, 1, 0, 0, 0),
//(82, 31399, 0, 30800, 0, 1, 0, 0, 0),
//(83, 30799, 0, 30200, 0, 1, 0, 0, 0),
//(84, 30199, 0, 29600, 0, 1, 0, 0, 0),
//(85, 29599, 0, 29000, 0, 1, 0, 0, 0),
//(86, 28999, 0, 28400, 0, 1, 0, 0, 0),
//(87, 28399, 0, 27800, 0, 1, 0, 0, 0),
//(88, 27799, 0, 27200, 0, 1, 0, 0, 0),
//(89, 27199, 0, 26600, 0, 1, 0, 0, 0),
//(90, 26599, 0, 26000, 0, 1, 0, 0, 0),
//(91, 25999, 0, 25400, 0, 1, 0, 0, 0),
//(92, 25399, 0, 24800, 0, 1, 0, 0, 0),
//(93, 24799, 0, 24200, 0, 1, 0, 0, 0),
//(94, 24199, 0, 23600, 0, 1, 0, 0, 0),
//(95, 23599, 0, 23000, 0, 1, 0, 0, 0);
////////////////////////////////////////////////////////////////////////////////////////////////////////////////
//기준밴드는 17
//1회 매수(FIRE) 강제 데이터(ticks_ts)
//DELETE FROM ticks_ts;

//--(IN)시작
//INSERT INTO ticks_ts(ts, price) VALUES (datetime('now','localtime'),               70015.0);
//INSERT INTO ticks_ts(ts, price) VALUES (datetime('now','+1 second','localtime'),  69950.0);

//--(OUT)하단 이탈 시작(69800 아래)
//INSERT INTO ticks_ts(ts, price) VALUES (datetime('now','+2 second','localtime'),  69790.0);
//INSERT INTO ticks_ts(ts, price) VALUES (datetime('now','+3 second','localtime'),  69750.0);
//INSERT INTO ticks_ts(ts, price) VALUES (datetime('now','+4 second','localtime'),  69700.0);

//--(OUT)저점 더 갱신
//INSERT INTO ticks_ts(ts, price) VALUES (datetime('now','+5 second','localtime'),  69650.0); --segMin = 69650

//-- ✅ (OUT)꺾임(반등) 발생: 69650-> 69750 = +100(kk = 100 충족) => 여기서 FIRE(매수 1회)
//INSERT INTO ticks_ts(ts, price) VALUES (datetime('now','+6 second','localtime'),  69750.0);

//--(선택)이후 확인용(여전히 OUT로 유지해도 되고, IN 복귀도 가능)
//INSERT INTO ticks_ts(ts, price) VALUES (datetime('now','+7 second','localtime'),  69790.0);
////////////////////////////////////////////////////////////////////////////////////////////////////////////////
///



//기준밴드는 17
//1회 매도(FIRE) 강제 데이터(ticks_ts)
//DELETE FROM ticks_ts;

//--(IN)밴드 안에서 시작
//INSERT INTO ticks_ts(ts, price) VALUES (datetime('now','localtime'),               70300.0);

//--(OUT)상단 이탈 시작: 70399 위로
//INSERT INTO ticks_ts(ts, price) VALUES (datetime('now','+1 second','localtime'),  70450.0);
//INSERT INTO ticks_ts(ts, price) VALUES (datetime('now','+2 second','localtime'),  70520.0); --segMax = 70520

//-- ✅ (OUT)되돌림(꺾임): 70520-> 70420 = 100(kk = 100 충족)
//--     그리고 70420 > 70399 이므로 OUT 유지 => 여기서 매도 FIRE 1회 기대
//INSERT INTO ticks_ts(ts, price) VALUES (datetime('now','+3 second','localtime'),  70420.0);

//--(선택)이후 확인용
//INSERT INTO ticks_ts(ts, price) VALUES (datetime('now','+4 second','localtime'),  70460.0);

////////////////////////////////////////////////////////////////////////////////////////////////////////////////

// 2026-02-16 82136
