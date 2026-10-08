// Shared logging for the tray app and the watchdog.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LunaBatteryTray
{
    internal static class AppLog
    {
        private static string _logDirectory;

        /// <summary>First writable of: %LOCALAPPDATA%\LunaBatteryTray, &lt;exe&gt;\logs, %TEMP%\LunaBatteryTray.</summary>
        public static string LogDirectory
        {
            get
            {
                if (_logDirectory != null) return _logDirectory;

                List<string> candidates = new List<string>();
                candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LunaBatteryTray"));
                try
                {
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    if (!string.IsNullOrEmpty(baseDir)) candidates.Add(Path.Combine(baseDir.TrimEnd('\\'), "logs"));
                }
                catch { }
                candidates.Add(Path.Combine(Path.GetTempPath(), "LunaBatteryTray"));

                foreach (string dir in candidates)
                {
                    try
                    {
                        System.IO.Directory.CreateDirectory(dir);
                        string probe = Path.Combine(dir, ".write-test");
                        System.IO.File.WriteAllText(probe, "ok");
                        System.IO.File.Delete(probe);
                        _logDirectory = dir;
                        return dir;
                    }
                    catch { }
                }
                return candidates[candidates.Count - 1];
            }
        }

        public static string PathIn(string fileName)
        {
            return Path.Combine(LogDirectory, fileName);
        }

        /// <summary>Tray-app log.</summary>
        public static void Log(string message)
        {
            Append("state.log", message);
        }

        /// <summary>Watchdog log (kept separate so the tray log stays readable).</summary>
        public static void LogWatchdog(string message)
        {
            Append("watchdog.log", message);
        }

        public static void Append(string fileName, string message)
        {
            try
            {
                string path = PathIn(fileName);
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine;
                FileInfo info = new FileInfo(path);
                if (info.Exists && info.Length > 262144) System.IO.File.Delete(path);
                System.IO.File.AppendAllText(path, line, Encoding.UTF8);
            }
            catch { }
        }

        public static bool Exists(string fileName)
        {
            try { return System.IO.File.Exists(PathIn(fileName)); }
            catch { return false; }
        }

        public static void Create(string fileName, string contents)
        {
            try { System.IO.File.WriteAllText(PathIn(fileName), contents, Encoding.UTF8); }
            catch { }
        }

        public static void Remove(string fileName)
        {
            try { if (System.IO.File.Exists(PathIn(fileName))) System.IO.File.Delete(PathIn(fileName)); }
            catch { }
        }
    }
}
