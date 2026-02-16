// 0500_매매전송_Xing.cs  (복붙용 / C# 7.3)
// ------------------------------------------------------------
// 역할:
// - 실행 레이어(통제): 주문 전송 직전 안전장치 적용
// - ✅ BUY 직전: 1000(CSPAQ12200) 즉시조회(RequestAsync)로 현금부족 차단
// - 실제 주문 전송은 0530의 매매_Xing.SendOrderLive(...)에 위임
// ------------------------------------------------------------

using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Exercise_1
{
    public sealed class _0500_매매전송_Xing : IDisposable
    {
        private readonly 매매_Xing _trader;
        private readonly Func<string> _getAcntNo;
        private readonly Func<string> _getPwd4;
        private readonly Func<_1000_현금주문가능금액> _getCash1000;

        private bool _disposed;

        public event Action<string> Log;

        public _0500_매매전송_Xing(
            매매_Xing trader,
            Func<string> getAcntNo,
            Func<string> getPwd4,
            Func<_1000_현금주문가능금액> getCash1000)
        {
            _trader = trader ?? throw new ArgumentNullException(nameof(trader));
            _getAcntNo = getAcntNo ?? throw new ArgumentNullException(nameof(getAcntNo));
            _getPwd4 = getPwd4 ?? throw new ArgumentNullException(nameof(getPwd4));
            _getCash1000 = getCash1000 ?? throw new ArgumentNullException(nameof(getCash1000));
        }

        public async Task<bool> SendOrderAsync(string sideKor, string shcode, int price, int qty, int band)
        {
            if (_disposed) return false;

            sideKor = NormalizeSideKorOnly(sideKor);

            if (qty <= 0) throw new ArgumentOutOfRangeException(nameof(qty));
            if (price <= 0) throw new ArgumentOutOfRangeException(nameof(price));
            if (band <= 0) throw new ArgumentOutOfRangeException(nameof(band));

            // ==================================================
            // [CASH] BUY 직전 즉시조회(1000) + 부족 시 차단
            // ==================================================
            if (sideKor == "매수")
            {
                var cash1000 = _getCash1000();
                if (cash1000 == null)
                {
                    Write("[0500][CASH][BLOCK] cash1000 is null -> BUY STOP");
                    return false;
                }

                string acnt = SafeTrim(_getAcntNo());
                string pwd4 = SafeTrim(_getPwd4());

                if (string.IsNullOrEmpty(acnt) || string.IsNullOrEmpty(pwd4))
                {
                    Write($"[0500][CASH][BLOCK] acnt/pwd empty acntLen={acnt.Length} pwdLen={pwd4.Length} -> BUY STOP");
                    return false;
                }

                long requiredCash = (long)qty * (long)price;

                try
                {
                    Write($"[0500][CASH][CALL] band={band} qty={qty} price={price} required={requiredCash}");

                    long orderable = await cash1000
                        .RequestAsync(acnt, pwd4, timeoutMs: 1500, showMessageBox: false)
                        .ConfigureAwait(false);

                    Write($"[0500][CASH][RET] orderable={orderable} required={requiredCash} band={band}");

                    if (orderable < requiredCash)
                    {
                        Write($"[0500][CASH][BLOCK] orderable={orderable} < required={requiredCash} -> BUY STOP (band={band})");
                        return false;
                    }

                    Write($"[0500][CASH][OK] orderable={orderable} >= required={requiredCash} band={band}");
                }
                catch (Exception ex)
                {
                    Write("[0500][CASH][EX] " + ex.Message);
                    return false;
                }
            }

            // ==================================================
            // 실제 주문 전송(0530 매매_Xing에 위임)
            // ==================================================
            try
            {
                await _trader.SendOrderLive(sideKor, shcode, price, qty, band).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Write("[0500][SEND][EX] " + ex.Message);
                return false;
            }
        }

        private static string NormalizeSideKorOnly(string sideKor)
        {
            sideKor = (sideKor ?? "").Trim();

            if (string.Equals(sideKor, "BUY", StringComparison.OrdinalIgnoreCase)) return "매수";
            if (string.Equals(sideKor, "SELL", StringComparison.OrdinalIgnoreCase)) return "매도";

            if (sideKor == "매수" || sideKor == "매도") return sideKor;

            throw new ArgumentException($"sideKor는 '매수' 또는 '매도'만 허용됩니다. sideKor='{sideKor}'", nameof(sideKor));
        }

        private static string SafeTrim(string s)
        {
            return string.IsNullOrWhiteSpace(s) ? "" : s.Trim();
        }

        private void Write(string msg)
        {
            try
            {
                Console.WriteLine(msg);
                Debug.WriteLine(msg);
                Log?.Invoke(msg);
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }
}

// 2026-02-15 83427
