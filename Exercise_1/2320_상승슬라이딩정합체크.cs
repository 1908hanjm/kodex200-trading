// 2320_상승슬라이딩정합체크.cs
// ------------------------------------------------------------
// 역할:
// - 상승슬라이딩 체인 무결성 검사
// - qty/from_band/from_qty 정합성 확인
// ------------------------------------------------------------

using System;
using System.Data.SQLite;

namespace Exercise_1
{
    public sealed class _2320_상승슬라이딩정합체크
    {
        public static void ValidateBand(int band)
        {
            using (var conn = new SQLiteConnection(Login.ConnStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT qty, from_band, from_qty " +
                        "FROM kodex200_new WHERE band=@b";
                    cmd.Parameters.AddWithValue("@b", band);

                    using (var rd = cmd.ExecuteReader())
                    {
                        if (!rd.Read()) return;

                        int qty = Convert.ToInt32(rd["qty"]);
                        object fb = rd["from_band"];
                        object fq = rd["from_qty"];

                        if (fb != DBNull.Value && fq == DBNull.Value)
                        {
                            Console.WriteLine(
                                $"[2320][FAIL] band={band} from_band exists but from_qty NULL");
                        }
                    }
                }
            }
        }
    }
}
