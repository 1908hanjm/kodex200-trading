using System;
using System.Threading;
using System.Threading.Tasks;

namespace Exercise_1
{
    public partial class Login
    {
        // =========================================================
        // ✅ 체인 종료 refresh hook 중복 연결 방지
        // =========================================================
        private int _chainFinishedHooked = 0;

        // =========================================================
        // ✅ t0424 janqty → textBox5 갱신 중복/과다 호출 방지
        // ---------------------------------------------------------
        // textBox5 : 증권사 실제 잔고수량(t0424 janqty 합계)
        // textBox8 : DB 내부 qty 합계
        // =========================================================
        private int _brokerJanQtyRefreshInFlight = 0;
        private long _brokerJanQtyLastRefreshMs = 0;
        private const int BROKER_JANQTY_REFRESH_MIN_INTERVAL_MS = 3000;
        private int _lv3ReloadFrom0800InFlight = 0;
        private int _orderableCashTextBox6RefreshInFlight = 0;
        private long _orderableCashLastRequestUtcTicks = 0;
        private long _orderableCashRateLimitUntilUtcTicks = 0;
        private const int ORDERABLE_CASH_MIN_INTERVAL_MS = 10000;
        private const int ORDERABLE_CASH_RATE_LIMIT_COOLDOWN_MS = 30000;

        // =========================================================
        // ✅ 프로그램 시작 시 1회만 호출
        // 예:
        //   EnsureChainFinishedRefreshHooked();
        // =========================================================
        private void EnsureChainFinishedRefreshHooked()
        {
            try
            {
                if (Interlocked.CompareExchange(ref _chainFinishedHooked, 1, 0) != 0)
                    return;

                WireChainFinishedRefresh();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[LOGIN][CHAIN] Ensure hook ERROR " + ex.Message);
            }
        }

        // =========================================================
        // ✅ 0300 체인 종료 이벤트 연결
        // =========================================================
        private void WireChainFinishedRefresh()
        {
            try
            {
                밴드매칭.ChainFinished -= OnBandChainFinished;
                밴드매칭.ChainFinished += OnBandChainFinished;

                // ✅ [FIX-A] UpSlide BUY qty=0 STOP 경로 refresh hook 연결
                밴드매칭.OnUpSlideBuyStopRefreshHook = () => _ = PostFillRefreshAsync("UPSLIDE_BUY_STOP");

                Console.WriteLine("[LOGIN][CHAIN] ChainFinished refresh hook wired");
                Console.WriteLine("[LOGIN][CHAIN] OnUpSlideBuyStopRefreshHook wired");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[LOGIN][CHAIN] hook wire ERROR " + ex.Message);
            }
        }

        // =========================================================
        // ✅ 체인 전체 종료 시점에만 listView3 refresh 1회
        // =========================================================
        private async void OnBandChainFinished(string reason, string side, long firePrice)
        {
            try
            {
                Console.WriteLine(
                    $"[LOGIN][CHAIN] FINISHED reason={reason} side={side} firePrice={firePrice:#,0}");
                if (밴드매칭.IsCheck1302ChainPending)
                    Console.WriteLine("[CHECK][1302][CHAIN] raised=true caller=Login_OnBandChainFinished reason=" + reason +
                                      " side=" + side);

                await PostFillRefreshAsync("CHAIN_FINISHED_" + side);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[LOGIN][CHAIN] FINISHED ERROR " + ex.Message);
            }
        }

