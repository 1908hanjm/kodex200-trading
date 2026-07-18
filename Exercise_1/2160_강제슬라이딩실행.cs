// 2160_강제슬라이딩실행.cs
// ------------------------------------------------------------
// ✅ 2026-05-17 정책 변경
// [슬라이딩 BUY 수량 계산 변경]
// - 기존: buyBudget = orderableCash + sellProceeds (예상현금 기반) → CASH NOT READY 빈번
// - 신규: SELL 완전체결 후 3초 대기 → CSPAQ12200 1회 조회 → buyQty 결정
// - 슬라이딩은 sina 절대 사용 금지 (실제 현금 기준만 사용)
//
// [CSPAQ12200 조회 정책 변경]
// - retry x3 / 반복 조회 완전 제거
// - SELL 완전체결 → 3초 대기 → CSPAQ12200 1회 → buyQty 계산 → BUY
// - TR 제한(rc=-21) 방지
//
// ✅ 2026-07-10 정책 변경 [RESTORE_ONLY_IDLE_CASH] (과매수 사고 조치)
// - 과거(growthCash 기반 extraQty 합산) 구조: fromQty+extraQty가 대수적으로
//   항상 cashAfterSell/price(=maxAffordableQty)와 같아져, 강제슬라이딩이
//   "판 수량 복원"이 아니라 "주문가능금액 전액 재투입"으로 동작함
//   → SELL보다 BUY가 반복적으로 많아지는 과매수의 직접 원인이었음
// - 신규: extraQty = 0 고정. finalBuyQty = min(fromQty, maxAffordableQty)
// - 남은 유휴현금(growthCash)은 강제슬라이딩에서 재투입하지 않고 계좌에 유지
// - (참고: 이전 주석의 "SAFETY_BUFFER 100,000 적용"은 실제로 코드에
//    구현된 적이 없던 죽은 문서였음 — 이번에 주석 정리함. 안전여유금 기능
//    자체를 신규 추가한 것은 아님)
// ------------------------------------------------------------

