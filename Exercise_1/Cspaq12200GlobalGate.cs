using System;
using System.Threading;

namespace Exercise_1
{
    public enum Cspaq12200GateSkipReason
    {
        None = 0,
        Throttle = 1,
        RateLimitCooldown = 2,
        AlreadyRunning = 3
    }

    public sealed class Cspaq12200GateLease : IDisposable
    {
        private int _disposed;

        internal Cspaq12200GateLease(string reason, string caller)
        {
            Reason = reason ?? "";
            Caller = caller ?? "";
        }

        public bool Allowed { get; internal set; }
        public Cspaq12200GateSkipReason SkipReason { get; internal set; }
        public string Reason { get; private set; }
        public string Caller { get; private set; }
        public long LastRequestAgoMs { get; internal set; }
        public DateTime? RateLimitUntilLocal { get; internal set; }

        public void ReportResult(int rc)
        {
            Cspaq12200GlobalGate.ReportResult(this, rc);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            Cspaq12200GlobalGate.Exit(this);
        }
    }

    public static class Cspaq12200GlobalGate
    {
        private static readonly object Sync = new object();
        private static bool _inFlight;
        private static long _lastRequestUtcTicks;
        private static long _rateLimitUntilUtcTicks;

        public const int MinIntervalMs = 10000;
        public const int RateLimitCooldownMs = 30000;

        public static Cspaq12200GateLease TryEnter(string reason, string caller, bool bypassThrottle = false)
        {
            reason = string.IsNullOrWhiteSpace(reason) ? "UNKNOWN" : reason.Trim();
            caller = string.IsNullOrWhiteSpace(caller) ? "UNKNOWN" : caller.Trim();

            var lease = new Cspaq12200GateLease(reason, caller);
            DateTime nowUtc = DateTime.UtcNow;

            lock (Sync)
            {
                long untilTicks = _rateLimitUntilUtcTicks;
                if (untilTicks > nowUtc.Ticks)
                {
                    lease.Allowed = false;
                    lease.SkipReason = Cspaq12200GateSkipReason.RateLimitCooldown;
                    lease.RateLimitUntilLocal = new DateTime(untilTicks, DateTimeKind.Utc).ToLocalTime();
                    LogSkip(lease);
                    return lease;
                }

                if (_inFlight)
                {
                    lease.Allowed = false;
                    lease.SkipReason = Cspaq12200GateSkipReason.AlreadyRunning;
                    LogSkip(lease);
                    return lease;
                }

                long lastTicks = _lastRequestUtcTicks;
                if (!bypassThrottle && lastTicks > 0)
                {
                    long agoMs = (long)(nowUtc - new DateTime(lastTicks, DateTimeKind.Utc)).TotalMilliseconds;
                    lease.LastRequestAgoMs = agoMs;
                    if (agoMs < MinIntervalMs)
                    {
                        lease.Allowed = false;
                        lease.SkipReason = Cspaq12200GateSkipReason.Throttle;
                        LogSkip(lease);
                        return lease;
                    }
                }

                _inFlight = true;
                _lastRequestUtcTicks = nowUtc.Ticks;
                lease.Allowed = true;
                lease.SkipReason = Cspaq12200GateSkipReason.None;
                if (lastTicks > 0)
                    lease.LastRequestAgoMs = (long)(nowUtc - new DateTime(lastTicks, DateTimeKind.Utc)).TotalMilliseconds;
                else
                    lease.LastRequestAgoMs = -1;
            }

            Console.WriteLine("[CSPAQ12200][GATE][ALLOW]");
            Console.WriteLine("reason=" + lease.Reason);
            Console.WriteLine("caller=" + lease.Caller);
            Console.WriteLine("lastRequestAgoMs=" + lease.LastRequestAgoMs);
            return lease;
        }

        internal static void Exit(Cspaq12200GateLease lease)
        {
            if (lease == null || !lease.Allowed)
                return;

            lock (Sync)
            {
                _inFlight = false;
            }
        }

        internal static void ReportResult(Cspaq12200GateLease lease, int rc)
        {
            if (rc != -21)
                return;

            ReportRateLimited(rc);
        }

        public static void ReportRateLimited(int rc)
        {
            DateTime untilUtc = DateTime.UtcNow.AddMilliseconds(RateLimitCooldownMs);
            lock (Sync)
            {
                _rateLimitUntilUtcTicks = untilUtc.Ticks;
            }

            Console.WriteLine("[CSPAQ12200][GATE][RATE_LIMIT]");
            Console.WriteLine("rc=" + rc);
            Console.WriteLine("cooldownMs=" + RateLimitCooldownMs);
            Console.WriteLine("until=" + untilUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"));
        }

        public static bool IsRateLimitSkip(Cspaq12200GateLease lease)
        {
            return lease != null && lease.SkipReason == Cspaq12200GateSkipReason.RateLimitCooldown;
        }

        public static string SkipMessage(Cspaq12200GateLease lease)
        {
            if (lease == null)
                return "CSPAQ12200 gate skip";

            return "CSPAQ12200 gate skip: " + lease.SkipReason;
        }

        private static void LogSkip(Cspaq12200GateLease lease)
        {
            Console.WriteLine("[CSPAQ12200][GATE][SKIP]");
            Console.WriteLine("reason=" + lease.Reason);
            Console.WriteLine("caller=" + lease.Caller);
            Console.WriteLine("skipReason=" + lease.SkipReason);

            if (lease.SkipReason == Cspaq12200GateSkipReason.Throttle)
            {
                Console.WriteLine("lastRequestAgoMs=" + lease.LastRequestAgoMs);
                Console.WriteLine("minIntervalMs=" + MinIntervalMs);
            }
            else if (lease.SkipReason == Cspaq12200GateSkipReason.RateLimitCooldown && lease.RateLimitUntilLocal.HasValue)
            {
                Console.WriteLine("until=" + lease.RateLimitUntilLocal.Value.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            }
        }
    }
}
