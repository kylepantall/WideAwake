// WideAwake - tray app that keeps the PC awake while plugged in, with a
// "Lights out" mode that turns the screen off and blocks the keyboard.
// Build: csc /target:winexe /out:WideAwake.exe WideAwake.cs
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        if (cmd == "/uninstall") { Uninstaller.Run(); return; } // called from Settings > Apps; install via WideAwakeSetup.exe

        bool created;
        using (var mutex = new Mutex(true, "WideAwake_SingleInstance", out created))
        {
            if (!created) return; // already running
            SetProcessDPIAware(); // crisp text on high-DPI screens
            Application.EnableVisualStyles();
            Application.Run(new WideAwake());
        }
    }
}

// Windows 11 (Fluent) style card: rounded corners, follows light/dark theme and accent colour,
// optional progress bar and up to two buttons (Enter = primary, Esc = secondary).
class FluentToast : Form
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34, DWMWCP_ROUND = 2;

    public event Action PrimaryClick, SecondaryClick;
    readonly bool activate, dark;
    readonly Color bg, footerBg, fg, subFg, accent, onAccent, track, buttonBg, buttonBorder;
    readonly Font glyphFont, titleFont, bodyFont;
    readonly string glyph, body, primaryText, secondaryText;
    readonly float s;
    string title;
    double progress = -1; // 0..1, or -1 for no bar
    RectangleF glyphRect, titleRect, bodyRect, barRect;
    Rectangle primaryRect, secondaryRect;
    int footerTop = -1, hover; // hover: 1 = primary, 2 = secondary
    readonly System.Windows.Forms.Timer fade = new System.Windows.Forms.Timer { Interval = 15 };
    bool closing;

    public FluentToast(string glyph, string title, string body, bool activate,
                       string primaryText = null, string secondaryText = null, bool showProgress = false)
    {
        this.glyph = glyph; this.title = title; this.body = body; this.activate = activate;
        this.primaryText = primaryText; this.secondaryText = secondaryText;
        if (showProgress) progress = 1;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true; KeyPreview = true; Opacity = 0;

        dark = IsDarkTheme();
        bg = dark ? Color.FromArgb(43, 43, 43) : Color.FromArgb(249, 249, 249);
        footerBg = dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243);
        fg = dark ? Color.White : Color.FromArgb(27, 27, 27);
        subFg = dark ? Color.FromArgb(206, 206, 206) : Color.FromArgb(96, 96, 96);
        track = dark ? Color.FromArgb(72, 72, 72) : Color.FromArgb(219, 219, 219);
        buttonBg = dark ? Color.FromArgb(55, 55, 55) : Color.White;
        buttonBorder = dark ? Color.FromArgb(68, 68, 68) : Color.FromArgb(229, 229, 229);
        accent = AccentColor(dark);
        onAccent = dark ? Color.Black : Color.White;
        BackColor = bg;

        glyphFont = PickFont(new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" }, 20f);
        titleFont = PickFont(new[] { "Segoe UI Variable Display Semibold", "Segoe UI Semibold" }, 15f);
        bodyFont = PickFont(new[] { "Segoe UI Variable Text", "Segoe UI" }, 10.5f);

        using (var g = CreateGraphics()) s = g.DpiX / 96f;
        float pad = 24 * s, w = 420 * s, textX = pad + 44 * s, textW = w - textX - pad;
        float y = pad;
        using (var g = CreateGraphics())
        {
            float titleH = g.MeasureString("Ag", titleFont).Height;
            float bodyH = g.MeasureString(body, bodyFont, (int)textW).Height;
            glyphRect = new RectangleF(pad, y + 2 * s, 40 * s, 40 * s);
            titleRect = new RectangleF(textX, y, textW, titleH);
            y += titleH + 2 * s;
            bodyRect = new RectangleF(textX, y, textW, bodyH);
            y += bodyH;
        }
        if (progress >= 0) { y += 16 * s; barRect = new RectangleF(textX, y, textW, 4 * s); y += 4 * s; }
        y += pad;
        if (primaryText != null || secondaryText != null)
        {
            footerTop = (int)y;
            int bh = (int)(32 * s), gap = (int)(8 * s), inner = (int)(w - 2 * pad);
            int bw = secondaryText != null && primaryText != null ? (inner - gap) / 2 : inner;
            int by = (int)(y + pad);
            if (primaryText != null) primaryRect = new Rectangle((int)pad, by, bw, bh);
            if (secondaryText != null) secondaryRect = new Rectangle(primaryText != null ? (int)pad + bw + gap : (int)pad, by, bw, bh);
            y += pad * 2 + bh;
        }
        ClientSize = new Size((int)w, (int)y);
        var area = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);

        fade.Tick += (o, e) =>
        {
            if (closing) { Opacity -= 0.12; if (Opacity <= 0) { fade.Stop(); Close(); } }
            else { Opacity += 0.15; if (Opacity >= 1) fade.Stop(); }
        };
    }

    public string Title { set { title = value; Invalidate(); } }
    public double Progress { set { progress = Math.Max(0, Math.Min(1, value)); Invalidate(); } }

    public void ShowCard()
    {
        Show();
        if (activate) Activate();
        fade.Start();
    }

    public void FadeOutAndClose() { closing = true; fade.Start(); }

    public void AutoClose(int ms)
    {
        var t = new System.Windows.Forms.Timer { Interval = ms };
        t.Tick += (o, e) => { t.Dispose(); FadeOutAndClose(); };
        t.Start();
    }

    protected override bool ShowWithoutActivation { get { return !activate; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: keep out of Alt+Tab
            if (!activate) cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int v = DWMWCP_ROUND; DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref v, 4);
        v = IsDarkTheme() ? 1 : 0; DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, 4);
        v = IsDarkTheme() ? 0x3A3A3A : 0xE5E5E5; DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref v, 4);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (var b = new SolidBrush(accent)) g.DrawString(glyph, glyphFont, b, glyphRect);
        using (var b = new SolidBrush(fg)) g.DrawString(title, titleFont, b, titleRect);
        using (var b = new SolidBrush(subFg)) g.DrawString(body, bodyFont, b, bodyRect);

        if (progress >= 0)
        {
            float r = barRect.Height / 2;
            using (var b = new SolidBrush(track)) FillRound(g, b, barRect, r);
            if (progress > 0)
                using (var b = new SolidBrush(accent))
                    FillRound(g, b, new RectangleF(barRect.X, barRect.Y, Math.Max(barRect.Height, barRect.Width * (float)progress), barRect.Height), r);
        }

        if (footerTop >= 0)
        {
            using (var b = new SolidBrush(footerBg)) g.FillRectangle(b, 0, footerTop, ClientSize.Width, ClientSize.Height - footerTop);
            using (var p = new Pen(buttonBorder)) g.DrawLine(p, 0, footerTop, ClientSize.Width, footerTop);
            if (primaryText != null)
                DrawButton(g, primaryRect, primaryText, hover == 1 ? Blend(accent, dark ? Color.Black : Color.White, 0.1) : accent, onAccent, null);
            if (secondaryText != null)
                DrawButton(g, secondaryRect, secondaryText, hover == 2 ? Blend(buttonBg, dark ? Color.White : Color.Black, 0.05) : buttonBg, fg, buttonBorder);
        }
    }

    void DrawButton(Graphics g, Rectangle r, string text, Color fill, Color textColor, Color? border)
    {
        using (var b = new SolidBrush(fill)) FillRound(g, b, r, 4 * s);
        if (border.HasValue) using (var p = new Pen(border.Value)) using (var path = Round(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), 4 * s)) g.DrawPath(p, path);
        TextRenderer.DrawText(g, text, bodyFont, r, textColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = primaryText != null && primaryRect.Contains(e.Location) ? 1 : secondaryText != null && secondaryRect.Contains(e.Location) ? 2 : 0;
        if (h != hover) { hover = h; Cursor = h != 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { if (hover != 0) { hover = 0; Invalidate(); } base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (primaryText != null && primaryRect.Contains(e.Location) && PrimaryClick != null) PrimaryClick();
        else if (secondaryText != null && secondaryRect.Contains(e.Location) && SecondaryClick != null) SecondaryClick();
        base.OnMouseClick(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && primaryText != null && PrimaryClick != null) PrimaryClick();
        else if (e.KeyCode == Keys.Escape && secondaryText != null && SecondaryClick != null) SecondaryClick();
        base.OnKeyDown(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        fade.Dispose(); glyphFont.Dispose(); titleFont.Dispose(); bodyFont.Dispose();
        base.OnFormClosed(e);
    }

    static System.Drawing.Drawing2D.GraphicsPath Round(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static void FillRound(Graphics g, Brush b, RectangleF r, float radius) { using (var p = Round(r, radius)) g.FillPath(b, p); }

    static Font PickFont(string[] names, float size)
    {
        foreach (var n in names)
        {
            var f = new Font(n, size);
            if (f.Name == n) return f; // GDI+ silently substitutes missing fonts
            f.Dispose();
        }
        return new Font(SystemFonts.MessageBoxFont.FontFamily, size);
    }

    static bool IsDarkTheme()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                return k != null && Convert.ToInt32(k.GetValue("AppsUseLightTheme", 1)) == 0;
        }
        catch (Exception) { return false; }
    }

    // Windows accent colour, adjusted like Fluent does (lighter in dark mode, darker in light mode).
    static Color AccentColor(bool dark)
    {
        Color c = Color.FromArgb(0, 103, 192);
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
            {
                object v = k == null ? null : k.GetValue("AccentColor");
                if (v != null) { int abgr = Convert.ToInt32(v); c = Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF); }
            }
        }
        catch (Exception) { }
        return dark ? Blend(c, Color.White, 0.45) : Blend(c, Color.Black, 0.15);
    }

    static Color Blend(Color a, Color b, double t)
    {
        return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }
}

