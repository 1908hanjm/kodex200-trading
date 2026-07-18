// 2310_상승슬라이딩실행순서.cs
// ------------------------------------------------------------
// 역할:
// - 2300 Plan 실행 (A안: SELL FIRE마다 1단계 복구)
// - ✅ OrderService의 특정 메서드명에 의존하지 않도록 "델리게이트 주입" 방식
// - ✅ 수정 후 실행 체인:
//      [1차] SELL 전송만 수행
//      [2차] SELL 완전체결 + UNLOCK + FINALIZE 이후에만 BUY 수행
//
// ✅ 이번 핵심 수정:
// - SELL 전송 전에 UpSlide snapshot을 static 으로 저장한다.
// - 0650은 DB 재계산(TryMakePlan) 없이 이 snapshot만 사용한다.
// ------------------------------------------------------------

using System;
using System.Data.SQLite;
using System.Threading.Tasks;

namespace Exercise_1
{
    public sealed class _2310_상승슬라이딩실행순서
    {
        private const int SAFETY_BUFFER = 100_000;

        // ✅ 실제 주문전송 메서드에 매핑할 델리게이트
        private readonly Func<string, string, int, int, int, Task<bool>> _sendOrderAsync;

        // ✅ 실제 주문가능현금 조회 메서드에 매핑할 델리게이트
        private readonly Func<Task<long>> _getOrderableCashAsync;

        // =========================================================
        // ✅ UpSlide snapshot (0650에서 사용)
        // =========================================================
        public sealed class UpSlideSnapshot
        {
            public bool IsValid;
            public int SellBand;
            public int SellQty;
            public int BuyBand;
            public int FromQty;
            public int FirePrice;
            public string Shcode;
        }

        private static readonly object _snapLock = new object();
        private static UpSlideSnapshot _saved;

        public static void SaveSnapshot(_2300_상승슬라이딩Plan.UpSlidePlan plan, int firePrice, string shcode)
        {
            if (plan == null) return;

            lock (_snapLock)
            {
                _saved = new UpSlideSnapshot
                {
                    IsValid = plan.IsUpSlide &&
                              plan.SellBand > 0 &&
                              plan.SellQty > 0 &&
                              plan.BuyBand > 0 &&
                              plan.FromQty > 0 &&
                              firePrice > 0 &&
                              !string.IsNullOrWhiteSpace(shcode),
                    SellBand = plan.SellBand,
                    SellQty = plan.SellQty,
                    BuyBand = plan.BuyBand,
                    FromQty = plan.FromQty,
                    FirePrice = firePrice,
                    Shcode = shcode ?? ""
                };
            }
        }

        public static bool TryGetSnapshot(out UpSlideSnapshot snap)
        {
            lock (_snapLock)
            {
                if (_saved != null && _saved.IsValid)
                {
                    snap = new UpSlideSnapshot
                    {
                        IsValid = _saved.IsValid,
                        SellBand = _saved.SellBand,
                        SellQty = _saved.SellQty,
                        BuyBand = _saved.BuyBand,
                        FromQty = _saved.FromQty,
                        FirePrice = _saved.FirePrice,
                        Shcode = _saved.Shcode
                    };
                    return true;
                }

                snap = null;
                return false;
            }
        }

        public static void ClearSnapshot()
        {
            lock (_snapLock)
            {
                _saved = null;
            }
        }

        public _2310_상승슬라이딩실행순서(
            Func<string, string, int, int, int, Task<bool>> sendOrderAsync,
            Func<Task<long>> getOrderableCashAsync)
        {
            _sendOrderAsync = sendOrderAsync ?? throw new ArgumentNullException(nameof(sendOrderAsync));
            _getOrderableCashAsync = getOrderableCashAsync ?? throw new ArgumentNullException(nameof(getOrderableCashAsync));
        }

