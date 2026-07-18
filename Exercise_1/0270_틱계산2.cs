// 0270_틱계산2.cs  (복붙용 / C# 7.3)
// ------------------------------------------------------------
// ✅ 변경(핵심):
// - 이제 0270은 히스토리(_ticks.Min/Max)로 tickMin/tickMax를 계산하지 않는다.
// - 0250(세션 판단기)가 segMin/segMax/gap(세션 기준)를 계산하여 0270에 전달한다.
// - 0270은 "그림(UI)"에 표시만 한다. (View 역할)
//
// 표시 규칙(세션 기준):
// - IN-BAND / 세션 없음: textbox 2,3,4,7 = 0 (현재가만 표시)
// - SELL 세션:
//     textBox3 = segMax
//     textBox2 = segMax - cur   (세션 기준 gap)
//     textBox7/textBox4 = 0
// - BUY 세션:
//     textBox7 = segMin
//     textBox4 = cur - segMin   (세션 기준 gap)
//     textBox3/textBox2 = 0
// ------------------------------------------------------------

using System;
using System.Globalization;
using System.Windows.Forms;

namespace Exercise_1
{
    public sealed class _0270_틱계산2 : _0250_Tick_Process.ISessionUiSink
    {
        private readonly Control _owner;

        private readonly TextBox _textBox1_Current;
        private readonly TextBox _textBox2_DiffUp;
        private readonly TextBox _textBox3_TickMax;
        private readonly TextBox _textBox4_GapDown;
        private readonly TextBox _textBox7_TickMin;

        public _0270_틱계산2(
            Control owner,
            TextBox textBox1_Current,
            TextBox textBox2_DiffUp,
            TextBox textBox3_TickMax,
            TextBox textBox4_GapDown,
            TextBox textBox7_TickMin)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));

            _textBox1_Current = textBox1_Current ?? throw new ArgumentNullException(nameof(textBox1_Current));
            _textBox2_DiffUp = textBox2_DiffUp ?? throw new ArgumentNullException(nameof(textBox2_DiffUp));
            _textBox3_TickMax = textBox3_TickMax ?? throw new ArgumentNullException(nameof(textBox3_TickMax));
            _textBox4_GapDown = textBox4_GapDown ?? throw new ArgumentNullException(nameof(textBox4_GapDown));
            _textBox7_TickMin = textBox7_TickMin ?? throw new ArgumentNullException(nameof(textBox7_TickMin));
        }

        // ============================================
        // ✅ 0250이 세션 없을 때 호출(현재가 + 0 리셋)
        // ============================================
        public void ShowInBand(long currentPrice)
        {
            if (_owner.IsDisposed) return;

            if (_owner.InvokeRequired)
            {
                _owner.BeginInvoke(new Action(() => ShowInBand(currentPrice)));
                return;
            }

            try
            {
                _textBox1_Current.Text = currentPrice.ToString(CultureInfo.InvariantCulture);
                _textBox2_DiffUp.Text = "0";
                _textBox3_TickMax.Text = "0";
                _textBox4_GapDown.Text = "0";
                _textBox7_TickMin.Text = "0";
            }
            catch { }
        }

        // ============================================
        // ✅ 0250이 세션 진행 중 매 틱 호출(표시만)
        // ============================================
        public void ShowSession(_0250_Tick_Process.SessionUiState s)
        {
            if (s == null) return;
            if (_owner.IsDisposed) return;

            if (_owner.InvokeRequired)
            {
                _owner.BeginInvoke(new Action(() => ShowSession(s)));
                return;
            }

            try
            {
                _textBox1_Current.Text = s.CurrentPrice.ToString(CultureInfo.InvariantCulture);

                if (string.Equals(s.Dir, "SELL", StringComparison.OrdinalIgnoreCase))
                {
                    // SELL 세션: segMax / (segMax-cur)
                    _textBox3_TickMax.Text = s.SegMax.ToString(CultureInfo.InvariantCulture);

                    long diff = s.SegMax - s.CurrentPrice;
                    if (diff < 0) diff = -diff; // 안전
                    _textBox2_DiffUp.Text = diff.ToString(CultureInfo.InvariantCulture);

                    _textBox7_TickMin.Text = "0";
                    _textBox4_GapDown.Text = "0";
                }
                else if (string.Equals(s.Dir, "BUY", StringComparison.OrdinalIgnoreCase))
                {
                    // BUY 세션: segMin / (cur-segMin)
                    _textBox7_TickMin.Text = s.SegMin.ToString(CultureInfo.InvariantCulture);

                    long gap = s.CurrentPrice - s.SegMin;
                    if (gap < 0) gap = -gap; // 안전
                    _textBox4_GapDown.Text = gap.ToString(CultureInfo.InvariantCulture);

                    _textBox3_TickMax.Text = "0";
                    _textBox2_DiffUp.Text = "0";
                }
                else
                {
                    // 예외: 방향 없음이면 리셋
                    _textBox2_DiffUp.Text = "0";
                    _textBox3_TickMax.Text = "0";
                    _textBox4_GapDown.Text = "0";
                    _textBox7_TickMin.Text = "0";
                }
            }
            catch { }
        }

        // ============================================
        // ✅ 강제 리셋(안전)
        // ============================================
        public void ResetAll(long currentPrice)
        {
            ShowInBand(currentPrice);
        }
    }
}
// 2026-02-21 90512