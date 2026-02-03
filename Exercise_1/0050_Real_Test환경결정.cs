// 0050_Real_Test환경결정.cs  (복붙용 / C# 7.3)  [최종본: LoginPw/CertPw 분리 고정]
// ------------------------------------------------------------
// 역할(확정):
// - 프로그램 기동 시 1회: Apply(runMode)로 환경을 고정(static)
// - 이후 어디서든 DbPath/ConnStr/Account/UserId/LoginPw/CertPw/LogPrefix 참조
// - ❌ XING 로그인/SC1 등록/매매 로직 금지 (설정만)
// ------------------------------------------------------------

using System;
using System.Diagnostics;

namespace Exercise_1
{
    public static class _0050_Real_Test환경결정
    {
        public const string MODE_TEST = "TEST";
        public const string MODE_REAL = "REAL";

        // ✅ 고정된 RunMode
        public static string RunMode { get; private set; } // "TEST" or "REAL"
        public static bool IsTest => string.Equals(RunMode, MODE_TEST, StringComparison.OrdinalIgnoreCase);
        public static bool IsReal => string.Equals(RunMode, MODE_REAL, StringComparison.OrdinalIgnoreCase);

        // ✅ DB
        public static string DbPath { get; private set; }

        public static string ConnStr
        {
            get
            {
                var db = DbPath;
                if (string.IsNullOrWhiteSpace(db)) db = @"C:\c#\mydb.db";
                return $"Data Source={db};Version=3;";
            }
        }

        // ✅ XING credential/계좌
        public static string UserId { get; private set; }    // 로그인 ID
        public static string LoginPw { get; private set; }   // ✅ 로그인 비밀번호 (XASession.Login userPw)
        public static string CertPw { get; private set; }    // ✅ 인증서 비밀번호 (XASession.Login certPw)
        public static string Account { get; private set; }   // 계좌번호

        // (레거시 호환) 예전 코드가 _0050.Password 를 참조하는 경우 대비
        // ✅ 의미: LoginPw
        public static string Password => LoginPw;

        // ✅ 로그 프리픽스
        public static string LogPrefix => IsTest ? "[TEST]" : "[REAL]";

        /// <summary>
        /// 프로그램 시작 시 딱 1회 호출. 이후 변경 금지.
        /// runMode: "TEST" or "REAL"
        /// </summary>
        public static void Apply(string runMode)
        {
            if (!string.IsNullOrWhiteSpace(RunMode))
                throw new InvalidOperationException("환경은 이미 확정되었습니다. 재설정은 허용되지 않습니다.");

            if (string.IsNullOrWhiteSpace(runMode))
                throw new ArgumentNullException(nameof(runMode));

            runMode = runMode.Trim().ToUpperInvariant();
            RunMode = runMode;

            if (runMode == MODE_TEST)
            {
                DbPath = @"C:\c#\mydb_test.db";
                UserId = "cds002";

                // ✅ TEST 로그인 비밀번호 (실제 모의 로그인 비번)
                LoginPw = "hanjm12";

                // ✅ TEST 인증서 비밀번호 (공동인증서 비번)
                CertPw = "1908hanjm!!";

                Account = "55504613901";
            }
            else if (runMode == MODE_REAL)
            {
                DbPath = @"C:\c#\mydb.db";
                UserId = "cds002";

                // ✅ REAL 로그인 비밀번호 (XING 로그인 화면의 비밀번호)
                // ⚠️ 주의: 4자리 주문비번(Login.JMpass)과 다를 수 있습니다.
                LoginPw = "002cds";

                // ✅ REAL 인증서 비밀번호
                CertPw = "1908hanjm!!";

                Account = "00511723753";
            }
            else
            {
                throw new InvalidOperationException("알 수 없는 RunMode: " + runMode);
            }

            // 최소 진단 로그
            Console.WriteLine($"[0050] Apply OK mode={RunMode} db='{DbPath}' id='{UserId}' act='{Account}'");
            Console.WriteLine($"[0050] pwLen(LoginPw)={(LoginPw ?? "").Length} certLen={(CertPw ?? "").Length}");
            Debug.WriteLine($"[0050] Apply OK mode={RunMode} db='{DbPath}' id='{UserId}' act='{Account}'");
        }
    }
}

// 2026-02-03 18427
