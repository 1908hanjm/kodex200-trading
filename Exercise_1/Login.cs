//1120
//만일 현재값이 band값의 팔가격을 상향으로넘는다면
//리스트박스에 있는 틱max를  textBox3 에 넣어줘
//textboxbox2 를 채워줘 식은 textbox3 - textbox1
//textbox7, textbox4에 0값 assign
//
//만일 현재값이 만일 band값의 살가격을 하향으로 넘는다면
//리스트박스에 있는 틱min를  textBox7 에 넣어줘
//textbox4 를 채워줘 식은 textbox7 - textbox3
//textbox3, textbox3에 0값 assign

// Login.cs (복붙용 / C# 7.3)  [static 방식 통일: 0001 선택 + 0050 Apply]
// ------------------------------------------------------------
// ✅ 최종 확정(사용자 결정):
// - REAL/TEST 라디오 버튼 삭제/미사용
// - 프로그램 기동(Shown) 시 MessageBox(0001)로 1회 선택
// - 0050.Apply(runMode)로 환경을 "고정" (static single source of truth)
// - 이후 DB/UI/모듈은 0050의 DbPath/ConnStr/Account/UserId/Password만 참조
// - 기존 Login_Shown_Async의 "자동 접속" 흐름은 StartRealMode_Async로 격리하여
//   IsReal일 때만 실행되도록 변경
// ------------------------------------------------------------

using Exercise_1.Domain;
using Exercise_1.Repositories;
using System;
using System.Collections.Generic;
using System.ComponentModel;   // BindingList
using System.Data;
using System.Data.SQLite;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using XA_DATASETLib;
using XA_SESSIONLib;
using Lv3Manager = Exercise_1._060_listView3_당일거래;

namespace Exercise_1
{
    public partial class Login : Form
    {
        // ─────────────────────────────────────────────────────────────────────
        // ✅ 전역 static
        // ─────────────────────────────────────────────────────────────────────
        public static BindingList<BandRange> BandList = new BindingList<BandRange>();

