using System;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace Exercise_1
{
    public partial class Login
    {
        private void TickFromDb_OnPrice(double p)
        {
            //try { Console.WriteLine("[TICK ENTRY] p=" + p); } catch { }
            try { HandleUiTick(p); } catch { }

            // =====================================================
            // Pending 복구 재평가
            // -----------------------------------------------------
            // 조건:
            // 1) 첫 유효 가격이어야 한다.
            // 2) 0003이 WAIT_FOR_PRICE 상태여야 한다.
            // 3) 재평가를 아직 시도하지 않았어야 한다.
            // 4) _pendingRecoveryRunner 가 살아 있어야 한다.
            // =====================================================
            try
            {
                int priceInt = 0;

                try
                {
                    priceInt = Convert.ToInt32(Math.Round(p));
                }
                catch
                {
                    priceInt = 0;
                }

                if (priceInt > 0 &&
                    _pendingRecoveryWaitForPrice &&
                    !_pendingRecoveryRecheckTriggered &&
                    _pendingRecoveryRunner != null)
                {
                    _pendingRecoveryRecheckTriggered = true;

                    Console.WriteLine("[PIPELINE] 첫 가격 수신 -> Pending 복구 재평가 시작 cur=" + priceInt);

                    BeginInvoke(new Action(async () =>
                    {
                        try
                        {
                            var result = await _pendingRecoveryRunner.RunOnceAsync(priceInt).ConfigureAwait(true);

                            Console.WriteLine("[PIPELINE][0003-RECHECK] " + result.Message);


                            if (result.HasPending)
                            {
                                AutoTradingBlocked = true;
                                _autoTradingReady = false;
                                UpdateStatus("[PENDING] 복구 재평가 진행 중...");
                                LogUiStateBeforeOrange();
                                SetPanel2Color(System.Drawing.Color.Orange);

                                _pendingRecoveryWaitForPrice = false;

                                // =====================================================
                                // 🔥 핵심 추가: 추격 시작
                                // =====================================================
                                if (result.ShouldStartChase)
                                {
                                    Console.WriteLine("[PIPELINE] → 0004 추격 시작");

                                    try
                                    {
                                        PendingChaser04.StartTracking(
                                            new _0004_미체결추격관리.TrackRequest
                                            {
                                                OrdNo = Guid.NewGuid().ToString(),

                                                Side = result.ChaseSideKor == "매수"
                                                    ? _0004_미체결추격관리.OrderSide.Buy
                                                    : _0004_미체결추격관리.OrderSide.Sell,

                                                Band = result.ChaseBand,
                                                OrderPrice = priceInt,
                                                OrderQty = result.ChaseQty,
                                                Reason = "PENDING_CHASE",
                                                Shcode = currentShcode
                                            });

                                        Console.WriteLine("[PIPELINE] 0004 StartTracking 호출 완료");
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine("[PIPELINE][0004][EX] " + ex.Message);
                                    }
                                }

                                Console.WriteLine("[PIPELINE] Pending 존재 -> 자동매매 계속 보류");
                                return;
                            }
                            // 재평가 결과 Pending 없음이면 자동매매 허용
                            _pendingRecoveryWaitForPrice = false;
                            _pendingRecoveryRecheckTriggered = false;
                            _pendingRecoveryRunner = null;

                            AutoTradingBlocked = false;
                            _autoTradingReady = true;
                            SetPanel2Color(GetSessionColor());
                            UpdateStatus("자동매매 시작 준비 완료");

                            Console.WriteLine("[PIPELINE] Pending 없음 -> AUTO TRADING READY");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("[PIPELINE][0003-RECHECK][EX] " + ex.Message);
                            AutoTradingBlocked = true;
                            _autoTradingReady = false;
                            UpdateStatus("[PENDING] 재평가 오류: " + ex.Message);
                            SetPanel2Color(System.Drawing.Color.Red);

                            _pendingRecoveryWaitForPrice = false;
                        }
                    }));
                }
            }
            catch (Exception ex)
            {
                try { Console.WriteLine("[PIPELINE][0003-RECHECK][OUTER EX] " + ex.Message); } catch { }
            }

            try { if (_tickProcess != null) _ = _tickProcess.ProcessTickAsync(p); } catch { }
        }

        private void TickFromDb_OnLog(string s)
        {
            try { System.Diagnostics.Debug.WriteLine(s); } catch { }
        }

        private void HandleUiTick(double price)
        {
            if (!double.IsNaN(_lastUiTickPrice) &&
                Math.Abs(_lastUiTickPrice - price) < double.Epsilon)
                return;

            _lastUiTickPrice = price;
            if (!IsHandleCreated) return;

            BeginInvoke(new Action(() =>
            {
                try
                {
                    int priceInt = (int)Math.Round(price);
                    textBox1.Text = priceInt.ToString(CultureInfo.InvariantCulture);

                    int startBandNow = (this.CurrentStartBand > 0) ? this.CurrentStartBand : Login.시작밴드변수;

                    // ✅ [BUG FIX 2026-05-13] BandList가 최신이면 BandList 기반 값 우선 사용
                    try
                    {
                        int blBand = 0;
                        var bl = Login.BandList;
                        if (bl != null)
                        {
                            foreach (var br in bl)
                            {
                                if (br != null && br.Qty > 0 && br.Band > blBand)
                                    blBand = br.Band;
                            }
                        }
                        if (blBand > 0) startBandNow = blBand;
                    }
                    catch { }

                    try
                    {
                        if (string.IsNullOrWhiteSpace(UiPlannedBandsText) || UiPlannedBandsText.Trim() == "(없음)")
                            UiPlannedBandsText = "(밴드" + startBandNow.ToString(CultureInfo.InvariantCulture) + ")";
                    }
                    catch { }

                    if (!_debugBandMsgShown)
                    {
                        _debugBandMsgShown = true;
                         // MessageBox.Show(this, "시작밴드: " + startBandNow + "\r\n현밴드: " + UiPlannedBandsText, "밴드 확인", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }

                    OnTickArrived(priceInt);
                }
                catch { }
            }));
        }

        private void OnTickArrived(int price)
        {
            현재가변수 = price;

            if (!_recent.Contains(price))
                _recent.Add(price);

            if (price % 10 != 0)
                return;

            var prevValues = _recent.Where(pv => pv % 10 == 0 && pv != price).ToArray();

            try { _rtbBands?.RenderDesc(price, prevValues); } catch { }
        }
    }
}
// 2026-04-15 51746
