using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Exercise_1
{
    public partial class Login : Form
    {
        // 🔥 Instance 방식 유지
        public static Login Instance { get; private set; }

        public Login()
        {
            InitializeComponent();

            // 🔥 핵심 수정
            Instance = this;
            LoginFormAccessor.Set(this);   // ⭐ 이 줄이 listView1 자동 로딩 복구 포인트

            ListView1Ref = this.listView1;

            try { }
            catch { }

            EnsureCoreModulesInitialized();

            _orderSvc = new OrderService();
            GlobalOrderSvc = _orderSvc;

            _orderSvc.OrderAccepted += ack => OnOrderAccepted_OnLoop(ack);
            _orderSvc.OrderRejected += msg => OnOrderRejected_OnLoop(msg);
            _orderSvc.OrderUpdated += row => OnOrderUpdated_OnLoop(row);

            _lv3Manager = new _0670_listView3_당일거래(
                this,
                this.listView3,
                _orderSvc,
                () => Actno,
                () => JMpass,
                () => currentShcode
            );

            _broker = new XingBrokerClient();

            _bal0900 = new _0900_banance_cspaq12200_t0424(
                s => Console.WriteLine("[0900] " + s)
            );

            _tickProcess = null;

            _tickFromXing = new _0230_Tick_fromXing();

            // =====================================================
            // 핵심 수정
            // -----------------------------------------------------
            // 기존:
            //   OnTick  -> HandleUiTick(t.Price)
            //   OnPrice -> _tickProcess.ProcessTickAsync(p)
            //
            // 변경:
            //   OnTick  -> TickFromDb_OnPrice(t.Price)
            //             즉, UI 갱신 + Pending 재평가 + _tickProcess 호출까지
            //             한 경로로 통일
            //
            //   OnPrice -> 중복 호출 방지를 위해 _tickProcess 호출 제거
            // =====================================================
            _tickFromXing.OnTick += t =>
            {
                try
                {
                    TickFromDb_OnPrice(t.Price);
                }
                catch { }
            };

            _tickFromXing.OnPrice += p =>
            {
                try
                {
                    // 중복 처리 방지:
                    // TickFromDb_OnPrice() 안에서 이미
                    // HandleUiTick(p) + _tickProcess.ProcessTickAsync(p) 를 호출한다.
                    //
                    // 따라서 여기서는 별도 처리하지 않는다.
                }
                catch { }
            };

            _tickFromXing.OnLog += s => System.Diagnostics.Debug.WriteLine(s);

            try
            {
                this.button6.Click -= button6_Click;
                this.button6.Click += button6_Click;
            }
            catch { }

            this.Load += MainForm_Load;
            this.Shown += Login_Shown_Bootstrap;

            _cashQuery = new _1000_현금주문가능금액();

            WireUpSlideDelegates();

            // ✅ DB qty 변경 시 textBox8 자동 갱신
            DbFuncs.KodexQtyUpdated += OnKodexQtyUpdated;
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            EnsureCoreModulesInitialized();

            try
            {
                _tickCalc2 = new _0270_틱계산2(
                    this,
                    this.textBox1,
                    this.textBox2,
                    this.textBox3,
                    this.textBox4,
                    this.textBox7
                );
                Console.WriteLine("[LOGIN][0270] init OK");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[LOGIN][0270] init FAIL: " + ex.Message);
            }

            try
            {
                if (_tickCalc2 != null)
                {
                    _tickProcess = new _0250_Tick_Process(this, _tickCalc2);
                    Console.WriteLine("[LOGIN][0250] init OK");
                }
                else
                {
                    Console.WriteLine("[LOGIN][0250] init SKIP");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[LOGIN][0250] init FAIL: " + ex.Message);
            }

            SetPanel2Color(Color.LightGray);

            try
            {
                var debugDir = new System.IO.DirectoryInfo(Application.StartupPath);
                if (debugDir.Parent != null &&
                    debugDir.Parent.Parent != null &&
                    debugDir.Parent.Parent.Parent != null)
                {
                    textBox16.Text = debugDir.Parent.Parent.Parent.FullName;
                }
                else
                {
                    textBox16.Text = Application.StartupPath;
                }
            }
            catch { }
        }

        private void ShowLogFileNameOnListBox()
        {
            try
            {
                if (listBox1 == null || listBox1.IsDisposed) return;
                listBox1.HorizontalScrollbar = true;
                listBox1.Items.Clear();
                listBox1.Items.Add(AppLog.FileName);

                SetLogDisplayText(AppLog.FileName, "LOG_CREATED");
            }
            catch { }
        }

        private async void Login_Shown_Bootstrap(object sender, EventArgs e)
        {
            if (_booted) return;
            _booted = true;

            EnsureCoreModulesInitialized();

            if (!_0001_real_test선택.TryChoose(out string runMode))
            {
                Close();
                return;
            }

            try
            {
                _0050_Real_Test환경결정.Apply(runMode);
                RedirectConsoleToFile();
                ShowLogFileNameOnListBox();
                ScheduleBootCaptureOnce();
                LogExistingTradingProcesses();
            }
            catch (Exception ex)
            {
                UpdateStatus("환경 오류: " + ex.Message);
                return;
            }

            try
            {
                ApplyEnvAndReloadDb_Safe("BOOT");   // ⭐ 여기서 listView1 자동 로딩 발생
            }
            catch (Exception ex)
            {
                UpdateStatus("DB 로딩 실패: " + ex.Message);
                return;
            }

            try
            {
                var pendingChecker = new _0002_Pending확인(_0050_Real_Test환경결정.ConnStr,textBox13);
                pendingChecker.CheckAndShow();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Pending 오류: " + ex.Message);
            }

            try
            {
                await StartTradingPipelineAsync();
            }
            catch (Exception ex)
            {
                UpdateStatus("기동 오류: " + ex.Message);
                return;
            }

            // ✅ 초기 1회 DB total qty 로드
            LoadDbQtySum();
        }

        private void LogExistingTradingProcesses()
        {
            try
            {
                int currentId = System.Diagnostics.Process.GetCurrentProcess().Id;
                string[] names = { "Exercise_1", "서방불패" };

                foreach (string name in names)
                {
                    System.Diagnostics.Process[] processes;
                    try { processes = System.Diagnostics.Process.GetProcessesByName(name); }
                    catch { continue; }

                    foreach (var p in processes)
                    {
                        try
                        {
                            if (p.Id == currentId)
                                continue;

                            string path = "";
                            string start = "";
                            try { path = p.MainModule != null ? p.MainModule.FileName : ""; } catch { }
                            try { start = p.StartTime.ToString("yyyy-MM-dd HH:mm:ss"); } catch { }

                            Console.WriteLine("[PROC][WARN]");
                            Console.WriteLine("process=" + p.ProcessName);
                            Console.WriteLine("pid=" + p.Id);
                            Console.WriteLine("startTime=" + start);
                            Console.WriteLine("path=" + path);
                            Console.WriteLine("action=warn_only");
                        }
                        catch { }
                        finally
                        {
                            try { p.Dispose(); } catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[PROC][WARN][EX] " + ex.Message);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { DbFuncs.KodexQtyUpdated -= OnKodexQtyUpdated; } catch { }
            try { _tickFromXing?.Dispose(); } catch { }

            base.OnFormClosed(e);
        }

        public int CurrentPrice
        {
            get
            {
                try
                {
                    return Convert.ToInt32(textBox1.Text);
                }
                catch
                {
                    return 0;
                }
            }
        }

        // =====================================================
        // [ADD] DB TOTAL QTY → textBox8
        // =====================================================
        private void LoadDbQtySum()
        {
            try
            {
                var db = new DbFuncs(Login.DbPath);
                long sumQty = db.GetTotalQty();

                if (this.textBox8 != null)
                    this.textBox8.Text = sumQty.ToString("N0");

                Console.WriteLine("[UI] TOTAL QTY = " + sumQty);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[UI][ERROR] " + ex.Message);
            }
        }

    }
}
// 2026-04-20 41827
