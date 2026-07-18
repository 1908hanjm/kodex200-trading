using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Exercise_1
{
    public partial class Login
    {
        // =========================================================
        // 0005 장종료정리 연결 필드
        // ---------------------------------------------------------
        // 설계 철학:
        // - Pending은 장중 미체결 관리용이 아니라
        //   "장 종료 시점에도 남아 있는 실제 미체결"을
        //   익일 복구용으로 저장하는 테이블이다.
        // - 따라서 이 연결 파일은 0005를 장종료 시점에만 호출하는
        //   역할을 담당한다.
        // =========================================================
        private _0005_장종료정리 _endOfDayCloser05;
        private bool _endOfDayCloserHooked = false;
        private bool _endOfDayPendingSaveRunning = false;

        // =========================================================
        // 외부에서 1회 호출:
        // - StartRealMode 끝
        // - StartTestMode 끝
        // - 또는 Login 초기화 완료 직후
        //
        // 역할:
        // - 0005 인스턴스 생성
        // - FormClosing hook 연결
        // =========================================================
        private void EnsureEndOfDayCloserInitialized()
        {
            try
            {
                if (_endOfDayCloser05 == null)
                {
                    _endOfDayCloser05 = new _0005_장종료정리(Login.ConnStr, s => Console.WriteLine(s));

                    // [중요]
                    // 실제 사용 중인 Login 인스턴스 필드와 공용 계좌정보 사용
                    _endOfDayCloser05.OrderSvc = _orderSvc;
                    _endOfDayCloser05.AccountNo = Login.Actno;
                    _endOfDayCloser05.Password = Login.JMpass;
                    _endOfDayCloser05.TargetSymbol = "069500";

                    // 한국 장 종료 기준
                    _endOfDayCloser05.EndOfDayHourKst = 15;
                    _endOfDayCloser05.EndOfDayMinuteKst = 20;
                    _endOfDayCloser05.SaveOnlyOncePerDay = true;

                    Console.WriteLine("[LOGIN][0005] _0005_장종료정리 생성 완료");
                }

                if (!_endOfDayCloserHooked)
                {
                    this.FormClosing -= Login_FormClosing_SavePending;
                    this.FormClosing += Login_FormClosing_SavePending;
                    _endOfDayCloserHooked = true;

                    Console.WriteLine("[LOGIN][0005] FormClosing hook 연결 완료");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[LOGIN][0005][INIT EX] " + ex.Message);
            }
        }

        // =========================================================
        // 폼 종료 직전 강제 저장
        // ---------------------------------------------------------
        // 중요:
        // - 프로그램 종료 시점에는 force=true로 저장 시도한다.
        // - 이것은 "지금 종료하므로 장종료 처리 성격으로 강제 캡처"하는 용도다.
        // - 실제 장중 자동 호출은 force=false로만 사용해야 한다.
        // =========================================================
        private async void Login_FormClosing_SavePending(object sender, FormClosingEventArgs e)
        {
            try
            {
                Console.WriteLine("[LOGIN][0005] FormClosing -> Pending 저장 시도(force=true)");
                await RunEndOfDayPendingSaveAsync(true);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[LOGIN][0005][FormClosing EX] " + ex.Message);
            }
        }

        // =========================================================
        // 수동/타이머/종료 직전 공용 호출 함수
        // ---------------------------------------------------------
        // force=true  : 시간 무시하고 저장 시도
        // force=false : 한국시간 15:20 이후에만 저장 시도
        //
        // 설계 철학상 사용 원칙:
        // - 장중 자동호출은 반드시 force=false
        // - force=true는 FormClosing 또는 디버그/관리자 수동테스트 전용
        // =========================================================
        public async Task RunEndOfDayPendingSaveAsync(bool force)
        {
            try
            {
                if (_endOfDayPendingSaveRunning)
                {
                    Console.WriteLine("[LOGIN][0005] 이미 저장 작업 실행 중 -> skip");
                    return;
                }

                _endOfDayPendingSaveRunning = true;

                EnsureEndOfDayCloserInitialized();

                if (_endOfDayCloser05 == null)
                {
                    Console.WriteLine("[LOGIN][0005] closer null -> skip");
                    return;
                }

                if (_endOfDayCloser05.OrderSvc == null)
                {
                    Console.WriteLine("[LOGIN][0005] OrderSvc == null -> skip");
                    return;
                }

                Console.WriteLine("[LOGIN][0005] RunEndOfDayPendingSaveAsync START force=" + force);

                var r = await _endOfDayCloser05.RunAsync(force);

                Console.WriteLine("[LOGIN][0005] result.Success=" + r.Success);
                Console.WriteLine("[LOGIN][0005] result.Skipped=" + r.Skipped);
                Console.WriteLine("[LOGIN][0005] result.NoUnfilledOrder=" + r.NoUnfilledOrder);
                Console.WriteLine("[LOGIN][0005] result.TimeNotReached=" + r.TimeNotReached);
                Console.WriteLine("[LOGIN][0005] result.AlreadySavedToday=" + r.AlreadySavedToday);
                Console.WriteLine("[LOGIN][0005] result.Message=" + (r.Message ?? ""));
                Console.WriteLine("[LOGIN][0005] result.KstNow=" + r.KstNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));

                if (r.SavedOrder != null)
                {
                    Console.WriteLine(
                        "[LOGIN][0005] saved " +
                        "ordNo=" + (r.SavedOrder.OrdNo ?? "") +
                        ", side=" + (r.SavedOrder.Side ?? "") +
                        ", stage=" + (r.SavedOrder.Stage ?? "") +
                        ", targetBand=" + r.SavedOrder.TargetBand +
                        ", fromBand=" + r.SavedOrder.FromBand +
                        ", price=" + r.SavedOrder.Price +
                        ", qty=" + r.SavedOrder.Qty);
                }

                Console.WriteLine("[LOGIN][0005] RunEndOfDayPendingSaveAsync END");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[LOGIN][0005][RUN EX] " + ex.Message);
            }
            finally
            {
                _endOfDayPendingSaveRunning = false;
            }
        }

        private async Task RunEndOfDayBandCapitalApplyAsync()
        {
            try
            {
                Console.WriteLine("[EOD][BAND_CAPITAL] START");

                if (_xingConn == null || !_xingConn.IsLoggedIn)
                {
                    Console.WriteLine("[EOD][BAND_CAPITAL] skip: not logged in");
                    return;
                }

                try
                {
                    var gate = Login.TradeWait;
                    if (gate != null && gate.IsLocked)
                    {
                        Console.WriteLine("[EOD][BAND_CAPITAL] skip: TradeWait locked");
                        return;
                    }
                }
                catch
                {
                    Console.WriteLine("[EOD][BAND_CAPITAL] skip: TradeWait state check failed");
                    return;
                }

                if (_bal0900 == null)
                    _bal0900 = new _0900_banance_cspaq12200_t0424(s => Console.WriteLine("[0900] " + s));

                string acnt = (Actno ?? "").Trim();
                string pwd = "";
                try
                {
                    if (_0050_Real_Test환경결정.IsTest)
                        pwd = (_0050_Real_Test환경결정.CertPw ?? "").Trim();
                    else
                        pwd = (JMpass ?? "").Trim();
                }
                catch
                {
                    pwd = (JMpass ?? "").Trim();
                }

                long? realizedPnl = await _bal0900.GetTodayRealizedPnlAsync(
                    acnt,
                    pwd,
                    TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                if (!realizedPnl.HasValue)
                {
                    Console.WriteLine("[EOD][BAND_CAPITAL] skip: GetTodayRealizedPnl failed");
                    return;
                }

                if (_dbFuncs == null)
                    _dbFuncs = new DbFuncs(DbPath, 10);

                bool applied = _dbFuncs.ApplyDailyRealizedPnlToBandCapital(realizedPnl.Value, 10);
                Console.WriteLine("[EOD][BAND_CAPITAL] DONE applied=" + applied +
                                  " realizedPnl=" + realizedPnl.Value.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                Console.WriteLine("[EOD][BAND_CAPITAL][EX] " + ex.Message);
            }
        }

        // =========================================================
        // 디버그/수동 테스트용 강제 저장
        // ---------------------------------------------------------
        // 주의:
        // - 이 함수는 force=true 이므로 장중에도 저장 시도를 할 수 있다.
        // - 따라서 운영 경로에서 자동 호출하면 안 된다.
        // - 디버그 버튼, 수동 테스트, 종료 직전 검증용으로만 사용한다.
        // =========================================================
        public async Task SavePendingNowForDebugAsync()
        {
            //Console.WriteLine("[LOGIN][0005][DEBUG] SavePendingNowForDebugAsync -> force=true");
            //await RunEndOfDayPendingSaveAsync(true);
        }
    }
}
// 2026-04-16 48162
