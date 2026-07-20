// 0700_매매후update.cs
// ------------------------------------------------------------
// ✅ 구 pending 컬럼 제거 버전
// - kodex200_new.pending 컬럼을 더 이상 사용하지 않는다.
// - 부분체결/완전체결 시 pending 기록/전환/삭제를 수행하지 않는다.
// - 실제 체결 qty만 반영한다.
//
// ✅ 사용자 확정 규칙 반영
// - 일반 BUY FIRE가 밴드 K에서 발생하면 체결 qty는 K+1 밴드에 반영한다.
// - ✅ Up-Swap BUY는 K+1이 아니라 targetBand 자체에 반영한다.
// - ✅ Down-Sliding BUY는 K+1이 아니라 SwapRecordBandK(실매수밴드) 자체에 반영한다.
//
// ✅ 최종 규칙
// - BUY 체결 후: qty 증가
// - SELL 체결 후: qty 감소
//
// ✅ 추가 유지
// - 매도 qty가 0 아래로 내려가지 않게 DB에서 clamp
// - 다운슬라이딩 완료(=Swap 플래그 존재) + DB 반영 후 MessageBox 출력
// - ✅ Up-Swap 런타임 플래그 정리 추가
// - ✅ Up-Swap BUY 완전체결 시 startBand row의 from_band / from_qty 정리 추가
// - ✅ Up-Swap 복구 체인 SELL 부분체결 시:
//      (1) 현재 band.qty 감소
//      (2) 현재 band.from_qty 감소
//      (3) target from_band.qty 증가
// - ✅ Down-Sliding BUY 부분/완전체결 시:
//      (1) 일반 BUY의 K+1 반영을 사용하지 않는다.
//      (2) Login.SwapRecordBandK 를 실매수밴드로 사용한다.
//      (3) qty 증가만 반영한다.
//      (4) ✅ 첫 부분체결 시점에도 from_band / from_qty 를 즉시 기록한다.
//
// ✅ 이번 수정 핵심
// - 0700에서는 listView3 refresh를 절대 하지 않는다.
// - listView3 refresh는 0300 체인 종료(ChainFinished)에서만 1회 수행한다.
// - 따라서 TryReloadUiSafely()는 밴드 UI만 갱신하고,
//   SafeReloadLv3Async / RefreshBandListView 는 호출하지 않는다.
// ------------------------------------------------------------

