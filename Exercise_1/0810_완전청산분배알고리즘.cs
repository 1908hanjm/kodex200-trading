using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Exercise_1
{
    public static class _0810_완전청산분배알고리즘
    {
        private static readonly double[] BaseRatio =
        {
            0.07, 0.08, 0.10, 0.12, 0.13,
            0.13, 0.12, 0.10, 0.08, 0.07
        };

        public static int[] CalcAsymmetricDiamondDistribution(int cumulativeQty, int targetBandCount)
        {
            if (targetBandCount <= 0)
                return new int[0];

            var ratios = BuildRescaledRatios(targetBandCount);
            return Allocate(cumulativeQty, ratios);
        }

        public static Dictionary<int, int> CalcAsymmetricDiamondDistribution(
            int cumulativeQty,
            IList<int> targetBands,
            int lastSoldBand)
        {
            var result = new Dictionary<int, int>();
            if (targetBands == null || targetBands.Count == 0)
                return result;

            var calcBands = targetBands.OrderByDescending(band => band).ToList();
            var ratios = BuildRescaledRatios(calcBands.Count).ToList();

            var qtys = Allocate(cumulativeQty, ratios);
            int remain = cumulativeQty - qtys.Sum();
            Write("[0810][CALC]");
            Write("Filled=" + cumulativeQty);
            Write("Ratios=" + FormatRatios(ratios));
            Write("Targets=" + FormatInts(calcBands));
            Write("TargetPercent=" + FormatRatios(ratios));
            Write("Alloc=" + FormatInts(qtys));
            Write("ActualPercent=" + FormatActualPercents(qtys, cumulativeQty));
            Write("Remain=" + remain);
            WriteVerify(cumulativeQty, qtys.Sum());

            for (int i = 0; i < calcBands.Count; i++)
            {
                int band = calcBands[i];
                result[band] = qtys[i];
                Write("[0810][CALC_RATIO_MAP] band=" + band +
                      " ratio=" + (ratios[i] * 100.0).ToString("0.##") +
                      " qty=" + qtys[i]);
            }

            return result;
        }

        public static void LogRebuildAudit(
            int filledDelta,
            int filledTotal,
            IList<int> targetBands,
            Dictionary<int, int> allocation,
            Dictionary<int, int> deltas,
            Dictionary<int, long> dbBefore,
            Dictionary<int, long> dbAfter)
        {
            var orderedTargets = targetBands != null ? targetBands.ToList() : new List<int>();
            var ratios = BuildRescaledRatios(orderedTargets.Count).ToList();
            var allocValues = BuildValues(orderedTargets, allocation);
            int allocSum = allocValues.Sum();
            int remain = filledTotal - allocSum;

            Write("[0810][REBUILD]");
            Write("Filled=" + filledTotal);
            Write("Ratios=" + FormatRatios(ratios));
            Write("Targets=" + FormatInts(orderedTargets));
            Write("TargetPercent=" + FormatRatios(ratios));
            Write("Alloc=" + FormatInts(allocValues));
            Write("ActualPercent=" + FormatActualPercents(allocValues, filledTotal));
            Write("Remain=" + remain);
            Write("Delta=" + FormatIntMap(deltas, orderedTargets));
            Write("DBBefore=" + FormatLongMap(dbBefore, orderedTargets));
            Write("DBAfter=" + FormatLongMap(dbAfter, orderedTargets));

            if (orderedTargets.Count != BaseRatio.Length)
                Write("[0810][WARN] Reason=TargetCount!=10 TargetCount=" + orderedTargets.Count);

            if (Math.Abs(ratios.Sum() - 1.0) > 0.000001)
                Write("[0810][WARN] Reason=RatioMismatch RatioSum=" + ratios.Sum().ToString("0.######"));

            for (int i = 0; i < orderedTargets.Count; i++)
            {
                int band = orderedTargets[i];
                long oldQty = GetLongValue(dbBefore, band);
                long newQty = GetLongValue(dbAfter, band);
                int applyDelta = GetIntValue(deltas, band);

                Write("[0810][DELTA] " +
                      "FilledDelta=" + filledDelta +
                      " FilledTotal=" + filledTotal +
                      " TargetBand=" + band +
                      " OldQty=" + oldQty +
                      " NewQty=" + newQty +
                      " ApplyDelta=" + applyDelta);

                if (applyDelta < 0 || newQty < oldQty)
                {
                    Write("[0810][WARN] Reason=DBDeltaNegative TargetBand=" + band +
                          " OldQty=" + oldQty +
                          " NewQty=" + newQty +
                          " ApplyDelta=" + applyDelta);
                }
            }

            WriteVerify(filledTotal, allocSum);
        }

        public static string FormatDistribution(Dictionary<int, int> distribution, IList<int> targetBands)
        {
            if (distribution == null || targetBands == null)
                return "[]";

            return "[" + string.Join(",", targetBands.Select(band =>
            {
                int qty;
                distribution.TryGetValue(band, out qty);
                return qty.ToString();
            })) + "]";
        }

        public static string FormatRatiosForTargetCount(int targetBandCount)
        {
            return FormatRatios(BuildRescaledRatios(targetBandCount));
        }

        public static string FormatActualPercent(Dictionary<int, int> distribution, IList<int> targetBands, int filledTotal)
        {
            return FormatActualPercents(BuildValues(targetBands, distribution), filledTotal);
        }

        private static double[] BuildRescaledRatios(int targetBandCount)
        {
            if (targetBandCount <= 0)
                return new double[0];

            if (targetBandCount == BaseRatio.Length)
                return (double[])BaseRatio.Clone();

            var ratios = new double[targetBandCount];
            if (targetBandCount == 1)
            {
                ratios[0] = 1.0;
                return ratios;
            }

            for (int i = 0; i < targetBandCount; i++)
            {
                double pos = i * (BaseRatio.Length - 1.0) / (targetBandCount - 1.0);
                int left = (int)Math.Floor(pos);
                int right = (int)Math.Ceiling(pos);
                double t = pos - left;

                ratios[i] = left == right
                    ? BaseRatio[left]
                    : (BaseRatio[left] * (1.0 - t)) + (BaseRatio[right] * t);
            }

            return ratios;
        }

        private static int[] Allocate(int cumulativeQty, IList<double> rawRatios)
        {
            if (rawRatios == null || rawRatios.Count == 0)
                return new int[0];

            var result = new int[rawRatios.Count];
            if (cumulativeQty <= 0)
                return result;

            double sum = rawRatios.Sum();
            if (sum <= 0.0)
            {
                for (int i = 0; i < rawRatios.Count; i++)
                    rawRatios[i] = 1.0;
                sum = rawRatios.Count;
            }

            var remainders = new List<Remainder>(rawRatios.Count);
            int floorSum = 0;
            for (int i = 0; i < rawRatios.Count; i++)
            {
                double exact = cumulativeQty * (rawRatios[i] / sum);
                int floor = (int)Math.Floor(exact);
                result[i] = floor;
                floorSum += floor;
                remainders.Add(new Remainder { Index = i, Fraction = exact - floor });
            }

            int remain = cumulativeQty - floorSum;
            foreach (var r in remainders.OrderByDescending(x => x.Fraction).ThenBy(x => x.Index))
            {
                if (remain <= 0) break;
                result[r.Index]++;
                remain--;
            }

            return result;
        }

        private sealed class Remainder
        {
            public int Index;
            public double Fraction;
        }

        private static string FormatRatios(IList<double> ratios)
        {
            if (ratios == null)
                return "[]";

            return "[" + string.Join(",", ratios.Select(r => (r * 100.0).ToString("0.##"))) + "]";
        }

        private static string FormatActualPercents(IList<int> values, int filledTotal)
        {
            if (values == null)
                return "[]";

            if (filledTotal <= 0)
                return "[" + string.Join(",", values.Select(_ => "0.0")) + "]";

            return "[" + string.Join(",", values.Select(v => ((v * 100.0) / filledTotal).ToString("0.0"))) + "]";
        }

        private static string FormatInts(IEnumerable<int> values)
        {
            return values == null ? "[]" : "[" + string.Join(",", values) + "]";
        }

        private static List<int> BuildValues(IList<int> targetBands, Dictionary<int, int> values)
        {
            var result = new List<int>();
            if (targetBands == null)
                return result;

            foreach (int band in targetBands)
                result.Add(GetIntValue(values, band));

            return result;
        }

        private static int GetIntValue(Dictionary<int, int> values, int band)
        {
            int value;
            return values != null && values.TryGetValue(band, out value) ? value : 0;
        }

        private static long GetLongValue(Dictionary<int, long> values, int band)
        {
            long value;
            return values != null && values.TryGetValue(band, out value) ? value : 0L;
        }

        private static string FormatIntMap(Dictionary<int, int> values, IList<int> targetBands)
        {
            if (targetBands == null)
                return "[]";

            return "[" + string.Join(",", targetBands.Select(band => GetIntValue(values, band).ToString())) + "]";
        }

        private static string FormatLongMap(Dictionary<int, long> values, IList<int> targetBands)
        {
            if (targetBands == null)
                return "[]";

            return "[" + string.Join(",", targetBands.Select(band => GetLongValue(values, band).ToString())) + "]";
        }

        private static void WriteVerify(int filledTotal, int allocSum)
        {
            int diff = filledTotal - allocSum;
            string result = diff == 0 ? "PASS" : "WARN";

            Write("[0810][VERIFY] " +
                  "Filled=" + filledTotal +
                  " AllocSum=" + allocSum +
                  " Diff=" + diff +
                  " Result=" + result);

            if (diff != 0)
                Write("[0810][WARN] Reason=Diff!=0 Filled=" + filledTotal + " AllocSum=" + allocSum + " Diff=" + diff);
        }

        private static void Write(string msg)
        {
            try
            {
                Console.WriteLine(msg);
                Debug.WriteLine(msg);
            }
            catch { }
        }
    }
}
