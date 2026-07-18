using System;
using System.Threading.Tasks;

namespace Exercise_1
{
    // DEPRECATED: unfilled chase is disabled by policy.
    public sealed class _0004_미체결추격관리 : IDisposable
    {
        public enum OrderSide
        {
            Buy,
            Sell
        }

        public sealed class SendOrderResult
        {
            public bool Success { get; set; }
            public string OrdNo { get; set; }
            public string Message { get; set; }
        }

        public sealed class TrackRequest
        {
            public string OrdNo { get; set; }
            public OrderSide Side { get; set; }
            public int Band { get; set; }
            public int OrderPrice { get; set; }
            public long OrderQty { get; set; }
            public string Reason { get; set; }
            public string Shcode { get; set; }
        }

        public sealed class Snapshot
        {
            public bool IsActive { get; set; }
        }

        public int ChaseIntervalSeconds { get; set; }
        public int MaxChaseCount { get; set; }
        public Func<OrderSide, int, int> GetChasedPrice { get; set; }
        public Func<string, Task<bool>> CancelOrderAsync { get; set; }
        public Func<OrderSide, int, int, int, string, string, Task<SendOrderResult>> SendLimitOrderAsync { get; set; }
        public Func<OrderSide, int, int, string, string, Task<SendOrderResult>> SendMarketOrderAsync { get; set; }
        public Action<string> Log { get; set; }
        public Action<string> BlockTrading { get; set; }
        public Action<string> UnblockTrading { get; set; }
        public event Action StateChanged;
        public event Action<bool, string> Finished;

        public void StartTracking(TrackRequest request)
        {
            Log?.Invoke("[0004][DEPRECATED] unfilled chase disabled by policy.");
        }

        public void RestorePending(string ordNo, long remainQty, OrderSide side)
        {
            Log?.Invoke("[0004][DEPRECATED] restore pending disabled by policy.");
        }

        public void RestorePending(
            string ordNo,
            long remainQty,
            OrderSide side,
            int targetBand,
            int price,
            string shcode,
            string reason)
        {
            Log?.Invoke("[0004][DEPRECATED] restore pending disabled by policy.");
        }

        public void NotifyFill(long fillQty)
        {
        }

        public void NotifyFill(string ordNo, long fillQty)
        {
        }

        public Task NotifyCancelConfirmedAsync(string orgOrdNo)
        {
            return Task.CompletedTask;
        }

        public Snapshot GetSnapshot()
        {
            return new Snapshot { IsActive = false };
        }

        public void Dispose()
        {
        }
    }
}
