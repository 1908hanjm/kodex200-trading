// 0670_listView3_당일거래.cs
// 당일 주문(T0425) 결과를 listView3에 표시하는 전용 모듈
// - 060 -> 0670 리네임 버전
// - Reload 동시실행 방지(SemaphoreSlim) + UI BeginInvoke 안전 처리

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Exercise_1
{
    // C# 규칙: 클래스 이름은 숫자로 시작할 수 없어서 앞에 '_' 추가
    public sealed class _0670_listView3_당일거래
    {
        private readonly Control _owner;      // 보통 Login 폼
        private readonly ListView _lv;
        private readonly OrderService _orderSvc;
        private readonly Func<string> _getActNo;
        private readonly Func<string> _getPwd;
        private readonly Func<string> _getShcode;

        // ✅ Reload 동시 실행 방지
        private static readonly SemaphoreSlim _reloadGate = new SemaphoreSlim(1, 1);

        public _0670_listView3_당일거래(
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

            _lv.SelectedIndexChanged -= OnSelectedIndexChanged;
            _lv.SelectedIndexChanged += OnSelectedIndexChanged;
        }

        // ─────────────────────────────────────
        // listView3 컬럼 구성
        // ─────────────────────────────────────
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
            _lv.Columns.Add("상태", 100);
        }

        // ─────────────────────────────────────
        // 인스턴스용 ReloadAsync
        // ─────────────────────────────────────
        public Task ReloadAsync()
        {
            return ReloadAsync(_owner, _lv, _orderSvc, _getActNo, _getPwd, _getShcode);
        }

        // ─────────────────────────────────────
        // 실제 주문 목록 조회 및 ListView 반영
        // ─────────────────────────────────────
        public static async Task ReloadAsync(
            Control owner,
            ListView lv,
            OrderService orderSvc,
            Func<string> getActNo,
            Func<string> getPwd,
            Func<string> getShcode)
        {
            if (owner == null || lv == null || orderSvc == null) return;

            // ✅ 중복 Reload 방지
            await _reloadGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var rows = await orderSvc.LoadOpenOrdersAsync(
                    getActNo(),
                    getPwd(),
                    getShcode()
                ).ConfigureAwait(false);

                if (!owner.IsHandleCreated) return;

                owner.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (lv.IsDisposed) return;

                        lv.BeginUpdate();
                        lv.Items.Clear();

                        // 병합용 사전 (ordNo 기준)
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

                            var item = new ListViewItem(new[]
                            {
                                agg.When.ToString("HH:mm:ss"),
                                agg.Side,
                                (agg.Qty > 0 ? agg.Qty.ToString("N0") : ""),
                                (agg.Price > 0 ? agg.Price.ToString("#,0.##") : ""),
                                ordNo,
                                agg.Status
                            });

                            lv.Items.Add(item);
                        }
                    }
                    finally
                    {
                        try { lv.EndUpdate(); } catch { }
                    }
                }));
            }
            catch (Exception ex)
            {
                if (owner.IsHandleCreated)
                {
                    owner.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            MessageBox.Show("증권사 주문 조회 실패: " + ex.Message);
                        }
                        catch { }
                    }));
                }
            }
            finally
            {
                try { _reloadGate.Release(); } catch { }
            }
        }

        // ─────────────────────────────────────
        // 선택 행 변경 시 처리 (현재는 읽기만)
        // ─────────────────────────────────────
        private void OnSelectedIndexChanged(object sender, EventArgs e)
        {
            if (_lv.SelectedItems.Count == 0) return;

            var it = _lv.SelectedItems[0];
            string side = it.SubItems.Count > 1 ? it.SubItems[1].Text?.Trim() : "";
            string qtyStr = it.SubItems.Count > 2 ? it.SubItems[2].Text?.Trim() : "";
            string ordNo = it.SubItems.Count > 4 ? it.SubItems[4].Text?.Trim() : "";

            // 필요하면 Login.cs 쪽으로 이벤트 보낼 수 있음
        }
    }
}
// 2026-02-10 48319
