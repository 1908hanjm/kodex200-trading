// 0300_밴드매칭.cs  (복붙용 / C# 7.3)
// ------------------------------------------------------------
// 역할:
// - Tick_Process(0250)가 전달한 FIRE(range)를 "체인(Queue) 주문"으로 변환
//
// ✅ [핵심 정책 변경 2026-05-14 v2]
// 정책: "한 밴드 체결 후 반드시 새 startBand 기준으로 다시 돌파/꺾임을 재판정"
//
// 이전 구조(응급 패치):  FIRE → Queue 순차 실행 → RevalidateSellQueue로 사후 검증
// 신규 구조(정책 반영):  FIRE → 1밴드 전송 → 체결 완료 → Queue Clear + 체인 종료
//                        → 다음 틱에서 0250이 새 startBand 기준으로 처음부터 재판정
//
// 즉, OnTradeCompleted_ThenSendNextOrFinish는 이제 항상 ForceStopChain("OneAndDone")을
// 호출한다. Queue에 항목이 남아 있더라도 이어서 실행하지 않는다.
// RevalidateSellQueue()는 더 이상 필요 없으므로 제거한다.
//
// 이 정책은 BUY/SELL 모두 동일하게 적용된다.
//
// ✅ 기존 유지:
// - 최초 실행배치() 시 Queue는 종전과 같이 range 전체로 채운다.
//   (단, 어차피 1밴드만 실행하고 종료되므로 실질적으로 Queue의 첫 항목만 사용됨)
// - SELL 상승슬라이딩 로직은 그대로 유지
// - 슬라이딩 트리거 / 쿨다운 / AutoTradingBlocked 가드 모두 유지
// - Login_08 t0424 janqty=0 재조회 → 0900에서 처리
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.Rebar;

namespace Exercise_1
{
    public static class 밴드매칭
    {
        private const string SIDE_BUY = "BUY";
        private const string SIDE_SELL = "SELL";

        private static readonly SemaphoreSlim _chainGate = new SemaphoreSlim(1, 1);

        private static readonly object _qLock = new object();

        // ---------------------------------------------------------
        // QueueItem
        // ---------------------------------------------------------
        private sealed class TradeQueueItem
        {
            public string Side;
            public long FirePrice;
            public int QueuedBand;
            public int ExecuteBand;
            public int SellDecisionBand;
        }

        private sealed class GeneralBuyTargets
        {
            public int HoldingCount;
            public int EmptySlots;
            public int TargetBand;
            public List<int> TargetBands = new List<int>();
            public string Verdict = "";
        }

        private sealed class GeneralBuyBudget
        {
            public int Band;
            public int HoldingCount;
            public int EmptySlots;
            public List<int> TargetBands = new List<int>();
            // ✅ [2026-07-10 정책 변경] 마름모 가중치(Weight/WeightSum) 폐기.
            // 실제 예산 분배는 EmptyCount(=현재 10밴드 윈도우 내 qty==0 밴드 수)
            // 기준 균등분배로 전환한다.
            public int EmptyCount;
            public long AllocatedCash;
            public string Calc = "";
            public string Verdict = "";
        }

        private sealed class GeneralBuyOrder
        {
            public int Band;
            public long Qty;
            public long UsedCash;
            public long RemainCash;
        }

        // ✅ [2026-07-10 정책 변경] GeneralBuyDiamondWeights / GeneralBuyDiamondCentralFirstIndexes
        // 제거됨 (Case2 일반 BUY 마름모 배분 폐기 → EmptyCount 기준 균등분배로 전환).


        private static Queue<TradeQueueItem> _bandQueue = new Queue<TradeQueueItem>(16);

        private static string _chainSide = "";
        private static long _chainFirePrice = 0;
        private static int _chainStartBandAtFire = 0;

        private static bool _chainActive = false;
        private static int _inFlightBand = 0;

        private static TaskCompletionSource<bool> _inFlightSendDoneTcs = null;
        private static int _inFlightSendBand = 0;
        private static long _sendSeq = 0;

        public static event Action<string, string, long> ChainFinished;
        private static bool _check1302ChainPending;
        private static string _check1302ChainCaller = "";

        // ✅ 5인자 버전
        public static Func<long, int, int, int, long, Task<SlideResult>> ForcedSlidingHandler;

        public static Func<Task<OrderableCashQueryResult>> NormalBuyGetOrderableCashAsync;
        public static Func<Task<long>> ForcedSlideGetOrderableCashAsync;

        public static Func<string, string, int, int, int, Task<bool>> UpSlideSendOrderAsync;
        public static Func<Task<long>> UpSlideGetOrderableCashAsync;

        // ✅ UpSlide BUY qty=0 STOP 경로 전용 UI refresh hook
        // Login_08에서 연결. 체결 없이 체인 종료 시 t0424/LV3/cash 갱신 트리거.
        public static Action OnUpSlideBuyStopRefreshHook;

        // ✅ Queue Count UI delegate
        public static Action<int> OnQueueCountChanged;
        public static Action OnQueueCountCleared;

        private static readonly object _coolLock = new object();
        private static DateTime _coolUntil = DateTime.MinValue;
        private static string _coolReason = "";
        private static readonly object _downSlideInterruptLock = new object();
        private static PendingSellFire _pendingSellAfterDownSlideCancel = null;

        private sealed class PendingSellFire
        {
            public string Side;
            public long FirePrice;
            public int FromBand;
            public int ToBand;
            public int StartBandNow;
            public long CancelOrdNo;
        }

        public static bool IsChainActive
        {
            get { lock (_qLock) { return _chainActive; } }
        }

        public static bool TryStopChainOnOrderBlock(string reason)
        {
            lock (_qLock)
            {
                if (!_chainActive)
                    return false;
            }

            // ✅ [2026-07-06 FIX] SELL은 이미 실체결됐지만 그 짝인 BUY가
            // CASH_GUARD/BUY_BLOCK 등으로 발주 자체에 실패한 경우,
            // 여기서 무조건 ForceStopChain(=_chainGate.Release())을 하면
            // 다음 가격틱이 곧바로 새 체인(새 SELL)을 시작할 수 있다.
            // SwapInProgress=true인 동안은 게이트를 풀지 말고
            // 자동매매를 완전히 정지시켜 사람의 확인/복구를 기다린다.
            bool swapInProgress = false;
            try { swapInProgress = Login.SwapInProgress; } catch { }

            if (swapInProgress)
            {
                Console.WriteLine("[0300][CHAIN][BLOCKED_DURING_SWAP] reason=OrderBlock:" + (reason ?? "") +
                                   " SwapInProgress=True action=STOP_AUTO_TRADING");
                try { Login.AutoTradingBlocked = true; } catch { }
                // ⚠ _chainActive/_chainGate는 그대로 유지한다 (Release 금지).
                //   AutoTradingBlocked=true가 실행배치() 최상단(278행 부근)에서
                //   이후 모든 재진입 자체를 막아준다.
                return true;
            }

            ForceStopChain("OrderBlock:" + (reason ?? ""));
            return true;
        }

        private static void RefreshQueueCount()
        {
            try
            {
                int count;
                lock (_qLock)
                {
                    count = _bandQueue.Count;
                }

                if (count > 0)
                    OnQueueCountChanged?.Invoke(count);
                else
                    OnQueueCountCleared?.Invoke();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[0300][QUEUECOUNT] " + ex.Message);
            }
        }

        private static string GetShcodeSafe()
        {
            try
            {
                var s = (Login.currentShcode ?? "").Trim();
                if (string.IsNullOrWhiteSpace(s)) return "069500";
                return s;
            }
            catch
            {
                return "069500";
            }
        }

