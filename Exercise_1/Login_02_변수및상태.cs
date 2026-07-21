
using Exercise_1.Domain;
using Exercise_1.Repositories;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Reflection;
using System.Windows.Forms;
using XA_DATASETLib;

namespace Exercise_1
{
    public partial class Login
    {
        // UI BOOT_SCAN thread writes; SC receiver thread reads.
        // volatile prevents a stale false read after BOOT_SCAN_DONE.
        private static volatile bool _restartRecoveryBootScanDone;

        public static BindingList<BandRange> BandList = new BindingList<BandRange>();

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
        private static bool _autoTradingBlocked = true;
        public static bool AutoTradingBlocked
        {
            get { return _autoTradingBlocked || RestartRecoveryStopLatched; }
            set
            {
                if (RestartRecoveryStopLatched && !value)
                {
                    _autoTradingBlocked = true;
                    try { Console.WriteLine("[RESTART_RECOVERY][STOP_LATCHED] auto_resume_suppressed=true"); } catch { }
                    return;
                }
                _autoTradingBlocked = value;
            }
        }
        private bool _debugBandMsgShown = false;
        public static System.Windows.Forms.ListView ListView1Ref;
        public static int 현재가변수 = 0;
        public static int 시작밴드변수;
        public static string currentShcode = "069500";

        public static string JMid = "cds002";
        public static string JMAuth = "1908hanjm!!";
        public static string Actno_Real = "00511723753";
        public static string JMpass = "1908";
        public static int 꺽임변수 = 200;
        public static string Actno => _0050_Real_Test환경결정.Account ?? "";

        public static _0550_부분체결확인 TradeWait;
        public static _0600_주문번호_매핑 OrdMap;
        public static _0650_SC1_수신처리 Sc1Receiver;
        public static _0700_매매후update AfterFillUpdate70;
        public static _0800__완전청산후 FullClearAfter80;
        public static _2160_강제슬라이딩실행 강제슬라이딩실행;

        public static bool SwapInProgress = false;
        public static int SwapFromBand = 0;
        public static int SwapFromQty = 0;
        public static long SwapExtraQty = 0;
        public static int SwapRecordBandK = 0;
        public static volatile bool DownSlideAborted = false;
        public static volatile bool DownSlideAbortedByNewSellSignal = false;
        public static volatile bool DownSlideCancelRequested = false;
        public static long DownSlideCancelOrdNo = 0;

        public static bool UpSwapInProgress = false;
        public static int UpSwapStartBand = 0;
        public static int UpSwapTargetBand = 0;
        public static int UpSwapFromBand = 0;
        public static long UpSwapFromQty = 0;
        public static long UpSwapExtraQty = 0;
        public static int FullClearSellDecisionBand = 0;

        // ✅ 2026-05-17: CHAIN_FINISHED 처리 중 플래그
        // OnBandChainFinished(t0425 + CSPAQ12200 조회)가 실행 중일 때 true
        // 2160/2100/2310이 CSPAQ12200 조회 전에 이 플래그를 확인하여 TR 충돌 방지
        public static volatile bool ChainFinishedBusy = false;

        // ✅ [BUG-FIX] CSPAQ12200 TR 제한(-21) 폴백용 캐시
        // CSPAQ12200 조회 성공 시마다 갱신한다.
        // 2160/2310이 TR 제한으로 조회 실패했을 때 0 대신 마지막 성공값을 사용하여
        // 슬라이딩이 불필요하게 중단되는 것을 방지한다.
        public static long LastKnownOrderableCash = 0L;

        public static bool SlideInProgress = false;
        public static int SlideFromBand = 0;
        public static long SlideFromQty = 0;
        public static int SlideRecordBand = 0;
        public static bool SlideRecorded = false;

        public static int SlideSellBand
        {
            get { return SlideFromBand; }
            set { SlideFromBand = value; }
        }

        public static long SlideSellQty
        {
            get { return SlideFromQty; }
            set { SlideFromQty = value; }
        }

        public static int SlideBuyBand
        {
            get { return SlideRecordBand; }
            set { SlideRecordBand = value; }
        }