        // =========================================================
        // ✅ [FIX-A/B/C/D] 체결 후 UI refresh 통합 진입점
        // ---------------------------------------------------------
        // 호출 경로:
        //   1. OnBandChainFinished (정상 체인 종료)
        //   2. OnUpSlideBuyStopRefreshHook (UpSlide BUY qty=0 STOP)
        //   3. 0650_SC1에서 직접 호출 가능 (향후 확장)
        //
        // 수행 순서:
        //   [START] → [LV3] → [T0424 unlock 대기 + delay] → [CASH] → [DONE]
        // =========================================================
        private async Task PostFillRefreshAsync(string trigger)
        {
            try
            {
                Console.WriteLine("[POST_FILL_REFRESH][START] trigger=" + trigger);

                // ✅ [FIX-C] listView3는 ChainFinished와 독립적으로 reload
                // ChainFinishedBusy 플래그는 유지 (2160과의 TR 충돌 방지)
                Login.ChainFinishedBusy = true;

                Console.WriteLine("[POST_FILL_REFRESH][LV3] reload START trigger=" + trigger);
                Console.WriteLine("[LV3][RELOAD_PATH] source=PostFillRefreshAsync trigger=" + trigger);
                if (밴드매칭.IsCheck1302ChainPending)
                    Console.WriteLine("[CHECK][1302][LV3] reload_enter reason=POST_FILL_REFRESH caller=" + trigger);
                await SafeReloadLv3Async("POST_FILL_" + trigger, force: true);
                Console.WriteLine("[POST_FILL_REFRESH][LV3] reload DONE trigger=" + trigger);

                // ✅ [FIX-B] t0424: TradeWait.IsLocked 해제될 때까지 최대 3초 대기 후 force refresh
                // 기존: IsLocked → 즉시 skip
                // 수정: unlock 대기(폴링) → force:true로 재시도
                Console.WriteLine("[POST_FILL_REFRESH][T0424] wait unlock START trigger=" + trigger);
                bool unlocked = await WaitForTradeWaitUnlockAsync(maxWaitMs: 3000, pollMs: 200).ConfigureAwait(false);
                Console.WriteLine("[POST_FILL_REFRESH][T0424] unlocked=" + unlocked + " trigger=" + trigger);

                // SELL 체인 종료 직후 증권사 서버 반영 지연 대기 (기존 로직 유지)
                bool isSellTrigger = trigger.IndexOf("SELL", StringComparison.OrdinalIgnoreCase) >= 0
                                  || trigger.IndexOf("CHAIN_FINISHED_SELL", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isSellTrigger)
                {
                    Console.WriteLine("[POST_FILL_REFRESH][T0424] SELL 체인 종료 -> 1000ms 대기 후 janqty 조회 trigger=" + trigger);
                    await Task.Delay(1000).ConfigureAwait(false);
                }

                await RefreshBrokerJanQtyTextBox5ForceAsync("POST_FILL_" + trigger).ConfigureAwait(false);
                Console.WriteLine("[POST_FILL_REFRESH][T0424] DONE trigger=" + trigger);

                // ✅ [FIX-D] CASH: throttle skip이면 기존값 유지, 10초 이후 재시도
                Console.WriteLine("[POST_FILL_REFRESH][CASH] START trigger=" + trigger);
                await RefreshOrderableCashTextBox6WithRetryAsync("POST_FILL_" + trigger).ConfigureAwait(false);
                Console.WriteLine("[POST_FILL_REFRESH][CASH] DONE trigger=" + trigger);

                Console.WriteLine("[POST_FILL_REFRESH][DONE] trigger=" + trigger);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[POST_FILL_REFRESH][ERR] trigger=" + trigger + " msg=" + ex.Message);
            }
            finally
            {
                Login.ChainFinishedBusy = false;
                Console.WriteLine("[LOGIN][CHAIN] ChainFinishedBusy = false trigger=" + trigger);
            }
        }

        // =========================================================
        // ✅ TradeWait unlock 폴링 대기
        // =========================================================
        private static async Task<bool> WaitForTradeWaitUnlockAsync(int maxWaitMs, int pollMs)
        {
            int elapsed = 0;
            while (elapsed < maxWaitMs)
            {
                try
                {
                    var gate = Login.TradeWait;
                    if (gate == null || !gate.IsLocked) return true;
                }
                catch { return true; }

                await Task.Delay(pollMs).ConfigureAwait(false);
                elapsed += pollMs;
            }

            // 최대 대기 후에도 locked → 그냥 진행 (force:true)
            Console.WriteLine("[POST_FILL_REFRESH][T0424] unlock wait timeout=" + maxWaitMs + "ms -> proceed anyway");
            return false;
        }

