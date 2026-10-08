// LunaBatteryTray - shows the battery level of a Lunafury LUNA TYPE33 wireless mouse
// in the Windows notification area (system tray).
//
// Protocol (verified on real hardware, VID 0x373E, vendor HID interface usage page 0xFFFF,
// 65-byte feature report):
//   write feature report: 00 00 00 02 02 00 83 00 ...
//   read  feature report: 00 A1 00 02 02 00 83 <charging> <battery> ...
//   A0 in byte 1 means the radio link is not up (no reading available).
//
// Build: csc.exe /target:winexe /out:LunaBatteryTray.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll LunaBatteryTray.cs

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LunaBatteryTray
{
    internal static class Native
    {
        // ---- setupapi / hid ----
        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, int Flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr hDevInfo, IntPtr devInfo, ref Guid interfaceClassGuid, int memberIndex, ref SP_DEVICE_INTERFACE_DATA did);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr hDevInfo, ref SP_DEVICE_INTERFACE_DATA did, IntPtr detail, int detailSize, out int required, IntPtr devInfo);
        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr hDevInfo);

        [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll")] public static extern bool HidD_GetAttributes(IntPtr h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr pp);
        [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr pp);
        [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr pp, IntPtr caps);
        [DllImport("hid.dll")] public static extern bool HidD_SetFeature(IntPtr h, byte[] buf, int len);
        [DllImport("hid.dll")] public static extern bool HidD_GetFeature(IntPtr h, byte[] buf, int len);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr templ);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr h);

        // ---- ui ----
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr handle);
        [DllImport("kernel32.dll")] public static extern bool AttachConsole(int processId);

        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 1;
        public const uint FILE_SHARE_WRITE = 2;
        public const uint OPEN_EXISTING = 3;
        public const int DIGCF_PRESENT = 0x02;
        public const int DIGCF_DEVICEINTERFACE = 0x10;
        public const int SM_CXSMICON = 49;
        public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
    }

    internal sealed class MouseReading
    {
        public bool DevicePresent;      // a vendor interface answered / was found
        public bool LinkUp;             // the radio link reported a real value
        public int Battery = -1;        // 0..100, -1 when unknown
        public bool Charging;
        public string ProductName = "";
        public int BatterySourcePid;

        public string LevelText
        {
            get
            {
                if (!DevicePresent) return "--";
                if (Battery < 0) return "?";
                return Battery.ToString(CultureInfo.InvariantCulture);
            }
        }

        public string StatusText
        {
            get
            {
                if (!DevicePresent) return "未检测到";
                if (Battery < 0) return "无线未连接";
                if (Charging) return Battery + "% 充电中";
                return Battery + "%";
            }
        }
    }

    internal static class LunaHid
    {
        private const ushort LUNA_VID = 0x373E;
        private const ushort VENDOR_USAGE_PAGE = 0xFFFF;
        private const int REPORT_LENGTH = 65;

        private static readonly byte[] QueryPrefix = new byte[] { 0x00, 0x00, 0x00, 0x02, 0x02, 0x00, 0x83 };

        private static List<string> EnumerateDevicePaths()
        {
            List<string> result = new List<string>();
            Guid hidGuid;
            Native.HidD_GetHidGuid(out hidGuid);

            IntPtr set = Native.SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
                Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
            if (set == Native.INVALID_HANDLE_VALUE) return result;

            try
            {
                int index = 0;
                while (true)
                {
                    Native.SP_DEVICE_INTERFACE_DATA did = new Native.SP_DEVICE_INTERFACE_DATA();
                    did.cbSize = Marshal.SizeOf(typeof(Native.SP_DEVICE_INTERFACE_DATA));
                    if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref did)) break;
                    index++;

                    int required = 0;
                    Native.SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out required, IntPtr.Zero);
                    if (required <= 0) continue;

                    IntPtr buffer = Marshal.AllocHGlobal(required);
                    try
                    {
                        Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                        if (Native.SetupDiGetDeviceInterfaceDetail(set, ref did, buffer, required, out required, IntPtr.Zero))
                        {
                            string path = Marshal.PtrToStringUni(new IntPtr(buffer.ToInt64() + 4));
                            if (!string.IsNullOrEmpty(path)) result.Add(path);
                        }
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }

            return result;
        }

        private static IntPtr OpenPath(string path)
        {
            return Native.CreateFile(path,
                Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        }

        /// <summary>Reads the battery once, scanning every Luna vendor HID interface.</summary>
        public static MouseReading Read()
        {
            MouseReading best = new MouseReading();
            string[] paths = EnumerateDevicePaths().ToArray();

            foreach (string path in paths)
            {
                IntPtr handle = OpenPath(path);
                if (handle == Native.INVALID_HANDLE_VALUE) continue;

                try
                {
                    Native.HIDD_ATTRIBUTES attrs = new Native.HIDD_ATTRIBUTES();
                    attrs.Size = Marshal.SizeOf(typeof(Native.HIDD_ATTRIBUTES));
                    if (!Native.HidD_GetAttributes(handle, ref attrs)) continue;
                    if (attrs.VendorID != LUNA_VID) continue;

                    // only the vendor-defined collection carries the command channel
                    ushort usagePage = 0;
                    ushort featureLength = REPORT_LENGTH;
                    IntPtr preparsed = IntPtr.Zero;
                    if (Native.HidD_GetPreparsedData(handle, out preparsed))
                    {
                        IntPtr caps = Marshal.AllocHGlobal(512);
                        try
                        {
                            if (Native.HidP_GetCaps(preparsed, caps) == 0x110000)
                            {
                                usagePage = (ushort)Marshal.ReadInt16(caps, 2);
                                ushort fl = (ushort)Marshal.ReadInt16(caps, 8);
                                if (fl >= REPORT_LENGTH) featureLength = fl;
                            }
                        }
                        finally { Marshal.FreeHGlobal(caps); }
                        Native.HidD_FreePreparsedData(preparsed);
                    }

                    if (usagePage != VENDOR_USAGE_PAGE) continue;

                    best.DevicePresent = true;
                    if (best.BatterySourcePid == 0) best.BatterySourcePid = attrs.ProductID;

                    byte[] command = new byte[featureLength];
                    Array.Copy(QueryPrefix, command, QueryPrefix.Length);

                    byte[] response = new byte[featureLength];
                    bool gotAnswer = false;
                    // Timing and both reply layouts mirror the vendor's own web driver
                    // (output.xvi3.min.js getBatPer(): send, sleep 200ms, read).
                    int[] settleDelays = new int[] { 200, 250, 400 };
                    for (int attempt = 0; attempt < settleDelays.Length && !gotAnswer; attempt++)
                    {
                        if (!Native.HidD_SetFeature(handle, command, command.Length)) break;
                        Thread.Sleep(settleDelays[attempt]);

                        response[0] = 0x00;
                        if (!Native.HidD_GetFeature(handle, response, response.Length)) continue;
                        if (response.Length < 10) continue;

                        int level;
                        int charging;
                        if (response[1] == 0xA1 || response[1] == 0xA2)
                        {
                            // report id kept in byte 0 (the layout this tool reads on Windows)
                            if (response[4] != 0x02 || response[6] != 0x83) continue;
                            charging = response[7];
                            level = response[8];
                        }
                        else if (response[0] == 0xA1 || response[0] == 0xA2)
                        {
                            // report id stripped (WebHID on some platforms returns this)
                            if (response[3] != 0x02 || response[5] != 0x83) continue;
                            charging = response[6];
                            level = response[7];
                        }
                        else continue;

                        if (level < 0 || level > 100) continue;

                        gotAnswer = true;
                        if (level > 0)
                        {
                            // a real reading wins over a silent link (0xA0 = radio link not up)
                            best.Battery = level;
                            best.Charging = charging != 0;
                            best.LinkUp = true;
                            best.BatterySourcePid = attrs.ProductID;
                        }
                    }
                }
                finally { Native.CloseHandle(handle); }
            }

            return best;
        }
    }

    internal static class IconPainter
    {
        public static int TrayIconSize
        {
            get
            {
                int size = Native.GetSystemMetrics(Native.SM_CXSMICON);
                if (size < 16) size = 16;
                return size;
            }
        }

        public static Bitmap Render(MouseReading reading, int size)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                Color background = Color.FromArgb(0x4A, 0x4A, 0x4A);
                if (reading.DevicePresent)
                {
                    if (reading.Battery < 0) background = Color.FromArgb(0x7A, 0x6A, 0xB0);
                    else if (reading.Charging) background = Color.FromArgb(0x1F, 0x7A, 0xE0);
                    else if (reading.Battery >= 50) background = Color.FromArgb(0x2F, 0xA8, 0x44);
                    else if (reading.Battery >= 20) background = Color.FromArgb(0xE0, 0x9A, 0x22);
                    else background = Color.FromArgb(0xD8, 0x3A, 0x2E);
                }

                float radius = Math.Max(2f, size * 0.22f);
                using (GraphicsPath path = RoundedRect(new RectangleF(0, 0, size, size), radius))
                using (SolidBrush fill = new SolidBrush(background))
                    g.FillPath(fill, path);

                string text = reading.LevelText;
                float fontSize;
                if (text.Length >= 3) fontSize = size * 0.48f;
                else if (text.Length == 2) fontSize = size * 0.70f;
                else fontSize = size * 0.78f;

                // only large icons have room for the charging bolt without stealing digit space
                bool withBolt = reading.Charging && reading.Battery >= 0 && size >= 24;
                float textShiftX = withBolt ? -size * 0.10f : 0f;
                float textShiftY = withBolt ? -size * 0.08f : 0f;

                using (Font font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                using (StringFormat format = new StringFormat())
                {
                    format.Alignment = StringAlignment.Center;
                    format.LineAlignment = StringAlignment.Center;
                    format.FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
                    format.Trimming = StringTrimming.None;

                    RectangleF layout = new RectangleF(textShiftX, textShiftY, size, size);
                    using (SolidBrush shadow = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
                        g.DrawString(text, font, shadow, new RectangleF(layout.X, layout.Y + 0.8f, layout.Width, layout.Height), format);
                    using (SolidBrush ink = new SolidBrush(Color.White))
                        g.DrawString(text, font, ink, layout, format);
                }

                if (withBolt) DrawBolt(g, size);
            }
            return bmp;
        }

        private static void DrawBolt(Graphics g, int size)
        {
            float boxW = size * 0.34f;
            float boxH = size * 0.40f;
            float ox = size - boxW - size * 0.08f;
            float oy = size - boxH - size * 0.05f;
            float sx = boxW / 10f;
            float sy = boxH / 14f;

            PointF[] unit = new PointF[]
            {
                new PointF(6.6f, 0f),
                new PointF(0f, 8.6f),
                new PointF(3.9f, 8.6f),
                new PointF(2.6f, 14f),
                new PointF(10f, 5.2f),
                new PointF(5.5f, 5.2f)
            };

            PointF[] points = new PointF[unit.Length];
            for (int i = 0; i < unit.Length; i++)
                points[i] = new PointF(ox + unit[i].X * sx, oy + unit[i].Y * sy);

            using (GraphicsPath outline = new GraphicsPath())
            {
                PointF[] shifted = new PointF[unit.Length];
                for (int i = 0; i < unit.Length; i++)
                    shifted[i] = new PointF(points[i].X + size * 0.05f, points[i].Y + size * 0.05f);
                outline.AddPolygon(shifted);
                using (SolidBrush halo = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
                    g.FillPath(halo, outline);
            }

            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddPolygon(points);
                using (SolidBrush yellow = new SolidBrush(Color.FromArgb(0xFF, 0xE0, 0x4A)))
                    g.FillPath(yellow, path);
            }
        }

        private static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = radius * 2f;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "LunaBatteryTray";

        private readonly NotifyIcon _icon;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _header;
        private readonly ToolStripMenuItem _autoStart;
        private readonly ToolStripMenuItem _refresh;
        private MouseReading _reading = new MouseReading();
        private int _intervalSeconds = 30;

        public TrayContext(int intervalSeconds)
        {
            _intervalSeconds = intervalSeconds;
            _reading = new MouseReading();

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.ShowImageMargin = false;
            // Records how the menu went away (ItemClicked / clicked away / Esc). This is the clue
            // that tells a deliberate Quit apart from the app being ended from the outside.
            menu.Closed += delegate(object sender, ToolStripDropDownClosedEventArgs e)
            {
                Log("menu closed: " + e.CloseReason);
            };

            _header = new ToolStripMenuItem("读取中…");
            _header.Enabled = false;
            menu.Items.Add(_header);
            menu.Items.Add(new ToolStripSeparator());

            _refresh = new ToolStripMenuItem("立即刷新");
            _refresh.Click += delegate { Log("menu item: 立即刷新"); RefreshNow(); };
            menu.Items.Add(_refresh);

            _autoStart = new ToolStripMenuItem("开机自启");
            _autoStart.CheckOnClick = false;
            _autoStart.Click += delegate { Log("menu item: 开机自启"); ToggleAutoStart(); };
            menu.Items.Add(_autoStart);

            ToolStripMenuItem intervalMenu = new ToolStripMenuItem("刷新间隔");
            int[] choices = new int[] { 15, 30, 60, 300 };
            string[] labels = new string[] { "15 秒", "30 秒", "1 分钟", "5 分钟" };
            for (int i = 0; i < choices.Length; i++)
            {
                int value = choices[i];
                ToolStripMenuItem item = new ToolStripMenuItem(labels[i]);
                item.Checked = (value == _intervalSeconds);
                ToolStripMenuItem captured = item;
                captured.Click += delegate
                {
                    Log("menu item: 刷新间隔 " + value + " 秒");
                    _intervalSeconds = value;
                    foreach (ToolStripItem entry in intervalMenu.DropDownItems)
                    {
                        ToolStripMenuItem mi = entry as ToolStripMenuItem;
                        if (mi != null) mi.Checked = (mi == captured);
                    }
                    _timer.Interval = _intervalSeconds * 1000;
                    RefreshNow();
                };
                intervalMenu.DropDownItems.Add(item);
            }
            menu.Items.Add(intervalMenu);

            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem about = new ToolStripMenuItem("关于 / 设备信息");
            about.Click += delegate { Log("menu item: 关于"); ShowAbout(); };
            menu.Items.Add(about);
            ToolStripMenuItem quit = new ToolStripMenuItem("退出");
            quit.Click += delegate
            {
                Log("menu item: 退出 -> ExitThread");
                ExitThread();
            };
            menu.Items.Add(quit);

            _icon = new NotifyIcon();
            _icon.Visible = true;
            _icon.MouseUp += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right) ShowTrayMenu();
            };
            _icon.MouseDoubleClick += delegate { RefreshNow(); };
            _icon.Text = "LUNA TYPE33 电量";
            _menu = menu;

            _autoStart.Checked = IsAutoStartEnabled();

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = _intervalSeconds * 1000;
            _timer.Tick += delegate { RefreshNow(); };
            _timer.Start();

            RefreshNow();
        }

        /// <summary>
        /// Shows the context menu so that the pointer never ends up on top of a menu item.
        /// Letting NotifyIcon position it (the default) opens the menu upwards at the tray,
        /// which parks the pointer exactly on the last item ("退出") - a right-click followed
        /// by any stray click would quit the app, which read as "right-clicking makes it vanish".
        /// </summary>
        private void ShowTrayMenu()
        {
            Point pointer = Cursor.Position;
            Rectangle work = Screen.FromPoint(pointer).WorkingArea;
            Size size = _menu.GetPreferredSize(Size.Empty);

            int x = pointer.X - size.Width + 12;
            if (x < work.Left) x = work.Left;
            if (x + size.Width > work.Right) x = work.Right - size.Width;

            // prefer opening entirely above the pointer; the taskbar keeps the menu in the work area
            int y = pointer.Y - size.Height - 8;
            if (y < work.Top) y = pointer.Y + 8;
            if (y + size.Height > work.Bottom) y = work.Bottom - size.Height;
            if (y < work.Top) y = work.Top;

            Rectangle menuBounds = new Rectangle(x, y, size.Width, size.Height);
            if (menuBounds.Contains(pointer))
            {
                // last resort: push the menu clear of the pointer
                int above = pointer.Y - menuBounds.Height - 8;
                if (above >= work.Top) y = above;
                else if (pointer.Y + 8 + size.Height <= work.Bottom) y = pointer.Y + 8;
            }

            _menu.Show(new Point(x, y));
            Log("menu shown at (" + x + "," + y + ") size " + size.Width + "x" + size.Height
                + " pointer (" + pointer.X + "," + pointer.Y + ") work " + work
                + " pointerInsideMenu=" + new Rectangle(x, y, size.Width, size.Height).Contains(pointer));
        }

        private void RefreshNow()
        {
            try
            {
                _reading = LunaHid.Read();
            }
            catch (Exception ex)
            {
                Log("read failed: " + ex.Message);
            }
            UpdateTray();
        }

        private void UpdateTray()
        {
            MouseReading r = _reading;

            IntPtr hicon = IntPtr.Zero;
            try
            {
                using (Bitmap bmp = IconPainter.Render(r, IconPainter.TrayIconSize))
                {
                    hicon = bmp.GetHicon();
                    using (Icon temp = Icon.FromHandle(hicon))
                    {
                        Icon previous = _icon.Icon;
                        _icon.Icon = (Icon)temp.Clone();
                        if (previous != null) previous.Dispose();
                    }
                }
            }
            finally
            {
                if (hicon != IntPtr.Zero) Native.DestroyIcon(hicon);
            }

            string tooltip;
            if (!r.DevicePresent) tooltip = "LUNA TYPE33：未检测到";
            else if (r.Battery < 0) tooltip = "LUNA TYPE33：无线未连接";
            else if (r.Charging) tooltip = "LUNA TYPE33：电量 " + r.Battery + "%（充电中）";
            else tooltip = "LUNA TYPE33：电量 " + r.Battery + "%";
            if (tooltip.Length > 63) tooltip = tooltip.Substring(0, 63);
            _icon.Text = tooltip;

            _header.Text = tooltip;
            Log("state: " + tooltip + (r.BatterySourcePid != 0 ? " [PID 0x" + r.BatterySourcePid.ToString("X4") + "]" : ""));
        }

        private void ShowAbout()
        {
            string text = "LunaBatteryTray\r\n\r\n"
                + "显示 Lunafury LUNA TYPE33 无线鼠标的电量。\r\n"
                + "协议：VID 0x373E，usage page 0xFFFF 的 65 字节 feature 报文。\r\n\r\n"
                + "当前状态：" + _reading.StatusText + "\r\n"
                + "刷新间隔：" + _intervalSeconds + " 秒\r\n"
                + "日志：\r\n" + LogPath;
            MessageBox.Show(text, "LUNA TYPE33 电量", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public static bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null) return false;
                    return key.GetValue(RunValueName) != null;
                }
            }
            catch { return false; }
        }

        public static void SetAutoStart(bool enabled, int intervalSeconds)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (key == null) throw new InvalidOperationException("无法打开 HKCU\\...\\Run 注册表项");
                if (enabled)
                    key.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\" --interval " + intervalSeconds);
                else
                    key.DeleteValue(RunValueName, false);
            }
        }

        private void ToggleAutoStart()
        {
            try
            {
                bool enable = !IsAutoStartEnabled();
                SetAutoStart(enable, _intervalSeconds);
                _autoStart.Checked = enable;
            }
            catch (Exception ex)
            {
                MessageBox.Show("设置开机自启失败：" + ex.Message, "LUNA TYPE33 电量");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Stop();
                _timer.Dispose();
                _icon.Visible = false;
                _icon.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---- logging ----
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
                    string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
                    if (!string.IsNullOrEmpty(exeDir)) candidates.Add(Path.Combine(exeDir, "logs"));
                }
                catch { }
                candidates.Add(Path.Combine(Path.GetTempPath(), "LunaBatteryTray"));

                foreach (string dir in candidates)
                {
                    try
                    {
                        Directory.CreateDirectory(dir);
                        string probe = Path.Combine(dir, ".write-test");
                        File.WriteAllText(probe, "ok");
                        File.Delete(probe);
                        _logDirectory = dir;
                        return dir;
                    }
                    catch { }
                }
                return candidates[candidates.Count - 1];
            }
        }

        public static string LogPath
        {
            get { return Path.Combine(LogDirectory, "state.log"); }
        }

        public static void Log(string message)
        {
            try
            {
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine;
                FileInfo info = new FileInfo(LogPath);
                if (info.Exists && info.Length > 262144) File.Delete(LogPath);
                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }
            catch { }
        }
    }

    internal static class Program
    {
        private static void TryAttachStdOut()
        {
            try
            {
                StreamWriter writer = new StreamWriter(Console.OpenStandardOutput());
                writer.AutoFlush = true;
                Console.SetOut(writer);
            }
            catch { }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            bool probe = false;
            string renderPath = null;
            bool enableAutoStart = false;
            bool disableAutoStart = false;
            int interval = 30;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--probe") probe = true;
                else if (a == "--enable-autostart") enableAutoStart = true;
                else if (a == "--disable-autostart") disableAutoStart = true;
                else if (a == "--render-icon" && i + 1 < args.Length) renderPath = args[++i];
                else if (a == "--interval" && i + 1 < args.Length)
                {
                    int parsed;
                    if (int.TryParse(args[i + 1], out parsed) && parsed >= 5) interval = parsed;
                    i++;
                }
            }

            if (enableAutoStart || disableAutoStart)
            {
                Native.AttachConsole(-1);
                TryAttachStdOut();
                try
                {
                    TrayContext.SetAutoStart(enableAutoStart, interval);
                    Console.WriteLine("开机自启 = " + (TrayContext.IsAutoStartEnabled() ? "已开启" : "已关闭"));
                }
                catch (Exception ex)
                {
                    Console.WriteLine("设置失败：" + ex.Message);
                }
                return;
            }

            if (probe || renderPath != null)
            {
                Native.AttachConsole(-1);
                TryAttachStdOut();

                MouseReading reading = LunaHid.Read();
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("LunaBatteryTray probe");
                sb.AppendLine("device present : " + reading.DevicePresent);
                sb.AppendLine("link up        : " + reading.LinkUp);
                sb.AppendLine("battery        : " + (reading.Battery < 0 ? "unknown" : reading.Battery + "%"));
                sb.AppendLine("charging       : " + reading.Charging);
                sb.AppendLine("source pid     : " + (reading.BatterySourcePid == 0 ? "-" : "0x" + reading.BatterySourcePid.ToString("X4")));
                sb.AppendLine("status         : " + reading.StatusText);

                string probeLog = null;
                try
                {
                    probeLog = Path.Combine(TrayContext.LogDirectory, "probe.txt");
                    File.WriteAllText(probeLog, sb.ToString(), Encoding.UTF8);
                    sb.AppendLine("probe file     : " + probeLog);
                }
                catch { }
                Console.WriteLine(sb.ToString());

                if (renderPath != null)
                {
                    string stem = renderPath;
                    string ext = Path.GetExtension(renderPath);
                    if (!string.IsNullOrEmpty(ext)) stem = renderPath.Substring(0, renderPath.Length - ext.Length);
                    int[] sizes = new int[] { 16, 24, 32, 64 };
                    foreach (int px in sizes)
                    {
                        string file = stem + "-" + px + ".png";
                        using (Bitmap bmp = IconPainter.Render(reading, px))
                            bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                        Console.WriteLine("icon written to " + file);
                    }
                }
                return;
            }

            bool createdNew;
            using (Mutex mutex = new Mutex(true, "LunaBatteryTray.SingleInstance", out createdNew))
            {
                if (!createdNew) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
                {
                    TrayContext.Log("ui exception: " + e.Exception);
                };
                // Exit paths worth recording: "the tray icon vanished" is otherwise indistinguishable
                // between a deliberate Quit, a clean shutdown and the process being killed outright
                // (a kill leaves no trace at all here, which is itself the diagnosis).
                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
                {
                    TrayContext.Log("UnhandledException: " + e.ExceptionObject);
                };
                AppDomain.CurrentDomain.ProcessExit += delegate
                {
                    TrayContext.Log("ProcessExit fired (process is ending)");
                };
                Application.ApplicationExit += delegate
                {
                    TrayContext.Log("ApplicationExit fired (message loop ended)");
                };
                TrayContext.Log("started, pid " + System.Diagnostics.Process.GetCurrentProcess().Id
                    + ", interval " + interval + "s, exe " + Application.ExecutablePath);
                try
                {
                    Application.Run(new TrayContext(interval));
                    TrayContext.Log("Application.Run returned normally");
                }
                catch (Exception ex)
                {
                    TrayContext.Log("fatal: " + ex);
                    try
                    {
                        File.WriteAllText(Path.Combine(TrayContext.LogDirectory, "fatal.txt"), ex.ToString(), Encoding.UTF8);
                    }
                    catch { }
                    MessageBox.Show("LunaBatteryTray 启动失败：\r\n" + ex.Message, "LUNA TYPE33 电量");
                }
                GC.KeepAlive(mutex);
            }
        }
    }
}
