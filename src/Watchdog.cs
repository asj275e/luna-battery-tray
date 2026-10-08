// LunaBatteryTray.Watchdog
//
// Runs every five minutes from a Windows scheduled task and restarts the tray app when it is
// gone. It deliberately does NOT restart it while game anti-cheat is running (the app would be
// killed again, and repeatedly poking at a protected game is pointless) and it respects a
// deliberate "Quit" from the tray menu.
//
//   LunaBatteryTray.Watchdog.exe              the check the scheduled task runs
//   LunaBatteryTray.Watchdog.exe --install    register the scheduled task
//   LunaBatteryTray.Watchdog.exe --uninstall  remove it
//   LunaBatteryTray.Watchdog.exe --status     show the task state
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LunaBatteryTray
{
    internal static class Watchdog
    {
        private const string TaskName = "LunaBatteryTray Watchdog";
        private const int CheckIntervalMinutes = 5;
        private const string TrayExeName = "LunaBatteryTray.exe";
        private const string QuitFlagFile = "user-quit.flag";

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);

        private static string ExeDir
        {
            get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); }
        }

        private static string TrayExePath
        {
            get { return Path.Combine(ExeDir, TrayExeName); }
        }

        private static void TryAttachConsole()
        {
            AttachConsole(-1);
            try
            {
                StreamWriter writer = new StreamWriter(Console.OpenStandardOutput());
                writer.AutoFlush = true;
                Console.SetOut(writer);
            }
            catch { }
        }

        private static void Say(string message)
        {
            Console.WriteLine(message);
            AppLog.LogWatchdog(message);
        }

        [STAThread]
        private static int Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "--check";

            if (mode == "--install" || mode == "--uninstall" || mode == "--status")
                TryAttachConsole();

            switch (mode)
            {
                case "--install": return Install();
                case "--uninstall": return Uninstall();
                case "--status": return Status();
                case "--detect": return Detect();
                case "--check": return Check();
                default:
                    TryAttachConsole();
                    Console.WriteLine("LunaBatteryTray.Watchdog");
                    Console.WriteLine("  (no arguments)  run one check and exit - this is what the scheduled task does");
                    Console.WriteLine("  --install       register the '" + TaskName + "' scheduled task (" + CheckIntervalMinutes + " min)");
                    Console.WriteLine("  --uninstall     remove it");
                    Console.WriteLine("  --status        show whether it is registered");
                    Console.WriteLine("  --detect        show what anti-cheat detection currently sees");
                    return 0;
            }
        }

        /// <summary>Diagnostics: what the anti-cheat detector can actually see right now.</summary>
        private static int Detect()
        {
            TryAttachConsole();
            Process[] processes;
            try { processes = Process.GetProcesses(); }
            catch (Exception ex)
            {
                Console.WriteLine("Process.GetProcesses() threw: " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }

            Console.WriteLine("processes visible : " + processes.Length);
            int unreadable = 0;
            System.Collections.Generic.List<string> seen = new System.Collections.Generic.List<string>();
            foreach (Process p in processes)
            {
                try { seen.Add(p.ProcessName); }
                catch { unreadable++; }
            }
            seen.Sort();
            Console.WriteLine("names unreadable  : " + unreadable);
            Console.WriteLine("names visible     : " + string.Join(", ", seen.ToArray()));
            Console.WriteLine("names configured  : " + AntiCheat.ConfiguredCount + " (" + AntiCheat.ConfiguredPreview + " ...)");
            string active = AntiCheat.ActiveName();
            Console.WriteLine("ActiveName()      : " + (active == null ? "(null - no anti-cheat seen)" : active));
            return 0;
        }

        // ---------------------------------------------------------------- the actual check
        private static int Check()
        {
            if (!File.Exists(TrayExePath))
            {
                Say("tray exe missing: " + TrayExePath);
                return 1;
            }

            if (IsTrayRunning())
            {
                Say("ok, tray already running");
                return 0;
            }

            string antiCheat = AntiCheat.ActiveName();
            if (antiCheat != null)
            {
                Say("skipped: anti-cheat is running (" + antiCheat + "), not restarting");
                return 0;
            }

            if (AppLog.Exists(QuitFlagFile))
            {
                Say("skipped: the tray was quit from its menu (" + QuitFlagFile + " present)");
                return 0;
            }

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(TrayExePath);
                psi.WorkingDirectory = ExeDir;
                psi.UseShellExecute = true;
                Process.Start(psi);
                Say("restarted the tray app");
                return 0;
            }
            catch (Exception ex)
            {
                Say("restart failed: " + ex.Message);
                return 1;
            }
        }

        private static bool IsTrayRunning()
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(TrayExeName)); }
            catch { return false; }

            try
            {
                for (int i = 0; i < processes.Length; i++)
                {
                    try
                    {
                        // the watchdog itself is never named like the tray exe, so a name hit is the app
                        if (processes[i].Id != Process.GetCurrentProcess().Id) return true;
                    }
                    catch { }
                }
            }
            finally
            {
                foreach (Process p in processes)
                {
                    try { p.Dispose(); } catch { }
                }
            }
            return false;
        }

        // ---------------------------------------------------------------- scheduled task
        private static int Install()
        {
            // written next to the log: that directory is probed for writability, which %TEMP%
            // or the install directory are not guaranteed to be
            string xmlPath = AppLog.PathIn("watchdog-task.xml");
            try
            {
                File.WriteAllText(xmlPath, BuildTaskXml(), Encoding.Unicode);
            }
            catch (Exception ex)
            {
                Say("could not write the task definition: " + ex.Message);
                return 1;
            }

            string output;
            int code = RunSchTasks("/Create /TN \"" + TaskName + "\" /XML \"" + xmlPath + "\" /F", out output);
            try { File.Delete(xmlPath); } catch { }

            Say(output.Trim());
            if (code == 0)
            {
                Say("installed: '" + TaskName + "' runs every " + CheckIntervalMinutes + " minutes");
            }
            else
            {
                Say("install failed (schtasks exit " + code + ")");
                Say("if the message above says access denied, right-click the install .cmd and pick 'Run as administrator'");
            }
            return code;
        }

        private static int Uninstall()
        {
            string output;
            int code = RunSchTasks("/Delete /TN \"" + TaskName + "\" /F", out output);
            Say(output.Trim());
            Say(code == 0 ? "removed: '" + TaskName + "'" : "nothing removed (schtasks exit " + code + ")");
            return code;
        }

        private static int Status()
        {
            string output;
            int code = RunSchTasks("/Query /TN \"" + TaskName + "\" /FO LIST", out output);
            Say(output.Trim());
            if (code != 0) Say("'" + TaskName + "' is not registered");
            return 0;
        }

        private static int RunSchTasks(string arguments, out string output)
        {
            string outFile = AppLog.PathIn("schtasks-output.txt");
            try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }

            // output goes to a file rather than a pipe: pipes are not always usable in
            // confined environments, and schtasks writes its messages in the OEM code page
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe",
                "/c schtasks " + arguments + " > \"" + outFile + "\" 2>&1");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = ExeDir;

            int code;
            try
            {
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit(30000);
                    code = p.HasExited ? p.ExitCode : -1;
                }
            }
            catch (Exception ex)
            {
                output = "could not run schtasks: " + ex.Message;
                return -1;
            }

            try { output = File.Exists(outFile) ? File.ReadAllText(outFile, Encoding.Default) : ""; }
            catch { output = ""; }
            try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
            return code;
        }

        private static string Esc(string value)
        {
            return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        private static string BuildTaskXml()
        {
            string start = DateTime.Now.AddMinutes(1).ToString("yyyy-MM-dd'T'HH:mm:ss");
            // TimeSpan.MaxValue means "repeat indefinitely"
            const string forever = "P10675199DT2H48M5.4775807S";

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
            sb.AppendLine("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">");
            sb.AppendLine("  <RegistrationInfo>");
            sb.AppendLine("    <Description>Restarts LunaBatteryTray if it is not running. Skips while game anti-cheat is active or after a deliberate quit.</Description>");
            sb.AppendLine("  </RegistrationInfo>");
            sb.AppendLine("  <Triggers>");
            sb.AppendLine("    <TimeTrigger>");
            sb.AppendLine("      <StartBoundary>" + start + "</StartBoundary>");
            sb.AppendLine("      <Enabled>true</Enabled>");
            sb.AppendLine("      <Repetition>");
            sb.AppendLine("        <Interval>PT" + CheckIntervalMinutes + "M</Interval>");
            sb.AppendLine("        <Duration>" + forever + "</Duration>");
            sb.AppendLine("        <StopAtDurationEnd>false</StopAtDurationEnd>");
            sb.AppendLine("      </Repetition>");
            sb.AppendLine("    </TimeTrigger>");
            sb.AppendLine("  </Triggers>");
            sb.AppendLine("  <Principals>");
            sb.AppendLine("    <Principal id=\"Author\">");
            sb.AppendLine("      <LogonType>InteractiveToken</LogonType>");
            sb.AppendLine("      <RunLevel>LeastPrivilege</RunLevel>");
            sb.AppendLine("    </Principal>");
            sb.AppendLine("  </Principals>");
            sb.AppendLine("  <Settings>");
            sb.AppendLine("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
            sb.AppendLine("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
            sb.AppendLine("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
            sb.AppendLine("    <AllowHardTerminate>true</AllowHardTerminate>");
            sb.AppendLine("    <StartWhenAvailable>true</StartWhenAvailable>");
            sb.AppendLine("    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>");
            sb.AppendLine("    <IdleSettings>");
            sb.AppendLine("      <StopOnIdleEnd>false</StopOnIdleEnd>");
            sb.AppendLine("      <RestartOnIdle>false</RestartOnIdle>");
            sb.AppendLine("    </IdleSettings>");
            sb.AppendLine("    <AllowStartOnDemand>true</AllowStartOnDemand>");
            sb.AppendLine("    <Enabled>true</Enabled>");
            sb.AppendLine("    <Hidden>true</Hidden>");
            sb.AppendLine("    <RunOnlyIfIdle>false</RunOnlyIfIdle>");
            sb.AppendLine("    <WakeToRun>false</WakeToRun>");
            sb.AppendLine("    <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>");
            sb.AppendLine("    <Priority>7</Priority>");
            sb.AppendLine("  </Settings>");
            sb.AppendLine("  <Actions Context=\"Author\">");
            sb.AppendLine("    <Exec>");
            sb.AppendLine("      <Command>" + Esc(Path.Combine(ExeDir, "LunaBatteryTray.Watchdog.exe")) + "</Command>");
            sb.AppendLine("    </Exec>");
            sb.AppendLine("  </Actions>");
            sb.AppendLine("</Task>");
            return sb.ToString();
        }
    }
}
