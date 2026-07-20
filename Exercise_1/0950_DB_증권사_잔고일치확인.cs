// 0950_DB_증권사_잔고일치확인.cs
// SC1 DB 반영 후 DB SUM(qty)와 증권사 t0424 qtySum 비교 전용.

using System;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Exercise_1
{
    public sealed class _0950_DB_증권사_잔고일치확인
    {
        private const string LogDir = @"C:\c#\잔고비교log";
        private static readonly object LogLock = new object();
        private static readonly string LogPath =
            Path.Combine(LogDir, "잔고비교_" + DateTime.Now.ToString("yyyy_MM_dd_HHmmss") + ".log");
        private static int _finalStopRequested;

        public sealed class FillContext
        {
            public string Side;
            public long OrdNo;
            public long ExecNo;
            public int Band;
            public int FillQty;
            public double Price;
            public int CumFill;
            public int Remain;
            public bool IsComplete;
        }

        public Task CheckAfterSc1DbAppliedAsync(FillContext ctx)
        {
            return Task.Run(async () =>
            {
                try
                {
                    await CheckCoreAsync(ctx).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    WriteLog("[BALANCE_CHECK][ERROR] " + Safe(ex.Message));
                }
            });
        }

        private async Task CheckCoreAsync(FillContext ctx)
        {
            if (ctx == null) return;

            WriteLog("[BALANCE_CHECK][START] " + FormatFill(ctx));

            if (!ctx.IsComplete || ctx.Remain > 0)
            {
                long partialDbTotal = ReadDbTotal();
                WriteLog("[BALANCE_CHECK][PARTIAL_ONLY_LOG] " + FormatFill(ctx) + " action=LOG_ONLY");
                WriteLog("[BALANCE_CHECK][DB] dbTotal=" + partialDbTotal + " phase=PARTIAL_ONLY_LOG");
                return;
            }

            CheckSnapshot first = await ReadSnapshotAsync().ConfigureAwait(false);
            if (!first.T0424Ok)
            {
                CheckSnapshot recovered = await RetryT0424FailureAsync(ctx, first).ConfigureAwait(false);
                if (!recovered.T0424Ok)
                {
                    WriteLog("[BALANCE_CHECK][T0424_QUERY_FAILED_FINAL] " +
                             FormatFill(ctx) + " error=" + Safe(recovered.T0424Error));
                    RequestSafeStopAndExit(ctx, recovered, "T0424_QUERY_FAILED_FINAL");
                    return;
                }

                first = recovered;
            }

            if (WriteAndIsMatch(ctx, first, false, 0))
                return;

            CheckSnapshot last = first;
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                WriteLog("[BALANCE_CHECK][RETRY] attempt=" + attempt + "/2 waitMs=1000 " + FormatFill(ctx));
                await Task.Delay(1000).ConfigureAwait(false);

                last = await ReadSnapshotAsync().ConfigureAwait(false);
                if (!last.T0424Ok)
                {
                    last = await RetryT0424FailureAsync(ctx, last).ConfigureAwait(false);
                    if (!last.T0424Ok)
                    {
                        WriteLog("[BALANCE_CHECK][T0424_QUERY_FAILED_FINAL] " +
                                 FormatFill(ctx) + " error=" + Safe(last.T0424Error));
                        RequestSafeStopAndExit(ctx, last, "T0424_QUERY_FAILED_FINAL");
                        return;
                    }
                }

                if (WriteAndIsMatch(ctx, last, true, attempt))
                    return;
            }

            WriteLog("[BALANCE_CHECK][MISMATCH_FINAL] " +
                     "dbTotal=" + last.DbTotal +
                     " t0424Total=" + last.T0424Total +
                     " diff=" + last.Diff + " " + FormatFill(ctx));
            RequestSafeStopAndExit(ctx, last, "DB_T0424_QTY_MISMATCH");
        }

        private bool WriteAndIsMatch(FillContext ctx, CheckSnapshot s, bool afterRetry, int retryAttempt)
        {
            WriteLog("[BALANCE_CHECK][DB] dbTotal=" + s.DbTotal + " " + FormatFill(ctx));
            WriteLog("[BALANCE_CHECK][T0424] t0424Total=" + s.T0424Total + " " + FormatFill(ctx));

            if (s.Diff != 0)
            {
                WriteLog("[BALANCE_CHECK][DIFF] dbTotal=" + s.DbTotal +
                         " t0424Total=" + s.T0424Total +
                         " diff=" + s.Diff +
                         (afterRetry ? " retryAttempt=" + retryAttempt : "") +
                         " " + FormatFill(ctx));
                return false;
            }

            WriteLog((afterRetry ? "[BALANCE_CHECK][MATCH_AFTER_RETRY] " : "[BALANCE_CHECK][MATCH] ") +
                     "dbTotal=" + s.DbTotal +
                     " t0424Total=" + s.T0424Total +
                     " diff=0" +
                     (afterRetry ? " retryAttempt=" + retryAttempt : "") +
                     " " + FormatFill(ctx));
            return true;
        }

        private async Task<CheckSnapshot> RetryT0424FailureAsync(FillContext ctx, CheckSnapshot first)
        {
            CheckSnapshot last = first;
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                WriteLog("[BALANCE_CHECK][RETRY] target=T0424_QUERY attempt=" + attempt +
                         "/2 waitMs=1000 error=" + Safe(last.T0424Error) + " " + FormatFill(ctx));
                await Task.Delay(1000).ConfigureAwait(false);
                last = await ReadSnapshotAsync().ConfigureAwait(false);
                if (last.T0424Ok) return last;
            }
            return last;
        }

        private async Task<CheckSnapshot> ReadSnapshotAsync()
        {
            var s = new CheckSnapshot();
            s.DbTotal = ReadDbTotal();
            try
            {
                var t0424 = new _0900_banance_cspaq12200_t0424(msg => WriteLog("[BALANCE_CHECK][T0424_RAW] " + msg));
                var result = await t0424.QueryT0424OnlyAsync(
                    actNo: (Login.Actno ?? "").Trim(),
                    pwd: ResolveAccountPassword(),
                    timeout: TimeSpan.FromSeconds(5),
                    retryT0424IfZero: false,
                    maxRetry: 0,
                    retryDelayMs: 1000).ConfigureAwait(false);

                s.T0424Ok = true;
                s.T0424Total = result.qtySum;
                s.Diff = s.DbTotal - s.T0424Total;
            }
            catch (Exception ex)
            {
                s.T0424Ok = false;
                s.T0424Error = ex.Message;
                s.Diff = s.DbTotal;
            }
            return s;
        }

        private static long ReadDbTotal()
        {
            using (var conn = new SQLiteConnection(Login.ConnStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COALESCE(SUM(qty),0) FROM kodex200_new";
                    object v = cmd.ExecuteScalar();
                    return v != null && v != DBNull.Value ? Convert.ToInt64(v) : 0;
                }
            }
        }

        private static string ResolveAccountPassword()
        {
            try
            {
                if (_0050_Real_Test환경결정.IsTest)
                    return (_0050_Real_Test환경결정.CertPw ?? "").Trim();
            }
            catch { }
            return (Login.JMpass ?? "").Trim();
        }

        private void RequestSafeStopAndExit(FillContext ctx, CheckSnapshot s, string reason)
        {
            if (Interlocked.Exchange(ref _finalStopRequested, 1) != 0)
            {
                WriteLog("[BALANCE_CHECK][STOP_AUTO] reason=" + reason + " skipped=already_requested");
                return;
            }

            WriteLog("[BALANCE_CHECK][STOP_AUTO] reason=" + reason);
            try
            {
                Login.StopAutoTradingForRestartRecovery(
                    reason,
                    ctx != null ? ctx.OrdNo : 0,
                    ctx != null ? ctx.Band : 0,
                    "DB/t0424 balance checker final stop",
                    showPopup: false,
                    side: ctx != null ? ctx.Side : "",
                    qty: ctx != null ? ctx.FillQty : 0,
                    price: ctx != null ? ctx.Price : 0);
            }
            catch
            {
                try { Login.AutoTradingBlocked = true; } catch { }
            }

            Login login = null;
            try { login = LoginFormAccessor.TryGetLogin(); } catch { }

            Action uiAction = () =>
            {
                try
                {
                    MessageBox.Show(
                        login,
                        "DB 잔고와 증권사 잔고가 일치하지 않습니다.\r\n" +
                        "자동매매를 중지하고 프로그램을 종료합니다.\r\n\r\n" +
                        "DB TOTAL = " + s.DbTotal + "\r\n" +
                        "t0424 TOTAL = " + (s.T0424Ok ? s.T0424Total.ToString() : "QUERY_FAILED") + "\r\n" +
                        "DIFF = " + s.Diff + "\r\n" +
                        "ordNo = " + (ctx != null ? ctx.OrdNo : 0) + "\r\n" +
                        "execNo = " + (ctx != null ? ctx.ExecNo : 0),
                        "잔고 불일치",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                catch { }

                try
                {
                    WriteLog("[BALANCE_CHECK][EXIT_APP] reason=" + reason);
                    Application.Exit();
                }
                catch { }
            };

            try
            {
                if (login != null && !login.IsDisposed && login.InvokeRequired)
                    login.BeginInvoke(uiAction);
                else
                    uiAction();
            }
            catch
            {
                WriteLog("[BALANCE_CHECK][EXIT_APP] reason=" + reason + " fallback=true");
                try { Application.Exit(); } catch { }
            }
        }

        private static string FormatFill(FillContext ctx)
        {
            if (ctx == null) return "";
            return "side=" + Safe(ctx.Side) +
                   " ordNo=" + ctx.OrdNo +
                   " execNo=" + ctx.ExecNo +
                   " band=" + ctx.Band +
                   " fillQty=" + ctx.FillQty +
                   " price=" + ctx.Price +
                   " cumFill=" + ctx.CumFill +
                   " remain=" + ctx.Remain +
                   " isComplete=" + ctx.IsComplete;
        }

        private static void WriteLog(string line)
        {
            try
            {
                lock (LogLock)
                {
                    Directory.CreateDirectory(LogDir);
                    string text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + line;
                    File.AppendAllText(LogPath, text + Environment.NewLine);
                    Debug.WriteLine(text);
                    Console.WriteLine(text);
                }
            }
            catch { }
        }

        private static string Safe(string s)
        {
            return (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        }

        private sealed class CheckSnapshot
        {
            public long DbTotal;
            public bool T0424Ok;
            public long T0424Total;
            public long Diff;
            public string T0424Error;
        }
    }
}
