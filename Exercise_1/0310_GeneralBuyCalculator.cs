using System;
using System.Collections.Generic;
using System.Linq;

namespace Exercise_1
{
    public sealed class GeneralBuyPlan
    {
        public int Band { get; set; }
        public long Qty { get; set; }
        public long UsedCash { get; set; }

        public int HoldingCount { get; set; }
        public int EmptySlots { get; set; }
        public List<int> TargetBands { get; set; } = new List<int>();
        public int Weight { get; set; }
        public int WeightSum { get; set; }
        public long SlotCash { get; set; }
        public long RemainCash { get; set; }
        public string Verdict { get; set; } = "";
        public string Calc { get; set; } = "";
    }

    public static class GeneralBuyCalculator
    {
        private static readonly int[] DiamondWeights = { 7, 8, 10, 12, 13, 13, 12, 10, 8, 7 };
        private static readonly int[] DiamondCentralFirstIndexes = { 4, 5, 6, 7, 8, 9, 3, 2, 1, 0 };

        public static GeneralBuyPlan CalculateGeneralBuy(
            long orderableCash,
            long price,
            int holdingCount,
            int targetBand,
            IList<BandRange> positions)
        {
            var plan = new GeneralBuyPlan();
            plan.Band = targetBand;
            plan.HoldingCount = CountHoldingBands(positions, holdingCount);
            plan.EmptySlots = 10 - plan.HoldingCount;
            if (plan.EmptySlots < 0) plan.EmptySlots = 0;

            if (orderableCash <= 0 || price <= 0)
            {
                plan.Verdict = "STOP_INVALID_CASH_OR_PRICE";
                plan.Calc = "cash=" + orderableCash + " price=" + price;
                return plan;
            }

            if (plan.EmptySlots <= 0)
            {
                plan.Verdict = "STOP_NO_EMPTY_SLOT";
                plan.Calc = "emptySlots<=0";
                return plan;
            }

            plan.TargetBands = BuildNormalBuyTargetBands(targetBand, plan.EmptySlots, positions);
            if (plan.TargetBands.Count <= 0 || !plan.TargetBands.Contains(targetBand))
            {
                plan.Verdict = "STOP_TARGET_NOT_EMPTY";
                plan.Calc = "targetBand=" + targetBand + " not-in-empty-targets";
                return plan;
            }

            int slotIndex = plan.TargetBands.IndexOf(targetBand);
            var weights = BuildDiamondWeightsForEmptySlots(plan.EmptySlots);
            plan.WeightSum = weights.Sum();
            plan.Weight = weights[slotIndex];
            plan.SlotCash = (orderableCash * plan.Weight) / plan.WeightSum;
            plan.Qty = (long)Math.Floor((double)plan.SlotCash / (double)price);
            plan.UsedCash = plan.Qty * price;
            plan.RemainCash = orderableCash - plan.UsedCash;
            if (plan.RemainCash < 0) plan.RemainCash = 0;
            plan.Verdict = plan.Qty > 0 ? "ALLOCATED" : "STOP_QTY_ZERO";
            plan.Calc =
                "weights=" + string.Join("/", weights) +
                " sum=" + plan.WeightSum +
                " targetWeight=" + plan.Weight +
                " slotCash=" + plan.SlotCash;

            return plan;
        }

        private static int CountHoldingBands(IList<BandRange> positions, int fallbackHoldingCount)
        {
            try
            {
                if (positions == null || positions.Count == 0)
                    return fallbackHoldingCount < 0 ? 0 : fallbackHoldingCount;

                return positions.Count(x => x != null && x.Qty > 0);
            }
            catch
            {
                return fallbackHoldingCount < 0 ? 0 : fallbackHoldingCount;
            }
        }

        private static List<int> BuildDiamondWeightsForEmptySlots(int emptySlots)
        {
            int count = emptySlots;
            if (count < 0) count = 0;
            if (count > 10) count = 10;

            var weights = new List<int>(count);
            for (int i = 0; i < count; i++)
                weights.Add(DiamondWeights[DiamondCentralFirstIndexes[i]]);

            return weights;
        }

        private static List<int> BuildNormalBuyTargetBands(int targetBand, int emptySlots, IList<BandRange> positions)
        {
            var result = new List<int>();

            try
            {
                var list = positions;
                if (list == null || list.Count == 0 || emptySlots <= 0)
                    return result;

                int windowStart = ResolveTenBandWindowStart(targetBand, list);
                int windowEnd = windowStart + 9;

                result = list
                    .Where(x => x != null &&
                                x.Band >= windowStart &&
                                x.Band <= windowEnd &&
                                x.Qty <= 0)
                    .Select(x => x.Band)
                    .Distinct()
                    .OrderBy(x => Math.Abs(x - targetBand))
                    .ThenBy(x => x)
                    .Take(emptySlots)
                    .ToList();

                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[BUY][ALLOC_AUDIT] targetBands EX: " + ex.Message);
                return result;
            }
        }

        private static int ResolveTenBandWindowStart(int targetBand, IList<BandRange> positions)
        {
            var list = positions;
            if (list == null || list.Count == 0)
                return targetBand;

            var held = list
                .Where(x => x != null && x.Qty > 0)
                .Select(x => x.Band)
                .ToList();

            int windowStart = held.Count > 0 ? held.Min() : targetBand;
            if (targetBand < windowStart)
                windowStart = targetBand;
            if (targetBand > windowStart + 9)
                windowStart = targetBand - 9;

            return windowStart;
        }
    }
}