// ASUS keyboard backlight via the ASUS ACPI driver (\\.\ATKACPI, installed with MyASUS /
// ASUS System Control Interface). Same interface G-Helper and Linux asus-wmi use.
// On other laptops the device doesn't exist and these calls quietly do nothing.
static class KeyboardLight
{
    const uint IOCTL = 0x0022240C, DSTS = 0x53545344, DEVS = 0x53564544, KBD_BACKLIGHT = 0x00050021, PRESENT = 0x10000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code, byte[] inBuf, int inLen, byte[] outBuf, int outLen, out int returned, IntPtr overlapped);

    static uint? Call(uint method, uint value)
    {
        try
        {
            using (var h = CreateFile(@"\\.\ATKACPI", 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero))
            {
                if (h.IsInvalid) return null;
                var inBuf = new byte[16];
                BitConverter.GetBytes(method).CopyTo(inBuf, 0);
                BitConverter.GetBytes(8u).CopyTo(inBuf, 4);
                BitConverter.GetBytes(KBD_BACKLIGHT).CopyTo(inBuf, 8);
                BitConverter.GetBytes(value).CopyTo(inBuf, 12);
                var outBuf = new byte[16];
                int n;
                if (!DeviceIoControl(h, IOCTL, inBuf, inBuf.Length, outBuf, outBuf.Length, out n, IntPtr.Zero)) return null;
                return BitConverter.ToUInt32(outBuf, 0);
            }
        }
        catch (Exception) { return null; }
    }

    // Current level (0-3), or -1 if there's no controllable ASUS keyboard backlight.
    public static int Get()
    {
        uint? s = Call(DSTS, 0);
        return s.HasValue && (s.Value & PRESENT) != 0 ? (int)(s.Value & 0x7F) : -1;
    }

    public static void Set(int level) { Call(DEVS, 0x80u | (uint)(level & 0x7F)); }
}

