using System;
using System.Threading.Tasks;

namespace Exercise_1
{
    // DEPRECATED: Pending recovery is disabled by policy.
    public sealed class _0003_Pending복구실행
    {
        private readonly Action<string> _log;

        public sealed class SendOrderAck
        {
            public bool Success { get; set; }
            public string OrdNo { get; set; }
            public string Message { get; set; }
        }

        public sealed class PendingRow
        {
            public string Side { get; set; }
            public int TargetBand { get; set; }
            public int Qty { get; set; }
            public int Price { get; set; }
            public int FromBand { get; set; }
            public string Stage { get; set; }
        }

        public sealed class RunResult
        {
            public bool HasPending { get; set; }
            public bool CanStartAutoTrading { get; set; }
            public bool PriceMatched { get; set; }
            public bool OrderSent { get; set; }
            public bool ShouldStartChase { get; set; }
            public string ChaseSideKor { get; set; }
            public int ChaseBand { get; set; }
            public int ChaseQty { get; set; }
            public string Message { get; set; }
            public PendingRow Pending { get; set; }
        }

        public _0003_Pending복구실행(
            string connStr,
            Func<string, int, int, int, Task<SendOrderAck>> sendOrderAsync,
            Action<string> log = null)
        {
            _log = log;
        }

        public Task<RunResult> RunOnceAsync(int currentPrice)
        {
            _log?.Invoke("[0003][DEPRECATED] Pending recovery disabled by policy.");
            return Task.FromResult(new RunResult
            {
                HasPending = false,
                CanStartAutoTrading = true,
                PriceMatched = false,
                OrderSent = false,
                ShouldStartChase = false,
                Message = "Pending disabled by policy"
            });
        }

        public PendingRow ReadPending()
        {
            return null;
        }

        public void TouchPending() { }
        public void UpdatePendingQty(long newQty) { }
        public void UpdatePendingStage(string newStage) { }
        public void ClearPending() { }
    }
}
