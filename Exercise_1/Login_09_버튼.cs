
using System;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using XA_DATASETLib;

namespace Exercise_1
{
    public partial class Login
    {
        private async void button1_Click(object sender, EventArgs e)
        {
            const int sellQty = 50;
            const string shcode = "069500";

            try
            {
                if (_orderSvc == null)
                    throw new InvalidOperationException("OrderService가 초기화되지 않았습니다.");

                string acnt = (Actno ?? "").Trim();
                string pwd = "";
                try
                {
                    if (_0050_Real_Test환경결정.IsTest) pwd = (_0050_Real_Test환경결정.CertPw ?? "").Trim();
                    else pwd = (JMpass ?? "").Trim();
                }
                catch
                {
                    pwd = (JMpass ?? "").Trim();
                }

                if (string.IsNullOrWhiteSpace(acnt))
                    throw new InvalidOperationException("계좌번호가 비어있습니다.");
                if (string.IsNullOrWhiteSpace(pwd))
                    throw new InvalidOperationException("주문 비밀번호가 비어있습니다.");

                Console.WriteLine("[BUTTON1][MARKET_SELL][START] shcode=" + shcode + " qty=" + sellQty);

                var ack = await _orderSvc.PlaceAsync(new OrderRequest
                {
                    AccountNo = acnt,
                    Password = pwd,
                    Symbol = shcode,
                    Qty = sellQty,
                    Price = 0,
                    Side = TradeSide.Sell,
                    Type = OrderType.Market
                }).ConfigureAwait(false);

                Console.WriteLine("[BUTTON1][MARKET_SELL][DONE] accepted=" + ack.Accepted +
                                  " orderNo=" + (ack.OrderNo ?? "") +
                                  " msg=" + (ack.Message ?? ""));

                string message = ack.Accepted
                    ? "시장가 매도 주문 접수\r\n\r\n종목: " + shcode + "\r\n수량: " + sellQty + "\r\n주문번호: " + (ack.OrderNo ?? "")
                    : "시장가 매도 주문 실패\r\n\r\n" + (ack.Message ?? "");

                Button1ShowMessageOnUiThread(
                    message,
                    "button1 시장가 매도",
                    ack.Accepted ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[BUTTON1][MARKET_SELL][EX] " + ex.Message);
                Button1ShowMessageOnUiThread(
                    "button1 시장가 매도 오류\r\n" + ex.Message,
                    "button1 오류",
                    MessageBoxIcon.Error);
            }
        }

        private void Button1ShowMessageOnUiThread(string message, string caption, MessageBoxIcon icon)
        {
            if (IsDisposed) return;

            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action(() => Button1ShowMessageOnUiThread(message, caption, icon)));
                }
                catch (InvalidOperationException)
                {
                }
                return;
            }

