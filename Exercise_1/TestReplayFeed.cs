// TestReplayFeed.cs
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Exercise_1.Domain
{
    public sealed class TestReplayFeed : IMarketFeed, IDisposable
    {
        private readonly string _path;
        private CancellationTokenSource _cts;

        public TestReplayFeed() : this(null) { }
        public TestReplayFeed(string path) { _path = path; }

        public event Action<Exercise_1.Tick> OnTick;

        public void Start(string symbol)
        {
            _cts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                var rnd = new Random();
                while (!_cts.IsCancellationRequested)
                {
                    var t = new Exercise_1.Tick
                    {
                        Symbol = symbol,
                        TsKst = DateTimeOffset.Now,
                        Price = 70000 + rnd.NextDouble() * 1000,
                        CVol = 0,
                        Bid1 = 0,
                        Ask1 = 0,
                    };
                    OnTick?.Invoke(t);
                    await Task.Delay(500, _cts.Token).ConfigureAwait(false);
                }
            }, _cts.Token);
        }

        public void Stop() { try { _cts?.Cancel(); } catch { } }
        public void Dispose() { Stop(); }
    }
}
