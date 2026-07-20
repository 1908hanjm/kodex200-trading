// 0600_주문번호_매핑.cs  (복붙용 / C# 7.3)
// ------------------------------------------------------------
// 역할(확정):
// - 주문전송확인 성공(OrdNo 존재) 시:
//   OrdNo -> (sideKor, executeBand, orderQty, cumFill=0, 등록시각) 저장
// - SC1 수신 시:
//   ordNo로 레코드 조회 후 cumFill 누적
//   -> partial / complete 판정에 필요한 정보 제공
//
// ✅ 이번 수정 핵심:
// - 0300에서 확정한 ExecuteBand가 0400 주문전송을 거쳐 0600에 저장된다.
// - 0650은 0600이 반환하는 band를 실제 주문 band로 신뢰한다.
// - 0600 내부 용어를 ExecuteBand 중심으로 정리했다.
// - 기존 Register(side, band, ordNo, qty) 호출은 그대로 호환된다.
// - 선택적으로 Register(side, queuedBand, executeBand, ordNo, qty)도 지원한다.
//
// 중요 원칙:
// ExecuteBand = 실제 주문 나간 band = OrdMap 저장 band = 0650 체결 처리 band = 0700 DB update band
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Data.SQLite;
using System.Globalization;
using System.Linq;

namespace Exercise_1
{
    public sealed class _0600_주문번호_매핑
    {
        private readonly object _lock = new object();

        private readonly Dictionary<long, OrdState> _ordNoToState = new Dictionary<long, OrdState>();

        // Register 완료 이벤트 (ordNo)
        public event Action<long> OrdRegistered;

        private sealed class OrdState
        {
            public string SideKor;

            // QueuedBand는 0300 FIRE/Queue 원본 band 보관용이다.
            // 일반 경로에서는 QueuedBand == ExecuteBand 이다.
            public int QueuedBand;

            // ExecuteBand는 실제 주문이 나간 band이다.
            // 0650/0700은 이 값을 기준으로 처리해야 한다.
            public int ExecuteBand;

            public int OrderQty;
            public int CumFill;
            public DateTime CreatedAt;
            public bool CanceledConfirmed;
            public int DownSlideFromBand;
            public int DownSlideFromQty;
            public long DownSlideExtraQty;
            public int DownSlideRecordBand;
            public string ChainType;
            public int UpSlideSourceBand;
            public int UpSlideTargetBand;
            public long UpSlideFromQty;
            public long UpSlideExtraQty;
            public int UpSlideRecordBand;
            public int ChainExtendFromBand;
            public int ChainExtendTargetBand;
            public int ChainExtendRecordBand;

            // ✅ [BUG-FIX] 완전체결 후 비동기로 늦게 도착하는 SC1 이벤트 중복처리 방지
            // AddFill에서 isComplete=true를 최초로 반환한 시점에 true로 세팅된다.
            // 이후 동일 ordNo로 AddFill이 재호출되면 AlreadyCompleted=true를 반환하여
            // 0650이 COMPLETE 처리(FinalizeAfterUnlock, OnTradeCompleted 등)를 재실행하지 않도록 한다.
            public bool AlreadyCompleted;

            public bool IsRebuildBuy;
            public int RebuildLastSoldBand;
            public int RebuildSellDecisionBand;
            public int RebuildStartBand;
            public List<int> RebuildTargetBands = new List<int>();
            public string RebuildDistributionMode;

            // 재기동 원장에서 복원된 스냅샷. 기존 주문 계산에는 개입하지 않는다.
            public string RecoveryTradeType;
            public long RecoveryFromBand;
            public long RecoveryFromQty;
            public long RecoveryExtraQty;
            public string RecoverySource;
        }

        // ------------------------------------------------------------
        // 기존 호출 호환: band는 실제 주문 band(ExecuteBand)로 취급한다.
        // ------------------------------------------------------------
        public void Register(string sideKor, int band, string ordNoRaw, int orderQty)
        {
            long ordNo = ParseOrdNo(ordNoRaw);
            Register(sideKor, band, band, ordNo, orderQty);
        }

        public void Register(string sideKor, int band, long ordNo, int orderQty)
        {
            Register(sideKor, band, band, ordNo, orderQty);
        }

