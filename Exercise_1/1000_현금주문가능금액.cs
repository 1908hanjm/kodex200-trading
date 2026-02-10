// 1000_현금주문가능금액.cs
// ------------------------------------------------------------
// 역할:
// - CSPAQ12200 조회로 "현금주문가능금액"을 가져와서
// - 이 클래스 내부에서 MessageBox로 바로 표시한다.
//
// 요청 반영(고정 입력):
//   CSPAQ12200InBlock1
//     RecCnt   : 00001
//     AcntNo   : 00511723753
//     Pwd      : 1908
//     BalCreTp : 0
//
// 1000_현금주문가능금액.cs  (복붙용 / C# 7.3)
// ------------------------------------------------------------
// ✅ 목적(요청 확정):
// - Login.cs에서 계좌/비번을 넘겨받아(Request(acnt,pwd) 전용)
// - CSPAQ12200을 조회해서 "현금주문가능금액"을 MessageBox로 표시
//
// ✅ 규칙:
// - Request(acntNo, pwd)만 제공 (무인수 Request 없음)
// - 쿨다운 없음
// - 메시지박스는 class1000(=이 클래스) 내부에서 띄운다.
//
// 전제:
// - XING 로그인(세션 연결) 이후 호출
// - Res 파일 경로: @"C:\LS_SEC\xingAPI\Res\CSPAQ12200.res"
//   (환경에 따라 다르면 아래 RES_PATH만 수정)
// ------------------------------------------------------------

using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using XA_DATASETLib;

namespace Exercise_1
{
    public sealed class _1000_현금주문가능금액 : IDisposable
    {
        private const string RES_PATH = @"C:\LS_SEC\xingAPI\Res\CSPAQ12200.res";

        private XAQueryClass _q;
        private bool _inFlight;
        private bool _disposed;

        public _1000_현금주문가능금액()
        {
            _q = new XAQueryClass();
            _q.LoadFromResFile(RES_PATH);

            _q.ReceiveData += OnReceiveData;
            _q.ReceiveMessage += OnReceiveMessage;
        }

        /// <summary>
        /// ✅ Request(acntNo, pwd) 전용
        /// - acntNo: 계좌번호(예: 00511723753)
        /// - pwd   : 계좌비번 4자리(예: 1908)
        /// </summary>
        public void Request(string acntNo, string pwd)
        {
            if (_disposed) return;

            acntNo = (acntNo ?? "").Trim();
            pwd = (pwd ?? "").Trim();

            if (string.IsNullOrWhiteSpace(acntNo))
            {
                MessageBox.Show(
                    "AcntNo(계좌번호)가 비어 있습니다.",
                    "현금주문가능금액",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
                return;
            }

            if (string.IsNullOrWhiteSpace(pwd))
            {
                MessageBox.Show(
                    "Pwd(계좌비밀번호)가 비어 있습니다.",
                    "현금주문가능금액",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
                return;
            }

            if (_inFlight)
            {
                MessageBox.Show(
                    "이미 조회 요청이 진행 중입니다.",
                    "현금주문가능금액",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
                return;
            }

            try
            {
                _inFlight = true;

                // ✅ 요청하신 InBlock 값
                // CSPAQ12200InBlock1:
                //   RecCnt    : 00001
                //   AcntNo    : (Login에서 전달)
                //   Pwd       : (Login에서 전달)
                //   BalCreTp  : 0
                _q.SetFieldData("CSPAQ12200InBlock1", "RecCnt", 0, "00001");
                _q.SetFieldData("CSPAQ12200InBlock1", "AcntNo", 0, acntNo);
                _q.SetFieldData("CSPAQ12200InBlock1", "Pwd", 0, pwd);
                _q.SetFieldData("CSPAQ12200InBlock1", "BalCreTp", 0, "0");

                int rc = _q.Request(false);
                if (rc < 0)
                {
                    _inFlight = false;

                    MessageBox.Show(
                        $"CSPAQ12200 Request 실패 (rc={rc})",
                        "현금주문가능금액",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error
                    );
                }
            }
            catch (Exception ex)
            {
                _inFlight = false;

                MessageBox.Show(
                    "요청 중 예외:\r\n" + ex.Message,
                    "현금주문가능금액",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
        }

        private void OnReceiveData(string trCode)
        {
            try
            {
                // 일반적으로 현금주문가능금액 필드는 OutBlock2의 MnyOrdAbleAmt로 사용됩니다.
                // (만약 0만 나온다면, 실제 사용하는 필드명이 다른지 RES의 OutBlock 필드명을 확인해야 합니다.)
                string raw = SafeGetField("CSPAQ12200OutBlock2", "MnyOrdAbleAmt", 0);
                long able = ParseLong(raw);

                MessageBox.Show(
                    $"현금주문가능금액(MnyOrdAbleAmt)\r\n\r\n{able:N0} 원",
                    "현금주문가능금액",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "수신 처리 예외:\r\n" + ex.Message,
                    "현금주문가능금액",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
            finally
            {
                _inFlight = false;
            }
        }

        private void OnReceiveMessage(bool bIsSystemError, string nMessageCode, string szMessage)
        {
            // 시스템 메시지(에러)만 사용자에게 알림
            if (!bIsSystemError) return;

            _inFlight = false;

            MessageBox.Show(
                $"[CSPAQ12200 MSG]\r\ncode={nMessageCode}\r\nmsg={szMessage}",
                "현금주문가능금액",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        }

        private string SafeGetField(string block, string field, int index)
        {
            try
            {
                return (_q.GetFieldData(block, field, index) ?? "").Trim();
            }
            catch
            {
                return "";
            }
        }

        private static long ParseLong(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;

            s = s.Trim().Replace(",", "");

            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v))
                return v;

            if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal d))
                return (long)d;

            return 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_q != null)
                {
                    try { _q.ReceiveData -= OnReceiveData; } catch { }
                    try { _q.ReceiveMessage -= OnReceiveMessage; } catch { }

                    try { Marshal.FinalReleaseComObject(_q); } catch { }
                    _q = null;
                }
            }
            catch { }
        }
    }
}

// 2026-02-07 48319
