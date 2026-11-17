<img src="assets/icon-256.png" alt="WideAwake logo" width="96">

# WideAwake

A small Windows tray app that keeps your PC awake while it's plugged in, so you can still reach it over SSH, Remote Desktop or Tailscale, and a **Lights out** mode that darkens the screen and keyboard.

## Features

- **Keep awake while plugged in.** Stops Windows sleeping on AC power and lets it sleep normally on battery. Switches instantly when you plug in or unplug.
- **Lights out** (Ctrl+Alt+L, the tray menu, or double-click the tray icon):
  - A 5-second countdown card first. Press Enter or **Start now** to skip it, or Esc or **Cancel** to stop.
  - Covers every screen with black, hides the cursor and sets brightness to 0. Black pixels are off on OLED screens.
  - Turns the keyboard backlight off on ASUS laptops (through the ASUS ACPI driver installed with MyASUS).
  - Ignores all key presses.
  - **Hold Esc for 3 seconds** to exit (Ctrl+Alt+U also works). Brightness and keyboard light are restored and the PC **locks**, so you sign back in (Windows Hello face or fingerprint makes this quick). The screen stays black for as long as you're away; the lock screen only appears when you come back. Use Lights out instead of Win+L when you leave the PC.
- **Lights out automatically when idle** (on by default, tray menu). While plugged in, WideAwake stops Windows turning the screen off and starts Lights out itself at the same point instead: when you've been idle for your "Turn off my screen after" time (Settings → System → Power, plugged in). A countdown card appears first, and moving the mouse or pressing a key cancels it. An automatic Lights out ends on any key or mouse movement, then locks the PC. It's off when the screen timeout is set to Never.
- **On battery** (tray menu): stay awake down to 50% (the default), down to 30%, or sleep normally. This is for battery charge limits ("smart charging", such as an 80% cap): when the battery reaches the cap, the laptop runs off the battery and Windows reports it as unplugged, even though the charger is still in. Windows can't tell that apart from a real unplug, so WideAwake keeps going down to the floor. Below it, the PC sleeps normally until it's plugged in again.
- **Log**: `%LOCALAPPDATA%\WideAwake\WideAwake.log` (tray menu → **Open log**). It records power-source changes, display on/off, lock/unlock, Lights out and sleep/resume, plus a heartbeat every 5 minutes, so gaps show when the PC was in standby.
- **Start with Windows** option in the tray menu.

![Countdown card](docs/card_enter.png)

### Why not just turn the display off?

On Modern Standby laptops (most recent ones), turning the display off with Windows' own command, or letting the screen timeout do it, puts the whole PC into standby, which cuts off network access. Lights out keeps the display technically "on" and blacks it out instead, so the PC stays reachable. This is also why keep-awake tools such as PowerToys Awake lose the connection when they're allowed to turn the screen off.

If you lock the PC yourself (Win+L), WideAwake can't draw over the lock screen. Instead it keeps the display technically on, and after your screen timeout it sets brightness to 0 and turns the keyboard light off, so the PC stays reachable. Touching a key or the mouse brings the brightness back.

## Install

Download `WideAwakeSetup.exe` from the [latest release](https://github.com/kylepantall/WideAwake/releases/latest) and run it. It installs for the current user only, so it doesn't need admin rights. It installs to `%LOCALAPPDATA%\Programs\WideAwake`, adds a Start menu shortcut, and can optionally start with Windows and add a desktop shortcut.

To uninstall, go to **Settings → Apps → Installed apps → WideAwake**, or run `Uninstall.cmd`.

A winget manifest is in [`winget/manifests`](winget/manifests). Once it's accepted into the Windows Package Manager community repository, you'll be able to install with `winget install KylePantall.WideAwake`.

For scripted installs, `WideAwakeSetup.exe /silent` installs with the defaults and no window, and `WideAwake.exe /uninstall /quiet` removes it without asking.

### SmartScreen and antivirus warnings

The installer isn't code-signed yet, so Windows SmartScreen may warn you. Click **More info → Run anyway**.

Some antivirus products flag WideAwake as "potentially unwanted" because it does things that malware also does: it blocks keyboard input during Lights out, starts with Windows, and its installer writes a program to disk. If yours does, you can restore it from quarantine and add an exception, and please report it to your antivirus vendor as a false positive. The source code is all in this repository, so you can check exactly what it does.

## Build

You don't need to install any tools; it uses the C# compiler that comes with Windows (.NET Framework 4.x).

```bat
Build.cmd
```

This produces `WideAwake.exe` and `WideAwakeSetup.exe`, which has `WideAwake.exe` built in. `Build.cmd app` and `Build.cmd setup` build just one of them.

GitHub Actions ([`.github/workflows/build.yml`](.github/workflows/build.yml)) builds every push. Pushing a tag such as `v1.8` publishes a release with the installer attached; the tag must match `Version` in `Setup.cs`. Code signing through [SignPath Foundation](https://signpath.org) switches on once its repository secret and variables are set; the workflow file lists them.

## License

[MIT](LICENSE)

| File | What it is |
| --- | --- |
| `WideAwake.cs` | The tray app, Lights out mode, Fluent cards, ASUS keyboard light, and uninstaller (`/uninstall`) |
| `Setup.cs` | The setup wizard |
| `Build.cmd` | Builds both programs |
| `Uninstall.cmd` | Runs the installed uninstaller |
| `assets/logo.svg` | The logo source (256-unit grid) |
| `assets/MakeIcon.cs` | Renders the logo to `WideAwake.ico` and the PNGs, using the same geometry as the SVG |

To regenerate the icon after changing the design:

```bat
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /out:MakeIcon.exe /r:System.Drawing.dll assets\MakeIcon.cs
MakeIcon.exe assets
```