        // ------------------------------------------------------------
        // 신규 명시형 호출: queuedBand와 executeBand를 분리해서 저장 가능
        // ------------------------------------------------------------
        public void Register(string sideKor, int queuedBand, int executeBand, string ordNoRaw, int orderQty)
        {
            long ordNo = ParseOrdNo(ordNoRaw);
            Register(sideKor, queuedBand, executeBand, ordNo, orderQty);
        }

        public void Register(string sideKor, int queuedBand, int executeBand, long ordNo, int orderQty)
        {
            TryRegister(sideKor, queuedBand, executeBand, ordNo, orderQty);
        }

        public bool TryRegister(string sideKor, int band, long ordNo, int orderQty)
        {
            return TryRegister(sideKor, band, band, ordNo, orderQty);
        }

        public bool TryRegister(string sideKor, int queuedBand, int executeBand, long ordNo, int orderQty)
        {
            if (ordNo <= 0) throw new ArgumentOutOfRangeException(nameof(ordNo));
            if (orderQty <= 0) throw new ArgumentOutOfRangeException(nameof(orderQty));
            if (string.IsNullOrWhiteSpace(sideKor)) throw new ArgumentNullException(nameof(sideKor));
            if (executeBand <= 0) throw new ArgumentOutOfRangeException(nameof(executeBand));
            if (queuedBand <= 0) queuedBand = executeBand;

            string side = sideKor.Trim();

            Action<long> ev = null;

            lock (_lock)
            {
                if (_ordNoToState.ContainsKey(ordNo))
                {
                    Console.WriteLine("[0600][SKIP] already registered ordNo=" + ordNo);
                    return false;
                }

                int downFromBand = 0;
                int downFromQty = 0;
                long downExtraQty = 0;
                int downRecordBand = 0;
                string chainType = "";
                int upSourceBand = 0;
                int upTargetBand = 0;
                long upFromQty = 0;
                long upExtraQty = 0;
                int upRecordBand = 0;
                int chainExtendFromBand = 0;
                int chainExtendTargetBand = 0;
                int chainExtendRecordBand = 0;

                try
                {
                    // ✅ [FIX] SwapInProgress 플래그 조건 완화:
                    // 2160이 BUY ExecuteAsync() 전송 후 0600.TryRegister() 호출 사이에
                    // 타이밍 경쟁으로 SwapInProgress가 이미 false가 될 수 있다.
                    // SwapFromBand / SwapFromQty / SwapRecordBandK 값이 모두 유효하면
                    // SwapInProgress 플래그와 무관하게 다운슬라이딩 스냅샷으로 사용한다.
                    if (side == "매수" &&
                        Login.SwapFromBand > 0 &&
                        Login.SwapFromQty > 0 &&
                        Login.SwapRecordBandK > 0)
                    {
                        downFromBand = Login.SwapFromBand;
                        downFromQty = Login.SwapFromQty;
                        downRecordBand = Login.SwapRecordBandK;
                        downExtraQty = Login.SwapExtraQty > 0
                            ? Login.SwapExtraQty
                            : ReadExtraQtySnapshot(downRecordBand);

                        if (!Login.SwapInProgress)
                        {
                            Console.WriteLine(
                                $"[0600][DOWN-LINK][FLAG-RACE] SwapInProgress=false but values valid -> snapshot used" +
                                $" fromBand={downFromBand} fromQty={downFromQty} extraQty={downExtraQty} recordBand={downRecordBand}");
                        }
                    }
                }
                catch
                {
                    downFromBand = 0;
                    downFromQty = 0;
                    downExtraQty = 0;
                    downRecordBand = 0;
                }

                try
                {
                    if (side == "매수" &&
                        Login.UpSwapStartBand > 0 &&
                        Login.UpSwapTargetBand > 0 &&
                        Login.UpSwapFromQty > 0 &&
                        executeBand == Login.UpSwapTargetBand)
                    {
                        chainType = "UPSLIDE";
                        upSourceBand = Login.UpSwapStartBand;
                        upTargetBand = Login.UpSwapTargetBand;
                        upFromQty = Login.UpSwapFromQty;
                        upExtraQty = Login.UpSwapExtraQty > 0 ? Login.UpSwapExtraQty : 0;
                        upRecordBand = Login.UpSwapTargetBand;
                    }
                }
                catch
                {
                    chainType = "";
                    upSourceBand = 0;
                    upTargetBand = 0;
                    upFromQty = 0;
                    upExtraQty = 0;
                    upRecordBand = 0;
                }

                try
                {
                    ChainExtendBuyContext chainExtend;
                    if (side == "매수" &&
                        _0320_10전슬라이딩BUY.TryCaptureForOrder(executeBand, out chainExtend) &&
                        chainExtend != null &&
                        chainExtend.TargetBand > 0 &&
                        chainExtend.FromBand > 0)
                    {
                        chainType = "10전슬라이딩BUY";
                        chainExtendFromBand = chainExtend.FromBand;
                        chainExtendTargetBand = chainExtend.TargetBand;
                        chainExtendRecordBand = chainExtend.TargetBand;
                    }
                }
                catch
                {
                    chainExtendFromBand = 0;
                    chainExtendTargetBand = 0;
                    chainExtendRecordBand = 0;
                }

                _ordNoToState[ordNo] = new OrdState
                {
                    SideKor = side,
                    QueuedBand = queuedBand,
                    ExecuteBand = executeBand,
                    OrderQty = orderQty,
                    CumFill = 0,
                    CreatedAt = DateTime.Now,
                    CanceledConfirmed = false,
                    AlreadyCompleted = false,
                    IsRebuildBuy = false,
                    RebuildLastSoldBand = 0,
                    RebuildSellDecisionBand = 0,
                    RebuildStartBand = 0,
                    RebuildTargetBands = new List<int>(),
                    RebuildDistributionMode = "",
                    DownSlideFromBand = downFromBand,
                    DownSlideFromQty = downFromQty,
                    DownSlideExtraQty = downExtraQty,
                    DownSlideRecordBand = downRecordBand,
                    ChainType = chainType,
                    UpSlideSourceBand = upSourceBand,
                    UpSlideTargetBand = upTargetBand,
                    UpSlideFromQty = upFromQty,
                    UpSlideExtraQty = upExtraQty,
                    UpSlideRecordBand = upRecordBand,
                    ChainExtendFromBand = chainExtendFromBand,
                    ChainExtendTargetBand = chainExtendTargetBand,
                    ChainExtendRecordBand = chainExtendRecordBand
                };

                Console.WriteLine(
                    $"[0600][REG] ordNo={ordNo} side={side} queuedBand={queuedBand} executeBand={executeBand} orderQty={orderQty} " +
                    $"downFromBand={downFromBand} downFromQty={downFromQty} downExtraQty={downExtraQty} downRecordBand={downRecordBand}");
                Debug.WriteLine(
                    $"[0600][REG] ordNo={ordNo} side={side} queuedBand={queuedBand} executeBand={executeBand} orderQty={orderQty} " +
                    $"downFromBand={downFromBand} downFromQty={downFromQty} downExtraQty={downExtraQty} downRecordBand={downRecordBand}");

                if (chainType == "UPSLIDE")
                {
                    string upLog =
                        $"[0600][REG][CHAIN_META] ordNo={ordNo} side={side} chainType={chainType} " +
                        $"sourceBand={upSourceBand} targetBand={upTargetBand} fromQty={upFromQty} extraQty={upExtraQty} recordBand={upRecordBand}";
                    Console.WriteLine(upLog);
                    Debug.WriteLine(upLog);
                }
                else if (chainType == "10전슬라이딩BUY")
                {
                    string chainLog =
                        $"[0600][REG][10전슬라이딩BUY] ordNo={ordNo} side={side} tradeType=10전슬라이딩BUY " +
                        $"targetBand={chainExtendTargetBand} fromBand={chainExtendFromBand} fromQty=0 recordBand={chainExtendRecordBand}";
                    Console.WriteLine(chainLog);
                    Debug.WriteLine(chainLog);
                }

                ev = OrdRegistered;
            }

            try
            {
                ev?.Invoke(ordNo);
            }
            catch { }
            return true;
        }

