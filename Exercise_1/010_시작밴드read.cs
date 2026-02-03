/// DB에서 kodex200_new 를 읽어서
/// 1) BandList 로드
/// 2) 시작밴드 계산 까지 담당하는 전용 모듈

using System;
using System.ComponentModel;
using System.Data.SQLite;
using System.Linq;
using System.Windows.Forms;

namespace Exercise_1
{
    public static class 시작밴드Read
    {
        /// <summary>
        /// DB에서 밴드 전체를 읽어 BindingList 로 반환
        /// (기존 구조 유지)
        /// </summary>
        public static BindingList<BandRange> LoadAllBands()
        {
            var list = new BindingList<BandRange>();

            using (var conn = new SQLiteConnection(Login.ConnStr))
            using (var cmd = new SQLiteCommand(@"
        SELECT band,
               팔가격,
               산가격,
               살가격,
               qty,
               sina,
               from_band,
               from_qty,
               진짜산가격
        FROM kodex200_new
        ORDER BY band;
    ", conn))
            {
                conn.Open();
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new BandRange
                        {
                            Band = r.IsDBNull(0) ? 0 : r.GetInt32(0),

                            // ✅ DB 컬럼명 그대로 사용 (alias 제거)
                            팔가격 = r.IsDBNull(1) ? 0 : r.GetInt64(1),
                            산가격 = r.IsDBNull(2) ? 0 : r.GetInt64(2),
                            살가격 = r.IsDBNull(3) ? 0 : r.GetInt64(3),

                            Qty = r.IsDBNull(4) ? 0 : r.GetInt64(4),
                            Sina = r.IsDBNull(5) ? 0 : r.GetInt64(5),
                            From_Band = r.IsDBNull(6) ? 0 : r.GetInt32(6),
                            From_Qty = r.IsDBNull(7) ? 0 : r.GetInt64(7),
                            진짜산가격 = r.IsDBNull(8) ? 0 : r.GetInt64(8)
                        });
                    }
                }
            }

            return list;
        }


        /// <summary>
        /// 시작밴드 계산
        /// 규칙: qty > 0 인 밴드 중 band 번호가 가장 큰 값
        /// </summary>
        public static int CalcStartBand(BindingList<BandRange> bands)
        {
            if (bands == null || bands.Count == 0)
                return -1;

            int startBand = -1;

            foreach (var b in bands)
            {
                if (b != null && b.Qty > 0 && b.Band > startBand)
                    startBand = b.Band;
            }

            return startBand;
        }

        /// <summary>
        /// ✔ 최종 헬퍼
        /// DB → BandList 로드 + 시작밴드 계산까지 한 번에
        /// </summary>
        public static BindingList<BandRange> LoadBandsAndSetStartBand()
        {
            var bands = LoadAllBands();

            Login.시작밴드변수 = CalcStartBand(bands);

            Console.WriteLine($"[시작밴드Read]\n시작밴드 = {Login.시작밴드변수}");

            return bands;
        }
    }
}
