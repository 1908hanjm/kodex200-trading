using System;
using XA_DATASETLib;

namespace Exercise_1
{
    public partial class Login
    {
        private void TryStartSc1ReceiverOnce()
        {
            try
            {
                EnsureCoreModulesInitialized();

                if (Sc1Receiver == null)
                    Sc1Receiver = new _0650_SC1_수신처리();

                if (_realSC1 == null)
                {
                    _realSC1 = new XARealClass();
                    _realSC1.LoadFromResFile(@"C:\LS_SEC\xingAPI\Res\SC1.res");
                    Console.WriteLine("[LOGIN][SC1] SC1.res loaded");
                }

                Sc1Receiver.Start(_realSC1);
                Console.WriteLine("[LOGIN][SC1] Sc1Receiver.Start(real) called");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[LOGIN][SC1] START FAIL: " + ex);
            }
        }

        private void SubscribeSc1FilledOnce()
        {
            try
            {
                if (Sc1Receiver == null) return;
                if (_isSc1FilledSubscribed) return;

                try { Sc1Receiver.Filled -= OnFilled_FromSc1; } catch { }
                try { Sc1Receiver.Filled += OnFilled_FromSc1; } catch { }

                _isSc1FilledSubscribed = true;
                Console.WriteLine("[LOGIN] Sc1Receiver.Filled subscribed -> OnFilled_FromSc1");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[LOGIN] SubscribeSc1FilledOnce FAIL: " + ex);
            }
        }

        private void OnFilled_FromSc1(string sideKor, int band, int deltaQty, double price, long execNo)
        {
            try
            {
                try { UiPlannedBandsText = "(없음)"; } catch { }
                if (!IsHandleCreated) return;

                Console.WriteLine("[LOGIN][FILLED] side=" + sideKor + " band=" + band + " qty=" + deltaQty + " price=" + price + " execNo=" + execNo);

                // ✅ [2026-05-17] 부분체결(LOCK 중) 시 SetFocusBand 금지
                // TradeWait.IsLocked == true 이면 아직 미체결 잔량이 있는 상태이므로
                // FOCUS band / startBand 재계산을 하면 안 된다.
                // 완전체결(UNLOCK) 후 FinalizeAfterUnlock → RefreshBandsAndTriggers에서 갱신한다.
                try
                {
                    var gate = Login.TradeWait;
                    if (gate != null && gate.IsLocked)
                    {
                        Console.WriteLine("[LOGIN][FILLED] LOCK 중(부분체결) -> SetFocusBand 금지 side=" + sideKor + " band=" + band);
                        return;
                    }
                }
                catch { }

                try
                {
                    // ✅ [BUG FIX 2026-05-13] 시작밴드변수 대신 BandList에서 직접 계산
                    // 체결 직후 시작밴드변수가 아직 갱신되지 않았을 수 있으므로
                    // BandList에서 qty>0인 최대 band를 직접 읽어 SetFocusBand에 사용
                    int latestStartBand = 0;
                    try
                    {
                        var bl = Login.BandList;
                        if (bl != null)
                        {
                            foreach (var br in bl)
                            {
                                if (br != null && br.Qty > 0 && br.Band > latestStartBand)
                                    latestStartBand = br.Band;
                            }
                        }
                    }
                    catch { }

                    if (latestStartBand <= 0)
                        latestStartBand = 시작밴드변수;

                    SetFocusBand(latestStartBand, "SC1_FILLED");
                }
                catch { }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[LOGIN][FILLED] handler EX: " + ex);
            }
        }

        // ✅ [수정] isComplete 파라미터 추가:
        // 부분체결(isComplete=false) 시 t0424 호출 금지
        // 완전체결(isComplete=true) 시에만 t0424 1회 호출
        private void OnKodexQtyUpdated(int band, long qty, bool isComplete)
        {
            if (!IsHandleCreated) return;

            BeginInvoke(new Action(() =>
            {
                try
                {
                    // ✅ [2026-05-17] 부분체결 시 RefreshBandsAndTriggers 금지
                    // isComplete=false(부분체결) 이면 DB qty 화면 갱신만 하고 종료.
                    // RefreshBandsAndTriggers()는 내부에서 BandList 재로드 + SetFocusBand 호출을 수행하므로
                    // LOCK 중 호출 시 FOCUS band 변경 → 0300 재진입 → 중복 주문 위험이 있다.
                    // 완전체결(isComplete=true) + UNLOCK 후에만 RefreshBandsAndTriggers를 허용한다.
                    if (!isComplete)
                    {
                        // 부분체결: UI 밴드 리스트뷰 표시 + DB qty 합계 표시만 허용
                        RefreshBandListView();
                        LoadDbQtySum();
                        Console.WriteLine($"[LOGIN][T0424] 부분체결 -> RefreshBandsAndTriggers/t0424 호출 금지 (band={band} qty={qty})");
                        return;
                    }

                    // 완전체결 경로
                    RefreshBandListView();
                    RefreshBandsAndTriggers();

                    // 핵심 수정:
                    // 거래 체결 후 DB의 qty 합계를 다시 읽어 textBox8에 반영한다.
                    LoadDbQtySum();

                    // ✅ 완전체결(isComplete=true) 시에만 t0424 1회 호출
                    _ = RefreshBrokerJanQtyTextBox5Async("KODEX_QTY_UPDATED", force: false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[LOGIN][QTY UPDATED] UI refresh EX: " + ex.Message);
                }
            }));
        }

        private void OnOrderAccepted_OnLoop(OrderAck ack)
        {
            if (!IsHandleCreated) return;
            if (ack == null) return;

            BeginInvoke(new Action(async () =>
            {
                try
                {
                    var ordNoRaw = (ack.OrderNo ?? "").Trim();
                    long ordNo;
                    if (!long.TryParse(ordNoRaw, out ordNo) || ordNo <= 0) return;

                    var gate = Login.TradeWait;
                    if (gate != null)
                    {
                        gate.MarkAccepted(ordNo: ordNo, orderQty: gate.LockedOrderQty, sideKor: gate.LockedSide, band: gate.LockedBand);
                    }

                    var map = Login.OrdMap;
                    if (map != null)
                    {
                        string side = (gate != null) ? gate.LockedSide : "";
                        int band = (gate != null) ? gate.LockedBand : 0;
                        int qty = (gate != null) ? gate.LockedOrderQty : 0;
                        map.Register(sideKor: side, band: band, ordNo: ordNo, orderQty: qty);
                    }

                    if (_lv3Manager != null)
                        await _lv3Manager.ReloadAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[OnOrderAccepted_OnLoop][EX] " + ex);
                }
            }));
        }

        private void OnOrderRejected_OnLoop(string msg)
        {
            if (!IsHandleCreated) return;
            BeginInvoke(new Action(() => { try { System.Diagnostics.Debug.WriteLine("[REJECT] " + msg); } catch { } }));
        }

        private void OnOrderUpdated_OnLoop(OrderRow row)
        {
            if (!IsHandleCreated || row == null) return;

            BeginInvoke(new Action(async () =>
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine("[UPDATE] " + row.OrderNo + " " + row.Status);
                    await ReloadTodayOrdersAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[OnOrderUpdated_OnLoop] " + ex.Message);
                }
            }));
        }
    }
}
// 2026-05-13 58204