using System;
using System.Data.SQLite;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Exercise_1
{
    public enum SlideResult
    {
        Fail = 0,
        Success = 1,
        WaitingFill = 2,
        AbortedByNewSellSignal = 3,
        WaitingPartialProgress = 4,
        // ✅ [2026-07-06 FIX] BUY가 CASH_GUARD/BUY_BLOCK/ordNo<=0 등으로
        // 실제로 발주되지 않은 채 종료된 경우를 Success와 구분하기 위해 추가.
        // SELL은 이미 실체결된 상태이므로 Fail(단순 실패)과도 구분한다:
        // Blocked는 "SwapInProgress 등 체인 상태를 절대 clear하지 말고,
        // 자동매매를 정지한 채 사람의 개입/재확인을 기다려야 한다"는 의미다.
        Blocked = 5
    }

    public sealed class _2160_강제슬라이딩실행
    {
        private enum UnlockWaitResult
        {
            Unlocked = 0,
            TimeoutNoProgress = 1
        }

        private readonly 매매실행 _exec;
        private readonly Func<string> _getConnStr;
        private readonly Func<string> _getShcode;
        private readonly Func<_1000_현금주문가능금액> _getCash1000;
        private readonly Func<string> _getAcntNo;
        private readonly Func<string> _getPwd4;

        public _2160_강제슬라이딩실행(
            매매실행 exec,
            Func<string> getConnStr,
            Func<string> getShcode,
            Func<_1000_현금주문가능금액> getCash1000,
            Func<string> getAcntNo,
            Func<string> getPwd4)
        {
            _exec = exec ?? throw new ArgumentNullException(nameof(exec));
            _getConnStr = getConnStr ?? throw new ArgumentNullException(nameof(getConnStr));
            _getShcode = getShcode ?? throw new ArgumentNullException(nameof(getShcode));
            _getCash1000 = getCash1000 ?? throw new ArgumentNullException(nameof(getCash1000));
            _getAcntNo = getAcntNo ?? throw new ArgumentNullException(nameof(getAcntNo));
            _getPwd4 = getPwd4 ?? throw new ArgumentNullException(nameof(getPwd4));
        }

        // firePrice      : 현재 FIRE 가격
        // decisionBandK  : BUY 판단 밴드 K
        // targetBuyBand  : 실제 반영 대상 밴드
        // startBandNow   : 현재 시작밴드(로그용)
        // orderableCash  : (미사용 - 하위호환 유지용 파라미터, 슬라이딩 buyQty는 SELL 후 실제현금 기준)
        public async Task<SlideResult> ExecuteAsync(
            int firePrice,
            int decisionBandK,
            int targetBuyBand,
            int startBandNow,
            long orderableCash)
        {
            try
            {
                Write("[2160] START firePrice=" + firePrice +
                      " K=" + decisionBandK +
                      " buyBand=" + targetBuyBand +
                      " startBandNow=" + startBandNow +
                      " orderableCash=" + orderableCash);

                // ✅ [2026-07-06 FIX] 재진입 가드.
                // 이전 사이클에서 SELL은 실체결됐지만 그 짝인 BUY가 아직
                // 완료(성공/명시적 실패 확정)되지 않은 상태(SwapInProgress=true)라면,
                // 여기서 GetMinHeldBand()로 새 SELL을 또 보내면 안 된다.
                // (과거 버그: 이 가드가 없어 band28 SELL의 BUY가 막힌 채로
                //  band18에 대해 새 SELL이 또 나가 버렸음)
                if (Login.SwapInProgress)
                {
                    Write("[2160][REENTRY_BLOCK] SwapInProgress=True fromBand=" + Login.SwapFromBand +
                          " fromQty=" + Login.SwapFromQty +
                          " recordBand=" + Login.SwapRecordBandK +
                          " reason=Previous SELL completed but BUY not finalized" +
                          " action=STOP_AUTO_TRADING");
                    try { Login.AutoTradingBlocked = true; } catch { }
                    // ⚠ SwapInProgress/SwapFromBand/SwapFromQty는 절대 clear하지 않는다.
                    //   (해당 값들은 아직 미해결 상태이며, 사람이 확인 후 복구해야 한다)
                    return SlideResult.Blocked;
                }

                string shcode = SafeTrim(_getShcode());
                if (string.IsNullOrEmpty(shcode))
                {
                    Write("[2160] STOP: shcode empty");
                    return SlideResult.Fail;
                }

                int sellBand = GetMinHeldBand();
                if (sellBand <= 0)
                {
                    Write("[2160] STOP: sellBand not found");
                    return SlideResult.Fail;
                }

                int sellQty = GetBandQty(sellBand);
                if (sellQty <= 0)
                {
                    Write("[2160] STOP: sellQty<=0 band=" + sellBand);
                    return SlideResult.Fail;
                }

                Write("[2160] PLAN sellBand=" + sellBand +
                      " sellQty=" + sellQty +
                      " buyBand=" + targetBuyBand +
                      " firePrice=" + firePrice +
                      " (buyQty will be determined after SELL via CSPAQ12200)");
                Write("[POST-SLIDE][SELL] sellBand=" + sellBand +
                      " sellQty=" + sellQty +
                      " reason=NEW_LOWER_BAND");

                // ✅ [FIX] recordBand: targetBuyBand → decisionBandK
                // targetBuyBand = decisionBandK + 1 (DB qty 반영 대상)
                // 실제 BUY 주문은 decisionBandK로 나가므로
                // from_band를 기록해야 할 band도 decisionBandK가 맞다.
                SetDownSlideRuntimeFlags(
                    fromBand: sellBand,
                    fromQty: sellQty,
                    recordBand: targetBuyBand);

                Write("[2160] SELL SEND band=" + sellBand + " qty=" + sellQty + " price=" + firePrice);

                await _exec.ExecuteAsync(
                    side: "매도",
                    band: sellBand,
                    qty: sellQty,
                    price: firePrice
                ).ConfigureAwait(false);

                long sellOrdNo = GetLockedOrderNoSafe();
                UnlockWaitResult sellWait = await WaitTradeUnlockForSellAsync(
                    timeoutMs: 15000,
                    noProgressTimeoutMs: 30000).ConfigureAwait(false);
                if (IsDownSlideAborted())
                {
                    Write("[2160] SELL interrupted by new SELL signal -> BUY stage skipped");
                    ClearDownSlideRuntimeFlags();
                    return SlideResult.AbortedByNewSellSignal;
                }

                if (sellWait != UnlockWaitResult.Unlocked)
                {
                    // SwapInProgress/TradeWait 플래그는 유지한다 (ClearDownSlideRuntimeFlags 호출 안 함).
                    // BUY 단계로 절대 진행하지 않고 자동매매를 차단한다.
                    Write("[2160] SELL unlock timeout(no partial progress) -> AutoTradingBlocked=true, SwapFlags 유지, BUY 중단");
                    try { Login.AutoTradingBlocked = true; } catch { }
                    WriteStateCheck("", 0);
                    return SlideResult.Fail;   // 슬라이딩 실패 → 호출자(0300)가 SLIDE_FAIL 쿨다운 처리
                }

                Write("[2160] SELL COMPLETE");

                if (IsDownSlideAborted())
                {
                    Write("[2160] SELL unlocked after abort -> BUY stage skipped");
                    ClearDownSlideRuntimeFlags();
                    return SlideResult.AbortedByNewSellSignal;
                }

                // ✅ 새 정책: SELL 완전체결 → 3초 대기 → CSPAQ12200 1회 조회 → buyQty 계산
                Write("[2160][CASH] wait 3000ms before CSPAQ12200 query");
                await Task.Delay(3000).ConfigureAwait(false);

                long cashAfterSell = await QueryCashOnceAsync().ConfigureAwait(false);

                if (cashAfterSell <= 0)
                {
                    // ✅ [BUG-FIX] 폴백도 0이면 진짜 정보 없음 → 중단
                    Write("[2160] STOP: CSPAQ12200 returned 0 or error (폴백도 없음)");
                    ClearDownSlideRuntimeFlags();
                    return SlideResult.Fail;
                }

                // 폴백 사용 여부 로그
                long directCash = 0L;
                try
                {
                    var cash1000 = _getCash1000();
                    directCash = cash1000 != null ? cash1000.LastOrderableCash : 0L;
                }
                catch { }
                if (directCash <= 0 && cashAfterSell > 0)
                    Write("[2160][CASH] WARN: 폴백(LastKnownOrderableCash) 사용 cash=" + cashAfterSell);

                int sellFilledQty = sellQty;
                int sellOrderQty = sellQty;
                int sellRemainQty = 0;
                try
                {
                    if (sellOrdNo > 0 && Login.OrdMap != null)
                    {
                        int oq, cf, rm;
                        if (Login.OrdMap.TryGetOrderProgress(sellOrdNo, out oq, out cf, out rm))
                        {
                            sellOrderQty = oq;
                            sellFilledQty = cf;
                            sellRemainQty = rm;
                        }
                    }
                }
                catch { }

                if (sellFilledQty <= 0)
                {
                    Write("[2160] STOP: SELL filled qty missing -> BUY fund not recognized ordNo=" + sellOrdNo);
                    ClearDownSlideRuntimeFlags();
                    return SlideResult.Fail;
                }

                long sellAvgPrice = firePrice;
                long sellProceedsCash = Math.Max(0L, (long)sellFilledQty) * Math.Max(0L, sellAvgPrice);
                long growthCash = cashAfterSell - sellProceedsCash;
                if (growthCash < 0) growthCash = 0;

                // ✅ [2026-07-10 정책 변경] RESTORE_ONLY_IDLE_CASH
                // 강제슬라이딩의 목적은 "10밴드 유지 + SELL 수량 복원"이며,
                // growthCash(남은 유휴현금)를 추가 매수(extraQty)로 전환하지 않는다.
                // extraQty는 항상 0으로 고정하고, 남은 현금은 계좌에 유휴현금으로 유지한다.
                // (기존 growthCash 기반 extraQty 계산은 fromQty+extraQty가 대수적으로
                //  항상 maxAffordableQty와 같아져 "판 만큼"이 아니라 "전액매수"로
                //  동작하는 과매수 구조의 직접 원인이었음 — 제거함)
                long fromQty = Math.Max(0, sellFilledQty);
                long extraQty = 0L;
                long maxAffordableQty = CalculateMaxAffordableQty(cashAfterSell, firePrice);
                long requestedBuyQty = fromQty;
                long finalBuyQtyBeforeBandCap = Math.Min(requestedBuyQty, maxAffordableQty);

                // ✅ [배정금 한도][프로세스6] targetBuyBand(=applyBand=SwapRecordBandK)의
                // 고정 배정금을 넘지 않도록 clamp. 초과분은 매수하지 않고 계좌에
                // 현금으로 남기며, 장종료 배치에서 실현손익과 합쳐 10개 band에 재분배한다.
                var bandCapClamp = 배정금_한도체크.ClampToBandCapital(targetBuyBand, finalBuyQtyBeforeBandCap, firePrice);
                long finalBuyQty = bandCapClamp.clampedQty;
                if (bandCapClamp.leftoverCash > 0)
                {
                    Write("[2160][BAND_CAP][CLAMP] targetBand=" + targetBuyBand +
                          " qtyBeforeBandCap=" + finalBuyQtyBeforeBandCap +
                          " finalBuyQty=" + finalBuyQty +
                          " leftoverCash=" + bandCapClamp.leftoverCash);
                    배정금_한도체크.AddOverLimitLeftoverCash(bandCapClamp.leftoverCash);
                }

                int buyQty = finalBuyQty > int.MaxValue ? int.MaxValue : (int)finalBuyQty;

                Login.SwapExtraQty = extraQty;
                SaveDownSlideBuyState(targetBuyBand, sellBand, fromQty, extraQty);
                Write("[SLIDE][FUND_SPLIT] " +
                      "DecisionBandK=" + decisionBandK +
                      " TargetBuyBand=" + targetBuyBand +
                      " FromBand=" + sellBand +
                      " FromQty=" + fromQty +
                      " SellFilledQty=" + sellFilledQty +
                      " SellAvgPrice=" + sellAvgPrice +
                      " SellProceedsCash=" + sellProceedsCash +
                      " OrderableCash=" + cashAfterSell +
                      " GrowthCash=" + growthCash +
                      " BuyPrice=" + firePrice +
                      " ExtraQty=" + extraQty +
                      " RawBuyQty=" + requestedBuyQty +
                      " MaxAffordableQty=" + maxAffordableQty +
                      " FinalBuyQty=" + buyQty +
                      " SellOrdNo=" + sellOrdNo +
                      " SellOrderQty=" + sellOrderQty +
                      " SellRemainQty=" + sellRemainQty);
                Write("[SLIDE][BUY_CALC] " +
                      "DecisionBandK=" + decisionBandK +
                      " TargetBuyBand=" + targetBuyBand +
                      " Band=" + targetBuyBand +
                      " FromBand=" + sellBand +
                      " FromQty=" + fromQty +
                      " ExtraQty=" + extraQty +
                      " BuyQty=" + buyQty +
                      " OrderableCash=" + cashAfterSell +
                      " GrowthCash=" + growthCash +
                      " SellProceedsCash=" + sellProceedsCash +
                      " BuyPrice=" + firePrice +
                      " MaxAffordableQty=" + maxAffordableQty +
                      " FinalBuyQty=" + buyQty +
                      " SwapFromBand=" + Login.SwapFromBand +
                      " SwapFromQty=" + Login.SwapFromQty +
                      " SwapExtraQty=" + Login.SwapExtraQty +
                      " SwapRecordBandK=" + Login.SwapRecordBandK);
                Write("[2160][BUY_QTY_POLICY] " +
                      "SellFilledQty=" + sellFilledQty +
                      " FromQty=" + fromQty +
                      " OrderableCash=" + cashAfterSell +
                      " BuyPrice=" + firePrice +
                      " MaxAffordableQty=" + maxAffordableQty +
                      " ExtraQty=0" +
                      " FinalBuyQty=" + buyQty +
                      " Policy=RESTORE_ONLY_IDLE_CASH");

                if (buyQty <= 0)
                {
                    Write("[ROLLING][STOP] buyQty <= 0 cashAfterSell=" + cashAfterSell +
                          " requestedBuyQty=" + requestedBuyQty +
                          " maxAffordableQty=" + maxAffordableQty +
                          " firePrice=" + firePrice);
                    ClearDownSlideRuntimeFlags();
                    return SlideResult.Fail;
                }

                Write("[POST-SLIDE][BUY-CALC] orderableCash=" + cashAfterSell +
                      " targetBand=" + targetBuyBand +
                      " buyPrice=" + firePrice +
                      " fromQty=" + fromQty +
                      " extraQty=" + extraQty +
                      " requestedBuyQty=" + requestedBuyQty +
                      " maxAffordableQty=" + maxAffordableQty +
                      " buyQty=" + buyQty);

                Write("[2160] BUY SEND K=" + decisionBandK +
                      " target=" + targetBuyBand +
                      " qty=" + buyQty +
                      " price=" + firePrice);

                await _exec.ExecuteAsync(
                    side: "매수",
                    band: decisionBandK,
                    qty: buyQty,
                    price: firePrice
                ).ConfigureAwait(false);

                // ✅ [2026-07-06 FIX] BUY가 실제로 발주됐는지(ordNo 확보) 먼저 확인한다.
                // 기존 버그: CASH_GUARD/BUY_BLOCK으로 주문이 아예 안 나간 경우에도
                // TradeWait가 애초에 잠긴 적이 없어 WaitTradeUnlockAsync가 즉시 true를
                // 반환해버려서 "COMPLETE/Success"로 오판했다.
                // SELL 쪽에서 이미 쓰는 GetLockedOrderNoSafe() 패턴을 BUY에도 동일 적용.
                long buyOrdNo = GetLockedOrderNoSafe();
                bool buyLockedNow = false;
                try { buyLockedNow = Login.TradeWait != null && Login.TradeWait.IsLocked; } catch { }

                if (buyOrdNo <= 0 && !buyLockedNow)
                {
                    Write("[2160][BUY_FAILED] reason=CASH_GUARD_OR_BLOCKED band=" + targetBuyBand +
                          " buyQty=" + buyQty + " ordNo<=0 result=Blocked");
                    try { Login.AutoTradingBlocked = true; } catch { }
                    // ⚠ SwapInProgress/SwapFromBand/SwapFromQty는 절대 clear하지 않는다.
                    //   SELL(sellBand)은 이미 실체결된 상태이고, BUY만 실패했다.
                    //   재기동/사람 확인 전에는 이 상태를 유지해야 다음 재진입 가드가 작동한다.
                    return SlideResult.Blocked;
                }

                bool buyUnlocked = await WaitTradeUnlockAsync(timeoutMs: 15000).ConfigureAwait(false);
                if (!buyUnlocked)
                {
                    // BUY 주문은 이미 나간 상태이므로 SwapFlags는 유지하고 차단만 한다.
                    Write("[2160] BUY unlock timeout -> AutoTradingBlocked=true, SwapFlags 유지");
                    try { Login.AutoTradingBlocked = true; } catch { }
                    var alive = await FindAliveBuyOrderAsync(shcode).ConfigureAwait(false);
                    if (alive != null && alive.RemainQty > 0)
                    {
                        Write("[2160] BUY timeout but order alive" +
                              " ordNo=" + alive.OrderNo +
                              " remain=" + alive.RemainQty);

                        Write("[PENDING][DISABLED] Pending logic is disabled by policy.");
                        Write("[2160][POLICY] BUY unfilled qty is protected; waiting fill. ordNo=" + alive.OrderNo +
                              " remain=" + alive.RemainQty);
                        Write("[SLIDE][UNFILLED_IGNORE] " +
                              "OrderNo=" + alive.OrderNo +
                              " RemainQty=" + alive.RemainQty +
                              " Reason=NotSourceOfTruth");

                        PendingInsert(
                            side: "BUY",
                            qty: alive.RemainQty,
                            targetBand: Login.SwapRecordBandK,
                            stage: "DOWN_BUYING",
                            price: firePrice,
                            fromBand: Login.SwapFromBand);

                        RestorePendingChaser(
                            alive.OrderNo,
                            alive.RemainQty,
                            _0004_미체결추격관리.OrderSide.Buy,
                            Login.SwapRecordBandK,
                            firePrice,
                            shcode);

                        WriteStateCheck(alive.OrderNo, alive.RemainQty);
                        return SlideResult.WaitingFill;
                    }

                    Write("[2160] BUY unlock timeout and no live order -> FAIL");
                    WriteStateCheck("", 0);
                    return SlideResult.Fail;
                }

                Write("[2160] COMPLETE");
                return SlideResult.Success;
            }
            catch (Exception ex)
            {
                if (IsCspaq12200RateLimitedMessage(ex.Message))
                    Write("[ORDER][STOP] reason=Cspaq12200RateLimited caller=2160 msg=" + ex.Message);

                Write("[2160] EX: " + ex.Message);
                try { ClearDownSlideRuntimeFlags(); } catch { }
                return SlideResult.Fail;
            }
        }

        // ✅ 새 정책: CSPAQ12200 1회만 조회 (retry/반복 제거)
        // - ChainFinishedBusy(t0425 + CSPAQ12200 동시실행) 대기 후 조회하여 rc=-21 방지
        private async Task<long> QueryCashOnceAsync()
        {
            var cash1000 = _getCash1000();
            if (cash1000 == null)
            {
                Write("[2160][CASH] STOP: cash1000 is null");
                return 0L;
            }

            string acnt = SafeTrim(_getAcntNo());
            string pwd4 = SafeTrim(_getPwd4());

            if (string.IsNullOrEmpty(acnt) || string.IsNullOrEmpty(pwd4))
            {
                Write("[2160][CASH] STOP: acnt/pwd empty acntLen=" + acnt.Length + " pwdLen=" + pwd4.Length);
                return 0L;
            }

            // ✅ ChainFinishedBusy 대기: OnBandChainFinished의 t0425+CSPAQ12200과 TR 겹침 방지
            const int busyWaitMaxMs = 6000;
            const int busyPollMs = 100;
            int busyWaited = 0;

            while (Login.ChainFinishedBusy && busyWaited < busyWaitMaxMs)
            {
                if (busyWaited == 0)
                    Write("[2160][CASH] ChainFinishedBusy=true -> 대기 시작");
                await Task.Delay(busyPollMs).ConfigureAwait(false);
                busyWaited += busyPollMs;
            }

            if (Login.ChainFinishedBusy)
                Write("[2160][CASH] ChainFinishedBusy timeout(" + busyWaitMaxMs + "ms) -> 진행");
            else if (busyWaited > 0)
                Write("[2160][CASH] ChainFinishedBusy cleared after " + busyWaited + "ms -> CSPAQ12200 조회");
            else
                Write("[2160][CASH] ChainFinishedBusy=false (즉시) -> CSPAQ12200 조회");

            try
            {
                long cash = await cash1000
                    .RequestAsync(
                        acnt,
                        pwd4,
                        3000,
                        false,
                        "2160_FORCED_SLIDE_AFTER_SELL",
                        "2160_강제슬라이딩실행")
                    .ConfigureAwait(false);

                Write("[2160][CASH] CSPAQ12200 1회 조회 cash=" + cash);
                return cash;
            }
            catch (Exception ex)
            {
                // ✅ [BUG-FIX] TR 제한(-21) 등 조회 실패 시 0 반환 대신 마지막 성공값 폴백
                long fallback = 0L;
                try { fallback = Login.LastKnownOrderableCash; } catch { }

                Write("[2160][CASH][EX] " + ex.Message +
                      " -> fallback LastKnownOrderableCash=" + fallback);
                return fallback;
            }
        }

        private static bool IsCspaq12200RateLimitedMessage(string message)
        {
            message = message ?? "";
            return message.IndexOf("RateLimitCooldown", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("rc=-21", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("-21", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("전송제한", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private sealed class AliveOrderSnapshot
        {
            public string OrderNo;
            public long RemainQty;
        }

        private async Task<AliveOrderSnapshot> FindAliveBuyOrderAsync(string shcode)
        {
            try
            {
                var svc = Login.GlobalOrderSvc;
                if (svc == null)
                {
                    Write("[2160][T0425] GlobalOrderSvc null");
                    return null;
                }

                string acnt = SafeTrim(_getAcntNo());
                string pwd4 = SafeTrim(_getPwd4());
                if (string.IsNullOrEmpty(acnt) || string.IsNullOrEmpty(pwd4))
                {
                    Write("[2160][T0425] acnt/pwd empty");
                    return null;
                }

                var rows = await svc.LoadOpenOrdersAsync(acnt, pwd4, shcode).ConfigureAwait(false);
                if (rows == null || rows.Count == 0)
                {
                    Write("[2160][T0425] no rows");
                    return null;
                }

                long lockedOrdNo = 0;
                try { if (Login.TradeWait != null) lockedOrdNo = Login.TradeWait.LockedOrdNo; } catch { }
                string lockedOrdNoText = lockedOrdNo > 0 ? lockedOrdNo.ToString() : "";

                var alive = rows
                    .Where(r => r != null &&
                                r.Side == TradeSide.Buy &&
                                r.RemainQty > 0 &&
                                !string.IsNullOrWhiteSpace(r.OrderNo))
                    .OrderByDescending(r => r.Ts)
                    .ToList();

                var row = !string.IsNullOrWhiteSpace(lockedOrdNoText)
                    ? alive.FirstOrDefault(r => string.Equals((r.OrderNo ?? "").Trim(), lockedOrdNoText, StringComparison.Ordinal))
                    : null;

                if (row == null)
                    row = alive.FirstOrDefault();

                if (row == null)
                {
                    Write("[2160][T0425] no live BUY order");
                    return null;
                }

                Write("[2160][T0425] live BUY found ordNo=" + row.OrderNo +
                      " remain=" + row.RemainQty +
                      " lockedOrdNo=" + lockedOrdNoText);

                return new AliveOrderSnapshot
                {
                    OrderNo = (row.OrderNo ?? "").Trim(),
                    RemainQty = row.RemainQty
                };
            }
            catch (Exception ex)
            {
                Write("[2160][T0425][EX] " + ex.Message);
                return null;
            }
        }

        private void PendingInsert(string side, long qty, int targetBand, string stage, int price, int fromBand)
        {
            Write("[PENDING][DISABLED] Pending logic is disabled by policy.");
            return;
        }

        private void RestorePendingChaser(
            string ordNo,
            long remainQty,
            _0004_미체결추격관리.OrderSide side,
            int targetBand,
            int price,
            string shcode)
        {
            Write("[PENDING][DISABLED] Pending logic is disabled by policy.");
            return;
#pragma warning disable CS0162
            try
            {
                var chaser = Login.PendingChaser04;
                if (chaser == null)
                {
                    Write("[2160][0004] PendingChaser04 null");
                    return;
                }

                chaser.RestorePending(
                    ordNo,
                    remainQty,
                    side,
                    targetBand,
                    price,
                    shcode,
                    "DOWN_SLIDE_BUY_TIMEOUT");
            }
            catch (Exception ex)
            {
                Write("[2160][0004][EX] " + ex.Message);
            }
#pragma warning restore CS0162
        }

        private void WriteStateCheck(string ordNo, long remain)
        {
            try
            {
                bool pendingExists = false;
                try
                {
                    var chaser = Login.PendingChaser04;
                    pendingExists = chaser != null && chaser.GetSnapshot().IsActive;
                }
                catch { }

                bool tradeWait = false;
                try { tradeWait = Login.TradeWait != null && Login.TradeWait.IsLocked; } catch { }

                Write("[STATE_CHECK] " +
                      "AutoTradingBlocked=" + Login.AutoTradingBlocked +
                      " SwapInProgress=" + Login.SwapInProgress +
                      " TradeWait=" + tradeWait +
                      " PendingExists=" + pendingExists +
                      " ordNo=" + (ordNo ?? "") +
                      " remain=" + remain);
            }
            catch (Exception ex)
            {
                Write("[STATE_CHECK][EX] " + ex.Message);
            }
        }

        private int GetMinHeldBand()
        {
            using (var conn = new SQLiteConnection(_getConnStr()))
            {
                conn.Open();

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT MIN(band) " +
                        "FROM kodex200_new " +
                        "WHERE qty > 0;";

                    object o = cmd.ExecuteScalar();
                    if (o == null || o == DBNull.Value) return 0;
                    return Convert.ToInt32(o);
                }
            }
        }

        private int GetBandQty(int band)
        {
            using (var conn = new SQLiteConnection(_getConnStr()))
            {
                conn.Open();

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT qty FROM kodex200_new WHERE band=@b;";
                    cmd.Parameters.AddWithValue("@b", band);

                    object o = cmd.ExecuteScalar();
                    if (o == null || o == DBNull.Value) return 0;
                    return Convert.ToInt32(o);
                }
            }
        }

        // ✅ [2026-07-10 정책 변경] CalculateExtraQty 제거됨 (RESTORE_ONLY_IDLE_CASH)
        // 강제슬라이딩은 더 이상 growthCash로부터 extraQty를 산출하지 않는다.
        // extraQty는 항상 0으로 고정한다.

        private static long CalculateMaxAffordableQty(long orderableCash, long price)
        {
            if (orderableCash <= 0 || price <= 0) return 0L;
            return orderableCash / price;
        }

        private static long GetLockedOrderNoSafe()
        {
            try
            {
                var gate = Login.TradeWait;
                if (gate == null) return 0L;
                return gate.LockedOrdNo;
            }
            catch
            {
                return 0L;
            }
        }

        private void SaveSlideExtraQty(int band, long extraQty)
        {
            if (band <= 0) return;
            if (extraQty < 0) extraQty = 0;

            using (var conn = new SQLiteConnection(_getConnStr()))
            {
                conn.Open();
                DB_Control.EnsureExtraQtyColumn(conn);

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "UPDATE kodex200_new " +
                        "SET extra_qty = @extra " +
                        "WHERE band = @b";
                    cmd.Parameters.AddWithValue("@extra", extraQty);
                    cmd.Parameters.AddWithValue("@b", band);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private void SaveDownSlideBuyState(int band, int fromBand, long fromQty, long extraQty)
        {
            if (band <= 0) return;
            if (fromBand < 0) fromBand = 0;
            if (fromQty < 0) fromQty = 0;
            if (extraQty < 0) extraQty = 0;

            using (var conn = new SQLiteConnection(_getConnStr()))
            {
                conn.Open();
                DB_Control.EnsureExtraQtyColumn(conn);

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "UPDATE kodex200_new " +
                        "SET from_band = @fb, " +
                        "    from_qty = @fq, " +
                        "    extra_qty = @extra " +
                        "WHERE band = @b";
                    cmd.Parameters.AddWithValue("@fb", fromQty > 0 ? fromBand : 0);
                    cmd.Parameters.AddWithValue("@fq", fromQty);
                    cmd.Parameters.AddWithValue("@extra", extraQty);
                    cmd.Parameters.AddWithValue("@b", band);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private async Task<bool> WaitTradeUnlockAsync(int timeoutMs)
        {
            int waited = 0;

            while (waited < timeoutMs)
            {
                try
                {
                    if (Login.TradeWait == null) return true;
                    if (!Login.TradeWait.IsLocked) return true;
                }
                catch
                {
                    return true;
                }

                await Task.Delay(20).ConfigureAwait(false);
                waited += 20;
            }

            return false;
        }

        private async Task<UnlockWaitResult> WaitTradeUnlockForSellAsync(int timeoutMs, int noProgressTimeoutMs)
        {
            int waited = 0;
            int lastCumFill = -1;
            int lastRemain = -1;
            int lastExtendLogSecond = -1;
            DateTime lastProgressAt = DateTime.Now;
            bool sawPartial = false;

            while (true)
            {
                long lockedOrdNo = 0;
                bool locked = false;

                try
                {
                    if (Login.TradeWait == null) return UnlockWaitResult.Unlocked;

                    locked = Login.TradeWait.IsLocked;
                    lockedOrdNo = Login.TradeWait.LockedOrdNo;

                    if (!locked) return UnlockWaitResult.Unlocked;
                }
                catch
                {
                    return UnlockWaitResult.Unlocked;
                }

                int orderQty = 0;
                int cumFill = 0;
                int remain = 0;
                bool hasProgressSnapshot = false;

                try
                {
                    hasProgressSnapshot =
                        lockedOrdNo > 0 &&
                        Login.OrdMap != null &&
                        Login.OrdMap.TryGetOrderProgress(lockedOrdNo, out orderQty, out cumFill, out remain);
                }
                catch
                {
                    hasProgressSnapshot = false;
                }

                if (hasProgressSnapshot && orderQty > 0 && remain > 0 && cumFill > 0)
                {
                    bool changed = (cumFill != lastCumFill) || (remain != lastRemain);
                    if (!sawPartial || changed)
                    {
                        lastProgressAt = DateTime.Now;
                        sawPartial = true;
                        lastExtendLogSecond = -1;
                        Write("[2160][WAIT_PARTIAL_PROGRESS] SELL ordNo=" + lockedOrdNo +
                              " cumFill=" + cumFill + "/" + orderQty +
                              " remain=" + remain +
                              " -> KEEP LOCK, AutoTradingBlocked=false, BUY 대기");
                    }

                    lastCumFill = cumFill;
                    lastRemain = remain;
                }

                if (waited >= timeoutMs)
                {
                    if (sawPartial)
                    {
                        double idleMs = (DateTime.Now - lastProgressAt).TotalMilliseconds;
                        if (idleMs < noProgressTimeoutMs)
                        {
                            int idleSec = (int)(idleMs / 1000);
                            if (idleSec != lastExtendLogSecond)
                            {
                                lastExtendLogSecond = idleSec;
                                Write("[2160][WAIT_PARTIAL_PROGRESS] timeout extended ordNo=" + lockedOrdNo +
                                      " cumFill=" + lastCumFill +
                                      " remain=" + lastRemain +
                                      " idleMs=" + ((int)idleMs) +
                                      " limitMs=" + noProgressTimeoutMs);
                            }
                        }
                        else
                        {
                            Write("[2160][WAIT_PARTIAL_PROGRESS][STOP] no SC progress ordNo=" + lockedOrdNo +
                                  " cumFill=" + lastCumFill +
                                  " remain=" + lastRemain +
                                  " idleMs=" + ((int)idleMs) +
                                  " limitMs=" + noProgressTimeoutMs);
                            return UnlockWaitResult.TimeoutNoProgress;
                        }
                    }
                    else
                    {
                        Write("[2160][WAIT_PARTIAL_PROGRESS][NONE] SELL timeout with no OrdMap partial snapshot");
                        return UnlockWaitResult.TimeoutNoProgress;
                    }
                }

                await Task.Delay(100).ConfigureAwait(false);
                waited += 100;
            }
        }

        private void SetDownSlideRuntimeFlags(int fromBand, int fromQty, int recordBand)
        {
            try
            {
                Login.SwapInProgress = true;
                Login.SwapFromBand = fromBand;
                Login.SwapFromQty = fromQty;
                Login.SwapExtraQty = 0;
                Login.SwapRecordBandK = recordBand;
                Login.DownSlideAborted = false;
                Login.DownSlideAbortedByNewSellSignal = false;
                Login.DownSlideCancelRequested = false;
                Login.DownSlideCancelOrdNo = 0;

                Write("[2160][FLAGS] set SwapInProgress=TRUE fromBand=" +
                      fromBand + " fromQty=" + fromQty + " recordBand=" + recordBand);
            }
            catch (Exception ex)
            {
                Write("[2160][FLAGS][SET][EX] " + ex.Message);
            }
        }

        private void ClearDownSlideRuntimeFlags()
        {
            try
            {
                Login.SwapInProgress = false;
                Login.SwapFromBand = 0;
                Login.SwapFromQty = 0;
                Login.SwapExtraQty = 0;
                Login.SwapRecordBandK = 0;
                Login.DownSlideCancelRequested = false;
                Login.DownSlideCancelOrdNo = 0;

                Write("[2160][FLAGS] cleared");
            }
            catch (Exception ex)
            {
                Write("[2160][FLAGS][CLEAR][EX] " + ex.Message);
            }
        }

        private bool IsDownSlideAborted()
        {
            try { return Login.DownSlideAborted || Login.DownSlideAbortedByNewSellSignal; }
            catch { return false; }
        }

        private static string SafeTrim(string s)
        {
            return string.IsNullOrWhiteSpace(s) ? "" : s.Trim();
        }

        private void Write(string msg)
        {
            try
            {
                Console.WriteLine(msg);
                Debug.WriteLine(msg);
            }
            catch
            {
            }
        }
    }
}
// 2026-05-18 73620