        public bool Contains(long ordNo)
        {
            lock (_lock)
            {
                return _ordNoToState.ContainsKey(ordNo);
            }
        }

        public bool TryRestoreFromRecovery(
            string sideRaw,
            int executeBand,
            long ordNo,
            int orderQty,
            int cumFill,
            long fromBand,
            long fromQty,
            long extraQty,
            string tradeType,
            string source,
            out string reason)
        {
            reason = "";
            if (ordNo <= 0) { reason = "invalid_ordNo"; return false; }
            if (executeBand <= 0) { reason = "invalid_band"; return false; }
            if (orderQty <= 0) { reason = "invalid_orderQty"; return false; }
            if (cumFill < 0 || cumFill > orderQty) { reason = "invalid_cumFill"; return false; }
            if (fromBand < 0 || fromBand > int.MaxValue || fromQty < 0 || extraQty < 0)
            {
                reason = "invalid_recovery_metadata";
                return false;
            }

            string side = NormalizeRecoverySide(sideRaw);
            if (side != "매수" && side != "매도")
            {
                reason = "invalid_side";
                return false;
            }

            Action<long> ev;
            lock (_lock)
            {
                if (_ordNoToState.ContainsKey(ordNo))
                {
                    reason = "already_registered";
                    return false;
                }

                string type = (tradeType ?? "").Trim();
                bool isUpSlide = string.Equals(type, "상승슬라이딩BUY", StringComparison.Ordinal);
                bool isDownSlide = string.Equals(type, "하락슬라이딩BUY", StringComparison.Ordinal);
                bool isChainExtend = string.Equals(type, "10전슬라이딩BUY", StringComparison.Ordinal);
                bool isGeneralBuy = string.Equals(type, "일반BUY", StringComparison.Ordinal);
                bool isGeneralSell = string.Equals(type, "일반SELL", StringComparison.Ordinal);

                bool metaValid =
                    (side == "매수" && isGeneralBuy && fromBand == 0 && fromQty == 0 && extraQty == 0) ||
                    (side == "매수" && isChainExtend && fromBand > 0 && fromQty >= 0 && extraQty == 0) ||
                    (side == "매수" && (isUpSlide || isDownSlide) && fromBand > 0 && fromQty > 0) ||
                    (side == "매도" && isGeneralSell);

                if (!metaValid)
                {
                    reason = "ambiguous_trade_type";
                    Console.WriteLine("[RESTART_RECOVERY][RESTORE_META_AMBIGUOUS] " +
                                      "ordNo=" + ordNo + " tradeType=" + type +
                                      " reason=stored_metadata_not_exact_or_incomplete");
                    return false;
                }

                Console.WriteLine("[RESTART_RECOVERY][RESTORE_USE_STORED_META] " +
                                  "ordNo=" + ordNo + " tradeType=" + type +
                                  " band=" + executeBand + " from_band=" + fromBand +
                                  " from_qty=" + fromQty + " extra_qty=" + extraQty);

                var state = new OrdState
                {
                    SideKor = side,
                    QueuedBand = executeBand,
                    ExecuteBand = executeBand,
                    OrderQty = orderQty,
                    CumFill = cumFill,
                    CreatedAt = DateTime.Now,
                    CanceledConfirmed = false,
                    AlreadyCompleted = cumFill >= orderQty,
                    IsRebuildBuy = false,
                    RebuildLastSoldBand = 0,
                    RebuildSellDecisionBand = 0,
                    RebuildStartBand = 0,
                    RebuildTargetBands = new List<int>(),
                    RebuildDistributionMode = "",
                    RecoveryTradeType = type,
                    RecoveryFromBand = fromBand,
                    RecoveryFromQty = fromQty,
                    RecoveryExtraQty = extraQty,
                    RecoverySource = source ?? ""
                };

                if (side == "매수" && fromBand > 0 && fromQty > 0 && isUpSlide)
                {
                    state.ChainType = "UPSLIDE";
                    state.UpSlideSourceBand = Convert.ToInt32(fromBand);
                    state.UpSlideTargetBand = executeBand;
                    state.UpSlideFromQty = fromQty;
                    state.UpSlideExtraQty = extraQty;
                    state.UpSlideRecordBand = executeBand;
                }
                else if (side == "매수" && fromBand > 0 && fromQty > 0 && isDownSlide)
                {
                    state.DownSlideFromBand = Convert.ToInt32(fromBand);
                    state.DownSlideFromQty = fromQty > int.MaxValue ? int.MaxValue : Convert.ToInt32(fromQty);
                    state.DownSlideExtraQty = extraQty;
                    state.DownSlideRecordBand = executeBand < int.MaxValue ? executeBand + 1 : executeBand;
                }
                else if (side == "매수" && fromBand > 0 && isChainExtend)
                {
                    state.ChainType = "10전슬라이딩BUY";
                    state.ChainExtendFromBand = Convert.ToInt32(fromBand);
                    state.ChainExtendTargetBand = executeBand < int.MaxValue ? executeBand + 1 : executeBand;
                    state.ChainExtendRecordBand = state.ChainExtendTargetBand;
                }

                _ordNoToState[ordNo] = state;
                ev = OrdRegistered;
            }

            try { ev?.Invoke(ordNo); } catch { }
            return true;
        }

