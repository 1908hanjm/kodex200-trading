// 배정금_한도체크.cs
// ------------------------------------------------------------
// ✅ 프로세스 6: 매수 시점 band별 배정금 한도 clamp
//
// 역할:
// - band별 배정금(고정 ceiling)과 현재 보유금액(qty × 진짜산가격)을 조회
// - 요청 수량이 남은 한도를 넘으면 한도까지만 사도록 clamp
// - 한도 초과로 매수되지 못한 금액(leftoverCash)은 호출부에서
//   AddOverLimitLeftoverCash()로 "한도초과_남은현금"에 누적시킨다.
//   (장종료 배치 프로세스3에서 실현손익과 합쳐 10개 band에 재분배됨)
//
// 적용 대상 (세 지점 전부 이 클래스를 통해 clamp):
// - 0300_밴드매칭.cs   : 일반 BUY / 10전슬라이딩 BUY (동일 코드 경로)
// - 2160_강제슬라이딩실행.cs : Down-Slide BUY (targetBuyBand = SwapRecordBandK)
// - 2310_상승슬라이딩실행순서.cs : Up-Swap BUY (buyBand = targetBand)
//
// 주의:
// - 이 클래스는 매매 로직 자체를 바꾸지 않는다. 이미 계산된 "요청 수량"을
//   band 한도 기준으로 한 번 더 clamp할 뿐이다.
// - DB 접근은 기존 2160/0700과 동일하게 Login.ConnStr을 직접 사용한다.
//
// ------------------------------------------------------------
// ✅ [FIX 2026-07-14] band 그룹 매핑 추가
// ------------------------------------------------------------
// 배정금 테이블에는 band 1~10에 대한 한도만 저장되어 있음.
// 그런데 실제 거래 band는 11, 21, 31, ... 90 처럼 10을 넘어갈 수 있고,
// 정책상 "밴드11/21/31/41은 밴드1과 같은 한도, 밴드10/20/...90은
// 밴드10과 같은 한도"를 따르도록 되어 있음 (10 단위로 순환).
//
// 수정 전에는 GetBandCapitalLimit(90)이 band=90 행을 그대로 찾다가
// 존재하지 않아 0을 반환 → 항상 clampedQty=0으로 강제매수가 전부 막히는
// 버그가 있었음 (2026-07-14 09:06 강제슬라이딩 매수 0건 사건으로 확인됨).
//
// 수정: 한도(ceiling) 조회 시에만 band를 1~10 그룹으로 매핑한다.
// 보유금액(GetBandHoldingValue)은 실제 band 그대로 조회한다.
// (각 band는 "같은 한도 금액"을 독립적으로 적용받을 뿐, 한도를
//  여러 band가 공유(합산)하는 것이 아니다.)
// ------------------------------------------------------------

using System;
using System.Data.SQLite;
using System.Globalization;

namespace Exercise_1
{
    public static class 배정금_한도체크
    {
        // band 번호를 1~10 그룹으로 매핑한다.
        // 1→1, 10→10, 11→1, 20→10, 21→1, 90→10 ...
        public static int MapToBandGroup(int band)
        {
            int g = band % 10;
            if (g == 0) g = 10;
            return g;
        }

        // band의 고정 배정금(ceiling)을 조회. 행이 없으면 0.
        // ※ 조회 시 band를 1~10 그룹으로 매핑해서 찾는다.
        public static double GetBandCapitalLimit(int band)
        {
            int groupBand = MapToBandGroup(band);

            try
            {
                using (var conn = new SQLiteConnection(Login.ConnStr))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT 배정금액 FROM 배정금 WHERE band = @b;";
                        cmd.Parameters.AddWithValue("@b", groupBand);

                        object result = cmd.ExecuteScalar();
                        if (result == null || result == DBNull.Value)
                        {
                            Console.WriteLine("[BAND_CAP][GetBandCapitalLimit][NO_ROW] band=" + band +
                                              " groupBand=" + groupBand + " -> 0");
                            return 0.0;
                        }
                        return Convert.ToDouble(result, CultureInfo.InvariantCulture);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[BAND_CAP][GetBandCapitalLimit][EX] band=" + band +
                                  " groupBand=" + groupBand + " " + ex.Message);
                return 0.0;
            }
        }

        // ✅ [2026-07-14 추가] 전체 배정금 총합 조회 ("5억" 등 총 자본금).
        // band 1~10 그룹 각각의 배정금액을 그대로 합산하므로,
        // 나중에 밴드 수/밴드당 한도가 바뀌어도 하드코딩 없이 자동 반영된다.
        public static double GetTotalBandCapitalLimit()
        {
            try
            {
                using (var conn = new SQLiteConnection(Login.ConnStr))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT SUM(배정금액) FROM 배정금;";

                        object result = cmd.ExecuteScalar();
                        if (result == null || result == DBNull.Value)
                        {
                            Console.WriteLine("[BAND_CAP][GetTotalBandCapitalLimit][NO_ROW] -> 0");
                            return 0.0;
                        }
                        return Convert.ToDouble(result, CultureInfo.InvariantCulture);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[BAND_CAP][GetTotalBandCapitalLimit][EX] " + ex.Message);
                return 0.0;
            }
        }

