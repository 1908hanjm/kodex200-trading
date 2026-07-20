// 060_listView3_당일거래.cs
// 당일 주문(T0425) 결과를 listView3에 표시하는 전용 모듈

using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Exercise_1
{
    public sealed class _060_listView3_당일거래
    {
        private readonly Control _owner;
        private readonly ListView _lv;
        private readonly OrderService _orderSvc;
        private readonly Func<string> _getActNo;
        private readonly Func<string> _getPwd;
        private readonly Func<string> _getShcode;

        public _060_listView3_당일거래(
            Control owner,
            ListView lv,
            OrderService orderSvc,
            Func<string> getActNo,
            Func<string> getPwd,
            Func<string> getShcode)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _lv = lv ?? throw new ArgumentNullException(nameof(lv));
            _orderSvc = orderSvc ?? throw new ArgumentNullException(nameof(orderSvc));
            _getActNo = getActNo ?? throw new ArgumentNullException(nameof(getActNo));
            _getPwd = getPwd ?? throw new ArgumentNullException(nameof(getPwd));
            _getShcode = getShcode ?? throw new ArgumentNullException(nameof(getShcode));

            EnsureColumns();

            _lv.SelectedIndexChanged += OnSelectedIndexChanged;
        }

        private void EnsureColumns()
        {
            if (_lv.Columns.Count > 0) return;

            _lv.View = View.Details;
            _lv.FullRowSelect = true;
            _lv.GridLines = true;

            _lv.Columns.Clear();
            _lv.Columns.Add("시간", 100);
            _lv.Columns.Add("구분", 80);
            _lv.Columns.Add("수량", 70, HorizontalAlignment.Right);
            _lv.Columns.Add("가격", 90, HorizontalAlignment.Right);
            _lv.Columns.Add("주문번호", 120);
            _lv.Columns.Add("밴드", 70);
            _lv.Columns.Add("손익", 80, HorizontalAlignment.Right);
            _lv.Columns.Add("상태", 100);
        }

        public Task ReloadAsync()
        {
            return ReloadAsync(_owner, _lv, _orderSvc, _getActNo, _getPwd, _getShcode);
        }

        public static async Task<IList<OrderRow>> ReloadAsync(
            Control owner,
            ListView lv,
            OrderService orderSvc,
            Func<string> getActNo,
            Func<string> getPwd,
            Func<string> getShcode)
        {
            try
            {
                Console.WriteLine("[CHECK][1302][LV3] reload_enter reason=ReloadAsync");
                Console.WriteLine("[CHECK][1302][UI] thread=" + System.Threading.Thread.CurrentThread.ManagedThreadId +
                                  " invoke=before_query handle=" + (owner != null && owner.IsHandleCreated));

                var rows = await orderSvc.LoadOpenOrdersAsync(
                    getActNo(),
                    getPwd(),
                    getShcode()
                );

                if (rows == null) rows = new List<OrderRow>();
                LogOrder1302Check(rows, "after_t0425_reload");

                if (!owner.IsHandleCreated)
                {
                    Console.WriteLine("[CHECK][1302][UI] thread=" + System.Threading.Thread.CurrentThread.ManagedThreadId +
                                      " invoke=return_before_begininvoke handle=False");
                    return rows;
                }

                Console.WriteLine("[CHECK][1302][UI] thread=" + System.Threading.Thread.CurrentThread.ManagedThreadId +
                                  " invoke=begininvoke_requested handle=True");
                owner.BeginInvoke(new Action(() =>
                {
                    Console.WriteLine("[CHECK][1302][UI] thread=" + System.Threading.Thread.CurrentThread.ManagedThreadId +
                                      " invoke=inside_begininvoke handle=" + owner.IsHandleCreated);
                    lv.BeginUpdate();
                    lv.Items.Clear();
                    Console.WriteLine("[CHECK][1302][LV3] clear items=0 reason=ReloadAsync");

                    var orderMap = new Dictionary<string, (DateTime When, string Side, long Qty, double Price, string Status)>();
                    var orderOrder = new List<string>();

                    foreach (var r in rows)
                    {
                        if (string.IsNullOrWhiteSpace(r.OrderNo)) continue;

                        string key = r.OrderNo.Trim();
                        DateTime when = (r.Ts == default(DateTime)) ? DateTime.Now : r.Ts;
                        string sideText = (r.Side == TradeSide.Buy ? "BUY" : "SELL");

                        if (!orderMap.ContainsKey(key))
                        {
                            orderMap[key] = (
                                When: when,
                                Side: sideText,
                                Qty: r.Qty,
                                Price: r.Price,
                                Status: r.Status ?? ""
                            );
                            orderOrder.Add(key);
                        }
                        else
                        {
                            var prev = orderMap[key];

                            orderMap[key] = (
                                When: when,
                                Side: string.IsNullOrWhiteSpace(sideText) ? prev.Side : sideText,
                                Qty: (r.Qty > 0 ? r.Qty : prev.Qty),
                                Price: (r.Price > 0 ? r.Price : prev.Price),
                                Status: string.IsNullOrWhiteSpace(r.Status) ? prev.Status : r.Status
                            );
                        }
                    }

                    foreach (var ordNo in orderOrder)
                    {
                        var agg = orderMap[ordNo];
                        string displayBand = ResolveDisplayBand(ordNo, agg.Side, agg.Qty);
                        int displayBandNo = ParseDisplayBandNo(displayBand);
                        string displayPnl = BuildDisplayPnl(agg.Side, displayBandNo, ordNo, agg.Price);

                        var item = new ListViewItem(new[]
                        {
                            agg.When.ToString("HH:mm:ss"),
                            agg.Side,
                            (agg.Qty > 0 ? agg.Qty.ToString("N0") : ""),
                            (agg.Price > 0 ? agg.Price.ToString("#,0.##") : ""),
                            ordNo,
                            displayBand,
                            displayPnl,
                            agg.Status
                        });

                        lv.Items.Add(item);

                        if (ordNo == "1302")
                        {
                            long srcRemainQty = 0;
                            long srcOrderQty = agg.Qty;
                            string srcStatus = agg.Status ?? "";

                            try
                            {
                                foreach (var src in rows)
                                {
                                    if (src == null) continue;
                                    if (((src.OrderNo ?? "").Trim()) != "1302") continue;

                                    srcRemainQty = src.RemainQty;
                                    srcOrderQty = src.Qty;
                                    srcStatus = src.Status ?? "";
                                }
                            }
                            catch { }

                            long cheQty = srcOrderQty - srcRemainQty;
                            if (cheQty < 0) cheQty = 0;

                            Console.WriteLine(
                                "[CHECK][LISTVIEW3_ADD] ordNo=1302" +
                                " col0=" + (item.SubItems.Count > 0 ? item.SubItems[0].Text : "") +
                                " col1=" + (item.SubItems.Count > 1 ? item.SubItems[1].Text : "") +
                                " col2=" + (item.SubItems.Count > 2 ? item.SubItems[2].Text : "") +
                                " col3=" + (item.SubItems.Count > 3 ? item.SubItems[3].Text : "") +
                                " col4=" + (item.SubItems.Count > 4 ? item.SubItems[4].Text : "") +
                                " col5=" + (item.SubItems.Count > 5 ? item.SubItems[5].Text : "") +
                                " col6=" + (item.SubItems.Count > 6 ? item.SubItems[6].Text : "") +
                                " col7=" + (item.SubItems.Count > 7 ? item.SubItems[7].Text : "") +
                                " statusSource=" + srcStatus +
                                " remainQty=" + srcRemainQty +
                                " cheQty=" + cheQty +
                                " orderQty=" + srcOrderQty);

                            Console.WriteLine("[CHECK][1302][LV3] add status=" + (item.SubItems.Count > 7 ? item.SubItems[7].Text : "") +
                                              " reason=ReloadAsync");
                        }
                    }

                    for (int rowIndex = 0; rowIndex < lv.Items.Count; rowIndex++)
                    {
                        var row = lv.Items[rowIndex];
                        string ordCol = row.SubItems.Count > 4 ? row.SubItems[4].Text : "";
                        if (ordCol != "1302") continue;

                        Console.WriteLine(
                            "[CHECK][LISTVIEW3_FINAL] ordNo=1302" +
                            " 상태컬럼=" + (row.SubItems.Count > 7 ? row.SubItems[7].Text : "") +
                            " 주문번호컬럼=" + ordCol +
                            " rowIndex=" + rowIndex);

                        Console.WriteLine("[CHECK][1302][LV3] final status=" +
                                          (row.SubItems.Count > 7 ? row.SubItems[7].Text : "") +
                                          " row=" + rowIndex);
                    }

                    lv.EndUpdate();
                }));

                return rows;
            }
            catch (Exception ex)
            {
                if (owner.IsHandleCreated)
                {
                    owner.BeginInvoke(new Action(() =>
                    {
                        MessageBox.Show("증권사 주문 조회 실패: " + ex.Message);
                    }));
                }

                return new List<OrderRow>();
            }
        }

        private static string ResolveDisplayBand(string ordNoText, string side, long qty)
        {
            string normalizedOrdNo = (ordNoText ?? "").Trim();
            long ordNo;

            if (string.IsNullOrWhiteSpace(normalizedOrdNo) || !long.TryParse(normalizedOrdNo, out ordNo))
            {
                Console.WriteLine(
                    "[LISTVIEW3 BAND][MISS] ordNo=" + normalizedOrdNo +
                    " side=" + (side ?? "") +
                    " qty=" + qty +
                    " source=OrdMap display=''");
                return "";
            }

            try
            {
                string mappedSide;
                int band;
                var ordMap = Login.OrdMap;
                if (ordMap != null && ordMap.TryGetDisplayBand(ordNo, out mappedSide, out band) && band > 0)
                {
                    string display = "band" + band;
                    Console.WriteLine(
                        "[LISTVIEW3 BAND] ordNo=" + normalizedOrdNo +
                        " side=" + (string.IsNullOrWhiteSpace(mappedSide) ? (side ?? "") : mappedSide) +
                        " qty=" + qty +
                        " band=" + band +
                        " source=OrdMap.DisplayBand display='" + display + "'");
                    return display;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[LISTVIEW3 BAND][MISS] ordNo=" + normalizedOrdNo +
                    " side=" + (side ?? "") +
                    " qty=" + qty +
                    " source=OrdMap display='' error=" + ex.Message);
                return "";
            }

            Console.WriteLine(
                "[LISTVIEW3 BAND][MISS] ordNo=" + normalizedOrdNo +
                " side=" + (side ?? "") +
                " qty=" + qty +
                " source=OrdMap display=''");
            return "";
        }


        private static int ParseDisplayBandNo(string displayBand)
        {
            string text = (displayBand ?? "").Trim();
            if (text.StartsWith("band", StringComparison.OrdinalIgnoreCase))
                text = text.Substring(4);

            int band;
            return int.TryParse(text, out band) ? band : 0;
        }

        private static string BuildDisplayPnl(string side, int band, string ordNo, double orderPrice)
        {
            if (!string.Equals((side ?? "").Trim(), "SELL", StringComparison.OrdinalIgnoreCase))
                return "";

            double sellPrice;
            if (!TryReadSc1SellPrice(ordNo, out sellPrice) || sellPrice <= 0)
            {
                Console.WriteLine(
                    "[DAY_TRADE_PNL_SKIP] " +
                    "side=SELL " +
                    "band=" + band +
                    " ordNo=" + (ordNo ?? "") +
                    " sellPrice=0" +
                    " reason=SC1_SELL_PRICE_NOT_FOUND");
                return "";
            }

            long realBuyPrice;
            if (!TryReadRealBuyPrice(band, out realBuyPrice) || realBuyPrice <= 0)
            {
                Console.WriteLine(
                    "[DAY_TRADE_PNL_SKIP] " +
                    "side=SELL " +
                    "band=" + band +
                    " ordNo=" + (ordNo ?? "") +
                    " sellPrice=" + sellPrice.ToString("0") +
                    " reason=REAL_BUY_PRICE_NOT_FOUND");
                return "";
            }

            long roundedSellPrice = Convert.ToInt64(Math.Round(sellPrice));
            long unitPnl = roundedSellPrice - realBuyPrice;
            string displayPnl = unitPnl.ToString("N0");

            Console.WriteLine(
                "[DAY_TRADE_PNL] " +
                "side=SELL " +
                "band=" + band +
                " ordNo=" + (ordNo ?? "") +
                " sellPrice=" + roundedSellPrice +
                " realBuyPrice=" + realBuyPrice +
                " unitPnl=" + unitPnl +
                " displayPnl=" + displayPnl);

            return displayPnl;
        }


        private static bool TryReadSc1SellPrice(string ordNoText, out double sellPrice)
        {
            sellPrice = 0;

            long ordNo;
            if (!long.TryParse((ordNoText ?? "").Trim(), out ordNo) || ordNo <= 0)
                return false;

            try
            {
                using (var conn = new SQLiteConnection(Login.ConnStr))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            "SELECT [평균체결가격] " +
                            "FROM [재기동체결복구] " +
                            "WHERE [주문일자] = @ymd AND [주문번호] = @ordNo " +
                            "ORDER BY [id] DESC LIMIT 1";
                        cmd.Parameters.AddWithValue("@ymd", DateTime.Now.ToString("yyyy-MM-dd"));
                        cmd.Parameters.AddWithValue("@ordNo", ordNo);

                        object value = cmd.ExecuteScalar();
                        if (value == null || value == DBNull.Value)
                            return false;

                        sellPrice = Convert.ToDouble(value);
                        return sellPrice > 0;
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadRealBuyPrice(int band, out long realBuyPrice)
        {
            realBuyPrice = 0;
            if (band <= 0) return false;

            try
            {
                using (var conn = new SQLiteConnection(Login.ConnStr))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT 진짜산가격 FROM kodex200_new WHERE band = @b";
                        cmd.Parameters.AddWithValue("@b", band);

                        object value = cmd.ExecuteScalar();
                        if (value == null || value == DBNull.Value)
                            return false;

                        realBuyPrice = Convert.ToInt64(value);
                        return realBuyPrice > 0;
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        private static void LogOrder1302Check(IList<OrderRow> rows, string reason)
        {
            const string targetOrdNoText = "1302";
            const long targetOrdNo = 1302;

            try
            {
                bool found = false;

                if (rows != null)
                {
                    foreach (var r in rows)
                    {
                        if (r == null) continue;

                        string ordNoText = (r.OrderNo ?? "").Trim();
                        if (ordNoText != targetOrdNoText) continue;

                        found = true;
                        string side = (r.Side == TradeSide.Buy ? "BUY" : "SELL");
                        string status = string.IsNullOrWhiteSpace(r.Status) ? "(empty)" : r.Status.Trim();

                        Console.WriteLine(
                            "[CHECK][T0425] ordNo=" + targetOrdNoText +
                            " side=" + side +
                            " qty=" + r.Qty +
                            " status=" + status +
                            " remainQty=" + r.RemainQty +
                            " price=" + r.Price.ToString("0"));
                    }
                }

                if (!found)
                    Console.WriteLine("[CHECK][T0425] ordNo=" + targetOrdNoText + " found=false reason=" + reason);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[CHECK][T0425][ERR] ordNo=" + targetOrdNoText + " reason=" + reason + " msg=" + ex.Message);
            }

            int orderQty = 0;
            int cumFill = 0;
            int remain = -1;
            string ordMapSide = "";
            int ordMapBand = 0;
            bool ordMapExists = false;

            try
            {
                var ordMap = Login.OrdMap;
                if (ordMap != null)
                {
                    ordMapExists = ordMap.TryGetOrderProgress(targetOrdNo, out orderQty, out cumFill, out remain);
                    if (ordMapExists)
                    {
                        try { ordMap.TryGetSideBand(targetOrdNo, out ordMapSide, out ordMapBand); } catch { }
                    }
                }

                bool complete = ordMapExists && remain <= 0;
                Console.WriteLine(
                    "[CHECK][ORDMAP] ordNo=" + targetOrdNoText +
                    " exists=" + ordMapExists +
                    " side=" + (ordMapSide ?? "") +
                    " orderQty=" + orderQty +
                    " cumFill=" + cumFill +
                    " remain=" + (ordMapExists ? remain.ToString() : "?") +
                    " complete=" + complete);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[CHECK][ORDMAP][ERR] ordNo=" + targetOrdNoText + " reason=" + reason + " msg=" + ex.Message);
            }

            try
            {
                var gate = Login.TradeWait;
                bool isLocked = gate != null && gate.IsLocked;
                long currentActiveOrderNo = 0;
                try { currentActiveOrderNo = Login.CurrentActiveOrderNo; } catch { }

                Console.WriteLine(
                    "[CHECK][TRADEWAIT] IsLocked=" + isLocked +
                    " CurrentActiveOrderNo=" + currentActiveOrderNo +
                    " side=" + (gate != null ? (gate.LockedSide ?? "") : "") +
                    " band=" + (gate != null ? gate.LockedBand.ToString() : "0") +
                    " remain=" + (ordMapExists ? remain.ToString() : "?") +
                    " reason=" + reason);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[CHECK][TRADEWAIT][ERR] ordNo=" + targetOrdNoText + " reason=" + reason + " msg=" + ex.Message);
            }
        }

        private void OnSelectedIndexChanged(object sender, EventArgs e)
        {
            if (_lv.SelectedItems.Count == 0) return;

            var it = _lv.SelectedItems[0];
            string side = it.SubItems.Count > 1 ? it.SubItems[1].Text?.Trim() : "";
            string qtyStr = it.SubItems.Count > 2 ? it.SubItems[2].Text?.Trim() : "";
            string ordNo = it.SubItems.Count > 4 ? it.SubItems[4].Text?.Trim() : "";
        }
    }
}
