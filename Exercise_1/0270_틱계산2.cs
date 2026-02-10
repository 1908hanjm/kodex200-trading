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
// ✅ [추가] 거래밴드(옵션2: 지나온 밴드만 표시)
// - 0270은 밴드번호를 "계산"하지 않는다. 호출자가 bandNo를 넘겨준다.
// - bandNo가 바뀌는 순간, 직전 bandNo만 문자열에 누적한다. (현재 밴드는 누적하지 않음)
// - FIRE 시점(체결 확정 등)에 ResetTradePath()를 호출해서 초기화한다.
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

        // =========================================================
        // ✅ 거래밴드(옵션2) 최소 상태: string + int
        // =========================================================
        private string _tradeBandsPassed = ""; // "밴드5, 밴드4"
        private int _prevBandNo = 0;           // 직전 bandNo (현재 bandNo는 포함하지 않음)
        private bool _tradeActive = false;     // bandNo 추적 활성화 여부

        /// <summary>
        /// 옵션2 거래밴드 표시 문자열. 비어있으면 "(없음)" 리턴.
        /// </summary>
        public string TradeBandsPassedText
        {
            get { return string.IsNullOrEmpty(_tradeBandsPassed) ? "(없음)" : _tradeBandsPassed; }
        }

        /// <summary>
        /// FIRE(매매 실행) 직후 호출: 거래밴드 초기화
        /// </summary>
        public void ResetTradePath()
        {
            _tradeBandsPassed = "";
            _prevBandNo = 0;
            _tradeActive = false;
        }

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
        // ✅ 기존 호환용(밴드번호 모름): 거래밴드 추적 안함
        // =========================================================
        public void UpdateByBand(long currentPrice, long 팔가격, long 살가격)
        {
            UpdateByBand(currentPrice, 팔가격, 살가격, bandNo: 0);
        }

        // =========================================================
        // ✅ 신규 오버로드: bandNo를 함께 받아 거래밴드(옵션2) 누적
        // =========================================================
        public void UpdateByBand(long currentPrice, long 팔가격, long 살가격, int bandNo)
        {
            // ✅ bandNo 추적(옵션2)
            if (bandNo > 0)
            {
                TrackTradeBand_Option2(bandNo);
            }

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

            // 1) IN-BAND이면 표시 리셋(거래밴드는 FIRE에서만 ResetTradePath로 초기화)
            if (inBand)
            {
                _wasAbove = false;
                _wasBelow = false;

                ApplyInBandReset(currentPrice);
                return;
            }

            // 2) OUT-BAND인데 둘 다 false면 방어
            if (!above && !below)
            {
                return;
            }

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

        // ✅ 옵션2: “지나온 밴드만” 누적
        private void TrackTradeBand_Option2(int currentBandNo)
        {
            if (!_tradeActive)
            {
                _tradeActive = true;
                _prevBandNo = currentBandNo; // 기준만 잡음
                return;
            }

            if (currentBandNo == _prevBandNo) return;

            // band가 바뀌었다 = 직전 밴드를 “지나왔다”
            AppendPassedBand(_prevBandNo);

            // 기준 갱신
            _prevBandNo = currentBandNo;
        }

        private void AppendPassedBand(int band)
        {
            if (band <= 0) return;

            string token = "밴드" + band.ToString(CultureInfo.InvariantCulture);

            if (string.IsNullOrEmpty(_tradeBandsPassed))
                _tradeBandsPassed = token;
            else
                _tradeBandsPassed += ", " + token;
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
                }
                else if (below)
                {
                    _textBox7_TickMin.Text = tickMin.ToString(CultureInfo.InvariantCulture);

                    // GAP: cur - tickMin
                    long gap = currentPrice - tickMin;
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
