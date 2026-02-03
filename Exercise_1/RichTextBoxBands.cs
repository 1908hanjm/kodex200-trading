// RichTextBoxBands.cs — 실전용 버전 (C# 7.3 호환)
// 현재가 + (시작밴드의) 팔가격/살가격 표시 + 틱 값 정렬/출력 + 색상 구분 + 4개씩 출력 + Bold + 큰 글씨
// 시작밴드 규칙: qty > 0 인 밴드 중 band 번호가 가장 큰 밴드

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Exercise_1
{
    public sealed class RichTextBoxBands
    {
        private readonly RichTextBox _rtb;
        private readonly string _connStr;

        public RichTextBoxBands(RichTextBox rtb, string connStr)
        {
            _rtb = rtb ?? throw new ArgumentNullException(nameof(rtb));
            _connStr = connStr ?? throw new ArgumentNullException(nameof(connStr));
        }

        // 예전 코드와의 호환용 (지금 구조에서는 할 일 없음)
        public void ReloadMinMax()
        {
            // 필요 없으니 비워둠
        }

        // ============================================================
        //  현재값 + 이전값들 내림차순 정렬하여 색상 출력 (4개씩 한 줄)
        //  - 현재가: 파란색
        //  - 시작밴드의 팔/살 가격: 빨간색
        //  - 나머지: 검정
        //  + 맨 위에 "현재가/팔가격/살가격" 라벨 붙여서 출력
        // ============================================================
        public void RenderDesc(int current, IEnumerable<int> prevValues)
        {
            if (_rtb.InvokeRequired)
            {
                _rtb.Invoke(new Action(() => RenderDesc(current, prevValues)));
                return;
            }

            _rtb.Clear();

            int sellPrice = 0;   // 시작밴드 팔가격
            int buyPrice = 0;    // 시작밴드 살가격
            int startBandNo = 0; // 시작밴드 번호
            bool hasBand = false;

            // ✅ 시작밴드 찾기
            // Login.BandList 타입: BindingList<BandRange>
            if (Login.BandList != null && Login.BandList.Count > 0)
            {
                var startBand = Login.BandList
                    .Where(b => b.Qty > 0)
                    .OrderByDescending(b => b.Band)
                    .FirstOrDefault();

                if (startBand != null && startBand.Band != 0)
                {
                    // BandRange.High = 팔가격, BandRange.Low = 살가격
                    sellPrice = (int)startBand.High;
                    buyPrice = (int)startBand.Low;
                    startBandNo = startBand.Band;
                    hasBand = true;
                }
            }

            // ─────────────────────────────────────────────────────
            // 1) 맨 위에 현재가/팔가격/살가격 출력
            //    → 팔가격(14), 살가격(14) 형식
            // ─────────────────────────────────────────────────────
            AppendFancyText("현재가 : ", Color.Black);
            AppendFancyText($"{current:#,0}\n", Color.Blue);

            if (hasBand)
            {
                AppendFancyText($"팔가격({startBandNo}) : ", Color.Black);
                AppendFancyText($"{sellPrice:#,0}\n", Color.Red);

                AppendFancyText($"살가격({startBandNo}) : ", Color.Black);
                AppendFancyText($"{buyPrice:#,0}\n\n", Color.Red);
            }
            else
            {
                _rtb.AppendText("※ qty > 0 인 시작밴드를 찾지 못했습니다.\n\n");
            }

            // ─────────────────────────────────────────────────────
            // 2) 아래에는 숫자 그리드 (4개씩 한 줄) 출력
            //    - 리스트에는 현재가 + 시작밴드 팔/살가격 + prevValues 를 포함
            // ─────────────────────────────────────────────────────
            var list = new List<int>();

            if (prevValues != null)
                list.AddRange(prevValues);

            list.Add(current);

            if (hasBand)
            {
                list.Add(sellPrice);
                list.Add(buyPrice);
            }

            // 중복 제거 + 내림차순
            var ordered = list
                .Distinct()
                .OrderByDescending(v => v)
                .ToList();

            int col = 0;

            foreach (var v in ordered)
            {
                Color c;

                if (v == current)
                {
                    c = Color.Blue; // 현재가
                }
                else if (hasBand && (v == sellPrice || v == buyPrice))
                {
                    c = Color.Red; // 시작밴드 팔/살
                }
                else
                {
                    c = Color.Black;
                }

                AppendFancyText($"{v:#,0}", c);

                col++;
                if (col == 6)
                {
                    _rtb.AppendText("\n");
                    col = 0;
                }
                else
                {
                    _rtb.AppendText("    ");
                }
            }

            _rtb.SelectionColor = _rtb.ForeColor;
        }

        // ============================================================
        //  Bold + 폰트크기 + 색상 적용 출력
        // ============================================================
        private void AppendFancyText(string text, Color color)
        {
            _rtb.SelectionStart = _rtb.TextLength;
            _rtb.SelectionLength = 0;

            _rtb.SelectionColor = color;
            _rtb.SelectionFont = new Font(
                _rtb.Font.FontFamily,
                _rtb.Font.Size + 2,
                FontStyle.Bold);

            _rtb.AppendText(text);

            // 초기화
            _rtb.SelectionColor = _rtb.ForeColor;
            _rtb.SelectionFont = _rtb.Font;
        }
    }
}

// 2026-01-28 48392
