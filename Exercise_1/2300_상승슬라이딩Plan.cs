// 2300_상승슬라이딩Plan.cs
// ------------------------------------------------------------
// 역할:
// - SELL FIRE 발생 시 상승슬라이딩 여부 판정
// - from_band / from_qty 기반 1단계 복구 계획 생성
// ------------------------------------------------------------

using System;
using System.Data.SQLite;

namespace Exercise_1
{
    public sealed class _2300_상승슬라이딩Plan
    {
        public sealed class UpSlidePlan
        {
            public bool IsUpSlide;
            public int SellBand;
            public int SellQty;
            public int BuyBand;
            public int FromQty;
            public int ExtraQty;
        }

        private const string CHAIN_NONE = "CHAIN_NONE";
        private const string CHAIN_ACTIVE = "CHAIN_ACTIVE";
        private const string CHAIN_PENDING = "CHAIN_PENDING";
        private const string CHAIN_DONE = "CHAIN_DONE";

        public static UpSlidePlan TryMakePlan(int startBand)
        {
            var plan = new UpSlidePlan();
            plan.IsUpSlide = false;

            using (var conn = new SQLiteConnection(Login.ConnStr))
            {
                conn.Open();
                DB_Control.EnsureExtraQtyColumn(conn);

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT qty, from_band, from_qty, extra_qty " +
                        "FROM kodex200_new WHERE band=@b";
                    cmd.Parameters.AddWithValue("@b", startBand);

                    using (var rd = cmd.ExecuteReader())
                    {
                        if (!rd.Read()) return plan;

                        int qty = ReadInt(rd["qty"]);
                        int fromBand = ReadInt(rd["from_band"]);
                        int fromQty = ReadInt(rd["from_qty"]);
                        int extraQty = ReadInt(rd["extra_qty"]);

                        string state = ResolveChainState(fromBand, fromQty, extraQty);
                        Console.WriteLine(
                            "[CHAIN][STATE] " +
                            "Band=" + startBand +
                            " Qty=" + qty +
                            " FromBand=" + fromBand +
                            " FromQty=" + fromQty +
                            " ExtraQty=" + extraQty +
                            " State=" + state);

                        if (state == CHAIN_DONE)
                        {
                            // [정책 변경 2026-07-02] 새 정책: 메타 clear는 BUY 최종 완료 후
                            // ApplyFillOnly에서만 수행한다. 계획 생성 단계에서
                            // BUY 완료 증거 없이 메타를 clear하면 안 된다.
                            // CHAIN_DONE 상태(from_band>0, from_qty=0, extra_qty=0)는
                            // 이미 BUY가 완료되어 메타 정리된 후 residual 상태이므로
                            // 상승슬라이딩 대상 아님으로 리턴만 한다.
                            Console.WriteLine(
                                "[2300][CHAIN_DONE][SKIP] " +
                                "BUY 완료 후 잔여 상태 - 메타 clear 생략 " +
                                "band=" + startBand +
                                " fromBand=" + fromBand +
                                " fromQty=" + fromQty +
                                " extraQty=" + extraQty);
                            return plan;
                        }

                        if (state == CHAIN_NONE)
                            return plan;

                        if (fromBand <= 0)
                        {
                            Console.WriteLine(
                                "[2300][PLAN][SKIP] reason=no_from_band chain_kept=true " +
                                "band=" + startBand +
                                " qty=" + qty +
                                " fromBand=" + fromBand +
                                " fromQty=" + fromQty +
                                " extraQty=" + extraQty);
                            return plan;
                        }

                        if (qty <= 0)
                        {
                            Console.WriteLine(
                                "[2300][PLAN][SKIP] reason=no_sell_qty chain_kept=true " +
                                "band=" + startBand +
                                " qty=" + qty +
                                " fromBand=" + fromBand +
                                " fromQty=" + fromQty +
                                " extraQty=" + extraQty);
                            return plan;
                        }

                        if (fromQty <= 0)
                        {
                            Console.WriteLine(
                                "[2300][PLAN][SKIP] reason=no_from_qty chain_kept=true " +
                                "band=" + startBand +
                                " qty=" + qty +
                                " fromBand=" + fromBand +
                                " fromQty=" + fromQty +
                                " extraQty=" + extraQty);
                            return plan;
                        }

                        plan.IsUpSlide = true;
                        plan.SellBand = startBand;
                        plan.SellQty = qty;
                        plan.BuyBand = fromBand;
                        plan.FromQty = fromQty;
                        plan.ExtraQty = extraQty;

                        Console.WriteLine(
                            $"[2300][PLAN] K={startBand} -> from={fromBand} sellQty={qty} fromQty={fromQty} extraQty={extraQty}");
                    }
                }
            }

            return plan;
        }

        private static string ResolveChainState(int fromBand, int fromQty, int extraQty)
        {
            if (fromBand <= 0 && fromQty <= 0 && extraQty <= 0)
                return CHAIN_NONE;

            if (fromBand > 0 && fromQty <= 0 && extraQty <= 0)
                return CHAIN_DONE;

            if (fromBand > 0 && (fromQty > 0 || extraQty > 0))
                return CHAIN_PENDING;

            if (fromBand > 0)
                return CHAIN_ACTIVE;

            return CHAIN_ACTIVE;
        }

        private static void CloseChain(SQLiteConnection conn, int band, string reason)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "UPDATE kodex200_new " +
                    "SET from_band = 0, from_qty = 0, extra_qty = 0 " +
                    "WHERE band = @b";
                cmd.Parameters.AddWithValue("@b", band);
                cmd.ExecuteNonQuery();
            }

            Console.WriteLine(
                "[CHAIN][CLOSE] " +
                "Band=" + band +
                " Reason=" + reason);
        }

        private static int ReadInt(object value)
        {
            if (value == null || value == DBNull.Value)
                return 0;
            return Convert.ToInt32(value);
        }
    }
}
