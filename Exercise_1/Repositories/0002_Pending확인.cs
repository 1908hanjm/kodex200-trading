using System;
using System.Windows.Forms;

namespace Exercise_1
{
    // DEPRECATED: Pending policy removed. Kept only so old UI hooks compile.
    public class _0002_Pending확인
    {
        private readonly TextBox _pendingTextBox;

        public _0002_Pending확인(string connStr, TextBox textBox13)
        {
            _pendingTextBox = textBox13;
        }

        public void CheckAndShow()
        {
            if (_pendingTextBox != null)
            {
                string before = _pendingTextBox.Text ?? "";
                _pendingTextBox.Text = "없음";
                Console.WriteLine("[UI][PENDING]");
                Console.WriteLine("before=" + before);
                Console.WriteLine("after=없음");
                Console.WriteLine("reason=BOOT_PENDING_CHECK");
            }
        }
    }
}
