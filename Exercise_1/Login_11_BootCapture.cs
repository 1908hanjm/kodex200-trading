using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Exercise_1
{
    public partial class Login
    {
        private int _bootCaptureScheduled = 0;
        private int _bootCaptureStarted = 0;

        private void ScheduleBootCaptureOnce()
        {
            if (Interlocked.Exchange(ref _bootCaptureScheduled, 1) == 1)
                return;

            DateTime kstNow = KoreaTime.NowKst();
            Console.WriteLine("[BOOT_CAPTURE] KST_NOW=" + kstNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            Console.WriteLine("[BOOT_CAPTURE] Capture scheduled at 15:32:00 KST");

            _ = RunBootCaptureAt1532Async();
        }

        private async Task RunBootCaptureAt1532Async()
        {
            try
            {
                DateTime nowKst = KoreaTime.NowKst();
                DateTime targetKst = new DateTime(nowKst.Year, nowKst.Month, nowKst.Day, 15, 32, 0);
                if (nowKst > targetKst)
                    targetKst = targetKst.AddDays(1);

                TimeSpan delay = targetKst - nowKst;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay).ConfigureAwait(true);

                if (Interlocked.Exchange(ref _bootCaptureStarted, 1) == 1)
                    return;

                Console.WriteLine("==============================");
                Console.WriteLine("[BOOT_CAPTURE] 장종료 했어요.");
                Console.WriteLine("[BOOT_CAPTURE] Capture START");
                Console.WriteLine("==============================");

                CaptureBootFinalState();

                Console.WriteLine("[BOOT_CAPTURE] Capture SUCCESS");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[BOOT_CAPTURE] Capture FAILED");
                Console.WriteLine("reason=" + ex.Message);
            }
        }

        private void CaptureBootFinalState()
        {
            string captureDir = @"C:\c#\cap";
            Directory.CreateDirectory(captureDir);

            string screenPath = ResolveNextCapturePath(captureDir);

            CaptureScreenImage(screenPath);

            Console.WriteLine("[BOOT_CAPTURE] screen=" + screenPath);
        }

        private string ResolveNextCapturePath(string captureDir)
        {
            string programFolderName = ResolveCaptureProgramFolderName();
            string datePart = KoreaTime.NowKst().ToString("yyyy_MM_dd", CultureInfo.InvariantCulture);
            string prefix = programFolderName + "_" + datePart + "_";
            int nextNo = 1;

            foreach (string file in Directory.GetFiles(captureDir, prefix + "*_F.png"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (name == null || name.Length < prefix.Length + 5)
                    continue;

                string seqText = name.Substring(prefix.Length, 3);
                int seq;
                if (int.TryParse(seqText, NumberStyles.None, CultureInfo.InvariantCulture, out seq) && seq >= nextNo)
                    nextNo = seq + 1;
            }

            string fileName = prefix + nextNo.ToString("000", CultureInfo.InvariantCulture) + "_F.png";
            return Path.Combine(captureDir, fileName);
        }

        private string ResolveCaptureProgramFolderName()
        {
            var dir = new DirectoryInfo(Application.StartupPath);
            if (dir.Parent != null &&
                dir.Parent.Parent != null &&
                dir.Parent.Parent.Parent != null &&
                string.Equals(dir.Parent.Name, "bin", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(dir.Name, "Debug", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(dir.Name, "Release", StringComparison.OrdinalIgnoreCase)))
            {
                return dir.Parent.Parent.Parent.Name;
            }

            return dir.Name;
        }

        private void CaptureScreenImage(string outputPath)
        {
            Rectangle bounds = Rectangle.Empty;
            foreach (Screen screen in Screen.AllScreens)
            {
                bounds = bounds == Rectangle.Empty
                    ? screen.Bounds
                    : Rectangle.Union(bounds, screen.Bounds);
            }

            if (bounds == Rectangle.Empty)
                bounds = Screen.PrimaryScreen.Bounds;

            using (Bitmap bitmap = new Bitmap(bounds.Width, bounds.Height))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                bitmap.Save(outputPath, ImageFormat.Png);
            }
        }
    }
}
