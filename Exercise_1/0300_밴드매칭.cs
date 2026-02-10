// 0300_밴드매칭.cs  (복붙용 / C# 7.3)
// ------------------------------------------------------------
// 역할:
// - Tick_Process(0250)가 전달한 FIRE(range)를 "체인(Queue) 주문"으로 변환
//
// ✅ 추가(이번 요청 핵심):
// - 체인 종료 시점(QueueEmpty/SendFail/EX 등)에서 "ChainFinished" 이벤트를 1회 발생
//   -> Login이 listView3를 딱 1번만 Refresh 하도록 연결 가능
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Exercise_1
{
    public static class 밴드매칭
    {
        private const string SIDE_BUY = "BUY";
        private const string SIDE_SELL = "SELL";

        // ✅ 체인 게이트(동시 체인 실행 완전 차단)
        private static readonly SemaphoreSlim _chainGate = new SemaphoreSlim(1, 1);

        private static readonly object _qLock = new object();
        private static Queue<int> _bandQueue = new Queue<int>(16);

        private static string _chainSide = "";
        private static long _chainFirePrice = 0;

        // (호환/로그용) 체인 시작 시점의 startBandNow
        private static int _chainStartBandAtFire = 0;

        private static bool _chainActive = false;
        private static int _inFlightBand = 0; // 현재 전송(진행) 중 밴드

        // ✅ inFlight SEND 완료 추적(레이스 방지)
        private static TaskCompletionSource<bool> _inFlightSendDoneTcs = null;
        private static int _inFlightSendBand = 0; // 이 TCS가 의미하는 band
        private static long _sendSeq = 0;         // 디버그/안전용

        // ✅ 체인 종료 이벤트(1회)
        // reason: QueueEmpty / SendFail / ExecNull / EX ...
        // side: BUY/SELL, firePrice: 마지막 체인의 firePrice
        public static event Action<string, string, long> ChainFinished;

        public static bool IsChainActive
        {
            get
            {
                lock (_qLock) { return _chainActive; }
            }
        }

        /// <summary>
        /// (구버전 호환) 단일 밴드 실행 엔트리
        /// </summary>
        public static void 실행(string side, long firePrice, int endBand, int startBand)
        {
            if (string.IsNullOrWhiteSpace(side))
            {
                Debug.WriteLine("[0300] side null/empty");
                return;
            }

            side = side.Trim().ToUpperInvariant();

            if (startBand <= 0 || endBand <= 0)
            {
                Debug.WriteLine($"[0300] invalid bands start={startBand} end={endBand}");
                return;
            }

            // ✅ 호환: startBandNow를 startBand로 넘김
            실행배치(side, firePrice, startBand, endBand, startBandNow: startBand);
        }

        // =========================================================
        // ✅ 실행배치 오버로드(호환 유지)
        // =========================================================

        public static void 실행배치(string side, long firePrice, int fromBand, int toBand)
        {
            int sb = 0;
            try { sb = Login.시작밴드변수; } catch { sb = 0; }
            실행배치(side, firePrice, fromBand, toBand, startBandNow: sb);
        }

        public static void 실행배치(string side, long firePrice, int fromBand, int toBand, int startBandNow)
        {
            if (string.IsNullOrWhiteSpace(side))
            {
                Debug.WriteLine("[0300] side null/empty");
                return;
            }

            side = side.Trim().ToUpperInvariant();

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

            bool isBuy = (side == SIDE_BUY);
            bool isSell = (side == SIDE_SELL);
            if (!isBuy && !isSell)
            {
                Console.WriteLine($"[0300] Unknown side='{side}'");
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
                        foreach (var b in bands) _bandQueue.Enqueue(b);

                        _chainSide = side;
                        _chainFirePrice = firePrice;
                        _chainStartBandAtFire = startBandNow;

                        _chainActive = true;
                        _inFlightBand = 0;

                        _inFlightSendDoneTcs = null;
                        _inFlightSendBand = 0;

                        Console.WriteLine($"[0300] RANGE={fromBand}~{toBand} -> Queue=[{string.Join(",", bands)}]");
                    }

                    await SendNextOneAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[0300] EX: " + ex);
                    ForceStopChain("EX");
                }
            });
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

        public static void OnTradeCompleted_ThenSendNextOrFinish(int completedBand)
        {
            Task.Run(async () =>
            {
                try
                {
                    await WaitInFlightSendDoneIfNeededAsync(completedBand).ConfigureAwait(false);

                    bool hasMore;
                    lock (_qLock)
                    {
                        if (!_chainActive) return;

                        _inFlightBand = 0;
                        hasMore = _bandQueue.Count > 0;
                    }

                    if (hasMore)
                    {
                        await SendNextOneAsync().ConfigureAwait(false);
                        return;
                    }

                    ForceStopChain("QueueEmpty");
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
            string side;
            long firePrice;
            int bandToSend;

            long mySeq;
            TaskCompletionSource<bool> myTcs;

            lock (_qLock)
            {
                if (!_chainActive) return;

                if (_bandQueue.Count <= 0)
                {
                    return;
                }

                side = _chainSide;
                firePrice = _chainFirePrice;

                bandToSend = _bandQueue.Dequeue();
                _inFlightBand = bandToSend;

                mySeq = ++_sendSeq;
                myTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _inFlightSendDoneTcs = myTcs;
                _inFlightSendBand = bandToSend;
            }

            var exec = LoginFormAccessor.TryGetExec();
            if (exec == null)
            {
                Console.WriteLine("[0300] Exec is null -> chain abort");
                CompleteInFlightSendDone(bandToSend, myTcs, success: false);
                ForceStopChain("ExecNull");
                return;
            }

            Console.WriteLine($"[0300] SEND band={bandToSend}");

            bool ok;
            try
            {
                int startBandNowFresh = 0;
                try { startBandNowFresh = Login.시작밴드변수; } catch { startBandNowFresh = _chainStartBandAtFire; }

                if (side == SIDE_BUY)
                {
                    ok = await ExecuteBuy_OneAsync(exec, firePrice, decisionBandK: bandToSend, startBandNow: startBandNowFresh).ConfigureAwait(false);
                }
                else
                {
                    ok = await ExecuteSell_OneAsync(exec, firePrice, decisionBand: bandToSend, startBandNow: startBandNowFresh).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[0300] SEND EX: " + ex);
                ok = false;
            }

            CompleteInFlightSendDone(bandToSend, myTcs, ok);

            if (!ok)
            {
                Console.WriteLine($"[0300] SEND FAIL band={bandToSend} -> chain abort");
                ForceStopChain("SendFail");
            }
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

            lock (_qLock)
            {
                side = _chainSide;
                firePrice = _chainFirePrice;

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

            try { _chainGate.Release(); } catch { }
            Console.WriteLine($"[0300][CHAIN] STOP reason={reason}");

            // ✅ 체인 종료 이벤트 1회
            try { ChainFinished?.Invoke(reason, side, firePrice); } catch { }
        }

        private static async Task<bool> ExecuteBuy_OneAsync(매매실행 exec, long firePrice, int decisionBandK, int startBandNow)
        {
            var k = GetBand(decisionBandK);
            if (k == null)
            {
                Console.WriteLine($"[0300][BUY] K not found K={decisionBandK}");
                return false;
            }

            long qty = k.Sina;
            if (qty <= 0)
            {
                Console.WriteLine($"[0300][BUY] SKIP K.Sina<=0  K={decisionBandK} Sina={qty}");
                return false;
            }

            int updateBand = decisionBandK + 1;
            var ub = GetBand(updateBand);
            if (ub == null)
            {
                Console.WriteLine($"[0300][BUY] SKIP updateBand(K+1) not found. K={decisionBandK} => {updateBand}");
                return false;
            }

            Console.WriteLine(
                $"[0300.BUY.REQUEST] firePrice={firePrice:#,0} decisionBand(K)={decisionBandK} qty(K.Sina)={qty:#,0} " +
                $"updateQtyBand(K+1)={updateBand} startBandNow={startBandNow}"
            );

            try
            {
                Console.WriteLine($"[0300.BUY.EXEC] SEND -> band(K)={decisionBandK} qty={qty:#,0} price={firePrice:#,0}");
                await exec.ExecuteAsync("매수", decisionBandK, (int)qty, firePrice).ConfigureAwait(false);
                Console.WriteLine($"[0300.BUY.EXEC] DONE band(K)={decisionBandK}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[0300.BUY.EXEC] FAIL band(K)={decisionBandK} msg={ex.Message}");
                return false;
            }
        }

        private static async Task<bool> ExecuteSell_OneAsync(매매실행 exec, long firePrice, int decisionBand, int startBandNow)
        {
            var b = GetBand(decisionBand);
            if (b == null)
            {
                Console.WriteLine($"[0300][SELL] band not found band={decisionBand}");
                return false;
            }

            long qty = b.Qty;
            if (qty <= 0)
            {
                Console.WriteLine($"[0300][SELL] SKIP Qty<=0 band={decisionBand} Qty={qty}");
                return false;
            }

            Console.WriteLine(
                $"[0300.SELL.REQUEST] firePrice={firePrice:#,0} decisionBand={decisionBand} qty(Qty)={qty:#,0} startBandNow={startBandNow}"
            );

            try
            {
                Console.WriteLine($"[0300.SELL.EXEC] SEND -> band={decisionBand} qty={qty:#,0} price={firePrice:#,0}");
                await exec.ExecuteAsync("매도", decisionBand, (int)qty, firePrice).ConfigureAwait(false);
                Console.WriteLine($"[0300.SELL.EXEC] DONE band={decisionBand}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[0300.SELL.EXEC] FAIL band={decisionBand} msg={ex.Message}");
                return false;
            }
        }

        private static BandRange GetBand(int band)
        {
            return Login.BandList.FirstOrDefault(x => x != null && x.Band == band);
        }
    }
}
// 2026-02-10 19755
