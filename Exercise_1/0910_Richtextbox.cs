// 0910_Richtextbox.cs  (복붙용 / C# 7.3)
// ------------------------------------------------------------
// 역할(통합):
// - RichTextBox 출력(WriteLine/Write/Append) 전담
// - UI 스레드 안전(InvokeRequired) 내장
// - 거래 확정 이후 UI 마무리(맨 마지막 호출):
//      ClearAll() -> 변경된 밴드/현재가 Write(콜백) 순서 보장
//
// 사용 예(권장):
//   // Login.cs에서 1회 생성
//   _rtb = new _0910_Richtextbox(this, richTextBox1);
//
//   // 어디서든 로그
//   _rtb.WriteLine("[TRACE] ...");
//
//   // 0700_매매후update.cs 맨 마지막에서
//   _rtb.AfterTradeClearThen(() => {
//       _rtb.WriteLine($"현재가={curPrice:N0}, startBand={startBand}");
//       // 밴드 요약 출력 등...
//   });
//
// 주의:
// - 이 클래스는 "표현/출력"만 담당. DB/계산/매매판단 로직은 절대 넣지 않는다.
// - 반드시 DB update + BandList 반영 + startBand 재계산 + ListView 갱신 등이 끝난 뒤,
//   "가장 마지막"에 AfterTradeClearThen()을 호출한다.
// ------------------------------------------------------------

using System;
using System.Diagnostics;
using System.Text;
using System.Windows.Forms;

namespace Exercise_1
{
    public sealed class _0910_Richtextbox
    {
        private readonly Control _uiRoot;
        private readonly RichTextBox _rtb;

        // 출력 직렬화(멀티 스레드 환경에서 로그가 섞이지 않도록)
        private readonly object _writeLock = new object();

        // 옵션
        public bool Enabled { get; set; } = true;           // 전체 출력 on/off
        public bool PrefixTime { get; set; } = true;         // 시간 프리픽스
        public int MaxChars { get; set; } = 300_000;         // 너무 커지면 자동 Trim
        public int TrimToChars { get; set; } = 200_000;      // Trim 후 유지 길이

        public _0910_Richtextbox(Control uiRoot, RichTextBox rtb)
        {
            _uiRoot = uiRoot ?? throw new ArgumentNullException(nameof(uiRoot));
            _rtb = rtb ?? throw new ArgumentNullException(nameof(rtb));

            // RichTextBox 기본 권장(필요 시 외부에서 덮어써도 됨)
            try
            {
                _rtb.ReadOnly = true;
                _rtb.HideSelection = false;
                _rtb.DetectUrls = false;
                _rtb.WordWrap = false;
            }
            catch { }
        }

        // ─────────────────────────────────────────────
        // Public API
        // ─────────────────────────────────────────────

        public void ClearAll()
        {
            if (!Enabled) return;
            UiInvoke(() =>
            {
                try { _rtb.Clear(); } catch { }
            });
        }

        public void Write(string text)
        {
            if (!Enabled) return;
            if (text == null) text = string.Empty;

            lock (_writeLock)
            {
                string outText = PrefixTime ? BuildWithTime(text, newline: false) : text;
                UiInvoke(() => AppendInternal(outText));
            }
        }

        public void WriteLine(string text)
        {
            if (!Enabled) return;
            if (text == null) text = string.Empty;

            lock (_writeLock)
            {
                string outText = PrefixTime ? BuildWithTime(text, newline: true) : (text + Environment.NewLine);
                UiInvoke(() => AppendInternal(outText));
            }
        }

        public void WriteLine(string format, params object[] args)
        {
            if (!Enabled) return;
            string text;
            try
            {
                text = (args == null || args.Length == 0) ? format : string.Format(format, args);
            }
            catch
            {
                text = format ?? string.Empty;
            }
            WriteLine(text);
        }

        /// <summary>
        /// 거래 확정 이후 UI 최종 처리:
        /// - UI thread에서 ClearAll을 먼저 실행
        /// - 이어서 afterClearWrite 콜백 실행(변경된 밴드/현재가 출력)
        /// ※ 반드시 0700_매매후update "맨 마지막"에서 호출할 것
        /// </summary>
        public void AfterTradeClearThen(Action afterClearWrite)
        {
            if (!Enabled) return;

            UiInvoke(() =>
            {
                try { _rtb.Clear(); } catch { }

                try
                {
                    afterClearWrite?.Invoke();
                }
                catch (Exception ex)
                {
                    // afterClearWrite가 예외를 내도 UI를 죽이지 않는다.
                    try
                    {
                        AppendInternal(BuildWithTime("[UI] AfterTradeClearThen writeAction EX: " + ex.Message, newline: true));
                    }
                    catch { }
                }
            });
        }

        /// <summary>
        /// UI가 이미 dispose 되었는지 대략 확인
        /// </summary>
        public bool IsAlive()
        {
            try { return !_uiRoot.IsDisposed && !_rtb.IsDisposed; }
            catch { return false; }
        }

        // ─────────────────────────────────────────────
        // Internals
        // ─────────────────────────────────────────────

        private void UiInvoke(Action action)
        {
            if (action == null) return;

            try
            {
                if (_uiRoot.IsDisposed) return;

                if (_uiRoot.InvokeRequired)
                {
                    // BeginInvoke: 비동기, 호출자 스레드 블로킹 방지
                    _uiRoot.BeginInvoke(action);
                }
                else
                {
                    action();
                }
            }
            catch
            {
                // 폼 종료/Dispose 타이밍 등에서 예외 가능 → 무시(로그 시스템이 앱을 죽이면 안 됨)
            }
        }

        private void AppendInternal(string text)
        {
            try
            {
                // 크기 제한(너무 커지면 앞부분 Trim)
                TryTrimIfNeeded();

                _rtb.AppendText(text);
                _rtb.SelectionStart = _rtb.TextLength;
                _rtb.ScrollToCaret();
            }
            catch
            {
                // 출력 실패는 무시
            }
        }

        private void TryTrimIfNeeded()
        {
            try
            {
                if (MaxChars <= 0) return;
                if (TrimToChars <= 0) return;
                if (TrimToChars >= MaxChars) return;

                int len = _rtb.TextLength;
                if (len <= MaxChars) return;

                // 끝부분만 남기고 앞부분 제거
                int keep = TrimToChars;
                if (keep > len) keep = len;

                string tail = _rtb.Text.Substring(len - keep, keep);
                _rtb.Clear();
                _rtb.AppendText(tail);

                // 구분선 삽입(선택)
                _rtb.AppendText(Environment.NewLine);
                _rtb.AppendText(BuildWithTime("[UI] RichTextBox trimmed", newline: true));
            }
            catch
            {
                // Trim 실패는 무시
            }
        }

        private static string BuildWithTime(string text, bool newline)
        {
            // 시:분:초.밀리초
            string prefix = DateTime.Now.ToString("HH:mm:ss.fff");
            if (newline) return $"[{prefix}] {text}{Environment.NewLine}";
            return $"[{prefix}] {text}";
        }
    }
}
// 2026-01-21-12-00-00
