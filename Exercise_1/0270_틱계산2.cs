// 0270_틱계산2.cs  (복붙용 / C# 7.3)
// ------------------------------------------------------------
// 목적(디버그 강화 + 안전한 밴드판정)  ※ Login.cs에서 listBox 없이 사용 버전
// - 밴드 상단/하단을 팔/살 순서에 상관없이 upper/lower로 정규화
// - IN-BAND이면 textbox 2,3,4,7 = 0
// - 상단 OUT(cur > upper):
//     textBox3=tickMax, textBox2=(tickMax-cur), textBox4/7=0
// - 하단 OUT(cur < lower):
//     textBox7=tickMin, textBox4=(cur-tickMin)  ★ GAP 표시(원하신 값: 69100-69000=100)
//     textBox2/3=0
// - ✅ 내부 tickList(_ticks)에 currentPrice를 누적하여 tickMin/tickMax를 계산
// - ✅ 콘솔에 분기/값을 강하게 출력해서 “왜 안 바뀌는지” 즉시 확인 가능
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

        public void UpdateByBand(long currentPrice, long 팔가격, long 살가격)
        {
            // ✅ upper/lower 정규화 (팔/살 순서 뒤집혀도 안전)
            long upper = Math.Max(팔가격, 살가격);
            long lower = Math.Min(팔가격, 살가격);

            bool inBand = (currentPrice <= upper && currentPrice >= lower);
            bool above = (currentPrice > upper);
            bool below = (currentPrice < lower);

            // tick 누적 (listBox 역할)
            _ticks.Add(currentPrice);

            // 메모리 보호
            if (_ticks.Count > 5000)
                _ticks.RemoveRange(0, 2000);

            long tickMin = _ticks.Min();
            long tickMax = _ticks.Max();

            //Console.WriteLine(
            //    $"[0270][CALL] cur={currentPrice} 팔={팔가격} 살={살가격} " +
            //    $"upper={upper} lower={lower} inBand={inBand} above={above} below={below} " +
            //    $"tickMin={tickMin} tickMax={tickMax}");

            // 1) IN-BAND이면 리셋
            if (inBand)
            {
                _wasAbove = false;
                _wasBelow = false;

                ApplyInBandReset(currentPrice);
                //Console.WriteLine("[0270][BRANCH] IN-BAND -> reset(2,3,4,7=0)");
                return;
            }

            // 2) OUT-BAND인데 둘 다 false면(이론상 없음) 방어
            if (!above && !below)
            {
                //Console.WriteLine("[0270][BRANCH] ??? (neither above nor below) -> return");
                return;
            }

            // ✅ 디버깅 단계에서는 “바뀐게 없다” 방지 위해 중복방지 OFF 권장
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

                    long diff = tickMax - currentPrice;
                    _textBox2_Diff.Text = diff.ToString(CultureInfo.InvariantCulture);

                    _textBox7_TickMin.Text = "0";
                    _textBox4_Gap.Text = "0";

                    //Console.WriteLine($"[0270][APPLY] ABOVE -> tb3=tickMax({tickMax}), tb2=diff(tickMax-cur)({diff}), tb4/tb7=0");
                }
                else if (below)
                {
                    _textBox7_TickMin.Text = tickMin.ToString(CultureInfo.InvariantCulture);

                    // ★ 원하신 GAP: cur - tickMin
                    // 예: tickMin=69000, cur=69100 => 100
                    long gap = currentPrice - tickMin;
                    _textBox4_Gap.Text = gap.ToString(CultureInfo.InvariantCulture);

                    _textBox3_TickMax.Text = "0";
                    _textBox2_Diff.Text = "0";

                    //Console.WriteLine($"[0270][APPLY] BELOW -> tb7=tickMin({tickMin}), tb4=gap(cur-tickMin)({gap}), tb2/tb3=0");
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

// 2026-01-28 67158