        // ✅ DbPath/ConnStr/Account는 0050(static)만 참조한다.
        public static string DbPath
        {
            get
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(_0050_Real_Test환경결정.DbPath))
                        return _0050_Real_Test환경결정.DbPath.Trim();
                }
                catch { }
                // ★ 기동 직후(Apply 전) fallback
                return @"C:\c#\mydb.db";
            }
        }

        public static string ConnStr
        {
            get
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(_0050_Real_Test환경결정.ConnStr))
                        return _0050_Real_Test환경결정.ConnStr;
                }
                catch { }
                return $"Data Source={DbPath};Version=3;";
            }
        }

        public static System.Windows.Forms.ListView ListView1Ref;   // ✅ ListView 모호성 방지

        public static int 현재가변수 = 0;
        public static int 시작밴드변수;

        public static string currentShcode = "069500";

        // ✅ REAL 기본값(기존 변수는 유지하되, 실제 사용은 0050에서만)
        public static string JMid = "cds002";
        public static string JMAuth = "1908hanjm!!";
        public static string Actno_Real = "00511723753";

        // ✅ 계좌비번(4자리)
        public static string JMpass = "1908";

        public static int 꺽임변수 = 100;

        // ✅ 계좌번호는 0050(static)에서만
        public static string Actno => _0050_Real_Test환경결정.Account ?? "";

        // ✅ 신형 모듈(static)
        public static _0550_매매전송후대기 TradeWait;
        public static _0600_주문번호_매핑 OrdMap;
        public static _0650_SC1_수신처리 Sc1Receiver;
        public static _0700_매매후update AfterFillUpdate70;

        public static OrderService GlobalOrderSvc;
        public static 매매_Xing XingTrade { get; private set; }

        // ✅ FocusBand(기존 유지)
        public static int FocusBand { get; private set; } = 0;
        public static long FocusVersion { get; private set; } = 0;

        public static void SetFocusBand(int newBand, string why)
        {
            if (newBand < 0) newBand = 0;
            if (FocusBand == newBand) return;

            FocusBand = newBand;
            FocusVersion++;

            Console.WriteLine($"[FOCUS] band -> {FocusBand} (ver={FocusVersion}) why={why}");
            Debug.WriteLine($"[FOCUS] band -> {FocusBand} (ver={FocusVersion}) why={why}");
        }

        // ✅ 0700이 갱신하는 인스턴스 속성
        public int CurrentStartBand { get; set; } = 0;

        // ─────────────────────────────────────────────────────────────────────
        // ✅ 0910 RichTextBox 통합 모듈(로그용) - "기존처럼"을 위해 기본 Disabled
        // ─────────────────────────────────────────────────────────────────────
        public _0910_Richtextbox RTB910 { get; private set; }

        // ─────────────────────────────────────────────────────────────────────
        // instance fields
        // ─────────────────────────────────────────────────────────────────────
        private 자료수집 _자료수집;
        private bool _isRecordingTicks = false;

        // ★ SC1 XAReal 보관 (GC/수명 문제 방지)
        private XARealClass _realSC1;

        private readonly OrderService _orderSvc;

        private string _replayConnStr;
        private IDecisionUnit _decision;

        private int _currentHoldingQty = 0;
        private double _currentCash = 0;
        private double _currentD2Estimate = 0;
        private double _todayRealizedPnl = 0;

        private readonly IBrokerClient _broker;
        private DbFuncs _dbFuncs;

        private ToolStripStatusLabel statusStripLabel
            => this.GetType().GetField("toolStripStatusLabel1",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(this) as ToolStripStatusLabel;

        private Label statusLabel
            => this.GetType().GetField("labelStatus",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(this) as Label;

        private IBandRepo _bandRepo;
        private ICycleLogRepo _cycleLogRepo;

        private DataTable _bands;

        // ✅ 기존처럼 밴드 화면 구성 엔진(중요)
        private RichTextBoxBands _rtbBands;

        private readonly List<int> _recent = new List<int>();
        public static bool TradingEnabled { get; private set; } = false;

        private _060_listView3_당일거래 _lv3Manager;

        private _0100_Xing_connect _xingConn;
        private _0900_banance_cspaq12200_t0424 _bal0900;

        private 매매_Xing _mmXing;
        private 매매실행 _exec;
        public 매매실행 Exec => _exec;

        private _0210_Tick_fromDB _tickFromDb;
        private _0230_Tick_fromXing _tickFromXing;
        private _0250_Tick_Process _tickProcess;

        private double _lastUiTickPrice = double.NaN;

        private _0270_틱계산2 _tickCalc2;          // ✅ 0270(틱계산2) - UI 계산/표시 모듈

        // ✅ Sc1Receiver.Filled 구독 여부(중복구독 방지)
        private bool _isSc1FilledSubscribed = false;

        // ✅ DB 재로딩 중복/경합 방지
        private readonly object _envReloadLock = new object();
        private bool _envReloading = false;

        // ✅ 기동 1회만
        private bool _booted = false;

        public Login()
        {
            InitializeComponent();
            LoginFormAccessor.Set(this);

            // ✅ 로그 리다이렉트: Apply 전이므로 DB는 fallback 경로로 기록(기동 후 Apply에 의해 실제 DB는 변경됨)
            RedirectConsoleToFile();

            ListView1Ref = this.listView1;

            // ✅ 0910_Richtextbox는 "로그 전용"인데,
            // 지금은 richTextBox1을 기존처럼 쓰기 위해 기본 Disabled(출력 금지).
            try
            {
                RTB910 = new _0910_Richtextbox(this, this.richTextBox1);
                RTB910.PrefixTime = false;
                RTB910.Enabled = false; // ★ 핵심: 기존처럼 보이게
            }
            catch { }

            EnsureCoreModulesInitialized();

            // 주문 서비스
            _orderSvc = new OrderService();
            GlobalOrderSvc = _orderSvc;

            // ✅ 이벤트 핸들러 반드시 존재
            _orderSvc.OrderAccepted += ack => OnOrderAccepted_OnLoop(ack);
            _orderSvc.OrderRejected += msg => OnOrderRejected_OnLoop(msg);
            _orderSvc.OrderUpdated += row => OnOrderUpdated_OnLoop(row);

            // listView3 매니저 (Actno는 0050 static 기반)
            _lv3Manager = new _060_listView3_당일거래(
                this,
                this.listView3,
                _orderSvc,
                () => Actno,
                () => JMpass,
                () => currentShcode
            );

            // 브로커
            _broker = new XingBrokerClient();

            // 0900
            _bal0900 = new _0900_banance_cspaq12200_t0424(s => Debug.WriteLine("[0900] " + s));

            // Tick 모듈
            _tickProcess = new _0250_Tick_Process(this);

            _tickFromXing = new _0230_Tick_fromXing();
            _tickFromXing.OnTick += t => { try { HandleUiTick(t.Price); } catch { } };
            _tickFromXing.OnPrice += p => { try { _ = _tickProcess.ProcessTickAsync(p); } catch { } };
            _tickFromXing.OnLog += s => Debug.WriteLine(s);

            // ✅ _tickFromDb / _자료수집 / _rtbBands / _dbFuncs 는 0050.Apply + DB 결정 후에 생성해야 함
            // → ApplyEnvAndReloadDb_Safe()가 생성/재생성한다.

            // button6 중복 방지(있으면)
            try
            {
                this.button6.Click -= button6_Click;
                this.button6.Click += button6_Click;
            }
            catch { }

            // 폼 이벤트
            this.Load += MainForm_Load;

            // ✅ Shown은 1개만: 기동 부트스트랩(0001 + 0050 Apply + DB reload + REAL/TEST 분기)
            this.Shown += Login_Shown_Bootstrap;
        }

        private void EnsureCoreModulesInitialized()
        {
            if (TradeWait == null) TradeWait = new _0550_매매전송후대기();
            if (OrdMap == null) OrdMap = new _0600_주문번호_매핑();
            if (Sc1Receiver == null) Sc1Receiver = new _0650_SC1_수신처리();
            if (AfterFillUpdate70 == null) AfterFillUpdate70 = new _0700_매매후update(this);
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            EnsureCoreModulesInitialized();

            // ✅ 0270 초기화(한 번만)
            try
            {
                _tickCalc2 = new _0270_틱계산2(
                    owner: this,
                    textBox1_Current: this.textBox1,
                    textBox2_DiffUp: this.textBox2,
                    textBox3_TickMax: this.textBox3,
                    textBox4_DiffDown: this.textBox4,
                    textBox7_TickMin: this.textBox7
                );

                Console.WriteLine("[LOGIN][0270] init OK");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[LOGIN][0270] init FAIL: " + ex.Message);
            }

            // ✅ 초기 상태 표시
            SetPanel2Color(Color.LightGray);
        }

        // ─────────────────────────────────────────────
        // ✅ 기동 부트스트랩(Shown 1회)
        //   1) 0001_real_test선택 (MessageBox)
        //   2) 0050.Apply (static 확정)
        //   3) DB/UI/모듈 로드
        //   4) REAL -> StartRealMode_Async / TEST -> StartTestMode
        // ─────────────────────────────────────────────
        private async void Login_Shown_Bootstrap(object sender, EventArgs e)
        {
            if (_booted) return;
            _booted = true;

            EnsureCoreModulesInitialized();

            // 1) 사용자 선택
            if (!_0001_real_test선택.TryChoose(out string runMode))
            {
                Close();
                return;
            }

            // 2) 환경 확정(딱 1회)
            try
            {
                _0050_Real_Test환경결정.Apply(runMode);
            }
            catch (Exception ex)
            {
                UpdateStatus("환경 확정 오류: " + ex.Message);
                SetPanel2Color(Color.Red);
                return;
            }

            // (선택) 타이틀 표시
            try
            {
                this.Text = _0050_Real_Test환경결정.IsTest ? "Login - TEST (DEMO)" : "Login - REAL";
            }
            catch { }

            // 3) DB/UI/모듈 로드 (0050.DbPath 기반)
            try
            {
                ApplyEnvAndReloadDb_Safe(reason: "BOOT");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[BOOT][ApplyEnvAndReloadDb] " + ex);
                UpdateStatus("DB/UI 로딩 실패: " + ex.Message);
                SetPanel2Color(Color.Red);
                return;
            }

            // 4) 분기
            try
            {
                if (_0050_Real_Test환경결정.IsReal)
                {
                    await StartRealMode_Async();
                }
                else
                {
                    StartTestMode();
                }
            }
            catch (Exception ex)
            {
                UpdateStatus("기동 분기 오류: " + ex.Message);
                SetPanel2Color(Color.Red);
            }
        }

        // ─────────────────────────────────────────────
        // ✅ TEST 시작(실시간 XING 접속 금지)
        // ─────────────────────────────────────────────
        // StartTestMode()를 "모의서버 로그인 + 주문 가능" 버전으로 교체
        private async void StartTestMode()
        {
            try
            {
                EnsureCoreModulesInitialized();

                // 거래 토글
                try
                {
                    if (checkBox1 != null)
                    {
                        checkBox1.Checked = true;
                        checkBox1.Text = "거래시작";
                        TradingEnabled = true;
                    }
                }
                catch { }

                // ✅ TEST도 REAL처럼: 0100 생성 + 로그인 + 매매_Xing 생성
                _xingConn = new _0100_Xing_connect(
                    jmid: _0050_Real_Test환경결정.UserId,
                    jmauth: _0050_Real_Test환경결정.Password,
                    updateStatus: UpdateStatus,
                    setPanel2Color: SetPanel2Color
                );

                // ✅ 중요: ConnectAsync 내부가 "TEST면 모의서버"로 붙도록 되어 있어야 합니다.
                // (그게 아니라면 _0100_Xing_connect 쪽에서 서버 선택 로직을 넣어야 합니다.)
                _mmXing = await _xingConn.ConnectAsync(currentShcode);
                if (_mmXing == null)
                {
                    UpdateStatus("TEST 로그인 실패");
                    SetPanel2Color(Color.Red);
                    return;
                }

                XingTrade = _mmXing;

                // ✅ SC1 수신 시작 + Filled 이벤트 구독
                TryStartSc1ReceiverOnce();
                SubscribeSc1FilledOnce();

                // ✅ Exec 생성 (이게 없어서 Exec null이 났던 겁니다)
                _exec = new 매매실행(_mmXing, () => currentShcode);
                _exec.Log += s => Debug.WriteLine("[매매실행][TEST] " + s);

                // ✅ 실시간 틱은 원하면 계속 막아도 됨(= DB replay만 사용)
                try { _tickFromXing?.Stop(); } catch { }
                try { _tickFromDb?.Stop(); } catch { } // 사용자가 button8로 시작하니까 기본 stop 유지

                UpdateStatus($"TEST(모의서버) 준비 완료  {_0050_Real_Test환경결정.LogPrefix}  DB={DbPath}");
                SetPanel2Color(Color.LightSkyBlue);

                Console.WriteLine($"[BOOT][TEST] ready(SERVER) db='{DbPath}' act='{Actno}' execReady={(this.Exec != null)}");
            }
            catch (Exception ex)
            {
                UpdateStatus("TEST 시작 오류: " + ex.Message);
                SetPanel2Color(Color.Red);
                Console.WriteLine("[BOOT][TEST] EX: " + ex);
            }
        }
        // 2026-02-03 64192


        // ─────────────────────────────────────────────
        // ✅ REAL 시작 (기존 Login_Shown_Async의 자동접속 흐름을 여기로 이동)
        // ─────────────────────────────────────────────
        private async Task StartRealMode_Async()
        {
            try
            {
                EnsureCoreModulesInitialized();

                // 거래 토글
                try
                {
                    if (checkBox1 != null)
                    {
                        checkBox1.Checked = true;
                        checkBox1.Text = "거래시작";
                        TradingEnabled = true;
                    }
                }
                catch { }

                // ✅ 0100 생성: 0050(static) 기반
                _xingConn = new _0100_Xing_connect(
                    jmid: _0050_Real_Test환경결정.UserId,
                    jmauth: _0050_Real_Test환경결정.Password,
                    updateStatus: UpdateStatus,
                    setPanel2Color: SetPanel2Color
                );

                // XING connect + 로그인 + 매매_Xing 생성
                _mmXing = await _xingConn.ConnectAsync(currentShcode);
                if (_mmXing == null) return;

                XingTrade = _mmXing;

                TryStartSc1ReceiverOnce();
                SubscribeSc1FilledOnce();

                _exec = new 매매실행(_mmXing, () => currentShcode);
                _exec.Log += s => Debug.WriteLine("[매매실행] " + s);

                try { _tickFromDb?.Stop(); } catch { }
                try { _tickFromXing?.Stop(); } catch { }

                try
                {
                    _tickFromXing?.Start(currentShcode);
                    UpdateStatus($"실시간 틱 시작: {currentShcode}  {_0050_Real_Test환경결정.LogPrefix}");
                }
                catch { }

                try
                {
                    await Lv3Manager.ReloadAsync(
                        owner: this,
                        lv: listView3,
                        orderSvc: _orderSvc,
                        getActNo: () => Actno,
                        getPwd: () => JMpass,
                        getShcode: () => currentShcode
                    );
                }
                catch { }

                try { await FetchDailyBalanceAsync(TimeSpan.FromSeconds(15)); } catch { }

                UpdateStatus("초기화 완료 " + _0050_Real_Test환경결정.LogPrefix);
                SetPanel2Color(Color.LightGreen);

                Console.WriteLine($"[BOOT][REAL] ready db='{DbPath}' act='{Actno}'");
            }
            catch (Exception ex)
            {
                UpdateStatus("초기화 오류: " + ex.Message);
                SetPanel2Color(Color.Red);
            }
        }

        // ─────────────────────────────────────────────
        // ✅ 핵심: 0050(DBPath) 확정 후 DB/UI/모듈 재로딩
        // ─────────────────────────────────────────────
        private void ApplyEnvAndReloadDb_Safe(string reason)
        {
            lock (_envReloadLock)
            {
                if (_envReloading) return;
                _envReloading = true;
            }

            try
            {
                // 1) DB 파일 존재 확인
                if (!File.Exists(DbPath))
                {
                    UpdateStatus("DB 파일이 없습니다: " + DbPath);
                    SetPanel2Color(Color.Red);
                    Console.WriteLine($"[ENV][DB] NOT FOUND: {DbPath}");
                    return;
                }

                _replayConnStr = ConnStr;

                // 2) Repo / DbFuncs 재생성
                try
                {
                    var repos = RepoBootstrap.Create(DbPath);
                    _bandRepo = repos.bands;
                    _cycleLogRepo = repos.cycles;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[ENV][RepoBootstrap] " + ex.Message);
                }

                try
                {
                    _dbFuncs = new DbFuncs(DbPath, 10);
                    try { _dbFuncs.EnsureDailyBalanceTable(); } catch { }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[ENV][DbFuncs] " + ex.Message);
                }

                // 3) DB -> listView 로드
                try
                {
                    _dbFuncs?.LoadKodexToListView(
                        listView1,
                        highlightMaxBandWithQtyNonZero: true,
                        textBoxQtySum: textBox5,
                        textBoxSinaSum: textBox6
                    );
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[ENV][LoadKodexToListView] " + ex.Message);
                }

                try
                {
                    _dbFuncs?.LoadDailyBalanceToListView(listView2);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[ENV][LoadDailyBalanceToListView] " + ex.Message);
                }

                // 4) BandList/시작밴드 재로딩
                try
                {
                    BandList = 시작밴드Read.LoadBandsAndSetStartBand();
                    SetFocusBand(Login.시작밴드변수, $"ENV({reason})->{(_0050_Real_Test환경결정.IsTest ? "TEST" : "REAL")}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[ENV][LoadBandsAndSetStartBand] " + ex.Message);
                }

                // 5) RichTextBoxBands는 ConnStr을 캡처하므로 재생성
                try
                {
                    _rtbBands = new RichTextBoxBands(richTextBox1, ConnStr);
                }
                catch { }

                // 6) Tick_fromDB 재생성(ConnStr 캡처) + 이벤트 재연결
                try
                {
                    if (_tickFromDb != null)
                    {
                        try { _tickFromDb.Stop(); } catch { }
                        try { _tickFromDb.Dispose(); } catch { }
                        _tickFromDb = null;
                    }
                }
                catch { }

                try
                {
                    _tickFromDb = new _0210_Tick_fromDB(ConnStr);
                    _tickFromDb.OnPrice += TickFromDb_OnPrice;
                    _tickFromDb.OnLog += TickFromDb_OnLog;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[ENV][_0210_Tick_fromDB] " + ex.Message);
                }

                // 7) 자료수집도 ConnStr 기반이므로 재생성
                try
                {
                    if (_자료수집 != null)
                    {
                        try { _자료수집.Dispose(); } catch { }
                        _자료수집 = null;
                    }
                }
                catch { }

                try
                {
                    _자료수집 = new 자료수집(ConnStr);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[ENV][자료수집] " + ex.Message);
                }

                // 8) 화면 상태 갱신
                try { RefreshBandsAndTriggers(); } catch { }

                UpdateStatus($"{_0050_Real_Test환경결정.LogPrefix} DB 적용완료: {DbPath}");
                SetPanel2Color(_0050_Real_Test환경결정.IsTest ? Color.LightSkyBlue : Color.LightGreen);

                Console.WriteLine($"[ENV][APPLY] reason={reason} mode={(_0050_Real_Test환경결정.IsTest ? "TEST" : "REAL")} db='{DbPath}' act='{Actno}'");
            }
            catch (Exception ex)
            {
                UpdateStatus("환경 적용 오류: " + ex.Message);
                SetPanel2Color(Color.Red);
                Console.WriteLine("[ENV][APPLY] EX: " + ex);
            }
            finally
            {
                lock (_envReloadLock) { _envReloading = false; }
            }
        }

        private void TickFromDb_OnPrice(double p)
        {
            try { HandleUiTick(p); } catch { }
            try { _ = _tickProcess.ProcessTickAsync(p); } catch { }
        }

        private void TickFromDb_OnLog(string s)
        {
            try { Debug.WriteLine(s); } catch { }
        }

        // ─────────────────────────────────────────────
        // ✅ SC1 시작(로그인 성공 직후 1회만)
        // ─────────────────────────────────────────────
        private void TryStartSc1ReceiverOnce()
        {
            try
            {
                EnsureCoreModulesInitialized();

                if (Sc1Receiver == null)
                    Sc1Receiver = new _0650_SC1_수신처리();

                if (_realSC1 == null)
                {
                    _realSC1 = new XARealClass();
                    _realSC1.LoadFromResFile(@"C:\LS_SEC\xingAPI\Res\SC1.res");
                    Console.WriteLine("[LOGIN][SC1] SC1.res loaded");
                }

                Console.WriteLine($"[LOGIN][SC1] injecting real hash={_realSC1.GetHashCode()}");
                Sc1Receiver.Start(_realSC1);
                Console.WriteLine("[LOGIN][SC1] Sc1Receiver.Start(real) called");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[LOGIN][SC1] START FAIL: " + ex);
            }
        }

        // ─────────────────────────────────────────────
        // ✅ 0650 Filled 이벤트 구독(중복 방지)
        // ─────────────────────────────────────────────
        private void SubscribeSc1FilledOnce()
        {
            try
            {
                if (Sc1Receiver == null) return;
                if (_isSc1FilledSubscribed) return;

                try { Sc1Receiver.Filled -= OnFilled_FromSc1; } catch { }
                try { Sc1Receiver.Filled += OnFilled_FromSc1; } catch { }

                _isSc1FilledSubscribed = true;
                Console.WriteLine("[LOGIN] Sc1Receiver.Filled subscribed -> OnFilled_FromSc1");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[LOGIN] SubscribeSc1FilledOnce FAIL: " + ex);
            }
        }

        private void OnFilled_FromSc1(string sideKor, int band, int deltaQty, double price, long execNo)
        {
            try
            {
                if (!IsHandleCreated) return;

                Console.WriteLine($"[LOGIN][FILLED] side={sideKor} band={band} qty={deltaQty} price={price} execNo={execNo}");

                try { SetFocusBand(시작밴드변수, "SC1_FILLED"); } catch { }

                BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        if (_lv3Manager != null)
                            await _lv3Manager.ReloadAsync();
                        else
                            await ReloadTodayOrdersAsync();

                        Console.WriteLine("[LOGIN][FILLED] listView3 reloaded");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[LOGIN][FILLED] listView3 reload FAIL: " + ex);
                    }
                }));
            }
            catch (Exception ex)
            {
                Console.WriteLine("[LOGIN][FILLED] handler EX: " + ex);
            }
        }

        private void HandleUiTick(double price)
        {
            // ✅ UI 표시용 중복틱 방지(표시만)
            if (!double.IsNaN(_lastUiTickPrice) && Math.Abs(_lastUiTickPrice - price) < double.Epsilon)
                return;

            _lastUiTickPrice = price;

            if (!IsHandleCreated) return;

            BeginInvoke(new Action(() =>
            {
                try
                {
                    int priceInt = (int)Math.Round(price);

                    textBox1.Text = priceInt.ToString(CultureInfo.InvariantCulture);
                    OnTickArrived(priceInt);

                    // ✅ 0270 호출 (StartBand 기준 팔/살)
                    if (_tickCalc2 != null)
                    {
                        int startBandNow = (this.CurrentStartBand > 0) ? this.CurrentStartBand : Login.시작밴드변수;
                        var br = Login.BandList.FirstOrDefault(b => b != null && b.Band == startBandNow);
                        if (br != null)
                        {
                            _tickCalc2.UpdateByBand(
                                currentPrice: priceInt,
                                팔가격: br.팔가격,
                                살가격: br.살가격
                            );
                        }
                    }
                }
                catch { }
            }));
        }

        private void OnTickArrived(int price)
        {
            현재가변수 = price;
            if (!_recent.Contains(price))
                _recent.Add(price);

            if (price % 10 != 0)
                return;

            var prevValues = _recent
                .Where(p => p % 10 == 0 && p != price)
                .ToArray();

            try { _rtbBands?.RenderDesc(price, prevValues); } catch { }
        }

        private void OnKodexQtyUpdated(int band, long qty)
        {
            if (!IsHandleCreated) return;

            BeginInvoke(new Action(() =>
            {
                RefreshBandListView();
                RefreshBandsAndTriggers();
            }));
        }

        public static void RefreshBandListView()
        {
            if (ListView1Ref == null) return;

            if (ListView1Ref.InvokeRequired)
            {
                ListView1Ref.BeginInvoke(new Action(() =>
                {
                    사용밴드.LoadIntoListView(ConnStr, ListView1Ref);
                }));
            }
            else
            {
                사용밴드.LoadIntoListView(ConnStr, ListView1Ref);
            }
        }

        public void RefreshBandsAndTriggers()
        {
            try
            {
                if (!File.Exists(DbPath))
                {
                    UpdateStatus("DB 파일이 없습니다: " + DbPath);
                    return;
                }

                if (_dbFuncs == null) _dbFuncs = new DbFuncs(DbPath);
                _bands = _dbFuncs.GetKodexBandsDataTable();

                Debug.WriteLine($"[BANDS] rows={_bands?.Rows.Count ?? 0}");
            }
            catch (Exception ex)
            {
                UpdateStatus("밴드 계산 오류: " + ex.Message);
            }
        }

        private async Task FetchDailyBalanceAsync(TimeSpan timeout)
        {
            await Task.Run(async () =>
            {
                try
                {
                    if (_xingConn == null || !_xingConn.IsLoggedIn)
                        throw new Exception("로그인 완료 전입니다. (0100_Xing_connect 기준)");

                    if (_bal0900 == null)
                        _bal0900 = new _0900_banance_cspaq12200_t0424(s => Debug.WriteLine("[0900] " + s));

                    var all = await _bal0900.QueryAllAsync(
                        actNo: Actno,
                        pwd: JMpass,
                        timeoutCspaq12200: timeout,
                        timeoutT0424: timeout
                    );

                    _currentCash = all.cash;
                    _currentD2Estimate = all.d2;
                    _currentHoldingQty = (int)all.qtySum;
                    _todayRealizedPnl = all.pnlSum;

                    if (_dbFuncs == null)
                        _dbFuncs = new DbFuncs(DbPath, 10);

                    _dbFuncs.UpsertDailyBalance(
                        보유량: _currentHoldingQty,
                        현금: _currentCash,
                        d2: _currentD2Estimate,
                        당일손익: _todayRealizedPnl,
                        nowLocal: DateTime.Now);

                    BeginInvoke(new Action(() =>
                    {
                        try { _dbFuncs.LoadDailyBalanceToListView(listView2); } catch { }
                        RefreshBandsAndTriggers();
                    }));
                }
                catch (Exception ex)
                {
                    BeginInvoke(new Action(() => UpdateStatus("[DailyBalance] " + ex.Message)));
                }
            });
        }

        private void UpdateStatus(string s)
        {
            try
            {
                if (statusStripLabel != null) statusStripLabel.Text = s;
                else if (statusLabel != null) statusLabel.Text = s;
                Debug.WriteLine("[STATUS] " + s);
            }
            catch { Debug.WriteLine("[STATUS] " + s); }
        }

        public void SetControlColor(Control ctrl, Color color)
        {
            if (ctrl == null) return;
            if (ctrl.BackColor == color) return;

            if (ctrl.InvokeRequired)
            {
                ctrl.BeginInvoke(new Action(() =>
                {
                    if (ctrl.IsDisposed) return;
                    if (ctrl.BackColor != color) ctrl.BackColor = color;
                }));
            }
            else
            {
                if (ctrl.BackColor != color) ctrl.BackColor = color;
            }
        }

        public void SetPanel2Color(Color color) => SetControlColor(panel2, color);

        private void OnOrderAccepted_OnLoop(OrderAck ack)
        {
            if (!IsHandleCreated) return;

            BeginInvoke(new Action(async () =>
            {
                try
                {
                    Debug.WriteLine($"[ACCEPT] {ack?.OrderNo} {ack?.Message}");
                    if (_lv3Manager != null)
                        await _lv3Manager.ReloadAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[OnOrderAccepted_OnLoop] " + ex.Message);
                }
            }));
        }

        private void OnOrderRejected_OnLoop(string msg)
        {
            if (!IsHandleCreated) return;

            BeginInvoke(new Action(() =>
            {
                try { Debug.WriteLine($"[REJECT] {msg}"); } catch { }
            }));
        }

        private void OnOrderUpdated_OnLoop(OrderRow row)
        {
            if (!IsHandleCreated || row == null) return;

            BeginInvoke(new Action(async () =>
            {
                try
                {
                    Debug.WriteLine($"[UPDATE] {row.OrderNo} {row.Status}");
                    await ReloadTodayOrdersAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[OnOrderUpdated_OnLoop] " + ex.Message);
                }
            }));
        }

        public Task ReloadTodayOrdersAsync()
        {
            return _060_listView3_당일거래.ReloadAsync(
                owner: this,
                lv: listView3,
                orderSvc: _orderSvc,
                getActNo: () => Actno,
                getPwd: () => JMpass,
                getShcode: () => currentShcode
            );
        }

        // ─────────────────────────────────────────────
        // ✅ Designer가 요구하는 버튼 핸들러(이름/시그니처 고정)
        // ─────────────────────────────────────────────
        private void button1_Click(object sender, EventArgs e)
        {
            try { new DB_Control().Show(); } catch { }
        }

        private void button2_Click(object sender, EventArgs e)
        {
            try
            {
                TradingEnabled = true;
                button2.BackColor = Color.Red;
                Debug.WriteLine("[BUTTON2] TradingEnabled=true");
            }
            catch { }
        }
        private async void button3_Click(object sender, EventArgs e)
        {
            // =========================================================
            // 1️⃣ t0424 추정순자산 조회 (button3 내부 로컬 함수)
            // =========================================================
            async Task<long> FetchSunamtAsync(TimeSpan timeout)
            {
                string resPath = @"C:\LS_SEC\xingAPI\Res\t0424.res";

                string accno = (Actno ?? "").Trim();
                string passwd = (JMpass ?? "").Trim(); // 현재 시스템 기준

                if (string.IsNullOrWhiteSpace(accno))
                    throw new Exception("계좌번호(Actno)가 비어있습니다.");
                if (string.IsNullOrWhiteSpace(passwd))
                    throw new Exception("비밀번호(JMpass)가 비어있습니다.");

                var tcs = new TaskCompletionSource<long>();
                XA_DATASETLib.XAQueryClass q = null;

                try
                {
                    q = new XA_DATASETLib.XAQueryClass();
                    q.LoadFromResFile(resPath);

                    _IXAQueryEvents_ReceiveDataEventHandler onReceiveData = null;
                    _IXAQueryEvents_ReceiveMessageEventHandler onReceiveMsg = null;

                    onReceiveData = (trCode) =>
                    {
                        try
                        {
                            string raw = (q.GetFieldData("t0424OutBlock", "sunamt", 0) ?? "").Trim();

                            if (!long.TryParse(raw, out long sunamt))
                                throw new Exception($"sunamt parse fail raw='{raw}'");

                            tcs.TrySetResult(sunamt);
                        }
                        catch (Exception ex)
                        {
                            tcs.TrySetException(ex);
                        }
                    };

                    onReceiveMsg = (bIsSystemError, nMessageCode, szMessage) =>
                    {
                        Console.WriteLine($"[t0424 MSG] sysErr={bIsSystemError} code={nMessageCode} msg={szMessage}");
                    };

                    q.ReceiveData += onReceiveData;
                    q.ReceiveMessage += onReceiveMsg;

                    // InBlock
                    q.SetFieldData("t0424InBlock", "accno", 0, accno);
                    q.SetFieldData("t0424InBlock", "passwd", 0, passwd);
                    q.SetFieldData("t0424InBlock", "prcgb", 0, "1");
                    q.SetFieldData("t0424InBlock", "chegb", 0, "0");
                    q.SetFieldData("t0424InBlock", "dangb", 0, "0");
                    q.SetFieldData("t0424InBlock", "charge", 0, "1");
                    q.SetFieldData("t0424InBlock", "cts_expcode", 0, "");

                    int r = q.Request(false);
                    if (r < 0)
                        throw new Exception($"t0424 Request fail r={r}");

                    var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
                    if (done != tcs.Task)
                        throw new TimeoutException("t0424 timeout");

                    return await tcs.Task;
                }
                finally
                {
                    try
                    {
                        if (q != null)
                            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(q);
                    }
                    catch { }
                }
            }

            // =========================================================
            // 2️⃣ 조회 실행 + MessageBox 표시
            // =========================================================
            try
            {
                long sunamt = await FetchSunamtAsync(TimeSpan.FromSeconds(10));

                MessageBox.Show(
                    this,
                    $"추정순자산(sunamt)\r\n\r\n{sunamt:N0} 원",
                    "t0424",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    "t0424 조회 실패\r\n" + ex.Message,
                    "t0424 오류",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }

            // =========================================================
            // 3️⃣ 기존 button3 Clear 동작 유지
            // =========================================================
            try
            {
                try { richTextBox1.Clear(); } catch { }

                try
                {
                    _recent.Clear();
                    _lastUiTickPrice = double.NaN;
                }
                catch { }

                Debug.WriteLine("[BUTTON3] cleared");
            }
            catch { }

            try { _tickCalc2?.Clear(); } catch { }
        }
        // 2026-02-03 81742



        private void button4_Click(object sender, EventArgs e)
        {
            try
            {
                _isRecordingTicks = true;
                button4.BackColor = Color.Red;
                Debug.WriteLine("[BUTTON4] recording ON");
            }
            catch { }
        }

        // button5: 수동 테스트(0700 직접 호출)
        private async void button5_Click(object sender, EventArgs e)
        {
            try
            {
                var up0700 = new _0700_매매후update(this);

                up0700.AfterFillUpdate(
                    band: 18,
                    deltaQty: 2,
                    price: 69150,
                    side: "매도",
                    execNo: -101
                );

                up0700.AfterFillUpdate(
                    band: 19,
                    deltaQty: 2,
                    price: 69150,
                    side: "매도",
                    execNo: -102
                );

                Debug.WriteLine("[BUTTON5][TEST] Forced 0700 AfterFillUpdate done.");

                try
                {
                    if (_lv3Manager != null) await _lv3Manager.ReloadAsync();
                    else await ReloadTodayOrdersAsync();
                }
                catch { }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[BUTTON5][TEST][ERROR] " + ex);
            }
        }

        private void button6_Click(object sender, EventArgs e)
        {
            try
            {
                _dbFuncs?.LoadKodexToListView(
                    listView1,
                    highlightMaxBandWithQtyNonZero: true,
                    textBoxQtySum: textBox5,
                    textBoxSinaSum: textBox6
                );
            }
            catch { }

            RefreshBandsAndTriggers();
        }

        // button8: DB replay 테스트 실행
        private async void button8_Click(object sender, EventArgs e)
        {
            try
            {
                try { _tickFromXing?.Stop(); } catch { }

                try { Login.BandList = 시작밴드Read.LoadAllBands(); } catch { }

                if (_tickFromDb == null)
                {
                    Debug.WriteLine("[BUTTON8] _tickFromDb is null");
                    return;
                }

                await _tickFromDb.StartReplayAsync(delayMsPerTick: 0);
                Debug.WriteLine("[BUTTON8] DB replay started");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[BUTTON8 ERROR] " + ex.Message);
            }
        }

        public void RecordTickIfEnabled(long price)
        {
            if (!_isRecordingTicks) return;
            try { _자료수집?.InsertTick(price); } catch { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { DbFuncs.KodexQtyUpdated -= OnKodexQtyUpdated; } catch { }

            try
            {
                if (Sc1Receiver != null)
                    Sc1Receiver.Filled -= OnFilled_FromSc1;
            }
            catch { }

            try { _tickFromXing?.Dispose(); } catch { }
            try { _tickFromDb?.Dispose(); } catch { }

            try { _xingConn?.Dispose(); } catch { }
            try { Sc1Receiver?.Dispose(); } catch { }

            try { _자료수집?.Dispose(); } catch { }

            base.OnFormClosed(e);
        }

        private void checkBox1_CheckedChanged(object sender, EventArgs e)
        {
            try
            {
                TradingEnabled = checkBox1.Checked;
                checkBox1.Text = checkBox1.Checked ? "거래시작" : "거래중단";
            }
            catch { }
        }

        // 콘솔 리다이렉트
        private void RedirectConsoleToFile()
        {
            // Apply 전일 수 있으므로 fallback DB를 사용해 로그 폴더 결정
            string dbPath = null;
            try
            {
                dbPath = !string.IsNullOrWhiteSpace(_0050_Real_Test환경결정.DbPath)
                    ? _0050_Real_Test환경결정.DbPath
                    : @"C:\c#\mydb.db";
            }
            catch
            {
                dbPath = @"C:\c#\mydb.db";
            }

            var baseDir = Path.GetDirectoryName(dbPath);

            if (string.IsNullOrEmpty(baseDir))
                baseDir = @"C:\c#";

            var logDir = Path.Combine(baseDir, "logs");
            Directory.CreateDirectory(logDir);

            var logPath = Path.Combine(
                logDir,
                $"app_{DateTime.Now:yyyyMMdd_HHmmss}.log"
            );

            var fs = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            var fileWriter = new StreamWriter(fs, Encoding.UTF8) { AutoFlush = true };

            var consoleWriter = Console.Out;

            var tee = new TeeTextWriter(consoleWriter, fileWriter);
            Console.SetOut(tee);
            Console.SetError(tee);

            Console.WriteLine("=== Console redirected (console + file) ===");
            Console.WriteLine($"DB(start): {dbPath}");
            Console.WriteLine($"LOG      : {logPath}");
        }

        public sealed class TeeTextWriter : TextWriter
        {
            private readonly TextWriter _console;
            private readonly TextWriter _file;

            public TeeTextWriter(TextWriter console, TextWriter file)
            {
                _console = console;
                _file = file;
            }

            public override Encoding Encoding => _console.Encoding;

            public override void Write(char value)
            {
                _console.Write(value);
                _file.Write(value);
            }

            public override void Write(string value)
            {
                _console.Write(value);
                _file.Write(value);
            }

            public override void WriteLine(string value)
            {
                _console.WriteLine(value);
                _file.WriteLine(value);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _file?.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }
}

// 2026-02-01 73918
