using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Exercise_1
{
        public sealed class _0800__완전청산후
    {
        private const int RebuildSlotCount = 10;

        private readonly Login _login;
        private readonly object _sync = new object();
        private bool _rebuildTaskRunning;
        private RebuildDistributionState _distributionState;

        private sealed class RebuildDistributionState
        {
            public List<int> TargetBands = new List<int>();
            public Dictionary<int, int> FilledByBand = new Dictionary<int, int>();
            public int TotalBuyQty;
            public int AppliedCumulativeQty;
            public int LastSoldBand;
            public int SellDecisionBand;
            public int RebuildStartBand;
            public int OrderBand;
            public bool IsActive;
            public string RebuildDistributionMode;
        }

        public _0800__완전청산후(Login login)
        {
            _login = login;
        }

        public bool IsRestorePendingOrActive
        {
            get
            {
                lock (_sync)
                {
                    return _rebuildTaskRunning || (_distributionState != null && _distributionState.IsActive);
                }
            }
        }

        public void CheckAfterSellCompleted(double currentPrice, int lastSoldBand)
        {
            CheckAfterSellCompleted(currentPrice, lastSoldBand, lastSoldBand);
        }

        public void CheckAfterSellCompleted(double currentPrice, int lastSoldBand, int sellDecisionBand)
        {
            try
            {
                int heldCount = CountHeldBands();
                Write("[0800][REBUILD] 완전청산 확인 lastSoldBand=" + lastSoldBand +
                      " sellDecisionBand=" + sellDecisionBand + " holdingCount=" + heldCount);

                if (heldCount != 0)
                    return;

                lock (_sync)
                {
                    if (_rebuildTaskRunning)
                    {
                        Write("[0800][REBUILD] already running -> skip");
                        return;
                    }

                    _rebuildTaskRunning = true;
                }

                Task.Run(async () =>
                {
                    try
                    {
                        await ExecuteAsync(currentPrice, lastSoldBand, sellDecisionBand).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Write("[0800][REBUILD] ERROR " + ex.Message);
                    }
                    finally
                    {
                        lock (_sync)
                        {
                            _rebuildTaskRunning = false;
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Write("[0800][REBUILD] CheckAfterSellCompleted ERROR " + ex.Message);
            }
        }

        public bool TryHandleRestoreBuyFill(string sideKor, int band, int fillQty, bool isComplete)
        {
            sideKor = (sideKor ?? "").Trim();
            if (sideKor != "매수" || fillQty <= 0)
                return false;

            RebuildDistributionState state;
            lock (_sync)
            {
                state = _distributionState;
                if (state == null || !state.IsActive)
                    return false;
            }

            Write("[0700][REBUILD_FILL] fillQty=" + fillQty);
            DistributeFillToTargetBands(state, fillQty);

            if (isComplete || IsDistributionComplete(state))
            {
                lock (_sync)
                {
                    if (_distributionState == state)
                        state.IsActive = false;
                }
            }

            return true;
        }

        public bool TryGetPendingRebuildBuyMeta(
            int orderBand,
            out int lastSoldBand,
            out int sellDecisionBand,
            out int rebuildStartBand,
            out List<int> targetBands,
            out string distributionMode)
        {
            lock (_sync)
            {
                var state = _distributionState;
                if (state != null && state.IsActive && state.OrderBand == orderBand)
                {
                    lastSoldBand = state.LastSoldBand;
                    sellDecisionBand = state.SellDecisionBand;
                    rebuildStartBand = state.RebuildStartBand;
                    targetBands = state.TargetBands != null ? new List<int>(state.TargetBands) : new List<int>();
                    distributionMode = state.RebuildDistributionMode;
                    return true;
                }
            }

            lastSoldBand = 0;
            sellDecisionBand = 0;
            rebuildStartBand = 0;
            targetBands = new List<int>();
            distributionMode = "";
            return false;
        }

        public async Task ExecuteAsync(int lastSoldBand)
        {
            await ExecuteAsync(0, lastSoldBand, lastSoldBand).ConfigureAwait(false);
        }

        public async Task ExecuteAsync(double currentPrice, int lastSoldBand)
        {
            await ExecuteAsync(currentPrice, lastSoldBand, lastSoldBand).ConfigureAwait(false);
        }

        public async Task ExecuteAsync(double currentPrice, int lastSoldBand, int sellDecisionBand)
        {
            if (lastSoldBand <= 0)
            {
                Write("[0800][REBUILD] invalid lastSoldBand=" + lastSoldBand);
                return;
            }

            int heldCount = CountHeldBands();
            if (heldCount != 0)
            {
                Write("[0800][REBUILD] holdingCount=" + heldCount + " -> skip");
                return;
            }

            List<int> targetBands = BuildTargetBands(lastSoldBand);
            Write("[0800][TARGET] LastSoldBand=" + lastSoldBand +
                  " SellDecisionBand=" + sellDecisionBand +
                  " TargetCount=" + targetBands.Count +
                  " TargetBands=" + string.Join(",", targetBands));
            if (targetBands.Count != RebuildSlotCount)
            {
                Write("[0800][WARN] TargetCount!=10 LastSoldBand=" + lastSoldBand +
                      " SellDecisionBand=" + sellDecisionBand +
                      " TargetCount=" + targetBands.Count +
                      " TargetBands=" + string.Join(",", targetBands));
            }

            Write("[0800][REBUILD] 완전청산 후 재진입 정합 처리 lastSoldBand=" + lastSoldBand +
                  " sellDecisionBand=" + sellDecisionBand);
            Write("[0800][REBUILD] RebuildTargetBands=" + string.Join(",", targetBands));
            Write("[0800][REBUILD] RebuildDistributionMode=AsymmetricDiamond");
            Write("[PENDING][DISABLED] Pending logic is disabled by policy.");

            long orderableCashAtStart = await QueryOrderableCash().ConfigureAwait(false);
            long buyPrice = ResolveRebuildBuyPrice(currentPrice, targetBands);
            int totalBuyQty = 0;
            if (orderableCashAtStart > 0 && buyPrice > 0)
                totalBuyQty = (int)Math.Floor(orderableCashAtStart / (double)buyPrice);

            Write("[0800][REBUILD] orderableCash=" + orderableCashAtStart +
                  " currentPrice=" + buyPrice);
            Write("[0800][REBUILD] totalBuyQty=" + totalBuyQty);

            if (totalBuyQty <= 0)
            {
                Write("[0800][REBUILD] STOP totalBuyQty=" + totalBuyQty);
                return;
            }

            var exec = _login != null ? _login.Exec : null;
            if (exec == null)
            {
                Write("[0800][REBUILD] STOP exec is null");
                return;
            }

            int orderBandK = targetBands[0] - 1;
            if (orderBandK <= 0)
            {
                Write("[0800][REBUILD] STOP orderBandK=" + orderBandK);
                return;
            }

            if (IsTradeLocked())
            {
                WriteStopLocked("LOCKED_BEFORE_SINGLE_BUY", orderBandK);
                return;
            }

            SetDistributionState(targetBands, totalBuyQty, lastSoldBand, sellDecisionBand, orderBandK);

            Write("[0800][REBUILD] executeBand=" + orderBandK + " rebuildStartBand=" + targetBands[0] +
                  " lastSoldBand=" + lastSoldBand + " sellDecisionBand=" + sellDecisionBand);
            Write("[0800][REBUILD] Single BUY Order qty=" + totalBuyQty);
            await exec.ExecuteAsync("매수", orderBandK, totalBuyQty, buyPrice).ConfigureAwait(false);
        }

        private void SetDistributionState(
            List<int> targetBands,
            int totalBuyQty,
            int lastSoldBand,
            int sellDecisionBand,
            int orderBandK)
        {
            var state = new RebuildDistributionState
            {
                TargetBands = targetBands != null ? new List<int>(targetBands) : new List<int>(),
                TotalBuyQty = totalBuyQty,
                AppliedCumulativeQty = 0,
                LastSoldBand = lastSoldBand,
                SellDecisionBand = sellDecisionBand,
                RebuildStartBand = targetBands != null && targetBands.Count > 0 ? targetBands[0] : 0,
                OrderBand = orderBandK,
                IsActive = true,
                RebuildDistributionMode = "AsymmetricDiamond"
            };

            foreach (int targetBand in state.TargetBands)
                state.FilledByBand[targetBand] = 0;

            lock (_sync)
            {
                _distributionState = state;
            }
        }

        private static List<int> BuildTargetBands(int lastSoldBand)
        {
            var bands = new List<int>();
            int newStartBand = lastSoldBand - 1;
            int newEndBand = lastSoldBand - RebuildSlotCount;

            for (int band = newStartBand; band >= newEndBand; band--)
                bands.Add(band);

            return bands;
        }

        private async Task<long> QueryOrderableCash()
        {
            var cash1000 = _login != null ? _login.CashQueryForFullClear : null;

            try
            {
                if (cash1000 != null)
                {
                    long cash = await cash1000
                        .RequestAsync(
                            Login.Actno,
                            Login.JMpass,
                            2000,
                            false,
                            "0800_FULL_CLEAR_REBUILD",
                            "0800__완전청산후")
                        .ConfigureAwait(false);

                    if (cash > 0)
                        return cash;
                }
            }
            catch (Exception ex)
            {
                Write("[0800][REBUILD] QueryOrderableCash WARN " + ex.Message);
            }

            try
            {
                if (Login.LastKnownOrderableCash > 0)
                    return Login.LastKnownOrderableCash;
            }
            catch { }

            return 0L;
        }

        private static long GetBuyPrice(int band)
        {
            using (var conn = new SQLiteConnection(Login.ConnStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT 살가격 FROM kodex200_new WHERE band = @b LIMIT 1";
                    cmd.Parameters.AddWithValue("@b", band);

                    object val = cmd.ExecuteScalar();
                    if (val == null || val == DBNull.Value)
                        return 0L;

                    return Convert.ToInt64(val);
                }
            }
        }

        private static long ResolveRebuildBuyPrice(double currentPrice, List<int> targetBands)
        {
            if (currentPrice > 0)
                return Convert.ToInt64(Math.Floor(currentPrice));

            if (targetBands != null && targetBands.Count > 0)
                return GetBuyPrice(targetBands[0]);

            return 0L;
        }

        private static bool IsDistributionComplete(RebuildDistributionState state)
        {
            if (state == null || state.TargetBands == null || state.TargetBands.Count == 0)
                return true;

            for (int i = 0; i < state.TargetBands.Count; i++)
            {
                int band = state.TargetBands[i];
                int qty;
                state.FilledByBand.TryGetValue(band, out qty);
                if (qty < GetFinalTargetQty(state, i))
                    return false;
            }

            return true;
        }

        private static void DistributeFillToTargetBands(RebuildDistributionState state, int fillQty)
        {
            if (state == null || state.TargetBands == null || state.TargetBands.Count == 0 || fillQty <= 0)
                return;

            int nextCumulative = state.AppliedCumulativeQty + fillQty;
            if (state.TotalBuyQty > 0 && nextCumulative > state.TotalBuyQty)
                nextCumulative = state.TotalBuyQty;
            int filledDelta = nextCumulative - state.AppliedCumulativeQty;

            var desired = _0810_완전청산분배알고리즘.CalcAsymmetricDiamondDistribution(
                nextCumulative,
                state.TargetBands,
                state.LastSoldBand);

            var dbBefore = ReadDbQtys(state.TargetBands);
            var deltas = new Dictionary<int, int>();

            Write("[0700][REBUILD_FILL] cumulativeQ=" + nextCumulative +
                  " mode=" + state.RebuildDistributionMode);

            int changedBands = 0;
            foreach (int targetBand in state.TargetBands)
            {
                int already = 0;
                state.FilledByBand.TryGetValue(targetBand, out already);

                int targetQty = 0;
                desired.TryGetValue(targetBand, out targetQty);

                int applyQty = targetQty - already;
                if (applyQty <= 0)
                    continue;

                deltas[targetBand] = applyQty;
                ApplyRebuildQty(targetBand, applyQty);
                state.FilledByBand[targetBand] = already + applyQty;
                changedBands++;

                Write("[0700][REBUILD_FILL] band" + targetBand +
                      " target=" + targetQty + " already=" + already + " diff+=" + applyQty);
            }

            state.AppliedCumulativeQty = nextCumulative;
            var dbAfter = ReadDbQtys(state.TargetBands);

            _0810_완전청산분배알고리즘.LogRebuildAudit(
                filledDelta,
                nextCumulative,
                state.TargetBands,
                desired,
                deltas,
                dbBefore,
                dbAfter);
            Write("[0800][DIST] changedBands=" + changedBands);

            if (changedBands > 0)
            {
                Write("[0800][LV3][ONCE] reload after distribution");
                try { LoginFormAccessor.TryGetLogin()?.RequestListView3ReloadFrom0800("FULL_CLEAR_REBUILD_FILL"); } catch { }
            }
        }

        private static void ApplyRebuildQty(int band, int qty)
        {
            long newQty = 0;

            using (var conn = new SQLiteConnection(Login.ConnStr))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText =
                            "UPDATE kodex200_new " +
                            "SET qty = qty + @q " +
                            "WHERE band = @b";
                        cmd.Parameters.AddWithValue("@q", qty);
                        cmd.Parameters.AddWithValue("@b", band);
                        cmd.ExecuteNonQuery();
                    }

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "SELECT qty FROM kodex200_new WHERE band = @b";
                        cmd.Parameters.AddWithValue("@b", band);
                        object val = cmd.ExecuteScalar();
                        newQty = val != null && val != DBNull.Value ? Convert.ToInt64(val) : 0;
                    }

                    tx.Commit();
                }
            }

            try { DbFuncs.RaiseKodexQtyUpdated(band, newQty, false); } catch { }
        }

        private static Dictionary<int, long> ReadDbQtys(IList<int> bands)
        {
            var result = new Dictionary<int, long>();
            if (bands == null || bands.Count == 0)
                return result;

            using (var conn = new SQLiteConnection(Login.ConnStr))
            {
                conn.Open();
                foreach (int band in bands)
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT qty FROM kodex200_new WHERE band = @b";
                        cmd.Parameters.AddWithValue("@b", band);
                        object val = cmd.ExecuteScalar();
                        result[band] = val != null && val != DBNull.Value ? Convert.ToInt64(val) : 0L;
                    }
                }
            }

            return result;
        }

        private static string FormatDbQtyLog(Dictionary<int, long> values, IList<int> bands)
        {
            if (values == null || bands == null)
                return "[]";

            return "[" + string.Join(",", bands.Select(band =>
            {
                long qty;
                values.TryGetValue(band, out qty);
                return qty.ToString();
            })) + "]";
        }

        private static string FormatDeltaLog(Dictionary<int, int> values, IList<int> bands)
        {
            if (values == null || bands == null)
                return "[]";

            return "[" + string.Join(",", bands.Select(band =>
            {
                int qty;
                values.TryGetValue(band, out qty);
                return qty.ToString();
            })) + "]";
        }

        private static int GetFinalTargetQty(RebuildDistributionState state, int index)
        {
            if (state == null || state.TargetBands == null || state.TargetBands.Count == 0)
                return 0;

            var finalTargets = _0810_완전청산분배알고리즘.CalcAsymmetricDiamondDistribution(
                state.TotalBuyQty,
                state.TargetBands,
                state.LastSoldBand);

            int band = state.TargetBands[index];
            int qty = 0;
            finalTargets.TryGetValue(band, out qty);
            return qty;
        }

        private static int CountHeldBands()
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

        private static bool IsTradeLocked()
        {
            try
            {
                var gate = Login.TradeWait;
                return gate != null && gate.IsLocked;
            }
            catch
            {
                return false;
            }
        }

        private static void WriteStopLocked(string reason, int waitingBand)
        {
            long waitingOrdNo = 0;
            int lockedBand = 0;

            try
            {
                var gate = Login.TradeWait;
                if (gate != null)
                {
                    waitingOrdNo = gate.LockedOrdNo;
                    lockedBand = gate.LockedBand;
                }
            }
            catch { }

            if (lockedBand > 0)
                waitingBand = lockedBand;

            Write("[0800][REBUILD][STOP]");
            Write("reason=" + reason);
            Write("waitingOrdNo=" + waitingOrdNo);
            Write("waitingBand=" + waitingBand);
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
