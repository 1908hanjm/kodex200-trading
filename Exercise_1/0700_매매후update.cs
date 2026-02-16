// 0700_매매후update.cs  (복붙용 / C# 7.3)  [✅LV3 컬럼/타이틀 보호 + ✅부분체결 시 밴드이동 금지(UNLOCK 후 Finalize)]
// ------------------------------------------------------------
// ✅ 이번 수정 핵심(당신 정책 반영):
// - 부분체결 시에는 DB/메모리 Qty만 반영한다. (ApplyFillOnly)
// - StartBand/FocusBand 이동 + Reset/UI/LV Reload는 "LOCK이 풀린 직후" 1회만 수행한다. (FinalizeAfterUnlock)
//   => "주문 내고 LOCK, LOCK이 풀리면 그때 밴드 이동" 정책 그대로 구현
//
// ✅ 기존 보호 유지:
// - DB Reload는 listView1(밴드표)만 수행한다.
// - listView3(주문/체결 목록)는 0700에서 절대 Clear/Columns 재구성하지 않는다.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Data.SQLite;
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

        // ------------------------------------------------------------
        // ✅ (호환용) 기존 호출이 남아있어도 컴파일/동작은 되게 유지
        // - 이제 이 메서드는 "Fill만 반영"하고 Finalize(밴드 이동)는 하지 않는다.
        // - Finalize는 0650에서 UNLOCK 직후 FinalizeAfterUnlock()로만 호출한다.
        // ------------------------------------------------------------
        public void AfterFillUpdate(int band, int deltaQty, double price, string side, long execNo)
        {
            ApplyFillOnly(band, deltaQty, price, side, execNo);

            // 안전 로그(정책 확인용)
            Console.WriteLine("[0700] AfterFillUpdate called -> ApplyFillOnly ONLY (Finalize is moved to FinalizeAfterUnlock)");
        }

        // ------------------------------------------------------------
        // ✅ 1) 부분체결 포함: 체결수량만큼 Qty 반영 (메모리+DB)
        //    ❌ StartBand 재계산 / Reset / UI / LV Reload 절대 금지
        // ------------------------------------------------------------
        public void ApplyFillOnly(int band, int deltaQty, double price, string side, long execNo)
        {
            try
            {
                side = (side ?? "").Trim();
                if (band <= 0 || deltaQty <= 0) return;

                long diffQty = (side == "매수") ? +deltaQty :
                               (side == "매도") ? -deltaQty : 0;
                if (diffQty == 0) return;

                // ✅ 사용자 규칙: BUY는 K+1 반영, SELL은 K 반영
                int applyBand = (side == "매수") ? band + 1 : band;

                var br = Login.BandList.FirstOrDefault(x => x.Band == applyBand);
                if (br == null)
                {
                    Console.WriteLine($"[0700] ApplyFillOnly: BandList missing applyBand={applyBand} (side={side}, K={band})");
                    return;
                }

                long oldQty = br.Qty;
                long newQty = Math.Max(0, oldQty + diffQty);
                br.Qty = newQty;

                Console.WriteLine($"[0700] QtyUpdate(mem) side={side} applyBand={applyBand} {oldQty}->{newQty} (execNo={execNo})");

                UpdateQtyInKodex200New(applyBand, newQty);

                // ❌ 여기서 StartBand 재계산/Reset/UI/LV Reload 하지 않는다!
            }
            catch (Exception ex)
            {
                Console.WriteLine("[0700 ERROR][ApplyFillOnly] " + ex);
            }
        }

        // ------------------------------------------------------------
        // ✅ 2) UNLOCK 직후 1회만 호출: StartBand 이동 + Reset + UI + LV Reload
        // ------------------------------------------------------------
        public void FinalizeAfterUnlock(string sideKor, double lastFillPrice)
        {
            try
            {
                sideKor = (sideKor ?? "").Trim();

                int oldStart = Login.시작밴드변수;
                int newStart = RecalcStartBandFromBandList();

                Login.시작밴드변수 = newStart;
                _login.CurrentStartBand = newStart;

                Console.WriteLine($"[0700] StartBand {oldStart} -> {newStart} (FinalizeAfterUnlock)");

                TryResetDecisionEngine_MUST_BE_LAST(sideKor);
                TryRenderBandsLikeBefore_MUST_BE_LAST(lastFillPrice);
                TryReloadListViews_MUST_BE_LAST(); // ✅ LV1만
            }
            catch (Exception ex)
            {
                Console.WriteLine("[0700 ERROR][FinalizeAfterUnlock] " + ex);
            }
        }

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

        private void TryRenderBandsLikeBefore_MUST_BE_LAST(double price)
        {
            if (!_login.IsHandleCreated) return;

            int p = (int)Math.Round(price);
            if (p % 10 != 0) p = (p / 10) * 10;

            _login.BeginInvoke(new Action(() =>
            {
                try
                {
                    var rtbField = typeof(Login).GetField("richTextBox1",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    var rtb = rtbField?.GetValue(_login) as RichTextBox;
                    rtb?.Clear();

                    var recentField = typeof(Login).GetField("_recent", BindingFlags.Instance | BindingFlags.NonPublic);
                    (recentField?.GetValue(_login) as System.Collections.IList)?.Clear();

                    var lastField = typeof(Login).GetField("_lastUiTickPrice", BindingFlags.Instance | BindingFlags.NonPublic);
                    lastField?.SetValue(_login, double.NaN);

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

        private void TryReloadListViews_MUST_BE_LAST()
        {
            if (!_login.IsHandleCreated) return;

            _login.BeginInvoke(new Action(() =>
            {
                try
                {
                    var lv1 = GetListViewByFieldName("listView1");
                    var lv3 = GetListViewByFieldName("listView3"); // ✅ 존재 확인만 (건드리지 않음)

                    var rows = LoadBandsFromDb_kodex200_new();

                    bool ok1 = false;

                    if (lv1 != null)
                    {
                        RenderBandsToListView(lv1, rows);
                        ok1 = true;
                    }

                    // ✅ 핵심: LV3는 절대 Clear/Columns 재구성하지 않는다.
                    bool lv3Exists = (lv3 != null);

                    Console.WriteLine($"[0700][LV] Reload(DB) OK lv1={ok1} lv3(untouched)={lv3Exists} rows={rows.Count}");
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

                // 시작밴드 선택 표시
                try
                {
                    int startBand = Login.시작밴드변수;
                    if (startBand > 0)
                    {
                        foreach (ListViewItem it2 in lv.Items)
                        {
                            if (it2 == null) continue;
                            if (int.TryParse(it2.Text, out int b) && b == startBand)
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
                    cmd.Parameters.AddWithValue("@q", qty); // ✅ 오타 수정(AddWAithValue -> AddWithValue)
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
// 2026-02-11 90742
