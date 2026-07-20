// 0400_매매실행.cs  (복붙용 / C# 7.3)
// ------------------------------------------------------------
// 3모드 정합 최종본
//
// 정책:
// - TEST_MOCK  : 내부 mock / replay / SimulateFill
// - TEST_LIVE  : 데모서버 실주문 (0500/0530 경유)
// - REAL       : 실계좌 실주문 (0500/0530 경유)
//
// 핵심 수정:
// 1) 0500 반환형을 Task<long> 기준으로 사용
// 2) catch에서 재전송 절대 금지
// 3) ordNo<=0 이면 OrdMap 등록하지 않고, 이번 주문은 "실주문 미발생"으로 보고 0550 LOCK 해제
// ------------------------------------------------------------

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Exercise_1
{
    public sealed class 매매실행
    {
        // 신형(0500 경유)
        private readonly _0500_매매전송_Xing _tx0500;

        // 구형(0530 직결)
        private readonly 매매_Xing _trader0530;

        private readonly Func<string> _getShcode;

        public event Action<string> Log;

        // MOCK 전용 ordNo seed
        private static long _mockOrdSeed = DateTime.Now.Ticks % 1000000000L;
        private static readonly object _mockCashLock = new object();
        private static long? _mockOrderableCash;

        public static void SetMockOrderableCash(long cash)
        {
            lock (_mockCashLock)
            {
                _mockOrderableCash = Math.Max(0L, cash);
            }
        }

        public static void ClearMockOrderableCash()
        {
            lock (_mockCashLock) { _mockOrderableCash = null; }
        }

        public static long? GetMockOrderableCash()
        {
            lock (_mockCashLock) { return _mockOrderableCash; }
        }

        public 매매실행(_0500_매매전송_Xing tx0500, Func<string> getShcode)
        {
            _tx0500 = tx0500 ?? throw new ArgumentNullException(nameof(tx0500));
            _trader0530 = null;
            _getShcode = getShcode ?? throw new ArgumentNullException(nameof(getShcode));
        }

        public 매매실행(매매_Xing trader0530, Func<string> getShcode)
        {
            _trader0530 = trader0530 ?? throw new ArgumentNullException(nameof(trader0530));
            _tx0500 = null;
            _getShcode = getShcode ?? throw new ArgumentNullException(nameof(getShcode));
        }

        public Task ExecuteAsync(string side, int band, int qty, int price)
        {
            return ExecuteCoreAsync(side, band, qty, price);
        }

        public Task ExecuteAsync(string side, int band, int qty, long price)
        {
            int p = SafeToIntPrice(price);
            return ExecuteCoreAsync(side, band, qty, p);
        }

        public Task 실행(string side, int price, int qty, int band) => ExecuteAsync(side, band, qty, price);
        public Task 실행(string side, long price, int qty, int band) => ExecuteAsync(side, band, qty, price);

        public Task ExecuteSellAsync(int band, int qty, int price) => ExecuteAsync("매도", band, qty, price);
        public Task ExecuteSellAsync(int band, int qty, long price) => ExecuteAsync("매도", band, qty, price);

        public Task ExecuteBuyAsync(int band, int qty, int price) => ExecuteAsync("매수", band, qty, price);
        public Task ExecuteBuyAsync(int band, int qty, long price) => ExecuteAsync("매수", band, qty, price);

        private async Task ExecuteCoreAsync(string side, int band, int qty, int price)
        {
            if (string.IsNullOrWhiteSpace(side))
                throw new ArgumentNullException(nameof(side));

            side = NormalizeSide(side);

            if (side != "매수" && side != "매도")
                throw new ArgumentException($"side는 '매수' 또는 '매도'만 허용됩니다. side={side}", nameof(side));

            if (band <= 0) throw new ArgumentOutOfRangeException(nameof(band));
            if (qty <= 0) throw new ArgumentOutOfRangeException(nameof(qty));
            if (price <= 0) throw new ArgumentOutOfRangeException(nameof(price));

            string shcode = _getShcode?.Invoke()?.Trim();
            if (string.IsNullOrEmpty(shcode))
                throw new InvalidOperationException("shcode(종목코드)가 비어있습니다.");

            if (Login.TradeWait == null)
                throw new InvalidOperationException("Login.TradeWait(0550)가 null 입니다.");

            string reason;
            if (!Login.TradeWait.TryBegin(side, band, (long)qty, (long)price, out reason))
            {
                WriteLog($"[0400][주문차단(0550)] mode={_0050_Real_Test환경결정.DisplayModeName} side={side}, band={band}, reason={reason}");
                return;
            }

            WriteLog($"[0400][주문전송] mode={_0050_Real_Test환경결정.DisplayModeName} side={side}, band={band}, qty={qty}, price={price}, shcode={shcode}");

            try
            {
                // =========================================================
                // TEST_MOCK / DB replay : 내부 mock 체결
                // =========================================================
                if (_0050_Real_Test환경결정.IsTestMock || Login.IsReplayMode)
                {
                    await ExecuteMockAsync(side, band, qty, price, shcode).ConfigureAwait(false);
                    return;
                }

                // =========================================================
                // TEST_LIVE / REAL : 실제 주문
                // =========================================================
                if (_0050_Real_Test환경결정.UseLiveOrder)
                {
                    long realOrdNo = 0;

                    if (_tx0500 != null)
                    {
                        realOrdNo = await TrySendVia0500_GetOrdNoAsync(side, shcode, price, qty, band).ConfigureAwait(false);

                        if (realOrdNo > 0)
                            WriteLog($"[0400][LIVE][0500] mode={_0050_Real_Test환경결정.DisplayModeName} side={side}, band={band}, ordNo={realOrdNo}");
                        else
                            WriteLog($"[0400][LIVE][0500] mode={_0050_Real_Test환경결정.DisplayModeName} side={side}, band={band}, ordNo=(none)");
                    }
                    else if (_trader0530 != null)
                    {
                        if (side == "매수")
                        {
                            WriteLog($"[BUY_BLOCK][CASH_GUARD] mode={_0050_Real_Test환경결정.DisplayModeName} account= side={side} qty={qty} price={price} requiredCash={(long)qty * price} orderableCash=0 cashSource=NONE reason=CASH_QUERY_FAILED");
                            WriteLog("BUY 차단: 0500 현금 방어막을 경유하지 않는 0530 직접 BUY 금지");
                            realOrdNo = 0L;
                        }
                        else
                        {
                            realOrdNo = await _trader0530.SendOrderLive(side, shcode, price, qty, band).ConfigureAwait(false);
                            WriteLog($"[0400][LIVE][0530] mode={_0050_Real_Test환경결정.DisplayModeName} side={side}, band={band}, ordNo={realOrdNo}");
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException("매매실행이 0500/0530 어느 쪽도 주입되지 않았습니다.");
                    }

                    // 실제 주문번호가 있으면 관리망(OrdMap + 재기동체결복구 원장)에 등록
                    if (realOrdNo > 0)
                    {
                        if (TryRegisterOrdMap_FixedSignature(
                            realOrdNo, sideKor: side, band: band, orderQty: qty,
                            orderPrice: price, shcode: shcode))
                        {
                            NotifyScWaitStarted(realOrdNo, "LIVE");
                        }
                        else
                        {
                            try { Login.TradeWait.EndOnFailed(side, band, "order manage failed"); } catch { }
                        }
                        return;
                    }

                    // 실제 주문이 나가지 않은 케이스:
                    // - 현금부족으로 0500이 차단
                    // - 0500 내부에서 대체 경로(슬라이딩) 처리
                    // - 정책상 이번 직접 주문은 미발생이므로 원래 LOCK은 반드시 해제
                    try
                    {
                        Login.TradeWait.EndOnFailed(side, band, "ordNo<=0 (직접 주문 미발생 / 차단 / 대체처리)");
                    }
                    catch { }

                    WriteLog($"[0400][OrdMap][SKIP] mode={_0050_Real_Test환경결정.DisplayModeName} ordNo<=0 side={side}, band={band}, qty={qty}");
                    return;
                }

                throw new InvalidOperationException("알 수 없는 주문 모드입니다. RunMode=" + _0050_Real_Test환경결정.RunMode);
            }
            catch (Exception ex)
            {
                if (ex is TimeoutException)
                {
                    WriteLog($"[0400][TIMEOUT][LOCK-KEEP] side={side} band={band} qty={qty} price={price}");
                }
                else
                {
                    try { Login.TradeWait.EndOnFailed(side, band, ex.Message); } catch { }
                }

                if (IsOrderReject(ex))
                {
                    WriteLog($"[0400][주문거절] mode={_0050_Real_Test환경결정.DisplayModeName} side={side}, band={band}, msg={ex.Message}");
                }
                else
                {
                    WriteLog($"[0400][주문전송 오류] mode={_0050_Real_Test환경결정.DisplayModeName} side={side}, band={band}, ex={ex}");
                }

                throw;
            }
        }

        private async Task ExecuteMockAsync(string side, int band, int qty, int price, string shcode)
        {
            if (!TryGuardMockBuy(side, qty, price))
            {
                try { Login.TradeWait.EndOnFailed(side, band, "BUY cash guard blocked"); } catch { }
                return;
            }

            if (Login.OrdMap == null)
                throw new InvalidOperationException("Login.OrdMap is null");

            if (Login.Sc1Receiver == null)
                throw new InvalidOperationException("Login.Sc1Receiver is null");

            long ordNo = NextMockOrdNo();
            string ordNoRaw = ordNo.ToString();

            WriteLog($"[0400][MOCK] side={side}, band={band}, qty={qty}, price={price}, ordNo={ordNoRaw}");

            if (!TryRegisterOrdMap_FixedSignature(
                ordNo, sideKor: side, band: band, orderQty: qty,
                orderPrice: price, shcode: shcode))
            {
                try { Login.TradeWait.EndOnFailed(side, band, "mock order manage failed"); } catch { }
                return;
            }
            NotifyScWaitStarted(ordNo, "MOCK");

            WriteLog($"[0400][MOCK][OrdMap] Register OK side={side} band={band} ordNoRaw={ordNoRaw} qty={qty}");

            var fillPlan = _9010_ReplayPartialFillScript.BuildPlan(
                sideKor: side,
                band: band,
                orderQty: qty,
                price: price,
                shcode: shcode,
                ordNoRaw: ordNoRaw
            );

            if (fillPlan == null)
            {
                Login.Sc1Receiver.SimulateFill(
                    ordNoRaw: ordNoRaw,
                    sideKor: side,
                    band: band,
                    fillQty: qty,
                    fillPrice: price
                );

                ApplyMockFillCash(side, qty, price);

                WriteLog($"[0400][MOCK] simulate fill DONE mode=FULL emitted={qty}/{qty} side={side}, band={band}, price={price}, shcode={shcode}");
                return;
            }

            if (!fillPlan.HasAnyFill)
            {
                WriteLog($"[0400][MOCK] NO FILL mode reason={fillPlan.Reason} side={side}, band={band}, qty={qty}, price={price}, shcode={shcode}");
                return;
            }

            int emitted = 0;

            foreach (int chunk in fillPlan.FillQuantities)
            {
                if (chunk <= 0) continue;

                emitted += chunk;

                Login.Sc1Receiver.SimulateFill(
                    ordNoRaw: ordNoRaw,
                    sideKor: side,
                    band: band,
                    fillQty: chunk,
                    fillPrice: price
                );

                ApplyMockFillCash(side, chunk, price);

                if (fillPlan.DelayMsBetweenFills > 0)
                {
                    await Task.Delay(fillPlan.DelayMsBetweenFills).ConfigureAwait(false);
                }
            }

            WriteLog(
                $"[0400][MOCK] simulate fill DONE " +
                $"mode={(fillPlan.IsPartial ? "PARTIAL" : "FULL")} " +
                $"emitted={emitted}/{qty} reason={fillPlan.Reason} " +
                $"side={side}, band={band}, price={price}, shcode={shcode}");
        }

        private static long NextMockOrdNo()
        {
            return Interlocked.Increment(ref _mockOrdSeed);
        }

        private static bool IsOrderReject(Exception ex)
        {
            if (ex == null) return false;
            string m = ex.Message ?? "";

            if (m.IndexOf("주문 거절", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (m.IndexOf("lastMsg=(", StringComparison.OrdinalIgnoreCase) >= 0 &&
                m.IndexOf(",01425,", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (m.IndexOf("주문가능금액", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (m.IndexOf("01425", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (m.IndexOf("01458", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            return false;
        }

        private async Task<long> TrySendVia0500_GetOrdNoAsync(string side, string shcode, int price, int qty, int band)
        {
            // 중요:
            // - 재전송 금지
            // - 0500이 Task<long>로 반환한 ordNo를 그대로 사용
            long ordNo = await _tx0500.SendOrderAsync(side, shcode, price, qty, band).ConfigureAwait(false);
            return ordNo;
        }

        private bool TryRegisterOrdMap_FixedSignature(
            long ordNo,
            string sideKor,
            int band,
            int orderQty,
            int orderPrice,
            string shcode)
        {
            try
            {
                if (Login.OrdMap == null)
                {
                    string reason = "Login.OrdMap is null";
                    WriteOrderManageFail(ordNo, sideKor, band, orderQty, orderPrice, reason);
                    return false;
                }

                if (ordNo <= 0)
                {
                    WriteLog($"[0400][OrdMap] SKIP ordNo<=0 side={sideKor} band={band} qty={orderQty}");
                    return false;
                }

                string ordNoRaw = ordNo.ToString();

                bool ordMapRegistered = Login.OrdMap.TryRegister(sideKor, band, ordNo, orderQty);
                if (!ordMapRegistered)
                {
                    WriteOrderManageFail(ordNo, sideKor, band, orderQty, orderPrice, "OrdMap.TryRegister returned false");
                    return false;
                }

                RegisterRebuildBuyIfNeeded(ordNo, sideKor, band);

                bool recoveryInserted = TryInsertRecoveryAck(ordNo, sideKor, band, orderQty, orderPrice, shcode);
                if (!recoveryInserted)
                {
                    WriteOrderManageFail(ordNo, sideKor, band, orderQty, orderPrice, "Recovery INSERT failed");
                    return false;
                }

                WriteLog($"[0400][OrdMap] Register OK side={sideKor} band={band} ordNoRaw={ordNoRaw} qty={orderQty}");
                WriteLog("[ORDER_MANAGE][OK] ordNo=" + ordNo +
                         " side=" + NormalizeOrderSideForLog(sideKor) +
                         " band=" + band);
                return true;
            }
            catch (Exception ex)
            {
                WriteOrderManageFail(ordNo, sideKor, band, orderQty, orderPrice,
                    ex.GetBaseException().Message);
                return false;
            }
        }

        private bool TryGuardMockBuy(string side, int qty, int price)
        {
            if (side != "매수") return true;

            long requiredCash;
            try { requiredCash = checked((long)qty * (long)price); }
            catch (OverflowException)
            {
                WriteMockBuyBlock(qty, price, 0L, 0L, "ZERO_OR_INVALID_REQUIRED_CASH");
                return false;
            }

            if (qty <= 0)
            {
                WriteMockBuyBlock(qty, price, requiredCash, 0L, "ZERO_OR_INVALID_QTY");
                return false;
            }
            if (price <= 0)
            {
                WriteMockBuyBlock(qty, price, requiredCash, 0L, "ZERO_OR_INVALID_PRICE");
                return false;
            }
            if (requiredCash <= 0)
            {
                WriteMockBuyBlock(qty, price, requiredCash, 0L, "ZERO_OR_INVALID_REQUIRED_CASH");
                return false;
            }

            long? ledger;
            lock (_mockCashLock) { ledger = _mockOrderableCash; }
            if (!ledger.HasValue)
            {
                WriteMockBuyBlock(qty, price, requiredCash, 0L, "MOCK_CASH_LEDGER_MISSING");
                return false;
            }

            long cash = ledger.Value;
            string result = cash > 0 && requiredCash <= cash ? "PASS" : "BLOCK";
            WriteLog("[CASH_GUARD][MOCK_CHECK] mockOrderableCash=" + cash +
                     " requiredCash=" + requiredCash + " result=" + result);

            if (cash <= 0)
            {
                WriteMockBuyBlock(qty, price, requiredCash, cash, "ORDERABLE_CASH_ZERO");
                return false;
            }
            if (requiredCash > cash)
            {
                WriteMockBuyBlock(qty, price, requiredCash, cash, "INSUFFICIENT_CASH");
                return false;
            }
            return true;
        }

        private void WriteMockBuyBlock(int qty, int price, long requiredCash, long orderableCash, string reason)
        {
            WriteLog("[BUY_BLOCK][CASH_GUARD] mode=" + _0050_Real_Test환경결정.DisplayModeName +
                     " account=MOCK side=매수 qty=" + qty +
                     " price=" + price +
                     " requiredCash=" + requiredCash +
                     " orderableCash=" + orderableCash +
                     " cashSource=MockOrderableCash reason=" + reason);
            WriteLog("BUY 차단: " + (reason == "INSUFFICIENT_CASH" || reason == "ORDERABLE_CASH_ZERO"
                ? "주문가능현금 부족" : "MOCK 현금 원장 확인 실패"));
        }

        private void ApplyMockFillCash(string side, int fillQty, int fillPrice)
        {
            long amount = checked((long)fillQty * (long)fillPrice);
            lock (_mockCashLock)
            {
                if (side == "매수")
                {
                    if (!_mockOrderableCash.HasValue) return;
                    _mockOrderableCash = Math.Max(0L, _mockOrderableCash.Value - amount);
                }
                else if (side == "매도")
                {
                    long current = _mockOrderableCash ?? 0L;
                    _mockOrderableCash = current > long.MaxValue - amount ? long.MaxValue : current + amount;
                }

                WriteLog("[CASH_GUARD][MOCK_LEDGER] side=" + side +
                         " fillQty=" + fillQty +
                         " fillPrice=" + fillPrice +
                         " amount=" + amount +
                         " mockOrderableCash=" + (_mockOrderableCash ?? 0L));
            }
        }

        private bool TryInsertRecoveryAck(
            long ordNo,
            string sideKor,
            int band,
            int orderQty,
            int orderPrice,
            string shcode)
        {
            try
            {
                int fromBand = 0;
                long fromQty = 0;
                long extraQty = 0;
                string tradeType = sideKor == "매수" ? "일반BUY" : "일반SELL";

                var ordMap = Login.OrdMap;
                if (ordMap != null)
                {
                    int mappedFromBand;
                    long mappedFromQty;
                    long mappedExtraQty;
                    string mappedTradeType;
                    if (ordMap.TryGetRecoveryMetadata(
                        ordNo,
                        out mappedFromBand,
                        out mappedFromQty,
                        out mappedExtraQty,
                        out mappedTradeType))
                    {
                        fromBand = mappedFromBand;
                        fromQty = mappedFromQty;
                        extraQty = mappedExtraQty;
                        tradeType = mappedTradeType;
                    }
                }

                bool ok = RestartExecutionRecovery.InsertAck(
                    ordNo: ordNo,
                    shcode: shcode,
                    side: sideKor,
                    tradeType: tradeType,
                    band: band,
                    fromBand: fromBand,
                    fromQty: fromQty,
                    extraQty: extraQty,
                    orderQty: orderQty,
                    orderPrice: orderPrice);

                if (!ok)
                {
                    LogRecoveryInsertFailed(
                        ordNo, sideKor, band, orderQty, orderPrice,
                        tradeType, fromBand, fromQty, extraQty,
                        "RestartExecutionRecovery.InsertAck returned false");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                int fromBand = 0;
                long fromQty = 0;
                long extraQty = 0;
                string tradeType = sideKor == "매수" ? "일반BUY" : "일반SELL";
                LogRecoveryInsertFailed(
                    ordNo, sideKor, band, orderQty, orderPrice,
                    tradeType, fromBand, fromQty, extraQty,
                    ex.GetBaseException().Message);
                return false;
            }
        }

        private void LogRecoveryInsertFailed(
            long ordNo,
            string sideKor,
            int band,
            int orderQty,
            int orderPrice,
            string tradeType,
            long fromBand,
            long fromQty,
            long extraQty,
            string message)
        {
            WriteLog("[CRITICAL][RECOVERY_INSERT_FAILED] " +
                     "ordNo=" + ordNo +
                     " side=" + NormalizeOrderSideForLog(sideKor) +
                     " band=" + band +
                     " qty=" + orderQty +
                     " price=" + orderPrice +
                     " tradeType=" + (tradeType ?? "") +
                     " from_band=" + fromBand +
                     " from_qty=" + fromQty +
                     " extra_qty=" + extraQty +
                     " exception/message=" + ((message ?? "unknown").Replace("\r", " ").Replace("\n", " ")));

            if (IsSellSide(sideKor))
            {
                StopForUnmanageableOrder(
                    "recovery_insert_failed",
                    ordNo, sideKor, band, orderQty, orderPrice,
                    message);
            }
        }

        private void WriteOrderManageFail(
            long ordNo,
            string sideKor,
            int band,
            int orderQty,
            int orderPrice,
            string reason)
        {
            string safeReason = (reason ?? "unknown").Replace("\r", " ").Replace("\n", " ");
            WriteLog("[ORDER_MANAGE][FAIL] ordNo=" + ordNo +
                     " side=" + NormalizeOrderSideForLog(sideKor) +
                     " band=" + band +
                     " reason=" + safeReason);

            StopForUnmanageableOrder(
                "order_manage_failed",
                ordNo, sideKor, band, orderQty, orderPrice,
                safeReason);
        }

        private static void StopForUnmanageableOrder(
            string reason,
            long ordNo,
            string sideKor,
            int band,
            int orderQty,
            int orderPrice,
            string detail)
        {
            try
            {
                Login.StopAutoTradingForRestartRecovery(
                    reason,
                    ordNo,
                    band,
                    detail ?? "",
                    showPopup: true,
                    side: NormalizeOrderSideForLog(sideKor),
                    qty: orderQty,
                    price: orderPrice);
            }
            catch { }
        }

        private static bool IsSellSide(string sideKor)
        {
            string side = (sideKor ?? "").Trim();
            return side == "매도" || side.Equals("SELL", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeOrderSideForLog(string sideKor)
        {
            string side = (sideKor ?? "").Trim();
            if (side == "매수") return "BUY";
            if (side == "매도") return "SELL";
            return side;
        }

        private void RegisterRebuildBuyIfNeeded(long ordNo, string sideKor, int orderBand)
        {
            try
            {
                if ((sideKor ?? "").Trim() != "매수") return;

                var fullClear = Login.FullClearAfter80;
                var ordMap = Login.OrdMap;
                if (fullClear == null || ordMap == null) return;

                int lastSoldBand;
                int sellDecisionBand;
                int rebuildStartBand;
                System.Collections.Generic.List<int> targetBands;
                string distributionMode;
                if (fullClear.TryGetPendingRebuildBuyMeta(
                    orderBand,
                    out lastSoldBand,
                    out sellDecisionBand,
                    out rebuildStartBand,
                    out targetBands,
                    out distributionMode))
                {
                    ordMap.MarkRebuildBuy(ordNo, lastSoldBand, sellDecisionBand, rebuildStartBand, targetBands, distributionMode);
                    WriteLog("[0400][REBUILD] OrdMap metadata saved ordNo=" + ordNo +
                             " lastSoldBand=" + lastSoldBand +
                             " sellDecisionBand=" + sellDecisionBand +
                             " rebuildStartBand=" + rebuildStartBand +
                             " distributionMode=" + distributionMode +
                             " targetBands=" + string.Join(",", targetBands));
                }
            }
            catch (Exception ex)
            {
                WriteLog("[0400][REBUILD][META][ERR] ordNo=" + ordNo + " " + ex.Message);
            }
        }

        private void NotifyScWaitStarted(long ordNo, string source)
        {
            try
            {
                LoginFormAccessor.TryGetLogin()?.SetScWaitStatus(true, ordNo, 0, source);
                WriteLog("[0800][SC_WAIT] textbox15=SC 기다리는중 ordNo=" + ordNo);
            }
            catch (Exception ex)
            {
                WriteLog("[0800][SC_WAIT][ERR] " + ex.Message);
            }
        }

        private static int SafeToIntPrice(long price)
        {
            if (price <= 0) throw new ArgumentOutOfRangeException(nameof(price));
            if (price > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(price), "price가 int 범위를 초과했습니다.");
            return (int)price;
        }

        private static string NormalizeSide(string side)
        {
            if (string.IsNullOrWhiteSpace(side)) return side;
            side = side.Trim();

            if (string.Equals(side, "BUY", StringComparison.OrdinalIgnoreCase)) return "매수";
            if (string.Equals(side, "SELL", StringComparison.OrdinalIgnoreCase)) return "매도";

            return side;
        }

        private void WriteLog(string msg)
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
// 2026-03-18 51704
// 2026-03-18 48172
