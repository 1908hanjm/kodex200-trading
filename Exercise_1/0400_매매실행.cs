// 0400_매매실행.cs  (복붙용 / C# 7.3)  [호환 포함: 구형(0530직결) + 신형(0500경유)]
// ------------------------------------------------------------
// 목적:
// - 주문 실행 단일 진입점
// - ✅ 0550_부분체결확인(TradeWait)로 1건 제한/pending 처리
//
// ✅ 호환 정책(컴파일 0 우선):
// - 과거 코드가 new 매매실행(매매_Xing ...) 로 생성하던 흐름을 깨지 않기 위해
//   (1) 0530 직결 생성자도 유지
//   (2) ExecuteAsync(..., long price) 오버로드도 유지
//
// ✅ 신형 목표:
// - 신형은 0500(_0500_매매전송_Xing) 경유: BUY 현금검사 + 0530 호출
// - price 타입은 단계적으로 int로 통일
// ------------------------------------------------------------

using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Exercise_1
{
    public sealed class 매매실행
    {
        // 신형(0500 경유)
        private readonly _0500_매매전송_Xing _tx0500;

        // 구형(0530 직결)
        private readonly 매매_Xing _trader0530;

        private readonly Func<string> _getShcode;

        public event Action<string> Log;

        // ✅ 신형 생성자: 0500을 받는다
        public 매매실행(_0500_매매전송_Xing tx0500, Func<string> getShcode)
        {
            _tx0500 = tx0500 ?? throw new ArgumentNullException(nameof(tx0500));
            _trader0530 = null;
            _getShcode = getShcode ?? throw new ArgumentNullException(nameof(getShcode));
        }

        // ✅ 구형 호환 생성자: 0530을 받는다 (기존 코드 컴파일 유지용)
        public 매매실행(매매_Xing trader0530, Func<string> getShcode)
        {
            _trader0530 = trader0530 ?? throw new ArgumentNullException(nameof(trader0530));
            _tx0500 = null;
            _getShcode = getShcode ?? throw new ArgumentNullException(nameof(getShcode));
        }

        // ------------------------------------------------------------
        // ✅ 신형 기준 Execute (price=int)
        // ------------------------------------------------------------
        public Task ExecuteAsync(string side, int band, int qty, int price)
        {
            return ExecuteCoreAsync(side, band, qty, price);
        }

        // ------------------------------------------------------------
        // ✅ 구형 호환 Execute (price=long)  -> int로 안전 변환 후 실행
        //   (지금 뜬 "4 인수: long -> int 변환" 에러를 여기서 제거)
        // ------------------------------------------------------------
        public Task ExecuteAsync(string side, int band, int qty, long price)
        {
            int p = SafeToIntPrice(price);
            return ExecuteCoreAsync(side, band, qty, p);
        }

        // 기존 호환 시그니처들 (프로젝트 내 호출부가 섞여있을 수 있어서 제공)
        public Task 실행(string side, int price, int qty, int band)
            => ExecuteAsync(side, band, qty, price);

        public Task 실행(string side, long price, int qty, int band)
            => ExecuteAsync(side, band, qty, price);

        public Task ExecuteSellAsync(int band, int qty, int price)
            => ExecuteAsync("매도", band, qty, price);

        public Task ExecuteSellAsync(int band, int qty, long price)
            => ExecuteAsync("매도", band, qty, price);

        public Task ExecuteBuyAsync(int band, int qty, int price)
            => ExecuteAsync("매수", band, qty, price);

        public Task ExecuteBuyAsync(int band, int qty, long price)
            => ExecuteAsync("매수", band, qty, price);

        // ------------------------------------------------------------

        private async Task ExecuteCoreAsync(string side, int band, int qty, int price)
        {
            if (string.IsNullOrWhiteSpace(side))
                throw new ArgumentNullException(nameof(side));

            side = NormalizeSide(side);

            if (side != "매수" && side != "매도")
                throw new ArgumentException($"side는 '매수' 또는 '매도'만 허용됩니다. side={side}", nameof(side));

            if (band <= 0) throw new ArgumentOutOfRangeException(nameof(band));
            if (qty <= 0) throw new ArgumentOutOfRangeException(nameof(qty));
            if (price <= 0) throw new ArgumentOutOfRangeException(nameof(price));

            string shcode = _getShcode()?.Trim();
            if (string.IsNullOrEmpty(shcode))
                throw new InvalidOperationException("shcode(종목코드)가 비어있습니다.");

            if (Login.TradeWait == null)
                throw new InvalidOperationException("Login.TradeWait(0550)가 null 입니다.");

            // ✅ 0550: 주문 전 pending / 중복 주문 차단
            string reason;
            if (!Login.TradeWait.TryBegin(side, band, (long)qty, (long)price, out reason))
            {
                WriteLog($"[주문차단(0550)] side={side}, band={band}, reason={reason}");
                return;
            }

            WriteLog($"[주문전송] side={side}, band={band}, qty={qty}, price={price}, shcode={shcode}");

            try
            {
                // ✅ 신형: 0500 경유 (BUY 현금검사 포함)
                if (_tx0500 != null)
                {
                    await _tx0500.SendOrderAsync(side, shcode, price, qty, band).ConfigureAwait(false);
                    WriteLog($"[주문요청 완료][0500] side={side}, band={band}");
                }
                // ✅ 구형: 0530 직결
                else if (_trader0530 != null)
                {
                    // 주의: 0530 시그니처가 (side, shcode, price, qty, band) 형태라는 가정
                    await _trader0530.SendOrderLive(side, shcode, price, qty, band).ConfigureAwait(false);
                    WriteLog($"[주문요청 완료][0530] side={side}, band={band}");
                }
                else
                {
                    throw new InvalidOperationException("매매실행이 0500/0530 어느 쪽도 주입되지 않았습니다.");
                }

                // ⚠️ pending 해제 금지: 체결(SC1)에서 0650이 EndOnFilled로 해제
            }
            catch (Exception ex)
            {
                // ✅ 예외 발생 시 pending 해제 + fail cooldown
                try { Login.TradeWait.EndOnFailed(side, band, ex.Message); } catch { }
                WriteLog($"[주문전송 오류] side={side}, band={band}, ex={ex}");
                throw;
            }
        }

        private static int SafeToIntPrice(long price)
        {
            if (price <= 0) throw new ArgumentOutOfRangeException(nameof(price));
            if (price > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(price), "price가 int 범위를 초과했습니다.");
            return (int)price;
        }

        private static string NormalizeSide(string side)
        {
            if (string.IsNullOrWhiteSpace(side)) return side;
            side = side.Trim();

            if (string.Equals(side, "BUY", StringComparison.OrdinalIgnoreCase)) return "매수";
            if (string.Equals(side, "SELL", StringComparison.OrdinalIgnoreCase)) return "매도";

            return side;
        }

        private void WriteLog(string msg)
        {
            try
            {
                Debug.WriteLine(msg);
                Log?.Invoke(msg);
            }
            catch { }
        }
    }
}

// 2026-02-15 90274
