using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;

namespace Exercise_1
{
    public static class AppLog
    {
        private static readonly object Sync = new object();
        private static string _path;
        private static string _fileName;
        private static bool _started;
        private static readonly List<string> PendingLines = new List<string>();

        public static string PathName
        {
            get
            {
                EnsureStarted();
                return _path;
            }
        }

        public static string FileName
        {
            get
            {
                EnsureStarted();
                return _fileName;
            }
        }

        public static void EnsureStarted()
        {
            if (_started) return;

            lock (Sync)
            {
                if (_started) return;
                if (!HasRunMode()) return;

                string startupPath = Application.StartupPath;
                string programFolderName = ResolveProgramFolderName(startupPath);
                string dateForFile = KoreaTime.DateForFile().Replace('-', '_');
                string fileStamp =
                    dateForFile + "_(" +
                    KoreaTime.KoreanDayOfWeek() + ")_" +
                    KoreaTime.TimeForFile();

                _fileName = programFolderName + "_" + fileStamp + ".log";

                string logDir = @"C:\c#\log";
                Directory.CreateDirectory(logDir);
                _path = System.IO.Path.Combine(logDir, _fileName);
                File.AppendAllText(_path, "", Encoding.UTF8);
                bool logFileExists = File.Exists(_path);

                File.AppendAllText(
                    _path,
                    "=== APP START ===" + Environment.NewLine +
                    "[LOG][TIMEZONE] LogTimeZone=KST UTC+09:00" + Environment.NewLine +
                    "[LOG][TIMEZONE] PC_LocalTime=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + Environment.NewLine +
                    "[LOG][TIMEZONE] KST_Now=" + KoreaTime.Timestamp() + Environment.NewLine +
                    "[LOG][TIMEZONE] FileName=" + _fileName + Environment.NewLine +
                    "LogFolder=" + logDir + @"\" + Environment.NewLine +
                    "LogFile=" + _path + Environment.NewLine +
                    "Exists=" + logFileExists.ToString() + Environment.NewLine,
                    Encoding.UTF8);

                if (PendingLines.Count > 0)
                {
                    File.AppendAllLines(_path, PendingLines.ToArray(), Encoding.UTF8);
                    PendingLines.Clear();
                }

                _started = true;
            }
        }

        public static void Info(string module, string message)
        {
            Write(module, "INFO", message);
        }

        public static void Warn(string module, string message)
        {
            Write(module, "WARN", message);
        }

        public static void Error(string module, string message)
        {
            Write(module, "ERR", message);
        }

        public static void Write(string module, string level, string message)
        {
            EnsureStarted();

            string line = string.Format(
                "[{0:HH:mm:ss.fff}][{1}][{2}] {3}",
                KoreaTime.NowKst(),
                string.IsNullOrWhiteSpace(module) ? "App" : module.Trim(),
                string.IsNullOrWhiteSpace(level) ? "INFO" : level.Trim(),
                message ?? "");

            lock (Sync)
            {
                try { System.Diagnostics.Debug.WriteLine(line); } catch { }
                if (!_started)
                {
                    PendingLines.Add(line);
                    return;
                }

                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
        }

        public static void AppendConsoleLine(string line)
        {
            EnsureStarted();

            lock (Sync)
            {
                if (!_started)
                {
                    PendingLines.Add(line ?? "");
                    return;
                }

                File.AppendAllText(_path, (line ?? "") + Environment.NewLine, Encoding.UTF8);
            }
        }

        private static bool HasRunMode()
        {
            try { return !string.IsNullOrWhiteSpace(_0050_Real_Test환경결정.RunMode); }
            catch { return false; }
        }

        private static string ResolveProgramFolderName(string startupPath)
        {
            var dir = new DirectoryInfo(startupPath);
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

        private static string ResolveMode()
        {
            try
            {
                return _0050_Real_Test환경결정.IsReal ? "REAL" : "TEST";
            }
            catch
            {
                return "TEST";
            }
        }
    }
}