        // ------------------------------------------------------------
        // 조회: 기존 이름 유지. 반환 band는 ExecuteBand이다.
        // ------------------------------------------------------------
        public bool TryGetSideBand(long ordNo, out string sideKor, out int band)
        {
            lock (_lock)
            {
                OrdState st;
                if (_ordNoToState.TryGetValue(ordNo, out st))
                {
                    sideKor = st.SideKor;
                    band = st.ExecuteBand;
                    return true;
                }
            }

            sideKor = null;
            band = 0;
            return false;
        }

        // listView3용 실제 반영 밴드 조회.
        // OrdMap의 ExecuteBand는 0650/0700 및 재기동 복구에 필요하므로 변경하지 않는다.
        // 일반 BUY만 0700과 동일하게 K+1, 특수 BUY는 주문별 recordBand, SELL은 K를 표시한다.
        public bool TryGetDisplayBand(long ordNo, out string sideKor, out int displayBand)
        {
            lock (_lock)
            {
                OrdState st;
                if (_ordNoToState.TryGetValue(ordNo, out st))
                {
                    sideKor = st.SideKor;
                    displayBand = st.ExecuteBand;

                    if (st.SideKor == "매수" && !st.IsRebuildBuy)
                    {
                        if (st.ChainType == "UPSLIDE" && st.UpSlideRecordBand > 0)
                        {
                            displayBand = st.UpSlideRecordBand;
                        }
                        else if (st.DownSlideRecordBand > 0)
                        {
                            displayBand = st.DownSlideRecordBand;
                        }
                        else if (st.ExecuteBand < int.MaxValue)
                        {
                            displayBand = st.ExecuteBand + 1;
                        }
                    }

                    return displayBand > 0;
                }
            }

            sideKor = null;
            displayBand = 0;
            return false;
        }