        // band의 현재 보유금액(qty × 진짜산가격)을 조회. 행이 없으면 0.
        // ※ 보유금액은 그룹이 아니라 실제 band 그대로 조회한다 (각자 보유분).
        public static double GetBandHoldingValue(int band)
        {
            try
            {
                using (var conn = new SQLiteConnection(Login.ConnStr))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT qty, 진짜산가격 FROM kodex200_new WHERE band = @b;";
                        cmd.Parameters.AddWithValue("@b", band);

                        using (var rd = cmd.ExecuteReader())
                        {
                            if (rd.Read())
                            {
                                long qty = rd["qty"] != DBNull.Value ? Convert.ToInt64(rd["qty"]) : 0L;
                                long price = rd["진짜산가격"] != DBNull.Value ? Convert.ToInt64(rd["진짜산가격"]) : 0L;
                                return (double)qty * (double)price;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[BAND_CAP][GetBandHoldingValue][EX] band=" + band + " " + ex.Message);
            }
            return 0.0;
        }

        // 남은 한도 = 배정금(그룹 기준) - 현재보유금액(실제 band 기준) (음수면 0)
        public static double GetRemainingCapacity(int band)
        {
            double limit = GetBandCapitalLimit(band);       // 그룹으로 매핑된 한도
            double holding = GetBandHoldingValue(band);     // 실제 band 보유액
            double remaining = limit - holding;

            Console.WriteLine("[BAND_CAP][GetRemainingCapacity] band=" + band +
                              " groupBand=" + MapToBandGroup(band) +
                              " limit=" + limit.ToString("F2", CultureInfo.InvariantCulture) +
                              " holding=" + holding.ToString("F2", CultureInfo.InvariantCulture) +
                              " remaining=" + Math.Max(remaining, 0.0).ToString("F2", CultureInfo.InvariantCulture));

            return remaining < 0 ? 0.0 : remaining;
        }

        // requestedQty를 band의 남은 한도 내로 clamp.
        // 반환: clampedQty(실제 매수 가능 수량), leftoverCash(한도초과로 못 산 금액)
        public static (long clampedQty, double leftoverCash) ClampToBandCapital(
            int band,
            long requestedQty,
            long price)
        {
            if (requestedQty <= 0 || price <= 0)
                return (0L, 0.0);

            double remaining = GetRemainingCapacity(band);
            long capQty = remaining <= 0 ? 0L : (long)Math.Floor(remaining / (double)price);

            long clampedQty = Math.Min(requestedQty, capQty);
            if (clampedQty < 0) clampedQty = 0;

            long leftoverQty = requestedQty - clampedQty;
            double leftoverCash = leftoverQty > 0 ? (double)leftoverQty * (double)price : 0.0;

            Console.WriteLine("[BAND_CAP][CLAMP] band=" + band +
                              " groupBand=" + MapToBandGroup(band) +
                              " requestedQty=" + requestedQty +
                              " capQty=" + capQty +
                              " clampedQty=" + clampedQty +
                              " leftoverCash=" + leftoverCash.ToString("F2", CultureInfo.InvariantCulture));

            return (clampedQty, leftoverCash);
        }

        // 한도초과로 매수되지 못한 금액을 "오늘자 한도초과_남은현금"에 누적.
        // 장종료 배치(ApplyDailyRealizedPnlToBandCapital)가 이 값을 실현손익과
        // 합산해 10개 band에 재분배하고 0으로 리셋한다.
        public static void AddOverLimitLeftoverCash(double amount)
        {
            if (amount <= 0) return;

            try
            {
                string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

                using (var conn = new SQLiteConnection(Login.ConnStr))
                {
                    conn.Open();

                    using (var cmdSchema = conn.CreateCommand())
                    {
                        cmdSchema.CommandText =
                            "CREATE TABLE IF NOT EXISTS 한도초과_남은현금 (" +
                            "일자 TEXT NOT NULL PRIMARY KEY, " +
                            "누적금액 REAL NOT NULL DEFAULT 0);";
                        cmdSchema.ExecuteNonQuery();
                    }

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            "INSERT INTO 한도초과_남은현금 (일자, 누적금액) VALUES (@today, @amt) " +
                            "ON CONFLICT(일자) DO UPDATE SET 누적금액 = 누적금액 + @amt;";
                        cmd.Parameters.AddWithValue("@today", today);
                        cmd.Parameters.AddWithValue("@amt", amount);
                        cmd.ExecuteNonQuery();
                    }

                    Console.WriteLine("[BAND_CAP][LEFTOVER][ADD] today=" + today +
                                      " amount=" + amount.ToString("F2", CultureInfo.InvariantCulture));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[BAND_CAP][AddOverLimitLeftoverCash][EX] " + ex.Message);
            }
        }
    }
}
