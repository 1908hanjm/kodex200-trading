using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Exercise_1
{
    public partial class Login
    {
        public static long CurrentActiveOrderNo = 0;

        private void UiInvoke(Action action)
        {
            try
            {
                if (this.IsDisposed) return;

                if (this.InvokeRequired)
                    this.BeginInvoke(action);
                else
                    action();
            }
            catch { }
        }

        private void SetControlColor(Control ctrl, Color color)
        {
            try
            {
                if (ctrl == null || ctrl.IsDisposed) return;

                if (ctrl.BackColor == color) return;

                if (ctrl.InvokeRequired)
                {
                    ctrl.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (ctrl != null && !ctrl.IsDisposed && ctrl.BackColor != color)
                                ctrl.BackColor = color;
                        }
                        catch { }
                    }));
                }
                else
                {
                    if (ctrl.BackColor != color)
                        ctrl.BackColor = color;
                }
            }
            catch { }
        }

        public void SetPanel2Color(Color color)
        {
            if (color.ToArgb() == Color.Orange.ToArgb())
                ShowOrangeReason("SetPanel2Color(Color.Orange)");

            SetControlColor(panel2, color);
        }

        public void SetLogDisplayText(string value, string reason)
        {
            UiInvoke(() =>
            {
                try
                {
                    if (textBox17 == null || textBox17.IsDisposed) return;

                    string before = textBox17.Text ?? "";
                    string after = value ?? "";
                    bool changed = !string.Equals(before, after, StringComparison.Ordinal);
                    textBox17.Text = after;

                    if (changed || IsImportantLogDisplayReason(reason))
                    {
                        Console.WriteLine("[UI][LOG] reason=" + (reason ?? "") + " fileName=" + textBox17.Text);
                    }
                }
                catch { }
            });
        }

        private static bool IsImportantLogDisplayReason(string reason)
        {
            string r = (reason ?? "").Trim();
            return string.Equals(r, "LOG_CREATED", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(r, "AUTO_TRADING_READY", StringComparison.OrdinalIgnoreCase);
        }

        public void SetPendingStatusText(string value, string reason)
        {
            UiInvoke(() =>
            {
                try
                {
                    if (textBox13 == null || textBox13.IsDisposed) return;

                    string before = textBox13.Text ?? "";
                    string after = NormalizePendingStatus(value);
                    textBox13.Text = after;

                    Console.WriteLine("[UI][PENDING]");
                    Console.WriteLine("before=" + before);
                    Console.WriteLine("after=" + after);
                    Console.WriteLine("reason=" + (reason ?? ""));
                }
                catch { }
            });
        }

        private static string NormalizePendingStatus(string value)
        {
            string text = (value ?? "").Trim();
            if (text == "없음" ||
                text == "있음" ||
                text == "확인중" ||
                text == "조회실패" ||
                text == "WaitingFill")
                return text;

            if (text.StartsWith("Pending(", StringComparison.OrdinalIgnoreCase) &&
                text.EndsWith(")", StringComparison.OrdinalIgnoreCase))
                return text;

            return "조회실패";
        }

        public void SetPartialFillStatus(bool isPartial)
        {
            SetPartialFillStatusForOrder(isPartial, CurrentActiveOrderNo, isPartial ? 1 : 0);
        }

        public void SetOrderRecoveryStatus(string text)
        {
            UiInvoke(() =>
            {
                try
                {
                    if (textBox14 == null || textBox14.IsDisposed) return;
                    textBox14.Text = text ?? "";
                    Console.WriteLine("[UI][RECOVERY] textBox14=" + (text ?? ""));
                }
                catch { }
            });
        }

        // ✅ [P1-FIX] 완전체결(0650_COMPLETE)로 SC_WAIT이 꺼진 ordNo 기록.
        // 완전체결 후 동일 ordNo로 SC_WAIT이 다시 켜지려 하면(예: 뒤늦게 도착한 비동기 호출)
        // 이를 재점등하지 않도록 막는다. 완전체결된 실주문은 더 이상 체결 이벤트가 오지 않으므로
        // 한번 재점등되면 꺼줄 이벤트가 없어 영구 고착된다.
        private static readonly object _scWaitCompletedLock = new object();
        private static readonly HashSet<long> _scWaitCompletedOrdNos = new HashSet<long>();

        public void SetScWaitStatus(bool isWaiting, long ordNo, int remain, string source)
        {
            if (isWaiting)
            {
                lock (_scWaitCompletedLock)
                {
                    if (_scWaitCompletedOrdNos.Contains(ordNo))
                    {
                        Console.WriteLine("[UI][SC_WAIT][SKIP] ordNo=" + ordNo + " source=" + (source ?? "") +
                                          " reason=AlreadyFilled -> SC_WAIT 재점등 무시");
                        return;
                    }
                }
            }
            else if (string.Equals(source, "0650_COMPLETE", StringComparison.OrdinalIgnoreCase))
            {
                lock (_scWaitCompletedLock)
                {
                    _scWaitCompletedOrdNos.Add(ordNo);
                }
            }

            UiInvoke(() =>
            {
                try
                {
                    if (textBox15 == null || textBox15.IsDisposed) return;

                    textBox15.Text = isWaiting ? "SC 기다리는중" : "";

                    if (isWaiting)
                    {
                        if (remain > 0)
                            Console.WriteLine("[UI][SC_WAIT] remain>0 keep waiting ordNo=" + ordNo + " remain=" + remain + " source=" + (source ?? ""));
                        else
                            Console.WriteLine("[UI][SC_WAIT] textbox15=SC 기다리는중 ordNo=" + ordNo + " source=" + (source ?? ""));
                    }
                    else
                    {
                        Console.WriteLine("[UI][SC_WAIT] textbox15 cleared ordNo=" + ordNo + " source=" + (source ?? ""));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[UI][SC_WAIT][ERR] " + ex.Message);
                }
            });
        }

        public void SetPartialFillStatusForOrder(bool isPartial, long currentOrdNo, int remain)
        {
            UiInvoke(() =>
            {
                try
                {
                    if (textBox14 == null || textBox14.IsDisposed) return;
                    if (isPartial && remain <= 0) return;
                    if (isPartial && currentOrdNo != CurrentActiveOrderNo) return;

                    textBox14.Text = isPartial ? "부분체결발생" : "";
                    Console.WriteLine(isPartial
                        ? "[UI][PARTIAL] textBox14=부분체결발생"
                        : "[UI][PARTIAL] textBox14 cleared");
                }
                catch { }
            });
        }

        private void LogUiStateBeforeOrange()
        {
            ShowOrangeReason("BeforeOrange");
        }

        private void ShowOrangeReason(string trigger)
        {
            try
            {
                bool tradeWait = false;
                bool pendingExists = false;
                bool liveOrderExists = false;
                int liveOrderCount = 0;
                string pendingStage = "DISABLED";
                string tradeWaitDetail = "";
                string liveOrderDetail = "";

                try
                {
                    tradeWait = TradeWait != null && TradeWait.IsLocked;
                    if (tradeWait)
                    {
                        tradeWaitDetail =
                            " ordNo=" + TradeWait.LockedOrdNo +
                            " side=" + (TradeWait.LockedSide ?? "") +
                            " band=" + TradeWait.LockedBand +
                            " qty=" + TradeWait.LockedOrderQty;
                    }
                }
                catch { }

                try
                {
                    var chaser = PendingChaser04;
                    if (chaser != null)
                    {
                        var snap = chaser.GetSnapshot();
                        pendingExists = snap != null && snap.IsActive;
                        pendingStage = pendingExists ? "ACTIVE" : "INACTIVE";
                    }
                }
                catch { }

                try
                {
                    var rows = new List<OrderRow>();
                    lock (_startupObservationRows)
                    {
                        rows.AddRange(_startupObservationRows);
                    }

                    liveOrderCount = rows.Count;
                    liveOrderExists = liveOrderCount > 0;

                    if (liveOrderExists)
                    {
                        var r = rows[0];
                        liveOrderDetail =
                            " ordNo=" + ((r.OrderNo ?? "").Trim()) +
                            " side=" + ((r.Side == TradeSide.Buy) ? "BUY" : "SELL") +
                            " remain=" + r.RemainQty +
                            " qty=" + r.Qty +
                            " status=" + (string.IsNullOrWhiteSpace(r.Status) ? "(empty)" : r.Status.Trim());

                        if (string.IsNullOrWhiteSpace(r.Status))
                        {
                            Console.WriteLine("[UI_ORANGE_REASON][ERROR] live order exists but t0425 status is empty" + liveOrderDetail);
                        }
                    }
                }
                catch { }

                string reason =
                    "[UI_ORANGE_REASON]" +
                    " trigger=" + (trigger ?? "") +
                    " AutoTradingBlocked=" + AutoTradingBlocked +
                    " SwapInProgress=" + SwapInProgress +
                    " UpSwapInProgress=" + UpSwapInProgress +
                    " TradeWait.IsLocked=" + tradeWait + tradeWaitDetail +
                    " PendingExists=" + pendingExists +
                    " PendingStage=" + pendingStage +
                    " LiveOrderExists=" + liveOrderExists +
                    " LiveOrderCount=" + liveOrderCount +
                    liveOrderDetail +
                    " ChainFinishedBusy=" + ChainFinishedBusy +
                    " Message=" + (_brokerOpenOrdersAtBootMessage ?? "");

                Console.WriteLine(reason);

                UiInvoke(() =>
                {
                    try
                    {
                        if (textBox14 != null && !textBox14.IsDisposed)
                        {
                            if (liveOrderExists)
                                textBox14.Text = "접수/미체결" + liveOrderDetail;
                            else if (tradeWait)
                                textBox14.Text = "주문대기" + tradeWaitDetail;

                            if (liveOrderExists && string.IsNullOrWhiteSpace(textBox14.Text))
                                Console.WriteLine("[UI_ORANGE_REASON][ERROR] live order exists but textBox14 reason is empty");
                        }

                        try { RTB910?.WriteLine(reason); } catch { }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[UI_ORANGE_REASON][UI_ERROR] " + ex.Message);
                    }
                });
            }
            catch { }
        }

        public void UpdateStatus(string msg)
        {
            UiInvoke(() =>
            {
                try
                {
                    Console.WriteLine("[STATUS] " + msg);
                }
                catch { }
            });
        }
    }
}
// 2026-03-16 45182