        public bool TryGetQueuedAndExecuteBand(long ordNo, out int queuedBand, out int executeBand)
        {
            lock (_lock)
            {
                OrdState st;
                if (_ordNoToState.TryGetValue(ordNo, out st))
                {
                    queuedBand = st.QueuedBand;
                    executeBand = st.ExecuteBand;
                    return true;
                }
            }

            queuedBand = 0;
            executeBand = 0;
            return false;
        }

        public bool TryGetOrderQty(long ordNo, out int orderQty)
        {
            lock (_lock)
            {
                OrdState st;
                if (_ordNoToState.TryGetValue(ordNo, out st))
                {
                    orderQty = st.OrderQty;
                    return true;
                }
            }

            orderQty = 0;
            return false;
        }

        public bool TryGetOrderProgress(long ordNo, out int orderQty, out int cumFill, out int remain)
        {
            lock (_lock)
            {
                OrdState st;
                if (_ordNoToState.TryGetValue(ordNo, out st))
                {
                    orderQty = st.OrderQty;
                    cumFill = st.CumFill;
                    remain = st.OrderQty - st.CumFill;
                    if (remain < 0) remain = 0;
                    return true;
                }
            }

            orderQty = 0;
            cumFill = 0;
            remain = 0;
            return false;
        }

        // ACK 원장에 OrdMap과 동일한 스냅샷을 저장하기 위한 읽기 전용 메타데이터 조회.
        // 재기동 복원/fallback은 수행하지 않는다.
        public bool TryGetRecoveryMetadata(
            long ordNo,
            out int fromBand,
            out long fromQty,
            out long extraQty,
            out string tradeType)
        {
            lock (_lock)
            {
                OrdState st;
                if (_ordNoToState.TryGetValue(ordNo, out st))
                {
                    if (!string.IsNullOrWhiteSpace(st.RecoveryTradeType))
                    {
                        fromBand = st.RecoveryFromBand > int.MaxValue
                            ? int.MaxValue
                            : Convert.ToInt32(st.RecoveryFromBand);
                        fromQty = st.RecoveryFromQty;
                        extraQty = st.RecoveryExtraQty;
                        tradeType = st.RecoveryTradeType;
                        return true;
                    }

                    fromBand = 0;
                    fromQty = 0;
                    extraQty = 0;

                    if (st.ChainType == "10전슬라이딩BUY" && st.ChainExtendFromBand > 0)
                    {
                        fromBand = st.ChainExtendFromBand;
                        fromQty = 0;
                        extraQty = 0;
                    }
                    else if (st.ChainType == "UPSLIDE" && st.UpSlideSourceBand > 0)
                    {
                        fromBand = st.UpSlideSourceBand;
                        fromQty = st.UpSlideFromQty;
                        extraQty = st.UpSlideExtraQty;
                    }
                    else if (st.DownSlideFromBand > 0)
                    {
                        fromBand = st.DownSlideFromBand;
                        fromQty = st.DownSlideFromQty;
                        extraQty = st.DownSlideExtraQty;
                    }

                    if (st.SideKor == "매수")
                    {
                        tradeType = st.IsRebuildBuy
                            ? "완전청산BUY"
                            : (st.ChainType == "UPSLIDE"
                                ? "상승슬라이딩BUY"
                                : (st.ChainType == "10전슬라이딩BUY"
                                    ? "10전슬라이딩BUY"
                                    : (fromBand > 0 ? "하락슬라이딩BUY" : "일반BUY")));
                    }
                    else
                    {
                        tradeType = "일반SELL";
                    }
                    return true;
                }
            }

            fromBand = 0;
            fromQty = 0;
            extraQty = 0;
            tradeType = "";
            return false;
        }

