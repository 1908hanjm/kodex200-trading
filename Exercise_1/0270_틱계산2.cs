// 0270_틱계산2.cs  (복붙용 / C# 7.3)
// ------------------------------------------------------------
// 목적(디버그 강화 + 안전한 밴드판정)  ※ Login.cs에서 listBox 없이 사용 버전
// - 밴드 상단/하단을 팔/살 순서에 상관없이 upper/lower로 정규화
// - IN-BAND이면 textbox 2,3,4,7 = 0
// - 상단 OUT(cur > upper):
//     textBox3=tickMax, textBox2=(tickMax-cur), textBox4/7=0
// - 하단 OUT(cur < lower):
//     textBox7=tickMin, textBox4=(cur-tickMin)  ★ GAP 표시
//     textBox2/3=0
// - ✅ 내부 tickList(_ticks)에 currentPrice를 누적하여 tickMin/tickMax를 계산
//
// ✅ 정리(사용자 결정: "실제 주문 예정 밴드만 표시"로 전환)
// - 0270은 거래밴드 문자열/누적/Reset 등 일절 담당하지 않는다.
// - 거래밴드 UI는 0300(밴드매칭)에서 "주문 예정 밴드"를 확정하고
//   Login.UiPlannedBandsText 같은 전역 문자열로만 전달한다.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace Exercise_1
{
    public sealed class _0270_틱계산2
    {
        private readonly Control _owner;

        private readonly TextBox _textBox1_Current;
        private readonly TextBox _textBox2_Diff;
        private readonly TextBox _textBox3_TickMax;
        private readonly TextBox _textBox4_Gap;
        private readonly TextBox _textBox7_TickMin;

        // 내부 tickList (listBox 대신)
        private readonly List<long> _ticks = new List<long>(capacity: 2048);

        // 중복 갱신 방지(원하면 사용). 디버깅 단계에서는 OFF 권장.
        private bool _wasAbove = false;
        private bool _wasBelow = false;

        public _0270_틱계산2(
            Control owner,
            TextBox textBox1_Current,
            TextBox textBox2_DiffUp,
            TextBox textBox3_TickMax,
            TextBox textBox4_DiffDown,
            TextBox textBox7_TickMin)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));

            _textBox1_Current = textBox1_Current ?? throw new ArgumentNullException(nameof(textBox1_Current));
            _textBox2_Diff = textBox2_DiffUp ?? throw new ArgumentNullException(nameof(textBox2_DiffUp));
            _textBox3_TickMax = textBox3_TickMax ?? throw new ArgumentNullException(nameof(textBox3_TickMax));
            _textBox4_Gap = textBox4_DiffDown ?? throw new ArgumentNullException(nameof(textBox4_DiffDown));
            _textBox7_TickMin = textBox7_TickMin ?? throw new ArgumentNullException(nameof(textBox7_TickMin));
        }

        // =========================================================
        // ✅ 핵심 API: StartBand의 팔/살(또는 upper/lower)을 넘겨서 표시만 수행
        // =========================================================
        public void UpdateByBand(long currentPrice, long 팔가격, long 살가격)
        {
            // ✅ upper/lower 정규화 (팔/살 순서 뒤집혀도 안전)
            long upper = Math.Max(팔가격, 살가격);
            long lower = Math.Min(팔가격, 살가격);

            bool inBand = (currentPrice <= upper && currentPrice >= lower);
            bool above = (currentPrice > upper);
            bool below = (currentPrice < lower);

            // tick 누적
            _ticks.Add(currentPrice);

            // 메모리 보호
            if (_ticks.Count > 5000)
                _ticks.RemoveRange(0, 2000);

            long tickMin = _ticks.Min();
            long tickMax = _ticks.Max();

            // 1) IN-BAND이면 리셋
            if (inBand)
            {
                _wasAbove = false;
                _wasBelow = false;

                ApplyInBandReset(currentPrice);
                return;
            }

            // 2) OUT-BAND인데 둘 다 false면 방어
            if (!above && !below)
                return;

            // 중복방지(디버깅 단계에서는 OFF 권장)
            // if (above && _wasAbove) return;
            // if (below && _wasBelow) return;

            _wasAbove = above;
            _wasBelow = below;

            if (_owner.IsDisposed) return;

            if (_owner.InvokeRequired)
            {
                _owner.BeginInvoke(new Action(() =>
                    ApplyOutBand(currentPrice, above, below, tickMin, tickMax)));
            }
            else
            {
                ApplyOutBand(currentPrice, above, below, tickMin, tickMax);
            }
        }

        // IN-BAND: textbox 0 초기화
        private void ApplyInBandReset(long currentPrice)
        {
            if (_owner.IsDisposed) return;

            if (_owner.InvokeRequired)
            {
                _owner.BeginInvoke(new Action(() => ApplyInBandReset(currentPrice)));
                return;
            }

            try
            {
                _textBox1_Current.Text = currentPrice.ToString(CultureInfo.InvariantCulture);
                _textBox2_Diff.Text = "0";
                _textBox3_TickMax.Text = "0";
                _textBox4_Gap.Text = "0";
                _textBox7_TickMin.Text = "0";
            }
            catch { }
        }

        // OUT-BAND
        private void ApplyOutBand(long currentPrice, bool above, bool below, long tickMin, long tickMax)
        {
            try
            {
                _textBox1_Current.Text = currentPrice.ToString(CultureInfo.InvariantCulture);

                if (above)
                {
                    _textBox3_TickMax.Text = tickMax.ToString(CultureInfo.InvariantCulture);

                    long diff = tickMax - currentPrice; // tickMax - cur
                    _textBox2_Diff.Text = diff.ToString(CultureInfo.InvariantCulture);

                    _textBox7_TickMin.Text = "0";
                    _textBox4_Gap.Text = "0";
                }
                else if (below)
                {
                    _textBox7_TickMin.Text = tickMin.ToString(CultureInfo.InvariantCulture);

                    long gap = currentPrice - tickMin; // cur - tickMin
                    _textBox4_Gap.Text = gap.ToString(CultureInfo.InvariantCulture);

                    _textBox3_TickMax.Text = "0";
                    _textBox2_Diff.Text = "0";
                }
            }
            catch { }
        }

        public void Clear()
        {
            try { _ticks.Clear(); } catch { }

            try
            {
                _textBox2_Diff.Text = "0";
                _textBox3_TickMax.Text = "0";
                _textBox4_Gap.Text = "0";
                _textBox7_TickMin.Text = "0";
            }
            catch { }
        }
    }
}

// 2026-02-10 59384