            MessageBox.Show(
                this,
                message,
                caption,
                MessageBoxButtons.OK,
                icon);
        }

        private void button2_Click(object sender, EventArgs e)
        {
            try { TradingEnabled = true; button2.BackColor = System.Drawing.Color.Red; } catch { }
        }

        private async void button3_Click(object sender, EventArgs e)
        {
            async Task<long> FetchSunamtAsync(TimeSpan timeout)
            {
                string resPath = @"C:\LS_SEC\xingAPI\Res\t0424.res";
                string accno = (Actno ?? "").Trim();
                string passwd = (JMpass ?? "").Trim();

                var tcs = new TaskCompletionSource<long>();
                XAQueryClass q = null;

                try
                {
                    q = new XAQueryClass();
                    q.LoadFromResFile(resPath);

                    _IXAQueryEvents_ReceiveDataEventHandler onReceiveData = null;
                    _IXAQueryEvents_ReceiveMessageEventHandler onReceiveMsg = null;

                    onReceiveData = (trCode) =>
                    {
                        try
                        {
                            string raw = (q.GetFieldData("t0424OutBlock", "sunamt", 0) ?? "").Trim();
                            if (!long.TryParse(raw, out long sunamt))
                                throw new Exception("sunamt parse fail raw='" + raw + "'");
                            tcs.TrySetResult(sunamt);
                        }
                        catch (Exception ex) { tcs.TrySetException(ex); }
                    };

                    onReceiveMsg = (bIsSystemError, nMessageCode, szMessage) => { Console.WriteLine("[t0424 MSG] " + szMessage); };

                    q.ReceiveData += onReceiveData;
                    q.ReceiveMessage += onReceiveMsg;
                    q.SetFieldData("t0424InBlock", "accno", 0, accno);
                    q.SetFieldData("t0424InBlock", "passwd", 0, passwd);
                    q.SetFieldData("t0424InBlock", "prcgb", 0, "1");
                    q.SetFieldData("t0424InBlock", "chegb", 0, "0");
                    q.SetFieldData("t0424InBlock", "dangb", 0, "0");
                    q.SetFieldData("t0424InBlock", "charge", 0, "1");
                    q.SetFieldData("t0424InBlock", "cts_expcode", 0, "");

                    int r = q.Request(false);
                    if (r < 0) throw new Exception("t0424 Request fail r=" + r);

                    var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
                    if (done != tcs.Task) throw new TimeoutException("t0424 timeout");

                    return await tcs.Task;
                }
                finally
                {
                    try { if (q != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(q); } catch { }
                }
            }

            try
            {
                long sunamt = await FetchSunamtAsync(TimeSpan.FromSeconds(10));
                MessageBox.Show(this, "추정순자산(sunamt)\r\n\r\n" + sunamt.ToString("N0") + " 원", "t0424", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "t0424 조회 실패\r\n" + ex.Message, "t0424 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            try { _recent.Clear(); _lastUiTickPrice = double.NaN; } catch { }
            try { UiPlannedBandsText = "(없음)"; } catch { }
        }

        private void button4_Click(object sender, EventArgs e)
        {
            try
            {
                string acnt = (Actno ?? "").Trim();
                string pwd = "";
                try
                {
                    if (_0050_Real_Test환경결정.IsTest) pwd = (_0050_Real_Test환경결정.CertPw ?? "").Trim();
                    else pwd = (JMpass ?? "").Trim();
                }
                catch
                {
                    pwd = (JMpass ?? "").Trim();
                }

                string shcode = "069500";   // KODEX200
                string qty = "108";
                string price = textBox1.Text;
                string bnsTpCode = "2";     // 2 = 매수
                string ordprcPtnCode = "00"; // 00 = 지정가

                Console.WriteLine("[BUTTON4] =======================================");
                Console.WriteLine("[BUTTON4] 테스트용 미체결 매수주문 시작");
                Console.WriteLine("[BUTTON4] Mode      = " + (_0050_Real_Test환경결정.IsTest ? "TEST" : "REAL"));
                Console.WriteLine("[BUTTON4] Account   = " + acnt);
                Console.WriteLine("[BUTTON4] Code      = " + shcode);
                Console.WriteLine("[BUTTON4] Qty       = " + qty);
                Console.WriteLine("[BUTTON4] Price     = " + price);
                Console.WriteLine("[BUTTON4] Side      = BUY");
                Console.WriteLine("[BUTTON4] Hoga      = 지정가(00)");
                Console.WriteLine("[BUTTON4] 현재가가 87880 근처라면 이 주문은 즉시 체결되지 않고 미체결 대기 가능성이 큼");
                Console.WriteLine("[BUTTON4] =======================================");

                var dr = MessageBox.Show(
                    this,
                    "테스트용 미체결 매수주문을 전송합니다.\r\n\r\n" +
                    "종목코드 : " + shcode + "\r\n" +
                    "수량     : " + qty + "\r\n" +
                    "가격     : " + price + "\r\n" +
                    "구분     : 매수\r\n" +
                    "호가     : 지정가\r\n\r\n" +
                    "계속하시겠습니까?",
                    "button4 테스트 주문",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (dr != DialogResult.Yes)
                {
                    Console.WriteLine("[BUTTON4] 사용자가 주문을 취소했습니다.");
                    return;
                }

                string resPath = @"C:\LS_SEC\xingAPI\Res\CSPAT00600.res";
                XAQueryClass query = new XAQueryClass();
                query.LoadFromResFile(resPath);

                query.ReceiveMessage += (bIsSystemError, nMessageCode, szMessage) =>
                {
                    try
                    {
                        Console.WriteLine("[BUTTON4][MSG] SystemError=" + bIsSystemError +
                                          ", Code=" + nMessageCode +
                                          ", Msg=" + szMessage);
                    }
                    catch { }
                };

                query.ReceiveData += (szTrCode) =>
                {
                    try
                    {
                        string ordNo = (query.GetFieldData("CSPAT00600OutBlock2", "OrdNo", 0) ?? "").Trim();
                        string ordTime = (query.GetFieldData("CSPAT00600OutBlock2", "OrdTime", 0) ?? "").Trim();
                        string ordMktCode = (query.GetFieldData("CSPAT00600OutBlock2", "OrdMktCode", 0) ?? "").Trim();
                        string ordPtnCode = (query.GetFieldData("CSPAT00600OutBlock2", "OrdPtnCode", 0) ?? "").Trim();
                        string shtnIsuNo = (query.GetFieldData("CSPAT00600OutBlock2", "ShtnIsuNo", 0) ?? "").Trim();

                        Console.WriteLine("[BUTTON4][RECV] 주문전송확인 수신");
                        Console.WriteLine("[BUTTON4][RECV] OrdNo      = " + ordNo);
                        Console.WriteLine("[BUTTON4][RECV] OrdTime    = " + ordTime);
                        Console.WriteLine("[BUTTON4][RECV] OrdMktCode = " + ordMktCode);
                        Console.WriteLine("[BUTTON4][RECV] OrdPtnCode = " + ordPtnCode);
                        Console.WriteLine("[BUTTON4][RECV] ShtnIsuNo  = " + shtnIsuNo);

                        MessageBox.Show(
                            this,
                            "button4 테스트 주문 전송 완료\r\n\r\n" +
                            "OrdNo   : " + ordNo + "\r\n" +
                            "OrdTime : " + ordTime,
                            "주문전송확인",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                    }
                    catch (Exception ex2)
                    {
                        Console.WriteLine("[BUTTON4][RECV EX] " + ex2.Message);
                    }
                    finally
                    {
                        try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(query); } catch { }
                    }
                };

                query.SetFieldData("CSPAT00600InBlock1", "AcntNo", 0, acnt);
                query.SetFieldData("CSPAT00600InBlock1", "InptPwd", 0, pwd);
                query.SetFieldData("CSPAT00600InBlock1", "IsuNo", 0, shcode);
                query.SetFieldData("CSPAT00600InBlock1", "OrdQty", 0, qty);
                query.SetFieldData("CSPAT00600InBlock1", "OrdPrc", 0, price);
                query.SetFieldData("CSPAT00600InBlock1", "BnsTpCode", 0, bnsTpCode);
                query.SetFieldData("CSPAT00600InBlock1", "OrdprcPtnCode", 0, ordprcPtnCode);
                query.SetFieldData("CSPAT00600InBlock1", "MgntrnCode", 0, "000");
                query.SetFieldData("CSPAT00600InBlock1", "LoanDt", 0, "");
                query.SetFieldData("CSPAT00600InBlock1", "OrdCndiTpCode", 0, "0");

                int r = query.Request(false);
                Console.WriteLine("[BUTTON4] CSPAT00600 Request return = " + r);

                if (r < 0)
                {
                    try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(query); } catch { }
                    MessageBox.Show(
                        this,
                        "주문 요청 실패\r\nRequest return = " + r,
                        "button4 오류",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    "button4_Click 오류\r\n" + ex.Message,
                    "button4 오류",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void button6_Click(object sender, EventArgs e)
        {
            try
            {
                // textBox5는 t0424 janqty 전용이므로 DB qty 합계는 textBox8에 표시한다.
                _dbFuncs?.LoadKodexToListView(listView1, highlightMaxBandWithQtyNonZero: true, textBoxQtySum: textBox8);
                _ = RefreshBrokerJanQtyTextBox5Async("BUTTON6_RELOAD", force: true);
            }
            catch { }
            RefreshBandsAndTriggers();
        }

        private async void button8_Click(object sender, EventArgs e)
        {
            try
            {
                UpdateStatus("DB 틱 replay 준비 중...");
                try { _tickFromXing?.Stop(); } catch { }
                try { Login.BandList = 시작밴드Read.LoadAllBands(); } catch { }

                if (_tickFromDb == null)
                {
                    UpdateStatus("DB 틱 replay 실패: _tickFromDb가 초기화되지 않았습니다.");
                    MessageBox.Show("DB 환경 적용 후 다시 시도하세요. (_tickFromDb null)");
                    return;
                }

                int tickCount = CountTicksTsRows();
                if (tickCount <= 0)
                {
                    UpdateStatus("DB 틱 replay 실패: ticks_ts 데이터가 없습니다.");
                    MessageBox.Show("ticks_ts 테이블에 재생할 틱 데이터가 없습니다.");
                    return;
                }

                UpdateStatus("DB 틱 replay 시작: ticks_ts " + tickCount + "건");
                Console.WriteLine("[BUTTON8][DB-REPLAY] START ticks_ts count=" + tickCount);

                Login.IsReplayMode = true;
                Console.WriteLine("[BUTTON8][DB-REPLAY] ReplayMode ON");

                try
                {
                    await _tickFromDb.StartReplayAsync(delayMsPerTick: 300);
                }
                finally
                {
                    Login.IsReplayMode = false;
                    Console.WriteLine("[BUTTON8][DB-REPLAY] ReplayMode OFF");
                }

                UpdateStatus("DB 틱 replay 완료: ticks_ts " + tickCount + "건");
                Console.WriteLine("[BUTTON8][DB-REPLAY] DONE ticks_ts count=" + tickCount);
            }
            catch (Exception ex)
            {
                UpdateStatus("DB 틱 replay 오류: " + ex.Message);
                Console.WriteLine("[BUTTON8][DB-REPLAY][ERR] " + ex);
                MessageBox.Show("DB 틱 replay 오류\r\n" + ex.Message);
            }
        }

        private int CountTicksTsRows()
        {
            using (var conn = new SQLiteConnection(Login.ConnStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM ticks_ts";
                    object val = cmd.ExecuteScalar();
                    return val != null && val != DBNull.Value ? Convert.ToInt32(val) : 0;
                }
            }
        }

        public void RecordTickIfEnabled(long price)
        {
            if (!_isRecordingTicks) return;
            try { _자료수집?.InsertTick(price); } catch { }
        }

        private void RedirectConsoleToFile()
        {
            AppLog.EnsureStarted();

            var logPath = AppLog.PathName;
            var consoleWriter = Console.Out;

            var tee = new TeeTextWriter(consoleWriter);
            Console.SetOut(tee);
            Console.SetError(tee);

            AppLog.Info("App", "Console redirected (console + unified file)");
            AppLog.Info("App", "LOG=" + logPath);
        }

        public sealed class TeeTextWriter : TextWriter
        {
            private readonly TextWriter _console;
            private readonly StringBuilder _line = new StringBuilder();

            public TeeTextWriter(TextWriter console) { _console = console; }
            public override Encoding Encoding => _console.Encoding;

            public override void Write(char value)
            {
                _console.Write(value);
                if (value == '\n')
                    FlushLine();
                else if (value != '\r')
                    _line.Append(value);
            }

            public override void Write(string value)
            {
                _console.Write(value);
                if (string.IsNullOrEmpty(value)) return;
                for (int i = 0; i < value.Length; i++)
                {
                    char c = value[i];
                    if (c == '\n')
                        FlushLine();
                    else if (c != '\r')
                        _line.Append(c);
                }
            }

            public override void WriteLine(string value)
            {
                _console.WriteLine(value);
                AppLog.Info("Console", value ?? "");
            }

            public override void Flush()
            {
                _console.Flush();
                FlushLine();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) FlushLine();
                base.Dispose(disposing);
            }

            private void FlushLine()
            {
                if (_line.Length == 0) return;
                AppLog.Info("Console", _line.ToString());
                _line.Length = 0;
            }
        }

        private void button7_Click(object sender, EventArgs e)
        {
            string acnt = (Actno ?? "").Trim();
            string pwd = "";
            try
            {
                if (_0050_Real_Test환경결정.IsTest) pwd = (_0050_Real_Test환경결정.CertPw ?? "").Trim();
                else pwd = (JMpass ?? "").Trim();
            }
            catch { pwd = (JMpass ?? "").Trim(); }

            MessageBox.Show(this, "Actno='" + acnt + "'\r\nPwd='" + pwd + "'\r\nMode=" + (_0050_Real_Test환경결정.IsTest ? "TEST" : "REAL"), "button7 DEBUG", MessageBoxButtons.OK, MessageBoxIcon.Information);
            RequestOrderableCashTextBox6Refresh("BUTTON7_MANUAL", delayMs: 0);
        }

        private async void button9_Click_1(object sender, EventArgs e)
        {
            if (!double.TryParse(textBox9.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double sec))
            {
                if (!double.TryParse(textBox9.Text.Trim(), out sec))
                {
                    MessageBox.Show("초 단위 입력(소수 가능)예: 0.5 / 1 / 1.25");
                    return;
                }
            }

            if (sec < 0)
            {
                MessageBox.Show("초 단위 입력(0 이상, 소수 가능)");
                return;
            }

            if (_tickProcess == null)
            {
                MessageBox.Show("0250 엔진이 초기화되지 않았습니다.");
                return;
            }

            try
            {
                var runner = new ReplayTickRunner(_tickProcess);
                await runner.RunAsync(sec);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Replay 실행 오류" + ex.Message);
            }
        }

        private void label12_Click(object sender, EventArgs e) { }
        private void label5_Click(object sender, EventArgs e) { }
        private void label6_Click(object sender, EventArgs e) { }
        private void Login_Load(object sender, EventArgs e) { }
        private void label11_Click(object sender, EventArgs e) { }
        private void label13_Click(object sender, EventArgs e) { }
        private void label10_Click(object sender, EventArgs e) { }
    }
}
// 2026-05-13 30658