        public static void ClearSlideFlags()
        {
            try
            {
                SlideInProgress = false;
                SlideFromBand = 0;
                SlideFromQty = 0;
                SlideRecordBand = 0;
            }
            catch { }
        }

        public static OrderService GlobalOrderSvc;
        public static 매매_Xing XingTrade { get; private set; }

        public static int FocusBand { get; private set; } = 0;
        public static long FocusVersion { get; private set; } = 0;
        public static string UiPlannedBandsText = "(없음)";

        public static void SetFocusBand(int newBand, string why)
        {
            if (TradeWait != null && TradeWait.IsLocked)
            {
                Console.WriteLine($"[FOCUS][SKIP] locked newBand={newBand} why={why}");
                return;
            }

            if (newBand < 0) newBand = 0;
            if (FocusBand == newBand) return;

            FocusBand = newBand;
            FocusVersion++;

            Console.WriteLine($"[FOCUS] band -> {FocusBand} (ver={FocusVersion}) why={why}");
            System.Diagnostics.Debug.WriteLine($"[FOCUS] band -> {FocusBand} (ver={FocusVersion}) why={why}");
        }

        public int CurrentStartBand { get; set; } = 0;
        public _0910_Richtextbox RTB910 { get; private set; }

        private 자료수집 _자료수집;
        private bool _isRecordingTicks = false;
        private XARealClass _realSC1;
        private readonly OrderService _orderSvc;
        private string _replayConnStr;
        private IDecisionUnit _decision;
        private int _currentHoldingQty = 0;
        private double _currentCash = 0;
        private double _currentD2Estimate = 0;
        private double _todayRealizedPnl = 0;
        private _1000_현금주문가능금액 _cashQuery;
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
        private RichTextBoxBands _rtbBands;
        private readonly List<int> _recent = new List<int>();
        public static bool TradingEnabled { get; private set; } = false;
        public static bool IsReplayMode = false;
        private _0670_listView3_당일거래 _lv3Manager;
        private long _lv3LastReloadMs = 0;
        private int _lv3ReloadInFlight = 0;
        private const int LV3_MIN_INTERVAL_MS = 1500;
        private bool _eodDoneForToday = false;

        private _0500_매매전송_Xing _tx0500;
        private _0100_Xing_connect _xingConn;
        private _0900_banance_cspaq12200_t0424 _bal0900;
        private 매매_Xing _mmXing;
        private 매매실행 _exec;
        public 매매실행 Exec => _exec;
        public _1000_현금주문가능금액 CashQueryForFullClear => _cashQuery;

        private _0210_Tick_fromDB _tickFromDb;
        private _0230_Tick_fromXing _tickFromXing;
        private _0250_Tick_Process _tickProcess;
        private double _lastUiTickPrice = double.NaN;
        private _0270_틱계산2 _tickCalc2;
        private bool _isSc1FilledSubscribed = false;
        private readonly object _envReloadLock = new object();
        private bool _envReloading = false;
        private bool _booted = false;

        private _0830_이월슬라이딩판정.Decision _carryDecision;
        private bool _carryCheckCompleted = false;
        private bool _autoTradingReady = false;

        private void EnsureCoreModulesInitialized()
        {
            if (TradeWait == null) TradeWait = new _0550_부분체결확인();
            if (OrdMap == null) OrdMap = new _0600_주문번호_매핑();
            if (Sc1Receiver == null) Sc1Receiver = new _0650_SC1_수신처리();
            if (AfterFillUpdate70 == null) AfterFillUpdate70 = new _0700_매매후update(this);
            if (FullClearAfter80 == null) FullClearAfter80 = new _0800__완전청산후(this);
        }
    }
}
//h 2026-02-01 73918
//h// ✅ REAL 기본값(기존 변수는 유지하되, 실제 사용은 0050에서만)
//hpublic static string JMid = "cds002";
//hpublic static string JMAuth = "1908hanjm!!";
//hpublic static string Actno_Real = "00511723753";

//h// ✅ 계좌비번(4자리)
//hpublic static string JMpass = "1908";

// ✅ TEST 
// LoginPw = "hanjm12";
// CertPw = "1908hanjm!!";
// Account = "55504613901";
// 2026-03-03 99401
// 2026-03-04 69010
// 2026-03-09 58271
// 2026-03-13 56405
