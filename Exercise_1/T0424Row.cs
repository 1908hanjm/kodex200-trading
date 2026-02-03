// 종목별 잔고 1행을 담는 POCO
using System.Windows.Forms;

public partial class Login : Form
{
    // ← Login 클래스 내부에서만 쓸 거라면
    private sealed class T0424Row
    {
        public string ExpCode { get; set; }
        public string Jangb { get; set; }
        public long JanQty { get; set; }
        public long MdposQt { get; set; }
        public double Pamt { get; set; }
        public double Mamt { get; set; }
        public double DtsUnik { get; set; }
    }

    private void InitializeComponent()
    {
            this.SuspendLayout();
            // 
            // Login
            // 
            this.ClientSize = new System.Drawing.Size(916, 453);
            this.Name = "Login";
            this.ResumeLayout(false);

    }
}
