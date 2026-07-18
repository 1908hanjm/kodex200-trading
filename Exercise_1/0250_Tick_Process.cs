// 0250_Tick_Process.cs  (복붙용 / C# 7.3)
// ------------------------------------------------------------
// ✅ 확정(업데이트):
// 1) 로그(콘솔 출력)는 "돌파(state) 발생 이후의 틱만" 출력한다.
// 2) FIRE 허용 조건은 "현재밴드 OUT"이 아니라 "StartBand/LastBreakBand OUT" 기준으로 본다.
//    - BUY  FIRE 허용: cur < LastBreakBand.살가격
//    - SELL FIRE 허용: cur > LastBreakBand.팔가격
//
// ✅ 돌파 정의(확정):
// - 돌파는 crossing 이벤트가 아니라 "상태(state)"
// - SELL 돌파: 현재가 > startBand.팔가격
// - BUY  돌파: 현재가 < startBand.살가격
//
// ✅ 매매 실행 조건(확정):
// - 반드시 돌파(state) + 꺾임(turn)이 모두 충족되어야 FIRE
//
// ✅ 중요 버그 수정(필수):
// - 세션 시작 시 _segMin/_segMax는 반드시 cur로 초기화한다.
//   (distinct tickList가 비어있거나 중복틱이어도 extrema가 0으로 오염되면 안 됨)
//
// ✅ 스레드 안전(필수):
// - ProcessTickAsync가 이벤트에서 중첩 호출될 수 있으므로 직렬화(SemaphoreSlim)
//
// ✅ 이번 수정(필수):
// - FIRE 시 밴드별로 0300을 N번 부르지 않는다.
// - 0300에 "배치 실행"을 1번만 호출하여, 0300 내부에서 순차 전송하도록 한다.
//
// ✅ 이번 수정(철학 반영 핵심):
// - 세션 진행 중, 가격이 "LastBreakBand 기준"으로 다시 밴드 범위(IN-BAND)로 복귀하면
//   => 그 돌파 시도를 접고(세션 종료), 상태를 즉시 초기화한다.
//
// ✅ (이번 작업 핵심):
// - "그림(UI 갭)"은 0270이 히스토리로 계산하지 않는다.
// - 0250이 세션 segMin/segMax/gap(=judgeGap)을 계산하여 0270에 전달한다.
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
    public sealed class _0250_Tick_Process
    {
        // ================================
        // ✅ UI Sink (0270이 구현)
        // - 0250이 세션 기준 값을 만들어서 전달
        // ================================
        public interface ISessionUiSink
        {
            void ShowInBand(long currentPrice);
            void ShowSession(SessionUiState s);
            void ResetAll(long currentPrice);
        }

        public sealed class SessionUiState
        {
            public string Dir;          // "BUY" / "SELL"
            public int StartBandK;      // 돌파 시작 밴드K
            public int LastBreakBand;   // 세션 extrema 기준으로 계산된 lastBreakBand
            public long CurrentPrice;   // cur
            public long SegMin;         // 세션 최저
            public long SegMax;         // 세션 최고
            public int KK;              // 꺾임
            public long Gap;            // 세션 기준 gap (BUY: cur-segMin, SELL: segMax-cur)
            public bool IsTurn;         // 꺾임 충족 여부
            public bool FireAllowed;    // LastBreakBand 기준 OUT 유지 여부
        }
        private DataTable _bands;
        private readonly Login _login;
        private readonly ISessionUiSink _ui; // ✅ 0270 주입(권장)

        // 동시 호출 방지(필수)
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        // distinct 가격만 누적 (중복이어도 판정은 진행)
        private readonly List<long> _tickList = new List<long>();
        private readonly HashSet<long> _tickSet = new HashSet<long>();

        private enum 돌파방향 { 없음, 매도, 매수 }
        private 돌파방향 _돌파방향 = 돌파방향.없음;

        // 돌파 기준 정보(세션)
        private int _돌파밴드K = 0;          // 세션의 시작밴드(돌파가 처음 발생한 band)
        private int _startBandAtBreak = 0;   // 실행 범위 계산용(세션 시작밴드)
        private int _breakIndex = 0;         // (현재 로직에서는 참고용/확장용)

        // 세션 extrema (세션 시작 이후 누적)
        private long _segMax = 0;
        private long _segMin = 0;
        private DateTime _lastSessionKeepLogAtKst = DateTime.MinValue;
        private string _lastSessionKeepSummary = "";

        // 방향 표시용
        private long _prevPrice = 0;
        private bool _hasPrev = false;

        private const string SIDE_SELL = "SELL";
        private const string SIDE_BUY = "BUY";

        // ✅ [수정] 동일 breakout zone 재발사 방지 필드
        // 마지막으로 FIRE된 방향과 밴드K를 기억해서
        // 가격이 아직 동일 breakout zone 안에 있는 동안 재발사를 금지한다.
        private int _lastFiredBandK = 0;          // 마지막 FIRE 시점의 _돌파밴드K
        private 돌파방향 _lastFiredDir = 돌파방향.없음; // 마지막 FIRE 방향

        // ✅ 기존 생성자 유지
        public _0250_Tick_Process(Login login) : this(login, null) { }

        // ✅ 권장: UI(0270)를 같이 주입
        public _0250_Tick_Process(Login login, ISessionUiSink uiSink)
        {
            _login = login ?? throw new ArgumentNullException(nameof(login));
            _ui = uiSink; // null 허용
        }

        public async Task ProcessTickAsync(double price)
        {
            // Console.WriteLine("[0250 ENTRY] price=" + price + " time=" + DateTime.Now.ToString("HH:mm:ss.fff"));

            var sb = GetBandByNo(GetStartBandFromBandList());
            // if (sb != null)
            //    Console.WriteLine("[0250 BAND] startBand=" + sb.Band + " 팔가격=" + sb.팔가격 + " 살가격=" + sb.살가격);
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                long cur = (long)Math.Round(price);

                // DB 기록(옵션)
                try { _login.RecordTickIfEnabled(cur); } catch { }

                int kk = Login.꺽임변수;

                // ─────────────────────────────────────────────
                // 1) distinct tick 누적 (중복이어도 판정은 진행)
                // ─────────────────────────────────────────────
                bool isDuplicate = !_tickSet.Add(cur);
                if (!isDuplicate)
                    _tickList.Add(cur);

                // ─────────────────────────────────────────────
                // 2) startBand 계산
                // ─────────────────────────────────────────────
                int startBandNow = GetStartBandFromBandList();
                if (startBandNow <= 0)
                {
                    _ui?.ResetAll(cur);
                    return;
                }

                var startBand = GetBandByNo(startBandNow);
                if (startBand == null)
                {
                    _ui?.ResetAll(cur);
                    return;
                }

                // ✅ [수정] startBand가 변경됐으면(체결 후 밴드 이동)
                // lastFired 기록을 초기화하여 새 밴드에서의 돌파를 허용한다.
                // lastFiredBandK != startBandNow 이면 다른 밴드로 이동한 것이므로 리셋.
                if (_lastFiredBandK > 0 && _lastFiredBandK != startBandNow)
                {
                    Debug.WriteLine($"[0250][BAND MOVED] startBand {_lastFiredBandK} -> {startBandNow} -> lastFired cleared");
                    _lastFiredDir = 돌파방향.없음;
                    _lastFiredBandK = 0;
                }

                // ✅ 돌파 전에도 그림은 "IN-BAND 리셋/현재가" 표시(원하면 주석 처리 가능)
                bool inBandNow = (cur <= Math.Max(startBand.팔가격, startBand.살가격) &&
                                  cur >= Math.Min(startBand.팔가격, startBand.살가격));
                if (_돌파방향 == 돌파방향.없음 && inBandNow)
                {
                    _ui?.ShowInBand(cur);
                }

                // ─────────────────────────────────────────────
                // 3) 돌파(state) 판정 (세션 시작/전환)
                // ─────────────────────────────────────────────
                bool isSellBreak = cur > startBand.팔가격;
                bool isBuyBreak = cur < startBand.살가격;

                if (_돌파방향 == 돌파방향.없음)
                {
                    if (isSellBreak)
                    {
                        // ✅ [수정] 동일 breakout zone 재발사 방지
                        // 마지막 FIRE가 매도이고 동일 밴드K에서 아직 OUT 상태이면
                        // 새 SELL 세션 생성을 금지한다.
                        if (_lastFiredDir == 돌파방향.매도 && _lastFiredBandK == startBandNow)
                        {
                            string allowLog =
                                $"[0250][SELL][OUT_REENTER_ALLOWED] startBand={startBandNow} cur={cur} highBand={startBand.팔가격}";
                            Console.WriteLine(allowLog);
                            Debug.WriteLine(allowLog);

                            string skipLog =
                                $"[0250][SELL][REFIRE_BLOCK_SKIPPED] reason=session_missing_out_state startBand={startBandNow}";
                            Console.WriteLine(skipLog);
                            Debug.WriteLine(skipLog);
                        }
                        StartBreakSession(돌파방향.매도, startBandNow, cur);
                    }
                    else if (isBuyBreak)
                    {
                        // BUY는 crossing 이벤트가 아니라 OUT 상태이면 같은 밴드도 새 세션을 허용한다.
                        if (_lastFiredDir == 돌파방향.매수 && _lastFiredBandK == startBandNow)
                        {
                            Debug.WriteLine(
                                $"[0250][BUY_REFIRE_ALLOW] band={startBandNow} price={cur} low={startBand.살가격} reason=OUT_STATE");
                        }
                        StartBreakSession(돌파방향.매수, startBandNow, cur);
                    }
                    else
                    {
                        // ✅ [수정] 가격이 IN-BAND로 돌아왔으면 lastFired 기록 초기화
                        // (다음 번 같은 방향 돌파는 새로운 breakout으로 허용)
                        if (_lastFiredDir != 돌파방향.없음)
                        {
                            Debug.WriteLine($"[0250][REFIRE RESET] IN-BAND cur={cur} -> lastFired cleared");
                            _lastFiredDir = 돌파방향.없음;
                            _lastFiredBandK = 0;
                        }
                        // 돌파 전: 로그 출력 안 함(확정)
                        return;
                    }
                }
                else
                {
                    // 세션 진행 중: 반대 방향 OUT이면 세션을 새로 시작(방향 전환)
                    if (_돌파방향 == 돌파방향.매수 && isSellBreak)
                    {
                        // 반대 방향 전환이므로 lastFired 리셋 후 새 세션 시작
                        _lastFiredDir = 돌파방향.없음;
                        _lastFiredBandK = 0;
                        StartBreakSession(돌파방향.매도, startBandNow, cur);
                    }
                    else if (_돌파방향 == 돌파방향.매도 && isBuyBreak)
                    {
                        // 반대 방향 전환이므로 lastFired 리셋 후 새 세션 시작
                        _lastFiredDir = 돌파방향.없음;
                        _lastFiredBandK = 0;
                        StartBreakSession(돌파방향.매수, startBandNow, cur);
                    }
                    // 같은 방향이면 세션 유지(별도 세팅 금지)
                }

                // ─────────────────────────────────────────────
                // 4) segMax / segMin 누적 갱신 (세션 유지 동안)
                // ─────────────────────────────────────────────
                long oldSegMax = _segMax;
                long oldSegMin = _segMin;
                if (cur > _segMax) _segMax = cur;
                if (cur < _segMin) _segMin = cur;

                long segMax = _segMax;
                long segMin = _segMin;

                if (_돌파방향 == 돌파방향.매도 && segMax != oldSegMax)
                {
                    WriteSessionLog($"[0250][SELL][SESSION_MAX] old={oldSegMax} new={segMax} startBand={_돌파밴드K} cur={cur}");
                }
                else if (_돌파방향 == 돌파방향.매수 && segMin != oldSegMin)
                {
                    WriteSessionLog($"[0250][BUY][SESSION_MIN] old={oldSegMin} new={segMin} startBand={_돌파밴드K} cur={cur}");
                }
                else
                {
                    WriteSessionKeepSummary(cur, segMin, segMax);
                }

                // ─────────────────────────────────────────────
                // 4.5) (철학 반영) "LastBreakBand 기준" IN-BAND 복귀 시 세션 종료
                // ─────────────────────────────────────────────
                int lastBreakBandForReturn = ComputeLastBreakBandByExtrema(_돌파방향, _돌파밴드K, segMin, segMax);
                var lastBandForReturn = GetBandByNo(lastBreakBandForReturn);
                if (lastBandForReturn != null)
                {
                    bool returnedToInBandByLast =
                        (_돌파방향 == 돌파방향.매도 && cur <= lastBandForReturn.팔가격) ||
                        (_돌파방향 == 돌파방향.매수 && cur >= lastBandForReturn.살가격);

                    if (returnedToInBandByLast)
                    {
                        // ✅ 세션 포기: UI도 세션 리셋
                        ResetAfterAbort($"INBAND_RETURN lastBand={lastBreakBandForReturn} cur={cur}");
                        _ui?.ShowInBand(cur);
                        return;
                    }
                }

                // ─────────────────────────────────────────────
                // 5) 꺾임 판정 (세션에서만)
                // ─────────────────────────────────────────────
                bool isTurn =
                    (_돌파방향 == 돌파방향.매도 && cur <= segMax - kk) ||
                    (_돌파방향 == 돌파방향.매수 && cur >= segMin + kk);

                // ─────────────────────────────────────────────
                // 6) UI "그림" 갱신: ✅ 세션 기준으로만 표시
                // ─────────────────────────────────────────────
                int lastBreakBandForUi = ComputeLastBreakBandByExtrema(_돌파방향, _돌파밴드K, segMin, segMax);
                var lastBandUi = GetBandByNo(lastBreakBandForUi);

                bool fireAllowedNow = false;
                if (lastBandUi != null)
                {
                    fireAllowedNow =
                        (_돌파방향 == 돌파방향.매수 && cur < lastBandUi.살가격) ||
                        (_돌파방향 == 돌파방향.매도 && cur > lastBandUi.팔가격);
                }

                long gapSession =
                    (_돌파방향 == 돌파방향.매수) ? (cur - segMin) : (segMax - cur);
                if (gapSession < 0) gapSession = -gapSession;

                if (_ui != null && _돌파방향 != 돌파방향.없음)
                {
                    var s = new SessionUiState
                    {
                        Dir = (_돌파방향 == 돌파방향.매수) ? SIDE_BUY : SIDE_SELL,
                        StartBandK = _돌파밴드K,
                        LastBreakBand = lastBreakBandForUi,
                        CurrentPrice = cur,
                        SegMin = segMin,
                        SegMax = segMax,
                        KK = kk,
                        Gap = gapSession,
                        IsTurn = isTurn,
                        FireAllowed = fireAllowedNow
                    };
                    _ui.ShowSession(s);
                }

                // ─────────────────────────────────────────────
                // 7) FIRE (turn + FIRE 허용 조건(LastBreakBand 기준))
                // ─────────────────────────────────────────────
                if (!isTurn)
                    return;

                int lastBreakBand = ComputeLastBreakBandByExtrema(_돌파방향, _돌파밴드K, segMin, segMax);

                var lastBandObj = GetBandByNo(lastBreakBand);
                if (lastBandObj == null)
                    return;

                bool fireAllowed =
                    (_돌파방향 == 돌파방향.매수 && cur < lastBandObj.살가격) ||
                    (_돌파방향 == 돌파방향.매도 && cur > lastBandObj.팔가격);

                if (!fireAllowed)
                    return;

                // ✅ 핵심: 여기서 밴드별로 0300을 N번 호출하지 않는다.
                // ✅ 0300의 "배치 실행"을 1번만 호출한다.
                if (_돌파방향 == 돌파방향.매수)
                {
                    // ✅ [수정] FIRE 직전에 lastFired 기록 (ResetAfterTrade 전에!)
                    // 이렇게 해야 Reset 후에도 재발사 방지 기준값이 남는다.
                    _lastFiredDir = 돌파방향.매수;
                    _lastFiredBandK = _돌파밴드K;
                    Debug.WriteLine($"[0250][FIRE BUY] lastFiredDir=매수 lastFiredBandK={_lastFiredBandK} cur={cur}");

                    // BUY 배치: startBandAtBreak ~ lastBreakBand (오름차순)
                    밴드매칭.실행배치(
                        side: SIDE_BUY,
                        firePrice: cur,
                        fromBand: _startBandAtBreak,
                        toBand: lastBreakBand,
                        startBandNow: startBandNow
                    );

                    ResetAfterTrade("BUY");
                    _ui?.ShowInBand(cur); // 체결 후 세션 종료(그림 리셋)
                }
                else if (_돌파방향 == 돌파방향.매도)
                {
                    // ⚠️ 기존 endBand 계산은 정책과 불일치 소지가 있었음.
                    // ✅ 세션 기반 "lastBreakBand"를 기준으로 SELL도 대칭 처리 권장:
                    //    (from=startBandAtBreak, to=lastBreakBand)
                    //    0300(밴드매칭) 내부에서 SELL은 내림차순으로 처리하도록 되어 있어야 한다.
                    //
                    // ✅ [수정 2026-05-14] SELL toBand 하한 클램프
                    // SELL 범위는 반드시 startBandNow 이상(번호 기준 >=)이어야 한다.
                    // lastBreakBand < startBandNow 인 경우: 현재 startBand보다 낮은 밴드(다음 startBand 후보)까지
                    // 팔아버리는 것이므로 startBandNow로 클램프한다.
                    // 예) startBandNow=35, lastBreakBand=34 → toBand=35 (Queue=[35]만 생성)
                    int sellToBand = lastBreakBand;
                    if (sellToBand < startBandNow)
                    {
                        Debug.WriteLine(
                            $"[0250][SELL][CLAMP] lastBreakBand={lastBreakBand} < startBandNow={startBandNow}" +
                            $" -> toBand clamped to {startBandNow}");
                        sellToBand = startBandNow;
                    }

                    // ✅ [수정] FIRE 직전에 lastFired 기록 (ResetAfterTrade 전에!)
                    _lastFiredDir = 돌파방향.매도;
                    _lastFiredBandK = _돌파밴드K;
                    Debug.WriteLine($"[0250][FIRE SELL] lastFiredDir=매도 lastFiredBandK={_lastFiredBandK} cur={cur}");
                    Console.WriteLine(
                        $"[0250][SELL][FIRE] startBand={_돌파밴드K} cur={cur} tickMax={segMax} kk={kk}");

                    밴드매칭.실행배치(
                        side: SIDE_SELL,
                        firePrice: cur,
                        fromBand: _startBandAtBreak,
                        toBand: sellToBand,
                        startBandNow: startBandNow
                    );

                    ResetAfterTrade("SELL");
                    _ui?.ShowInBand(cur); // 체결 후 세션 종료(그림 리셋)
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[0250 ProcessTickAsync ERROR] " + ex);
            }
            finally
            {
                _gate.Release();
            }

            await Task.CompletedTask;
        }

        // ─────────────────────────────────────────────
        // 세션 시작(최초 돌파 시점 1회)
        // ─────────────────────────────────────────────
        private void StartBreakSession(돌파방향 dir, int startBandNow, long cur)
        {
            _돌파방향 = dir;
            _돌파밴드K = startBandNow;
            _startBandAtBreak = startBandNow;

            // 참고용(확장용): 세션 시작 시점 인덱스
            _breakIndex = (_tickList.Count > 0) ? (_tickList.Count - 1) : 0;

            // ✅ 핵심: extrema는 반드시 현재 cur로 초기화
            _segMin = cur;
            _segMax = cur;

            // 방향표시도 세션 시작에서는 prev 갱신
            _prevPrice = cur;
            _hasPrev = true;

            Debug.WriteLine($"[0250] StartBreakSession dir={dir} K={startBandNow} cur={cur}");
            if (dir == 돌파방향.매수)
                WriteSessionLog($"[0250][BUY][SESSION_START] startBand={startBandNow} cur={cur} segMin={_segMin}");
            else if (dir == 돌파방향.매도)
            {
                WriteSessionLog($"[0250][SELL][SESSION_START] startBand={startBandNow} cur={cur} segMax={_segMax}");
            }
        }

        private void WriteSessionKeepSummary(long cur, long segMin, long segMax)
        {
            if (_돌파방향 == 돌파방향.없음)
                return;

            DateTime now = KoreaTime.NowKst();
            if ((now - _lastSessionKeepLogAtKst).TotalSeconds < 5)
                return;

            string side = _돌파방향 == 돌파방향.매수 ? SIDE_BUY : SIDE_SELL;
            string summary = $"[0250][{side}][SESSION_KEEP] startBand={_돌파밴드K} cur={cur} segMin={segMin} segMax={segMax}";
            if (string.Equals(summary, _lastSessionKeepSummary, StringComparison.Ordinal))
                return;

            _lastSessionKeepLogAtKst = now;
            _lastSessionKeepSummary = summary;
            WriteSessionLog(summary);
        }

        private static void WriteSessionLog(string message)
        {
            Console.WriteLine(message);
            Debug.WriteLine(message);
        }

        // ─────────────────────────────────────────────
        // LastBreakBand 계산 (BUY/SELL 대칭)
        // ─────────────────────────────────────────────
        private int ComputeLastBreakBandByExtrema(돌파방향 dir, int startBandK, long segMin, long segMax)
        {
            if (startBandK <= 0) return startBandK;

            if (dir == 돌파방향.매수)
            {
                int last = startBandK;

                // startBandK부터 아래(번호 증가)로 스캔
                for (int b = startBandK; ; b++)
                {
                    var br = GetBandByNo(b);
                    if (br == null) break;

                    if (segMin < br.살가격)
                        last = b;
                    else
                        break;
                }

                return last;
            }
            else if (dir == 돌파방향.매도)
            {
                int last = startBandK;

                // startBandK부터 위(번호 감소)로 스캔
                for (int b = startBandK; b >= 1; b--)
                {
                    var br = GetBandByNo(b);
                    if (br == null) break;

                    if (segMax > br.팔가격)
                        last = b;
                    else
                        break;
                }

                return last;
            }

            return startBandK;
        }

        // ─────────────────────────────────────────────
        // Reset (정상 체결 후)
        // ─────────────────────────────────────────────
        private void ResetAfterTrade(string why)
        {
            _tickList.Clear();
            _tickSet.Clear();

            _돌파방향 = 돌파방향.없음;
            _돌파밴드K = 0;
            _startBandAtBreak = 0;
            _breakIndex = 0;

            _segMax = 0;
            _segMin = 0;

            _hasPrev = false;
            _prevPrice = 0;

            Debug.WriteLine($"[0250] ResetAfterTrade ({why})");
        }

        // ─────────────────────────────────────────────
        // Reset (세션 포기)
        // ─────────────────────────────────────────────
        private void ResetAfterAbort(string why)
        {
            _tickList.Clear();
            _tickSet.Clear();

            _돌파방향 = 돌파방향.없음;
            _돌파밴드K = 0;
            _startBandAtBreak = 0;
            _breakIndex = 0;

            _segMax = 0;
            _segMin = 0;

            _hasPrev = false;
            _prevPrice = 0;

            // ✅ [수정] 세션 포기(Abort) 시에는 lastFired도 초기화
            // (IN-BAND 복귀로 세션이 취소된 것이므로 다음 돌파는 새 breakout으로 허용)
            _lastFiredDir = 돌파방향.없음;
            _lastFiredBandK = 0;

            Debug.WriteLine($"[0250] ResetAfterAbort ({why}) -> lastFired cleared");
        }

        // ─────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────
        private int GetStartBandFromBandList()
        {
            var list = Login.BandList;
            if (list == null || list.Count == 0) return 0;

            int max = 0;
            foreach (var b in list)
            {
                if (b == null) continue;
                if (b.Qty > 0 && b.Band > max)
                    max = b.Band;
            }
            return max;
        }

        private BandRange GetBandByNo(int band)
        {
            return Login.BandList.FirstOrDefault(b => b != null && b.Band == band);
        }

        private string GetArrow(long cur)
        {
            if (!_hasPrev)
            {
                _prevPrice = cur;
                _hasPrev = true;
                return "=";
            }

            string a;
            if (cur > _prevPrice) a = "↑";
            else if (cur < _prevPrice) a = "↓";
            else a = "=";

            _prevPrice = cur;
            return a;
        }
        public void UpdateBands(DataTable bands)
        {
            try
            {
                _bands = bands;
                Console.WriteLine("[0250] Bands updated");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[0250][ERROR] " + ex.Message);
            }
        }
    }
}
// 2026-02-21 41837
