// ============================================================
// 0300_BUY판정.cs
//
// 역할
// - BUY 종류를 판정한다.
// - 일반 BUY
// - 10전슬라이딩BUY
// - 10후슬라이딩BUY
//
// 실제 매매를 수행하지 않는다.
// 판단만 담당한다.
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace Exercise_1
{
    public enum BuyDecisionType
    {
        General = 0,
        TenBeforeSliding = 1,
        TenAfterSliding = 2
    }

    public sealed class BuyDecisionResult
    {
        public BuyDecisionType Type;
        public bool HasExistingChain;
        public int ChainFromBand;
        public string LogType;
    }

    public static class _0300_BUY판정
    {
        public static BuyDecisionResult Decide(
            int heldCount,
            int targetBand,
            bool targetAlreadyHeld,
            bool hasForcedSlidingHandler,
            IList<BandRange> bandList)
        {
            bool hasExistingChain = HasExistingChain(bandList);

            if (!targetAlreadyHeld && hasForcedSlidingHandler && heldCount >= 10)
            {
                return new BuyDecisionResult
                {
                    Type = BuyDecisionType.TenAfterSliding,
                    HasExistingChain = hasExistingChain,
                    ChainFromBand = 0,
                    LogType = "10후슬라이딩BUY"
                };
            }

            if (!targetAlreadyHeld && heldCount < 10 && hasExistingChain)
            {
                return new BuyDecisionResult
                {
                    Type = BuyDecisionType.TenBeforeSliding,
                    HasExistingChain = true,
                    ChainFromBand = targetBand - 10,
                    LogType = "10전슬라이딩BUY"
                };
            }

            return new BuyDecisionResult
            {
                Type = BuyDecisionType.General,
                HasExistingChain = hasExistingChain,
                ChainFromBand = 0,
                LogType = "GENERAL"
            };
        }

        private static bool HasExistingChain(IList<BandRange> bandList)
        {
            try
            {
                return bandList != null &&
                       bandList.Any(x => x != null && x.Qty > 0 && x.From_Band > 0);
            }
            catch
            {
                return false;
            }
        }
    }
}
