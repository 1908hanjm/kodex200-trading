// 0300_밴드매칭.cs  (복붙용 / C# 7.3)
// ------------------------------------------------------------
// 역할:
// - Tick_Process(0250)가 전달한 FIRE 정보를 "거래 요청"으로 변환
// - ✅ 이번 수정(핵심): "배치 실행"을 도입하여, 한 FIRE에 대해 0300은 1번만 호출된다.
//   - 배치 내부에서 밴드별 주문을 "순차"로 전송한다.
//   - 배치 실행 중에는 다른 배치 실행을 전부 차단한다(전역 1건 제한).
//
// - ✅ 실제 주문 전송: LoginFormAccessor.TryGetExec() → 매매실행.ExecuteAsync()
//
// BUY 규칙(확정):
// - BUY FIRE가 밴드 K에서 발생하면
//      주문수량 = band K의 Sina
//      체결 후 Qty 반영 = band (K+1)  (0700에서 처리)
//
// SELL 규칙(현재 유지):
// - SELL 수량은 해당 밴드 Qty
//
// 주의:
// - "배치"는 큐가 아니라, from~to 범위를 한 Task에서 for로 순차 실행하는 구조다.
// - 0550은 여전히 side:band 단위로 중복 방지/대기 상태를 관리한다.
// - 배치 차단은 0300에서 별도 전역 게이트로 처리한다.
// ------------------------------------------------------------

