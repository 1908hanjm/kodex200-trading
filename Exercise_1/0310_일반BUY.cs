// ============================================================
// 0310_일반BUY.cs
//
// 역할
// - 일반 BUY 처리
// - 마름모 예산 계산
// - 주문가능현금 계산
// - BUY 수량 계산
// - BUY 주문 전송
//
// 체인(from_band/from_qty)은 생성하지 않는다.
// ============================================================
//
// 주의
//
// - 10전슬라이딩을 포함하지 않는다.
// - 10후슬라이딩을 포함하지 않는다.
// - 체인 메타데이터를 생성하지 않는다.

using System.Threading.Tasks;

namespace Exercise_1
{
    public static class _0310_일반BUY
    {
        public static Task ExecuteAsync(
            매매실행 exec,
            int decisionBandK,
            int qty,
            long firePrice)
        {
            return exec.ExecuteAsync("매수", decisionBandK, qty, firePrice);
        }
    }
}
