// 0300_밴드매칭.cs  (복붙용 / C# 7.3)
// ------------------------------------------------------------
// 역할:
// - Tick_Process(0250)가 전달한 FIRE 정보를 "거래 요청"으로 변환
// - ✅ 실제 주문 전송까지 연결: LoginFormAccessor.TryGetExec() → 매매실행.ExecuteAsync()
// - (중요) BUY 규칙: BUY FIRE가 밴드 K에서 발생하면
//      주문수량 = band K의 Sina
//      체결 후 Qty 반영 = band (K+1)
//      startBand는 qty>0 기준으로 재계산되어 이동 (체결 후 단계에서)
//
// 주의:
// - 본 파일은 static이므로 await 직접 사용 불가 → Task.Run(async () => ...)로 전송
// - Exec가 null이면(아직 Login_Shown_Async 전 등) 주문 전송을 건너뛰고 로그만 남김
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Exercise_1
{
    public static class 밴드매칭
    {
        private const string SIDE_BUY = "BUY";
        private const string SIDE_SELL = "SELL";

        /// <summary>
        /// Tick_Process에서 호출되는 진입점
        /// - side: "BUY" or "SELL"
        /// - firePrice: FIRE 시점 가격(로그/주문 기준)
        /// - endBand: 결정 밴드(= BUY면 K, SELL이면 결정밴드)
        /// - startBand: 호출 시점의 시작밴드(qty>0 중 가장 큰 band)
        /// </summary>
        public static void 실행(string side, long firePrice, int endBand, int startBand)
        {
            if (string.IsNullOrWhiteSpace(side))
            {
                Debug.WriteLine("[0300] side null/empty");
                return;
            }

            side = side.Trim().ToUpperInvariant();

            if (Login.BandList == null || Login.BandList.Count == 0)
            {
                Debug.WriteLine("[0300] Login.BandList 비어 있음");
                return;
            }

            if (startBand <= 0)
            {
                Debug.WriteLine("[0300] startBand<=0 (보유 밴드 없음)");
                return;
            }

            if (endBand <= 0)
            {
                Debug.WriteLine("[0300] endBand<=0 (결정 밴드 없음)");
                return;
            }

            // ─────────────────────────────────────────────
            // SEGMENT 구성(로그 목적 + 필요 시 확장 가능)
            // ─────────────────────────────────────────────
            int lowerBand = Math.Min(startBand, endBand);
            int upperBand = Math.Max(startBand, endBand);

            var segment = Login.BandList
                .Where(b => b.Band >= lowerBand && b.Band <= upperBand)
                .OrderBy(b => b.Band)
                .ToList();

            Console.WriteLine(
                $"[밴드매칭.SEG] count={segment.Count}  order={(endBand < startBand ? "ASC" : "DESC")}  range={lowerBand}~{upperBand}"
            );

            // ─────────────────────────────────────────────
            // 핵심 매칭
            // ─────────────────────────────────────────────
            if (side == SIDE_BUY)
            {
                ExecuteBuy(firePrice, endBand, startBand);
            }
            else if (side == SIDE_SELL)
            {
                ExecuteSell(firePrice, endBand, startBand);
            }
            else
            {
                Debug.WriteLine($"[0300] Unknown side='{side}'");
            }
        }

        // ─────────────────────────────────────────────
        // BUY: 합의 로직 반영
        // - BUY FIRE가 밴드 K에서 발생하면:
        //      qty = band K의 Sina
        //      체결 후 Qty 반영은 K+1
        // - ✅ 여기서 "실제 주문 전송"까지 수행 (Login.Exec)
        // ─────────────────────────────────────────────
        private static void ExecuteBuy(long firePrice, int decisionBandK, int startBand)
        {
            var k = GetBand(decisionBandK);
            if (k == null)
            {
                Debug.WriteLine($"[0300][BUY] decisionBandK={decisionBandK} not found");
                return;
            }

            long qty = k.Sina; // ✅ 합의: 수량은 K의 Sina
            if (qty <= 0)
            {
                Debug.WriteLine($"[0300][BUY] K.Sina<=0  K={decisionBandK} Sina={qty}");
                return;
            }

            int updateBand = decisionBandK + 1; // ✅ 합의: Qty 반영 밴드 = K+1 (체결 후)
            var ub = GetBand(updateBand);
            if (ub == null)
            {
                Debug.WriteLine($"[0300][BUY] updateBand(K+1) not found. K={decisionBandK} => {updateBand}");
                return;
            }

            // 주문 요청 로그
            Console.WriteLine(
                $"[0300.BUY.REQUEST] firePrice={firePrice:#,0} decisionBand(K)={decisionBandK} qty(K.Sina)={qty:#,0} " +
                $"updateQtyBand(K+1)={updateBand} startBand={startBand}"
            );

            // ✅ 실제 주문 전송(0400_매매실행 단일 진입점)
            var exec = LoginFormAccessor.TryGetExec();
            if (exec == null)
            {
                Console.WriteLine("[0300.BUY.EXEC] Exec is null (Login.Exec not ready) -> skip send");
                return;
            }

            // 0300은 static + sync이므로 Task.Run으로 비동기 호출
            Task.Run(async () =>
            {
                try
                {
                    Console.WriteLine($"[0300.BUY.EXEC] SEND -> band(K)={decisionBandK} qty={qty:#,0} price={firePrice:#,0}");

                    // ✅ 매매실행.ExecuteAsync(side, band, qty, price)
                    await exec.ExecuteAsync("매수", decisionBandK, (int)qty, firePrice).ConfigureAwait(false);

                    Console.WriteLine($"[0300.BUY.EXEC] DONE band={decisionBandK}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[0300.BUY.EXEC] EX band={decisionBandK} msg={ex.Message}");
                }
            });
        }

        // ─────────────────────────────────────────────
        // SELL: 일반적인 형태(보유밴드에서 매도)
        // - SELL 수량은 보통 "해당 밴드 Qty" 기반
        // - ✅ 여기서 "실제 주문 전송"까지 수행 (Login.Exec)
        // ─────────────────────────────────────────────
        private static void ExecuteSell(long firePrice, int decisionBand, int startBand)
        {
            var b = GetBand(decisionBand);
            if (b == null)
            {
                Debug.WriteLine($"[0300][SELL] decisionBand={decisionBand} not found");
                return;
            }

            long qty = b.Qty;
            if (qty <= 0)
            {
                Debug.WriteLine($"[0300][SELL] Qty<=0  band={decisionBand} Qty={qty}");
                return;
            }

            Console.WriteLine(
                $"[0300.SELL.REQUEST] firePrice={firePrice:#,0} decisionBand={decisionBand} qty(Qty)={qty:#,0} startBand={startBand}"
            );

            // ✅ 실제 주문 전송(0400_매매실행 단일 진입점)
            var exec = LoginFormAccessor.TryGetExec();
            if (exec == null)
            {
                Console.WriteLine("[0300.SELL.EXEC] Exec is null (Login.Exec not ready) -> skip send");
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    Console.WriteLine($"[0300.SELL.EXEC] SEND -> band={decisionBand} qty={qty:#,0} price={firePrice:#,0}");

                    await exec.ExecuteAsync("매도", decisionBand, (int)qty, firePrice).ConfigureAwait(false);

                    Console.WriteLine($"[0300.SELL.EXEC] DONE band={decisionBand}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[0300.SELL.EXEC] EX band={decisionBand} msg={ex.Message}");
                }
            });
        }

        // ─────────────────────────────────────────────
        // 체결 후 반영(핵심: BUY는 K+1 반영)
        // - 이 함수는 SC1 체결(또는 테스트에서 강제체결) 후에 호출하는 것이 정석
        // ─────────────────────────────────────────────

        /// <summary>
        /// BUY 체결 후 반영:
        /// - filledQty를 band (K+1)의 Qty에 더한다
        /// - 이후 startBand는 qty>0 기준 재계산되어 이동
        /// </summary>
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

        /// <summary>
        /// SELL 체결 후 반영:
        /// - filledQty를 해당 decisionBand의 Qty에서 뺀다(0 미만 방지)
        /// - 이후 startBand 재계산
        /// </summary>
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

        // ─────────────────────────────────────────────
        // 유틸
        // ─────────────────────────────────────────────

        private static BandRange GetBand(int band)
        {
            return Login.BandList.FirstOrDefault(x => x.Band == band);
        }

        private static int GetStartBandFromBandList()
        {
            int max = 0;
            foreach (var b in Login.BandList)
                if (b.Qty > 0 && b.Band > max) max = b.Band;
            return max;
        }
    }
}

// 오늘 2026-01-16-00-00-00