        private static string NormalizeRecoverySide(string sideRaw)
        {
            string side = (sideRaw ?? "").Trim();
            if (side == "매수" || side.Equals("BUY", StringComparison.OrdinalIgnoreCase) || side == "2") return "매수";
            if (side == "매도" || side.Equals("SELL", StringComparison.OrdinalIgnoreCase) || side == "1") return "매도";
            return "";
        }

        public bool TryGetDownSlideLink(long ordNo, out int fromBand, out int fromQty, out int recordBand)
        {
            lock (_lock)
            {
                OrdState st;
                if (_ordNoToState.TryGetValue(ordNo, out st) &&
                    st.DownSlideFromBand > 0 &&
                    st.DownSlideFromQty > 0 &&
                    st.DownSlideRecordBand > 0)
                {
                    fromBand = st.DownSlideFromBand;
                    fromQty = st.DownSlideFromQty;
                    recordBand = st.DownSlideRecordBand;
                    return true;
                }
            }

            fromBand = 0;
            fromQty = 0;
            recordBand = 0;
            return false;
        }

        public bool TryGetUpSlideLink(
            long ordNo,
            out string chainType,
            out int sourceBand,
            out int targetBand,
            out long fromQty,
            out long extraQty,
            out int recordBand)
        {
            lock (_lock)
            {
                OrdState st;
                if (_ordNoToState.TryGetValue(ordNo, out st) &&
                    st.ChainType == "UPSLIDE" &&
                    st.UpSlideSourceBand > 0 &&
                    st.UpSlideTargetBand > 0 &&
                    st.UpSlideFromQty > 0)
                {
                    chainType = st.ChainType;
                    sourceBand = st.UpSlideSourceBand;
                    targetBand = st.UpSlideTargetBand;
                    fromQty = st.UpSlideFromQty;
                    extraQty = st.UpSlideExtraQty;
                    recordBand = st.UpSlideRecordBand > 0 ? st.UpSlideRecordBand : st.UpSlideTargetBand;
                    return true;
                }
            }

            chainType = "";
            sourceBand = 0;
            targetBand = 0;
            fromQty = 0;
            extraQty = 0;
            recordBand = 0;
            return false;
        }

        public bool TryGetChainExtendLink(
            long ordNo,
            out int fromBand,
            out int targetBand,
            out int recordBand)
        {
            lock (_lock)
            {
                OrdState st;
                if (_ordNoToState.TryGetValue(ordNo, out st) &&
                    st.ChainType == "10전슬라이딩BUY" &&
                    st.ChainExtendFromBand > 0 &&
                    st.ChainExtendTargetBand > 0)
                {
                    fromBand = st.ChainExtendFromBand;
                    targetBand = st.ChainExtendTargetBand;
                    recordBand = st.ChainExtendRecordBand > 0 ? st.ChainExtendRecordBand : st.ChainExtendTargetBand;
                    return true;
                }
            }

            fromBand = 0;
            targetBand = 0;
            recordBand = 0;
            return false;
        }

