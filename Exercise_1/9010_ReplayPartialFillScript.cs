// 9010_ReplayPartialFillScript.cs  (복붙용 / C# 7.3)
// ------------------------------------------------------------
// 역할:
// - TEST / REPLAY 모드에서 "이 주문을 얼마만큼 체결시킬지"를 결정한다.
// - 0400은 이 모듈의 결과를 받아 0650.SimulateFill(...) 을 호출한다.
//
// 기본 정책:
// 1) 특별 규칙이 없으면 전량체결
// 2) 현재 기본 탑재 규칙:
//    - 업슬라이딩 중 / 매도 / band=41
//      => 100만 부분체결하고 추가체결 없음
//    - 다운슬라이딩 중 / 매수 / order band=40
//      => 100만 부분체결하고 추가체결 없음
//
// ✅ 중요 수정:
// - 사용자 개념상 "41밴드 매수"이지만,
//   현재 프로그램 내부 첫 down-slide BUY 주문은
//   decisionBandK=40, targetBuyBand=41 구조로 들어온다.
// - 따라서 9010의 partial 대상 band는 41이 아니라 40으로 맞춘다.
//
// 확장 가능:
// - band별
// - side별
// - 다중 chunk (예: 50 + 50)
// - 무체결
// ------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace Exercise_1
{
    public static class _9010_ReplayPartialFillScript
    {
        public sealed class ReplayFillPlan
        {
            public bool IsPartial;
            public int[] FillQuantities;
            public int DelayMsBetweenFills;
            public string Reason;

            public bool HasAnyFill
            {
                get
                {
                    if (FillQuantities == null || FillQuantities.Length == 0)
                        return false;

                    for (int i = 0; i < FillQuantities.Length; i++)
                    {
                        if (FillQuantities[i] > 0)
                            return true;
                    }

                    return false;
                }
            }
        }

        private static readonly object _lock = new object();
        private static readonly HashSet<string> _usedOrdNos = new HashSet<string>();

        // ------------------------------------------------------------
        // ✅ 현재 기본 테스트 시나리오
        // - 업슬라이딩 SELL band=41 이면 100만 부분체결
        // - 다운슬라이딩 BUY  order band=40 이면 100만 부분체결
        // ------------------------------------------------------------
        public static bool Enable_UpSlideSellBand41_Partial100_Only = true;
        public static int UpSlideSellBand = 41;
        public static int UpSlidePartialQty = 100;
        public static int UpSlideDelayMs = 0;

        public static bool Enable_DownSlideBuyBand40_Partial100_Only = true;
        public static int DownSlideBuyBand = 40;
        public static int DownSlidePartialQty = 100;
        public static int DownSlideDelayMs = 0;

        // ------------------------------------------------------------
        // 필요 시 테스트 시작 전에 호출해서 사용 이력 초기화
        // ------------------------------------------------------------
        public static void Reset()
        {
            lock (_lock)
            {
                _usedOrdNos.Clear();
            }
        }

        // ------------------------------------------------------------
        // 0400이 호출하는 핵심 진입점
        // ------------------------------------------------------------
        public static ReplayFillPlan BuildPlan(
            string sideKor,
            int band,
            int orderQty,
            int price,
            string shcode,
            string ordNoRaw)
        {
            sideKor = NormalizeSide(sideKor);

            if (orderQty <= 0)
                return BuildNoFill("orderQty<=0");

            // --------------------------------------------------------
            // ✅ 규칙 1:
            // 업슬라이딩 중 band=41 매도는 100만 부분체결 후 종료
            // --------------------------------------------------------
            if (Enable_UpSlideSellBand41_Partial100_Only &&
                sideKor == "매도" &&
                band == UpSlideSellBand &&
                Login.UpSwapInProgress)
            {
                if (!TryMarkOrdNoOnce(ordNoRaw))
                {
                    return BuildNoFill("already scripted once");
                }

                int partialQty = Math.Min(orderQty, Math.Max(1, UpSlidePartialQty));

                return new ReplayFillPlan
                {
                    IsPartial = (partialQty < orderQty),
                    FillQuantities = new[] { partialQty },
                    DelayMsBetweenFills = UpSlideDelayMs,
                    Reason = $"upslide partial sell band={band} qty={partialQty}/{orderQty}"
                };
            }

            // --------------------------------------------------------
            // ✅ 규칙 2:
            // 다운슬라이딩 중 order band=40 매수는 100만 부분체결 후 종료
            // - 사용자 개념의 실매수밴드는 41이지만
            //   현재 주문 band 식별값은 40으로 들어온다.
            // - replay 종료 자체를 장종료로 간주한다.
            // - 따라서 여기서는 추가 fill을 절대 만들지 않는다.
            // --------------------------------------------------------
            if (Enable_DownSlideBuyBand40_Partial100_Only &&
                sideKor == "매수" &&
                band == DownSlideBuyBand &&
                Login.SwapInProgress)
            {
                if (!TryMarkOrdNoOnce(ordNoRaw))
                {
                    return BuildNoFill("already scripted once");
                }

                int partialQty = Math.Min(orderQty, Math.Max(1, DownSlidePartialQty));

                return new ReplayFillPlan
                {
                    IsPartial = (partialQty < orderQty),
                    FillQuantities = new[] { partialQty },
                    DelayMsBetweenFills = DownSlideDelayMs,
                    Reason = $"downslide partial buy orderBand={band} targetBand={Login.SwapRecordBandK} qty={partialQty}/{orderQty}"
                };
            }

            // --------------------------------------------------------
            // ✅ 기본: 전량체결
            // --------------------------------------------------------
            return new ReplayFillPlan
            {
                IsPartial = false,
                FillQuantities = new[] { orderQty },
                DelayMsBetweenFills = 0,
                Reason = "default full fill"
            };
        }

        private static ReplayFillPlan BuildNoFill(string reason)
        {
            return new ReplayFillPlan
            {
                IsPartial = true,
                FillQuantities = new int[0],
                DelayMsBetweenFills = 0,
                Reason = reason ?? "no fill"
            };
        }

        private static bool TryMarkOrdNoOnce(string ordNoRaw)
        {
            ordNoRaw = (ordNoRaw ?? "").Trim();
            if (ordNoRaw.Length == 0) return false;

            lock (_lock)
            {
                if (_usedOrdNos.Contains(ordNoRaw))
                    return false;

                _usedOrdNos.Add(ordNoRaw);
                return true;
            }
        }

        private static string NormalizeSide(string side)
        {
            if (string.IsNullOrWhiteSpace(side)) return side;

            side = side.Trim();

            if (string.Equals(side, "BUY", StringComparison.OrdinalIgnoreCase)) return "매수";
            if (string.Equals(side, "SELL", StringComparison.OrdinalIgnoreCase)) return "매도";

            return side;
        }
    }
}
// 2026-03-12 64018