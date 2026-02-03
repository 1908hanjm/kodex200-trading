// 0230_Tick_fromXing.cs  (복붙용 / C# 7.3)
// ------------------------------------------------------------
// 실전용: XingRealTimeFeed로부터 Tick을 받아 OnPrice/OnTick 이벤트로 전달
// - 틱 처리 로직(돌파/꺾임/주문/UI)은 포함하지 않음
// ------------------------------------------------------------
//
// 전제: 프로젝트에 XingRealTimeFeed, Tick 타입이 이미 존재
// (기존 Login.cs에서 사용하던 것과 동일)

using System;
using System.Diagnostics;

namespace Exercise_1
{
    public sealed class _0230_Tick_fromXing : IDisposable
    {
        private XingRealTimeFeed _feed;
        private bool _started;
        private bool _disposed;

        public event Action<Tick> OnTick;
        public event Action<double> OnPrice;
        public event Action<string> OnLog;

        public bool IsRunning => _started;

        public void Start(string shcode)
        {
            EnsureNotDisposed();

            if (_started) Stop();

            _feed = new XingRealTimeFeed();
            _feed.OnTick += HandleTick;
            _feed.Start(shcode);

            _started = true;
            WriteLog("[XING-TICK] START shcode=" + shcode);
        }

        public void Stop()
        {
            try
            {
                if (_feed != null)
                {
                    try { _feed.OnTick -= HandleTick; } catch { }
                    try { _feed.Stop(); } catch { }
                    _feed = null;
                }
            }
            catch { }

            if (_started) WriteLog("[XING-TICK] STOP");
            _started = false;
        }

        private void HandleTick(Tick t)
        {
            try { OnTick?.Invoke(t); } catch (Exception ex) { WriteLog("[XING-TICK] OnTick handler error: " + ex.Message); }
            try { OnPrice?.Invoke(t.Price); } catch (Exception ex) { WriteLog("[XING-TICK] OnPrice handler error: " + ex.Message); }
        }

        private void WriteLog(string s)
        {
            try { OnLog?.Invoke(s); } catch { }
            try { Debug.WriteLine(s); } catch { }
        }

        private void EnsureNotDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(_0230_Tick_fromXing));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { Stop(); } catch { }
        }
    }
}

//2026-01-13-12-00-00
