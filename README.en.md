# LunaBatteryTray

[中文](README.md) | **English**

A tiny Windows system-tray app that shows the battery level of a
**Lunafury LUNA TYPE33** wireless mouse (`Luna33-W`, USB VID `0x373E`,
PID `0x0032` wired / `0x0033` 8K 2.4 GHz receiver).

Single executable, no dependencies, no vendor driver required.

![tray](images/tray.png)

- The percentage is drawn **directly on the tray icon**: ≥50% green, 20–49% amber,
  <20% red, **charging blue** (with a small bolt on larger icon sizes)
- Tooltip: `LUNA TYPE33: 32% (charging)`
- Device found but the radio link is down → purple `?`; not plugged in → grey `--`
- Right-click menu: refresh now / start with Windows / refresh interval / about / quit

## Quick start

1. Download or build `LunaBatteryTray.exe`
2. Double-click it — the icon appears in the notification area
   (if Windows hides it in the `^` overflow, drag it out)
3. To start with Windows, tick **开机自启** in the menu, or run `tools\启用开机自启.cmd`

## Build

Requires .NET Framework 4.x, which ships with Windows — no Visual Studio needed:

```cmd
build.cmd
```

which is just:

```cmd
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /optimize+ ^
  /out:LunaBatteryTray.exe ^
  /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  src\LunaBatteryTray.cs
```

## CLI

```
LunaBatteryTray.exe --probe                     read once, print the result, exit
LunaBatteryTray.exe --interval 60               run the tray, refresh every 60s (default 30)
LunaBatteryTray.exe --enable-autostart          enable start-with-Windows
LunaBatteryTray.exe --disable-autostart         disable it
LunaBatteryTray.exe --render-icon out.png       dump the current icon as PNG (16/24/32/64)
```

Logs go to `%LOCALAPPDATA%\LunaBatteryTray\state.log`, falling back to `<exe dir>\logs\`
and then `%TEMP%\LunaBatteryTray\`. `--probe` also writes `probe.txt` next to the log.

## How the battery level is read

Windows exposes **no** battery level for this mouse: it is not a Bluetooth device
(`Get-PnpDevice -Class Battery` does not list it), it talks over its 2.4 GHz receiver
or the USB cable, and no generic API covers that. Every comparable tool does the same
thing — a per-device HID driver, with Windows' own properties used only for Bluetooth.

Interface layout (measured on real hardware):

```
USB\VID_373E&PID_0032   "Luna33-W"  wired
  └─ MI_02  usage page 0xFFFF, usage 0x00  → 65-byte feature report  ← command channel
USB\VID_373E&PID_0033   "Luna33-W"  2.4 GHz receiver
  └─ MI_02  same 65-byte feature report
```

Protocol (verified on the device):

```
write feature report (65 bytes):
  00 00 00 02 02 00 83 00 00 ... 00

read feature report (65 bytes):
  index: 0  1  2  3  4  5  6  7        8
        00 A1 00 02 02 00 83 charging battery%
```

* Byte 1 `0xA1` (or `0xA2`) means a valid answer; **`0xA0` means the radio link is not up**
  (e.g. the mouse is on the cable), which must not be reported as 0%.
* Byte 7 = charging flag, byte 8 = battery percentage.
* The app scans **every VID `0x373E` interface whose usage page is `0xFFFF`** and takes the
  first valid reading, so wired, wireless, or both connected at once all work.

Measured on this machine:

| interface | reply | meaning |
|---|---|---|
| PID_0032 (wired) | `00 A1 00 02 02 00 83 01 1D` | charging, battery `0x1D` = **29%** |
| PID_0033 (receiver) | `00 A0 00 02 02 00 83 00 00` | radio link inactive → “wireless not connected” |

The app only ever writes this one query; it never changes any mouse setting.

### Cross-check against the official web driver

The vendor's WebHID configurator (`https://mouse.lunafury.games/`) reads the battery with
exactly the same frame — its `getBatPer()` in `/js/output.xvi3.min.js`:

```js
t[2] = 2; t[3] = 2; t[5] = 131;            // 131 = 0x83
await this.setReport(t); await this.mySleep(200);
var e = await this.getReport(a);
if (e[1] == 161 && e[4] == 2 && e[6] == 131) return [e[7], e[8]];  // 161 = 0xA1 → [charging, percent]
```

Its device table also confirms the interface split
(`VIDWired 373E / PIDWired 0032 / VIDWireless4K8K 373E / PIDWireless4K8K 0033`).

The vendor's JavaScript is proprietary and is **not** included in this repository.

## Known limitations

* The mouse must be on the cable, or the receiver plugged in with the mouse powered on and
  linked over 2.4 GHz; otherwise the icon shows `--`.
* While the mouse sleeps, the receiver may report 0 or a stale value; the app shows a purple
  `?` instead of a fake 0%.
* Only tuned for the LUNA TYPE33. For another `0x373E` mouse, adjusting the VID/usage page in
  `LunaHid` should be enough — please open an issue with your `--probe` output.

## Changelog

**v1.1 — fix “it disappears as soon as I right-click”**

With the tray at the bottom-right corner, `NotifyIcon.ContextMenuStrip` opens the menu
*upwards*, which parks the pointer exactly on the last item (“退出” / Quit). Any click right
after the right-click therefore hit Quit and the app exited cleanly — which is why the event
log showed no crash at all. Reproduced on real hardware:

```
pid before right-click : 23936
pid after  right-click : 23936   <- the right-click itself is harmless
left-clicking at (1475,1061) ... <- the pointer was sitting on "Quit"
pid after  left-click  : GONE
```

The menu is now positioned by `ShowTrayMenu()` so that it always lands inside the working
area and never under the pointer:

```
menu shown at (1231,892) size 256x148   pointer (1475,1061)   pointerInsideMenu=False
pid after left-click   : 23204          <- still running
```

**v1.0** — first release: reverse-engineered the 65-byte feature protocol and cross-checked
it against the vendor's own web driver.

## Prior art

* [incconutwo/mouse-battery-tray](https://github.com/incconutwo/mouse-battery-tray)
* [HeyOkay/HaloBattery](https://github.com/HeyOkay/HaloBattery)
* [maxboeer/attackshark-battery-bridge](https://github.com/maxboeer/attackshark-battery-bridge)
* [ryanlewis/mousebatt](https://github.com/ryanlewis/mousebatt)
* [stuffz/mouse-battery-monitor](https://github.com/stuffz/mouse-battery-monitor)

## License

[MIT](LICENSE).
