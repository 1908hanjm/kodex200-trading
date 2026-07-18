using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;
using System.Threading;

namespace Exercise_1
{
    public partial class Login
    {
        private static readonly Color RealSessionColor = Color.FromArgb(226, 244, 232); // REAL: 연한 민트
        private static readonly Color TestSessionColor = Color.FromArgb(227, 242, 253); // TEST: 연한 하늘색
        private static readonly object RestartRecoveryStopUiSync = new object();
        public static bool RestartRecoveryBootScanDone
        {
            get { return _restartRecoveryBootScanDone; }
            private set { _restartRecoveryBootScanDone = value; }
        }
        public static bool RestartRecoveryStopLatched { get; private set; }
        public static bool RestartRecoveryStopShown { get; private set; }

        // =========================================================
        // Pending 복구 재평가용 필드
        // ---------------------------------------------------------
        // _pendingRecoveryRunner:

        private void StartEndOfDayAutoSave()
        {
            try
            {
                if (_eodTimer != null)
                    return;

                _eodDoneForToday = false;

                _eodTimer = new System.Threading.Timer(async _ =>
                {
                    if (System.Threading.Interlocked.Exchange(ref _eodBusy, 1) == 1)
                        return;

                    try
                    {
                        if (this.IsDisposed) return;
                        if (_eodDoneForToday) return;

                        // 장종료 전에는 호출 자체 금지
                        if (!IsKstAfterEndOfDay(15, 20))
                            return;

                        await RunEndOfDayPendingSaveAsync(false).ConfigureAwait(false);
                        await RunEndOfDayBandCapitalApplyAsync().ConfigureAwait(false);

                        // 오늘 장종료 저장 처리는 1회만 수행
                        _eodDoneForToday = true;
                        StopEndOfDayAutoSave();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[EOD][TIMER][EX] " + ex.Message);
                    }
                    finally
                    {
                        System.Threading.Interlocked.Exchange(ref _eodBusy, 0);
                    }

                }, null, 60000, 60000);

                Console.WriteLine("[EOD] AutoSave Timer Started");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[EOD][START][EX] " + ex.Message);
            }
        }

        private void StopEndOfDayAutoSave()
        {
            try
            {
                if (_eodTimer != null)
                {
                    _eodTimer.Dispose();
                    _eodTimer = null;
                }

                Console.WriteLine("[EOD] AutoSave Timer Stopped");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[EOD][STOP][EX] " + ex.Message);
            }
        }

        //   StartTradingPipelineAsync에서 1회 생성 후 보관
        //
        // _pendingRecoveryWaitForPrice:
        //   0003이 WAIT_FOR_PRICE로 끝난 상태인지 표시
        //
        // _pendingRecoveryRecheckTriggered:

        // =========================================================
        // 장종료 자동 1회 Pending 저장
        // =========================================================
        private System.Threading.Timer _eodTimer;
        private int _eodBusy = 0;

        //   첫 가격 수신 후 재평가를 이미 시도했는지 표시
        // =========================================================
        private _0003_Pending복구실행 _pendingRecoveryRunner;
        private bool _pendingRecoveryWaitForPrice = false;
        private bool _pendingRecoveryRecheckTriggered = false;

        private bool _brokerOpenOrdersAtBoot = false;
        private string _brokerOpenOrdersAtBootMessage = "";
        private readonly List<OrderRow> _startupObservationRows = new List<OrderRow>();

        private static bool IsSourceOfTruthMode()
        {
            return true;
        }

        public Color GetSessionColor()
        {
            return _0050_Real_Test환경결정.IsReal ? RealSessionColor : TestSessionColor;
        }

        // =========================================================
        // 0004 미체결추격관리 연동 (Stage 1 + CancelConfirmed)
        // ---------------------------------------------------------
        // 주의:
        // - 0004는 미체결 감시/추격 전용이다.
        // - Pending 주문 전송 시 주문번호(OrdNo)를 확보해 StartTracking 한다.
        // - 0650 Fill 통보는 별도 파일에서 PendingChaser04.NotifyFill(...) 로 연결된다.
        // - 취소확인은 OrderService.CancelConfirmed 이벤트를 통해
        //   PendingChaser04.NotifyCancelConfirmedAsync(...) 로 연결한다.
        // =========================================================
        public static _0004_미체결추격관리 PendingChaser04 { get; private set; }

