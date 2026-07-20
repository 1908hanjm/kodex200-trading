// 9000_ReplayMock.cs  (복붙용 / C# 7.3)
// ------------------------------------------------------------
// 목적:
// - Replay 틱 실행기
// - 0400 Replay Mock 브리지
// - Replay 중 실주문 차단 + 0650 가짜 체결 진입
//
// 전제:
// - Login.cs 에 public static bool IsReplayMode 존재
// - button9 에서 ReplayTickRunner 사용
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Exercise_1
{
    public sealed class ReplayTickRunner
    {
        private readonly _0250_Tick_Process _engine;

        public ReplayTickRunner(_0250_Tick_Process engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        public async Task RunAsync(double sec)
        {
            int delayMs = (int)Math.Round(sec * 1000.0, MidpointRounding.AwayFromZero);
            if (delayMs < 0) delayMs = 0;

            var ticks = LoadTicks();

            int seq = 0;
            Login.IsReplayMode = true;
            Console.WriteLine("[REPLAY MODE] ON");

            try
            {
                foreach (var price in ticks)
                {
                    seq++;

                    MessageBox.Show(
                        $"REPLAY TICK\n\n번호: {seq}\n가격: {price}",
                        "Replay Confirm",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);

                    Console.WriteLine($"[REPLAY] seq={seq} price={price}");

                    await _engine.ProcessTickAsync(price).ConfigureAwait(true);

                    if (delayMs > 0)
                        await Task.Delay(delayMs).ConfigureAwait(true);
                }

                Console.WriteLine("[REPLAY] END");
            }
            finally
            {
                Login.IsReplayMode = false;
                Console.WriteLine("[REPLAY MODE] OFF");
            }
        }

        private List<double> LoadTicks()
        {
            var list = new List<double>();

            using (var conn = new SQLiteConnection(Login.ConnStr))
            {
                conn.Open();

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT price FROM from_test ORDER BY ts ASC";

                    using (var rd = cmd.ExecuteReader())
                    {
                        while (rd.Read())
                        {
                            list.Add(Convert.ToDouble(rd["price"], CultureInfo.InvariantCulture));
                        }
                    }
                }
            }

            return list;
        }
    }

    public static class ReplayMockBridge
    {
        private static readonly object _lock = new object();
        private static long _mockOrdSeed = DateTime.Now.Ticks % 1000000000L;

        public static bool TryHandleMockOrder(
            string sideKor,
            int band,
            int qty,
            int price,
            string shcode,
            Action<string> log)
        {
            if (!Login.IsReplayMode) return false;

            sideKor = (sideKor ?? "").Trim();
            string ordNoRaw = NextMockOrdNo().ToString();

            try
            {
                log?.Invoke($"[0400][REPLAY][MOCK] side={sideKor}, band={band}, qty={qty}, price={price}, ordNo={ordNoRaw}");

                if (Login.OrdMap == null)
                    throw new InvalidOperationException("Login.OrdMap is null");

                Login.OrdMap.Register(sideKor, band, ordNoRaw, qty);
                log?.Invoke($"[0400][REPLAY][MOCK][OrdMap] Register OK side={sideKor} band={band} ordNoRaw={ordNoRaw} qty={qty}");

                if (Login.Sc1Receiver == null)
                    throw new InvalidOperationException("Login.Sc1Receiver is null");

                // ✅ 전량 완전체결로 가짜 SC1 진입
                Login.Sc1Receiver.SimulateFill(
                    ordNoRaw: ordNoRaw,
                    sideKor: sideKor,
                    band: band,
                    fillQty: qty,
                    fillPrice: price
                );

                log?.Invoke($"[0400][REPLAY][MOCK] simulate fill DONE side={sideKor}, band={band}, qty={qty}, price={price}, shcode={shcode}");
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[0400][REPLAY][MOCK] FAIL side={sideKor}, band={band}, ex={ex.Message}");
                throw;
            }
        }

        private static long NextMockOrdNo()
        {
            lock (_lock)
            {
                _mockOrdSeed++;
                if (_mockOrdSeed <= 0) _mockOrdSeed = 1;
                return _mockOrdSeed;
            }
        }
    }
}
// 2026-03-09 73146