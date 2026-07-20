// 0050_Real_Test환경결정.cs  (복붙용 / C# 7.3)
// ------------------------------------------------------------
// 3모드 최종본
// - REAL       : 실계좌 실주문
// - TEST_LIVE  : 모의투자 서버(데모) 실주문
// - TEST_MOCK  : 내부 mock / replay
//
// 중요 정책:
// - 기존 선택창이 "TEST"를 넘기면 자동으로 TEST_LIVE로 해석한다.
//   => 기존 UI/선택창을 당장 안 바꿔도 TEST에서는 실주문이 나간다.
// - mock는 오직 "TEST_MOCK"일 때만 사용한다.
// ------------------------------------------------------------

using System;
using System.Diagnostics;

namespace Exercise_1
{
    public static class _0050_Real_Test환경결정
    {
        public const string MODE_REAL = "REAL";
        public const string MODE_TEST_LIVE = "TEST_LIVE";
        public const string MODE_TEST_MOCK = "TEST_MOCK";

        // 레거시 호환용 alias
        public const string MODE_TEST_LEGACY = "TEST";

        // ✅ 고정된 RunMode
        public static string RunMode { get; private set; }

        // ------------------------------------------------------------
        // 모드 판별
        // ------------------------------------------------------------
        public static bool IsReal =>
            string.Equals(RunMode, MODE_REAL, StringComparison.OrdinalIgnoreCase);

        public static bool IsTestLive =>
            string.Equals(RunMode, MODE_TEST_LIVE, StringComparison.OrdinalIgnoreCase);

        public static bool IsTestMock =>
            string.Equals(RunMode, MODE_TEST_MOCK, StringComparison.OrdinalIgnoreCase);

        // ✅ 기존 코드 호환:
        // 예전 코드가 IsTest를 보면 "실모의(TEST_LIVE) + MOCK(TEST_MOCK)" 둘 다 true
        public static bool IsTest => IsTestLive || IsTestMock;

        // 실주문 경로 여부
        public static bool UseLiveOrder => IsReal || IsTestLive;

        // 내부 mock 체결 경로 여부
        public static bool UseMockOrder => IsTestMock;

        // 화면/로그용
        public static string DisplayModeName
        {
            get
            {
                if (IsReal) return "REAL";
                if (IsTestLive) return "TEST_LIVE";
                if (IsTestMock) return "TEST_MOCK";
                return RunMode ?? "(UNSET)";
            }
        }

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
        public static string LoginPw { get; private set; }   // 로그인 비밀번호
        public static string CertPw { get; private set; }    // 인증서 비밀번호
        public static string Account { get; private set; }   // 계좌번호

        // (레거시 호환) 예전 코드가 _0050.Password 를 참조하는 경우 대비
        public static string Password => LoginPw;

        public static string LogPrefix
        {
            get
            {
                if (IsReal) return "[REAL]";
                if (IsTestLive) return "[TEST_LIVE]";
                if (IsTestMock) return "[TEST_MOCK]";
                return "[UNKNOWN]";
            }
        }

        /// <summary>
        /// 프로그램 시작 시 딱 1회 호출. 이후 변경 금지.
        /// 허용 입력:
        ///   REAL
        ///   TEST_LIVE
        ///   TEST_MOCK
        ///   TEST        -> 자동으로 TEST_LIVE 로 승격
        /// </summary>
        public static void Apply(string runMode)
        {
            if (!string.IsNullOrWhiteSpace(RunMode))
                throw new InvalidOperationException("환경은 이미 확정되었습니다. 재설정은 허용되지 않습니다.");

            if (string.IsNullOrWhiteSpace(runMode))
                throw new ArgumentNullException(nameof(runMode));

            runMode = NormalizeMode(runMode);

            RunMode = runMode;

            if (runMode == MODE_TEST_LIVE)
            {
                // ✅ 데모서버 실주문 테스트
                DbPath = @"C:\c#\mydb_test.db";
                UserId = "cds002";
                LoginPw = "hanjm12";
                CertPw = "1908hanjm!!";
                Account = "55505064701";
            }
            else if (runMode == MODE_TEST_MOCK)
            {
                // ✅ 내부 mock / replay
                // DB는 test DB 사용
                DbPath = @"C:\c#\mydb_test.db";
                UserId = "cds002";
                LoginPw = "hanjm12";
                CertPw = "1908hanjm!!";
                Account = "55505064701";
            }
            else if (runMode == MODE_REAL)
            {
                DbPath = @"C:\c#\mydb.db";
                UserId = "cds002";
                LoginPw = "002cds";
                CertPw = "1908hanjm!!";
                Account = "00511723753";
            }
            else
            {
                throw new InvalidOperationException("알 수 없는 RunMode: " + runMode);
            }

            Console.WriteLine($"[0050] Apply OK mode={RunMode} display={DisplayModeName} db='{DbPath}' id='{UserId}' act='{Account}'");
            Console.WriteLine($"[0050] flags IsReal={IsReal} IsTest={IsTest} IsTestLive={IsTestLive} IsTestMock={IsTestMock} UseLiveOrder={UseLiveOrder} UseMockOrder={UseMockOrder}");
            Console.WriteLine($"[0050] pwLen(LoginPw)={(LoginPw ?? "").Length} certLen={(CertPw ?? "").Length}");
            Debug.WriteLine($"[0050] Apply OK mode={RunMode} display={DisplayModeName} db='{DbPath}' id='{UserId}' act='{Account}'");
        }

        private static string NormalizeMode(string runMode)
        {
            string m = (runMode ?? "").Trim().ToUpperInvariant();

            if (m == MODE_TEST_LEGACY)
                return MODE_TEST_LIVE;   // ✅ 레거시 TEST는 이제 "실주문 가능한 데모 모드"

            if (m == MODE_REAL) return MODE_REAL;
            if (m == MODE_TEST_LIVE) return MODE_TEST_LIVE;
            if (m == MODE_TEST_MOCK) return MODE_TEST_MOCK;

            throw new InvalidOperationException("지원하지 않는 RunMode: " + runMode);
        }
    }
}
// 2026-03-18 48261