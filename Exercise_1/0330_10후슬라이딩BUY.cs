// ============================================================
// 0330_10후슬라이딩BUY.cs
//
// 역할
// - 10후슬라이딩BUY 처리
// - 기존 슬라이딩 처리
// - SELL 수행
// - BUY 복원 수행
// - 기존 슬라이딩 메타데이터 처리
//
// SELL 후 BUY가 이루어지는 기존 슬라이딩이다.
// ============================================================
//
// 주의
//
// - 반드시 SELL 후 BUY 순서를 유지한다.
// - 일반 BUY를 포함하지 않는다.
// - 10전슬라이딩BUY를 포함하지 않는다.

using System.Threading.Tasks;

namespace Exercise_1
{
    public static class _0330_10후슬라이딩BUY
    {
        public static Task<SlideResult> ExecuteAsync(
            System.Func<Task<SlideResult>> existingSlidingHandler)
        {
            System.Console.WriteLine("[0330][10후슬라이딩BUY] SELL -> BUY -> RESTORE");
            return existingSlidingHandler();
        }
    }
}
