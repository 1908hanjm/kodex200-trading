// ============================================================
// 0320_10전슬라이딩BUY.cs
//
// 역할
// - 10전슬라이딩BUY 처리
// - 기존 체인 존재 여부 확인 후 진입한 BUY 처리
// - from_band = targetBand - 10 생성
// - from_qty 관리
// - 체인 메타데이터 생성
//
// SELL 없이 BUY만 수행한다.
// 체인의 연속성을 유지하기 위한 BUY이다.
// ============================================================
//
// 주의
//
// - SELL을 수행하지 않는다.
// - 일반 BUY와 혼합하지 않는다.
// - 10후슬라이딩BUY와 혼합하지 않는다.

using System.Threading.Tasks;

namespace Exercise_1
{
    public sealed class ChainExtendBuyContext
    {
        public int DecisionBandK;
        public int TargetBand;
        public int FromBand;
    }

    public static class _0320_10전슬라이딩BUY
    {
        private static readonly object Sync = new object();
        private static ChainExtendBuyContext _pending;

        public static async Task ExecuteAsync(
            매매실행 exec,
            int decisionBandK,
            int targetBand,
            int qty,
            long firePrice)
        {
            int fromBand = targetBand - 10;
            SetPending(decisionBandK, targetBand, fromBand);

            try
            {
                await exec.ExecuteAsync("매수", decisionBandK, qty, firePrice).ConfigureAwait(false);
            }
            finally
            {
                ClearPending(decisionBandK, targetBand);
            }
        }

        public static bool TryCaptureForOrder(int executeBand, out ChainExtendBuyContext context)
        {
            lock (Sync)
            {
                if (_pending != null &&
                    _pending.DecisionBandK == executeBand &&
                    _pending.TargetBand == executeBand + 1 &&
                    _pending.FromBand == _pending.TargetBand - 10)
                {
                    context = new ChainExtendBuyContext
                    {
                        DecisionBandK = _pending.DecisionBandK,
                        TargetBand = _pending.TargetBand,
                        FromBand = _pending.FromBand
                    };
                    return true;
                }
            }

            context = null;
            return false;
        }

        private static void SetPending(int decisionBandK, int targetBand, int fromBand)
        {
            lock (Sync)
            {
                _pending = new ChainExtendBuyContext
                {
                    DecisionBandK = decisionBandK,
                    TargetBand = targetBand,
                    FromBand = fromBand
                };
            }
        }

        private static void ClearPending(int decisionBandK, int targetBand)
        {
            lock (Sync)
            {
                if (_pending != null &&
                    _pending.DecisionBandK == decisionBandK &&
                    _pending.TargetBand == targetBand)
                {
                    _pending = null;
                }
            }
        }
    }
}