        // =========================================================
        // ✅ [FIX-B] t0424 force refresh (TradeWait unlock 이후 호출)
        // RefreshBrokerJanQtyTextBox5Async의 IsLocked guard를 우회하는 전용 경로
        // =========================================================
        private async Task RefreshBrokerJanQtyTextBox5ForceAsync(string reason)
        {
            try
            {
                // InFlight 교환. 이미 실행 중이면 skip (force라도 중복은 방지)
                if (Interlocked.CompareExchange(ref _brokerJanQtyRefreshInFlight, 1, 0) != 0)
                {
                    Console.WriteLine("[POST_FILL_REFRESH][T0424] already in-flight -> skip reason=" + reason);
                    return;
                }

                _brokerJanQtyLastRefreshMs = Environment.TickCount;

                Console.WriteLine("[POST_FILL_REFRESH][T0424] janqty refresh START reason=" + reason);

                if (_xingConn == null || !_xingConn.IsLoggedIn)
                {
                    Console.WriteLine("[POST_FILL_REFRESH][T0424] skip: not logged in reason=" + reason);
                    return;
                }

                if (_bal0900 == null)
                    _bal0900 = new _0900_banance_cspaq12200_t0424(s => Console.WriteLine("[0900] " + s));

                string acnt = (Actno ?? "").Trim();
                string pwd = "";
                try
                {
                    if (_0050_Real_Test환경결정.IsTest) pwd = (_0050_Real_Test환경결정.CertPw ?? "").Trim();
                    else pwd = (JMpass ?? "").Trim();
                }
                catch { pwd = (JMpass ?? "").Trim(); }

                var timeout = TimeSpan.FromSeconds(5);
                var t0424 = await _bal0900.QueryT0424SumAsync(acnt, pwd, timeout).ConfigureAwait(false);

                // ✅ [2026-07-16 FIX] THROTTLED로 실제 값을 확인 못한 결과(confirmed=false)는
                // textBox5/_currentHoldingQty를 덮어쓰지 않고 기존 값을 유지한다.
                // (기존 버그: 전송제한(rc=-21)으로 못 받은 값을 "실제 보유 없음(0)"으로 오판해
                //  실제로는 잔고가 있는데도 화면에 0으로 표시되는 문제)
                if (!t0424.confirmed)
                {
                    Console.WriteLine("[POST_FILL_REFRESH][T0424] throttle/unconfirmed -> keep existing textBox5/_currentHoldingQty reason=" + reason);
                    return;
                }

                _currentHoldingQty = (int)t0424.qtySum;
                _todayRealizedPnl = t0424.pnlSum;

                try
                {
                    if (IsHandleCreated && !IsDisposed)
                    {
                        BeginInvoke(new Action(() =>
                        {
                            try { if (textBox5 != null) textBox5.Text = t0424.qtySum.ToString("N0"); } catch { }
                        }));
                    }
                }
                catch { }

                Console.WriteLine("[POST_FILL_REFRESH][T0424] janqty refresh DONE reason=" + reason + " janqty=" + t0424.qtySum);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[POST_FILL_REFRESH][T0424] janqty refresh ERROR reason=" + reason + " msg=" + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _brokerJanQtyRefreshInFlight, 0);
            }
        }

        // =========================================================
        // ✅ [FIX-D] CSPAQ12200 throttle skip 시 기존값 유지 + 10초 후 재시도
        // =========================================================
        private async Task RefreshOrderableCashWithRetryOnThrottleAsync(string reason, int delayMs = 0)
        {
            if (delayMs > 0)
                await Task.Delay(delayMs).ConfigureAwait(false);

            if (_cashQuery == null)
                _cashQuery = new _1000_현금주문가능금액();

            string acnt = (Actno ?? "").Trim();
            string pwd = "";
            try
            {
                if (_0050_Real_Test환경결정.IsTest) pwd = (_0050_Real_Test환경결정.CertPw ?? "").Trim();
                else pwd = (JMpass ?? "").Trim();
            }
            catch { pwd = (JMpass ?? "").Trim(); }

            OrderableCashQueryResult result;
            if (_xingConn == null || !_xingConn.IsLoggedIn)
            {
                result = OrderableCashQueryResult.From(OrderableCashResultKind.NotLoggedIn, 0, 0, "Xing not logged in");
            }
            else
            {
                result = await _cashQuery.RequestDetailedAsync(acnt, pwd, 2500, false, reason,
                    "Login.RefreshOrderableCashWithRetryOnThrottleAsync").ConfigureAwait(false);
            }

            Console.WriteLine("[POST_FILL_REFRESH][CASH] result=" + result.Kind + " cash=" + ToOrderableCashLogValue(result) + " reason=" + reason);

            if (result.Kind == OrderableCashResultKind.RateLimited ||
                result.Kind == OrderableCashResultKind.QueryFailed)
            {
                // ✅ throttle/skip → textbox6 덮어쓰지 않음 (기존값 유지)
                Console.WriteLine("[POST_FILL_REFRESH][CASH] throttle/skip -> keep existing textbox6 value reason=" + reason);
                return;
            }

            string after = await ComputeOrderableCashTextBox6TextAsync(result, reason).ConfigureAwait(false);
            try
            {
                if (IsHandleCreated && !IsDisposed && textBox6 != null)
                {
                    BeginInvoke(new Action(() =>
                    {
                        try { textBox6.Text = after; } catch { }
                    }));
                }
            }
            catch { }

            Console.WriteLine("[CASH][UI] reason=" + reason + " result=" + result.Kind + " textbox6=" + after);
        }