        private void EnsurePendingChaserInitialized()
        {
            try
            {
                if (PendingChaser04 != null)
                    return;

                PendingChaser04 = new _0004_미체결추격관리
                {
                    ChaseIntervalSeconds = 5,
                    MaxChaseCount = 3,

                    Log = s => Console.WriteLine(s),

                    BlockTrading = why =>
                    {
                        try { AutoTradingBlocked = true; } catch { }
                        try { _autoTradingReady = false; } catch { }
                        try { Console.WriteLine("[0004][BLOCK] " + why); } catch { }
                    },

                    UnblockTrading = why =>
                    {
                        try { AutoTradingBlocked = false; } catch { }
                        try { _autoTradingReady = true; } catch { }
                        try { Console.WriteLine("[0004][UNBLOCK] " + why); } catch { }
                    },

                    GetChasedPrice = (side, currentPrice) =>
                    {
                        // 사용자 확정 tick = 50
                        const int tick = 50;

                        if (currentPrice <= 0)
                            return 0;

                        return side == _0004_미체결추격관리.OrderSide.Buy
                            ? currentPrice + tick
                            : currentPrice - tick;
                    },

                    // =====================================================
                    // 취소 요청
                    // =====================================================
                    // =====================================================
                    // 취소 요청
                    // =====================================================
                    CancelOrderAsync = async (ordNo) =>
                    {
                        try
                        {
                            if (_orderSvc == null)
                            {
                                Console.WriteLine("[0004][CANCEL] _orderSvc NULL");
                                return false;
                            }

                            if (string.IsNullOrWhiteSpace(ordNo))
                            {
                                Console.WriteLine("[0004][CANCEL] ordNo empty");
                                return false;
                            }

                            // =====================================================
                            // Pending에서 새로 추격 시작할 때는 실제 주문번호가 아니라
                            // Guid 문자열을 임시 ordNo로 넣고 있다.
                            // 이런 경우에는 취소 대상 실제 주문이 없으므로 취소를 건너뛴다.
                            // true를 반환해서 0004가 다음 단계(재주문)로 진행하게 한다.
                            // =====================================================
                            Guid dummyGuid;
                            if (Guid.TryParse(ordNo, out dummyGuid))
                            {
                                Console.WriteLine("[0004][CANCEL] GUID ordNo -> cancel skip, ordNo=" + ordNo);
                                await Task.CompletedTask;
                                return true;
                            }

                            if (string.IsNullOrWhiteSpace(Actno))
                            {
                                Console.WriteLine("[0004][CANCEL] Actno empty");
                                return false;
                            }

                            if (string.IsNullOrWhiteSpace(JMpass))
                            {
                                Console.WriteLine("[0004][CANCEL] JMpass empty");
                                return false;
                            }

                            if (string.IsNullOrWhiteSpace(currentShcode))
                            {
                                Console.WriteLine("[0004][CANCEL] currentShcode empty");
                                return false;
                            }

                            Console.WriteLine(
                                "[0004][CANCEL] request ordNo=" + ordNo +
                                ", act=" + Actno +
                                ", shcode=" + currentShcode);

                            bool ok = await _orderSvc.CancelAsync(
                                Actno,
                                JMpass,
                                ordNo,
                                currentShcode
                            ).ConfigureAwait(false);

                            Console.WriteLine("[0004][CANCEL] result=" + ok + " ordNo=" + ordNo);
                            return ok;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("[0004][CANCEL][EX] " + ex.Message);
                            return false;
                        }
                    },
                    //CancelOrderAsync = async (ordNo) =>
                    //{
                    //    try
                    //    {
                    //        if (_orderSvc == null)
                    //        {
                    //            Console.WriteLine("[0004][CANCEL] _orderSvc NULL");
                    //            return false;
                    //        }

                    //        if (string.IsNullOrWhiteSpace(ordNo))
                    //        {
                    //            Console.WriteLine("[0004][CANCEL] ordNo empty");
                    //            return false;
                    //        }

                    //        if (string.IsNullOrWhiteSpace(Actno))
                    //        {
                    //            Console.WriteLine("[0004][CANCEL] Actno empty");
                    //            return false;
                    //        }

                    //        if (string.IsNullOrWhiteSpace(JMpass))
                    //        {
                    //            Console.WriteLine("[0004][CANCEL] JMpass empty");
                    //            return false;
                    //        }

                    //        if (string.IsNullOrWhiteSpace(currentShcode))
                    //        {
                    //            Console.WriteLine("[0004][CANCEL] currentShcode empty");
                    //            return false;
                    //        }

                    //        Console.WriteLine(
                    //            "[0004][CANCEL] request ordNo=" + ordNo +
                    //            ", act=" + Actno +
                    //            ", shcode=" + currentShcode);

                    //        bool ok = await _orderSvc.CancelAsync(
                    //            Actno,
                    //            JMpass,
                    //            ordNo,
                    //            currentShcode
                    //        ).ConfigureAwait(false);

                    //        Console.WriteLine("[0004][CANCEL] result=" + ok + " ordNo=" + ordNo);
                    //        return ok;
                    //    }
                    //    catch (Exception ex)
                    //    {
                    //        Console.WriteLine("[0004][CANCEL][EX] " + ex.Message);
                    //        return false;
                    //    }
                    //},

                    // =====================================================
                    // 1틱 추격 지정가 재주문
                    // =====================================================
                    SendLimitOrderAsync = async (side, band, price, qty, shcode, reason) =>
                    {
                        var r = new _0004_미체결추격관리.SendOrderResult
                        {
                            Success = false,
                            OrdNo = "",
                            Message = ""
                        };

                        try
                        {
                            string sideKor =
                                side == _0004_미체결추격관리.OrderSide.Buy ? "매수" : "매도";

                            Console.WriteLine(
                                $"[0004][REORDER] start side={sideKor}, band={band}, price={price}, qty={qty}, shcode={shcode}, reason={reason}");

                            var ack = await SendPendingOrderWithAckAsync(sideKor, band, qty, price).ConfigureAwait(false);

                            r.Success = ack != null && ack.Success;
                            r.OrdNo = ack != null ? (ack.OrdNo ?? "") : "";
                            r.Message = ack != null ? (ack.Message ?? "") : "";

                            Console.WriteLine(
                                $"[0004][REORDER] result success={r.Success}, ordNo={r.OrdNo}, msg={r.Message}");

                            return r;
                        }
                        catch (Exception ex)
                        {
                            r.Success = false;
                            r.Message = ex.Message;
                            Console.WriteLine("[0004][REORDER][EX] " + ex.Message);
                            return r;
                        }
                    },

                    // =====================================================
                    // 마지막 시장가 주문
                    // 주의:
                    // 현재 SendPendingOrderWithAckAsync는 price를 받는 구조이므로
                    // 시장가 전송 전용 함수가 따로 있으면 그 함수로 바꾸는 것이 가장 좋다.
                    // 일단은 현재가(CurrentPrice)를 넣어 사실상 공격적으로 체결시키는 형태로 둔다.
                    // =====================================================
                    SendMarketOrderAsync = async (side, band, qty, shcode, reason) =>
                    {
                        var r = new _0004_미체결추격관리.SendOrderResult
                        {
                            Success = false,
                            OrdNo = "",
                            Message = ""
                        };

                        try
                        {
                            string sideKor =
                                side == _0004_미체결추격관리.OrderSide.Buy ? "매수" : "매도";

                            int marketLikePrice = CurrentPrice;
                            if (marketLikePrice <= 0)
                            {
                                r.Success = false;
                                r.Message = "CurrentPrice <= 0";
                                Console.WriteLine("[0004][MARKET] invalid CurrentPrice");
                                return r;
                            }

                            Console.WriteLine(
                                $"[0004][MARKET] start side={sideKor}, band={band}, qty={qty}, shcode={shcode}, reason={reason}, price={marketLikePrice}");

                            var ack = await SendPendingOrderWithAckAsync(sideKor, band, qty, marketLikePrice).ConfigureAwait(false);

                            r.Success = ack != null && ack.Success;
                            r.OrdNo = ack != null ? (ack.OrdNo ?? "") : "";
                            r.Message = ack != null ? (ack.Message ?? "") : "";

                            Console.WriteLine(
                                $"[0004][MARKET] result success={r.Success}, ordNo={r.OrdNo}, msg={r.Message}");

                            return r;
                        }
                        catch (Exception ex)
                        {
                            r.Success = false;
                            r.Message = ex.Message;
                            Console.WriteLine("[0004][MARKET][EX] " + ex.Message);
                            return r;
                        }
                    }
                };

                Console.WriteLine("[0004] PendingChaser04 initialized");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[0004][INIT][EX] " + ex.Message);
            }
        }

        // =========================================================
        // 0004 CancelConfirmed 연결
        // =========================================================
        private void WireOrderServiceCancelConfirmed()
        {
            try
            {
                if (_orderSvc == null)
                {
                    Console.WriteLine("[LOGIN][0004] _orderSvc NULL -> skip");
                    return;
                }

                _orderSvc.CancelConfirmed -= OnCancelConfirmed;
                _orderSvc.CancelConfirmed += OnCancelConfirmed;

                Console.WriteLine("[LOGIN][0004] CancelConfirmed wired");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[LOGIN][0004][Wire EX] " + ex.Message);
            }
        }

