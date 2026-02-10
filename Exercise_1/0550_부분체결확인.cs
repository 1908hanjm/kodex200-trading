// 0550_부분체결확인.cs  (복붙용 / C# 7.3)
// ------------------------------------------------------------
// ✅ A안(확정): 부분체결이면 잠금 유지, 완전체결 또는 전량취소 확정 시에만 잠금 해제
// ------------------------------------------------------------

using System;
using System.Diagnostics;

namespace Exercise_1
{
    public sealed class _0550_부분체결확인
    {
        private readonly object _lock = new object();

        private bool _locked;
        private DateTime _lockedAt;

        private string _sideKor;
        private int _band;
        private int _orderQty;
        private long _ordNo;

        public bool IsLocked
        {
            get { lock (_lock) return _locked; }
        }

        public long LockedOrdNo
        {
            get { lock (_lock) return _ordNo; }
        }

        public string LockedSide
        {
            get { lock (_lock) return _sideKor; }
        }

        public int LockedBand
        {
            get { lock (_lock) return _band; }
        }

        public int LockedOrderQty
        {
            get { lock (_lock) return _orderQty; }
        }

        public bool TryEnterBeforeSend(string sideKor, int band, int orderQty, long firePrice, out string whyFail)
        {
            whyFail = null;

            lock (_lock)
            {
                if (_locked)
                {
                    whyFail = $"LOCKED ordNo={_ordNo} side={_sideKor} band={_band} qty={_orderQty} since={_lockedAt:HH:mm:ss}";
                    return false;
                }

                _locked = true;
                _lockedAt = DateTime.Now;

                _sideKor = (sideKor ?? "").Trim();
                _band = band;
                _orderQty = orderQty;
                _ordNo = 0;

                Console.WriteLine($"[0550][LOCK] ENTER side={_sideKor} band={_band} qty={_orderQty} price={firePrice} at={_lockedAt:HH:mm:ss.fff}");
                Debug.WriteLine($"[0550][LOCK] ENTER side={_sideKor} band={_band} qty={_orderQty} price={firePrice}");

                return true;
            }
        }

        public void MarkAccepted(long ordNo, int orderQty, string sideKor, int band)
        {
            lock (_lock)
            {
                if (!_locked)
                {
                    Console.WriteLine($"[0550][ACCEPT] but NOT LOCKED? ordNo={ordNo} side={sideKor} band={band} qty={orderQty}");
                    return;
                }

                _ordNo = ordNo;
                if (orderQty > 0) _orderQty = orderQty;
                if (!string.IsNullOrWhiteSpace(sideKor)) _sideKor = sideKor.Trim();
                _band = band;

                Console.WriteLine($"[0550][ACCEPT] ordNo={_ordNo} side={_sideKor} band={_band} qty={_orderQty}");
            }
        }

        public void MarkPartialFill(long ordNo, int cumFill, int remain)
        {
            lock (_lock)
            {
                if (!_locked) return;
                Console.WriteLine($"[0550][PARTIAL] ordNo={ordNo} cumFill={cumFill} remain={remain} -> KEEP LOCK");
            }
        }

        public void MarkCompleteFill(long ordNo, int cumFill)
        {
            lock (_lock)
            {
                if (!_locked) return;
                Console.WriteLine($"[0550][COMPLETE] ordNo={ordNo} cumFill={cumFill} -> RELEASE");
                ReleaseNoLock($"COMPLETE ordNo={ordNo}");
            }
        }

        public void ReleaseAfterFillOrReject(string reason)
        {
            lock (_lock)
            {
                if (!_locked) return;
                Console.WriteLine($"[0550][RELEASE] reason={reason}");
                ReleaseNoLock(reason);
            }
        }

        public void ReleaseAfterCancelConfirmed(long ordNo, string reason)
        {
            lock (_lock)
            {
                if (!_locked) return;
                Console.WriteLine($"[0550][CANCEL.CONFIRMED] ordNo={ordNo} reason={reason} -> RELEASE");
                ReleaseNoLock($"CANCEL.CONFIRMED ordNo={ordNo} {reason}");
            }
        }

        private void ReleaseNoLock(string reason)
        {
            _locked = false;
            _lockedAt = default(DateTime);

            _sideKor = "";
            _band = 0;
            _orderQty = 0;
            _ordNo = 0;

            Console.WriteLine($"[0550][UNLOCK] {reason}");
            Debug.WriteLine($"[0550][UNLOCK] {reason}");
        }

        // ── 호환 메서드들 ──────────────────────────────
        public bool TryBegin(string sideKor, int band, int orderQty, long firePrice, out string whyFail)
            => TryEnterBeforeSend(sideKor, band, orderQty, firePrice, out whyFail);

        public bool TryBegin(string sideKor, int band, int orderQty, out string whyFail)
            => TryEnterBeforeSend(sideKor, band, orderQty, firePrice: 0, out whyFail);

        public bool TryBegin(string sideKor, int band, long orderQty, long firePrice, out string whyFail)
        {
            int q = (orderQty > int.MaxValue) ? int.MaxValue :
                    (orderQty < int.MinValue) ? int.MinValue : (int)orderQty;
            return TryEnterBeforeSend(sideKor, band, q, firePrice, out whyFail);
        }

        public bool TryBegin(string sideKor, int band, long orderQty, out string whyFail)
        {
            int q = (orderQty > int.MaxValue) ? int.MaxValue :
                    (orderQty < int.MinValue) ? int.MinValue : (int)orderQty;
            return TryEnterBeforeSend(sideKor, band, q, firePrice: 0, out whyFail);
        }

        public void EndOnFailed(string reason) => ReleaseAfterFillOrReject("EndOnFailed: " + (reason ?? ""));
        public void EndOnFailed() => ReleaseAfterFillOrReject("EndOnFailed()");
        public void EndOnFailed(params object[] args) => ReleaseAfterFillOrReject("EndOnFailed: " + JoinArgs(args));
        public void OnOrderSendConfirmFailed(params object[] args) => ReleaseAfterFillOrReject("OnOrderSendConfirmFailed: " + JoinArgs(args));
        public void OnRequestFailed(params object[] args) => ReleaseAfterFillOrReject("OnRequestFailed: " + JoinArgs(args));
        public void OnOrderMessageFail(params object[] args) => ReleaseAfterFillOrReject("OnOrderMessageFail: " + JoinArgs(args));

        private static string JoinArgs(object[] args)
        {
            try
            {
                if (args == null || args.Length == 0) return "";
                for (int i = 0; i < args.Length; i++) args[i] = args[i] ?? "";
                return string.Join(" | ", args);
            }
            catch { return ""; }
        }
    }
}
// 2026-02-09 77106
