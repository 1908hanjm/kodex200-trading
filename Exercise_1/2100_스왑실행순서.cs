// 2100_스왑실행순서.cs  (복붙용 / C# 7.3)  [간단형: 2200 없음]
// ------------------------------------------------------------
// 역할(간단형 / 하락스왑 전용):
// - BUY 직전 현금 부족 시
//   1) 상단밴드 전량 매도 (SELL)
//   2) SC1 완전체결로 0550 잠금 해제될 때까지 대기
//   3) CSPAQ12200 1회 조회(주문가능금액)
//   4) buyQty = floor((cashAfterSell - SAFETY_BUFFER) / buyPrice)
//   5) 하단밴드(결정밴드 K) 매수 (BUY)
// - 스왑 기록 최소화 정책:
//   - kodex200_new의 from_band/from_qty는 0700(FinalizeAfterUnlock)에서 기록한다.
//   - 여기서는 Login.Swap* static에 "기록할 값"만 세팅한다.
//
// ✅ 2026-05-17 변경
// - 슬라이딩 BUY 수량: SAFETY_BUFFER(100,000) 차감 후 계산 (CASH NOT READY 방지)
// - CSPAQ12200 retry 제거 → 1회 조회
// ------------------------------------------------------------

using System;
using System.Linq;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Exercise_1
{
    public sealed class _2100_스왑실행순서
    {
        private const int SAFETY_BUFFER = 100_000;

        private readonly _0500_매매전송_Xing _tx0500;
        private readonly Func<string> _getAcntNo;
        private readonly Func<string> _getPwd4;
        private readonly Func<_1000_현금주문가능금액> _getCash1000;

        public event Action<string> Log;

        public _2100_스왑실행순서(
            _0500_매매전송_Xing tx0500,
            Func<string> getAcntNo,
            Func<string> getPwd4,
            Func<_1000_현금주문가능금액> getCash1000)
        {
            _tx0500 = tx0500 ?? throw new ArgumentNullException(nameof(tx0500));
            _getAcntNo = getAcntNo ?? throw new ArgumentNullException(nameof(getAcntNo));
            _getPwd4 = getPwd4 ?? throw new ArgumentNullException(nameof(getPwd4));
            _getCash1000 = getCash1000 ?? throw new ArgumentNullException(nameof(getCash1000));
        }

        public async Task<bool> ExecuteDownSwapAsync(int buyBandK, int buyPrice, int requestedQty, string shcode)
        {
            if (buyBandK <= 0) throw new ArgumentOutOfRangeException(nameof(buyBandK));
            if (buyPrice <= 0) throw new ArgumentOutOfRangeException(nameof(buyPrice));
            if (requestedQty <= 0) throw new ArgumentOutOfRangeException(nameof(requestedQty));
            if (string.IsNullOrWhiteSpace(shcode)) throw new ArgumentOutOfRangeException(nameof(shcode));

            var planner = new _2000_스왑Plan();

            _2000_스왑Plan.SwapPlan plan;
            string why;

            if (!planner.TryBuildDownSwapPlan(buyBandK, buyPrice, requestedQty, out plan, out why))
            {
                Write("[2100][PLAN][FAIL] " + why);
                return false;
            }

            Write("[2100][PLAN][OK] sellBand=" + plan.SellBand + " sellQty=" + plan.SellQty +
                  " buyBandK=" + plan.BuyBandK + " buyPrice=" + plan.BuyPrice + " reqQty=" + plan.RequestedQty);

            // 1) SELL
            if (Login.TradeWait == null)
            {
                Write("[2100][FAIL] Login.TradeWait(0550) is null");
                return false;
            }

            if (Login.TradeWait.IsLocked)
            {
                Write("[2100][FAIL] TradeWait is locked already (side=" + Login.TradeWait.LockedSide +
                      " band=" + Login.TradeWait.LockedBand + " ordNo=" + Login.TradeWait.LockedOrdNo + ")");
                return false;
            }

            try
            {
                Write("[2100][SELL][SEND] band=" + plan.SellBand + " qty=" + plan.SellQty + " price(cur)=" + buyPrice);
                await _tx0500.SendOrderAsync("매도", shcode, buyPrice, plan.SellQty, plan.SellBand).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Write("[2100][SELL][EX] " + ex.Message);
                return false;
            }

            // 2) SELL 완전체결 대기(0550 unlock)
            bool sellDone = await WaitUnlockAsync(timeoutMs: 30000, tag: "SELL").ConfigureAwait(false);
            if (!sellDone)
            {
                Write("[2100][SELL][TIMEOUT] unlock timeout");
                return false;
            }

            // 3) CSPAQ12200 재조회
            var cash1000 = _getCash1000();
            if (cash1000 == null)
            {
                Write("[2100][CASH][FAIL] cash1000 is null");
                return false;
            }

            string acnt = SafeTrim(_getAcntNo());
            string pwd4 = SafeTrim(_getPwd4());

            if (string.IsNullOrEmpty(acnt) || string.IsNullOrEmpty(pwd4))
            {
                Write("[2100][CASH][FAIL] acnt/pwd empty acntLen=" + acnt.Length + " pwdLen=" + pwd4.Length);
                return false;
            }

            long orderable;
            try
            {
                Write("[2100][CASH][CALL] after SELL, buyBandK=" + plan.BuyBandK + " buyPrice=" + plan.BuyPrice);
                orderable = await cash1000.RequestAsync(
                    acnt,
                    pwd4,
                    2000,
                    false,
                    "2100_SWAP_AFTER_SELL",
                    "2100_스왑실행순서").ConfigureAwait(false);
                Write("[2100][CASH][RET] orderable=" + orderable);
            }
            catch (Exception ex)
            {
                if (IsCspaq12200RateLimitedMessage(ex.Message))
                    Write("[ORDER][STOP] reason=Cspaq12200RateLimited caller=2100_SWAP msg=" + ex.Message);
                Write("[2100][CASH][EX] " + ex.Message);
                return false;
            }

            // 4) buyQty 확정 (SAFETY_BUFFER 차감 후 계산)
            int buyQtyFinal = 0;
            try
            {
                long safeBudget = Math.Max(0L, orderable - SAFETY_BUFFER);
                buyQtyFinal = (int)Math.Floor((double)safeBudget / (double)plan.BuyPrice);
                Write("[2100][CASH] orderable=" + orderable + " safeBudget=" + safeBudget + " SAFETY_BUFFER=" + SAFETY_BUFFER);
            }
            catch { buyQtyFinal = 0; }

            if (buyQtyFinal <= 0)
            {
                Write("[2100][BUY][BLOCK] buyQtyFinal<=0 (orderable=" + orderable + ", buyPrice=" + plan.BuyPrice + ")");
                return false;
            }

            // 5) 기록값 세팅 (0700이 DB에 기록)
            Login.SwapInProgress = true;
            Login.SwapFromBand = plan.SellBand;
            Login.SwapFromQty = plan.SellQty;
            Login.SwapExtraQty = 0;
            Login.SwapRecordBandK = plan.BuyBandK;

            Write("[2100][SWAP][SET] SwapInProgress=1 fromBand=" + Login.SwapFromBand +
                  " fromQty=" + Login.SwapFromQty + " recordBandK=" + Login.SwapRecordBandK +
                  " buyQtyFinal=" + buyQtyFinal);

            // 6) BUY
            try
            {
                Write("[2100][BUY][SEND] bandK=" + plan.BuyBandK + " qty=" + buyQtyFinal + " price(cur)=" + plan.BuyPrice);
                long buyOrdNo = await _tx0500.SendOrderAsync("매수", shcode, plan.BuyPrice, buyQtyFinal, plan.BuyBandK).ConfigureAwait(false);
                if (buyOrdNo <= 0)
                {
                    Write("[2100][BUY][SAFE_BLOCK] 0500 cash guard blocked order");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Write("[2100][BUY][EX] " + ex.Message);
                return false;
            }

            return true;
        }

        
        // ------------------------------------------------------------
        // ✅ 상승스왑(Up-Swap) 실행 (부분복구 허용 / 스키마=0 정책)
        // - startBand의 from_band/from_qty(>0) 링크를 1단계 복구한다.
        // - 절차: startBand SELL 전량 -> UNLOCK 대기 -> CSPAQ12200 재조회 -> target(from_band) BUY
        // - BUY 수량: min(from_qty, floor(orderableCash / curPrice))
        // - BUY 체결 처리:
        //   0700.ApplyFillOnly에서 Login.UpSwapInProgress && side=매수 && band==targetBand이면
        //   (1) Qty는 targetBand 자체에 반영
        //   (2) targetBand.sina = targetBand.qty 동기화
        //   (3) startBand.from_qty를 체결분만큼 감소 (remain>0 링크 유지, remain==0이면 0/0으로 초기화)
        // ------------------------------------------------------------
        public async Task<bool> ExecuteUpSwapAsync(int startBand, int curPrice, string shcode)
        {
            if (startBand <= 0) throw new ArgumentOutOfRangeException(nameof(startBand));
            if (curPrice <= 0) throw new ArgumentOutOfRangeException(nameof(curPrice));
            if (string.IsNullOrWhiteSpace(shcode)) throw new ArgumentOutOfRangeException(nameof(shcode));

            // 0) startBand 최신 from_link는 DB에서 읽는다 (BandList는 down-swap 기록과 비동기일 수 있음)
            BandRange startRow;
            try
            {
                startRow = 사용밴드.ReadOne(Login.ConnStr, startBand);
            }
            catch (Exception ex)
            {
                Write("[2100][UP][DB][EX] " + ex.Message);
                return false;
            }

            if (startRow == null)
            {
                Write("[2100][UP][DB][FAIL] startRow null band=" + startBand);
                return false;
            }

            int targetBand = startRow.From_Band;
            long fromQty = Math.Max(0L, startRow.From_Qty);
            long oldExtraQty = Math.Max(0L, startRow.Extra_Qty);

            if (targetBand <= 0 || fromQty <= 0)
            {
                Write("[2100][UP][SKIP] from_link empty (from_band=" + targetBand +
                      ", from_qty=" + fromQty +
                      ", old_extra_qty=" + oldExtraQty + ")");
                return false;
            }

            // 1) SELL 전량(현재 startBand 보유수량)
            var k = Login.BandList.FirstOrDefault(x => x.Band == startBand);
            int sellQty = 0;
            try { sellQty = (k == null) ? 0 : (int)Math.Max(0, k.Qty); } catch { sellQty = 0; }

            if (sellQty <= 0)
            {
                Write("[2100][UP][BLOCK] sellQty<=0 startBand=" + startBand);
                return false;
            }

            if (Login.TradeWait == null)
            {
                Write("[2100][UP][FAIL] Login.TradeWait(0550) is null");
                return false;
            }

            if (Login.TradeWait.IsLocked)
            {
                Write("[2100][UP][FAIL] TradeWait is locked already (side=" + Login.TradeWait.LockedSide +
                      " band=" + Login.TradeWait.LockedBand + " ordNo=" + Login.TradeWait.LockedOrdNo + ")");
                return false;
            }

            try
            {
                Write("[2100][UP][SELL][SEND] startBand=" + startBand + " qty=" + sellQty + " price(cur)=" + curPrice +
                      " link-> from_band=" + targetBand + " from_qty=" + fromQty + " old_extra_qty=" + oldExtraQty);
                await _tx0500.SendOrderAsync("매도", shcode, curPrice, sellQty, startBand).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Write("[2100][UP][SELL][EX] " + ex.Message);
                return false;
            }

            // 2) SELL 완전체결 대기(0550 unlock)
            bool sellDone = await WaitUnlockAsync(timeoutMs: 30000, tag: "UP.SELL").ConfigureAwait(false);
            if (!sellDone)
            {
                Write("[2100][UP][SELL][TIMEOUT] unlock timeout");
                return false;
            }

            // 3) CSPAQ12200 재조회
            var cash1000 = _getCash1000();
            if (cash1000 == null)
            {
                Write("[2100][UP][CASH][FAIL] cash1000 is null");
                return false;
            }

            string acnt = SafeTrim(_getAcntNo());
            string pwd4 = SafeTrim(_getPwd4());

            if (string.IsNullOrEmpty(acnt) || string.IsNullOrEmpty(pwd4))
            {
                Write("[2100][UP][CASH][FAIL] acnt/pwd empty acntLen=" + acnt.Length + " pwdLen=" + pwd4.Length);
                return false;
            }

            long orderable;
            try
            {
                Write("[2100][UP][CASH][CALL] after SELL, targetBand=" + targetBand + " curPrice=" + curPrice);
                orderable = await cash1000.RequestAsync(
                    acnt,
                    pwd4,
                    2000,
                    false,
                    "2100_UP_SWAP_AFTER_SELL",
                    "2100_스왑실행순서").ConfigureAwait(false);
                Write("[2100][UP][CASH][RET] orderable=" + orderable);
            }
            catch (Exception ex)
            {
                if (IsCspaq12200RateLimitedMessage(ex.Message))
                    Write("[ORDER][STOP] reason=Cspaq12200RateLimited caller=2100_UP_SWAP msg=" + ex.Message);
                Write("[2100][UP][CASH][EX] " + ex.Message);
                return false;
            }

            // 4) Up-Swap BUY qty = from_qty + new extra_qty from current orderable cash.
            // Old DB extra_qty is historical growth and must not be reused.
            long newExtraQty = 0;
            long wantQty = 0;
            try
            {
                newExtraQty = curPrice > 0 ? orderable / curPrice : 0L;
                if (newExtraQty < 0) newExtraQty = 0;
                wantQty = fromQty + newExtraQty;
                Write("[SLIDE][BUY_CALC] " +
                      "TargetBand=" + targetBand +
                      " FromBand=" + startBand +
                      " FromQty=" + fromQty +
                      " OldExtraQty=" + oldExtraQty +
                      " OrderableCash=" + orderable +
                      " BuyPrice=" + curPrice +
                      " NewExtraQty=" + newExtraQty +
                      " BuyQty=" + wantQty);
            }
            catch
            {
                newExtraQty = 0;
                wantQty = fromQty;
            }

            int buyQtyFinal = wantQty > int.MaxValue ? int.MaxValue : (int)wantQty;
            if (buyQtyFinal <= 0)
            {
                Write("[2100][UP][BUY][BLOCK] buyQtyFinal<=0 (orderable=" + orderable + ", curPrice=" + curPrice + ", wantQty=" + wantQty + ")");
                return false;
            }

            // 5) 런타임 플래그 세팅 (0700이 체결분 즉시반영 + from_qty 감소)
            Login.UpSwapInProgress = true;
            Login.UpSwapStartBand = startBand;
            Login.UpSwapTargetBand = targetBand;
            Login.UpSwapFromBand = startBand;
            Login.UpSwapFromQty = fromQty;
            Login.UpSwapExtraQty = newExtraQty;

            Write("[2100][UP][SET] UpSwapInProgress=1 startBand=" + startBand + " targetBand=" + targetBand +
                  " fromQty=" + fromQty +
                  " oldExtraQty=" + oldExtraQty +
                  " newExtraQty=" + newExtraQty +
                  " buyQtyFinal=" + buyQtyFinal +
                  " wantQty=" + wantQty);

            // 6) BUY
            try
            {
                Write("[2100][UP][BUY][SEND] targetBand=" + targetBand +
                      " fromBand=" + startBand +
                      " fromQty=" + fromQty +
                      " oldExtraQty=" + oldExtraQty +
                      " orderableCash=" + orderable +
                      " buyPrice=" + curPrice +
                      " newExtraQty=" + newExtraQty +
                      " buyQty=" + buyQtyFinal);
                long buyOrdNo = await _tx0500.SendOrderAsync("매수", shcode, curPrice, buyQtyFinal, targetBand).ConfigureAwait(false);
                if (buyOrdNo <= 0)
                {
                    Write("[2100][UP][BUY][SAFE_BLOCK] 0500 cash guard blocked order");
                    ClearUpSwapRuntimeFlags();
                    return false;
                }
            }
            catch (Exception ex)
            {
                Write("[2100][UP][BUY][EX] " + ex.Message);
                ClearUpSwapRuntimeFlags(); // 런타임만 초기화(링크 DB는 유지)
                return false;
            }

            return true;
        }

        private static void ClearUpSwapRuntimeFlags()
        {
            Login.UpSwapInProgress = false;
            Login.UpSwapStartBand = 0;
            Login.UpSwapTargetBand = 0;
            Login.UpSwapFromBand = 0;
            Login.UpSwapFromQty = 0;
            Login.UpSwapExtraQty = 0;
        }


private async Task<bool> WaitUnlockAsync(int timeoutMs, string tag)
        {
            var sw = Stopwatch.StartNew();

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    if (!Login.TradeWait.IsLocked) return true;
                }
                catch { }

                await Task.Delay(50).ConfigureAwait(false);
            }

            Write("[2100][WAIT][" + tag + "] timeoutMs=" + timeoutMs);
            return false;
        }



        private static string SafeTrim(string s) => string.IsNullOrWhiteSpace(s) ? "" : s.Trim();

        private static bool IsCspaq12200RateLimitedMessage(string message)
        {
            message = message ?? "";
            return message.IndexOf("RateLimitCooldown", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("rc=-21", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("-21", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("전송제한", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void Write(string msg)
        {
            try
            {
                Console.WriteLine(msg);
                Debug.WriteLine(msg);
                Log?.Invoke(msg);
            }
            catch { }
        }
    }
}



// 2026-02-16 55250
