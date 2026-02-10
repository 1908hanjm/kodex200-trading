// 0400_매매실행.cs  (복붙용 / C# 7.3)  [신형 고정]
// ------------------------------------------------------------
// 목적:
// - 주문 실행 단일 진입점
// - ✅ 0550(매매전송후대기)로 1건 제한/pending 처리
// - 전송은 매매_Xing.SendOrderLive(...) 호출
// - pending 해제는 체결(SC1)에서 0650이 0550.EndOnFilled(...) 호출로 처리
// ------------------------------------------------------------

using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Exercise_1
{
    public sealed class 매매실행
    {
        private readonly 매매_Xing _xing;
        private readonly Func<string> _getShcode;

        public event Action<string> Log;

        public 매매실행(매매_Xing xing, Func<string> getShcode)
        {
            _xing = xing ?? throw new ArgumentNullException(nameof(xing));
            _getShcode = getShcode ?? throw new ArgumentNullException(nameof(getShcode));
        }

        public async Task ExecuteAsync(string side, int band, int qty, double price)
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

            // ✅ 0550: 주문 전 pending / 중복 주문 차단
            string reason;
            if (Login.TradeWait == null)
                throw new InvalidOperationException("Login.TradeWait(0550)가 null 입니다.");

            if (!Login.TradeWait.TryBegin(side, band, (long)qty, (long)price, out reason))
            {
                WriteLog($"[주문차단(0550)] side={side}, band={band}, reason={reason}");
                return;
            }

            WriteLog($"[주문전송] side={side}, band={band}, qty={qty}, price={price}, shcode={shcode}");

            try
            {
                await _xing.SendOrderLive(side, shcode, price, qty, band);
                WriteLog($"[주문요청 완료] side={side}, band={band}");
                // ⚠️ 여기서 pending 해제 금지: 체결(SC1)에서 0650이 EndOnFilled로 해제
            }
            catch (Exception ex)
            {
                // ✅ 예외 발생 시 pending 해제 + fail cooldown
                try { Login.TradeWait.EndOnFailed(side, band, ex.Message); } catch { }
                WriteLog($"[주문전송 오류] side={side}, band={band}, ex={ex}");
                throw;
            }
        }

        // 기존 호환
        public Task 실행(string side, double price, int qty, int band)
            => ExecuteAsync(side, band, qty, price);

        public Task ExecuteSellAsync(int band, int qty, double price)
            => ExecuteAsync("매도", band, qty, price);

        public Task ExecuteBuyAsync(int band, int qty, double price)
            => ExecuteAsync("매수", band, qty, price);

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

//2026-01-18-00-00-00
