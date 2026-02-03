// 0001_real_test선택.cs
// ------------------------------------------------------------
// 역할:
// - 프로그램 기동 시 TEST / REAL 환경을 사용자에게 묻는다.
// - MessageBox를 통해 1회 선택만 수행한다.
// - 설정, 로그인, DB, XING 호출은 절대 하지 않는다.
// ------------------------------------------------------------

using System.Windows.Forms;

namespace Exercise_1
{
    public static class _0001_real_test선택
    {
        public const string MODE_TEST = "TEST";
        public const string MODE_REAL = "REAL";

        /// <summary>
        /// 기동 시 환경 선택
        /// [Yes]    TEST
        /// [No]     REAL
        /// [Cancel] 종료
        /// </summary>
        public static bool TryChoose(out string runMode)
        {
            runMode = null;

            DialogResult r = MessageBox.Show(
                "어떤 환경에서 거래하시겠습니까?\n\n" +
                "[예]    테스트(TEST)\n" +
                "[아니오] 실계좌(REAL)\n" +
                "[취소]  프로그램 종료",
                "거래 환경 선택",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question);

            if (r == DialogResult.Cancel)
                return false;

            runMode = (r == DialogResult.Yes)
                ? MODE_TEST
                : MODE_REAL;

            return true;
        }
    }
}
