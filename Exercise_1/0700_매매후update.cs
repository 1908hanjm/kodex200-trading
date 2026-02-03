// 0700_매매후update.cs  (복붙용 / C# 7.3)
// ------------------------------------------------------------
// 역할:
// - 체결 후 BandList + DB qty 반영
// - 시작밴드 재계산 + Login.시작밴드변수 갱신
// - 매수 체결은 band+1 Qty 반영
// - 거래 후 0250 결정엔진 Reset
// - richTextBox Clear → 새 밴드 기준 재렌더
// - ✅ listView1/listView3 갱신: DB 직접 로드(확정)
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace Exercise_1
{
    public sealed class _0700_매매후update
    {
        private readonly Login _login;

        public _0700_매매후update(Login login)
        {
            _login = login ?? throw new ArgumentNullException(nameof(login));
        }

        public void AfterFillUpdate(int band, int deltaQty, double price, string side, long execNo)
        {
            try
            {
                side = (side ?? "").Trim();
                if (band <= 0 || deltaQty <= 0) return;

                long diffQty = (side == "매수") ? +deltaQty :
                               (side == "매도") ? -deltaQty : 0;
                if (diffQty == 0) return;

                int applyBand = (side == "매수") ? band + 1 : band;

                var br = Login.BandList.FirstOrDefault(x => x.Band == applyBand);
                if (br == null) return;

                long oldQty = br.Qty;
                long newQty = Math.Max(0, oldQty + diffQty);
                br.Qty = newQty;

                Console.WriteLine($"[0700] QtyUpdate(mem) side={side} applyBand={applyBand} {oldQty}->{newQty}");

                UpdateQtyInKodex200New(applyBand, newQty);

                int oldStart = Login.시작밴드변수;
                int newStart = RecalcStartBandFromBandList();
                Login.시작밴드변수 = newStart;
                _login.CurrentStartBand = newStart;

                Console.WriteLine($"[0700] StartBand {oldStart} -> {newStart}");

                // ✅ 순서: Reset → Render → ListView DB Reload
                TryResetDecisionEngine_MUST_BE_LAST(side);
                TryRenderBandsLikeBefore_MUST_BE_LAST(price);
                TryReloadListViews_MUST_BE_LAST(); // ✅ DB 직접 로드 버전
            }
            catch (Exception ex)
            {
                Console.WriteLine("[0700 ERROR] " + ex);
            }
        }

        // ─────────────────────────────────────────────
        // 거래 후 결정엔진 Reset
        // ─────────────────────────────────────────────
        private void TryResetDecisionEngine_MUST_BE_LAST(string sideKor)
        {
            if (!_login.IsHandleCreated) return;

            _login.BeginInvoke(new Action(() =>
            {
                try
                {
                    var fld = typeof(Login).GetField("_tickProcess", BindingFlags.Instance | BindingFlags.NonPublic);
                    var tp = fld?.GetValue(_login);
                    if (tp == null) return;

                    var mi = tp.GetType().GetMethod("ResetAfterTrade", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (mi != null)
                    {
                        string why = sideKor == "매수" ? "BUY(FILL)" : "SELL(FILL)";
                        mi.Invoke(tp, new object[] { why });
                        Console.WriteLine($"[0700][RESET] OK ({why})");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[0700][RESET ERROR] " + ex);
                }
            }));
        }

        // ─────────────────────────────────────────────
        // richTextBox Clear → 새 밴드 기준 재렌더
        // ─────────────────────────────────────────────
        private void TryRenderBandsLikeBefore_MUST_BE_LAST(double price)
        {
            if (!_login.IsHandleCreated) return;

            int p = (int)Math.Round(price);
            if (p % 10 != 0) p = (p / 10) * 10;

            _login.BeginInvoke(new Action(() =>
            {
                try
                {
                    // ✅ richTextBox1.Clear()
                    var rtbField = typeof(Login).GetField("richTextBox1",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    var rtb = rtbField?.GetValue(_login) as RichTextBox;
                    rtb?.Clear();

                    // ✅ _recent.Clear()
                    var recentField = typeof(Login).GetField("_recent", BindingFlags.Instance | BindingFlags.NonPublic);
                    (recentField?.GetValue(_login) as System.Collections.IList)?.Clear();

                    // ✅ last price reset
                    var lastField = typeof(Login).GetField("_lastUiTickPrice", BindingFlags.Instance | BindingFlags.NonPublic);
                    lastField?.SetValue(_login, double.NaN);

                    // 기존 렌더 호출
                    var mi = typeof(Login).GetMethod("OnTickArrived", BindingFlags.Instance | BindingFlags.NonPublic);
                    mi?.Invoke(_login, new object[] { p });

                    Console.WriteLine($"[0700][UI] Re-render OK ({p})");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[0700][UI ERROR] " + ex);
                }
            }));
        }

        // ─────────────────────────────────────────────
        // ✅ listView1 / listView3 DB 직접 로드로 강제 갱신
        // - Login의 로더 메서드 이름에 의존하지 않음 (확정)
        // - kodex200_new를 직접 SELECT 해서 렌더
        // ─────────────────────────────────────────────
        private void TryReloadListViews_MUST_BE_LAST()
        {
            if (!_login.IsHandleCreated) return;

            _login.BeginInvoke(new Action(() =>
            {
                try
                {
                    var lv1 = GetListViewByFieldName("listView1");
                    var lv3 = GetListViewByFieldName("listView3");

                    // DB에서 밴드 목록 읽기
                    var rows = LoadBandsFromDb_kodex200_new();

                    bool ok1 = false, ok3 = false;

                    if (lv1 != null)
                    {
                        RenderBandsToListView(lv1, rows);
                        ok1 = true;
                    }

                    if (lv3 != null)
                    {
                        RenderBandsToListView(lv3, rows);
                        ok3 = true;
                    }

                    Console.WriteLine($"[0700][LV] Reload(DB) OK lv1={ok1} lv3={ok3} rows={rows.Count}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[0700][LV ERROR] " + ex);
                }
            }));
        }

        private ListView GetListViewByFieldName(string fieldName)
        {
            try
            {
                var f = typeof(Login).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                return f?.GetValue(_login) as ListView;
            }
            catch { return null; }
        }

        // DB row model
        private sealed class BandRow
        {
            public int Band;
            public long Pal;
            public long San;
            public long Sal;
            public long Qty;
            public long Sina;
        }

        private List<BandRow> LoadBandsFromDb_kodex200_new()
        {
            var list = new List<BandRow>(256);

            using (var conn = new SQLiteConnection(Login.ConnStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    // ⚠️ 컬럼명은 당신 DB 기준: band, 팔가격, 산가격, 살가격, qty, sina
                    cmd.CommandText =
                        "SELECT band, 팔가격, 산가격, 살가격, qty, sina " +
                        "FROM kodex200_new " +
                        "ORDER BY band ASC";

                    using (var rd = cmd.ExecuteReader())
                    {
                        while (rd.Read())
                        {
                            var r = new BandRow
                            {
                                Band = SafeGetInt(rd, 0),
                                Pal = SafeGetLong(rd, 1),
                                San = SafeGetLong(rd, 2),
                                Sal = SafeGetLong(rd, 3),
                                Qty = SafeGetLong(rd, 4),
                                Sina = SafeGetLong(rd, 5)
                            };
                            list.Add(r);
                        }
                    }
                }
            }

            return list;
        }

        private static int SafeGetInt(SQLiteDataReader rd, int i)
        {
            try
            {
                if (rd.IsDBNull(i)) return 0;
                return Convert.ToInt32(rd.GetValue(i), CultureInfo.InvariantCulture);
            }
            catch { return 0; }
        }

        private static long SafeGetLong(SQLiteDataReader rd, int i)
        {
            try
            {
                if (rd.IsDBNull(i)) return 0;
                return Convert.ToInt64(rd.GetValue(i), CultureInfo.InvariantCulture);
            }
            catch { return 0; }
        }

        private void RenderBandsToListView(ListView lv, List<BandRow> rows)
        {
            if (lv == null) return;

            lv.BeginUpdate();
            try
            {
                lv.Clear();
                lv.View = View.Details;
                lv.FullRowSelect = true;
                lv.GridLines = true;
                lv.MultiSelect = false;
                lv.HideSelection = false;

                lv.Columns.Add("band", 70, HorizontalAlignment.Left);
                lv.Columns.Add("팔가격", 90, HorizontalAlignment.Right);
                lv.Columns.Add("산가격", 90, HorizontalAlignment.Right);
                lv.Columns.Add("살가격", 90, HorizontalAlignment.Right);
                lv.Columns.Add("qty", 70, HorizontalAlignment.Right);
                lv.Columns.Add("sina", 70, HorizontalAlignment.Right);

                foreach (var r in rows)
                {
                    var it = new ListViewItem(r.Band.ToString(CultureInfo.InvariantCulture));
                    it.SubItems.Add(r.Pal.ToString(CultureInfo.InvariantCulture));
                    it.SubItems.Add(r.San.ToString(CultureInfo.InvariantCulture));
                    it.SubItems.Add(r.Sal.ToString(CultureInfo.InvariantCulture));
                    it.SubItems.Add(r.Qty.ToString(CultureInfo.InvariantCulture));
                    it.SubItems.Add(r.Sina.ToString(CultureInfo.InvariantCulture));
                    lv.Items.Add(it);
                }

                // (선택) 시작밴드 하이라이트: qty>0 중 band 최대
                try
                {
                    int startBand = Login.시작밴드변수;
                    if (startBand > 0)
                    {
                        foreach (ListViewItem it2 in lv.Items)
                        {
                            if (it2 == null) continue;
                            int b;
                            if (int.TryParse(it2.Text, out b) && b == startBand)
                            {
                                it2.Selected = true;
                                it2.Focused = true;
                                it2.EnsureVisible();
                                break;
                            }
                        }
                    }
                }
                catch { }
            }
            finally
            {
                lv.EndUpdate();
            }
        }

        private void UpdateQtyInKodex200New(int band, long qty)
        {
            using (var conn = new SQLiteConnection(Login.ConnStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "UPDATE kodex200_new SET qty=@q, 진짜산가격=0 WHERE band=@b";
                    cmd.Parameters.AddWithValue("@q", qty);
                    cmd.Parameters.AddWithValue("@b", band);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private int RecalcStartBandFromBandList()
        {
            return Login.BandList
                .Where(b => b.Qty > 0)
                .Select(b => b.Band)
                .DefaultIfEmpty(0)
                .Max();
        }
    }
}
//2026-01-27 61408
