using System;
using System.Threading;
using Exercise_1.Domain;

namespace Exercise_1
{
    // 실거래용 자리(나중에 XING XARealClass 붙이세요)
    public sealed class RealTimeFeed : IMarketFeed
    {
        public event Action<Tick> OnTick;
        private Timer _tm;
        public void Start(string symbol)
        {
            // 데모: 1초마다 가짜 틱 발행
            _tm = new Timer(_ =>
            {
                OnTick?.Invoke(new Tick { Symbol = symbol, TsKst = DateTimeOffset.Now, Price = 50000 });
            }, null, 1000, 1000);
        }
        public void Stop() => _tm?.Dispose();
    }


}