        // =========================================================
        // ✅ [FIX-D] throttle skip 시 10초 후 재시도
        // =========================================================
        private async Task RefreshOrderableCashTextBox6WithRetryAsync(string reason)
        {
            if (_cashQuery == null)
                _cashQuery = new _1000_현금주문가능금액();

            string acnt = (Actno ?? "").Trim();
            string pwd = "";
            try
            {
                if (_0050_Real_Test환경결정.IsTest) pwd = (_0050_Real_Test환경결정.CertPw ?? "").Trim();
                else pwd = (JMpass ?? "").Trim();
            }
            catch { pwd = (JMpass ?? "").Trim(); }

            OrderableCashQueryResult result;
            if (_xingConn == null || !_xingConn.IsLoggedIn)
            {
                result = OrderableCashQueryResult.From(OrderableCashResultKind.NotLoggedIn, 0, 0, "Xing not logged in");
            }
            else
            {
                result = await _cashQuery.RequestDetailedAsync(acnt, pwd, 2500, false, reason,
                    "Login.RefreshOrderableCashTextBox6WithRetryAsync").ConfigureAwait(false);
            }

            Console.WriteLine("[POST_FILL_REFRESH][CASH] result=" + result.Kind + " cash=" + ToOrderableCashLogValue(result) + " reason=" + reason);

            bool isThrottleOrSkip = result.Kind == OrderableCashResultKind.RateLimited
                                 || result.Kind == OrderableCashResultKind.QueryFailed;

            if (!isThrottleOrSkip)
            {
                // 성공 → 즉시 업데이트
                string after = await ComputeOrderableCashTextBox6TextAsync(result, reason).ConfigureAwait(false);
                try
                {
                    if (IsHandleCreated && !IsDisposed && textBox6 != null)
                    {
                        BeginInvoke(new Action(() =>
                        {
                            try { textBox6.Text = after; } catch { }
                        }));
                    }
                }
                catch { }
                Console.WriteLine("[POST_FILL_REFRESH][CASH] updated textbox6=" + after + " reason=" + reason);
                return;
            }

            // ✅ throttle/skip → 기존값 유지 + 10초 후 재시도 1회
            Console.WriteLine("[POST_FILL_REFRESH][CASH] throttle/skip -> keep textbox6, retry after 10s reason=" + reason);
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(10000).ConfigureAwait(false);
                    Console.WriteLine("[POST_FILL_REFRESH][CASH] retry START reason=" + reason + "_RETRY");
                    await RefreshOrderableCashWithRetryOnThrottleAsync(reason + "_RETRY").ConfigureAwait(false);
                    Console.WriteLine("[POST_FILL_REFRESH][CASH] retry DONE reason=" + reason + "_RETRY");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[POST_FILL_REFRESH][CASH] retry EX reason=" + reason + " msg=" + ex.Message);
                }
            });
        }

        public void RequestOrderableCashTextBox6Refresh(string reason, int delayMs = 0)
        {
            try
            {
                _ = RefreshOrderableCashTextBox6Async(reason, delayMs);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[CASH][UI][REQUEST][EX] reason=" + reason + " msg=" + ex.Message);
            }
        }

        public async Task RefreshOrderableCashTextBox6Async(string reason, int delayMs = 0)
        {
            reason = string.IsNullOrWhiteSpace(reason) ? "UNKNOWN" : reason.Trim();

            if (delayMs > 0)
                await Task.Delay(delayMs).ConfigureAwait(false);

            bool isManual = IsManualOrderableCashRefresh(reason);

            if (Interlocked.Exchange(ref _orderableCashTextBox6RefreshInFlight, 1) == 1)
            {
                Console.WriteLine("[CASH][SKIP]");
                Console.WriteLine("reason=AlreadyRunning");
                Console.WriteLine("requestReason=" + reason);
                Console.WriteLine("[CSPAQ12200][GATE][SKIP]");
                Console.WriteLine("reason=" + reason);
                Console.WriteLine("caller=Login.RefreshOrderableCashTextBox6Async");
                Console.WriteLine("skipReason=AlreadyRunning");
                return;
            }

            try
            {
                if (_cashQuery == null)
                    _cashQuery = new _1000_현금주문가능금액();

                string before = "";
                try
                {
                    if (textBox6 != null)
                    {
                        if (textBox6.InvokeRequired)
                            before = (string)textBox6.Invoke(new Func<string>(() => textBox6.Text));
                        else
                            before = textBox6.Text;
                    }
                }
                catch { before = ""; }

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

                OrderableCashQueryResult result;
                if (_xingConn == null || !_xingConn.IsLoggedIn)
                {
                    result = OrderableCashQueryResult.From(
                        OrderableCashResultKind.NotLoggedIn,
                        0,
                        0,
                        "Xing not logged in");
                }
                else
                {
                    result = await _cashQuery.RequestDetailedAsync(
                        acnt,
                        pwd,
                        2500,
                        false,
                        reason,
                        "Login.RefreshOrderableCashTextBox6Async"
                    ).ConfigureAwait(false);
                }

                string after = await ComputeOrderableCashTextBox6TextAsync(result, reason).ConfigureAwait(false);

                // ✅ [FIX-D] throttle/RateLimited 결과는 textbox6에 덮어쓰지 않음 (기존값 유지)
                bool isThrottleOrSkip = result.Kind == OrderableCashResultKind.RateLimited
                                     || result.Kind == OrderableCashResultKind.QueryFailed;
                if (!isThrottleOrSkip)
                {
                    try
                    {
                        if (IsHandleCreated && !IsDisposed && textBox6 != null)
                        {
                            BeginInvoke(new Action(() =>
                            {
                                try { textBox6.Text = after; } catch { }
                            }));
                        }
                    }
                    catch { }
                }
                else
                {
                    Console.WriteLine("[CASH][UI] throttle/skip -> keep existing textbox6 reason=" + reason + " result=" + result.Kind);
                }

                Console.WriteLine("[CASH][UI]");
                Console.WriteLine("reason=" + reason);
                Console.WriteLine("before=" + before);
                Console.WriteLine("after=" + after);
                Console.WriteLine("result=" + result.Kind);
                Console.WriteLine("orderableCash=" + ToOrderableCashLogValue(result));
                Console.WriteLine("rc=" + result.Rc);
                Console.WriteLine("textbox6=" + after);
                Console.WriteLine("msg=" + result.Message);
            }
            catch (Exception ex)
            {
                try
                {
                    string after = "오류";
                    if (IsHandleCreated && !IsDisposed && textBox6 != null)
                    {
                        BeginInvoke(new Action(() =>
                        {
                            try { textBox6.Text = after; } catch { }
                        }));
                    }

                    Console.WriteLine("[CASH][UI]");
                    Console.WriteLine("reason=" + reason);
                    Console.WriteLine("result=Exception");
                    Console.WriteLine("orderableCash=N/A");
                    Console.WriteLine("textbox6=" + after);
                    Console.WriteLine("msg=" + ex.Message);
                }
                catch { }
            }
            finally
            {
                Interlocked.Exchange(ref _orderableCashTextBox6RefreshInFlight, 0);
            }
        }

        private static bool IsManualOrderableCashRefresh(string reason)
        {
            return string.Equals((reason ?? "").Trim(), "BUTTON7_MANUAL", StringComparison.OrdinalIgnoreCase);
        }

        private bool TrySkipOrderableCashByRateLimit(string reason)
        {
            long untilTicks = Interlocked.Read(ref _orderableCashRateLimitUntilUtcTicks);
            if (untilTicks <= DateTime.UtcNow.Ticks)
                return false;

            SetTextBox6Safe("TR제한");
            Console.WriteLine("[CASH][SKIP]");
            Console.WriteLine("reason=RateLimitCooldown");
            Console.WriteLine("requestReason=" + (reason ?? ""));
            Console.WriteLine("until=" + new DateTime(untilTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"));
            Console.WriteLine("textbox6=TR제한");
            return true;
        }

        private bool TrySkipOrderableCashByThrottle(string reason)
        {
            long lastTicks = Interlocked.Read(ref _orderableCashLastRequestUtcTicks);
            if (lastTicks <= 0)
                return false;

            long agoMs = (long)(DateTime.UtcNow - new DateTime(lastTicks, DateTimeKind.Utc)).TotalMilliseconds;
            if (agoMs >= ORDERABLE_CASH_MIN_INTERVAL_MS)
                return false;

            Console.WriteLine("[CASH][SKIP]");
            Console.WriteLine("reason=Throttle");
            Console.WriteLine("requestReason=" + (reason ?? ""));
            Console.WriteLine("lastRequestAgoMs=" + agoMs);
            Console.WriteLine("minIntervalMs=" + ORDERABLE_CASH_MIN_INTERVAL_MS);
            return true;
        }

        private void MarkOrderableCashRateLimited(int rc)
        {
            long untilTicks = DateTime.UtcNow.AddMilliseconds(ORDERABLE_CASH_RATE_LIMIT_COOLDOWN_MS).Ticks;
            Interlocked.Exchange(ref _orderableCashRateLimitUntilUtcTicks, untilTicks);

            Console.WriteLine("[CASH][RATE_LIMIT]");
            Console.WriteLine("rc=" + rc);
            Console.WriteLine("cooldownMs=" + ORDERABLE_CASH_RATE_LIMIT_COOLDOWN_MS);
            Console.WriteLine("until=" + new DateTime(untilTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"));
        }

        private void SetTextBox6Safe(string value)
        {
            try
            {
                if (IsHandleCreated && !IsDisposed && textBox6 != null)
                {
                    BeginInvoke(new Action(() =>
                    {
                        try { textBox6.Text = value ?? ""; } catch { }
                    }));
                }
            }
            catch { }
        }

        private static string ToOrderableCashLogValue(OrderableCashQueryResult result)
        {
            if (result == null)
                return "N/A";

            if (result.Kind == OrderableCashResultKind.RateLimited ||
                result.Kind == OrderableCashResultKind.QueryFailed ||
                result.Kind == OrderableCashResultKind.Timeout ||
                result.Kind == OrderableCashResultKind.NotLoggedIn ||
                result.Kind == OrderableCashResultKind.ParseFailed ||
                result.Kind == OrderableCashResultKind.Exception)
                return "N/A";

            return result.OrderableCash.ToString();
        }

        private static string ToOrderableCashTextBox6Text(OrderableCashQueryResult result)
        {
            if (result == null)
                return "오류";

            switch (result.Kind)
            {
                case OrderableCashResultKind.Success:
                case OrderableCashResultKind.ActualZeroCash:
                    return result.OrderableCash.ToString("N0");
                case OrderableCashResultKind.RateLimited:
                    return "TR제한";
                case OrderableCashResultKind.QueryFailed:
                    return "조회실패";
                case OrderableCashResultKind.Timeout:
                    return "시간초과";
                case OrderableCashResultKind.NotLoggedIn:
                    return "로그인안됨";
                case OrderableCashResultKind.ParseFailed:
                    return "파싱실패";
                case OrderableCashResultKind.Exception:
                    return "오류";
                default:
                    return "오류";
            }
        }

        // =========================================================
        // ✅ [2026-07-14 추가] textBox6("주문가능현금") 표시값 계산
        // ---------------------------------------------------------
        // 기존: Xing CSPAQ12200의 계좌 전체 현금(raw)을 그대로 표시
        //       → 밴드 한도(예: 100원)와 무관한 계좌 전체 금액(예: 3억5천만원)이
        //         보여서 "매수 로직이 잘못됐다"는 오해를 유발함
        // 변경: 총배정금합계(배정금 테이블 SUM, 자동 반영) - Xing t0424 매입금액합계(mamt)
        //       → 밴드 배정 구조 기준으로 실제 남은 매수 여력을 표시
        //
        // result(CSPAQ12200 조회 결과)는 로그인/TR 오류 상태 판단에만 사용하고,
        // Success/ActualZeroCash일 때만 새 계산식으로 값을 구한다.
        // =========================================================
        private async Task<string> ComputeOrderableCashTextBox6TextAsync(OrderableCashQueryResult result, string reason)
        {
            if (result == null)
                return "오류";

            switch (result.Kind)
            {
                case OrderableCashResultKind.Success:
                case OrderableCashResultKind.ActualZeroCash:
                    return await ComputeBandCapitalRemainDisplayAsync(reason).ConfigureAwait(false);
                case OrderableCashResultKind.RateLimited:
                    return "TR제한";
                case OrderableCashResultKind.QueryFailed:
                    return "조회실패";
                case OrderableCashResultKind.Timeout:
                    return "시간초과";
                case OrderableCashResultKind.NotLoggedIn:
                    return "로그인안됨";
                case OrderableCashResultKind.ParseFailed:
                    return "파싱실패";
                case OrderableCashResultKind.Exception:
                    return "오류";
                default:
                    return "오류";
            }
        }

        // 총배정금합계 - Xing t0424 매입금액(mamt) 합계 = 실제 남은 매수 여력.
        // 계산에 우리 DB의 qty×진짜산가격을 쓰지 않고, Xing이 알려주는
        // 매입금액(mamt)을 그대로 사용한다(사용자 요청: "Xing에서 읽어오는 걸로 구현").
        private async Task<string> ComputeBandCapitalRemainDisplayAsync(string reason)
        {
            try
            {
                double totalLimit = 배정금_한도체크.GetTotalBandCapitalLimit();

                if (_bal0900 == null)
                    _bal0900 = new _0900_banance_cspaq12200_t0424(s => Console.WriteLine("[0900] " + s));

                string acnt = (Actno ?? "").Trim();
                string pwd = "";
                try
                {
                    if (_0050_Real_Test환경결정.IsTest) pwd = (_0050_Real_Test환경결정.CertPw ?? "").Trim();
                    else pwd = (JMpass ?? "").Trim();
                }
                catch { pwd = (JMpass ?? "").Trim(); }

                var t0424 = await _bal0900.QueryT0424SumAsync(acnt, pwd, TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                // ✅ [2026-07-16 FIX] THROTTLED로 mamtSum을 확인 못한 경우, 0으로 계산하면
                // remain이 totalLimit 전체로 잘못 부풀려 표시된다. 확인 실패 시에는
                // "조회지연"으로 표시해 잘못된 숫자를 보여주지 않는다.
                if (!t0424.confirmed)
                {
                    Console.WriteLine("[BAND_CAP][TEXTBOX6] reason=" + reason + " throttle/unconfirmed -> 조회지연 표시");
                    return "조회지연";
                }

                double remain = totalLimit - t0424.mamtSum;
                if (remain < 0) remain = 0;

                Console.WriteLine("[BAND_CAP][TEXTBOX6] reason=" + reason +
                                  " totalLimit=" + totalLimit.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                                  " mamtSum=" + t0424.mamtSum.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                                  " remain=" + remain.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));

                return remain.ToString("N0");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[BAND_CAP][TEXTBOX6][EX] reason=" + reason + " msg=" + ex.Message);
                return "오류";
            }
        }

        public async Task SafeReloadLv3Async(string reason, bool force = false)
        {
            bool ownsReloadGate = false;
            try
            {
                if (!force)
                {
                    long now = Environment.TickCount;
                    if (Interlocked.CompareExchange(ref _lv3ReloadInFlight, 1, 0) != 0)
                    {
                        Console.WriteLine("[0800][LV3][SKIP] alreadyReloaded=True reason=" + reason);
                        return;
                    }
                    ownsReloadGate = true;

                    long last = _lv3LastReloadMs;
                    if (now - last < LV3_MIN_INTERVAL_MS)
                    {
                        Interlocked.Exchange(ref _lv3ReloadInFlight, 0);
                        ownsReloadGate = false;
                        Console.WriteLine("[0800][LV3][SKIP] alreadyReloaded=True reason=" + reason);
                        return;
                    }

                    _lv3LastReloadMs = now;
                }
                else
                {
                    Interlocked.Exchange(ref _lv3ReloadInFlight, 1);
                    ownsReloadGate = true;
                    _lv3LastReloadMs = Environment.TickCount;
                }

                Console.WriteLine($"[LOGIN][LV3] reload START reason={reason} force={force}");
                if (밴드매칭.IsCheck1302ChainPending)
                    Console.WriteLine("[CHECK][1302][LV3] reload_enter reason=" + reason +
                                      " force=" + force);

                if (_lv3Manager != null) await _lv3Manager.ReloadAsync();
                else await ReloadTodayOrdersAsync();

                Console.WriteLine($"[LOGIN][LV3] reload DONE reason={reason} force={force}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LOGIN][LV3] reload ERROR reason={reason} msg={ex.Message}");
            }
            finally
            {
                if (ownsReloadGate)
                    Interlocked.Exchange(ref _lv3ReloadInFlight, 0);
            }
        }

        public void RequestListView3ReloadFrom0800(string reason)
        {
            try
            {
                string reloadReason = string.IsNullOrWhiteSpace(reason) ? "FULL_CLEAR_REBUILD_FILL" : reason;

                if (Interlocked.CompareExchange(ref _lv3ReloadFrom0800InFlight, 1, 0) != 0)
                {
                    Console.WriteLine("[0800][LV3][SKIP] alreadyReloaded=True reason=" + reloadReason);
                    return;
                }

                Action run = async () =>
                {
                    try
                    {
                        Console.WriteLine("[0800][LV3] rebuild buy fill -> reload START reason=" + reloadReason);
                        await SafeReloadLv3Async(reloadReason, force: false);
                        Console.WriteLine("[0800][LV3] rebuild buy fill -> reload DONE reason=" + reloadReason);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[LOGIN][LV3][0800][ERR] {ex.Message}");
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _lv3ReloadFrom0800InFlight, 0);
                    }
                };

                if (IsDisposed)
                {
                    Interlocked.Exchange(ref _lv3ReloadFrom0800InFlight, 0);
                    return;
                }

                if (InvokeRequired)
                    BeginInvoke(run);
                else
                    run();
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _lv3ReloadFrom0800InFlight, 0);
                Console.WriteLine($"[LOGIN][LV3][0800][ERR] {ex.Message}");
            }
        }

        // =========================================================
        // ✅ t0424 janqty 합계를 textBox5에 표시
        // ---------------------------------------------------------
        // - 프로그램 시작 후 호출
        // - 거래/체인 완료 후 호출
        // - textBox5는 이제 DB qty가 아니라 증권사 실제 잔고수량이다.
        // =========================================================
        public async Task RefreshBrokerJanQtyTextBox5Async(string reason, bool force = false)
        {
            try
            {
                if (!force)
                {
                    long now = Environment.TickCount;
                    long last = _brokerJanQtyLastRefreshMs;

                    if (now - last < BROKER_JANQTY_REFRESH_MIN_INTERVAL_MS)
                        return;

                    if (Interlocked.CompareExchange(ref _brokerJanQtyRefreshInFlight, 1, 0) != 0)
                        return;

                    _brokerJanQtyLastRefreshMs = now;
                }
                else
                {
                    if (Interlocked.CompareExchange(ref _brokerJanQtyRefreshInFlight, 1, 0) != 0)
                        return;

                    _brokerJanQtyLastRefreshMs = Environment.TickCount;
                }

                if (Login.TradeWait != null && Login.TradeWait.IsLocked)
                {
                    Console.WriteLine("[LOGIN][T0424] janqty refresh SKIP: TradeWait locked reason=" + reason);
                    return;
                }

                Console.WriteLine("[LOGIN][T0424] janqty refresh START reason=" + reason + " force=" + force);

                if (_xingConn == null || !_xingConn.IsLoggedIn)
                {
                    Console.WriteLine("[LOGIN][T0424] skip: not logged in");
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

                var timeout = TimeSpan.FromSeconds(5);
                var t0424 = await _bal0900.QueryT0424SumAsync(acnt, pwd, timeout).ConfigureAwait(false);

                // ✅ [2026-07-16 FIX] 09:10 잔고 오표시 버그의 실제 발생 지점.
                // THROTTLED(rc=-21)로 실제 값을 확인 못한 결과(confirmed=false)를
                // "실제 보유 없음(0)"으로 오판해 textBox5(t4024 잔고)에 0을 써버리던 부분.
                // confirmed=false면 기존 값을 그대로 유지하고 UI를 덮어쓰지 않는다.
                if (!t0424.confirmed)
                {
                    Console.WriteLine("[LOGIN][T0424] throttle/unconfirmed -> keep existing textBox5/_currentHoldingQty reason=" + reason);
                    return;
                }

                _currentHoldingQty = (int)t0424.qtySum;
                _todayRealizedPnl = t0424.pnlSum;

                try
                {
                    if (IsHandleCreated && !IsDisposed)
                    {
                        BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                if (textBox5 != null)
                                    textBox5.Text = t0424.qtySum.ToString("N0");
                            }
                            catch { }
                        }));
                    }
                }
                catch { }

                Console.WriteLine("[LOGIN][T0424] janqty refresh DONE reason=" + reason + " janqty=" + t0424.qtySum);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[LOGIN][T0424] janqty refresh ERROR reason=" + reason + " msg=" + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _brokerJanQtyRefreshInFlight, 0);
            }
        }

        private async Task<long> QueryOrderableCashForSlideAsync()
        {
            try
            {
                if (_xingConn == null || !_xingConn.IsLoggedIn) return 0;

                if (_bal0900 == null)
                    _bal0900 = new _0900_banance_cspaq12200_t0424(s => Console.WriteLine("[0900] " + s));

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

                var timeout = TimeSpan.FromSeconds(3);
                var all = await _bal0900.QueryAllAsync(
                    actNo: acnt,
                    pwd: pwd,
                    timeoutCspaq12200: timeout,
                    timeoutT0424: timeout).ConfigureAwait(false);

                try { _currentCash = all.cash; } catch { }
                // ✅ [BUG-FIX] 슬라이딩 TR 실패 폴백용 캐시 갱신
                try { if (all.cash > 0) Login.LastKnownOrderableCash = (long)all.cash; } catch { }

                long cash = 0;
                try { cash = (long)Math.Max(0, all.cash); } catch { cash = 0; }
                return cash;
            }
            catch
            {
                return 0;
            }
        }

        private async Task<long> QuerySellAvailQtyForSlideAsync()
        {
            try
            {
                if (_xingConn == null || !_xingConn.IsLoggedIn) return 0;

                using (var bal = new _1010_증권사보유주수())
                {
                    string account = (Actno ?? "").Trim();
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

                    string shcode = (currentShcode ?? "069500").Trim();
                    long qty = await bal.RequestAsync(account, pwd, shcode).ConfigureAwait(false);
                    return Math.Max(0, qty);
                }
            }
            catch
            {
                return 0;
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
                        _bal0900 = new _0900_banance_cspaq12200_t0424(s => Console.WriteLine("[0900] " + s));

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

                    var t0424 = await _bal0900.QueryT0424OnlyAsync(
                        actNo: Actno,
                        pwd: pwd,
                        timeout: timeout);

                    // ✅ [2026-07-16 FIX] THROTTLED로 확인 못한 값(confirmed=false)을
                    // DB 일일잔고/textBox5에 0으로 기록하지 않도록 가드.
                    if (!t0424.confirmed)
                    {
                        Console.WriteLine("[DailyBalance][T0424] throttle/unconfirmed -> skip DB/UI update");
                        BeginInvoke(new Action(() => UpdateStatus("[DailyBalance] t0424 조회 지연(전송제한) - 기존 값 유지")));
                        return;
                    }

                    _currentHoldingQty = (int)t0424.qtySum;
                    _todayRealizedPnl = t0424.pnlSum;

                    if (_dbFuncs == null) _dbFuncs = new DbFuncs(DbPath, 10);

                    _dbFuncs.UpsertDailyBalance(
                        보유량: _currentHoldingQty,
                        현금: _currentCash,
                        d2: _currentD2Estimate,
                        당일손익: _todayRealizedPnl,
                        nowLocal: DateTime.Now);

                    BeginInvoke(new Action(() =>
                    {
                        try { _dbFuncs.LoadDailyBalanceToListView(listView2); } catch { }
                        try { if (textBox5 != null) textBox5.Text = t0424.qtySum.ToString("N0"); } catch { }
                        RefreshBandsAndTriggers();
                    }));
                }
                catch (Exception ex)
                {
                    BeginInvoke(new Action(() => UpdateStatus("[DailyBalance] " + ex.Message)));
                }
            });
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
    }
}
// 2026-05-13 73941
