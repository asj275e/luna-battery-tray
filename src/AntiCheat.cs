// Best-effort detection of kernel-level game anti-cheat.
//
// Why this exists: a small unsigned process that periodically opens the mouse's vendor HID
// channel looks exactly like a macro / aim-assist tool to anti-cheat software, and Tencent's
// AntiCheatExpert (ACE) does kill such processes - observed on real hardware, where this tool
// died in the same 30-second window in which ACE loaded its kernel filter drivers.
// While anti-cheat is running the tray app therefore simply stops touching the device.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace LunaBatteryTray
{
    internal static class AntiCheat
    {
        // ACE-Tray.exe is deliberately NOT in this list: it stays resident long after the game
        // exits, so treating it as "active" would leave the tool paused forever.
        private static readonly string[] BuiltInNames = new string[]
        {
            // Tencent AntiCheatExpert (ACE / SGuard)
            "SGuard64", "SGuardSvc64", "SGuardUpdate64", "SGuard", "ACE-Service64", "ACE-BOOT",
            // Easy Anti-Cheat
            "EasyAntiCheat", "EasyAntiCheat_EOS", "EasyAntiCheat_EOS_Setup",
            // BattlEye
            "BEService", "BEService_x64", "BEService_lite", "BEClient_x64",
            // Riot Vanguard
            "vgc", "vgtray",
            // nProtect GameGuard
            "GameMon", "GameMon64",
            // XignCode3
            "XignCode", "xhunter1",
            // NetEase
            "NeacSafe", "NeacSafe64", "NeacService",
        };

        private static string[] _names;
        private static DateTime _namesStamp = DateTime.MinValue;

        /// <summary>
        /// Extra names may be listed in anticheat.txt next to the executable, one per line,
        /// '#' starts a comment, a trailing '*' matches any suffix.
        /// </summary>
        private static string[] Names
        {
            get
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "anticheat.txt");
                DateTime stamp = DateTime.MinValue;
                try { if (File.Exists(configPath)) stamp = File.GetLastWriteTimeUtc(configPath); } catch { }
                if (_names != null && stamp == _namesStamp) return _names;

                List<string> names = new List<string>(BuiltInNames);
                try
                {
                    if (File.Exists(configPath))
                    {
                        foreach (string raw in File.ReadAllLines(configPath))
                        {
                            string line = raw.Trim();
                            if (line.Length == 0 || line.StartsWith("#")) continue;
                            names.Add(line);
                        }
                    }
                }
                catch { }

                _names = names.ToArray();
                _namesStamp = stamp;
                return _names;
            }
        }

        /// <summary>Number of configured anti-cheat process names (diagnostics).</summary>
        public static int ConfiguredCount
        {
            get { return Names.Length; }
        }

        /// <summary>First few configured names (diagnostics).</summary>
        public static string ConfiguredPreview
        {
            get
            {
                string[] names = Names;
                int take = names.Length < 6 ? names.Length : 6;
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < take; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(names[i]);
                }
                return sb.ToString();
            }
        }

        /// <summary>Name of the anti-cheat process that is running, or null.</summary>
        public static string ActiveName()
        {
            Process[] processes;
            try { processes = Process.GetProcesses(); }
            catch { return null; }

            string[] names = Names;
            try
            {
                foreach (Process p in processes)
                {
                    string name;
                    try { name = p.ProcessName; }
                    catch { continue; }
                    if (string.IsNullOrEmpty(name)) continue;

                    foreach (string candidate in names)
                    {
                        if (candidate.Length == 0) continue;
                        if (candidate.EndsWith("*"))
                        {
                            if (name.StartsWith(candidate.Substring(0, candidate.Length - 1), StringComparison.OrdinalIgnoreCase))
                                return name;
                        }
                        else if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase))
                        {
                            return name;
                        }
                    }
                }
            }
            finally
            {
                foreach (Process p in processes)
                {
                    try { p.Dispose(); } catch { }
                }
            }
            return null;
        }

        public static bool IsActive()
        {
            return ActiveName() != null;
        }
    }
}