        /// <summary>
        /// SC1 체결 수량을 누적 반영한다.
        /// 반환되는 band는 반드시 ExecuteBand이다.
        /// alreadyComplete=true 이면 이미 이전에 완전체결 처리된 주문이므로
        /// 0650은 COMPLETE 후속처리(FinalizeAfterUnlock, OnTradeCompleted 등)를 실행하면 안 된다.
        /// </summary>
        public bool AddFill(
            long ordNo,
            int filledQty,
            out string sideKor,
            out int band,
            out int orderQty,
            out int cumFill,
            out int remain,
            out bool isComplete,
            out bool alreadyComplete)
        {
            if (filledQty <= 0) throw new ArgumentOutOfRangeException(nameof(filledQty));

            lock (_lock)
            {
                OrdState st;
                if (!_ordNoToState.TryGetValue(ordNo, out st))
                {
                    sideKor = null;
                    band = 0;
                    orderQty = 0;
                    cumFill = 0;
                    remain = 0;
                    isComplete = false;
                    alreadyComplete = false;
                    return false;
                }

                // ✅ [BUG-FIX] 이미 완전체결 처리된 주문에 뒤늦은 SC1 이벤트가 도착한 경우
                // cumFill/remain은 최신 상태로 반환하되, alreadyComplete=true로 표시한다.
                // 0650은 이 값을 보고 COMPLETE 후속처리(Finalize, OnTradeCompleted 등)를 건너뛴다.
                if (st.AlreadyCompleted)
                {
                    sideKor = st.SideKor;
                    band = st.ExecuteBand;
                    orderQty = st.OrderQty;
                    cumFill = st.CumFill;
                    remain = 0;
                    isComplete = true;
                    alreadyComplete = true;
                    Console.WriteLine(
                        $"[0600][FILL][SKIP] ordNo={ordNo} +{filledQty} -> AlreadyCompleted -> 중복처리 방지");
                    return true;
                }

                st.CumFill += filledQty;

                if (st.CumFill > st.OrderQty)
                {
                    Console.WriteLine(
                        $"[0600][WARN] cumFill > orderQty ordNo={ordNo} cumFill={st.CumFill} orderQty={st.OrderQty} (clamp)");
                    st.CumFill = st.OrderQty;
                }

                sideKor = st.SideKor;

                // 핵심: 0650으로 넘기는 band는 ExecuteBand이다.
                band = st.ExecuteBand;

                orderQty = st.OrderQty;
                cumFill = st.CumFill;
                remain = st.OrderQty - st.CumFill;
                isComplete = (remain == 0);
                alreadyComplete = false;

                // ✅ [BUG-FIX] 최초 완전체결 시점에 플래그 세팅
                if (isComplete)
                    st.AlreadyCompleted = true;

                // ✅ [LOG-REDUCE] 틱 단위 부분체결마다 찍히던 로그를 Debug로 내리고,
                // 완전체결 시점 1회만 로그파일(Console)에 남긴다.
                if (isComplete)
                {
                    Console.WriteLine(
                        $"[0600][FILL][COMPLETE] ordNo={ordNo} queuedBand={st.QueuedBand} executeBand={st.ExecuteBand} cumFill={cumFill}/{orderQty}");
                }
                Debug.WriteLine(
                    $"[0600][FILL] ordNo={ordNo} +{filledQty} -> queuedBand={st.QueuedBand} executeBand={st.ExecuteBand} cumFill={cumFill}/{orderQty} remain={remain} complete={isComplete}");

                return true;
            }
        }

        // ✅ [하위 호환] alreadyComplete 파라미터 없는 기존 호출도 지원
        public bool AddFill(
            long ordNo,
            int filledQty,
            out string sideKor,
            out int band,
            out int orderQty,
            out int cumFill,
            out int remain,
            out bool isComplete)
        {
            bool alreadyComplete;
            return AddFill(ordNo, filledQty, out sideKor, out band, out orderQty,
                           out cumFill, out remain, out isComplete, out alreadyComplete);
        }