        private static bool ShouldBlockNormalSellByUpSwap(int sellBand, int startBandNow)
        {
            try
            {
                if (!Login.UpSwapInProgress) return false;

                int upStart = 0;
                int upTarget = 0;

                try { upStart = Login.UpSwapStartBand; } catch { upStart = 0; }
                try { upTarget = Login.UpSwapTargetBand; } catch { upTarget = 0; }

                bool sameStartBand = (upStart > 0 && startBandNow == upStart);
                bool sameSellBand = (upStart > 0 && sellBand == upStart);

                if (sameStartBand || sameSellBand)
                {
                    Console.WriteLine(
                        $"[0300][UPSLIDE][BLOCK] normal SELL blocked " +
                        $"sellBand={sellBand} startBandNow={startBandNow} " +
                        $"UpSwapInProgress={Login.UpSwapInProgress} upStart={upStart} upTarget={upTarget}");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[0300][UPSLIDE][BLOCK] EX: " + ex.Message);
                return false;
            }
        }

        private static bool IsInCooldown(out DateTime until, out string reason)
        {
            lock (_coolLock)
            {
                until = _coolUntil;
                reason = _coolReason;
                return DateTime.Now < _coolUntil;
            }
        }

        private static void EnterCooldownSeconds(int seconds, string reason)
        {
            if (seconds <= 0) return;

            var until = DateTime.Now.AddSeconds(seconds);
            lock (_coolLock)
            {
                if (until > _coolUntil)
                {
                    _coolUntil = until;
                    _coolReason = reason ?? "";
                }
            }

            Console.WriteLine($"[0300][COOLDOWN][ENTER] until={until:HH:mm:ss} reason={reason}");
        }

        public static void 실행배치(string side, long firePrice, int fromBand, int toBand, int startBandNow)
        {
            if (string.IsNullOrWhiteSpace(side))
            {
                Debug.WriteLine("[0300] side null/empty");
                return;
            }

            side = side.Trim().ToUpperInvariant();

            try
            {
                var gate0 = Login.TradeWait;
                if (gate0 != null && gate0.IsLocked &&
                    TryStartDownSlideSellInterrupt(side, firePrice, fromBand, toBand, startBandNow, gate0))
                {
                    Console.WriteLine("[0300][DOWNSLIDE-INTERRUPT] accepted before AutoTradingBlocked guard");
                    return;
                }
            }
            catch { }

            // ✅ 가장 먼저 차단
            if (Login.AutoTradingBlocked)
            {
                Console.WriteLine("[0300][BLOCK] AutoTradingBlocked=true -> skip");
                return;
            }

            // ✅ [2026-05-17] LOCK 중(부분체결/주문진행 중) 재진입 차단
            // TradeWait.IsLocked == true 이면 미체결 주문이 존재하는 상태이므로
            // 새 breakout Queue 생성 및 주문 발송을 금지한다.
            // 완전체결 + UNLOCK 후에만 새 신호를 허용한다.
            try
            {
                var gate = Login.TradeWait;
                if (gate != null && gate.IsLocked)
                {
                    if (TryStartDownSlideSellInterrupt(side, firePrice, fromBand, toBand, startBandNow, gate))
                    {
                        Console.WriteLine("[0300][DOWNSLIDE-INTERRUPT] accepted -> cancel old SELL and replay new SELL after cancel confirm");
                        return;
                    }

                    Console.WriteLine("[0300][BLOCK] TradeWait.IsLocked=true -> skip side=" + side +
                                      " range=" + fromBand + "~" + toBand + " firePrice=" + firePrice);
                    return;
                }
            }
            catch { }

            bool isBuy = (side == SIDE_BUY);
            bool isSell = (side == SIDE_SELL);
            if (!isBuy && !isSell)
            {
                Console.WriteLine($"[0300] Unknown side='{side}'");
                return;
            }

            if (IsInCooldown(out DateTime until, out string reason))
            {
                Console.WriteLine($"[0300][COOLDOWN] skip start side={side} range={fromBand}~{toBand} firePrice={firePrice:#,0} until={until:HH:mm:ss} reason={reason}");
                return;
            }

            if (Login.BandList == null || Login.BandList.Count == 0)
            {
                Console.WriteLine("[0300] Login.BandList 비어 있음 -> skip");
                return;
            }

            if (fromBand <= 0 || toBand <= 0)
            {
                Console.WriteLine($"[0300] invalid range from={fromBand} to={toBand}");
                return;
            }

            if (isBuy && fromBand > toBand)
            {
                Console.WriteLine($"[0300] BUY range invalid from={fromBand} to={toBand}");
                return;
            }
            if (isSell && fromBand < toBand)
            {
                Console.WriteLine($"[0300] SELL range invalid from={fromBand} to={toBand}");
                return;
            }

            Task.Run(async () =>
            {
                if (!await TryEnterChainGateAsync().ConfigureAwait(false))
                {
                    Console.WriteLine($"[0300] SKIP (chain running) side={side} range={fromBand}~{toBand} firePrice={firePrice:#,0}");
                    return;
                }

                try
                {
                    // ✅ Task.Run 안에서도 한 번 더 차단
                    if (Login.AutoTradingBlocked)
                    {
                        Console.WriteLine("[0300][BLOCK] AutoTradingBlocked=true -> abort before queue");
                        ForceStopChain("Blocked");
                        return;
                    }

                    if (IsInCooldown(out DateTime until2, out string reason2))
                    {
                        Console.WriteLine($"[0300][COOLDOWN] skip start side={side} range={fromBand}~{toBand} firePrice={firePrice:#,0} until={until2:HH:mm:ss} reason={reason2}");
                        ForceStopChain("Cooldown");
                        return;
                    }

                    var exec0 = LoginFormAccessor.TryGetExec();
                    if (exec0 == null)
                    {
                        Console.WriteLine("[0300] Exec is null -> chain abort");
                        ForceStopChain("ExecNull");
                        return;
                    }

                    var bands = BuildBandsInOrder(isBuy, fromBand, toBand);

                    lock (_qLock)
                    {
                        _bandQueue.Clear();

                        foreach (var b in bands)
                        {
                            // ✅ SELL Queue 생성 시 qty<=0 band 제외
                            if (isSell)
                            {
                                var bandObj = Login.BandList?.FirstOrDefault(x => x != null && x.Band == b);
                                if (bandObj == null || bandObj.Qty <= 0)
                                {
                                    Console.WriteLine($"[0300][QUEUE][SKIP] SELL qty<=0 band={b} Qty={bandObj?.Qty ?? -1}");
                                    continue;
                                }
                            }

                            _bandQueue.Enqueue(new TradeQueueItem
                            {
                                Side = side,
                                FirePrice = firePrice,
                                QueuedBand = b,
                                ExecuteBand = b,
                                SellDecisionBand = isSell ? toBand : 0
                            });
                        }

                        _chainSide = side;
                        _chainFirePrice = firePrice;
                        _chainStartBandAtFire = startBandNow;

                        _chainActive = true;
                        _inFlightBand = 0;

                        _inFlightSendDoneTcs = null;
                        _inFlightSendBand = 0;

                        // ✅ [정책 반영] 첫 항목만 실행한다는 것을 로그에 명시
                        Console.WriteLine(
                            $"[0300] RANGE={fromBand}~{toBand} -> Queue=[{string.Join(",", bands)}] " +
                            $"startBandNow={startBandNow} " +
                            $"POLICY=OneAndDone(1밴드 체결 후 Queue Clear, 0250 재판정)");
                    }

                    RefreshQueueCount();

                    await SendNextOneAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[0300] EX: " + ex);
                    ForceStopChain("EX");
                }
            });
        }

        private static bool TryStartDownSlideSellInterrupt(
            string side,
            long firePrice,
            int fromBand,
            int toBand,
            int startBandNow,
            _0550_부분체결확인 gate)
        {
            if (side != SIDE_SELL) return false;
            if (gate == null || !gate.IsLocked) return false;

            try
            {
                if (!Login.SwapInProgress) return false;
                if (Login.SwapFromBand <= 0 || Login.SwapRecordBandK <= 0) return false;
                if ((gate.LockedSide ?? "").Trim() != "매도") return false;
                if (gate.LockedBand != Login.SwapFromBand) return false;
                if (fromBand < toBand) return false;

                bool normalSellTarget =
                    fromBand == startBandNow ||
                    fromBand == 19 ||
                    (Login.BandList != null && Login.BandList.Any(x => x != null && x.Band == fromBand && x.Qty > 0));

                if (!normalSellTarget)
                {
                    Console.WriteLine("[0300][DOWNSLIDE-INTERRUPT][SKIP] abnormal SELL target fromBand=" + fromBand +
                                      " startBandNow=" + startBandNow);
                    return false;
                }

                long ordNo = gate.LockedOrdNo;
                if (ordNo <= 0)
                {
                    Console.WriteLine("[0300][DOWNSLIDE-INTERRUPT][SKIP] locked ordNo missing");
                    return false;
                }

                lock (_downSlideInterruptLock)
                {
                    if (Login.DownSlideCancelRequested)
                    {
                        Console.WriteLine("[0300][DOWNSLIDE-INTERRUPT][SKIP] cancel already requested ordNo=" +
                                          Login.DownSlideCancelOrdNo);
                        return true;
                    }

                    Login.DownSlideAborted = true;
                    Login.DownSlideAbortedByNewSellSignal = true;
                    Login.DownSlideCancelRequested = true;
                    Login.DownSlideCancelOrdNo = ordNo;

                    _pendingSellAfterDownSlideCancel = new PendingSellFire
                    {
                        Side = side,
                        FirePrice = firePrice,
                        FromBand = fromBand,
                        ToBand = toBand,
                        StartBandNow = startBandNow,
                        CancelOrdNo = ordNo
                    };
                }

                Task.Run(async () => await CancelDownSlideSellRemainAsync(ordNo).ConfigureAwait(false));
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[0300][DOWNSLIDE-INTERRUPT][EX] " + ex.Message);
                return false;
            }
        }

        private static async Task CancelDownSlideSellRemainAsync(long ordNo)
        {
            try
            {
                long remain = await QueryRemainQtyForCancelAsync(ordNo).ConfigureAwait(false);
                if (remain <= 0)
                {
                    Console.WriteLine("[0300][DOWNSLIDE-INTERRUPT] no remaining qty -> complete locally ordNo=" + ordNo);
                    OnDownSlideSellCancelConfirmed(ordNo.ToString());
                    return;
                }

                var svc = Login.GlobalOrderSvc;
                if (svc == null)
                {
                    Console.WriteLine("[0300][DOWNSLIDE-INTERRUPT][CANCEL] GlobalOrderSvc null");
                    return;
                }

                string acnt = Login.Actno ?? "";
                string pwd = Login.JMpass ?? "";
                string shcode = Login.currentShcode ?? "";

                Console.WriteLine("[0300][DOWNSLIDE-INTERRUPT][CANCEL] request ordNo=" + ordNo +
                                  " remain=" + remain + " shcode=" + shcode);

                bool ok = await svc.CancelAsync(acnt, pwd, ordNo.ToString(), shcode).ConfigureAwait(false);
                Console.WriteLine("[0300][DOWNSLIDE-INTERRUPT][CANCEL] result=" + ok + " ordNo=" + ordNo);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[0300][DOWNSLIDE-INTERRUPT][CANCEL][EX] " + ex.Message);
            }
        }

        private static async Task<long> QueryRemainQtyForCancelAsync(long ordNo)
        {
            try
            {
                var svc = Login.GlobalOrderSvc;
                if (svc != null)
                {
                    var rows = await svc.LoadOpenOrdersAsync(Login.Actno ?? "", Login.JMpass ?? "", Login.currentShcode ?? "")
                        .ConfigureAwait(false);

                    var row = rows == null ? null : rows.FirstOrDefault(r =>
                        r != null &&
                        r.RemainQty > 0 &&
                        string.Equals((r.OrderNo ?? "").Trim(), ordNo.ToString(), StringComparison.Ordinal));

                    if (row != null)
                    {
                        Console.WriteLine("[0300][DOWNSLIDE-INTERRUPT][T0425] ordNo=" + ordNo +
                                          " remain=" + row.RemainQty);
                        return row.RemainQty;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[0300][DOWNSLIDE-INTERRUPT][T0425][EX] " + ex.Message);
            }

            try
            {
                int orderQty, cumFill, remain;
                if (Login.OrdMap != null &&
                    Login.OrdMap.TryGetOrderProgress(ordNo, out orderQty, out cumFill, out remain))
                {
                    Console.WriteLine("[0300][DOWNSLIDE-INTERRUPT][ORDMAP] ordNo=" + ordNo +
                                      " remain=" + remain + " cum=" + cumFill + "/" + orderQty);
                    return remain;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[0300][DOWNSLIDE-INTERRUPT][ORDMAP][EX] " + ex.Message);
            }

            return 0;
        }

        public static void OnDownSlideSellCancelConfirmed(string orgOrdNo)
        {
            long ordNo;
            if (!long.TryParse((orgOrdNo ?? "").Trim(), out ordNo) || ordNo <= 0) return;

            PendingSellFire replay = null;

            lock (_downSlideInterruptLock)
            {
                if (!(Login.DownSlideAborted || Login.DownSlideAbortedByNewSellSignal) ||
                    Login.DownSlideCancelOrdNo != ordNo) return;

                try { Login.OrdMap?.MarkCancelConfirmed(ordNo); } catch { }
                try { Login.TradeWait?.ReleaseAfterCancelConfirmed(ordNo, "DownSlide SELL interrupted by new SELL FIRE"); } catch { }

                Login.SwapInProgress = false;
                Login.SwapFromBand = 0;
                Login.SwapFromQty = 0;
                Login.SwapExtraQty = 0;
                Login.SwapRecordBandK = 0;
                Login.AutoTradingBlocked = false;
                Login.DownSlideCancelRequested = false;
                Login.DownSlideCancelOrdNo = 0;

                replay = _pendingSellAfterDownSlideCancel;
                _pendingSellAfterDownSlideCancel = null;
            }

            if (replay != null && replay.CancelOrdNo == ordNo)
            {
                Console.WriteLine("[0300][DOWNSLIDE-INTERRUPT] replay new SELL FIRE range=" +
                                  replay.FromBand + "~" + replay.ToBand + " firePrice=" + replay.FirePrice);

                Task.Run(async () =>
                {
                    await Task.Delay(50).ConfigureAwait(false);
                    실행배치(replay.Side, replay.FirePrice, replay.FromBand, replay.ToBand, replay.StartBandNow);
                });
            }
        }

        private static List<int> BuildBandsInOrder(bool isBuy, int fromBand, int toBand)
        {
            var list = new List<int>(Math.Abs(toBand - fromBand) + 1);

            if (isBuy)
            {
                for (int b = fromBand; b <= toBand; b++) list.Add(b);
            }
            else
            {
                for (int b = fromBand; b >= toBand; b--) list.Add(b);
            }

            return list;
        }

        private static async Task<bool> TryEnterChainGateAsync()
        {
            try
            {
                return await _chainGate.WaitAsync(0).ConfigureAwait(false);
            }
            catch
            {
                return false;
            }
        }

        // ✅ [핵심 정책 변경 2026-05-14 v2]
        // 체결 완료 후 → Queue를 무조건 Clear하고 체인 종료.
        // "다음 Queue 항목 실행"은 하지 않는다.
        // 이유: 1밴드 체결 후 startBand가 바뀌었을 수 있으므로,
        //       반드시 0250이 새 startBand 기준으로 돌파/꺾임을 다시 판정해야 한다.
        //
        // 이전: hasMore → RevalidateSellQueue() → SendNextOneAsync()
        // 현재: 무조건 ForceStopChain("OneAndDone")
        // ✅ [BUG-FIX] 단, 슬라이딩(강제/상승) 진행 중일 때는 ChainFinished 금지.
        //   슬라이딩은 SELL → BUY를 내부에서 순차 처리하므로
        //   SELL 완전체결에 의해 OnTradeCompleted가 호출되더라도
        //   SwapInProgress/UpSwapInProgress가 true이면 체인 종료를 건너뛴다.
        //   슬라이딩 완료(또는 실패) 후 2160/2310이 직접 흐름을 제어한다.
        public static void OnTradeCompleted_ThenSendNextOrFinish(int completedBand)
        {
            Task.Run(async () =>
            {
                try
                {
                    if (_check1302ChainPending)
                        Console.WriteLine("[CHECK][1302][CHAIN] raised=pending caller=0300_OnTradeCompleted completedBand=" + completedBand +
                                          " source=" + _check1302ChainCaller);

                    await WaitInFlightSendDoneIfNeededAsync(completedBand).ConfigureAwait(false);

                    lock (_qLock)
                    {
                        if (!_chainActive)
                        {
                            if (_check1302ChainPending)
                                Console.WriteLine("[CHECK][1302][CHAIN] raised=false caller=0300_OnTradeCompleted reason=chain_not_active completedBand=" + completedBand);
                            return;
                        }
                        _inFlightBand = 0;
                    }

                    // ✅ [BUG-FIX] 슬라이딩 진행 중이면 ChainFinished 발생 금지
                    // 2160(강제슬라이딩) 또는 2310(상승슬라이딩)이 SELL을 보낸 경우
                    // SELL 완전체결이 여기까지 도달하지만, BUY는 아직 진행 중이므로
                    // ForceStopChain을 호출하면 안 된다.
                    bool swapActive = false;
                    try { swapActive = Login.SwapInProgress || Login.UpSwapInProgress; } catch { }

                    if (swapActive)
                    {
                        if (Login.UpSwapInProgress &&
                            Login.UpSwapStartBand > 0 &&
                            Login.UpSwapTargetBand > 0 &&
                            completedBand == Login.UpSwapStartBand)
                        {
                            if (UpSlideSendOrderAsync == null || UpSlideGetOrderableCashAsync == null)
                            {
                                Console.WriteLine("[0300][UPSLIDE][BUY_STAGE] delegates NULL -> BUY stage blocked");
                                return;
                            }

                            _2310_상승슬라이딩실행순서.UpSlideSnapshot snap;
                            if (!_2310_상승슬라이딩실행순서.TryGetSnapshot(out snap) || snap == null)
                            {
                                Console.WriteLine("[0300][UPSLIDE][BUY_STAGE] snapshot missing -> BUY stage blocked");
                                return;
                            }

                            Console.WriteLine(
                                $"[0300][UPSLIDE][BUY_STAGE] SELL complete -> start BUY sourceBand={snap.SellBand} targetBand={snap.BuyBand}");

                            var runner = new _2310_상승슬라이딩실행순서(
                                sendOrderAsync: UpSlideSendOrderAsync,
                                getOrderableCashAsync: UpSlideGetOrderableCashAsync
                            );

                            await runner.ExecuteBuyStageAsync(null, 0, "").ConfigureAwait(false);

                            // ✅ [FIX-A] UpSlide BUY stage 완료(qty=0 STOP 포함) → UI refresh hook 발동
                            // ChainFinished 이벤트 없이 종료되는 경로이므로 여기서 직접 hook 실행.
                            Console.WriteLine("[POST_FILL_REFRESH][START] trigger=UPSLIDE_BUY_STAGE_DONE completedBand=" + completedBand);
                            try { OnUpSlideBuyStopRefreshHook?.Invoke(); } catch (Exception exHook) { Console.WriteLine("[POST_FILL_REFRESH][HOOK][EX] " + exHook.Message); }
                            return;
                        }

                        Console.WriteLine(
                            $"[0300] Swap/Slide 진행중 -> ChainFinished 금지 completedBand={completedBand} " +
                            $"SwapInProgress={Login.SwapInProgress} UpSwapInProgress={Login.UpSwapInProgress}");
                        if (_check1302ChainPending)
                            Console.WriteLine("[CHECK][1302][CHAIN] raised=false caller=0300_OnTradeCompleted reason=swap_active completedBand=" + completedBand);
                        return;
                    }

                    // ✅ [정책] 체결 완료 → 무조건 체인 종료 (남은 Queue 무시)
                    // 0250이 다음 틱에서 새 startBand 기준으로 재판정한다.
                    int remaining;
                    lock (_qLock) { remaining = _bandQueue.Count; }

                    Console.WriteLine(
                        $"[0300][POLICY] OneAndDone: completedBand={completedBand} " +
                        $"remainingQueue={remaining} -> Clear + 체인종료. " +
                        $"0250이 다음 틱에서 새 startBand로 재판정.");

                    ForceStopChain("OneAndDone");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[0300][CHAIN] OnTradeCompleted EX: " + ex);
                    ForceStopChain("EX");
                }
            });
        }

        private static async Task WaitInFlightSendDoneIfNeededAsync(int completedBand)
        {
            Task waitTask = null;
            int band = 0;
            long seq = 0;

            lock (_qLock)
            {
                if (!_chainActive) return;

                if (_inFlightSendDoneTcs != null && _inFlightSendBand == completedBand)
                {
                    waitTask = _inFlightSendDoneTcs.Task;
                    band = _inFlightSendBand;
                    seq = _sendSeq;
                }
            }

            if (waitTask == null) return;

            var done = await Task.WhenAny(waitTask, Task.Delay(5000)).ConfigureAwait(false);
            if (!object.ReferenceEquals(done, waitTask))
            {
                Console.WriteLine($"[0300][WARN] WaitInFlightSendDone TIMEOUT band={band} seq={seq} -> continue");
            }
        }

        private static async Task SendNextOneAsync()
        {
            // ✅ 전송 직전에도 차단
            if (Login.AutoTradingBlocked)
            {
                Console.WriteLine("[0300][BLOCK] AutoTradingBlocked=true -> stop before send");
                ForceStopChain("Blocked");
                return;
            }

            string side;
            long firePrice;
            int queuedBand;
            int executeBand;
            int sellDecisionBand;

            TaskCompletionSource<bool> myTcs;

            lock (_qLock)
            {
                if (!_chainActive) return;
                if (_bandQueue.Count <= 0) return;

                TradeQueueItem item = _bandQueue.Dequeue();
                queuedBand = item.QueuedBand;
                executeBand = item.ExecuteBand;
                sellDecisionBand = item.SellDecisionBand;
                side = !string.IsNullOrEmpty(item.Side) ? item.Side : _chainSide;
                firePrice = item.FirePrice > 0 ? item.FirePrice : _chainFirePrice;

                _inFlightBand = executeBand;

                ++_sendSeq;
                myTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _inFlightSendDoneTcs = myTcs;
                _inFlightSendBand = executeBand;
            }

            RefreshQueueCount();

            var exec = LoginFormAccessor.TryGetExec();
            if (exec == null)
            {
                Console.WriteLine("[0300] Exec is null -> chain abort");
                CompleteInFlightSendDone(executeBand, myTcs, success: false);
                ForceStopChain("ExecNull");
                return;
            }

            Console.WriteLine($"[0300] SEND queuedBand={queuedBand} executeBand={executeBand} side={side}");

            bool ok;
            bool waitingFill = false;
            bool abortedByNewSell = false;
            bool blockedDuringSwap = false;
            try
            {
                int startBandSnapshot = _chainStartBandAtFire;

                if (side == SIDE_BUY)
                {
                    var buyResult = await ExecuteBuy_OneAsync(
                        exec,
                        firePrice,
                        decisionBandK: executeBand,
                        startBandNow: startBandSnapshot
                    ).ConfigureAwait(false);

                    waitingFill = buyResult == SlideResult.WaitingFill ||
                                  buyResult == SlideResult.WaitingPartialProgress;
                    abortedByNewSell = buyResult == SlideResult.AbortedByNewSellSignal;
                    blockedDuringSwap = buyResult == SlideResult.Blocked;
                    ok = buyResult == SlideResult.Success || waitingFill;
                }
                else
                {
                    ok = await ExecuteSell_OneAsync(
                        exec,
                        firePrice,
                        sellBand: executeBand,
                        sellDecisionBand: sellDecisionBand > 0 ? sellDecisionBand : executeBand,
                        startBandNow: startBandSnapshot
                    ).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[0300] SEND EX: " + ex);
                ok = false;
            }

            CompleteInFlightSendDone(executeBand, myTcs, ok);

            if (waitingFill)
            {
                Console.WriteLine($"[0300] WAIT_PARTIAL_PROGRESS queuedBand={queuedBand} executeBand={executeBand}");
                return;
            }

            if (abortedByNewSell)
            {
                Console.WriteLine($"[0300] DOWNSLIDE_ABORTED_BY_NEW_SELL queuedBand={queuedBand} executeBand={executeBand}");
                ForceStopChain("DownSlideAbortedByNewSell");
                return;
            }

            // ✅ [2026-07-06 FIX] SELL은 실체결, BUY만 CASH_GUARD/BUY_BLOCK 등으로 실패한 경우.
            // 일반 SendFail(ForceStopChain)과 달리 여기서는 _chainGate를 풀지 않는다.
            // (AutoTradingBlocked=true는 2160/ExecuteBuy_OneAsync에서 이미 설정됨 ->
            //  실행배치() 최상단에서 다음 재진입 자체를 막아준다)
            if (blockedDuringSwap)
            {
                Console.WriteLine($"[0300] BUY_BLOCKED_DURING_SWAP queuedBand={queuedBand} executeBand={executeBand} " +
                                  "-> keep chain state, AutoTradingBlocked should already be true");
                return;
            }

            if (!ok)
            {
                Console.WriteLine($"[0300] SEND FAIL queuedBand={queuedBand} executeBand={executeBand} -> chain abort");
                ForceStopChain("SendFail");
            }
            // ✅ ok=true여도 여기서 멈춘다.
            // OnTradeCompleted_ThenSendNextOrFinish가 체결 이벤트 수신 후 ForceStopChain("OneAndDone")을 호출한다.
        }

        private static void CompleteInFlightSendDone(int band, TaskCompletionSource<bool> tcs, bool success)
        {
            try { tcs.TrySetResult(success); } catch { }

            lock (_qLock)
            {
                if (_inFlightSendDoneTcs == tcs && _inFlightSendBand == band)
                {
                    _inFlightSendDoneTcs = null;
                    _inFlightSendBand = 0;
                }
            }
        }

        private static void ForceStopChain(string reason)
        {
            string side;
            long firePrice;
            bool check1302;
            string check1302Caller;

            lock (_qLock)
            {
                side = _chainSide;
                firePrice = _chainFirePrice;
                check1302 = _check1302ChainPending;
                check1302Caller = _check1302ChainCaller;

                _bandQueue.Clear();
                _chainSide = "";
                _chainFirePrice = 0;
                _chainStartBandAtFire = 0;

                _chainActive = false;
                _inFlightBand = 0;

                try { _inFlightSendDoneTcs?.TrySetCanceled(); } catch { }
                _inFlightSendDoneTcs = null;
                _inFlightSendBand = 0;
            }

            RefreshQueueCount();

            try { _chainGate.Release(); } catch { }
            Console.WriteLine($"[0300][CHAIN] STOP reason={reason}");

            if (check1302)
                Console.WriteLine("[CHECK][1302][CHAIN] raised=true caller=0300_ForceStopChain reason=" + reason +
                                  " source=" + check1302Caller);

            try { ChainFinished?.Invoke(reason, side, firePrice); } catch { }

            if (check1302)
            {
                _check1302ChainPending = false;
                _check1302ChainCaller = "";
            }
        }

        public static void MarkCheck1302ChainPending(string caller)
        {
            _check1302ChainPending = true;
            _check1302ChainCaller = caller ?? "";
        }

        public static bool IsCheck1302ChainPending
        {
            get { return _check1302ChainPending; }
        }

        private static async Task<long> TryGetForcedSlideOrderableCashAsync()
        {
            try
            {
                if (ForcedSlideGetOrderableCashAsync == null)
                {
                    Console.WriteLine("[0300][SLIDE][CASH] delegate null -> 0");
                    return 0L;
                }

                long v = await ForcedSlideGetOrderableCashAsync().ConfigureAwait(false);
                if (v < 0) v = 0;
                return v;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[0300][SLIDE][CASH] EX: " + ex.Message);
                return 0L;
            }
        }

        private static async Task<OrderableCashQueryResult> TryGetNormalBuyOrderableCashAsync()
        {
            try
            {
                if (NormalBuyGetOrderableCashAsync == null)
                {
                    var missing = OrderableCashQueryResult.From(
                        OrderableCashResultKind.QueryFailed,
                        0,
                        0,
                        "NormalBuyGetOrderableCashAsync delegate null");

                    Console.WriteLine("[BUY][CASH_FAIL] reason=NormalBuy result=" + missing.Kind +
                                      " rc=" + missing.Rc +
                                      " msg=" + missing.Message);
                    return missing;
                }

                // ✅ [P0-FIX] CSPAQ12200 Throttle(10초 최소간격)로 QueryFailed가 나면
                // t0424([T0424][RETRY])와 동일하게 짧게 재시도한 뒤에만 실패로 확정한다.
                // 기존 버그: Throttle=QueryFailed를 즉시 CASH_QUERY_FAILED로 취급해
                // 매수 체인을 재시도 없이 중단시켰음.
                const int MAX_RETRY = 2;
                const int RETRY_DELAY_MS = 1500;

                OrderableCashQueryResult result = null;
                for (int attempt = 0; attempt <= MAX_RETRY; attempt++)
                {
                    if (attempt > 0)
                    {
                        Console.WriteLine("[BUY][CASH][RETRY] reason=NormalBuy " + RETRY_DELAY_MS +
                                          "ms 후 재조회 attempt=" + attempt + "/" + MAX_RETRY);
                        await Task.Delay(RETRY_DELAY_MS).ConfigureAwait(false);
                    }

                    result = await NormalBuyGetOrderableCashAsync().ConfigureAwait(false);
                    if (result == null)
                    {
                        result = OrderableCashQueryResult.From(
                            OrderableCashResultKind.QueryFailed,
                            0,
                            0,
                            "NormalBuyGetOrderableCashAsync returned null");
                    }

                    // Throttle/AlreadyRunning 등 일시적 QueryFailed만 재시도.
                    // RateLimited(rc=-21 쿨다운)/NotLoggedIn 등은 즉시 재시도해도 성공 확률이 없으므로 재시도하지 않는다.
                    if (result.Kind != OrderableCashResultKind.QueryFailed)
                        break;
                }

                if (result.Kind == OrderableCashResultKind.Success ||
                    result.Kind == OrderableCashResultKind.ActualZeroCash)
                {
                    Console.WriteLine("[BUY][CASH] reason=NormalBuy result=" + result.Kind +
                                      " orderableCash=" + result.OrderableCash);
                }
                else
                {
                    Console.WriteLine("[BUY][CASH_FAIL] reason=NormalBuy result=" + result.Kind +
                                      " rc=" + result.Rc +
                                      " msg=" + result.Message);
                }

                return result;
            }
            catch (Exception ex)
            {
                var result = OrderableCashQueryResult.From(
                    OrderableCashResultKind.Exception,
                    0,
                    0,
                    ex.Message);

                Console.WriteLine("[BUY][CASH_FAIL] reason=NormalBuy result=" + result.Kind +
                                  " rc=" + result.Rc +
                                  " msg=" + result.Message);
                return result;
            }
        }

        private static async Task<SlideResult> ExecuteBuy_OneAsync(매매실행 exec, long firePrice, int decisionBandK, int startBandNow)
        {
            if (Login.AutoTradingBlocked)
            {
                Console.WriteLine("[0300][BUY][BLOCK] AutoTradingBlocked=true -> skip");
                return SlideResult.Fail;
            }

            var k = GetBand(decisionBandK);
            if (k == null)
            {
                Console.WriteLine($"[0300][BUY] K not found K={decisionBandK}");
                return SlideResult.Fail;
            }

            int targetBuyBand = decisionBandK + 1;
            var ub = GetBand(targetBuyBand);
            if (ub == null)
            {
                Console.WriteLine($"[0300][BUY] SKIP updateBand(K+1) not found. K={decisionBandK} => {targetBuyBand}");
                return SlideResult.Fail;
            }

            bool targetAlreadyHeld = false;
            int heldCount = 0;
            int minHeld = -1, maxHeld = -1;

            try
            {
                var heldBands = Login.BandList
                    .Where(x => x != null && x.Qty > 0)
                    .Select(x => x.Band)
                    .ToList();

                heldCount = heldBands.Count;
                if (heldCount > 0)
                {
                    minHeld = heldBands.Min();
                    maxHeld = heldBands.Max();
                    targetAlreadyHeld = heldBands.Contains(targetBuyBand);
                }
            }
            catch (Exception exHeld)
            {
                Console.WriteLine("[0300][SLIDE][CHECK] heldBands EX: " + exHeld.Message);
            }

            var buyDecision = _0300_BUY판정.Decide(
                heldCount,
                targetBuyBand,
                targetAlreadyHeld,
                ForcedSlidingHandler != null,
                Login.BandList);

            Console.WriteLine(
                "[0300][BUY_TYPE] " + buyDecision.LogType +
                " heldCount=" + heldCount +
                " hasExistingChain=" + buyDecision.HasExistingChain +
                " K=" + decisionBandK +
                " targetBand=" + targetBuyBand +
                " fromBand=" + buyDecision.ChainFromBand);

            // ✅ 고정 10밴드 유지 규칙
            bool needForcedSlideByFixedHolding =
                buyDecision.Type == BuyDecisionType.TenAfterSliding;

            bool isNormalBuy =
                buyDecision.Type == BuyDecisionType.General &&
                heldCount < 10 &&
                !targetAlreadyHeld;

            bool isTenBeforeSlidingBuy =
                buyDecision.Type == BuyDecisionType.TenBeforeSliding;

            long orderableCash = 0;
            long qty = 0;
            long requiredCash = 0;
            bool needForcedSlideByCash = false;
            bool shouldForceSlide = false;

            if (isNormalBuy || isTenBeforeSlidingBuy)
            {
                var cashResult = await TryGetNormalBuyOrderableCashAsync().ConfigureAwait(false);
                if (!cashResult.CanCalculateBuyQty)
                {
                    Console.WriteLine("[BUY][STOP] reason=CashQueryFailed result=" + cashResult.Kind +
                                      " rc=" + cashResult.Rc +
                                      " msg=" + cashResult.Message +
                                      " targetBand=" + targetBuyBand +
                                      " buyPrice=" + firePrice);
                    return SlideResult.Fail;
                }

                orderableCash = cashResult.OrderableCash;
                if (cashResult.Kind == OrderableCashResultKind.ActualZeroCash || orderableCash <= 0)
                {
                    Console.WriteLine("[BUY][STOP] reason=ActualZeroCash targetBand=" + targetBuyBand +
                                      " buyPrice=" + firePrice +
                                      " orderableCash=" + orderableCash);
                    return SlideResult.Fail;
                }

                var targets = ResolveGeneralBuyTargets(firePrice, targetBuyBand, heldCount, Login.BandList);
                var budget = AllocateGeneralBuyBudget(orderableCash, targets);
                qty = CalculateGeneralBuyQty(budget.AllocatedCash, firePrice);

                // ✅ [배정금 한도][프로세스6] targetBuyBand(=applyBand)의 고정 배정금을
                // 넘지 않도록 clamp. 일반 BUY와 10전슬라이딩 BUY가 이 코드 경로를
                // 공유하므로 여기 한 곳의 clamp로 두 경우 모두 커버된다.
                // 초과분은 매수하지 않고 계좌에 현금으로 남기며, 장종료 배치에서
                // 실현손익과 합쳐 10개 band에 재분배한다.
                var bandCapClamp = 배정금_한도체크.ClampToBandCapital(targetBuyBand, qty, firePrice);
                if (bandCapClamp.leftoverCash > 0)
                {
                    Console.WriteLine("[BUY][BAND_CAP][CLAMP] targetBand=" + targetBuyBand +
                                      " requestedQty=" + qty +
                                      " clampedQty=" + bandCapClamp.clampedQty +
                                      " leftoverCash=" + bandCapClamp.leftoverCash);
                    배정금_한도체크.AddOverLimitLeftoverCash(bandCapClamp.leftoverCash);
                }
                qty = bandCapClamp.clampedQty;

                var order = BuildGeneralBuyOrders(targetBuyBand, qty, firePrice, orderableCash);
                requiredCash = order.UsedCash;

                Console.WriteLine("[BUY][ROUTE] Targets=" + string.Join(",", targets.TargetBands) +
                                  " AllocatedCash=" + budget.AllocatedCash +
                                  " Qty=" + qty);
                Console.WriteLine("[BUY][CALC] InputCash=" + orderableCash + " Price=" + firePrice + " Holding=" + budget.HoldingCount + " ResultQty=" + qty);
                Console.WriteLine("[BUY][CASH_AUDIT] " +
                                  "OrderableCash=" + orderableCash +
                                  " CashSource=CSPAQ12200:NORMAL_BUY_CASH" +
                                  " ResultKind=" + cashResult.Kind +
                                  " Reserved=0" +
                                  " Effective=0" +
                                  " BuyPrice=" + firePrice +
                                  " Qty=" + qty);

                Console.WriteLine(
                    "[BUY][ALLOC_AUDIT] " +
                    "Holding=" + budget.HoldingCount +
                    " Empty=" + budget.EmptySlots +
                    " OrderableCash=" + orderableCash +
                    " TargetBands=" + string.Join(",", budget.TargetBands) +
                    " Calc=" + budget.Calc +
                    " Verdict=" + budget.Verdict);

                Console.WriteLine(
                    "[BUY][ALLOC] " +
                    "Band=" + targetBuyBand +
                    " EmptyCount=" + budget.EmptyCount +
                    " Cash=" + budget.AllocatedCash +
                    " Price=" + firePrice +
                    " Qty=" + qty);

                Console.WriteLine("[BUY][QTY] targetBand=" + targetBuyBand +
                                  " buyPrice=" + firePrice +
                                  " orderableCash=" + orderableCash +
                                  " buyQty=" + qty);

                Console.WriteLine(
                    "[GENERAL_BUY_BUDGET][EVEN_SPLIT] " +
                    "OrderableCash=" + orderableCash +
                    " EmptyCount=" + budget.EmptyCount +
                    " BudgetPerBand=" + budget.AllocatedCash +
                    " TargetBand=" + targetBuyBand +
                    " BuyPrice=" + firePrice +
                    " BuyQty=" + qty);

                if (qty <= 0)
                {
                    Console.WriteLine("[BUY][STOP] reason=BuyQtyZero targetBand=" + targetBuyBand +
                                      " buyPrice=" + firePrice +
                                      " orderableCash=" + orderableCash);
                    return SlideResult.Fail;
                }
            }
            else
            {
                orderableCash = await TryGetForcedSlideOrderableCashAsync().ConfigureAwait(false);
                qty = CalculatePreSlideBuyQty(orderableCash, firePrice, targetBuyBand);
                Console.WriteLine("[BUY][ROUTE] Type=SLIDING Calculator=CalculatePreSlideBuyQty Band=" + targetBuyBand + " Qty=" + qty);
                if (qty <= 0)
                {
                    Console.WriteLine(
                        $"[ROLLING][STOP] buyQty <= 0 orderableCash={orderableCash} targetBand={targetBuyBand} buyPrice={firePrice}");
                    return SlideResult.Fail;
                }

                // ✅ [P1 2026-07-22] GENERAL/2160/2310 경로와 동일하게 SLIDING 경로에도
                // 배정금(BAND_CAP) 한도 클램프를 적용한다. 기존에는 이 경로만 clamp가 빠져 있었다.
                var bandCapClamp = 배정금_한도체크.ClampToBandCapital(targetBuyBand, qty, firePrice);
                if (bandCapClamp.leftoverCash > 0)
                {
                    Console.WriteLine("[BUY][BAND_CAP][CLAMP][SLIDING] band=" + targetBuyBand +
                                      " requestedQty=" + qty +
                                      " clampedQty=" + bandCapClamp.clampedQty +
                                      " leftoverCash=" + bandCapClamp.leftoverCash);
                    배정금_한도체크.AddOverLimitLeftoverCash(bandCapClamp.leftoverCash);
                }
                qty = bandCapClamp.clampedQty;
                if (qty <= 0)
                {
                    Console.WriteLine("[0300][SLIDE][SKIP] reason=BAND_CAP_EXHAUSTED targetBand=" + targetBuyBand);
                    return SlideResult.Fail;
                }

                requiredCash = qty * firePrice;

                // ✅ 기존 현금 부족 슬라이딩
                needForcedSlideByCash =
                    !targetAlreadyHeld &&
                    ForcedSlidingHandler != null &&
                    orderableCash > 0 &&
                    requiredCash > orderableCash;

                shouldForceSlide =
                    needForcedSlideByFixedHolding || needForcedSlideByCash;
            }

            if (isNormalBuy || isTenBeforeSlidingBuy)
            {
                Console.WriteLine(
                    $"[BUY][CHECK] heldCount={heldCount} minHeld={minHeld} maxHeld={maxHeld} " +
                    $"K={decisionBandK} targetBuyBand={targetBuyBand} alreadyHeld={targetAlreadyHeld} " +
                    $"requiredCash={requiredCash} orderableCash={orderableCash}");
            }
            else
            {
                Console.WriteLine(
                    $"[0300][SLIDE][CHECK] heldCount={heldCount} minHeld={minHeld} maxHeld={maxHeld} " +
                    $"K={decisionBandK} targetBuyBand={targetBuyBand} alreadyHeld={targetAlreadyHeld} " +
                    $"needForcedSlideByFixedHolding={needForcedSlideByFixedHolding} " +
                    $"requiredCash={requiredCash} orderableCash={orderableCash} needForcedSlideByCash={needForcedSlideByCash} " +
                    $"shouldForceSlide={shouldForceSlide} handler={(ForcedSlidingHandler != null)}");
            }

            if (isTenBeforeSlidingBuy)
            {
                Console.WriteLine(
                    "[0320][10전슬라이딩BUY][SEND] " +
                    "targetBand=" + targetBuyBand +
                    " fromBand=" + buyDecision.ChainFromBand +
                    " qty=" + qty +
                    " price=" + firePrice);

                await _0320_10전슬라이딩BUY.ExecuteAsync(
                    exec,
                    decisionBandK,
                    targetBuyBand,
                    (int)qty,
                    firePrice).ConfigureAwait(false);

                Console.WriteLine(
                    "[0320][10전슬라이딩BUY][DONE] " +
                    "targetBand=" + targetBuyBand +
                    " fromBand=" + buyDecision.ChainFromBand);
                return SlideResult.Success;
            }

            if (shouldForceSlide)
            {
                string triggerReason = needForcedSlideByFixedHolding
                    ? "FIXED_10_HOLDINGS"
                    : "CASH_SHORTAGE";

                Console.WriteLine(
                    $"[0300][SLIDE][TRIGGER] reason={triggerReason} " +
                    $"firePrice={firePrice:#,0} K={decisionBandK} buyBand={targetBuyBand} " +
                    $"heldCount={heldCount} orderableCash={orderableCash} requiredCash={requiredCash}");

                SlideResult handled = SlideResult.Fail;
                try
                {
                    handled = await _0330_10후슬라이딩BUY.ExecuteAsync(() =>
                        ForcedSlidingHandler(
                            firePrice,
                            decisionBandK,
                            targetBuyBand,
                            startBandNow,
                            orderableCash
                        )).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[0300][SLIDE][HANDLER] EX: " + ex.Message);
                    handled = SlideResult.Fail;
                }

                if (handled == SlideResult.Success)
                {
                    Console.WriteLine("[0300][SLIDE] handled=true -> SKIP normal BUY");
                    return SlideResult.Success;
                }

                if (handled == SlideResult.WaitingFill ||
                    handled == SlideResult.WaitingPartialProgress)
                {
                    Console.WriteLine("[0300][SLIDE] WAIT_PARTIAL_PROGRESS -> SKIP SendFail/cooldown");
                    return handled;
                }

                if (handled == SlideResult.AbortedByNewSellSignal)
                {
                    Console.WriteLine("[0300][SLIDE] ABORTED_BY_NEW_SELL -> SKIP normal BUY/cooldown");
                    return SlideResult.AbortedByNewSellSignal;
                }

                // ✅ [2026-07-06 FIX] SELL은 실체결됐지만 BUY가 CASH_GUARD/BUY_BLOCK 등으로
                // 발주 자체에 실패한 경우. 3초 쿨다운 후 재시도하면 SwapInProgress가 그대로인 채
                // 새 SELL이 또 나갈 수 있으므로, 쿨다운/재시도 없이 자동매매를 정지 상태로 둔다.
                if (handled == SlideResult.Blocked)
                {
                    Console.WriteLine("[0300][SLIDE] handled=Blocked -> STOP_AUTO_TRADING, no cooldown retry");
                    return SlideResult.Blocked;
                }

                Console.WriteLine("[0300][SLIDE] handled=false -> enter cooldown and stop BUY");
                EnterCooldownSeconds(3, "SLIDE_FAIL");
                return SlideResult.Fail;
            }

            // ✅ [P0 2026-07-22] 이미 보유중인 밴드는 강제슬라이딩이 필요치 않으면 추가매수하지 않는다.
            // targetAlreadyHeld=True인데 Decide()가 General을 반환하면 isNormalBuy 조건(!targetAlreadyHeld)에
            // 걸려 이 else(SLIDING) 분기로 떨어지는데, needForcedSlideByFixedHolding/ByCash가 모두 False라
            // shouldForceSlide=False로 나와도 이 게이트가 없으면 그대로 아래 일반 발주로 흘러 이중매수가 발생했다.
            if (targetAlreadyHeld && !shouldForceSlide)
            {
                Console.WriteLine(
                    "[0300][SLIDE][SKIP] reason=ALREADY_HELD_NO_FORCE_NEEDED " +
                    "targetBand=" + targetBuyBand +
                    " shouldForceSlide=" + shouldForceSlide);
                return SlideResult.Fail;
            }

            Console.WriteLine(
                $"[0300.BUY.REQUEST] firePrice={firePrice:#,0} decisionBand(K)={decisionBandK} qty(cashBased)={qty:#,0} " +
                $"updateQtyBand(K+1)={targetBuyBand} startBandNow={startBandNow}"
            );

            try
            {
                long remainCashAfterOrder = orderableCash - requiredCash;
                if (remainCashAfterOrder < 0) remainCashAfterOrder = 0;
                Console.WriteLine(
                    "[BUY][ORDER] " +
                    "Band=" + targetBuyBand +
                    " BuyQty=" + qty +
                    " UsedCash=" + requiredCash +
                    " RemainCash=" + remainCashAfterOrder);

                Console.WriteLine($"[0300.BUY.EXEC] SEND -> band(K)={decisionBandK} qty={qty:#,0} price={firePrice:#,0}");
                await _0310_일반BUY.ExecuteAsync(exec, decisionBandK, (int)qty, firePrice).ConfigureAwait(false);
                Console.WriteLine($"[0300.BUY.EXEC] DONE band(K)={decisionBandK}");
                return SlideResult.Success;
            }
            catch (Exception ex)
            {
                string emsg = ex.Message ?? "";
                Console.WriteLine($"[0300.BUY.EXEC] FAIL band(K)={decisionBandK} msg={emsg}");

                bool isNoCash =
                    emsg.Contains("01425") ||
                    emsg.Contains("주문가능금액") ||
                    emsg.Contains("주문가능 금액") ||
                    emsg.Contains("주문가능금액 부족");

                if (isNoCash)
                {
                    if (!targetAlreadyHeld && ForcedSlidingHandler != null)
                    {
                        Console.WriteLine("[0300][SLIDE][RETRY] got 01425 -> try sliding delegate instead of cooldown");

                        SlideResult handled2 = SlideResult.Fail;
                        try
                        {
                            handled2 = await _0330_10후슬라이딩BUY.ExecuteAsync(() =>
                                ForcedSlidingHandler(
                                    firePrice,
                                    decisionBandK,
                                    targetBuyBand,
                                    startBandNow,
                                    orderableCash
                                )).ConfigureAwait(false);
                        }
                        catch (Exception ex2)
                        {
                            Console.WriteLine("[0300][SLIDE][RETRY] EX: " + ex2.Message);
                            handled2 = SlideResult.Fail;
                        }

                        if (handled2 == SlideResult.Success)
                        {
                            Console.WriteLine("[0300][SLIDE][RETRY] handled=true -> stop normal BUY failure path");
                            return SlideResult.Success;
                        }

                        if (handled2 == SlideResult.WaitingFill ||
                            handled2 == SlideResult.WaitingPartialProgress)
                        {
                            Console.WriteLine("[0300][SLIDE][RETRY] WAIT_PARTIAL_PROGRESS -> stop normal BUY failure path");
                            return handled2;
                        }

                        if (handled2 == SlideResult.AbortedByNewSellSignal)
                        {
                            Console.WriteLine("[0300][SLIDE][RETRY] ABORTED_BY_NEW_SELL -> stop normal BUY failure path");
                            return SlideResult.AbortedByNewSellSignal;
                        }

                        // ✅ [2026-07-06 FIX] 위와 동일한 이유로 Blocked는 쿨다운 재시도 없이 정지 유지.
                        if (handled2 == SlideResult.Blocked)
                        {
                            Console.WriteLine("[0300][SLIDE][RETRY] handled=Blocked -> STOP_AUTO_TRADING, no cooldown retry");
                            return SlideResult.Blocked;
                        }
                    }

                    EnterCooldownSeconds(30, "01425: 주문가능금액 부족");
                }

                return SlideResult.Fail;
            }
        }

        private static async Task<bool> ExecuteSell_OneAsync(
            매매실행 exec,
            long firePrice,
            int sellBand,
            int sellDecisionBand,
            int startBandNow)
        {
            if (Login.AutoTradingBlocked)
            {
                Console.WriteLine("[0300][SELL][BLOCK] AutoTradingBlocked=true -> skip");
                return false;
            }

            if (ShouldBlockNormalSellByUpSwap(sellBand, startBandNow))
            {
                return true;
            }

            var b = GetBand(sellBand);
            if (b == null)
            {
                Console.WriteLine($"[0300][SELL] band not found sellBand={sellBand} sellDecisionBand={sellDecisionBand} startBandNow={startBandNow}");
                return false;
            }

            long qty = b.Qty;
            if (qty <= 0)
            {
                Console.WriteLine($"[0300][SELL] SKIP Qty<=0 sellBand={sellBand} Qty={qty} sellDecisionBand={sellDecisionBand} startBandNow={startBandNow}");
                return true; // SKIP은 체인 유지(true)
            }

            if (sellBand == startBandNow)
            {
                try
                {
                    var plan = _2300_상승슬라이딩Plan.TryMakePlan(startBandNow);
                    if (plan != null && plan.IsUpSlide)
                    {
                        if (UpSlideSendOrderAsync == null || UpSlideGetOrderableCashAsync == null)
                        {
                            Console.WriteLine($"[0300][UPSLIDE] plan OK but delegates NULL -> fallback normal SELL (K={startBandNow})");
                        }
                        else
                        {
                            Console.WriteLine($"[0300][UPSLIDE] ENTER K={startBandNow} from={plan.BuyBand} fromQty={plan.FromQty} firePrice={firePrice:#,0}");

                            var runner = new _2310_상승슬라이딩실행순서(
                                sendOrderAsync: UpSlideSendOrderAsync,
                                getOrderableCashAsync: UpSlideGetOrderableCashAsync
                            );

                            string shcode = GetShcodeSafe();

                            await runner.ExecuteAsync(plan, (int)firePrice, shcode).ConfigureAwait(false);
                            Console.WriteLine($"[0300][UPSLIDE] DONE K={startBandNow}");
                            return true;
                        }
                    }
                }
                catch (Exception exUp)
                {
                    Console.WriteLine($"[0300][UPSLIDE] EX {exUp.Message} -> fallback normal SELL");
                }
            }

            Console.WriteLine(
                $"[0300.SELL.REQUEST] firePrice={firePrice:#,0} sellDecisionBand={sellDecisionBand} " +
                $"effectiveSellBand={sellBand} qty(Qty)={qty:#,0} startBandNow={startBandNow}"
            );

            try
            {
                try { Login.FullClearSellDecisionBand = sellDecisionBand; } catch { }
                Console.WriteLine($"[0300.SELL.EXEC] SEND -> band={sellBand} qty={qty:#,0} price={firePrice:#,0}");
                await exec.ExecuteAsync("매도", sellBand, (int)qty, firePrice).ConfigureAwait(false);
                Console.WriteLine($"[0300.SELL.EXEC] DONE band={sellBand}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[0300.SELL.EXEC] FAIL band={sellBand} msg={ex.Message}");
                return false;
            }
        }

        private static BandRange GetBand(int band)
        {
            return Login.BandList.FirstOrDefault(x => x != null && x.Band == band);
        }

        private static long CalculatePreSlideBuyQty(long orderableCash, long buyPrice, int targetBand)
        {
            if (orderableCash <= 0 || buyPrice <= 0)
                return 0;

            int zeroBandCount = CountZeroBandsInCurrentTenBandWindow(targetBand);
            if (zeroBandCount <= 0)
                zeroBandCount = 1;

            long buyBudget = orderableCash / zeroBandCount;
            long buyQty = (long)Math.Floor((double)buyBudget / (double)buyPrice);

            Console.WriteLine(
                "[PRE-SLIDE][BUY-CALC] " +
                "orderableCash=" + orderableCash +
                " zeroBandCount=" + zeroBandCount +
                " buyBudget=" + buyBudget +
                " targetBand=" + targetBand +
                " buyPrice=" + buyPrice +
                " buyQty=" + buyQty);

            return buyQty;
        }

        private static GeneralBuyTargets ResolveGeneralBuyTargets(
            long price,
            int targetBand,
            int holdingCount,
            IList<BandRange> positions)
        {
            var targets = new GeneralBuyTargets();
            targets.HoldingCount = CountGeneralBuyHoldingBands(positions, holdingCount);
            targets.EmptySlots = 10 - targets.HoldingCount;
            if (targets.EmptySlots < 0) targets.EmptySlots = 0;
            targets.TargetBand = targetBand;

            if (price <= 0)
            {
                targets.Verdict = "STOP_INVALID_PRICE";
                return targets;
            }

            if (targets.EmptySlots <= 0)
            {
                targets.Verdict = "STOP_NO_EMPTY_SLOT";
                return targets;
            }

            targets.TargetBands = BuildGeneralBuyTargetBands(targetBand, targets.EmptySlots, positions);
            targets.Verdict = targets.TargetBands.Contains(targetBand)
                ? "TARGETS_RESOLVED"
                : "STOP_TARGET_NOT_EMPTY";
            return targets;
        }

        private static GeneralBuyBudget AllocateGeneralBuyBudget(long orderableCash, GeneralBuyTargets targets)
        {
            var budget = new GeneralBuyBudget();
            budget.Band = targets != null ? targets.TargetBand : 0;
            budget.HoldingCount = targets != null ? targets.HoldingCount : 0;
            budget.EmptySlots = targets != null ? targets.EmptySlots : 0;
            budget.TargetBands = targets != null && targets.TargetBands != null
                ? new List<int>(targets.TargetBands)
                : new List<int>();

            if (orderableCash <= 0)
            {
                budget.Verdict = "STOP_INVALID_CASH";
                budget.Calc = "cash=" + orderableCash;
                return budget;
            }

            if (targets == null || targets.EmptySlots <= 0)
            {
                budget.Verdict = "STOP_NO_EMPTY_SLOT";
                budget.Calc = "emptySlots<=0";
                return budget;
            }

            if (targets.TargetBands == null ||
                targets.TargetBands.Count <= 0 ||
                !targets.TargetBands.Contains(targets.TargetBand))
            {
                budget.Verdict = "STOP_TARGET_NOT_EMPTY";
                budget.Calc = "targetBand=" + targets.TargetBand + " not-in-empty-targets";
                return budget;
            }

            // ✅ [2026-07-10 정책 변경] 마름모 가중치 배분 폐기 → 균등분배(EVEN_SPLIT)
            // emptyCount = 현재 10밴드 가격 윈도우 안에서 실제로 qty==0인
            // 매수 대상 밴드 수(targets.TargetBands.Count). EmptySlots(전체 보유
            // 기준 이론치, 10-HoldingCount)는 진입 가능 여부/후보 상한 계산에만
            // 쓰고, 실제 예산 분모로는 쓰지 않는다.
            int emptyCount = targets.TargetBands.Count;
            if (emptyCount <= 0)
            {
                budget.Verdict = "STOP_NO_EMPTY_SLOT";
                budget.Calc = "emptyCount<=0";
                return budget;
            }

            budget.EmptyCount = emptyCount;
            budget.AllocatedCash = orderableCash / emptyCount;
            budget.Verdict = "ALLOCATED";
            budget.Calc =
                "emptyCount=" + emptyCount +
                " orderableCash=" + orderableCash +
                " budgetPerBand=" + budget.AllocatedCash;
            return budget;
        }

        private static long CalculateGeneralBuyQty(long allocatedCash, long price)
        {
            long qty = 0;
            if (allocatedCash > 0 && price > 0)
                qty = (long)Math.Floor((double)allocatedCash / (double)price);

            if (qty < 0)
                qty = 0;

            Console.WriteLine("[BUY][QTY] Cash=" + allocatedCash +
                              " Price=" + price +
                              " Qty=" + qty);
            return qty;
        }

        private static GeneralBuyOrder BuildGeneralBuyOrders(int band, long calculatedQty, long price, long orderableCash)
        {
            var order = new GeneralBuyOrder();
            order.Band = band;
            order.Qty = calculatedQty < 0 ? 0 : calculatedQty;
            order.UsedCash = order.Qty * price;
            order.RemainCash = orderableCash - order.UsedCash;
            if (order.RemainCash < 0)
                order.RemainCash = 0;
            return order;
        }

        private static int CountGeneralBuyHoldingBands(IList<BandRange> positions, int fallbackHoldingCount)
        {
            try
            {
                if (positions == null || positions.Count == 0)
                    return fallbackHoldingCount < 0 ? 0 : fallbackHoldingCount;

                return positions.Count(x => x != null && x.Qty > 0);
            }
            catch
            {
                return fallbackHoldingCount < 0 ? 0 : fallbackHoldingCount;
            }
        }

        // ✅ [2026-07-10 정책 변경] BuildGeneralBuyDiamondWeightsForEmptySlots 제거됨.
        // Case2 일반 BUY는 더 이상 마름모 가중치를 사용하지 않는다
        // (AllocateGeneralBuyBudget의 EVEN_SPLIT 로직 참고).

        private static List<int> BuildGeneralBuyTargetBands(int targetBand, int emptySlots, IList<BandRange> positions)
        {
            var result = new List<int>();

            try
            {
                var list = positions;
                if (list == null || list.Count == 0 || emptySlots <= 0)
                    return result;

                int windowStart = ResolveGeneralBuyTenBandWindowStart(targetBand, list);
                int windowEnd = windowStart + 9;

                result = list
                    .Where(x => x != null &&
                                x.Band >= windowStart &&
                                x.Band <= windowEnd &&
                                x.Qty <= 0)
                    .Select(x => x.Band)
                    .Distinct()
                    .OrderBy(x => Math.Abs(x - targetBand))
                    .ThenBy(x => x)
                    .Take(emptySlots)
                    .ToList();

                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[BUY][ALLOC_AUDIT] targetBands EX: " + ex.Message);
                return result;
            }
        }

        private static int ResolveGeneralBuyTenBandWindowStart(int targetBand, IList<BandRange> positions)
        {
            var list = positions;
            if (list == null || list.Count == 0)
                return targetBand;

            var held = list
                .Where(x => x != null && x.Qty > 0)
                .Select(x => x.Band)
                .ToList();

            int windowStart = held.Count > 0 ? held.Min() : targetBand;
            if (targetBand < windowStart)
                windowStart = targetBand;
            if (targetBand > windowStart + 9)
                windowStart = targetBand - 9;

            return windowStart;
        }

        private static int CountZeroBandsInCurrentTenBandWindow(int targetBand)
        {
            try
            {
                var list = Login.BandList;
                if (list == null || list.Count == 0)
                    return 1;

                var held = list
                    .Where(x => x != null && x.Qty > 0)
                    .Select(x => x.Band)
                    .ToList();

                int windowStart;
                if (held.Count > 0)
                {
                    windowStart = held.Min();
                    if (targetBand < windowStart)
                        windowStart = targetBand;
                    if (targetBand > windowStart + 9)
                        windowStart = targetBand - 9;
                }
                else
                {
                    windowStart = targetBand;
                }

                int windowEnd = windowStart + 9;
                int zeroCount = list.Count(x =>
                    x != null &&
                    x.Band >= windowStart &&
                    x.Band <= windowEnd &&
                    x.Qty <= 0);

                if (targetBand >= windowStart && targetBand <= windowEnd)
                {
                    var target = list.FirstOrDefault(x => x != null && x.Band == targetBand);
                    if (target == null)
                        zeroCount++;
                    // ✅ [P2 2026-07-22] target.Qty > 0(이미 보유중)인 경우는 빈 밴드가 아니므로
                    // zeroCount를 증가시키지 않는다 (기존에는 반대로 증가시켜 분모가 부풀려졌음).
                }

                return zeroCount > 0 ? zeroCount : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[PRE-SLIDE][BUY-CALC] zeroBandCount EX: " + ex.Message);
                return 1;
            }
        }
    }
}
// 2026-04-22 41852
// 2026-05-08 41792
// 2026-05-11 REMAP_FIX v1: 4인자 실행배치 오버로드 제거 (race condition 차단)
// 2026-05-11 REMAP_FIX v2: 실행(4인자) 메서드 제거 / TradeQueueItem에 Side, FirePrice 추가
// 2026-05-11 QTY_FIX v1: SELL Queue 생성 시 qty<=0 band 제외 (재진입 방지)
// 2026-05-11 QTY_FIX v2: ExecuteSell qty<=0 SKIP → return true (체인 유지)
// 2026-05-11 DELAY_FIX: OnTradeCompleted SendNext 직전 300ms delay (TEST_LIVE sync)
// 2026-05-14 REVALIDATE_FIX: SELL 체인 연속 실행 전 qty/startBand 재검증 (Queue stale 방지)
// 2026-05-14 POLICY_FIX v2: OneAndDone 정책 적용
//   - OnTradeCompleted_ThenSendNextOrFinish → 무조건 ForceStopChain("OneAndDone")
//   - 1밴드 체결 후 Queue Clear + 체인 종료
//   - 0250이 다음 틱에서 새 startBand 기준으로 돌파/꺾임 재판정
//   - RevalidateSellQueue() 제거 (더 이상 필요 없음)
