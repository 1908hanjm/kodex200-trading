using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;

namespace Exercise_1
{
    public sealed class _0800_이월상태점검
    {
        private readonly string _connStr;
        private readonly Action<string> _log;

        public _0800_이월상태점검(string connStr, Action<string> log = null)
        {
            _connStr = connStr;
            _log = log ?? (_ => { });
        }

        public sealed class Row
        {
            public int Band { get; set; }
            public long Qty { get; set; }
            public int FromBand { get; set; }
            public long FromQty { get; set; }

            //h 구 pending 컬럼 제거 이후 호환성 유지를 위해 프로퍼티는 남겨둠.
            //h 현재 0800에서는 사용하지 않으며 항상 "0"으로 둔다.
        }

        public sealed class Snapshot
        {
            public List<Row> AllRows { get; set; } = new List<Row>();
            public List<Row> HeldRows { get; set; } = new List<Row>();

            //h 구 pending 구조 제거 단계.
            //h 0830/0840 컴파일 호환을 위해 멤버는 유지하되 현재는 빈 리스트/false로만 사용.
            public List<Row> PendingRows { get; set; } = new List<Row>();

            public int HeldCount { get; set; }
            public int MinHeldBand { get; set; }
            public int MaxHeldBand { get; set; }

            public bool HasPending { get; set; }
            public bool HasUpPending { get; set; }
            public bool HasDownPending { get; set; }

            public Row FirstPendingRow { get; set; }
        }

        public Snapshot Run()
        {
            var snap = new Snapshot();

            using (var conn = new SQLiteConnection(_connStr))
            {
                conn.Open();

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT " +
                        " band, " +
                        " COALESCE(qty,0)        AS qty, " +
                        " COALESCE(from_band,0)  AS from_band, " +
                        " COALESCE(from_qty,0)   AS from_qty " +
                        "FROM kodex200_new " +
                        "ORDER BY band ASC";

                    using (var rd = cmd.ExecuteReader())
                    {
                        while (rd.Read())
                        {
                            var row = new Row
                            {
                                Band = Convert.ToInt32(rd["band"]),
                                Qty = Convert.ToInt64(rd["qty"]),
                                FromBand = Convert.ToInt32(rd["from_band"]),
                                FromQty = Convert.ToInt64(rd["from_qty"])
                            };

                            snap.AllRows.Add(row);
                        }
                    }
                }
            }

            snap.HeldRows = snap.AllRows
                .Where(r => r.Qty > 0)
                .OrderBy(r => r.Band)
                .ToList();

            snap.HeldCount = snap.HeldRows.Count;
            snap.MinHeldBand = snap.HeldRows.Count > 0 ? snap.HeldRows.Min(r => r.Band) : 0;
            snap.MaxHeldBand = snap.HeldRows.Count > 0 ? snap.HeldRows.Max(r => r.Band) : 0;

            //h 구 pending 제거 단계이므로 0800에서는 pending을 판단하지 않는다.
            //h 새 Pending 테이블 연동은 이후 단계에서 별도로 붙인다.
            snap.PendingRows = new List<Row>();
            snap.HasPending = false;
            snap.HasUpPending = false;
            snap.HasDownPending = false;
            snap.FirstPendingRow = null;

            _log("[0800] Snapshot created");
            _log("[0800] heldCount=" + snap.HeldCount);
            _log("[0800] minHeldBand=" + snap.MinHeldBand + ", maxHeldBand=" + snap.MaxHeldBand);
            _log("[0800] pending=(disabled in 0800; old column removed)");

            return snap;
        }
    }
}
// 2026-04-09 41827