        /// <summary>
        /// [1차 단계]
        /// 상승슬라이딩 SELL만 전송한다.
        /// 절대로 여기서 CASH 조회 / BUY 전송을 하지 않는다.
        /// </summary>
        public async Task ExecuteAsync(_2300_상승슬라이딩Plan.UpSlidePlan plan, int firePrice, string shcode)
        {
            if (plan == null)
            {
                Console.WriteLine("[2310][UPSLIDE] plan == null -> STOP");
                return;
            }

            if (!plan.IsUpSlide)
            {
                Console.WriteLine("[2310][UPSLIDE] plan.IsUpSlide == false -> STOP");
                return;
            }

            if (string.IsNullOrWhiteSpace(shcode))
            {
                Console.WriteLine("[2310][UPSLIDE] shcode is empty -> STOP");
                return;
            }

            if (plan.SellBand <= 0 || plan.SellQty <= 0 || plan.BuyBand <= 0 || plan.FromQty <= 0)
            {
                Console.WriteLine(
                    $"[2310][UPSLIDE] invalid plan " +
                    $"sellBand={plan.SellBand}, sellQty={plan.SellQty}, buyBand={plan.BuyBand}, fromQty={plan.FromQty} -> STOP");
                return;
            }

            if (firePrice <= 0)
            {
                Console.WriteLine($"[2310][UPSLIDE] firePrice <= 0 ({firePrice}) -> STOP");
                return;
            }

            // ✅ snapshot 저장
            SaveSnapshot(plan, firePrice, shcode);

            try
            {
                Login.UpSwapInProgress = true;
                Login.UpSwapStartBand = plan.SellBand;
                Login.UpSwapTargetBand = plan.BuyBand;

                Console.WriteLine(
                    $"[2310][UPSLIDE] FLAGS set " +
                    $"UpSwapInProgress=TRUE startBand={Login.UpSwapStartBand} targetBand={Login.UpSwapTargetBand}");
            }
            catch (Exception exFlag)
            {
                Console.WriteLine("[2310][UPSLIDE] FLAG set EX: " + exFlag.Message);
            }

            Console.WriteLine($"[2310][UPSLIDE] SEND_SELL K={plan.SellBand} qty={plan.SellQty} price={firePrice}");

            bool sellSent = await _sendOrderAsync("매도", shcode, firePrice, plan.SellQty, plan.SellBand);
            if (!sellSent)
            {
                Console.WriteLine("[2310][UPSLIDE] SELL send failed -> STOP");

                try
                {
                    Login.UpSwapInProgress = false;
                    Login.UpSwapStartBand = 0;
                    Login.UpSwapTargetBand = 0;
                }
                catch { }

                ClearSnapshot();
                return;
            }

            Console.WriteLine("[2310][UPSLIDE] SELL sent OK");
            Console.WriteLine("[2310][UPSLIDE] WAIT SELL COMPLETE -> 0650/0550/0700 will continue BUY stage later");
        }

