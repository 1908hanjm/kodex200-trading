using System;
using System.Collections.Generic;
using System.Linq;

namespace Exercise_1
{
    public enum OrderRecoveryState
    {
        Pending = 0,
        Recovering = 1,
        Recovered = 2,
        Expired = 3
    }

    public sealed class RecoveryCandidate
    {
        public string RequestId { get; set; }
        public string AccountNo { get; set; }
        public string Symbol { get; set; }
        public string Side { get; set; }
        public int Qty { get; set; }
        public int Price { get; set; }
        public int Band { get; set; }
        public DateTime SendTimeUtc { get; set; }
        public DateTime TimeoutAtUtc { get; set; }
        public long OrdNo { get; set; }
        public OrderRecoveryState RecoveryState { get; set; }

        public RecoveryCandidate Clone()
        {
            return (RecoveryCandidate)MemberwiseClone();
        }
    }

    public static class _0540_주문복구관리
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, RecoveryCandidate> Candidates =
            new Dictionary<string, RecoveryCandidate>(StringComparer.Ordinal);

        public static void AddPending(RecoveryCandidate candidate)
        {
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.RequestId))
                return;

            lock (Sync)
            {
                candidate.RecoveryState = OrderRecoveryState.Pending;
                Candidates[candidate.RequestId] = candidate.Clone();
            }

            WriteState(candidate.RequestId, "Pending", "Pending");
        }

        public static RecoveryCandidate Get(string requestId)
        {
            lock (Sync)
            {
                RecoveryCandidate candidate;
                return Candidates.TryGetValue(requestId ?? "", out candidate)
                    ? candidate.Clone()
                    : null;
            }
        }

        public static RecoveryCandidate FindPendingMatch(
            string side,
            int qty,
            int price,
            string symbol,
            DateTime observedUtc,
            int secondsTolerance)
        {
            lock (Sync)
            {
                var matches = Candidates.Values
                    .Where(x => x.RecoveryState == OrderRecoveryState.Pending)
                    .Where(x => string.Equals(NormalizeSide(x.Side), NormalizeSide(side), StringComparison.Ordinal))
                    .Where(x => qty > 0 && x.Qty == qty)
                    .Where(x => price > 0 && Math.Abs(x.Price - price) <= 1)
                    .Where(x => string.IsNullOrWhiteSpace(symbol) ||
                                string.IsNullOrWhiteSpace(x.Symbol) ||
                                NormalizeSymbol(x.Symbol) == NormalizeSymbol(symbol))
                    .Where(x => Math.Abs((observedUtc - x.SendTimeUtc).TotalSeconds) <= secondsTolerance)
                    .OrderBy(x => Math.Abs((observedUtc - x.SendTimeUtc).TotalSeconds))
                    .Take(2)
                    .ToList();

                return matches.Count == 1 ? matches[0].Clone() : null;
            }
        }

        public static bool TryBeginRecover(string requestId, long ordNo, string source, out RecoveryCandidate candidate)
        {
            candidate = null;
            lock (Sync)
            {
                RecoveryCandidate current;
                if (!Candidates.TryGetValue(requestId ?? "", out current))
                    return false;
                if (current.RecoveryState != OrderRecoveryState.Pending)
                    return false;

                current.RecoveryState = OrderRecoveryState.Recovering;
                current.OrdNo = ordNo;
                candidate = current.Clone();
            }

            WriteState(requestId, "Pending", "Recovering");
            Console.WriteLine("[0540][BEGIN] requestId=" + requestId + " ordNo=" + ordNo + " source=" + source);
            SetUi("주문복구중");
            return true;
        }

        public static bool TryCompleteRecover(string requestId, long ordNo, string source)
        {
            lock (Sync)
            {
                RecoveryCandidate current;
                if (!Candidates.TryGetValue(requestId ?? "", out current))
                    return false;
                if (current.RecoveryState != OrderRecoveryState.Recovering)
                    return false;

                current.RecoveryState = OrderRecoveryState.Recovered;
                current.OrdNo = ordNo;
            }

            WriteState(requestId, "Recovering", "Recovered");
            Console.WriteLine("[0540][SUCCESS] requestId=" + requestId + " ordNo=" + ordNo + " source=" + source);
            SetUi("체결복구완료");
            return true;
        }

        public static bool TryExpire(string requestId)
        {
            lock (Sync)
            {
                RecoveryCandidate current;
                if (!Candidates.TryGetValue(requestId ?? "", out current))
                    return false;
                if (current.RecoveryState != OrderRecoveryState.Pending)
                    return false;

                current.RecoveryState = OrderRecoveryState.Expired;
            }

            WriteState(requestId, "Pending", "Expired");
            return true;
        }

        public static bool TryRecoverAndRegister(string requestId, long ordNo, string source)
        {
            RecoveryCandidate candidate;
            if (!TryBeginRecover(requestId, ordNo, source, out candidate))
                return false;

            bool registered = false;
            try
            {
                var map = Login.OrdMap;
                if (map == null)
                    return false;

                try
                {
                    Login.TradeWait?.MarkAccepted(ordNo, candidate.Qty, candidate.Side, candidate.Band);
                }
                catch { }

                registered = map.TryRegister(candidate.Side, candidate.Band, ordNo, candidate.Qty);
                Console.WriteLine("[0600][RECOVERY] requestId=" + requestId +
                                  " ordNo=" + ordNo + " registered=" + registered + " source=" + source);

                if (registered || map.Contains(ordNo))
                    return TryCompleteRecover(requestId, ordNo, source);

                return false;
            }
            finally
            {
                if (!registered)
                {
                    lock (Sync)
                    {
                        RecoveryCandidate current;
                        if (Candidates.TryGetValue(requestId ?? "", out current) &&
                            current.RecoveryState == OrderRecoveryState.Recovering)
                        {
                            current.RecoveryState = OrderRecoveryState.Pending;
                        }
                    }
                }
            }
        }

        private static void WriteState(string requestId, string from, string to)
        {
            Console.WriteLine("[0540][STATE] requestId=" + requestId + " " + from + "->" + to);
        }

        private static void SetUi(string text)
        {
            try { LoginFormAccessor.TryGetLogin()?.SetOrderRecoveryStatus(text); } catch { }
        }

        private static string NormalizeSide(string side)
        {
            side = (side ?? "").Trim();
            if (string.Equals(side, "BUY", StringComparison.OrdinalIgnoreCase) || side == "2") return "매수";
            if (string.Equals(side, "SELL", StringComparison.OrdinalIgnoreCase) || side == "1") return "매도";
            return side;
        }

        private static string NormalizeSymbol(string symbol)
        {
            symbol = (symbol ?? "").Trim().ToUpperInvariant();
            return symbol.StartsWith("A", StringComparison.Ordinal) ? symbol.Substring(1) : symbol;
        }
    }
}
