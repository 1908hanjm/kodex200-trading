using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Exercise_1
{
    public sealed class _0500_매매전송_Xing : IDisposable
    {
        private readonly 매매_Xing _trader;
        private readonly Func<string> _getAcntNo;
        private readonly Func<string> _getPwd4;
        private readonly Func<_1000_현금주문가능금액> _getCash1000;

        private bool _disposed;

        public event Action<string> Log;

        public _0500_매매전송_Xing(
            매매_Xing trader,
            Func<string> getAcntNo,
            Func<string> getPwd4,
            Func<_1000_현금주문가능금액> getCash1000)
        {
            _trader = trader ?? throw new ArgumentNullException(nameof(trader));
            _getAcntNo = getAcntNo ?? throw new ArgumentNullException(nameof(getAcntNo));
            _getPwd4 = getPwd4 ?? throw new ArgumentNullException(nameof(getPwd4));
            _getCash1000 = getCash1000 ?? throw new ArgumentNullException(nameof(getCash1000));
        }

        public async Task<long> SendOrderAsync(string sideKor, string shcode, int price, int qty, int band)
        {
            if (_disposed) return 0L;

            sideKor = NormalizeSideKorOnly(sideKor);
            shcode = SafeTrim(shcode);

            if (string.IsNullOrEmpty(shcode))
                throw new ArgumentException("shcode가 비어 있습니다.", nameof(shcode));

            if (sideKor == "매수" && qty <= 0)
            {
                WriteBuyBlock("", sideKor, qty, price, 0L, 0L, "VALIDATION", "ZERO_OR_INVALID_QTY");
                return 0L;
            }

            if (sideKor == "매수" && price <= 0)
            {
                WriteBuyBlock("", sideKor, qty, price, 0L, 0L, "VALIDATION", "ZERO_OR_INVALID_PRICE");
                return 0L;
            }

            if (qty <= 0) throw new ArgumentOutOfRangeException(nameof(qty));
            if (price <= 0) throw new ArgumentOutOfRangeException(nameof(price));

            if (band <= 0)
                throw new ArgumentOutOfRangeException(nameof(band));

            // BUY 전송 직전 공통 현금 방어막. REAL/TEST_LIVE 모두 최신
            // CSPAQ12200 MnyOrdAbleAmt만 사용하며 Dps/D1Dps/D2Dps/캐시는 fallback이 아니다.
            if (sideKor == "매수")
            {
                long requiredCash;
                try { requiredCash = checked((long)qty * (long)price); }
                catch (OverflowException)
                {
                    WriteBuyBlock("", sideKor, qty, price, 0L, 0L, "VALIDATION", "ZERO_OR_INVALID_REQUIRED_CASH");
                    return 0L;
                }

                if (requiredCash <= 0)
                {
                    WriteBuyBlock("", sideKor, qty, price, requiredCash, 0L, "VALIDATION", "ZERO_OR_INVALID_REQUIRED_CASH");
                    return 0L;
                }

                bool inSlide = Login.SwapInProgress || Login.SlideInProgress || Login.UpSwapInProgress;
                bool inRecovery =
                    Login.AutoTradingBlocked; // 복구 보류 상태와 겹칠 가능성이 높아 로그용으로만 표시

                // ==================================================
                // 마지막 방어선:
                // 일반 BUY가 10밴드 초과를 만들려 하면 TEST라도 차단
                //
                // 전제:
                // - 일반 BUY의 band는 decisionBand(K)
                // - 실제 신규 보유 밴드는 K+1
                // - 슬라이딩/복구/업슬라이딩 진행 중인 BUY는 여기서 막지 않음
                // ==================================================
                if (!inSlide)
                {
                    try
                    {
                        int targetBuyBand = band + 1;

                        var heldBands = Login.BandList
                            .Where(x => x != null && x.Qty > 0)
                            .Select(x => x.Band)
                            .ToList();

                        int heldCount = heldBands.Count;
                        bool targetAlreadyHeld = heldBands.Contains(targetBuyBand);

                        if (heldCount >= 10 && !targetAlreadyHeld)
                        {
                            Write("[0500][GUARD][STOP] general BUY would exceed fixed 10 holdings " +
                                  "band(K)=" + band +
                                  " targetBuyBand=" + targetBuyBand +
                                  " heldCount=" + heldCount +
                                  " inSlide=" + inSlide +
                                  " inRecovery=" + inRecovery +
                                  " -> return 0");
                            if (밴드매칭.TryStopChainOnOrderBlock("GUARD_10BAND"))
                                Write("[CHAIN_CLEAR] reason=GUARD_10BAND");
                            return 0L;
                        }
                    }
                    catch (Exception exGuard)
                    {
                        Write("[0500][GUARD][EX] " + exGuard.Message + " -> continue");
                    }
                }

                string acnt = SafeTrim(_getAcntNo());
                string pwd4 = SafeTrim(_getPwd4());
                const string cashSource = "CSPAQ12200.MnyOrdAbleAmt";

                if (string.IsNullOrEmpty(acnt) || string.IsNullOrEmpty(pwd4))
                {
                    WriteBuyBlock(acnt, sideKor, qty, price, requiredCash, 0L, cashSource, "CASH_QUERY_FAILED");
                    return 0L;
                }

                var cash1000 = _getCash1000();
                if (cash1000 == null)
                {
                    WriteBuyBlock(acnt, sideKor, qty, price, requiredCash, 0L, cashSource, "CASH_QUERY_FAILED");
                    return 0L;
                }

                // ─────────────────────────────────────────────────────────
                // [P1] Fresh cache 우선 사용
                // 0300이 BUY 계산 직전에 CSPAQ12200 조회를 완료한 경우,
                // 0500 진입 시점은 수백 ms 이내이므로 Throttle(10초)에 막힌다.
                // LastResult 가 10초 이내이면 재조회 없이 그 값을 사용한다.
                // ─────────────────────────────────────────────────────────
                const int FRESH_THRESHOLD_MS = 10000;
                OrderableCashQueryResult cashResult = null;

                bool usedCache = false;
                double cacheAgoMs = -1;
                long cachedOrderableCash = 0L;
                try
                {
                    var lastResult = cash1000.LastResult;
                    var lastAt     = cash1000.LastResultAt;
                    double agoMs   = (DateTime.UtcNow - lastAt).TotalMilliseconds;
                    cacheAgoMs = agoMs;
                    cachedOrderableCash = lastResult == null ? 0L : lastResult.OrderableCash;

                    if (lastResult != null &&
                        lastResult.CanCalculateBuyQty &&
                        agoMs >= 0 &&
                        agoMs < FRESH_THRESHOLD_MS)
                    {
                        cashResult = lastResult;
                        usedCache  = true;
                        Write("[CASH_GUARD][CACHE_HIT] source=1000_LOCAL agoMs=" + (int)agoMs +
                              " thresholdMs=" + FRESH_THRESHOLD_MS +
                              " cachedOrderableCash=" + lastResult.OrderableCash +
                              " requiredCash=" + requiredCash +
                              " side=" + sideKor +
                              " band=" + band +
                              " qty=" + qty +
                              " price=" + price +
                              " → 재조회 생략");
                    }
                }
                catch (Exception exCache)
                {
                    Write("[CASH_GUARD][CACHE_CHECK_EX] " + exCache.Message + " → 실조회 진행");
                }

                // ✅ [P1-FIX 2026-07-20] cash1000(1000_현금주문가능금액)의 로컬 캐시가 MISS여도,
                // 0900.QueryAllAsync가 방금 CSPAQ12200을 조회해 성공했다면 그 결과가
                // Cspaq12200SharedCache에 남아 있으므로 재조회 없이 재사용한다.
                if (!usedCache)
                {
                    long sharedCash;
                    double sharedAgoMs;
                    if (Cspaq12200SharedCache.TryGetFresh(FRESH_THRESHOLD_MS, out sharedCash, out sharedAgoMs))
                    {
                        cashResult = OrderableCashQueryResult.From(
                            sharedCash > 0 ? OrderableCashResultKind.Success : OrderableCashResultKind.ActualZeroCash,
                            sharedCash, 0, "CACHED_FROM_0900");
                        usedCache = true;
                        cacheAgoMs = sharedAgoMs;
                        cachedOrderableCash = sharedCash;

                        Write("[CASH_GUARD][CACHE_HIT] source=0900_SHARED agoMs=" + (int)sharedAgoMs +
                              " thresholdMs=" + FRESH_THRESHOLD_MS +
                              " cachedOrderableCash=" + sharedCash +
                              " requiredCash=" + requiredCash +
                              " side=" + sideKor +
                              " band=" + band +
                              " qty=" + qty +
                              " price=" + price +
                              " → 재조회 생략");
                    }
                }

                if (!usedCache)
                {
                    Write("[CASH_GUARD][CACHE_MISS] agoMs=" + (int)cacheAgoMs +
                          " thresholdMs=" + FRESH_THRESHOLD_MS +
                          " cachedOrderableCash=" + cachedOrderableCash +
                          " requiredCash=" + requiredCash +
                          " side=" + sideKor +
                          " band=" + band +
                          " qty=" + qty +
                          " price=" + price +
                          " → 실조회 진행");

                    // ✅ [P0-FIX] CSPAQ12200 Throttle(10초 최소간격)로 QueryFailed가 나면
                    // t0424([T0424][RETRY])와 동일하게 짧게 재시도한 뒤에만 매수 체인을 차단한다.
                    // 기존 버그: Throttle=QueryFailed를 즉시 CASH_QUERY_FAILED로 취급해
                    // 재시도 없이 체인을 STOP시켰음.
                    //
                    // ✅ [P2-FIX 2026-07-20] 기존 1.5초×2회=3초는 CSPAQ12200 스로틀 간격
                    // (Cspaq12200GlobalGate.MinIntervalMs=10초)보다 훨씬 짧아 구조적으로
                    // 통과가 불가능했음(t0424 재시도 파라미터를 검증 없이 그대로 이식한 것으로 추정,
                    // 로그근거: 2026-07-20 13:37:24~27 lastRequestAgoMs 442→1961→3479 로
                    // 10000에 전혀 도달하지 못하고 CASH_QUERY_FAILED 발생).
                    // 재시도 횟수를 스로틀 간격 기준으로 계산해 총 대기시간이 10초를 확실히 넘기도록 한다.
                    const int CASH_GUARD_RETRY_DELAY_MS = 1500;
                    int CASH_GUARD_MAX_RETRY = (int)Math.Ceiling(
                        (double)Cspaq12200GlobalGate.MinIntervalMs / CASH_GUARD_RETRY_DELAY_MS);

                    for (int attempt = 0; attempt <= CASH_GUARD_MAX_RETRY; attempt++)
                    {
                        if (attempt > 0)
                        {
                            Write("[CASH_GUARD][RETRY] " + CASH_GUARD_RETRY_DELAY_MS +
                                  "ms 후 재조회 attempt=" + attempt + "/" + CASH_GUARD_MAX_RETRY);
                            await Task.Delay(CASH_GUARD_RETRY_DELAY_MS).ConfigureAwait(false);
                        }

                        try
                        {
                            cashResult = await cash1000.RequestDetailedAsync(
                                acnt, pwd4, 2000, false,
                                "0500_ORDER_CASH_GUARD",
                                "0500_매매전송_Xing").ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Write("[CASH_GUARD][QUERY_EX] " + ex.Message);
                            cashResult = null;
                        }

                        // Throttle/AlreadyRunning 등 일시적 QueryFailed만 재시도.
                        // RateLimited(rc=-21 쿨다운)/NotLoggedIn 등은 즉시 재시도해도 성공 확률이 없으므로 재시도하지 않는다.
                        if (cashResult != null && cashResult.Kind != OrderableCashResultKind.QueryFailed)
                            break;
                    }
                }

                if (cashResult == null || !cashResult.CanCalculateBuyQty)
                {
                    Write("[CASH_GUARD][QUERY_FAIL] kind=" + (cashResult == null ? "NULL" : cashResult.Kind.ToString()) +
                          " msg=" + (cashResult == null ? "" : cashResult.Message));
                    WriteBuyBlock(acnt, sideKor, qty, price, requiredCash, 0L, cashSource, "CASH_QUERY_FAILED");
                    return 0L;
                }

                long orderable = cashResult.OrderableCash;
                string resultText = orderable > 0 && requiredCash <= orderable ? "PASS" : "BLOCK";
                Write("[CASH_GUARD][CHECK] mode=" + _0050_Real_Test환경결정.DisplayModeName +
                      " MnyOrdAbleAmt=" + orderable +
                      " Dps=" + cashResult.Dps +
                      " D1Dps=" + cashResult.D1Dps +
                      " D2Dps=" + cashResult.D2Dps +
                      " requiredCash=" + requiredCash +
                      " result=" + resultText +
                      " inSlide=" + inSlide +
                      " inRecovery=" + inRecovery);

                if (orderable <= 0)
                {
                    WriteBuyBlock(acnt, sideKor, qty, price, requiredCash, orderable, cashSource, "ORDERABLE_CASH_ZERO");
                    return 0L;
                }

                if (requiredCash > orderable)
                {
                    WriteBuyBlock(acnt, sideKor, qty, price, requiredCash, orderable, cashSource, "INSUFFICIENT_CASH");
                    return 0L;
                }

                await WriteBuyCashSnapshotBeforeSendAsync(
                    cash1000, acnt, pwd4, shcode, band, qty, price, requiredCash, orderable)
                    .ConfigureAwait(false);
            }

            // ==================================================
            // 실제 주문 전송(0530 매매_Xing에 위임)
            // ==================================================
            try
            {
                long ordNo = await _trader
                    .SendOrderLive(sideKor, shcode, price, qty, band)
                    .ConfigureAwait(false);

                if (ordNo > 0)
                {
                    Write("[0500][SEND][OK] side=" + sideKor +
                          " band=" + band +
                          " qty=" + qty +
                          " price=" + price +
                          " ordNo=" + ordNo);
                }
                else
                {
                    Write("[0500][SEND][WARN] side=" + sideKor +
                          " band=" + band +
                          " qty=" + qty +
                          " price=" + price +
                          " ordNo<=0");

                    if (sideKor == "매수" && 밴드매칭.TryStopChainOnOrderBlock("ORDNO_ZERO_AFTER_SEND"))
                        Write("[CHAIN_CLEAR] reason=ORDNO_ZERO_AFTER_SEND");
                }

                return ordNo;
            }
            catch (Exception ex)
            {
                Write("[0500][SEND][EX] " + ex.Message);
                throw;
            }
        }

        private static string NormalizeSideKorOnly(string sideKor)
        {
            sideKor = (sideKor ?? "").Trim();

            if (string.Equals(sideKor, "BUY", StringComparison.OrdinalIgnoreCase)) return "매수";
            if (string.Equals(sideKor, "SELL", StringComparison.OrdinalIgnoreCase)) return "매도";

            if (sideKor == "매수" || sideKor == "매도") return sideKor;

            throw new ArgumentException(
                "sideKor는 '매수' 또는 '매도'만 허용됩니다. sideKor='" + sideKor + "'",
                nameof(sideKor));
        }

        private static string SafeTrim(string s)
        {
            return string.IsNullOrWhiteSpace(s) ? "" : s.Trim();
        }

        private static bool IsCspaq12200RateLimitedMessage(string message)
        {
            message = message ?? "";
            return message.IndexOf("RateLimitCooldown", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("rc=-21", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("-21", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("전송제한", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private async Task WriteBuyCashSnapshotBeforeSendAsync(
            _1000_현금주문가능금액 cash1000,
            string account,
            string pwd4,
            string shcode,
            int band,
            int qty,
            int price,
            long orderAmount,
            long ordAbleCashUsedByProgram)
        {
            const string caller = "0500.SendOrderAsync";
            const string side = "BUY";
            string tradeType = ResolveBuyTradeType();
            string mode = ResolveModeName();
            OrderableCashQueryResult snapshot = null;
            string result = "ERROR";
            string error = "";

            try
            {
                snapshot = await cash1000.RequestDetailedAsync(
                    account, pwd4, 2000, false,
                    "0500_BUY_CASH_SNAPSHOT_BEFORE_SEND",
                    caller).ConfigureAwait(false);

                if (snapshot != null && snapshot.CanCalculateBuyQty)
                {
                    result = "OK";
                    WriteBuyCashSnapshotLog(
                        "[BUY_CASH_SNAPSHOT][BEFORE_SEND]",
                        caller, tradeType, side, band, qty, price, orderAmount,
                        ordAbleCashUsedByProgram, snapshot, account, shcode, mode, result, error);
                    return;
                }

                error = snapshot == null
                    ? "NULL_RESULT"
                    : snapshot.Kind + ":" + (snapshot.Message ?? "");
            }
            catch (Exception ex)
            {
                error = ex.Message ?? ex.ToString();
            }

            WriteBuyCashSnapshotLog(
                "[BUY_CASH_SNAPSHOT][ERROR]",
                caller, tradeType, side, band, qty, price, orderAmount,
                ordAbleCashUsedByProgram, snapshot, account, shcode, mode, result, error);
        }

        private void WriteBuyCashSnapshotLog(
            string tag,
            string caller,
            string tradeType,
            string side,
            int band,
            int qty,
            int price,
            long orderAmount,
            long ordAbleCashUsedByProgram,
            OrderableCashQueryResult snapshot,
            string account,
            string shcode,
            string mode,
            string result,
            string error)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

            long mnyOrdAbleAmt = snapshot == null ? 0L : snapshot.OrderableCash;
            long rcvblUablOrdAbleAmt = snapshot == null ? 0L : snapshot.RcvblUablOrdAbleAmt;
            long mgnRat100OrdAbleAmt = snapshot == null ? 0L : snapshot.MgnRat100OrdAbleAmt;
            long mgnRat100pctOrdAbleAmt = snapshot == null ? 0L : snapshot.MgnRat100pctOrdAbleAmt;
            long dps = snapshot == null ? 0L : snapshot.Dps;
            long d1Dps = snapshot == null ? 0L : snapshot.D1Dps;
            long d2Dps = snapshot == null ? 0L : snapshot.D2Dps;

            Write(tag + Environment.NewLine +
                  "time=" + timestamp + Environment.NewLine +
                  "caller=" + caller + Environment.NewLine +
                  "tradeType=" + tradeType + Environment.NewLine +
                  "side=" + side + Environment.NewLine +
                  "band=" + band + Environment.NewLine +
                  "orderQty=" + qty + Environment.NewLine +
                  "orderPrice=" + price + Environment.NewLine +
                  "orderAmount=" + orderAmount + Environment.NewLine +
                  "ordAbleCash_used_by_program=" + ordAbleCashUsedByProgram + Environment.NewLine +
                  "MnyOrdAbleAmt=" + mnyOrdAbleAmt + Environment.NewLine +
                  "RcvblUablOrdAbleAmt=" + rcvblUablOrdAbleAmt + Environment.NewLine +
                  "MgnRat100OrdAbleAmt=" + mgnRat100OrdAbleAmt + Environment.NewLine +
                  "MgnRat100pctOrdAbleAmt=" + mgnRat100pctOrdAbleAmt + Environment.NewLine +
                  "Dps=" + dps + Environment.NewLine +
                  "D1Dps=" + d1Dps + Environment.NewLine +
                  "D2Dps=" + d2Dps + Environment.NewLine +
                  "account=" + SafeTrim(account) + Environment.NewLine +
                  "shcode=" + SafeTrim(shcode) + Environment.NewLine +
                  "mode=" + mode +
                  (string.IsNullOrEmpty(error) ? "" : Environment.NewLine + "error=" + error));

            AppendBuyCashSnapshotCsv(
                timestamp, caller, tradeType, side, band, qty, price, orderAmount,
                ordAbleCashUsedByProgram, mnyOrdAbleAmt, rcvblUablOrdAbleAmt,
                mgnRat100OrdAbleAmt, mgnRat100pctOrdAbleAmt, dps, d1Dps, d2Dps,
                account, shcode, mode, result, error);
        }

        private static void AppendBuyCashSnapshotCsv(
            string timestamp,
            string caller,
            string tradeType,
            string side,
            int band,
            int qty,
            int price,
            long orderAmount,
            long ordAbleCashUsed,
            long mnyOrdAbleAmt,
            long rcvblUablOrdAbleAmt,
            long mgnRat100OrdAbleAmt,
            long mgnRat100pctOrdAbleAmt,
            long dps,
            long d1Dps,
            long d2Dps,
            string account,
            string shcode,
            string mode,
            string result,
            string error)
        {
            try
            {
                string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                Directory.CreateDirectory(logDir);

                string path = Path.Combine(
                    logDir,
                    "buy_cash_snapshot_" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".csv");

                bool writeHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
                var sb = new StringBuilder();
                if (writeHeader)
                {
                    sb.AppendLine("timestamp,caller,tradeType,side,band,orderQty,orderPrice,orderAmount,ordAbleCashUsed,MnyOrdAbleAmt,RcvblUablOrdAbleAmt,MgnRat100OrdAbleAmt,MgnRat100pctOrdAbleAmt,Dps,D1Dps,D2Dps,account,shcode,mode,result,error");
                }

                sb.Append(Csv(timestamp)).Append(',')
                  .Append(Csv(caller)).Append(',')
                  .Append(Csv(tradeType)).Append(',')
                  .Append(Csv(side)).Append(',')
                  .Append(band.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(qty.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(price.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(orderAmount.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(ordAbleCashUsed.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(mnyOrdAbleAmt.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(rcvblUablOrdAbleAmt.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(mgnRat100OrdAbleAmt.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(mgnRat100pctOrdAbleAmt.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(dps.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(d1Dps.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(d2Dps.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(Csv(account)).Append(',')
                  .Append(Csv(shcode)).Append(',')
                  .Append(Csv(mode)).Append(',')
                  .Append(Csv(result)).Append(',')
                  .Append(Csv(error)).AppendLine();

                File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
            }
        }

        private static string Csv(string value)
        {
            value = value ?? "";
            if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
                return value;

            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static string ResolveBuyTradeType()
        {
            try
            {
                if (Login.UpSwapInProgress) return "상승슬라이딩BUY";
                if (Login.SlideInProgress) return "슬라이딩BUY";
                if (Login.SwapInProgress) return "스왑BUY";
            }
            catch
            {
            }

            return "일반BUY";
        }

        private static string ResolveModeName()
        {
            try
            {
                string mode = _0050_Real_Test환경결정.DisplayModeName;
                if (!string.IsNullOrWhiteSpace(mode)) return mode.Trim();
            }
            catch
            {
            }

            try
            {
                if (_0050_Real_Test환경결정.IsReal) return "REAL";
                if (_0050_Real_Test환경결정.IsTestLive) return "TEST_LIVE";
                if (_0050_Real_Test환경결정.IsTestMock) return "TEST_MOCK";
            }
            catch
            {
            }

            return "UNKNOWN";
        }

        private void WriteBuyBlock(
            string account,
            string side,
            int qty,
            int price,
            long requiredCash,
            long orderableCash,
            string cashSource,
            string reason)
        {
            Write("[BUY_BLOCK][CASH_GUARD] " +
                  "mode=" + _0050_Real_Test환경결정.DisplayModeName +
                  " account=" + SafeTrim(account) +
                  " side=" + side +
                  " qty=" + qty +
                  " price=" + price +
                  " requiredCash=" + requiredCash +
                  " orderableCash=" + orderableCash +
                  " cashSource=" + cashSource +
                  " reason=" + reason);
            Write("BUY 차단: " + (reason == "INSUFFICIENT_CASH" || reason == "ORDERABLE_CASH_ZERO"
                ? "주문가능현금 부족"
                : "현금 안전검사 실패"));

            // 0500에서 ordNo=0을 반환하면 체결 경로가 실행되지 않으므로
            // 체인 소유자인 밴드매칭을 통해 활성 체인을 종료한다.
            try
            {
                if (밴드매칭.TryStopChainOnOrderBlock(reason))
                    Write("[CHAIN_CLEAR] BUY_BLOCK(" + reason + ") -> active chain stopped");
            }
            catch (Exception exChain)
            {
                Write("[CHAIN_CLEAR][EX] " + exChain.Message);
            }
        }

        private void Write(string msg)
        {
            try
            {
                Console.WriteLine(msg);
                Debug.WriteLine(msg);
                Log?.Invoke(msg);
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }
}
// 2026-04-22 41853
