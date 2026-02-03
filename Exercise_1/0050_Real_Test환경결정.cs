// 0050_Real_Test환경결정.cs  (복붙용 / C# 7.3)
// ------------------------------------------------------------
// 역할(확정):
// - 프로그램 기동 시 1회: Apply(runMode)로 환경을 고정(static)
// - 이후 어디서든 DbPath/ConnStr/UserId/Password/Account/LogPrefix를 참조
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

        // ✅ 고정된 RunMode (Apply 1회 후 변경 금지)
        public static string RunMode { get; private set; }   // "TEST" or "REAL"
        public static bool IsTest => string.Equals(RunMode, MODE_TEST, StringComparison.OrdinalIgnoreCase);
        public static bool IsReal => string.Equals(RunMode, MODE_REAL, StringComparison.OrdinalIgnoreCase);

        // ✅ DB
        public static string DbPath { get; private set; }

        public static string ConnStr
        {
            get
            {
                // Apply 전에 호출될 수 있으므로 안전 처리
                var db = DbPath;
                if (string.IsNullOrWhiteSpace(db))
                    db = @"C:\c#\mydb.db";
                return $"Data Source={db};Version=3;";
            }
        }

        // ✅ XING credential / 계좌
        public static string UserId { get; private set; }
        public static string Password { get; private set; }
        public static string Account { get; private set; }

        // ✅ 로그 프리픽스 (0100에서 사용)
        public static string LogPrefix => IsTest ? "[TEST]" : "[REAL]";

        /// <summary>
        /// 프로그램 시작 시 딱 1회 호출. 이후 변경 금지.
        /// runMode: "TEST" or "REAL" (0001_real_test선택 결과)
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
                // ✅ TEST 고정값 (원하면 여기만 수정)
                DbPath = @"C:\c#\mydb_test.db";
                UserId = "cds002";
                Password = "hanjm12";
                Account = "55504613901";
            }
            else if (runMode == MODE_REAL)
            {
                // ✅ REAL 고정값 (원하면 여기만 수정)
                DbPath = @"C:\c#\mydb.db";
                UserId = "cds002";
                Password = "1908hanjm!!";
                Account = "00511723753";
            }
            else
            {
                throw new InvalidOperationException("알 수 없는 RunMode: " + runMode);
            }

            Console.WriteLine($"[0050] Apply OK mode={RunMode} db='{DbPath}' id='{UserId}' act='{Account}'");
            Debug.WriteLine($"[0050] Apply OK mode={RunMode} db='{DbPath}' id='{UserId}' act='{Account}'");
        }
    }
}

// 2026-02-02 91834
