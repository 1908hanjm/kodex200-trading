using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Exercise_1
{
    public sealed class test_tick_read
    {
        private readonly _0250_Tick_Process _engine;

        public test_tick_read(_0250_Tick_Process engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        public async Task RunAsync(int sec)
        {
            int delayMs = sec * 1000;

            var ticks = LoadTicks();

            int seq = 0;

            foreach (var price in ticks)
            {
                seq++;

                // 🔴 메시지 박스: 번호-틱값
                //MessageBox.Show(
                //    $"REPLAY TICK\n\n번호: {seq}\n가격: {price}",
                //    "Replay Confirm",
                //    MessageBoxButtons.OK,
                //    MessageBoxIcon.Information);

                Console.WriteLine($"[REPLAY] seq={seq} price={price}");

                await _engine.ProcessTickAsync(price);

                if (delayMs > 0)
                    await Task.Delay(delayMs);
            }

            Console.WriteLine("[REPLAY] END");
        }

        private List<double> LoadTicks()
        {
            var list = new List<double>();

            using (var conn = new SQLiteConnection(Login.ConnStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT price FROM from_test ORDER BY ts ASC";

                    using (var rd = cmd.ExecuteReader())
                    {
                        while (rd.Read())
                        {
                            list.Add(Convert.ToDouble(rd["price"]));
                        }
                    }
                }
            }

            return list;
        }
    }
}