using System;
using System.Data.SQLite;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Exercise_1
{
    public sealed class _0700_매매후update
    {
        private readonly Login _login;
        private readonly _0950_DB_증권사_잔고일치확인 _balanceChecker;
        private static long _check1302ExecNo;

        private sealed class BuyTypeRow
        {
            public long Qty;
            public int FromBand;
            public long FromQty;
            public long ExtraQty;
            public string BuyType;
        }

        public _0700_매매후update(Login login)
        {
            _login = login;
            _balanceChecker = new _0950_DB_증권사_잔고일치확인();
        }

        public static void MarkCheck1302Exec(long execNo)
        {
            _check1302ExecNo = execNo;
        }

        public void AfterFillUpdate(int band, int deltaQty, double price, string side, long execNo = 0, bool complete = false)
        {
            ApplyFillOnly(band: band, deltaQty: deltaQty, price: price, side: side, execNo: execNo, isComplete: complete);

            if (complete)
            {
                FinalizeAfterUnlock(side, band, price);
            }
        }

        public bool ApplyFillOnly(
            int band,
            int deltaQty,
            double price,
            string side,
            long execNo = 0,
            bool isComplete = false,
            long ordNo = 0,
            int downSlideFromBand = 0,
            int downSlideFromQty = 0,
            int downSlideRecordBand = 0,
            int upSlideSourceBand = 0,
            long upSlideFromQty = 0,
            long upSlideExtraQty = 0,
            int upSlideTargetBand = 0,
            int chainExtendFromBand = 0,
            int chainExtendTargetBand = 0,
            bool isRebuildBuy = false)
        {
            try
            {
                side = (side ?? "").Trim();
                if (deltaQty <= 0) return false;

                bool check1302 = execNo != 0 && execNo == _check1302ExecNo;
                if (check1302)
                {
                    Console.WriteLine("[CHECK][1302][0700] enter=true side=" +
                                      (side == "매도" ? "SELL" : side == "매수" ? "BUY" : side) +
                                      " qty=" + deltaQty +
                                      " reload=false chain=apply_fill_only_start");
                }

                var fullClear80 = Login.FullClearAfter80;
                if (isRebuildBuy && fullClear80 != null &&
                    fullClear80.TryHandleRestoreBuyFill(side, band, deltaQty, isComplete))
                {
                    return true;
                }

                // =========================================================
                // ✅ 특별 처리:
                // Up-Swap 복구 체인 중 "매도 부분/체결"은 일반 SELL이 아니라
                //   (1) 현재 band.qty 감소
                //   (2) 현재 band.from_qty 감소
                //   (3) target from_band.qty 증가
                // 로 처리한다.
                // =========================================================
                if (TryApplyUpSwapRecoverySellFill(side, band, deltaQty, price, execNo, ordNo, isComplete))
                {
                    // 부분체결 중에는 BandList/StartBand/FOCUS 재계산 금지.
                    // 완전체결 후 FinalizeAfterUnlock 경로에서만 RefreshBandsAndTriggers 허용.
                    if (isComplete)
                        TryReloadUiSafely();
                    else
                        Debug.WriteLine("[0700] partial UpSwap link fill -> skip RefreshBandsAndTriggers");
                    return true;
                }

                // =========================================================
                // ✅ applyBand 결정
                // - 일반 BUY: K+1
                // - Up-Swap BUY: targetBand 자체
                // - Down-Sliding BUY: SwapRecordBandK(실매수밴드) 자체
                // - SELL: band 자체
                // =========================================================
                int applyBand;

                bool useUpSwapTarget = false;
                int upTargetBand = 0;
                int upSourceBand = 0;
                long upMetaFromQty = 0;
                long upMetaExtraQty = 0;
                bool useDownSlideTarget = false;
                int downSlideTargetBand = 0;
                bool useChainExtendTarget = false;
                int chainExtendApplyBand = 0;

                if (side == "매수")
                {
                    try
                    {
                        bool hasOrderUpSlideMeta =
                            upSlideSourceBand > 0 &&
                            upSlideTargetBand > 0 &&
                            upSlideFromQty > 0;

                        if (hasOrderUpSlideMeta)
                        {
                            useUpSwapTarget = true;
                            upSourceBand = upSlideSourceBand;
                            upTargetBand = upSlideTargetBand;
                            upMetaFromQty = upSlideFromQty;
                            upMetaExtraQty = upSlideExtraQty > 0 ? upSlideExtraQty : 0;
                        }
                        else
                        {
                            useUpSwapTarget = Login.UpSwapInProgress && Login.UpSwapTargetBand > 0;
                            upSourceBand = Login.UpSwapStartBand;
                            upTargetBand = Login.UpSwapTargetBand;
                            upMetaFromQty = Login.UpSwapFromQty;
                            upMetaExtraQty = Login.UpSwapExtraQty;
                        }
                    }
                    catch
                    {
                        useUpSwapTarget = false;
                        upSourceBand = 0;
                        upTargetBand = 0;
                        upMetaFromQty = 0;
                        upMetaExtraQty = 0;
                    }

                    if (downSlideFromBand > 0 && downSlideFromQty > 0 && downSlideRecordBand > 0)
                    {
                        useDownSlideTarget = true;
                        downSlideTargetBand = downSlideRecordBand;
                    }
                    else
                    {
                        try
                        {
                            useDownSlideTarget = Login.SwapInProgress && Login.SwapRecordBandK > 0;
                            downSlideTargetBand = Login.SwapRecordBandK;
                        }
                        catch
                        {
                            useDownSlideTarget = false;
                            downSlideTargetBand = 0;
                        }
                    }

                    if (chainExtendFromBand > 0 && chainExtendTargetBand > 0)
                    {
                        useChainExtendTarget = true;
                        chainExtendApplyBand = chainExtendTargetBand;
                    }

                    if (useUpSwapTarget)
                    {
                        applyBand = upTargetBand;

                        Debug.WriteLine(
                            $"[0700] ApplyFillOnly UpSwap BUY " +
                            $"orderBand={band} targetBand={applyBand} deltaQty={deltaQty} execNo={execNo}");
                        Console.WriteLine(
                            "[0700][UP][APPLY_BAND] " +
                            $"orderBand={band} " +
                            $"applyBand={applyBand} " +
                            $"sourceBand={upSourceBand} " +
                            $"filledQty={deltaQty} " +
                            $"fromBand={upSourceBand} " +
                            $"fromQty={upMetaFromQty} " +
                            $"extraQty={upMetaExtraQty}");
                    }
                    else if (useDownSlideTarget)
                    {
                        applyBand = downSlideTargetBand;

                        Debug.WriteLine(
                            $"[0700] ApplyFillOnly DownSlide BUY " +
                            $"orderBand={band} targetBand={applyBand} deltaQty={deltaQty} execNo={execNo}");
                        Console.WriteLine(
                            "[0700][DOWN][APPLY_BAND] " +
                            $"orderBand={band} " +
                            $"downRecordBand={downSlideTargetBand} " +
                            $"applyBand={applyBand} " +
                            $"SwapRecordBandK={Login.SwapRecordBandK} " +
                            $"SwapFromBand={Login.SwapFromBand} " +
                            $"SwapFromQty={Login.SwapFromQty} " +
                            $"SwapExtraQty={Login.SwapExtraQty}");
                    }
                    else if (useChainExtendTarget)
                    {
                        applyBand = chainExtendApplyBand;

                        Debug.WriteLine(
                            $"[0700] ApplyFillOnly ChainExtend BUY " +
                            $"orderBand={band} targetBand={applyBand} deltaQty={deltaQty} execNo={execNo}");
                        Console.WriteLine(
                            "[0700][10전슬라이딩BUY][APPLY_BAND] " +
                            $"orderBand={band} " +
                            $"applyBand={applyBand} " +
                            $"fromBand={chainExtendFromBand} " +
                            $"filledQty={deltaQty}");
                    }
                    else
                    {
                        applyBand = band + 1;
                    }
                }
                else // "매도"
                {
                    applyBand = band;
                }

                long newQty = 0;
                long dbBeforeQty = 0;
                long savedRealBuyPrice = 0;
                BuyTypeRow afterRow = null;

                using (var conn = new SQLiteConnection(Login.ConnStr))
                {
                    conn.Open();
                    DB_Control.EnsureExtraQtyColumn(conn);

                    using (var tx = conn.BeginTransaction())
                    {
                        long chainQtyBefore = 0;
                        long chainFromQtyBefore = 0;
                        int chainFromBandBefore = 0;
                        long upSourceFromQtyBefore = 0;
                        long upSourceExtraQtyBefore = 0;
                        long upTargetQtyBefore = 0;
                        int upTargetFromBandBefore = 0;
                        long upTargetFromQtyBefore = 0;
                        long upTargetExtraQtyBefore = 0;

                        using (var cmdQtyBefore = conn.CreateCommand())
                        {
                            cmdQtyBefore.Transaction = tx;
                            cmdQtyBefore.CommandText =
                                "SELECT qty FROM kodex200_new WHERE band = @b";
                            cmdQtyBefore.Parameters.AddWithValue("@b", applyBand);

                            object beforeValue = cmdQtyBefore.ExecuteScalar();
                            dbBeforeQty = beforeValue != null && beforeValue != DBNull.Value
                                ? Convert.ToInt64(beforeValue)
                                : 0;
                        }

                        if (side == "매도")
                        {
                            using (var cmdChainBefore = conn.CreateCommand())
                            {
                                cmdChainBefore.Transaction = tx;
                                cmdChainBefore.CommandText =
                                    "SELECT qty, from_band, from_qty FROM kodex200_new WHERE band = @b";
                                cmdChainBefore.Parameters.AddWithValue("@b", applyBand);

                                using (var rdChainBefore = cmdChainBefore.ExecuteReader())
                                {
                                    if (rdChainBefore.Read())
                                    {
                                        chainQtyBefore = rdChainBefore["qty"] != DBNull.Value ? Convert.ToInt64(rdChainBefore["qty"]) : 0;
                                        chainFromBandBefore = rdChainBefore["from_band"] != DBNull.Value ? Convert.ToInt32(rdChainBefore["from_band"]) : 0;
                                        chainFromQtyBefore = rdChainBefore["from_qty"] != DBNull.Value ? Convert.ToInt64(rdChainBefore["from_qty"]) : 0;
                                    }
                                }
                            }
                        }
                        else if (side == "매수" && useUpSwapTarget && upSourceBand > 0)
                        {
                            using (var cmdUpSourceBefore = conn.CreateCommand())
                            {
                                cmdUpSourceBefore.Transaction = tx;
                                cmdUpSourceBefore.CommandText =
                                    "SELECT from_qty, extra_qty FROM kodex200_new WHERE band = @b";
                                cmdUpSourceBefore.Parameters.AddWithValue("@b", upSourceBand);

                                using (var rdUpSource = cmdUpSourceBefore.ExecuteReader())
                                {
                                    if (rdUpSource.Read())
                                    {
                                        upSourceFromQtyBefore = rdUpSource["from_qty"] != DBNull.Value ? Convert.ToInt64(rdUpSource["from_qty"]) : 0;
                                        upSourceExtraQtyBefore = rdUpSource["extra_qty"] != DBNull.Value ? Convert.ToInt64(rdUpSource["extra_qty"]) : 0;
                                    }
                                }
                            }

                            using (var cmdUpTargetBefore = conn.CreateCommand())
                            {
                                cmdUpTargetBefore.Transaction = tx;
                                cmdUpTargetBefore.CommandText =
                                    "SELECT qty, from_band, from_qty, extra_qty FROM kodex200_new WHERE band = @b";
                                cmdUpTargetBefore.Parameters.AddWithValue("@b", applyBand);

                                using (var rdUpTarget = cmdUpTargetBefore.ExecuteReader())
                                {
                                    if (rdUpTarget.Read())
                                    {
                                        upTargetQtyBefore = rdUpTarget["qty"] != DBNull.Value ? Convert.ToInt64(rdUpTarget["qty"]) : 0;
                                        upTargetFromBandBefore = rdUpTarget["from_band"] != DBNull.Value ? Convert.ToInt32(rdUpTarget["from_band"]) : 0;
                                        upTargetFromQtyBefore = rdUpTarget["from_qty"] != DBNull.Value ? Convert.ToInt64(rdUpTarget["from_qty"]) : 0;
                                        upTargetExtraQtyBefore = rdUpTarget["extra_qty"] != DBNull.Value ? Convert.ToInt64(rdUpTarget["extra_qty"]) : 0;
                                    }
                                }
                            }
                        }

                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = tx;

                            if (side == "매수")
                            {
                                if (useUpSwapTarget)
                                {
                                    // =====================================================
                                    // [정책 변경 2026-07-02] UPSWAP BUY qty 실제 반영
                                    // 새 정책: SELL은 sourceBand qty 감소만 수행하고
                                    // targetBand qty를 건드리지 않는다.
                                    // 따라서 BUY 체결 시 실제 체결량만큼
                                    // targetBand qty += deltaQty 해야 한다.
                                    // 진짜산가격도 함께 갱신한다.
                                    // =====================================================
                                    cmd.CommandText =
                                        "UPDATE kodex200_new " +
                                        "SET qty = qty + @q, " +
                                        "    진짜산가격 = @realBuyPrice " +
                                        "WHERE band = @b";

                                    string buyLog =
                                        "[UPSWAP_BUY_QTY_ADD] " +
                                        $"ordNo={ordNo} " +
                                        $"band={applyBand} " +
                                        $"fillQty={deltaQty} " +
                                        $"isComplete={isComplete}";
                                    // ✅ [LOG-REDUCE] 틱마다 찍히던 로그 -> 완전체결만 Console, 나머지는 Debug
                                    if (isComplete)
                                        Console.WriteLine(buyLog);
                                    else
                                        Debug.WriteLine(buyLog);
                                }
                                else
                                {
                                    if (dbBeforeQty <= 0)
                                    {
                                        // ✅ [SAFETY-NET 2026-07-14] 일반(GENERAL) 매수가
                                        // 빈 밴드(qty=0)에 새로 들어갈 때, 예전 강제슬라이딩/
                                        // 업스와이드 체인에서 남아있던 from_band/from_qty/extra_qty
                                        // 잔재를 함께 초기화한다.
                                        // (band가 비어 있었다는 것은 그 잔재가 이미 유효한 체인이
                                        //  아니라는 뜻이므로, 새 매수가 시작되는 시점에 청소한다.)
                                        cmd.CommandText =
                                            "UPDATE kodex200_new " +
                                            "SET qty = qty + @q, " +
                                            "    진짜산가격 = @realBuyPrice, " +
                                            "    from_band = 0, " +
                                            "    from_qty = 0, " +
                                            "    extra_qty = 0 " +
                                            "WHERE band = @b";

                                        Debug.WriteLine(
                                            $"[0700][GENERAL_BUY][STALE_CHAIN_RESET] band={applyBand} " +
                                            $"ordNo={ordNo} dbBeforeQty={dbBeforeQty} reason=empty_band_before_buy");
                                    }
                                    else
                                    {
                                        cmd.CommandText =
                                            "UPDATE kodex200_new " +
                                            "SET qty = qty + @q, " +
                                            "    진짜산가격 = @realBuyPrice " +
                                            "WHERE band = @b";
                                    }
                                }
                            }
                            else // "매도"
                            {
                                // SELL: qty만 감소(clamp). from_band/from_qty는 복구 체인 메타데이터로 보존한다.
                                cmd.CommandText =
                                    "UPDATE kodex200_new " +
                                    "SET qty = CASE WHEN (qty - @q) < 0 THEN 0 ELSE (qty - @q) END " +
                                    "WHERE band = @b";
                            }

                            cmd.Parameters.AddWithValue("@q", deltaQty);
                            if (side == "매수")
                            {
                                savedRealBuyPrice = Convert.ToInt64(Math.Round(price));
                                cmd.Parameters.AddWithValue("@realBuyPrice", savedRealBuyPrice);
                            }
                            cmd.Parameters.AddWithValue("@b", applyBand);
                            cmd.ExecuteNonQuery();
                        }

                        // =====================================================
                        // [정책 추가 2026-06-23] 복원 SELL
                        // SELL 대상 밴드에 from_band > 0 이면:
                        //   a) 원 from_band 밴드 qty += sellFillQty (복원)
                        //   b) 매도 밴드 from_qty = max(0, from_qty - sellFillQty)
                        //   c) 매도 밴드 qty 가 0 이면 from_band/from_qty/extra_qty 모두 0
                        //   d) 매도 밴드 qty 가 남아 있으면 from_band 유지
                        // UpSwap SELL (TryApplyUpSwapRecoverySellFill)과는 별개 경로이므로
                        // 해당 함수가 return true 하지 않았을 때만 이 블록에 도달한다.
                        // =====================================================
                        if (side == "매도" && chainFromBandBefore > 0)
                        {
                            int originBand = chainFromBandBefore;

                            // 매도 밴드의 현재 qty/from_qty 읽기
                            long sellQtyAfter = 0;
                            long sellFromQtyAfter = 0;
                            using (var cmdSellRead = conn.CreateCommand())
                            {
                                cmdSellRead.Transaction = tx;
                                cmdSellRead.CommandText =
                                    "SELECT qty, from_qty FROM kodex200_new WHERE band = @b";
                                cmdSellRead.Parameters.AddWithValue("@b", applyBand);
                                using (var rdSR = cmdSellRead.ExecuteReader())
                                {
                                    if (rdSR.Read())
                                    {
                                        sellQtyAfter     = rdSR["qty"]      != DBNull.Value ? Convert.ToInt64(rdSR["qty"])      : 0;
                                        long fq          = rdSR["from_qty"] != DBNull.Value ? Convert.ToInt64(rdSR["from_qty"]) : 0;
                                        sellFromQtyAfter = Math.Max(0L, fq - deltaQty);
                                    }
                                }
                            }

                            // origin 밴드 복원 전 qty 읽기
                            long originQtyBefore = 0;
                            using (var cmdOriRead = conn.CreateCommand())
                            {
                                cmdOriRead.Transaction = tx;
                                cmdOriRead.CommandText =
                                    "SELECT qty FROM kodex200_new WHERE band = @b";
                                cmdOriRead.Parameters.AddWithValue("@b", originBand);
                                object v = cmdOriRead.ExecuteScalar();
                                originQtyBefore = v != null && v != DBNull.Value ? Convert.ToInt64(v) : 0;
                            }

                            // a) origin 밴드 qty += fillQty
                            using (var cmdOriUpd = conn.CreateCommand())
                            {
                                cmdOriUpd.Transaction = tx;
                                cmdOriUpd.CommandText =
                                    "UPDATE kodex200_new " +
                                    "SET qty = qty + @q " +
                                    "WHERE band = @b";
                                cmdOriUpd.Parameters.AddWithValue("@q", deltaQty);
                                cmdOriUpd.Parameters.AddWithValue("@b", originBand);
                                cmdOriUpd.ExecuteNonQuery();
                            }

                            long originQtyAfter = originQtyBefore + deltaQty;

                            // b/c/d) 매도 밴드 from_qty 감소, qty=0 이면 계보 초기화
                            if (sellQtyAfter == 0)
                            {
                                // 매도 밴드 수량 소진 → 계보 전부 초기화
                                using (var cmdSellClose = conn.CreateCommand())
                                {
                                    cmdSellClose.Transaction = tx;
                                    cmdSellClose.CommandText =
                                        "UPDATE kodex200_new " +
                                        "SET from_band  = 0, " +
                                        "    from_qty   = 0, " +
                                        "    extra_qty  = 0 " +
                                        "WHERE band = @b";
                                    cmdSellClose.Parameters.AddWithValue("@b", applyBand);
                                    cmdSellClose.ExecuteNonQuery();
                                }

                                string closeLog =
                                    "[CHAIN_CLOSE] " +
                                    $"sellBand={applyBand} " +
                                    $"originBand={originBand} " +
                                    "reason=SELL_BAND_QTY_ZERO";
                                Console.WriteLine(closeLog);
                                Debug.WriteLine(closeLog);
                            }
                            else
                            {
                                // 잔여 수량 있음 → from_qty만 감소, from_band 유지
                                using (var cmdSellFqUpd = conn.CreateCommand())
                                {
                                    cmdSellFqUpd.Transaction = tx;
                                    cmdSellFqUpd.CommandText =
                                        "UPDATE kodex200_new " +
                                        "SET from_qty = @fq " +
                                        "WHERE band = @b";
                                    cmdSellFqUpd.Parameters.AddWithValue("@fq", sellFromQtyAfter);
                                    cmdSellFqUpd.Parameters.AddWithValue("@b", applyBand);
                                    cmdSellFqUpd.ExecuteNonQuery();
                                }
                            }

                            string restoreLog =
                                "[CHAIN_SELL_RESTORE] " +
                                $"sellBand={applyBand} " +
                                $"originBand={originBand} " +
                                $"fillQty={deltaQty} " +
                                $"beforeSellQty={chainQtyBefore} " +
                                $"afterSellQty={sellQtyAfter} " +
                                $"beforeOriginQty={originQtyBefore} " +
                                $"afterOriginQty={originQtyAfter} " +
                                $"beforeFromQty={chainFromQtyBefore} " +
                                $"afterFromQty={sellFromQtyAfter}";
                            Console.WriteLine(restoreLog);
                            Debug.WriteLine(restoreLog);

                            // origin 밴드 UI 이벤트
                            DbFuncs.RaiseKodexQtyUpdated(originBand, originQtyAfter);
                        }

                        if (side == "매도")
                        {
                            using (var cmdChainAfter = conn.CreateCommand())
                            {
                                cmdChainAfter.Transaction = tx;
                                cmdChainAfter.CommandText =
                                    "SELECT qty, from_band, from_qty FROM kodex200_new WHERE band = @b";
                                cmdChainAfter.Parameters.AddWithValue("@b", applyBand);

                                using (var rdChainAfter = cmdChainAfter.ExecuteReader())
                                {
                                    if (rdChainAfter.Read())
                                    {
                                        long chainQtyAfter = rdChainAfter["qty"] != DBNull.Value ? Convert.ToInt64(rdChainAfter["qty"]) : 0;
                                        int chainFromBandAfter = rdChainAfter["from_band"] != DBNull.Value ? Convert.ToInt32(rdChainAfter["from_band"]) : 0;
                                        long chainFromQtyAfter = rdChainAfter["from_qty"] != DBNull.Value ? Convert.ToInt64(rdChainAfter["from_qty"]) : 0;
                                        bool preserved = chainFromBandAfter == chainFromBandBefore &&
                                                         chainFromQtyAfter == chainFromQtyBefore;

                                        Debug.WriteLine(
                                            "[CHAIN_KEEP] " +
                                            $"band={applyBand} " +
                                            $"qty_before={chainQtyBefore} " +
                                            $"qty_after={chainQtyAfter} " +
                                            $"from_band={chainFromBandAfter} " +
                                            $"from_qty_before={chainFromQtyBefore} " +
                                            $"from_qty_after={chainFromQtyAfter} " +
                                            $"preserved={preserved}");
                                    }
                                }
                            }
                        }
                        else if (side == "매수" && useUpSwapTarget && upSourceBand > 0)
                        {
                            // =====================================================
                            // [정책 변경 2026-07-02] 새 정책: 체인 메타 clear는
                            // BUY 최종 완료(isComplete=true) 시에만 수행한다.
                            // 부분체결 중에는 targetBand/sourceBand 메타를 건드리지 않는다.
                            // =====================================================
                            int sourceBand = upSourceBand;
                            int targetBand = applyBand;
                            long upTargetQtyAfter = upTargetQtyBefore + deltaQty; // qty는 위 cmd에서 이미 반영됨

                            string buyFillLog =
                                "[CHAIN_RECOVERY][BUY_FILL] " +
                                $"sourceBand={sourceBand} " +
                                $"targetBand={targetBand} " +
                                $"fillQty={deltaQty} " +
                                $"isComplete={isComplete} " +
                                $"target_qty_before={upTargetQtyBefore} " +
                                $"target_qty_after={upTargetQtyAfter}";
                            Debug.WriteLine(buyFillLog);
                            if (isComplete)
                                Console.WriteLine(buyFillLog);

                            if (isComplete)
                            {
                                // BUY 최종 완료: targetBand 메타 clear
                                using (var cmdUpTargetMeta = conn.CreateCommand())
                                {
                                    cmdUpTargetMeta.Transaction = tx;
                                    cmdUpTargetMeta.CommandText =
                                        "UPDATE kodex200_new " +
                                        "SET from_band = 0, " +
                                        "    from_qty = 0, " +
                                        "    extra_qty = 0 " +
                                        "WHERE band = @b";
                                    cmdUpTargetMeta.Parameters.AddWithValue("@b", targetBand);
                                    cmdUpTargetMeta.ExecuteNonQuery();
                                }

                                // BUY 최종 완료: sourceBand 메타 clear
                                using (var cmdUpSourceMeta = conn.CreateCommand())
                                {
                                    cmdUpSourceMeta.Transaction = tx;
                                    cmdUpSourceMeta.CommandText =
                                        "UPDATE kodex200_new " +
                                        "SET from_band = 0, " +
                                        "    from_qty = 0, " +
                                        "    extra_qty = 0 " +
                                        "WHERE band = @b";
                                    cmdUpSourceMeta.Parameters.AddWithValue("@b", sourceBand);
                                    cmdUpSourceMeta.ExecuteNonQuery();
                                }

                                string upSlideClearLog =
                                    "[UPSLIDE_CHAIN_CLEAR] " +
                                    $"targetBand={targetBand} " +
                                    $"sourceBand={sourceBand} " +
                                    $"beforeTargetFromBand={upTargetFromBandBefore} " +
                                    "afterTargetFromBand=0 " +
                                    $"beforeTargetFromQty={upTargetFromQtyBefore} " +
                                    "afterTargetFromQty=0 " +
                                    $"beforeTargetExtraQty={upTargetExtraQtyBefore} " +
                                    "afterTargetExtraQty=0 " +
                                    $"beforeSourceFromQty={upSourceFromQtyBefore} " +
                                    "afterSourceFromQty=0 " +
                                    "reason=BUY_COMPLETE";
                                Console.WriteLine(upSlideClearLog);
                                Debug.WriteLine(upSlideClearLog);
                            }
                            else
                            {
                                // BUY 부분체결: 메타 유지, 로그만 기록
                                string partialLog =
                                    "[UPSWAP_BUY_PARTIAL] " +
                                    $"ordNo={ordNo} " +
                                    $"targetBand={targetBand} " +
                                    $"fillQty={deltaQty} " +
                                    $"targetQtyAfter={upTargetQtyAfter} " +
                                    "meta=preserved";
                                Debug.WriteLine(partialLog);
                            }
                        }

                        // =====================================================
                        // [정책 변경 2026-06-23]
                        // Down-Sliding BUY 체결 시 from_band/from_qty/extra_qty를
                        // 소진하지 않는다. 계보 필드는 SELL 시점까지 그대로 보존한다.
                        // BUY 체결은 qty 증가만 반영한다.
                        // =====================================================
                        if (side == "매수" && useChainExtendTarget)
                        {
                            long chainExtendFromQtyAfter = 0;
                            long chainExtendQtyAfter = 0;

                            using (var cmdChainExtend = conn.CreateCommand())
                            {
                                cmdChainExtend.Transaction = tx;
                                cmdChainExtend.CommandText =
                                    "UPDATE kodex200_new " +
                                    "SET from_band = @fb, " +
                                    "    from_qty = CASE WHEN IFNULL(from_band, 0) = @fb THEN IFNULL(from_qty, 0) + @q ELSE @q END, " +
                                    "    extra_qty = 0 " +
                                    "WHERE band = @b";
                                cmdChainExtend.Parameters.AddWithValue("@fb", chainExtendFromBand);
                                cmdChainExtend.Parameters.AddWithValue("@q", deltaQty);
                                cmdChainExtend.Parameters.AddWithValue("@b", applyBand);
                                cmdChainExtend.ExecuteNonQuery();
                            }

                            using (var cmdChainExtendRead = conn.CreateCommand())
                            {
                                cmdChainExtendRead.Transaction = tx;
                                cmdChainExtendRead.CommandText =
                                    "SELECT qty, from_qty FROM kodex200_new WHERE band = @b";
                                cmdChainExtendRead.Parameters.AddWithValue("@b", applyBand);
                                using (var rdChainExtend = cmdChainExtendRead.ExecuteReader())
                                {
                                    if (rdChainExtend.Read())
                                    {
                                        chainExtendQtyAfter = rdChainExtend["qty"] != DBNull.Value ? Convert.ToInt64(rdChainExtend["qty"]) : 0;
                                        chainExtendFromQtyAfter = rdChainExtend["from_qty"] != DBNull.Value ? Convert.ToInt64(rdChainExtend["from_qty"]) : 0;
                                    }
                                }
                            }

                            string chainExtendLog =
                                "[0320][10전슬라이딩BUY] " +
                                $"targetBand={applyBand} " +
                                $"fromBand={chainExtendFromBand} " +
                                $"fillQty={deltaQty} " +
                                $"fromQty={chainExtendFromQtyAfter} " +
                                $"qty={chainExtendQtyAfter}";
                            Console.WriteLine(chainExtendLog);
                            Debug.WriteLine(chainExtendLog);
                        }
                        else if (side == "매수" && useDownSlideTarget)
                        {
                            int recordBandK = downSlideTargetBand;
                            int fromBand = downSlideFromBand;

                            if (recordBandK <= 0 || deltaQty <= 0)
                            {
                                Debug.WriteLine(
                                    "[0700][DOWN-FROM][SKIP] reason=invalid_link " +
                                    $"band={recordBandK} fromBand={fromBand} deltaQty={deltaQty}");
                            }
                            else
                            {
                                // from_band/from_qty/extra_qty는 건드리지 않는다.
                                // qty는 이미 위에서 증가 처리됨.
                                // BUY 완료 후 계보 상태를 로그로 기록한다.
                                using (var cmdKeepRead = conn.CreateCommand())
                                {
                                    cmdKeepRead.Transaction = tx;
                                    cmdKeepRead.CommandText =
                                        "SELECT qty, from_band, from_qty, extra_qty FROM kodex200_new WHERE band = @b";
                                    cmdKeepRead.Parameters.AddWithValue("@b", recordBandK);

                                    using (var rdKeep = cmdKeepRead.ExecuteReader())
                                    {
                                        if (rdKeep.Read())
                                        {
                                            long kQty      = rdKeep["qty"]       != DBNull.Value ? Convert.ToInt64(rdKeep["qty"])       : 0;
                                            int  kFromBand = rdKeep["from_band"] != DBNull.Value ? Convert.ToInt32(rdKeep["from_band"]) : 0;
                                            long kFromQty  = rdKeep["from_qty"]  != DBNull.Value ? Convert.ToInt64(rdKeep["from_qty"])  : 0;
                                            long kExtraQty = rdKeep["extra_qty"] != DBNull.Value ? Convert.ToInt64(rdKeep["extra_qty"]) : 0;

                                            string keepLog =
                                                "[CHAIN_KEEP_AFTER_BUY] " +
                                                $"band={recordBandK} " +
                                                $"qty={kQty} " +
                                                $"from_band={kFromBand} " +
                                                $"from_qty={kFromQty} " +
                                                $"extra_qty={kExtraQty}";
                                            Console.WriteLine(keepLog);
                                            Debug.WriteLine(keepLog);
                                        }
                                    }
                                }
                            }
                        }
                        else if (side == "매수" && downSlideFromBand > 0 && downSlideRecordBand > 0)
                        {
                            Debug.WriteLine(
                                "[0700][DOWN-FROM][SKIP] reason=down_target_not_used " +
                                $"band={downSlideRecordBand} fromBand={downSlideFromBand} deltaQty={deltaQty}");
                        }

                        tx.Commit();
                    }

                    using (var cmd2 = conn.CreateCommand())
                    {
                        cmd2.CommandText = "SELECT qty, from_band, from_qty, extra_qty FROM kodex200_new WHERE band = @b";
                        cmd2.Parameters.AddWithValue("@b", applyBand);

                        using (var rd = cmd2.ExecuteReader())
                        {
                            if (rd.Read())
                            {
                                if (rd["qty"] != DBNull.Value)
                                    newQty = Convert.ToInt64(rd["qty"]);

                                afterRow = new BuyTypeRow
                                {
                                    Qty = newQty,
                                    FromBand = rd["from_band"] != DBNull.Value ? Convert.ToInt32(rd["from_band"]) : 0,
                                    FromQty = rd["from_qty"] != DBNull.Value ? Convert.ToInt64(rd["from_qty"]) : 0,
                                    ExtraQty = rd["extra_qty"] != DBNull.Value ? Convert.ToInt64(rd["extra_qty"]) : 0,
                                    BuyType = ""
                                };
                            }
                        }
                    }
                }

                if (side == "매수")
                {
                    LogBuyTypeAfter(applyBand, afterRow);

                    string realBuyLog =
                        "[REAL_BUY_PRICE_SAVE] " +
                        "side=BUY " +
                        $"band={applyBand} " +
                        $"ordNo={ordNo} " +
                        $"fillQty={deltaQty} " +
                        $"fillPrice={price} " +
                        $"savedRealBuyPrice={savedRealBuyPrice} " +
                        $"dbBeforeQty={dbBeforeQty} " +
                        $"dbAfterQty={newQty}";
                    // ✅ [LOG-REDUCE] 이 로그가 실제 매수 대상 band(applyBand)를 정확히 찍는
                    // 유일한 실시간 로그이므로 완전체결 시점은 Console 유지, 부분체결은 Debug로.
                    if (isComplete)
                        Console.WriteLine(realBuyLog);
                    else
                        Debug.WriteLine(realBuyLog);
                }

                Debug.WriteLine(
                    $"[0700] ApplyFillOnly side={side} band={band} applyBand={applyBand} " +
                    $"deltaQty={deltaQty} price={price} execNo={execNo} newQty={newQty}");

                if (check1302)
                {
                    Console.WriteLine("[CHECK][1302][0700] enter=true side=" +
                                      (side == "매도" ? "SELL" : side == "매수" ? "BUY" : side) +
                                      " qty=" + deltaQty +
                                      " reload=false chain=db_update_done applyBand=" + applyBand +
                                      " isComplete=" + isComplete);
                }

                if (!isComplete)
                {
                    string sideText = side == "매수" ? "BUY" : "SELL";
                    Debug.WriteLine("[FILL][PARTIAL] " +
                                    "side=" + sideText +
                                    " band=" + applyBand +
                                    " fillQty=" + deltaQty +
                                    " dbQtyAfter=" + newQty +
                                    " unfilledIgnored=true");
                }

                // ✅ 체결 즉시 UI 이벤트도 올려준다.
                // isComplete 파라미터를 그대로 전달: 부분체결 시 t0424 호출 금지 판단은 Login_05에서 수행
                DbFuncs.RaiseKodexQtyUpdated(applyBand, newQty, isComplete);

                RequestBalanceCheckAfterDbApplied(
                    side: side,
                    ordNo: ordNo,
                    execNo: execNo,
                    band: band,
                    fillQty: deltaQty,
                    price: price,
                    cumFill: isComplete ? deltaQty : deltaQty,
                    remain: isComplete ? 0 : -1,
                    isComplete: isComplete);

                // 부분체결 중에는 BandList/StartBand/FOCUS 재계산 금지.
                // isComplete=false이면 DbFuncs 이벤트를 통한 수량 표시만 허용하고 종료한다.
                if (isComplete)
                {
                    if (side == "매도")
                    {
                        try
                        {
                            int holdingCount = CountHeldBandsFromDb();
                            Debug.WriteLine("[0700][FULL-CLEAR-CHECK] lastSoldBand=" + band + " holdingCount=" + holdingCount);

                            if (holdingCount == 0)
                            {
                                int sellDecisionBand = band;
                                try
                                {
                                    if (Login.FullClearSellDecisionBand != 0)
                                        sellDecisionBand = Login.FullClearSellDecisionBand;
                                }
                                catch { }

                                Login.FullClearAfter80?.CheckAfterSellCompleted(price, band, sellDecisionBand);
                                try { Login.FullClearSellDecisionBand = 0; } catch { }
                            }
                        }
                        catch (Exception ex80) { Debug.WriteLine("[0700][0800] CheckAfterSellCompleted ERROR " + ex80.Message); }
                    }

                    // ✅ listView3는 갱신하지 않고 밴드 UI만 갱신
                    if (check1302)
                    {
                        Console.WriteLine("[CHECK][1302][0700] enter=true side=" +
                                          (side == "매도" ? "SELL" : side == "매수" ? "BUY" : side) +
                                          " qty=" + deltaQty +
                                          " reload=false chain=before_band_ui_refresh_only");
                    }
                    TryReloadUiSafely();
                }
                else
                {
                    Debug.WriteLine("[0700] partial fill -> skip RefreshBandsAndTriggers");
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[0700] ApplyFillOnly ERROR " + ex.Message);
                return false;
            }
        }

        private void RequestBalanceCheckAfterDbApplied(
            string side,
            long ordNo,
            long execNo,
            int band,
            int fillQty,
            double price,
            int cumFill,
            int remain,
            bool isComplete)
        {
            try
            {
                if (_balanceChecker == null) return;

                var ctx = new _0950_DB_증권사_잔고일치확인.FillContext
                {
                    Side = side,
                    OrdNo = ordNo,
                    ExecNo = execNo,
                    Band = band,
                    FillQty = fillQty,
                    Price = price,
                    CumFill = cumFill,
                    Remain = remain,
                    IsComplete = isComplete
                };

                Task ignored = _balanceChecker.CheckAfterSc1DbAppliedAsync(ctx);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[0700][BALANCE_CHECK] request failed " + ex.Message);
            }
        }

        private static BuyTypeRow ReadBuyTypeRow(SQLiteConnection conn, int band, SQLiteTransaction tx = null)
        {
            return new BuyTypeRow { Qty = 0, FromBand = 0, FromQty = 0, BuyType = "" };
        }

        private static int CountHeldBandsFromDb()
        {
            using (var conn = new SQLiteConnection(Login.ConnStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM kodex200_new WHERE qty > 0";
                    object val = cmd.ExecuteScalar();
                    return val != null && val != DBNull.Value ? Convert.ToInt32(val) : 0;
                }
            }
        }

        private static void LogBuyTypeBefore(
            int decisionBand,
            int targetBand,
            int fillQty,
            int downFromBand,
            int downFromQty,
            BuyTypeRow before)
        {
            if (before == null) before = new BuyTypeRow { BuyType = "UNKNOWN" };

            Debug.WriteLine(
                "[0700][BUY_TYPE][BEFORE] " +
                $"side=BUY decisionBand={decisionBand} targetBand={targetBand} fillQty={fillQty} " +
                $"SwapRecordBandK={Login.SwapRecordBandK} downFromBand={downFromBand} downFromQty={downFromQty} " +
                $"before.qty={before.Qty} " +
                $"before.from_band={before.FromBand} before.from_qty={before.FromQty} before.extra_qty={before.ExtraQty} " +
                $"before.kind={before.BuyType}");
        }

        private static void LogBuyTypeUpdate(int targetBand, BuyTypeRow row, string buyType, string reason)
        {
            if (row == null) row = new BuyTypeRow { BuyType = buyType };

            Debug.WriteLine(
                "[0700][BUY_TYPE][UPDATE] " +
                $"targetBand={targetBand} qty={row.Qty} " +
                $"from_band={row.FromBand} from_qty={row.FromQty} extra_qty={row.ExtraQty} " +
                $"kind={buyType} reason={reason}");
        }

        private static void LogBuyTypeAfter(int targetBand, BuyTypeRow after)
        {
            if (after == null) after = new BuyTypeRow { BuyType = "UNKNOWN" };

            Debug.WriteLine(
                "[0700][BUY_TYPE][AFTER] " +
                $"targetBand={targetBand} after.qty={after.Qty} " +
                $"after.from_band={after.FromBand} after.from_qty={after.FromQty} after.extra_qty={after.ExtraQty} " +
                $"after.kind={after.BuyType}");
        }

        public void FinalizeAfterUnlock(string side)
        {
            FinalizeAfterUnlock(side, 0, 0);
        }

        public void FinalizeAfterUnlock(string side, int band)
        {
            FinalizeAfterUnlock(side, band, 0);
        }

        public void FinalizeAfterUnlock(string side, int band, double price)
        {
            FinalizeAfterUnlock(side, band, price, 0, 0, 0, 0);
        }

        public void FinalizeAfterUnlock(
            string side,
            int band,
            double price,
            int downSlideFromBand,
            int downSlideFromQty,
            int downSlideRecordBand)
        {
            FinalizeAfterUnlock(side, band, price, downSlideFromBand, downSlideFromQty, downSlideRecordBand, 0);
        }

        // ✅ [2026-07-20 P0-FIX] ordNo 추가: 이 체결완료가 2160 타임아웃을 유발한
        // 바로 그 SELL 주문인지 판별해 자동 복구를 트리거하기 위해 필요하다.
        public void FinalizeAfterUnlock(
            string side,
            int band,
            double price,
            int downSlideFromBand,
            int downSlideFromQty,
            int downSlideRecordBand,
            long ordNo)
        {
            try
            {
                side = (side ?? "").Trim();

                // ✅ Down-swap 기록은 "매수 완료" 시점에도 보강 기록
                TryRecordSwapFromBandIfNeeded(side, downSlideFromBand, downSlideFromQty, downSlideRecordBand);

                // ✅ Up-Swap BUY 완전체결이면
                // startBand row의 from_band / from_qty 를 정리한다.
                TryClearUpSwapLinkIfNeeded(side, band);

                int oldStart = Login.시작밴드변수;
                int newStart = RecalcStartBandFromDb();
                Login.시작밴드변수 = newStart;

                // ✅ 외부에서는 event 직접 Invoke 금지
                DbFuncs.RaiseKodexQtyUpdated(band, 0);

                Debug.WriteLine($"[0700] FinalizeAfterUnlock side={side} band={band} price={price} StartBand {oldStart} -> {newStart}");

                // ✅ StartBand 변경 시 textBox11(팔가격), textBox10(살가격) 갱신
                try
                {
                    if (_login != null && !_login.IsDisposed)
                    {
                        if (_login.InvokeRequired)
                        {
                            _login.BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    _login.UpdateBandPriceTextBoxes(newStart);
                                }
                                catch (Exception exUi)
                                {
                                    Debug.WriteLine("[0700] UpdateBandPriceTextBoxes ERROR " + exUi.Message);
                                }
                            }));
                        }
                        else
                        {
                            _login.UpdateBandPriceTextBoxes(newStart);
                        }
                    }
                }
                catch (Exception exUiOuter)
                {
                    Debug.WriteLine("[0700] UpdateBandPriceTextBoxes OUTER ERROR " + exUiOuter.Message);
                }

                // ✅ listView3는 갱신하지 않고 밴드 UI만 갱신
                TryReloadUiSafely();

                // ✅ 다운슬라이딩 완료 메시지: "DB 반영 + StartBand 재계산 후"
                try
                {
                    if (Login.SwapInProgress && Login.SwapFromBand > 0 && Login.SwapFromQty > 0)
                    {
                        //MessageBox.Show(
                        //    $"다운슬라이딩 완료 (DB 반영 완료)\n\n" +
                        //    $"FROM BAND : {Login.SwapFromBand}\n" +
                        //    $"FROM QTY  : {Login.SwapFromQty}\n" +
                        //    $"RECORD K  : {Login.SwapRecordBandK}\n" +
                        //    $"StartBand : {oldStart} -> {newStart}",
                        //    "DOWN SLIDING COMPLETE",
                        //    MessageBoxButtons.OK,
                        //    MessageBoxIcon.Information
                        //);
                    }
                }
                catch { }

                bool downSlideActive = false;
                bool isDownSlideBuyComplete = false;
                bool isDownSlideSellComplete = false;
                try
                {
                    downSlideActive =
                        Login.SwapInProgress &&
                        Login.SwapFromBand > 0 &&
                        Login.SwapFromQty > 0 &&
                        Login.SwapRecordBandK > 0;

                    isDownSlideBuyComplete =
                        side == "매수" &&
                        (downSlideActive ||
                         (downSlideFromBand > 0 && downSlideFromQty > 0 && downSlideRecordBand > 0));

                    isDownSlideSellComplete =
                        side == "매도" &&
                        downSlideActive;
                }
                catch
                {
                    downSlideActive = false;
                    isDownSlideBuyComplete = false;
                    isDownSlideSellComplete = false;
                }

                if (isDownSlideSellComplete)
                {
                    Debug.WriteLine("[0700] DownSlide SELL complete -> keep SwapInProgress until BUY complete");

                    // ✅ [2026-07-20 P0-FIX] AutoTradingBlocked 영구 고착 방지 안전장치.
                    // 이 SELL이 과거 2160의 WAIT_PARTIAL_PROGRESS 오판으로 타임아웃 처리되어
                    // AutoTradingBlocked=true를 유발한 바로 그 주문이라면(ordNo 일치),
                    // BUY 없이도 지금 즉시 SwapFlags/AutoTradingBlocked를 해제한다.
                    // (원인이 된 SELL이 실제로는 정상 완전체결됐으므로 더 이상 차단할 이유가 없음)
                    try
                    {
                        if (ordNo > 0 && _2160_강제슬라이딩실행.TryConsumeTimedOutSwapSellOrdNo(ordNo))
                        {
                            ClearSwapRuntimeFlags();
                            Login.AutoTradingBlocked = false;
                            Console.WriteLine("[2160][AUTO_RECOVER] ordNo=" + ordNo +
                                " 타임아웃 이후 완전체결 확인, 매수 차단 해제");
                        }
                    }
                    catch (Exception exAutoRecover)
                    {
                        Console.WriteLine("[2160][AUTO_RECOVER][EX] " + exAutoRecover.Message);
                    }
                }
                else
                {
                    ClearSwapRuntimeFlags();
                    if (isDownSlideBuyComplete)
                    {
                        try { Login.AutoTradingBlocked = false; } catch { }
                        Debug.WriteLine("[0700] DownSlide BUY complete -> clear SwapInProgress and AutoTradingBlocked=false");
                    }
                }
                bool isUpSwapSellComplete = false;
                bool isUpSwapBuyComplete = false;
                try
                {
                    isUpSwapSellComplete =
                        side == "매도" &&
                        Login.UpSwapInProgress &&
                        Login.UpSwapStartBand > 0 &&
                        band == Login.UpSwapStartBand;

                    isUpSwapBuyComplete =
                        side == "매수" &&
                        Login.UpSwapInProgress &&
                        Login.UpSwapTargetBand > 0 &&
                        band == Login.UpSwapTargetBand;
                }
                catch
                {
                    isUpSwapSellComplete = false;
                    isUpSwapBuyComplete = false;
                }

                if (isUpSwapSellComplete)
                {
                    Debug.WriteLine("[0700] UpSwap SELL complete -> keep UpSwapInProgress until BUY complete");
                }
                else if (isUpSwapBuyComplete)
                {
                    ClearUpSwapRuntimeFlags();
                    _2310_상승슬라이딩실행순서.ClearSnapshot();
                    Debug.WriteLine("[0700] UpSwap BUY complete -> clear UpSwapInProgress and snapshot");
                }
                else if (!Login.UpSwapInProgress)
                {
                    ClearUpSwapRuntimeFlags();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[0700] FinalizeAfterUnlock ERROR " + ex.Message);
            }
        }

        private int RecalcStartBandFromDb()
        {
            int result = 0;

            using (var conn = new SQLiteConnection(Login.ConnStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT MAX(band) FROM kodex200_new WHERE qty > 0";
                    var val = cmd.ExecuteScalar();
                    if (val != null && val != DBNull.Value)
                        result = Convert.ToInt32(val);
                }
            }

            return result;
        }

        private void TryReloadUiSafely()
        {
            try
            {
                if (_login == null || _login.IsDisposed) return;
                if (Login.TradeWait != null && Login.TradeWait.IsLocked)
                {
                    Debug.WriteLine("[0700] RefreshBandsAndTriggers skipped while TradeWait locked");
                    return;
                }

                if (_login.InvokeRequired)
                {
                    _login.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (Login.TradeWait != null && Login.TradeWait.IsLocked) return;
                            _login.RefreshBandsAndTriggers();
                        }
                        catch (Exception ex2)
                        {
                            Debug.WriteLine("[0700] RefreshBandsAndTriggers ERROR " + ex2.Message);
                        }
                    }));
                }
                else
                {
                    try
                    {
                        _login.RefreshBandsAndTriggers();
                    }
                    catch (Exception ex2)
                    {
                        Debug.WriteLine("[0700] RefreshBandsAndTriggers ERROR " + ex2.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[0700] TryReloadUiSafely ERROR " + ex.Message);
            }
        }

        private void TryRecordSwapFromBandIfNeeded(
            string side,
            int downSlideFromBand = 0,
            int downSlideFromQty = 0,
            int downSlideRecordBand = 0)
        {
            try
            {
                side = (side ?? "").Trim();
                if (side != "매수") return;

                bool hasOrderSnapshot =
                    downSlideFromBand > 0 &&
                    downSlideFromQty > 0 &&
                    downSlideRecordBand > 0;

                if (hasOrderSnapshot || Login.SwapInProgress)
                {
                    Debug.WriteLine("[0700][DOWN-FROM][FINALIZE-SKIP] reason=fill_apply_consumes_from_and_extra");
                    return;
                }

                if (!hasOrderSnapshot && !Login.SwapInProgress) return;

                if (!hasOrderSnapshot &&
                    (Login.SwapFromBand <= 0 || Login.SwapFromQty <= 0 || Login.SwapRecordBandK <= 0))
                {
                    ClearSwapRuntimeFlags();
                    return;
                }

                int recordBandK = hasOrderSnapshot ? downSlideRecordBand : Login.SwapRecordBandK;
                int fromBand = hasOrderSnapshot ? downSlideFromBand : Login.SwapFromBand;

                if (recordBandK <= 0 || fromBand <= 0)
                {
                    Debug.WriteLine(
                        "[0700][DOWN-FROM][SKIP] reason=finalize_invalid_link " +
                        $"band={recordBandK} fromBand={fromBand}");
                    return;
                }

                using (var conn = new SQLiteConnection(Login.ConnStr))
                {
                    conn.Open();

                    long currentQty = 0;
                    int oldFromBand = 0;
                    long oldFromQty = 0;
                    bool hasRow = false;

                    using (var cmdRead = conn.CreateCommand())
                    {
                        cmdRead.CommandText =
                            "SELECT qty, from_band, from_qty FROM kodex200_new WHERE band=@b";
                        cmdRead.Parameters.AddWithValue("@b", recordBandK);

                        using (var rd = cmdRead.ExecuteReader())
                        {
                            if (rd.Read())
                            {
                                hasRow = true;
                                currentQty = rd["qty"] != DBNull.Value ? Convert.ToInt64(rd["qty"]) : 0;
                                oldFromBand = rd["from_band"] != DBNull.Value ? Convert.ToInt32(rd["from_band"]) : 0;
                                oldFromQty = rd["from_qty"] != DBNull.Value ? Convert.ToInt64(rd["from_qty"]) : 0;
                            }
                        }
                    }

                    if (!hasRow)
                    {
                        Debug.WriteLine(
                            "[0700][DOWN-FROM][SKIP] reason=finalize_record_band_not_found " +
                            $"band={recordBandK} fromBand={fromBand}");
                        return;
                    }

                    if (oldFromBand > 0 && oldFromQty > 0)
                    {
                        Debug.WriteLine(
                            "[0700][DOWN-FROM][FINALIZE-SKIP] reason=already_recorded " +
                            $"band={recordBandK} fromBand={oldFromBand} fromQty={oldFromQty}");
                        return;
                    }

                    if (oldFromBand > 0 && oldFromBand != fromBand)
                    {
                        Debug.WriteLine(
                            "[0700][DOWN-FROM][WARN] " +
                            $"band={recordBandK} oldFromBand={oldFromBand} newFromBand={fromBand} reason=finalize_conflict");
                        return;
                    }

                    long patchFromQty = oldFromQty > 0 ? oldFromQty : currentQty;
                    if (patchFromQty <= 0)
                    {
                        Debug.WriteLine(
                            "[0700][DOWN-FROM][SKIP] reason=finalize_no_filled_qty " +
                            $"band={recordBandK} fromBand={fromBand} dbQty={currentQty}");
                        return;
                    }

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            "UPDATE kodex200_new " +
                            "SET from_band = CASE WHEN IFNULL(from_band, 0) = 0 THEN @fb ELSE from_band END, " +
                            "    from_qty = CASE WHEN IFNULL(from_qty, 0) = 0 THEN @fq ELSE from_qty END " +
                            "WHERE band=@b";
                        cmd.Parameters.AddWithValue("@fb", fromBand);
                        cmd.Parameters.AddWithValue("@fq", patchFromQty);
                        cmd.Parameters.AddWithValue("@b", recordBandK);
                        cmd.ExecuteNonQuery();
                    }

                    Debug.WriteLine(
                        "[0700][DOWN-FROM][FINALIZE-PATCH] " +
                        $"band={recordBandK} fromBand={fromBand} fromQty={patchFromQty}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SWAP] TryRecordSwapFromBandIfNeeded ERROR " + ex.Message);
            }
        }

        // =========================================================
        // ✅ Up-Swap BUY 완전체결 시
        // startBand row 의 from_band / from_qty 정리
        // =========================================================
        private void TryClearUpSwapLinkIfNeeded(string side, int band)
        {
            try
            {
                side = (side ?? "").Trim();
                if (side != "매수") return;
                if (!Login.UpSwapInProgress) return;
                if (Login.UpSwapStartBand <= 0 || Login.UpSwapTargetBand <= 0) return;
                if (band != Login.UpSwapTargetBand) return;

                int startBandRow = Login.UpSwapStartBand;
                int targetBandRow = Login.UpSwapTargetBand;
                int sourceFromBand = 0;
                long sourceFromQty = 0;
                long sourceExtraQty = 0;
                int targetFromBand = 0;
                long targetFromQty = 0;
                long targetExtraQty = 0;

                using (var conn = new SQLiteConnection(Login.ConnStr))
                {
                    conn.Open();
                    DB_Control.EnsureExtraQtyColumn(conn);
                    using (var tx = conn.BeginTransaction())
                    {
                        using (var cmdReadSource = conn.CreateCommand())
                        {
                            cmdReadSource.Transaction = tx;
                            cmdReadSource.CommandText =
                                "SELECT from_band, from_qty, extra_qty FROM kodex200_new WHERE band = @b";
                            cmdReadSource.Parameters.AddWithValue("@b", startBandRow);

                            using (var rd = cmdReadSource.ExecuteReader())
                            {
                                if (rd.Read())
                                {
                                    sourceFromBand = rd["from_band"] != DBNull.Value ? Convert.ToInt32(rd["from_band"]) : 0;
                                    sourceFromQty = rd["from_qty"] != DBNull.Value ? Convert.ToInt64(rd["from_qty"]) : 0;
                                    sourceExtraQty = rd["extra_qty"] != DBNull.Value ? Convert.ToInt64(rd["extra_qty"]) : 0;
                                }
                            }
                        }

                        using (var cmdReadTarget = conn.CreateCommand())
                        {
                            cmdReadTarget.Transaction = tx;
                            cmdReadTarget.CommandText =
                                "SELECT from_band, from_qty, extra_qty FROM kodex200_new WHERE band = @b";
                            cmdReadTarget.Parameters.AddWithValue("@b", targetBandRow);

                            using (var rd = cmdReadTarget.ExecuteReader())
                            {
                                if (rd.Read())
                                {
                                    targetFromBand = rd["from_band"] != DBNull.Value ? Convert.ToInt32(rd["from_band"]) : 0;
                                    targetFromQty = rd["from_qty"] != DBNull.Value ? Convert.ToInt64(rd["from_qty"]) : 0;
                                    targetExtraQty = rd["extra_qty"] != DBNull.Value ? Convert.ToInt64(rd["extra_qty"]) : 0;
                                }
                            }
                        }

                        if (sourceFromQty > 0 || sourceExtraQty > 0)
                        {
                            Debug.WriteLine(
                                "[CHAIN_RECOVERY][LINK_KEEP] " +
                                $"sourceBand={startBandRow} from_band={sourceFromBand} from_qty={sourceFromQty} extra_qty={sourceExtraQty}");
                            tx.Commit();
                        }
                        else
                        {
                            using (var cmd = conn.CreateCommand())
                            {
                                cmd.Transaction = tx;
                                cmd.CommandText =
                                    "UPDATE kodex200_new " +
                                    "SET from_band = 0, " +
                                    "    from_qty  = 0, " +
                                    "    extra_qty = 0 " +
                                    "WHERE band = @b";
                                cmd.Parameters.AddWithValue("@b", startBandRow);
                                cmd.ExecuteNonQuery();
                            }

                            ResetCompletedUpRecoveryBuyType(conn, tx, startBandRow);
                            tx.Commit();

                            Debug.WriteLine(
                                "[CHAIN_RECOVERY][LINK_CLEAR] " +
                                $"sourceBand={startBandRow} " +
                                $"cleared_from_band={sourceFromBand} " +
                                "cleared_from_qty=0 " +
                                "cleared_extra_qty=0");
                        }
                    }
                }

                Debug.WriteLine(
                    "[CHAIN_RECOVERY][CHAIN_KEEP] " +
                    $"targetBand={targetBandRow} " +
                    $"target_from_band={targetFromBand} " +
                    $"target_from_qty={targetFromQty} " +
                    $"target_extra_qty={targetExtraQty} " +
                    "preserved=True");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SWAP][UP] TryClearUpSwapLinkIfNeeded ERROR " + ex.Message);
            }
        }

        // =========================================================
        // ✅ Up-Swap 복구 체인 SELL 체결 반영
        // =========================================================
        private bool TryApplyUpSwapRecoverySellFill(
            string side,
            int band,
            int deltaQty,
            double price,
            long execNo,
            long ordNo,
            bool isComplete)
        {
            try
            {
                side = (side ?? "").Trim();
                if (side != "매도") return false;
                if (!Login.UpSwapInProgress) return false;
                if (Login.UpSwapStartBand <= 0) return false;
                if (band != Login.UpSwapStartBand) return false;

                int startBand = band;
                int targetBand = 0;
                long startQty = 0;
                long startFromQty = 0;
                long startQtyAfter = 0;

                using (var conn = new SQLiteConnection(Login.ConnStr))
                {
                    conn.Open();
                    DB_Control.EnsureExtraQtyColumn(conn);

                    using (var tx = conn.BeginTransaction())
                    {
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = tx;
                            cmd.CommandText =
                                "SELECT qty, from_band, from_qty " +
                                "FROM kodex200_new " +
                                "WHERE band = @b";
                            cmd.Parameters.AddWithValue("@b", startBand);

                            using (var rd = cmd.ExecuteReader())
                            {
                                if (!rd.Read()) return false;

                                startQty = rd["qty"] != DBNull.Value ? Convert.ToInt64(rd["qty"]) : 0;
                                targetBand = rd["from_band"] != DBNull.Value ? Convert.ToInt32(rd["from_band"]) : 0;
                                startFromQty = rd["from_qty"] != DBNull.Value ? Convert.ToInt64(rd["from_qty"]) : 0;
                            }
                        }

                        // [정책 변경 2026-06-23]
                        // from_qty=0이어도 from_band > 0이고 qty > 0이면
                        // 성장분 SELL이므로 복원 계속 진행한다.
                        if (targetBand <= 0)
                            return false;
                        if (startQty <= 0)
                            return false;

                        Debug.WriteLine(
                            "[CHAIN_RECOVERY][SELL_DETECTED] " +
                            $"sellBand={startBand} " +
                            $"qty={startQty} " +
                            $"from_band={targetBand} " +
                            $"from_qty={startFromQty}");

                        using (var cmd1 = conn.CreateCommand())
                        {
                            cmd1.Transaction = tx;
                            cmd1.CommandText =
                                "UPDATE kodex200_new " +
                                "SET qty = CASE WHEN (qty - @q) < 0 THEN 0 ELSE (qty - @q) END " +
                                "WHERE band = @b";
                            cmd1.Parameters.AddWithValue("@q", deltaQty);
                            cmd1.Parameters.AddWithValue("@b", startBand);
                            cmd1.ExecuteNonQuery();
                        }

                        using (var cmdReadAfter = conn.CreateCommand())
                        {
                            cmdReadAfter.Transaction = tx;
                            cmdReadAfter.CommandText =
                                "SELECT qty FROM kodex200_new WHERE band = @b";
                            cmdReadAfter.Parameters.AddWithValue("@b", startBand);

                            object v = cmdReadAfter.ExecuteScalar();
                            startQtyAfter = v != null && v != DBNull.Value ? Convert.ToInt64(v) : 0;
                        }

                        // =====================================================
                        // [정책 변경 2026-07-02] 새 정책: SELL은 sourceBand qty 감소만.
                        // targetBand qty 증가 금지. 메타 clear 금지(BUY 완료 후에만 가능).
                        // targetBand/sourceBand 메타(from_band/from_qty/extra_qty)는
                        // 모두 BUY 최종 완료 시 ApplyFillOnly에서 처리한다.
                        // =====================================================

                        tx.Commit();
                    }
                }

                string upRestoreLog =
                    "[UPSWAP_SELL_QTY_ONLY] " +
                    $"sellBand={startBand} " +
                    $"targetBand(from_band)={targetBand} " +
                    $"fillQty={deltaQty} " +
                    $"beforeSellQty={startQty} " +
                    $"afterSellQty={startQtyAfter} " +
                    "targetBandQty=unchanged " +
                    "meta=preserved_until_buy_complete";
                Console.WriteLine(upRestoreLog);
                Debug.WriteLine(upRestoreLog);

                Debug.WriteLine(
                    $"[0700][UP-RECOVERY-SELL] band={startBand} targetBand={targetBand} " +
                    $"deltaQty={deltaQty} price={price} execNo={execNo} " +
                    $"qty_before={startQty} qty_after={startQtyAfter}");

                DbFuncs.RaiseKodexQtyUpdated(startBand, startQtyAfter);

                RequestBalanceCheckAfterDbApplied(
                    side: side,
                    ordNo: ordNo,
                    execNo: execNo,
                    band: band,
                    fillQty: deltaQty,
                    price: price,
                    cumFill: isComplete ? deltaQty : deltaQty,
                    remain: isComplete ? 0 : -1,
                    isComplete: isComplete);

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[0700][UP-RECOVERY-SELL] ERROR " + ex.Message);
                return false;
            }
        }

        private void ClearSwapRuntimeFlags()
        {
            try
            {
                Login.SwapInProgress = false;
                Login.SwapFromBand = 0;
                Login.SwapFromQty = 0;
                Login.SwapExtraQty = 0;
                Login.SwapRecordBandK = 0;
            }
            catch { }
        }

        private static void ResetCompletedUpRecoveryBuyType(SQLiteConnection conn, SQLiteTransaction tx, int band)
        {
            return;
#pragma warning disable CS0162
            if (band <= 0) return;

            using (var cmd = conn.CreateCommand())
            {
                if (tx != null) cmd.Transaction = tx;
                cmd.CommandText =
                    "UPDATE kodex200_new " +
                    "SET from_qty = from_qty " +
                    "WHERE band = @b " +
                    "  AND qty = 0 " +
                    "  AND IFNULL(from_band, 0) = 0 " +
                    "  AND IFNULL(from_qty, 0) = 0 " +
                    "  AND 1 = 0";
                cmd.Parameters.AddWithValue("@b", band);
                cmd.ExecuteNonQuery();
            }
#pragma warning restore CS0162
        }

        private void ClearUpSwapRuntimeFlags()
        {
            try
            {
                Login.UpSwapInProgress = false;
                Login.UpSwapStartBand = 0;
                Login.UpSwapTargetBand = 0;
                Login.UpSwapFromBand = 0;
                Login.UpSwapFromQty = 0;
                Login.UpSwapExtraQty = 0;
            }
            catch { }
        }
    }
}
// 2026-05-18 91374
// 2026-06-23 from_band 계보 복원 정책 적용
//   - BUY 체결 시 from_qty/extra_qty 소진 제거 (CHAIN_KEEP_AFTER_BUY)
//   - SELL 체결 시 from_band > 0 이면 originBand qty 복원 (CHAIN_SELL_RESTORE)
//   - qty=0 시 from_band/from_qty/extra_qty 초기화 (CHAIN_CLOSE)
//   - TryApplyUpSwapRecoverySellFill 동일 정책 적용
//   - from_qty=0 이어도 from_band > 0 && qty > 0 이면 성장분 복원 계속
