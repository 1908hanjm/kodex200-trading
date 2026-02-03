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
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Exercise_1
{
    public sealed class _0250_Tick_Process
    {
        private readonly Login _login;

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

        // 방향 표시용
        private long _prevPrice = 0;
        private bool _hasPrev = false;

        private const string SIDE_SELL = "SELL";
        private const string SIDE_BUY = "BUY";

        public _0250_Tick_Process(Login login)
        {
            _login = login ?? throw new ArgumentNullException(nameof(login));
        }

        public async Task ProcessTickAsync(double price)
        {
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
                    return;

                var startBand = GetBandByNo(startBandNow);
                if (startBand == null)
                    return;

                // ─────────────────────────────────────────────
                // 3) 돌파(state) 판정 (세션 시작/전환)
                // ─────────────────────────────────────────────
                bool isSellBreak = cur > startBand.팔가격;
                bool isBuyBreak = cur < startBand.살가격;

                if (_돌파방향 == 돌파방향.없음)
                {
                    if (isSellBreak)
                    {
                        StartBreakSession(돌파방향.매도, startBandNow, cur);
                    }
                    else if (isBuyBreak)
                    {
                        StartBreakSession(돌파방향.매수, startBandNow, cur);
                    }
                    else
                    {
                        // 돌파 전: 로그 출력 안 함(확정)
                        return;
                    }
                }
                else
                {
                    // 세션 진행 중: 반대 방향 OUT이면 세션을 새로 시작(방향 전환)
                    if (_돌파방향 == 돌파방향.매수 && isSellBreak)
                    {
                        StartBreakSession(돌파방향.매도, startBandNow, cur);
                    }
                    else if (_돌파방향 == 돌파방향.매도 && isBuyBreak)
                    {
                        StartBreakSession(돌파방향.매수, startBandNow, cur);
                    }
                    // 같은 방향이면 세션 유지(별도 세팅 금지)
                }

                // ─────────────────────────────────────────────
                // 4) segMax / segMin 누적 갱신 (세션 유지 동안)
                // ─────────────────────────────────────────────
                if (cur > _segMax) _segMax = cur;
                if (cur < _segMin) _segMin = cur;

                long segMax = _segMax;
                long segMin = _segMin;

                // ─────────────────────────────────────────────
                // 5) 꺾임 판정 (세션에서만)
                // ─────────────────────────────────────────────
                bool isTurn =
                    (_돌파방향 == 돌파방향.매도 && cur <= segMax - kk) ||
                    (_돌파방향 == 돌파방향.매수 && cur >= segMin + kk);

                // ─────────────────────────────────────────────
                // 6) TRACE (돌파 이후만 출력)
                // ─────────────────────────────────────────────
                var kBand = Login.BandList.FirstOrDefault(b => b != null && b.Band == _돌파밴드K);
                long edgeMax = (kBand != null) ? kBand.팔가격 : 0;
                long edgeMin = (kBand != null) ? kBand.살가격 : 0;

                long tickMax = segMax;
                long tickMin = segMin;

                long judgeGap = (_돌파방향 == 돌파방향.매수)
                    ? (cur - tickMin)
                    : (tickMax - cur);

                long viewGap = Math.Abs(judgeGap);

                string 방향표시 = GetArrow(cur);

                if (_돌파방향 == 돌파방향.매도)
                {
                    //Console.WriteLine(
                    //    $"Band={_돌파밴드K} Max={edgeMax:#,0} 방향={방향표시} 현재가={cur:#,0} 틱Max={tickMax:#,0} 꺽임={kk} 갭={viewGap:#,0}"
                    //);
                }
                else // 매수
                {
                    //Console.WriteLine(
                    //    $"Band={_돌파밴드K} Min={edgeMin:#,0} 방향={방향표시} 현재가={cur:#,0} 틱Min={tickMin:#,0} 꺽임={kk} 갭={viewGap:#,0}"
                    //);
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

                // ✅ 실행 밴드 범위: StartBand(=세션 시작밴드) ~ LastBreakBand
                if (_돌파방향 == 돌파방향.매수)
                {
                    for (int b = _startBandAtBreak; b <= lastBreakBand; b++)
                        밴드매칭.실행(SIDE_BUY, cur, b, startBandNow);

                    ResetAfterTrade("BUY");
                }
                else if (_돌파방향 == 돌파방향.매도)
                {
                    // 매도는 현재 구현을 보수적으로 유지
                    int 현재밴드 = FindBandByPriceInMemory(cur);
                    int endBand = Math.Max(현재밴드 + 1, 1);

                    for (int b = _startBandAtBreak; b >= endBand; b--)
                        밴드매칭.실행(SIDE_SELL, cur, b, startBandNow);

                    ResetAfterTrade("SELL");
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
        }

        // ─────────────────────────────────────────────
        // LastBreakBand 계산 (BUY 중심)
        // ─────────────────────────────────────────────
        private int ComputeLastBreakBandByExtrema(돌파방향 dir, int startBandK, long segMin, long segMax)
        {
            if (dir == 돌파방향.매수)
            {
                int last = startBandK;

                // startBandK부터 아래(번호 증가)로 스캔
                for (int b = startBandK; ; b++)
                {
                    var br = GetBandByNo(b);
                    if (br == null) break;

                    // segMin이 해당 밴드의 살가격 아래로 내려간 상태면 그 밴드는 "돌파된 상태"로 본다
                    if (segMin < br.살가격)
                        last = b;
                    else
                        break;
                }

                return last;
            }

            // 매도 방향은 별도 정의 필요(현재는 보수적으로 startBandK)
            return startBandK;
        }

        // ─────────────────────────────────────────────
        // Reset
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

        private int FindBandByPriceInMemory(long price)
        {
            // price가 밴드 구간 "안"에 있을 수도/없을 수도 있음
            var b = Login.BandList
                .Where(x => x != null && price >= x.살가격 && price <= x.팔가격)
                .OrderBy(x => x.Band)
                .LastOrDefault();

            return b?.Band ?? -1;
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
    }
}

// 2026-01-21-00-00-00