        private async void OnCancelConfirmed(string orgOrdNo)
        {
            try
            {
                Console.WriteLine("[LOGIN][0004] CancelConfirmed: " + orgOrdNo);

                if (PendingChaser04 != null)
                {
                    await PendingChaser04.NotifyCancelConfirmedAsync(orgOrdNo);
                }

                SetPartialFillStatus(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[LOGIN][0004][EX] " + ex.Message);
            }
        }

        public async Task<_0003_Pending복구실행.SendOrderAck> SendPendingOrderWithAckAsync(
            string sideKor,
            int band,
            int qty,
            int price)
        {
            var ack = new _0003_Pending복구실행.SendOrderAck
            {
                Success = false,
                OrdNo = "",
                Message = ""
            };

            try
            {
                EnsurePendingChaserInitialized();

                if (_exec == null)
                {
                    ack.Message = "_exec NULL";
                    Console.WriteLine("[PENDING][SENDACK] " + ack.Message);
                    return ack;
                }

                var ordMap = Login.OrdMap;
                if (ordMap == null)
                {
                    ack.Message = "Login.OrdMap NULL";
                    Console.WriteLine("[PENDING][SENDACK] " + ack.Message);
                    return ack;
                }

                var tcs = new TaskCompletionSource<long>();
                Action<long> handler = null;

                handler = ordNo =>
                {
                    try
                    {
                        if (ordNo > 0)
                            tcs.TrySetResult(ordNo);
                    }
                    catch { }
                };

                try
                {
                    try { ordMap.OrdRegistered -= handler; } catch { }
                    ordMap.OrdRegistered += handler;

                    await _exec.ExecuteAsync(sideKor, band, qty, price).ConfigureAwait(false);

                    var completed = await Task.WhenAny(tcs.Task, Task.Delay(3000)).ConfigureAwait(false);
                    if (completed != tcs.Task)
                    {
                        ack.Success = false;
                        ack.Message = "주문전송은 했지만 OrdRegistered timeout";
                        Console.WriteLine("[PENDING][SENDACK] " + ack.Message);
                        return ack;
                    }

                    long ordNoLong = tcs.Task.Result;
                    ack.Success = ordNoLong > 0;
                    ack.OrdNo = ordNoLong > 0 ? ordNoLong.ToString() : "";
                    ack.Message = ack.Success ? "주문전송확인" : "주문번호 미확보";

                    if (ack.Success && PendingChaser04 != null)
                    {
                        try
                        {
                            PendingChaser04.StartTracking(new _0004_미체결추격관리.TrackRequest
                            {
                                OrdNo = ack.OrdNo,
                                Side = string.Equals((sideKor ?? "").Trim(), "매수", StringComparison.Ordinal)
                                    ? _0004_미체결추격관리.OrderSide.Buy
                                    : _0004_미체결추격관리.OrderSide.Sell,
                                Band = band,
                                OrderPrice = price,
                                OrderQty = qty,
                                Reason = "PENDING RECOVERY",
                                Shcode = currentShcode
                            });
                        }
                        catch (Exception exTrack)
                        {
                            Console.WriteLine("[PENDING][SENDACK][TRACK][EX] " + exTrack.Message);
                        }
                    }

                    Console.WriteLine("[PENDING][SENDACK] OK ordNo=" + ack.OrdNo +
                                      " side=" + sideKor +
                                      " band=" + band +
                                      " qty=" + qty +
                                      " price=" + price);
                    return ack;
                }
                finally
                {
                    try { ordMap.OrdRegistered -= handler; } catch { }
                }
            }
            catch (Exception ex)
            {
                ack.Success = false;
                ack.Message = ex.Message;
                Console.WriteLine("[PENDING][SENDACK][EX] " + ex.Message);
                return ack;
            }
        }

        private void WireUpSlideDelegates()
        {
            // ================================
            // ✅ 0300 Queue Count → label18 연결
            // ================================
            밴드매칭.OnQueueCountChanged = count =>
            {
                try
                {
                    if (label18.InvokeRequired)
                    {
                        label18.BeginInvoke(new Action(() =>
                        {
                            label18.Text = count.ToString();
                        }));
                    }
                    else
                    {
                        label18.Text = count.ToString();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[UI][label18][COUNT] " + ex.Message);
                }
            };

            밴드매칭.OnQueueCountCleared = () =>
            {
                try
                {
                    if (label18.InvokeRequired)
                    {
                        label18.BeginInvoke(new Action(() =>
                        {
                            label18.Text = "";
                        }));
                    }
                    else
                    {
                        label18.Text = "";
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[UI][label18][CLEAR] " + ex.Message);
                }
            };

            밴드매칭.UpSlideSendOrderAsync = async (sideKor, shcode, price, qty, band) =>
            {
                try
                {
                    var exec = LoginFormAccessor.TryGetExec();
                    if (exec == null)
                    {
                        Console.WriteLine("[LOGIN][UPSLIDE][SendOrder] exec NULL");
                        return false;
                    }

                    if ((sideKor ?? "").Trim() == "매수" &&
                        Login.UpSwapStartBand > 0 &&
                        Login.UpSwapTargetBand > 0 &&
                        band == Login.UpSwapTargetBand &&
                        Login.UpSwapFromQty > 0)
                    {
                        Console.WriteLine(
                            "[0400][CHAIN_META][SEND] " +
                            $"side={sideKor} band={band} chainType=UPSLIDE " +
                            $"sourceBand={Login.UpSwapStartBand} targetBand={Login.UpSwapTargetBand} " +
                            $"fromQty={Login.UpSwapFromQty} extraQty={Login.UpSwapExtraQty}");
                    }

                    await exec.ExecuteAsync(sideKor, band, qty, price);
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[LOGIN][UPSLIDE][SendOrder][EX] " + ex.Message);
                    return false;
                }
            };

            밴드매칭.UpSlideGetOrderableCashAsync = async () =>
            {
                try
                {
                    if (_cashQuery == null)
                    {
                        Console.WriteLine("[LOGIN][UPSLIDE][Cash] _cashQuery NULL");
                        return 0L;
                    }

                    return await _cashQuery.RequestAsync(
                        Actno,
                        JMpass,
                        2000,
                        false,
                        "UPSLIDE_CASH",
                        "Login.UpSlideGetOrderableCashAsync"
                    );
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[LOGIN][UPSLIDE][Cash][EX] " + ex.Message);
                    return 0L;
                }
            };

            밴드매칭.NormalBuyGetOrderableCashAsync = async () =>
            {
                try
                {
                    if (_cashQuery == null)
                    {
                        Console.WriteLine("[LOGIN][BUY][Cash] _cashQuery NULL");
                        return OrderableCashQueryResult.From(
                            OrderableCashResultKind.QueryFailed,
                            0,
                            0,
                            "_cashQuery NULL");
                    }

                    if (_xingConn == null || !_xingConn.IsLoggedIn)
                    {
                        Console.WriteLine("[LOGIN][BUY][Cash] not logged in");
                        return OrderableCashQueryResult.From(
                            OrderableCashResultKind.NotLoggedIn,
                            0,
                            0,
                            "Xing not logged in");
                    }

                    return await _cashQuery.RequestDetailedAsync(
                        Actno,
                        JMpass,
                        2000,
                        false,
                        "NORMAL_BUY_CASH",
                        "Login.NormalBuyGetOrderableCashAsync"
                    ).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[LOGIN][BUY][Cash][EX] " + ex.Message);
                    return OrderableCashQueryResult.From(
                        OrderableCashResultKind.Exception,
                        0,
                        0,
                        ex.Message);
                }
            };

            밴드매칭.ForcedSlideGetOrderableCashAsync = async () =>
            {
                try
                {
                    long cash = await QueryOrderableCashForSlideAsync().ConfigureAwait(false);
                    Console.WriteLine("[LOGIN][SLIDE][CASH] orderableCash=" + cash);
                    return cash;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[LOGIN][SLIDE][CASH][EX] " + ex.Message);
                    return 0L;
                }
            };

            밴드매칭.ForcedSlidingHandler = async (firePrice, decisionBandK, targetBuyBand, startBandNow, orderableCash) =>
            {
                try
                {
                    if (강제슬라이딩실행 == null)
                    {
                        Console.WriteLine("[LOGIN][SLIDE] 강제슬라이딩실행=null -> handler returns Fail");
                        return SlideResult.Fail;
                    }

                    Console.WriteLine(
                        $"[LOGIN][SLIDE] handler ENTER firePrice={firePrice:#,0} K={decisionBandK} buyBand={targetBuyBand} startBandNow={startBandNow} orderableCash={orderableCash:#,0}");

                    SlideResult result = await 강제슬라이딩실행.ExecuteAsync(
                        firePrice: Convert.ToInt32(firePrice),
                        decisionBandK: decisionBandK,
                        targetBuyBand: targetBuyBand,
                        startBandNow: startBandNow,
                        orderableCash: orderableCash
                    ).ConfigureAwait(false);

                    Console.WriteLine("[LOGIN][SLIDE] handler EXIT result=" + result);
                    return result;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[LOGIN][SLIDE] handler EX: " + ex.Message);
                        return SlideResult.Fail;
                }
            };
        }

        // =========================================================
        // 현재 StartBand 기준 팔가격/살가격을 UI(textBox11/textBox10)에 표시
        // textBox11 = 팔가격
        // textBox10 = 살가격
        // =========================================================
        public void UpdateBandPriceTextBoxes(int band)
        {
            try
            {
                if (band <= 0)
                {
                    band = ResolveCurrentStartBandFromDb();
                    if (band <= 0)
                    {
                        bool changedEmpty = SetBandPriceTextBoxes("", "");
                        if (changedEmpty)
                            Console.WriteLine("[UI][BandPrice] no valid start band");
                        return;
                    }
                }

                string sellPrice = "";
                string buyPrice = "";

                using (var conn = new SQLiteConnection(ConnStr))
                {
                    conn.Open();

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            "SELECT 팔가격, 살가격 " +
                            "FROM kodex200_new " +
                            "WHERE band = @band " +
                            "LIMIT 1";
                        cmd.Parameters.AddWithValue("@band", band);

                        using (var rd = cmd.ExecuteReader())
                        {
                            if (rd.Read())
                            {
                                sellPrice = rd["팔가격"] == DBNull.Value
                                    ? ""
                                    : Convert.ToDecimal(rd["팔가격"]).ToString("0", CultureInfo.InvariantCulture);

                                buyPrice = rd["살가격"] == DBNull.Value
                                    ? ""
                                    : Convert.ToDecimal(rd["살가격"]).ToString("0", CultureInfo.InvariantCulture);
                            }
                        }
                    }
                }

                bool changed = SetBandPriceTextBoxes(sellPrice, buyPrice);

                if (changed)
                {
                    Console.WriteLine(
                        "[UI][BandPrice] band=" + band +
                        " 팔가격=" + sellPrice +
                        " 살가격=" + buyPrice);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[UI][BandPrice][EX] " + ex.Message);
            }
        }

        private void UpdateCurrentBandPriceUiFromDb()
        {
            try
            {
                int band = 0;

                try
                {
                    band = 시작밴드변수;
                }
                catch
                {
                    band = 0;
                }

                if (band <= 0)
                    band = ResolveCurrentStartBandFromDb();

                UpdateBandPriceTextBoxes(band);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[UI][BandPrice][Start][EX] " + ex.Message);
            }
        }

        private int ResolveCurrentStartBandFromDb()
        {
            try
            {
                using (var conn = new SQLiteConnection(ConnStr))
                {
                    conn.Open();

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            "SELECT MAX(band) " +
                            "FROM kodex200_new " +
                            "WHERE qty > 0";

                        var v = cmd.ExecuteScalar();
                        if (v != null && v != DBNull.Value)
                            return Convert.ToInt32(v);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[UI][BandPrice][ResolveStartBand][EX] " + ex.Message);
            }

            return 0;
        }

        private bool SetBandPriceTextBoxes(string sellPrice, string buyPrice)
        {
            try
            {
                if (this.IsDisposed) return false;

                if (this.InvokeRequired)
                {
                    this.BeginInvoke(new Action(() => SetBandPriceTextBoxes(sellPrice, buyPrice)));
                    return false;
                }

                bool changed = false;
                if (textBox11 != null && !string.Equals(textBox11.Text ?? "", sellPrice ?? "", StringComparison.Ordinal))
                {
                    textBox11.Text = sellPrice;
                    changed = true;
                }

                if (textBox10 != null && !string.Equals(textBox10.Text ?? "", buyPrice ?? "", StringComparison.Ordinal))
                {
                    textBox10.Text = buyPrice;
                    changed = true;
                }

                return changed;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[UI][BandPrice][Set][EX] " + ex.Message);
                return false;
            }
        }

        private async Task StartTradingPipelineAsync()
        {
            WireUpSlideDelegates();
            EnsureChainFinishedRefreshHooked();
            EnsurePendingChaserInitialized();

            bool coreReady;

            AutoTradingBlocked = true;

            Console.WriteLine("==================================================");
            Console.WriteLine("[PIPELINE] START");
            Console.WriteLine("==================================================");

            if (_0050_Real_Test환경결정.IsReal)
            {
                Console.WriteLine("[PIPELINE] StartRealModeCoreAsync ENTER");
                coreReady = await StartRealModeCoreAsync().ConfigureAwait(true);
                Console.WriteLine("[PIPELINE] StartRealModeCoreAsync EXIT coreReady=" + coreReady);
            }
            else
            {
                Console.WriteLine("[PIPELINE] StartTestModeCoreAsync ENTER");
                coreReady = await StartTestModeCoreAsync().ConfigureAwait(true);
                Console.WriteLine("[PIPELINE] StartTestModeCoreAsync EXIT coreReady=" + coreReady);
            }

            if (!coreReady)
            {
                _autoTradingReady = false;
                Console.WriteLine("[PIPELINE] coreReady=false -> STOP");
                SetPanel2Color(Color.Red);
                return;
            }

            if (RestartRecoveryStopLatched)
            {
                AutoTradingBlocked = true;
                _autoTradingReady = false;
                Console.WriteLine("[PIPELINE][RESTART_RECOVERY] stop requested -> SC only / auto trading disabled");
                SetPanel2Color(Color.Red);
                return;
            }

            if (_brokerOpenOrdersAtBoot)
            {
                string msg = string.IsNullOrWhiteSpace(_brokerOpenOrdersAtBootMessage)
                    ? "t0425 startup open order observation active"
                    : _brokerOpenOrdersAtBootMessage;

                AutoTradingBlocked = true;
                _autoTradingReady = false;
                Console.WriteLine("[PIPELINE][BROKER_OPEN_ORDER][OBSERVE] " + msg);
                UpdateStatus("[미체결 관찰] " + msg);
                LogUiStateBeforeOrange();
                SetPanel2Color(Color.Orange);
                EnsureEndOfDayCloserInitialized();
                StartEndOfDayAutoSave();
                return;
            }

            UpdateCurrentBandPriceUiFromDb();

            Console.WriteLine("[PIPELINE] StartAutoTradingEnginesAsync ENTER");
            await StartAutoTradingEnginesAsync().ConfigureAwait(true);
            Console.WriteLine("[PIPELINE] StartAutoTradingEnginesAsync EXIT");

            UpdateCurrentBandPriceUiFromDb();

            _autoTradingReady = false;

            Console.WriteLine("[PIPELINE] PENDING CHECK ENTER");

            if (IsSourceOfTruthMode())
            {
                _pendingRecoveryWaitForPrice = false;
                _pendingRecoveryRecheckTriggered = false;
                _pendingRecoveryRunner = null;
                Console.WriteLine("[PIPELINE][0003] Source-of-Truth mode: Pending DB 복구/재주문 실행 안 함");
            }
            else
            {
                try
                {
                    int currentPrice = 0;

                    try
                    {
                        currentPrice = Convert.ToInt32(textBox1.Text);
                    }
                    catch
                    {
                        currentPrice = 0;
                    }

                    // -------------------------------------------------
                    // Pending 복구 러너를 필드에 보관
                    // 이후 Tick 첫 가격 수신 시 재평가에 사용
                    // -------------------------------------------------
                    _pendingRecoveryRunner = new _0003_Pending복구실행(
                        ConnStr,
                        async (sideKor, band, qty, price) =>
                        {
                            return await SendPendingOrderWithAckAsync(sideKor, band, qty, price).ConfigureAwait(false);
                        },
                        s => Console.WriteLine(s)
                    );

                    var result = await _pendingRecoveryRunner.RunOnceAsync(currentPrice).ConfigureAwait(true);

                    Console.WriteLine("[PIPELINE][0003] " + result.Message);

                    if (result.HasPending)
                    {
                        AutoTradingBlocked = true;
                        _autoTradingReady = false;

                        UpdateStatus("[PENDING] 복구 진행 중...");
                        LogUiStateBeforeOrange();
                        SetPanel2Color(Color.Orange);

                        // ---------------------------------------------
                        // WAIT_FOR_PRICE 상태를 기억
                        // ---------------------------------------------
                        if (string.Equals(result.Message, "WAIT_FOR_PRICE", StringComparison.Ordinal))
                        {
                            _pendingRecoveryWaitForPrice = true;
                            _pendingRecoveryRecheckTriggered = false;
                            Console.WriteLine("[PIPELINE] Pending 존재 + WAIT_FOR_PRICE -> 첫 가격 재평가 대기");
                        }
                        else
                        {
                            _pendingRecoveryWaitForPrice = false;
                            _pendingRecoveryRecheckTriggered = false;
                            Console.WriteLine("[PIPELINE] Pending 존재 -> 자동매매 보류");
                        }

                        return;
                    }

                    // Pending 없음
                    _pendingRecoveryWaitForPrice = false;
                    _pendingRecoveryRecheckTriggered = false;
                    _pendingRecoveryRunner = null;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[PIPELINE][0003][EX] " + ex.Message);
                    AutoTradingBlocked = true;
                    _autoTradingReady = false;
                    UpdateStatus("[PENDING] 확인 오류: " + ex.Message);
                    SetPanel2Color(Color.Red);

                    _pendingRecoveryWaitForPrice = false;
                    _pendingRecoveryRecheckTriggered = false;

                    return;
                }
            }

            Console.WriteLine("[PIPELINE] PENDING CHECK EXIT");

            UpdateCurrentBandPriceUiFromDb();

            AutoTradingBlocked = false;
            _autoTradingReady = true;
            SetPanel2Color(GetSessionColor());
            EnsureEndOfDayCloserInitialized();
            StartEndOfDayAutoSave();
            SetLogDisplayText(AppLog.FileName, "AUTO_TRADING_READY");

            Console.WriteLine("[PIPELINE] AUTO TRADING READY");
            UpdateStatus("자동매매 시작 준비 완료");
        }

        private Task<bool> ApplyBrokerOpenOrdersFromRowsAsync(IList<OrderRow> rows, string reason)
        {
            _brokerOpenOrdersAtBoot = false;
            _brokerOpenOrdersAtBootMessage = "";

            try
            {
                var observationRows = new List<OrderRow>();

                if (rows != null)
                {
                    foreach (var r in rows)
                    {
                        if (r == null || r.RemainQty <= 0)
                            continue;

                        observationRows.Add(r);
                    }
                }

                lock (_startupObservationRows)
                {
                    _startupObservationRows.Clear();
                    _startupObservationRows.AddRange(observationRows);
                }

                if (observationRows.Count == 0)
                {
                    Console.WriteLine("[BROKER_OPEN_ORDER][OK] reason=" + reason + " remainQty>0 none");
                    return Task.FromResult(false);
                }

                _brokerOpenOrdersAtBoot = true;
                var firstObserved = observationRows[0];
                _brokerOpenOrdersAtBootMessage =
                    "t0425 startup open order observation: " + observationRows.Count + " order(s)" +
                    " firstOrdNo=" + ((firstObserved.OrderNo ?? "").Trim()) +
                    " firstSide=" + ((firstObserved.Side == TradeSide.Buy) ? "BUY" : "SELL") +
                    " firstRemain=" + firstObserved.RemainQty +
                    " firstStatus=" + (string.IsNullOrWhiteSpace(firstObserved.Status) ? "(empty)" : firstObserved.Status.Trim());

                AutoTradingBlocked = true;
                _autoTradingReady = false;

                Console.WriteLine("[BROKER_OPEN_ORDER][OBSERVE] reason=" + reason +
                                  " count=" + observationRows.Count +
                                  " status=StartupObservation cancel=disabled");

                foreach (var r in observationRows)
                {
                    string statusText = string.IsNullOrWhiteSpace(r.Status) ? "(empty)" : r.Status.Trim();
                    Console.WriteLine("[BROKER_OPEN_ORDER][OBSERVE] ordNo=" + ((r.OrderNo ?? "").Trim()) +
                                      " side=" + ((r.Side == TradeSide.Buy) ? "BUY" : "SELL") +
                                      " qty=" + r.Qty +
                                      " remain=" + r.RemainQty +
                                      " price=" + r.Price.ToString("N0") +
                                      " ordtime=" + r.Ts.ToString("yyyy-MM-dd HH:mm:ss") +
                                      " t0425Status=" + statusText +
                                      " status=StartupObservation");

                    long observedOrdNo;
                    if (long.TryParse((r.OrderNo ?? "").Trim(), out observedOrdNo) && observedOrdNo > 0)
                    {
                        RestartRecoveryOrder recoveryRow;
                        string recoveryReason;
                        if (RestartExecutionRecovery.TryGetTodayOrder(observedOrdNo, out recoveryRow, out recoveryReason))
                        {
                            Console.WriteLine("[BROKER_OPEN_ORDER][RECOVERY_ROW_OK] ordNo=" + observedOrdNo +
                                              " side=" + (recoveryRow == null ? "" : recoveryRow.Side) +
                                              " band=" + (recoveryRow == null ? 0 : recoveryRow.Band));
                        }
                        else
                        {
                            Console.WriteLine("[BROKER_OPEN_ORDER][RECOVERY_ROW_MISSING] ordNo=" + observedOrdNo +
                                              " side=" + ((r.Side == TradeSide.Buy) ? "BUY" : "SELL") +
                                              " remain=" + r.RemainQty +
                                              " reason=" + recoveryReason);
                            AutoTradingBlocked = true;
                            _autoTradingReady = false;
                            try { SetPanel2Color(Color.Orange); } catch { }
                        }
                    }
                    else
                    {
                        Console.WriteLine("[BROKER_OPEN_ORDER][RECOVERY_ROW_MISSING] ordNo=" + ((r.OrderNo ?? "").Trim()) +
                                          " side=" + ((r.Side == TradeSide.Buy) ? "BUY" : "SELL") +
                                          " remain=" + r.RemainQty +
                                          " reason=invalid_ordNo");
                        AutoTradingBlocked = true;
                        _autoTradingReady = false;
                        try { SetPanel2Color(Color.Orange); } catch { }
                    }

                    if (string.IsNullOrWhiteSpace(r.Status))
                    {
                        Console.WriteLine("[BROKER_OPEN_ORDER][OBSERVE][ERROR] t0425 live order status empty ordNo=" + ((r.OrderNo ?? "").Trim()) +
                                          " side=" + ((r.Side == TradeSide.Buy) ? "BUY" : "SELL") +
                                          " remain=" + r.RemainQty);
                    }
                }

                try
                {
                    if (textBox14 != null && !textBox14.IsDisposed)
                        textBox14.Text = "접수/미체결 ordNo=" + ((firstObserved.OrderNo ?? "").Trim()) +
                                         " side=" + ((firstObserved.Side == TradeSide.Buy) ? "BUY" : "SELL") +
                                         " remain=" + firstObserved.RemainQty +
                                         " status=" + (string.IsNullOrWhiteSpace(firstObserved.Status) ? "(empty)" : firstObserved.Status.Trim());
                }
                catch (Exception exUi)
                {
                    Console.WriteLine("[BROKER_OPEN_ORDER][OBSERVE][UI_ERROR] " + exUi.Message);
                }

                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _brokerOpenOrdersAtBoot = true;
                _brokerOpenOrdersAtBootMessage = "t0425 startup open order observation failed: " + ex.Message;

                AutoTradingBlocked = true;
                _autoTradingReady = false;

                Console.WriteLine("[BROKER_OPEN_ORDER][OBSERVE][EX][BLOCK] reason=" + reason + " " + ex.Message);
                return Task.FromResult(true);
            }
        }
        private async Task<bool> StartTestModeCoreAsync()
        {
            try
            {
                EnsureCoreModulesInitialized();

                try
                {
                }
                catch { }

                _xingConn = new _0100_Xing_connect(
                    jmid: _0050_Real_Test환경결정.UserId,
                    jmauth: _0050_Real_Test환경결정.Password,
                    updateStatus: UpdateStatus,
                    setPanel2Color: SetPanel2Color
                );

                _mmXing = await _xingConn.ConnectAsync(currentShcode);

                if (_mmXing == null)
                {
                    UpdateStatus("TEST 로그인 실패");
                    SetPanel2Color(Color.Red);
                    return false;
                }

                XingTrade = _mmXing;

                TryStartSc1ReceiverOnce();
                SubscribeSc1FilledOnce();

                if (_cashQuery == null)
                    _cashQuery = new _1000_현금주문가능금액();

                _tx0500 = new _0500_매매전송_Xing(_mmXing, () => Actno, () => JMpass, () => _cashQuery);

                _exec = new 매매실행(_tx0500, () => currentShcode);

                WireOrderServiceCancelConfirmed();

                // ✅ [FIX 2026-07-15] t0425 대조 정정(ReconcileWithT0425)에 필요하므로
                // AfterFillUpdate70과 t0425 조회를 RestoreRestartRecoveryOrdersAtBoot()보다 먼저 준비한다.
                AfterFillUpdate70 = new _0700_매매후update(this);
                FullClearAfter80 = new _0800__완전청산후(this);

                강제슬라이딩실행 =
                    new _2160_강제슬라이딩실행(
                        _exec,
                        () => Login.ConnStr,
                        () => currentShcode,
                        () => _cashQuery,
                        () => Actno,
                        () => JMpass
                    );


                IList<OrderRow> startupOrderRows = null;
                try
                {
                    startupOrderRows = await _060_listView3_당일거래.ReloadAsync(
                        owner: this,
                        lv: listView3,
                        orderSvc: _orderSvc,
                        getActNo: () => Actno,
                        getPwd: () => JMpass,
                        getShcode: () => currentShcode
                    ).ConfigureAwait(true);
                }
                catch { }

                // ✅ [FIX 2026-07-15] t0425 실체결 결과를 넘겨서 로컬 재기동 복구
                // 원장의 stale 상태(로컬 미반영 체결분)를 먼저 정정한 뒤 OrdMap을 복원한다.
                RestoreRestartRecoveryOrdersAtBoot(startupOrderRows);

                await ApplyBrokerOpenOrdersFromRowsAsync(startupOrderRows, _0050_Real_Test환경결정.IsReal ? "REAL_CORE" : "TEST_CORE").ConfigureAwait(true);

                try { await FetchDailyBalanceAsync(TimeSpan.FromSeconds(15)); } catch { }
                RequestOrderableCashTextBox6Refresh("LOGIN_READY_TEST", delayMs: 0);
                SetLogDisplayText(AppLog.FileName, "LOGIN_READY_TEST");

                SetPanel2Color(GetSessionColor());
                UpdateStatus("TEST 코어 준비 완료 " + _0050_Real_Test환경결정.LogPrefix);

                UpdateCurrentBandPriceUiFromDb();

                Console.WriteLine("[CORE][TEST] READY");
                return true;
            }
            catch (Exception ex)
            {
                UpdateStatus("TEST 시작 오류: " + ex.Message);
                SetPanel2Color(Color.Red);
                Console.WriteLine("[CORE][TEST][EX] " + ex);
                return false;
            }
        }

        private async Task<bool> StartRealModeCoreAsync()
        {
            try
            {
                EnsureCoreModulesInitialized();

                try
                {
                }
                catch { }

                _xingConn = new _0100_Xing_connect(
                    jmid: _0050_Real_Test환경결정.UserId,
                    jmauth: _0050_Real_Test환경결정.Password,
                    updateStatus: UpdateStatus,
                    setPanel2Color: SetPanel2Color
                );

                _mmXing = await _xingConn.ConnectAsync(currentShcode);

                if (_mmXing == null)
                {
                    UpdateStatus("REAL 로그인 실패");
                    SetPanel2Color(Color.Red);
                    return false;
                }

                XingTrade = _mmXing;

                TryStartSc1ReceiverOnce();
                SubscribeSc1FilledOnce();

                if (_cashQuery == null)
                    _cashQuery = new _1000_현금주문가능금액();

                _tx0500 = new _0500_매매전송_Xing(_mmXing, () => Actno, () => JMpass, () => _cashQuery);

                _exec = new 매매실행(_tx0500, () => currentShcode);

                WireOrderServiceCancelConfirmed();

                // ✅ [FIX 2026-07-15] t0425 대조 정정(ReconcileWithT0425)에 필요하므로
                // AfterFillUpdate70과 t0425 조회를 RestoreRestartRecoveryOrdersAtBoot()보다 먼저 준비한다.
                AfterFillUpdate70 = new _0700_매매후update(this);
                FullClearAfter80 = new _0800__완전청산후(this);

                강제슬라이딩실행 =
                    new _2160_강제슬라이딩실행(
                        _exec,
                        () => Login.ConnStr,
                        () => currentShcode,
                        () => _cashQuery,
                        () => Actno,
                        () => JMpass
                    );

                IList<OrderRow> startupOrderRows = null;
                try
                {
                    startupOrderRows = await _060_listView3_당일거래.ReloadAsync(
                        owner: this,
                        lv: listView3,
                        orderSvc: _orderSvc,
                        getActNo: () => Actno,
                        getPwd: () => JMpass,
                        getShcode: () => currentShcode
                    ).ConfigureAwait(true);
                }
                catch { }

                // ✅ [FIX 2026-07-15] t0425 실체결 결과를 넘겨서 로컬 재기동 복구
                // 원장의 stale 상태(로컬 미반영 체결분)를 먼저 정정한 뒤 OrdMap을 복원한다.
                RestoreRestartRecoveryOrdersAtBoot(startupOrderRows);

                await ApplyBrokerOpenOrdersFromRowsAsync(startupOrderRows, _0050_Real_Test환경결정.IsReal ? "REAL_CORE" : "TEST_CORE").ConfigureAwait(true);

                try { await FetchDailyBalanceAsync(TimeSpan.FromSeconds(15)); } catch { }
                RequestOrderableCashTextBox6Refresh("LOGIN_READY_REAL", delayMs: 0);
                SetLogDisplayText(AppLog.FileName, "LOGIN_READY_REAL");

                SetPanel2Color(GetSessionColor());
                UpdateStatus("REAL 코어 준비 완료 " + _0050_Real_Test환경결정.LogPrefix);

                UpdateCurrentBandPriceUiFromDb();

                Console.WriteLine("[CORE][REAL] READY");
                return true;
            }
            catch (Exception ex)
            {
                UpdateStatus("초기화 오류: " + ex.Message);
                SetPanel2Color(Color.Red);
                Console.WriteLine("[CORE][REAL][EX] " + ex);
                return false;
            }
        }

        private void RestoreRestartRecoveryOrdersAtBoot(IList<OrderRow> brokerRowsAtBoot = null)
        {
            RestartRecoveryBootScanDone = false;
            List<RestartRecoveryOrder> rows;
            string scanReason;
            if (!RestartExecutionRecovery.TryLoadPendingOrders(out rows, out scanReason))
            {
                Console.WriteLine("[RESTART_RECOVERY][RESTORE_FAIL] reason=boot_scan_failed:" + scanReason);
                AbortRestartRecoveryBootScan("boot_scan_failed", 0, 0, scanReason);
                return;
            }

            if (rows == null || rows.Count == 0)
            {
                RestartRecoveryBootScanDone = true;
                Console.WriteLine("[RESTART_RECOVERY][BOOT_SCAN_DONE] count=0");
                return;
            }

            // ✅ [FIX 2026-07-15] t0425(증권사 실체결) 결과와 대조하여, 로컬이 아직
            // 미완결로 믿고 있지만 증권사에서는 이미 전량체결된 주문(stale)을 먼저 정정한다.
            // (예: ordNo=9513 — 로컬 cumFill=62/474, t0425 cheqty=474)
            // 정정 대상이었던 주문은 결과 목록(rows)에서 제외되어, 아래 OrdMap 복원 루프에
            // 더 이상 "아직 미체결"로 다시 올라가지 않는다.
            if (brokerRowsAtBoot != null && brokerRowsAtBoot.Count > 0 && AfterFillUpdate70 != null)
            {
                var brokerFills = new List<RestartExecutionRecovery.T0425BrokerFill>();
                foreach (var r in brokerRowsAtBoot)
                {
                    if (r == null) continue;

                    long ordNoParsed;
                    if (!long.TryParse((r.OrderNo ?? "").Trim(), out ordNoParsed) || ordNoParsed <= 0)
                        continue;

                    // ⚠️ [확인 필요] OrderRow에 누적체결(cheqty) 전용 필드가 있다면 그것을
                    // 직접 쓰는 것이 더 정확하다. 여기서는 Qty-RemainQty로 근사했다.
                    long approxCheQty = r.Qty - r.RemainQty;

                    brokerFills.Add(new RestartExecutionRecovery.T0425BrokerFill
                    {
                        OrdNo = ordNoParsed,
                        CheQty = approxCheQty,
                        RemainQty = r.RemainQty,
                        Price = (double)r.Price,
                        Status = string.IsNullOrWhiteSpace(r.Status) ? "" : r.Status.Trim()
                    });
                }

                rows = RestartExecutionRecovery.ReconcileWithT0425(
                    rows,
                    brokerFills,
                    applyCatchUpFill: (row, missingQty, brokerPrice) =>
                    {
                        try
                        {
                            bool isBuy = string.Equals(row.Side, "BUY", StringComparison.OrdinalIgnoreCase) ||
                                         row.Side == "매수";
                            string sideKor = isBuy ? "매수" : "매도";

                            AfterFillUpdate70.AfterFillUpdate(
                                band: row.Band,
                                deltaQty: (int)missingQty,
                                price: brokerPrice,
                                side: sideKor,
                                execNo: 0,
                                complete: true);

                            return true;
                        }
                        catch (Exception exCatchUp)
                        {
                            Console.WriteLine("[RESTART_RECOVERY][T0425_RECONCILE][APPLY_EX] ordNo=" +
                                               row.OrdNo + " " + exCatchUp.Message);
                            return false;
                        }
                    });
            }

            var ordMap = Login.OrdMap;
            if (ordMap == null)
            {
                Console.WriteLine("[RESTART_RECOVERY][RESTORE_FAIL] reason=ordmap_null");
                AbortRestartRecoveryBootScan("restore_failed", 0, 0, "Login.OrdMap is null");
                return;
            }

            for (int i = 0; i < rows.Count; i++)
            {
                RestartRecoveryOrder row = rows[i];
                if (row == null || row.OrdNo <= 0 || string.IsNullOrWhiteSpace(row.Side) ||
                    row.Band <= 0 || row.OrderQty <= 0)
                {
                    string invalid = "invalid_boot_row index=" + i;
                    Console.WriteLine("[RESTART_RECOVERY][RESTORE_FAIL] reason=" + invalid);
                    AbortRestartRecoveryBootScan(
                        "restore_failed", row == null ? 0 : row.OrdNo,
                        row == null ? 0 : row.Band, invalid);
                    return;
                }

                string restoreReason;
                bool restored = ordMap.TryRestoreFromRecovery(
                    sideRaw: row.Side,
                    executeBand: row.Band,
                    ordNo: row.OrdNo,
                    orderQty: row.OrderQty,
                    cumFill: row.CumFill,
                    fromBand: row.FromBand,
                    fromQty: row.FromQty,
                    extraQty: row.ExtraQty,
                    tradeType: row.TradeType,
                    source: "BOOT_SCAN",
                    reason: out restoreReason);

                if (!restored)
                {
                    Console.WriteLine("[RESTART_RECOVERY][RESTORE_FAIL] reason=" + restoreReason +
                                      " ordNo=" + row.OrdNo);
                    AbortRestartRecoveryBootScan(
                        restoreReason == "ambiguous_trade_type" ? "ambiguous_trade_type" : "restore_failed",
                        row.OrdNo, row.Band, restoreReason);
                    return;
                }

                Console.WriteLine("[RESTART_RECOVERY][RESTORE_ORDMAP] " +
                                  "ordNo=" + row.OrdNo +
                                  " side=" + row.Side +
                                  " band=" + row.Band +
                                  " qty=" + row.OrderQty +
                                  " tradeType=" + row.TradeType +
                                  " cumFill=" + row.CumFill +
                                  " remain=" + row.Remain);

                if (row.Complete && !row.DbApplied)
                {
                    const string unappliedReason = "filled_but_db_unapplied_requires_t0425_reconcile";
                    Console.WriteLine("[RESTART_RECOVERY][RESTORE_FAIL] reason=" + unappliedReason +
                                      " ordNo=" + row.OrdNo);
                    AbortRestartRecoveryBootScan(
                        unappliedReason, row.OrdNo, row.Band,
                        "complete=true dbApplied=false");
                    return;
                }
            }

            RestartRecoveryBootScanDone = true;
            Console.WriteLine("[RESTART_RECOVERY][BOOT_SCAN_DONE] count=" + rows.Count);
        }

        private static void AbortRestartRecoveryBootScan(string reason, long ordNo, int band, string detail)
        {
            Console.WriteLine("[RESTART_RECOVERY][BOOT_SCAN_ABORT] reason=" + reason + " ordNo=" + ordNo);
            StopAutoTradingForRestartRecovery(reason, ordNo, band, detail);
        }

        public static void StopAutoTradingForRestartRecovery(
            string reason,
            long ordNo,
            int band,
            string detail,
            bool showPopup = true,
            string side = "",
            int qty = 0,
            double price = 0)
        {
            string safeReason = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason.Trim();
            string safeDetail = (detail ?? "").Trim();
            RestartRecoveryStopLatched = true;
            AutoTradingBlocked = true;
            TradingEnabled = false;

            Login login = null;
            try { login = LoginFormAccessor.TryGetLogin(); } catch { }
            bool loginUnavailable = login == null || login.IsDisposed || login.Disposing;
            if (!loginUnavailable) login._autoTradingReady = false;

            bool uiUpdated = false;
            bool popupShown = false;
            string displayReason = DescribeRestartRecoveryStopReason(safeReason);
            string statusText =
                "자동매매 중지\r\n" + displayReason +
                (ordNo > 0 ? "\r\nordNo=" + ordNo : "") +
                (band > 0 ? "\r\nband=" + band : "") +
                "\r\n로그 확인 필요";

            if (!loginUnavailable)
            {
                uiUpdated = true;
                try { login.SetOrderRecoveryStatus(statusText); } catch { }
                try { login.SetPanel2Color(Color.Red); } catch { }
                try
                {
                    string statusOneLine = statusText.Replace("\r\n", " / ");
                    if (login.InvokeRequired)
                        login.BeginInvoke(new Action(() => login.UpdateStatus(statusOneLine)));
                    else
                        login.UpdateStatus(statusOneLine);
                }
                catch { }
            }

            if (showPopup && !loginUnavailable)
            {
                popupShown = TryLatchRestartRecoveryPopup();

                if (popupShown)
                {
                    string popupText =
                        "재기동체결복구 안전장치가 동작했습니다.\r\n\r\n" +
                        "원인:\r\n" + displayReason + " (" + safeReason + ")\r\n\r\n" +
                        "주문번호:\r\n" + ordNo + "\r\n\r\n" +
                        "밴드:\r\n" + band + "\r\n\r\n" +
                        "자동 DB 복구는 수행되지 않았습니다.\r\n\r\n" +
                        "로그를 확인한 후\r\n수동 점검하세요.";
                    Action show = () =>
                    {
                        try
                        {
                            System.Windows.Forms.MessageBox.Show(
                                login, popupText, "자동매매 중지",
                                System.Windows.Forms.MessageBoxButtons.OK,
                                System.Windows.Forms.MessageBoxIcon.Warning);
                        }
                        catch { }
                    };
                    try
                    {
                        if (login.InvokeRequired) login.BeginInvoke(show);
                        else show();
                    }
                    catch { }
                    Console.WriteLine("[RESTART_RECOVERY][MSGBOX_SHOWN] reason=" + safeReason + " ordNo=" + ordNo);
                }
                else
                {
                    Console.WriteLine("[RESTART_RECOVERY][MSGBOX_SUPPRESSED] reason=already_shown originalReason=" + safeReason);
                }
            }
            else if (showPopup)
            {
                Console.WriteLine("[RESTART_RECOVERY][MSGBOX_SKIPPED] reason=form_disposed originalReason=" + safeReason);
                Console.WriteLine("[RESTART_RECOVERY][MSGBOX_SUPPRESSED] reason=ui_unavailable originalReason=" + safeReason);
            }

            Console.WriteLine("[RESTART_RECOVERY][UI_STOP] reason=" + safeReason +
                              " ordNo=" + ordNo + " band=" + band);
            Console.WriteLine("[RESTART_RECOVERY][STOP] reason=" + safeReason +
                              " ordNo=" + ordNo + " band=" + band +
                              " side=" + (side ?? "") + " qty=" + qty + " price=" + price +
                              " detail=" + safeDetail + " autoTradingStopped=true" +
                              " popupShown=" + popupShown + " uiUpdated=" + uiUpdated);
            Console.WriteLine("[RESTART_RECOVERY][STOP_LATCHED] reason=" + safeReason + " ordNo=" + ordNo);
        }

        private static bool TryLatchRestartRecoveryPopup()
        {
            lock (RestartRecoveryStopUiSync)
            {
                if (RestartRecoveryStopShown) return false;
                RestartRecoveryStopShown = true;
                return true;
            }
        }

        private static string DescribeRestartRecoveryStopReason(string reason)
        {
            switch ((reason ?? "").Trim())
            {
                case "sell_max_band_fallback_disabled": return "SELL 복구 불가";
                case "buy_without_recovery_row": return "BUY 복구 불가";
                case "boot_scan_failed": return "기동 복구 검사 실패";
                case "boot_scan_not_done": return "BOOT_SCAN 완료 전 SC 수신";
                case "restore_failed": return "주문 복원 실패";
                case "ambiguous_trade_type": return "거래유형 불명확";
                case "filled_but_db_unapplied_requires_t0425_reconcile": return "체결완료/DB 미반영 수동 대조 필요";
                default: return string.IsNullOrWhiteSpace(reason) ? "원인 불명" : reason;
            }
        }

        private async Task StartAutoTradingEnginesAsync()
        {
            try
            {
                Console.WriteLine("==================================================");
                Console.WriteLine("[BOOT][TICK] START");
                Console.WriteLine($"[BOOT][TICK] currentShcode='{currentShcode}'");
                Console.WriteLine($"[BOOT][TICK] _tickFromXing null? {(_tickFromXing == null)}");
                Console.WriteLine("==================================================");

                try { _tickFromDb?.Stop(); } catch { }
                try { _tickFromXing?.Stop(); } catch { }

                if (_tickFromXing == null)
                {
                    UpdateStatus("실시간 틱 시작 실패: _tickFromXing NULL");
                    SetPanel2Color(Color.Red);
                    Console.WriteLine("[BOOT][TICK] FAIL: _tickFromXing is NULL");
                    return;
                }

                Console.WriteLine("[BOOT][TICK] calling _tickFromXing.Start now...");
                _tickFromXing.Start(currentShcode);
                Console.WriteLine("[BOOT][TICK] _tickFromXing.Start called shcode='" + currentShcode + "'");

                SetPanel2Color(GetSessionColor());
                UpdateStatus("실시간 틱 시작: " + currentShcode + "  " + _0050_Real_Test환경결정.LogPrefix);

                UpdateCurrentBandPriceUiFromDb();
            }
            catch (Exception ex)
            {
                UpdateStatus("실시간 틱 시작 오류: " + ex.Message);
                SetPanel2Color(Color.Red);
                Console.WriteLine("[START][TICK][EX] " + ex);
            }

            await Task.CompletedTask;
        }

        private int ToSafeIntQty(long qty)
        {
            if (qty <= 0) return 0;
            if (qty > int.MaxValue) return int.MaxValue;
            return Convert.ToInt32(qty);
        }

        private int NormalizeOrderPrice(int price)
        {
            if (price <= 0) return 0;

            const int tick = 50;
            return (price / tick) * tick;
        }

        private int ToSafeIntQtyFromLong(long qty)
        {
            if (qty <= 0) return 0;
            if (qty > int.MaxValue) return int.MaxValue;
            return Convert.ToInt32(qty);
        }
        // =========================================================
        // 한국 장종료(KST) 이후 여부 판단
        // =========================================================
        private bool IsKstAfterEndOfDay(int hour, int minute)
        {
            try
            {
                TimeZoneInfo tz;
                try
                {
                    tz = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
                }
                catch
                {
                    tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul");
                }

                DateTime nowKst = TimeZoneInfo.ConvertTime(DateTime.UtcNow, tz);

                int hhmm = nowKst.Hour * 100 + nowKst.Minute;
                int limit = hour * 100 + minute;

                return hhmm >= limit;
            }
            catch
            {
                return false;
            }
        }


    }
}
// 2026-04-15 64281