        public void MarkRebuildBuy(
            long ordNo,
            int lastSoldBand,
            int sellDecisionBand,
            int rebuildStartBand,
            IEnumerable<int> targetBands,
            string distributionMode)
        {
            lock (_lock)
            {
                OrdState st;
                if (!_ordNoToState.TryGetValue(ordNo, out st))
                {
                    Console.WriteLine("[0600][REBUILD][WARN] MarkRebuildBuy mapping not found ordNo=" + ordNo);
                    return;
                }

                st.IsRebuildBuy = true;
                st.RebuildLastSoldBand = lastSoldBand;
                st.RebuildSellDecisionBand = sellDecisionBand;
                st.RebuildStartBand = rebuildStartBand;
                st.RebuildTargetBands = targetBands != null ? targetBands.ToList() : new List<int>();
                st.RebuildDistributionMode = string.IsNullOrWhiteSpace(distributionMode)
                    ? "AsymmetricDiamond"
                    : distributionMode.Trim();

                Console.WriteLine("[0600][REBUILD][REG] ordNo=" + ordNo +
                                  " IsRebuildBuy=true lastSoldBand=" + lastSoldBand +
                                  " sellDecisionBand=" + sellDecisionBand +
                                  " rebuildStartBand=" + rebuildStartBand +
                                  " distributionMode=" + st.RebuildDistributionMode +
                                  " targetBands=" + string.Join(",", st.RebuildTargetBands));
            }
        }

        public bool TryGetRebuildBuyInfo(
            long ordNo,
            out int lastSoldBand,
            out int sellDecisionBand,
            out int rebuildStartBand,
            out List<int> targetBands,
            out string distributionMode)
        {
            lock (_lock)
            {
                OrdState st;
                if (_ordNoToState.TryGetValue(ordNo, out st) && st.IsRebuildBuy)
                {
                    lastSoldBand = st.RebuildLastSoldBand;
                    sellDecisionBand = st.RebuildSellDecisionBand;
                    rebuildStartBand = st.RebuildStartBand;
                    targetBands = st.RebuildTargetBands != null ? new List<int>(st.RebuildTargetBands) : new List<int>();
                    distributionMode = st.RebuildDistributionMode ?? "";
                    return true;
                }
            }

            lastSoldBand = 0;
            sellDecisionBand = 0;
            rebuildStartBand = 0;
            targetBands = new List<int>();
            distributionMode = "";
            return false;
        }

        /// <summary>
        /// 전량취소 확정 표시(취소모듈/T0425가 호출)
        /// </summary>
        public void MarkCancelConfirmed(long ordNo)
        {
            lock (_lock)
            {
                OrdState st;
                if (_ordNoToState.TryGetValue(ordNo, out st))
                {
                    st.CanceledConfirmed = true;
                    Console.WriteLine($"[0600][CANCEL.CONFIRMED] ordNo={ordNo}");
                }
            }
        }

        private static long ReadExtraQtySnapshot(int band)
        {
            if (band <= 0)
                return 0L;

            try
            {
                using (var conn = new SQLiteConnection(Login.ConnStr))
                {
                    conn.Open();
                    DB_Control.EnsureExtraQtyColumn(conn);

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT extra_qty FROM kodex200_new WHERE band=@b LIMIT 1";
                        cmd.Parameters.AddWithValue("@b", band);
                        object val = cmd.ExecuteScalar();
                        long extraQty = val != null && val != DBNull.Value ? Convert.ToInt64(val) : 0L;
                        return extraQty < 0 ? 0L : extraQty;
                    }
                }
            }
            catch
            {
                return 0L;
            }
        }

        public static long ParseOrdNo(string ordNoRaw)
        {
            ordNoRaw = (ordNoRaw ?? "").Trim();
            if (string.IsNullOrWhiteSpace(ordNoRaw))
                throw new Exception("OrdNo is empty.");

            long ordNo;
            if (!long.TryParse(ordNoRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out ordNo))
                throw new Exception($"OrdNo parse fail raw='{ordNoRaw}'");

            if (ordNo <= 0) throw new Exception($"OrdNo invalid ordNo={ordNo}");
            return ordNo;
        }

        // 기존 코드가 Register(side, band, ordNoRaw)로 부르는 경우 호환용
        public void Register(string sideKor, int band, string ordNoRaw)
        {
            int qty = 0;
            try
            {
                qty = Login.TradeWait != null ? Login.TradeWait.LockedOrderQty : 0;
            }
            catch
            {
                qty = 0;
            }

            long ordNo = ParseOrdNo(ordNoRaw);

            if (qty <= 0)
            {
                Console.WriteLine(
                    $"[0600][WARN] Register(3args) qty unknown ordNo={ordNo} -> complete 판단 불완전, qty=1 임시 저장");
            }

            Register(sideKor, band, band, ordNo, (qty > 0 ? qty : 1));
        }
    }
}
// 2026-05-08 73941
