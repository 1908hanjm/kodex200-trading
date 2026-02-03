using System;
using System.Data.SQLite;

namespace Exercise_1
{
    public sealed class 자료수집 : IDisposable
    {
        private readonly string _connStr;

        public 자료수집(string connStr)
        {
            _connStr = connStr;
        }

        /// <summary>
        /// 틱 한 건을 DB에 기록
        /// </summary>
        public void InsertTick(long price)
        {
            try
            {
                using (var conn = new SQLiteConnection(_connStr))
                using (var cmd = new SQLiteCommand(
                    "INSERT INTO ticks_test (ts, price) " +
                    "VALUES (datetime('now','localtime'), @price);", conn))
                {
                    conn.Open();
                    cmd.Parameters.AddWithValue("@price", price);
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                // 필요하면 로그만 남기고 무시해도 됩니다.
                System.Diagnostics.Debug.WriteLine("[자료수집] " + ex.Message);
            }
        }

        public void Dispose()
        {
            // 현재 구조에서는 정리할 리소스 없음
        }
    }
}
