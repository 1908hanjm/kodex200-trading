using System;
using System.Threading.Tasks;

namespace Exercise_1
{
    // DEPRECATED: end-of-day Pending save is disabled by policy.
    public sealed class _0005_장종료정리
    {
        private readonly Action<string> _log;

        public OrderService OrderSvc { get; set; }
        public string AccountNo { get; set; }
        public string Password { get; set; }
        public string TargetSymbol { get; set; }
        public int EndOfDayHourKst { get; set; }
        public int EndOfDayMinuteKst { get; set; }
        public bool SaveOnlyOncePerDay { get; set; }

        public sealed class PendingRow
        {
            public string OrdNo { get; set; }
            public string Side { get; set; }
            public int TargetBand { get; set; }
            public int Qty { get; set; }
            public int Price { get; set; }
            public int FromBand { get; set; }
            public string Stage { get; set; }
            public DateTime CreatedAt { get; set; }
        }

        public sealed class CloseCaptureResult
        {
            public bool Success { get; set; }
            public bool Skipped { get; set; }
            public bool NoUnfilledOrder { get; set; }
            public bool TimeNotReached { get; set; }
            public bool AlreadySavedToday { get; set; }
            public string Message { get; set; }
            public PendingRow SavedOrder { get; set; }
            public DateTime KstNow { get; set; }
        }

        public _0005_장종료정리(string connStr, Action<string> logger)
        {
            _log = logger;
        }

        public _0005_장종료정리(string connStr)
            : this(connStr, null)
        {
        }

        public Task<CloseCaptureResult> RunAsync(bool force)
        {
            _log?.Invoke("[0005][DEPRECATED] end-of-day Pending save disabled by policy.");
            return Task.FromResult(new CloseCaptureResult
            {
                Success = true,
                Skipped = true,
                NoUnfilledOrder = true,
                Message = "Pending disabled by policy",
                KstNow = DateTime.Now
            });
        }
    }
}
