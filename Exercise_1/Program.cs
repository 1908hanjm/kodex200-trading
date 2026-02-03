// 프로그램 시작시 먼저 뜨는 프레임 변경 finish
// form1 -> delete


using System;
using System.Data.SQLite;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace Exercise_1
{
    static class Program
    {
        /// <summary>
        /// 해당 응용 프로그램의 주 진입점입니다.
        /// </summary>
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Login());
        }
    }
}
