// 0210_Tick_fromDB.cs  (복붙용 / C# 7.3)
// ------------------------------------------------------------
// 테스트용 ticks_ts에서 price를 ts ASC로 읽어 OnPrice 이벤트로 전달
// - 틱 처리 로직(돌파/꺾임/주문/UI)은 포함하지 않음
// - Login.cs에서 0250_Tick_Process로 연결하여 사용
// ------------------------------------------------------------

using System;
using System.Data.SQLite;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Exercise_1
{
    public sealed class _0210_Tick_fromDB : IDisposable
    {
        private readonly string _connStr;
        private CancellationTokenSource _cts;
        private bool _disposed;

        public event Action<double> OnPrice;
        public event Action<string> OnLog;

        public _0210_Tick_fromDB(string connStr)
        {
            _connStr = connStr ?? throw new ArgumentNullException(nameof(connStr));
        }

        public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;

        /// <summary>
        /// ticks_ts를 ts ASC로 읽어 OnPrice로 흘려보냅니다.
        /// delayMsPerTick=0이면 지연 없이 최대 속도로 재생합니다.
        /// </summary>
        public async Task StartReplayAsync(int delayMsPerTick = 0)
        {
            EnsureNotDisposed();

            Stop();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            await Task.Run(async () =>
            {
                try
                {
                    WriteLog("[DB-REPLAY] START");

                    using (var conn = new SQLiteConnection(_connStr))
                    using (var cmd = new SQLiteCommand("SELECT price FROM ticks_ts ORDER BY ts ASC;", conn))
                    {
                        conn.Open();
                        using (var rd = cmd.ExecuteReader())
                        {
                            while (!token.IsCancellationRequested && rd.Read())
                            {
                                double price = rd.GetDouble(0);

                                try { OnPrice?.Invoke(price); }
                                catch (Exception ex) { WriteLog("[DB-REPLAY] OnPrice handler error: " + ex.Message); }

                                if (delayMsPerTick > 0)
                                {
                                    try { await Task.Delay(delayMsPerTick, token); }
                                    catch { /* canceled */ }
                                }
                            }
                        }
                    }

                    WriteLog("[DB-REPLAY] END");
                }
                catch (Exception ex)
                {
                    WriteLog("[DB-REPLAY] ERROR: " + ex.Message);
                }
            }, token);
        }

        public void Stop()
        {
            try
            {
                if (_cts != null && !_cts.IsCancellationRequested)
                {
                    _cts.Cancel();
                    WriteLog("[DB-REPLAY] STOP");
                }
            }
            catch { }
        }

        private void WriteLog(string s)
        {
            try { OnLog?.Invoke(s); } catch { }
            try { Debug.WriteLine(s); } catch { }
        }

        private void EnsureNotDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(_0210_Tick_fromDB));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { Stop(); } catch { }
            try { _cts?.Dispose(); } catch { }
            _cts = null;
        }
    }
}

//2026-01-13-12-00-00