        /// <summary>
        /// [2차 단계]
        /// SELL 완전체결 + UNLOCK + FINALIZE 이후 호출해야 한다.
        /// 이 단계에서만 주문가능현금 재조회 후 BUY를 전송한다.
        /// </summary>
        public async Task ExecuteBuyStageAsync(_2300_상승슬라이딩Plan.UpSlidePlan plan, int buyPrice, string shcode)
        {
            int sellBand = 0;
            int buyBand = 0;
            int fromQty = 0;

            if (plan != null && plan.IsUpSlide && plan.BuyBand > 0 && plan.FromQty > 0)
            {
                sellBand = plan.SellBand;
                buyBand = plan.BuyBand;
                fromQty = plan.FromQty;
            }
            else
            {
                UpSlideSnapshot snap;
                if (!TryGetSnapshot(out snap) || snap == null)
                {
                    Console.WriteLine("[2310][UPSLIDE][BUY_STAGE] snapshot missing -> STOP");
                    return;
                }

                sellBand = snap.SellBand;
                buyBand = snap.BuyBand;
                fromQty = snap.FromQty;

                if (string.IsNullOrWhiteSpace(shcode))
                    shcode = snap.Shcode;

                if (buyPrice <= 0)
                    buyPrice = snap.FirePrice;
            }

            if (string.IsNullOrWhiteSpace(shcode))
            {
                Console.WriteLine("[2310][UPSLIDE][BUY_STAGE] shcode is empty -> STOP");
                return;
            }

            if (buyBand <= 0)
            {
                Console.WriteLine(
                    $"[2310][UPSLIDE][BUY_STAGE] invalid buy plan " +
                    $"buyBand={buyBand}, fromQty={fromQty} -> STOP");
                return;
            }

            if (buyPrice <= 0)
            {
                Console.WriteLine($"[2310][UPSLIDE][BUY_STAGE] buyPrice <= 0 ({buyPrice}) -> STOP");
                return;
            }

            long orderableCash = await _getOrderableCashAsync();
            Console.WriteLine($"[2310][UPSLIDE][BUY_STAGE] CASH orderable={orderableCash:n0}");

            long desiredFromQty = Math.Max(0L, fromQty);
            long appliedExtraQty = CalculateExtraQty(orderableCash, buyPrice);
            long maxAffordableQty = CalculateMaxAffordableQty(orderableCash, buyPrice);
            long appliedFromQtyBeforeBandCap = Math.Min(desiredFromQty, maxAffordableQty);

            // ✅ [배정금 한도][프로세스6] buyBand(=targetBand=applyBand)의 고정
            // 배정금을 넘지 않도록 clamp. 초과분은 매수하지 않고 계좌에 현금으로
            // 남기며, 장종료 배치에서 실현손익과 합쳐 10개 band에 재분배한다.
            var bandCapClamp = 배정금_한도체크.ClampToBandCapital(buyBand, appliedFromQtyBeforeBandCap, buyPrice);
            long appliedFromQty = bandCapClamp.clampedQty;
            if (bandCapClamp.leftoverCash > 0)
            {
                Console.WriteLine("[2310][BAND_CAP][CLAMP] targetBand=" + buyBand +
                                  " qtyBeforeBandCap=" + appliedFromQtyBeforeBandCap +
                                  " finalQty=" + appliedFromQty +
                                  " leftoverCash=" + bandCapClamp.leftoverCash);
                배정금_한도체크.AddOverLimitLeftoverCash(bandCapClamp.leftoverCash);
            }

            long finalBuyQty = appliedFromQty;
            long requestedBuyQty = desiredFromQty;
            int buyQty = finalBuyQty > int.MaxValue ? int.MaxValue : (int)finalBuyQty;

            Console.WriteLine("[SLIDE][BUY_CALC_BEFORE] " +
                              "fromQty=" + desiredFromQty +
                              " desiredExtraQty=0" +
                              " maxAffordableQty=" + maxAffordableQty);

            if (buyQty <= 0)
            {
                Console.WriteLine(
                    $"[2310][UPSLIDE][BUY_STAGE] BUY qty=0 " +
                    $"(fromQty={desiredFromQty}, cash={orderableCash:n0}, price={buyPrice}) -> STOP");
                return;
            }

            SaveSlideExtraQty(buyBand, appliedExtraQty);
            try
            {
                Login.UpSwapFromBand = sellBand;
                Login.UpSwapFromQty = appliedFromQty;
                Login.UpSwapExtraQty = appliedExtraQty;
            }
            catch { }

            Console.WriteLine("[SLIDE][BUY_CALC_AFTER] " +
                              "appliedFromQty=" + appliedFromQty +
                              " appliedExtraQty=" + appliedExtraQty +
                              " finalBuyQty=" + buyQty);
            Console.WriteLine("[SLIDE][BUY_CALC_POLICY] tradeType=RECOVERY_BUY growthAllowed=False finalBuyQty=min(fromQty,maxAffordableQty)");
            Console.WriteLine("[UPSLIDE][BUY] " +
                              "fromQty=" + desiredFromQty +
                              " extraQty=" + appliedExtraQty +
                              " maxAffordableQty=" + maxAffordableQty +
                              " finalBuyQty=" + buyQty +
                              " orderableCash=" + orderableCash +
                              " tradeType=RECOVERY_BUY");
            Console.WriteLine(
                "[2310][UPSLIDE][META_PREPARE] " +
                "sourceBand=" + sellBand +
                " targetBand=" + buyBand +
                " fromQty=" + appliedFromQty +
                " extraQty=" + appliedExtraQty +
                " finalBuyQty=" + buyQty);

            Console.WriteLine("[SLIDE][BUY_CALC] " +
                              "Band=" + buyBand +
                              " FromBand=" + sellBand +
                              " FromQty=" + appliedFromQty +
                              " ExtraQty=" + appliedExtraQty +
                              " OrderableCash=" + orderableCash +
                              " BuyPrice=" + buyPrice +
                              " MaxAffordableQty=" + maxAffordableQty +
                              " FinalBuyQty=" + buyQty);

            Console.WriteLine("[PRE-SLIDE][BUY-CALC] " +
                              "orderableCash=" + orderableCash +
                              " zeroBandCount=1" +
                              " targetBand=" + buyBand +
                              " buyPrice=" + buyPrice +
                              " requestedBuyQty=" + requestedBuyQty +
                              " maxAffordableQty=" + maxAffordableQty +
                              " buyQty=" + buyQty);

            Console.WriteLine(
                "[CHAIN_RECOVERY][BUY_TARGET] " +
                "sourceBand=" + sellBand +
                " targetBand=" + buyBand +
                " remainFromQty=" + appliedFromQty +
                " remainExtraQty=" + appliedExtraQty +
                " orderableCash=" + orderableCash +
                " buyPrice=" + buyPrice +
                " buyQty=" + buyQty);

            if (buyQty <= 0)
            {
                Console.WriteLine(
                    $"[2310][UPSLIDE][BUY_STAGE] BUY qty=0 " +
                    $"(cash={orderableCash:n0}, price={buyPrice}) -> STOP");
                return;
            }

            try
            {
                Login.UpSwapInProgress = true;
                Login.UpSwapStartBand = sellBand;
                Login.UpSwapTargetBand = buyBand;
                Login.UpSwapFromBand = sellBand;
                Login.UpSwapFromQty = appliedFromQty;
                Login.UpSwapExtraQty = appliedExtraQty;

                Console.WriteLine(
                    $"[2310][UPSLIDE][BUY_STAGE] FLAGS confirm " +
                    $"UpSwapInProgress=TRUE startBand={Login.UpSwapStartBand} targetBand={Login.UpSwapTargetBand}");
            }
            catch (Exception exFlag)
            {
                Console.WriteLine("[2310][UPSLIDE][BUY_STAGE] FLAG confirm EX: " + exFlag.Message);
            }

            Console.WriteLine($"[2310][UPSLIDE][BUY_STAGE] SEND_BUY toBand={buyBand} qty={buyQty} price={buyPrice}");

            bool buySent = await _sendOrderAsync("매수", shcode, buyPrice, buyQty, buyBand);
            if (!buySent)
            {
                Console.WriteLine("[2310][UPSLIDE][BUY_STAGE] BUY send failed -> STOP");
                return;
            }

            Console.WriteLine("[2310][UPSLIDE][BUY_STAGE] BUY sent OK (fill-handling in 0650/0700)");
        }

        private static long CalculateExtraQty(long orderableCash, long price)
        {
            return 0L;
        }

        private static long CalculateMaxAffordableQty(long orderableCash, long price)
        {
            if (orderableCash <= 0 || price <= 0) return 0L;
            return orderableCash / price;
        }

        private static void SaveSlideExtraQty(int band, long extraQty)
        {
            if (band <= 0) return;
            if (extraQty < 0) extraQty = 0;

            using (var conn = new SQLiteConnection(Login.ConnStr))
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
    }
}
// 2026-03-11 58341