using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Exercise_1
{
    public static class 밴드매칭
    {
        private const string SIDE_BUY = "BUY";
        private const string SIDE_SELL = "SELL";

        // ✅ 전역 배치 게이트(동시 배치 실행 완전 차단)
        private static readonly SemaphoreSlim _batchGate = new SemaphoreSlim(1, 1);

        /// <summary>
        /// (구버전 호환용) 단일 밴드 실행 엔트리
        /// - 이제 0250에서는 사용하지 말고, 실행배치()만 호출하는 것을 권장한다.
        /// </summary>
        public static void 실행(string side, long firePrice, int endBand, int startBand)
        {
            if (string.IsNullOrWhiteSpace(side))
            {
                Debug.WriteLine("[0300] side null/empty");
                return;
            }

            side = side.Trim().ToUpperInvariant();

            if (startBand <= 0 || endBand <= 0)
            {
                Debug.WriteLine($"[0300] invalid bands start={startBand} end={endBand}");
                return;
            }

            // 구버전도 내부적으로 배치로 한번 감싸서 "전역 차단 + 순차"를 유지
            실행배치(side, firePrice, startBand, endBand, startBandNow: startBand);
        }

        /// <summary>
        /// ✅ NEW: 배치 실행 (핵심)
        /// - side: "BUY" or "SELL"
        /// - fromBand/toBand: BUY는 from<=to (오름차순), SELL은 from>=to (내림차순)
        /// - startBandNow: 호출 시점의 시작밴드(로그 표기용)
        /// </summary>
        public static void 실행배치(string side, long firePrice, int fromBand, int toBand, int startBandNow)
        {
            if (string.IsNullOrWhiteSpace(side))
            {
                Debug.WriteLine("[0300.BATCH] side null/empty");
                return;
            }

            side = side.Trim().ToUpperInvariant();

            if (Login.BandList == null || Login.BandList.Count == 0)
            {
                Debug.WriteLine("[0300.BATCH] Login.BandList 비어 있음");
                return;
            }

            if (fromBand <= 0 || toBand <= 0)
            {
                Debug.WriteLine($"[0300.BATCH] invalid band range from={fromBand} to={toBand}");
                return;
            }

            // Exec 확보
            var exec = LoginFormAccessor.TryGetExec();
            if (exec == null)
            {
                Console.WriteLine("[0300.BATCH] Exec is null (Login.Exec not ready) -> skip send");
                return;
            }

            // 배치 작업을 1개 Task에서 순차 실행
            Task.Run(async () =>
            {
                // ✅ 전역 배치 게이트: 배치 중 다른 배치는 진입 불가
                if (!await TryEnterBatchGateAsync().ConfigureAwait(false))
                {
                    Console.WriteLine($"[0300.BATCH] SKIP (another batch running) side={side} range={fromBand}->{toBand} firePrice={firePrice:#,0}");
                    return;
                }

                try
                {
                    bool isBuy = (side == SIDE_BUY);
                    bool isSell = (side == SIDE_SELL);

                    if (!isBuy && !isSell)
                    {
                        Console.WriteLine($"[0300.BATCH] Unknown side='{side}'");
                        return;
                    }

                    // 방향/범위 검사
                    if (isBuy && fromBand > toBand)
                    {
                        Console.WriteLine($"[0300.BATCH] BUY range invalid from={fromBand} to={toBand}");
                        return;
                    }
                    if (isSell && fromBand < toBand)
                    {
                        Console.WriteLine($"[0300.BATCH] SELL range invalid from={fromBand} to={toBand}");
                        return;
                    }

                    // SEG 로그(배치 전체 1회)
                    int lowerBand = Math.Min(fromBand, toBand);
                    int upperBand = Math.Max(fromBand, toBand);

                    int count = Login.BandList.Count(b => b != null && b.Band >= lowerBand && b.Band <= upperBand);

                    Console.WriteLine(
                        $"[밴드매칭.BATCH] side={side} count~={count} order={(isBuy ? "ASC" : "DESC")} range={lowerBand}~{upperBand} " +
                        $"firePrice={firePrice:#,0} startBandNow={startBandNow}"
                    );

                    if (isBuy)
                    {
                        // BUY: from -> to (ASC)
                        for (int b = fromBand; b <= toBand; b++)
                        {
                            bool ok = await ExecuteBuy_OneAsync(exec, firePrice, decisionBandK: b, startBandNow: startBandNow).ConfigureAwait(false);
                            if (!ok)
                            {
                                // 실패해도 다음 밴드 계속 진행(정책 선택)
                                // 원하면 break로 바꿀 수 있음.
                            }
                        }
                    }
                    else
                    {
                        // SELL: from -> to (DESC)
                        for (int b = fromBand; b >= toBand; b--)
                        {
                            bool ok = await ExecuteSell_OneAsync(exec, firePrice, decisionBand: b, startBandNow: startBandNow).ConfigureAwait(false);
                            if (!ok)
                            {
                                // 실패해도 다음 밴드 계속 진행
                            }
                        }
                    }

                    Console.WriteLine($"[밴드매칭.BATCH] DONE side={side} range={fromBand}->{toBand}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[0300.BATCH] EX " + ex);
                }
                finally
                {
                    try { _batchGate.Release(); } catch { }
                }
            });
        }

        private static async Task<bool> TryEnterBatchGateAsync()
        {
            try
            {
                // 즉시 진입 시도: 이미 배치 돌고 있으면 스킵
                // (Wait(0) 대신 async 형태로 구현)
                var entered = await _batchGate.WaitAsync(0).ConfigureAwait(false);
                return entered;
            }
            catch
            {
                return false;
            }
        }

        // ─────────────────────────────────────────────
        // BUY 1건 (순차 전송)
        // ─────────────────────────────────────────────
        private static async Task<bool> ExecuteBuy_OneAsync(매매실행 exec, long firePrice, int decisionBandK, int startBandNow)
        {
            var k = GetBand(decisionBandK);
            if (k == null)
            {
                Console.WriteLine($"[0300][BUY] K not found K={decisionBandK}");
                return false;
            }

            long qty = k.Sina; // ✅ 합의: 수량은 K의 Sina
            if (qty <= 0)
            {
                Console.WriteLine($"[0300][BUY] SKIP K.Sina<=0  K={decisionBandK} Sina={qty}");
                return false;
            }

            int updateBand = decisionBandK + 1; // ✅ 합의: Qty 반영 밴드 = K+1 (체결 후)
            var ub = GetBand(updateBand);
            if (ub == null)
            {
                Console.WriteLine($"[0300][BUY] SKIP updateBand(K+1) not found. K={decisionBandK} => {updateBand}");
                return false;
            }

            Console.WriteLine(
                $"[0300.BUY.REQUEST] firePrice={firePrice:#,0} decisionBand(K)={decisionBandK} qty(K.Sina)={qty:#,0} " +
                $"updateQtyBand(K+1)={updateBand} startBandNow={startBandNow}"
            );

            try
            {
                Console.WriteLine($"[0300.BUY.EXEC] SEND -> band(K)={decisionBandK} qty={qty:#,0} price={firePrice:#,0}");
                await exec.ExecuteAsync("매수", decisionBandK, (int)qty, firePrice).ConfigureAwait(false);
                Console.WriteLine($"[0300.BUY.EXEC] DONE band(K)={decisionBandK}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[0300.BUY.EXEC] FAIL band(K)={decisionBandK} msg={ex.Message}");
                return false;
            }
        }

        // ─────────────────────────────────────────────
        // SELL 1건 (순차 전송)
        // ─────────────────────────────────────────────
        private static async Task<bool> ExecuteSell_OneAsync(매매실행 exec, long firePrice, int decisionBand, int startBandNow)
        {
            var b = GetBand(decisionBand);
            if (b == null)
            {
                Console.WriteLine($"[0300][SELL] band not found band={decisionBand}");
                return false;
            }

            long qty = b.Qty;
            if (qty <= 0)
            {
                Console.WriteLine($"[0300][SELL] SKIP Qty<=0 band={decisionBand} Qty={qty}");
                return false;
            }

            Console.WriteLine(
                $"[0300.SELL.REQUEST] firePrice={firePrice:#,0} decisionBand={decisionBand} qty(Qty)={qty:#,0} startBandNow={startBandNow}"
            );

            try
            {
                Console.WriteLine($"[0300.SELL.EXEC] SEND -> band={decisionBand} qty={qty:#,0} price={firePrice:#,0}");
                await exec.ExecuteAsync("매도", decisionBand, (int)qty, firePrice).ConfigureAwait(false);
                Console.WriteLine($"[0300.SELL.EXEC] DONE band={decisionBand}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[0300.SELL.EXEC] FAIL band={decisionBand} msg={ex.Message}");
                return false;
            }
        }

        // ─────────────────────────────────────────────
        // 유틸
        // ─────────────────────────────────────────────
        private static BandRange GetBand(int band)
        {
            return Login.BandList.FirstOrDefault(x => x != null && x.Band == band);
        }

        // (유지) 체결 후 반영 함수들은 0700이 처리하는 구조면 사실상 미사용이지만,
        // 기존 호출/테스트 코드가 있을 수 있어 남겨둔다.

        public static int ApplyFill_Buy(int decisionBandK, long filledQty)
        {
            if (filledQty <= 0) return GetStartBandFromBandList();

            int updateBand = decisionBandK + 1;

            var ub = GetBand(updateBand);
            if (ub == null)
            {
                Debug.WriteLine($"[0300][FILL-BUY] updateBand not found. K={decisionBandK} => {updateBand}");
                return GetStartBandFromBandList();
            }

            ub.Qty += filledQty;

            int newStart = GetStartBandFromBandList();

            Console.WriteLine(
                $"[0300.FILL-BUY] K={decisionBandK} filledQty={filledQty:#,0} -> QtyBand(K+1)={updateBand} newQty={ub.Qty:#,0} newStartBand={newStart}"
            );

            return newStart;
        }

        public static int ApplyFill_Sell(int decisionBand, long filledQty)
        {
            if (filledQty <= 0) return GetStartBandFromBandList();

            var b = GetBand(decisionBand);
            if (b == null)
            {
                Debug.WriteLine($"[0300][FILL-SELL] band not found. band={decisionBand}");
                return GetStartBandFromBandList();
            }

            long newQty = b.Qty - filledQty;
            if (newQty < 0) newQty = 0;
            b.Qty = newQty;

            int newStart = GetStartBandFromBandList();

            Console.WriteLine(
                $"[0300.FILL-SELL] band={decisionBand} filledQty={filledQty:#,0} newQty={b.Qty:#,0} newStartBand={newStart}"
            );

            return newStart;
        }

        private static int GetStartBandFromBandList()
        {
            int max = 0;
            foreach (var b in Login.BandList)
            {
                if (b == null) continue;
                if (b.Qty > 0 && b.Band > max) max = b.Band;
            }
            return max;
        }
    }
}
// 2026-02-03 73164