// Removes what WideAwakeSetup.exe installed (paths/keys must match Setup.cs).
static class Uninstaller
{
    const string Name = "WideAwake";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WideAwake";
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Name);
    static readonly string Exe = Path.Combine(Dir, Name + ".exe");
    static readonly string Shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name + ".lnk");
    static readonly string DesktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Name + ".lnk");

    public static void Run()
    {
        if (MessageBox.Show("Remove WideAwake from this PC?", Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            StopRunning();
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) if (k != null) k.DeleteValue(Name, false);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            if (File.Exists(Shortcut)) File.Delete(Shortcut);
            if (File.Exists(DesktopShortcut)) File.Delete(DesktopShortcut);

            bool runningFromDir = string.Equals(Path.GetDirectoryName(Path.GetFullPath(Application.ExecutablePath)), Dir, StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(Dir) && !runningFromDir) Directory.Delete(Dir, true);

            MessageBox.Show("WideAwake has been removed.", Name, MessageBoxButtons.OK, MessageBoxIcon.Information);

            // A running exe can't delete itself, so a hidden cmd removes the folder once we've exited.
            if (runningFromDir)
                Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + Dir + "\"")
                    { CreateNoWindow = true, UseShellExecute = false });
        }
        catch (Exception ex) { MessageBox.Show("Uninstall failed: " + ex.Message, Name, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    static void StopRunning()
    {
        int me = Process.GetCurrentProcess().Id;
        foreach (var p in Process.GetProcessesByName(Name))
            using (p)
                if (p.Id != me) { try { p.Kill(); p.WaitForExit(5000); } catch (Exception) { } }
    }

}

class WideAwake : Form
{
    // --- Win32 ---
    const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001, ES_DISPLAY_REQUIRED = 0x00000002;
    const int WM_POWERBROADCAST = 0x0218, WM_HOTKEY = 0x0312;
    const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    const int VK_L = 0x4C, VK_U = 0x55, VK_ESCAPE = 0x1B, EscHoldMs = 3000;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // --- State ---
    readonly NotifyIcon tray = new NotifyIcon();
    readonly ToolStripMenuItem keepAwakeItem, lightsOutItem, startupItem;
    readonly System.Windows.Forms.Timer powerTimer = new System.Windows.Forms.Timer { Interval = 30000 };
    readonly System.Collections.Generic.List<Form> blackouts = new System.Collections.Generic.List<Form>();
    readonly HookProc hookProc; // field keeps the delegate alive for the hook
    IntPtr hook = IntPtr.Zero;
    bool ctrlDown, altDown, unlockPending;
    int escDownAt, savedBrightness = -1, savedKeyboardLight = -1;
    // Some ASUS firmware relights the keyboard on a key press, so keep switching it off while dark.
    readonly System.Windows.Forms.Timer keyLightTimer = new System.Windows.Forms.Timer { Interval = 3000 };
    uint heldFlags;
    FluentToast countdown; // non-null while the "Lights out in N" card is showing

    public WideAwake()
    {
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        var h = Handle; // force window handle creation for messages/hotkeys
        hookProc = KeyboardHook;

        keepAwakeItem = new ToolStripMenuItem("Keep awake while plugged in") { Checked = true, CheckOnClick = true };
        keepAwakeItem.CheckedChanged += (s, e) => UpdateAwake();
        lightsOutItem = new ToolStripMenuItem("Lights out  (Ctrl+Alt+L, hold Esc 3s to exit)", null, (s, e) => RequestLightsOut());
        startupItem = new ToolStripMenuItem("Start with Windows") { Checked = IsStartup(), CheckOnClick = true };
        startupItem.CheckedChanged += (s, e) => SetStartup(startupItem.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.Add(keepAwakeItem);
        menu.Items.Add(lightsOutItem);
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (s, e) => Close());

        tray.Icon = SystemIcons.Shield;
        tray.ContextMenuStrip = menu;
        tray.Visible = true;
        tray.DoubleClick += (s, e) => RequestLightsOut();

        if (!RegisterHotKey(Handle, 1, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_L))
            tray.ShowBalloonTip(3000, "WideAwake", "Ctrl+Alt+L is in use by another app; use the tray menu instead.", ToolTipIcon.Warning);

        powerTimer.Tick += (s, e) => UpdateAwake();
        powerTimer.Start();
        keyLightTimer.Tick += (s, e) => { if (KeyboardLight.Get() > 0) KeyboardLight.Set(0); };
        UpdateAwake();
    }

    protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); } // never show a window

    // Hold the system awake only while on AC power.
    // During lights out we also hold the display "on": on Modern Standby laptops, the display
    // turning off (by timeout or SC_MONITORPOWER) puts the whole PC into standby, which drops
    // network access. Instead we black the screen ourselves (black pixels are off on OLED).
    void UpdateAwake()
    {
        bool onAC = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
        bool want = keepAwakeItem.Checked && onAC;
        bool dark = hook != IntPtr.Zero;
        uint flags = ES_CONTINUOUS | (want ? ES_SYSTEM_REQUIRED : 0) | (want && dark ? ES_DISPLAY_REQUIRED : 0);
        if (flags != heldFlags) { SetThreadExecutionState(flags); heldFlags = flags; }

        string text = "WideAwake - " + (want ? "keeping PC awake" : onAC ? "paused" : "on battery, normal sleep");
        if (dark) text += " | LIGHTS OUT";
        tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
    }

    // Shows a countdown card first; Esc/Cancel aborts, Enter/"Start now" skips the wait.
    void RequestLightsOut()
    {
        if (hook != IntPtr.Zero || countdown != null) return;
        const int total = 5000;
        var card = new FluentToast("", "Lights out in 5",
            "The screen and keyboard light will turn off. Your PC stays awake and reachable. Hold Esc for 3 seconds to turn them back on.",
            true, "Start now", "Cancel", true);
        countdown = card;
        var clock = Stopwatch.StartNew();
        var tick = new System.Windows.Forms.Timer { Interval = 30 };
        bool done = false;
        Action<bool> finish = go =>
        {
            if (done) return;
            done = true;
            tick.Dispose();
            countdown = null;
            if (go) { card.Close(); LightsOut(); }
            else card.FadeOutAndClose();
        };
        tick.Tick += (s, e) =>
        {
            long left = total - clock.ElapsedMilliseconds;
            if (left <= 0) { finish(true); return; }
            card.Title = "Lights out in " + (int)Math.Ceiling(left / 1000.0);
            card.Progress = left / (double)total;
        };
        card.PrimaryClick += () => finish(true);
        card.SecondaryClick += () => finish(false);
        card.FormClosed += (s, e) => finish(false);
        card.ShowCard();
        tick.Start();
    }

    void LightsOut()
    {
        if (hook != IntPtr.Zero) return;
        ctrlDown = altDown = unlockPending = false;
        escDownAt = 0;
        hook = SetWindowsHookEx(WH_KEYBOARD_LL, hookProc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero)
        {
            MessageBox.Show("Could not block the keyboard (error " + Marshal.GetLastWin32Error() + ").", "WideAwake");
            return;
        }
        lightsOutItem.Enabled = false;
        UpdateAwake();

        // Black, topmost window over every screen; cursor hidden while over it.
        foreach (var screen in Screen.AllScreens)
        {
            var f = new Form
            {
                FormBorderStyle = FormBorderStyle.None, BackColor = Color.Black, TopMost = true,
                ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Bounds = screen.Bounds
            };
            f.Show();
            f.Bounds = screen.Bounds; // re-apply after Show in case of DPI scaling
            blackouts.Add(f);
        }
        Cursor.Hide();
        savedBrightness = GetBrightness();
        SetBrightness(0);
        savedKeyboardLight = KeyboardLight.Get();
        if (savedKeyboardLight > 0) KeyboardLight.Set(0);
        keyLightTimer.Start();
    }

    void Restore(bool notify)
    {
        if (hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(hook); hook = IntPtr.Zero;
        foreach (var f in blackouts) f.Close();
        blackouts.Clear();
        Cursor.Show();
        if (savedBrightness >= 0) SetBrightness(savedBrightness);
        keyLightTimer.Stop();
        if (savedKeyboardLight > 0) KeyboardLight.Set(savedKeyboardLight);
        lightsOutItem.Enabled = true;
        UpdateAwake();
        if (notify)
        {
            var card = new FluentToast("", "Lights back on", "Screen and keyboard light are restored.", false);
            card.ShowCard();
            card.AutoClose(2500);
        }
    }

    // Built-in laptop panel brightness via WMI. Returns -1 / does nothing if unsupported (e.g. external monitors).
    static int GetBrightness()
    {
        try
        {
            using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
                foreach (ManagementObject o in s.Get()) using (o) return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch (Exception) { }
        return -1;
    }

    static void SetBrightness(int level)
    {
        try
        {
            using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods"))
                foreach (ManagementObject o in s.Get()) using (o) o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)level });
        }
        catch (Exception) { }
    }

    // Blocks every key press while in lights-out.
    // Unlock: hold Esc for 3 seconds (or press Ctrl+Alt+U).
    // Key-ups are passed through so no modifier ever gets "stuck".
    IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            int vk = Marshal.ReadInt32(lParam);
            bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;
            if (vk == 0x11 || vk == 0xA2 || vk == 0xA3) ctrlDown = down || (!up && ctrlDown);
            if (vk == 0x12 || vk == 0xA4 || vk == 0xA5) altDown = down || (!up && altDown);
            if (down && vk == VK_U && ctrlDown && altDown) BeginInvoke((Action)(() => Restore(true)));

            if (vk == VK_ESCAPE)
            {
                // Holding a key auto-repeats key-downs, so we just measure from the first one.
                if (down && escDownAt == 0) escDownAt = Environment.TickCount | 1; // |1 keeps it non-zero
                else if (down && Environment.TickCount - escDownAt >= EscHoldMs) unlockPending = true;
                else if (up)
                {
                    // Unlock on release so the remaining Esc repeats don't leak into other apps.
                    escDownAt = 0;
                    if (unlockPending) { unlockPending = false; BeginInvoke((Action)(() => Restore(true))); return (IntPtr)1; }
                }
            }
            if (down) return (IntPtr)1; // swallow
        }
        return CallNextHookEx(hook, nCode, wParam, lParam);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && (int)m.WParam == 1) RequestLightsOut();
        else if (m.Msg == WM_POWERBROADCAST) UpdateAwake(); // plugged/unplugged
        base.WndProc(ref m);
    }

    static bool IsStartup()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && k.GetValue("WideAwake") != null;
    }

    static void SetStartup(bool on)
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue("WideAwake", "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue("WideAwake", false);
            }
        }
        catch (Exception ex) { MessageBox.Show("Could not change startup setting: " + ex.Message, "WideAwake"); }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Restore(false);
        UnregisterHotKey(Handle, 1);
        SetThreadExecutionState(ES_CONTINUOUS); // let Windows sleep normally again
        tray.Visible = false;
        tray.Dispose();
        base.OnFormClosed(e);
    }
}
