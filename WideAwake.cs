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
        if (cmd == "/uninstall") { Uninstaller.Run(Array.Exists(args, a => a.ToLowerInvariant() == "/quiet")); return; } // called from Settings > Apps; install via WideAwakeSetup.exe

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

// Windows 11 (Fluent) style card, drawn as a per-pixel-alpha layered window so the rounded
// corners are truly transparent and it gets a soft shadow (DWM rounding doesn't apply to
// layered windows, which any fade needs). Follows light/dark theme and accent colour;
// optional progress bar and up to two buttons (Enter = primary, Esc = secondary).
class FluentToast : Form
{
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, ULW_ALPHA = 2;

    public event Action PrimaryClick, SecondaryClick;
    readonly bool activate, dark;
    readonly Color bg, footerBg, fg, subFg, accent, onAccent, track, buttonBg, buttonBorder, edge;
    readonly Font titleFont, bodyFont;
    readonly Image logo;
    readonly string body, primaryText, secondaryText;
    readonly float s, radius;
    readonly int margin, cardW, cardH; // margin = room around the card for the shadow
    string title;
    double progress = -1; // 0..1, or -1 for no bar
    RectangleF logoRect, titleRect, bodyRect, barRect;
    Rectangle primaryRect, secondaryRect;
    int footerTop = -1, hover; // hover: 1 = primary, 2 = secondary
    readonly System.Windows.Forms.Timer fade = new System.Windows.Forms.Timer { Interval = 15 };
    bool closing;
    int alpha; // whole-window fade, 0..255

    public FluentToast(string title, string body, bool activate,
                       string primaryText = null, string secondaryText = null, bool showProgress = false)
    {
        this.title = title; this.body = body; this.activate = activate;
        this.primaryText = primaryText; this.secondaryText = secondaryText;
        if (showProgress) progress = 1;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual; KeyPreview = true;

        dark = IsDarkTheme();
        bg = dark ? Color.FromArgb(43, 43, 43) : Color.FromArgb(249, 249, 249);
        footerBg = dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243);
        fg = dark ? Color.White : Color.FromArgb(27, 27, 27);
        subFg = dark ? Color.FromArgb(206, 206, 206) : Color.FromArgb(96, 96, 96);
        track = dark ? Color.FromArgb(72, 72, 72) : Color.FromArgb(219, 219, 219);
        buttonBg = dark ? Color.FromArgb(55, 55, 55) : Color.White;
        buttonBorder = dark ? Color.FromArgb(68, 68, 68) : Color.FromArgb(229, 229, 229);
        edge = dark ? Color.FromArgb(64, 64, 64) : Color.FromArgb(222, 222, 222);
        accent = AccentColor(dark);
        onAccent = dark ? Color.Black : Color.White;

        titleFont = PickFont(new[] { "Segoe UI Variable Display Semibold", "Segoe UI Semibold" }, 15f);
        bodyFont = PickFont(new[] { "Segoe UI Variable Text", "Segoe UI" }, 10.5f);

        using (var g = CreateGraphics()) s = g.DpiX / 96f;
        using (var ico = AppIcon.Get((int)(36 * s))) logo = ico.ToBitmap();
        radius = 8 * s;
        margin = (int)(28 * s);
        float pad = 24 * s, w = 420 * s, textX = pad + 44 * s, textW = w - textX - pad;
        float y = pad;
        using (var g = CreateGraphics())
        {
            float titleH = g.MeasureString("Ag", titleFont).Height;
            float bodyH = g.MeasureString(body, bodyFont, (int)textW).Height;
            logoRect = new RectangleF(pad, y + 2 * s, 36 * s, 36 * s);
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
        cardW = (int)w; cardH = (int)y;
        var area = Screen.PrimaryScreen.WorkingArea;
        Bounds = new Rectangle(area.Left + (area.Width - cardW) / 2 - margin, area.Top + (area.Height - cardH) / 2 - margin,
                               cardW + 2 * margin, cardH + 2 * margin);

        fade.Tick += (o, e) =>
        {
            if (closing) { alpha = Math.Max(0, alpha - 32); if (alpha == 0) { fade.Stop(); Close(); return; } }
            else { alpha = Math.Min(255, alpha + 40); if (alpha == 255) fade.Stop(); }
            Render();
        };
    }

    public string Title { set { title = value; Render(); } }
    public double Progress { set { progress = Math.Max(0, Math.Min(1, value)); Render(); } }

    public void ShowCard()
    {
        alpha = 0;
        Show();
        Render();
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
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW; // toolwindow: keep out of Alt+Tab
            if (!activate) cp.ExStyle |= WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e) { } // all drawing goes through Render()

    // Draws the card into a 32-bit bitmap and hands it to Windows with per-pixel alpha.
    void Render()
    {
        if (IsDisposed || !IsHandleCreated) return;
        using (var bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.Transparent); Draw(g); }
            IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hbmp = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hbmp);
            try
            {
                var size = new SIZE { cx = Width, cy = Height };
                var src = new POINT();
                var dst = new POINT { x = Left, y = Top };
                var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = (byte)alpha, AlphaFormat = 1 };
                UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, ULW_ALPHA);
            }
            finally { SelectObject(mem, old); DeleteObject(hbmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen); }
        }
    }

    void Draw(Graphics g)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit; // ClearType needs an opaque target
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

        // Soft shadow: stacked, slightly offset rounded rects with low alpha.
        // Layers get darker towards the card (quadratic falloff) so the outer edge fades to nothing.
        const int steps = 24;
        float spread = margin - 6 * s;
        for (int i = 0; i < steps; i++)
        {
            float t = (float)(i + 1) / steps;            // 0 = outermost, 1 = card edge
            float grow = spread * (1 - t);
            int a = (int)Math.Round((dark ? 9 : 6) * t * t);
            if (a == 0) continue;
            var r = new RectangleF(margin - grow, margin - grow + 4 * s, cardW + 2 * grow, cardH + 2 * grow);
            using (var b = new SolidBrush(Color.FromArgb(a, 0, 0, 0))) FillRound(g, b, r, radius + grow);
        }

        g.TranslateTransform(margin, margin);
        var card = new RectangleF(0, 0, cardW, cardH);
        using (var path = Round(card, radius))
        {
            using (var b = new SolidBrush(bg)) g.FillPath(b, path);
            g.SetClip(path);
            if (footerTop >= 0)
            {
                using (var b = new SolidBrush(footerBg)) g.FillRectangle(b, 0, footerTop, cardW, cardH - footerTop);
                using (var p = new Pen(buttonBorder)) g.DrawLine(p, 0, footerTop, cardW, footerTop);
            }
            g.ResetClip();
            using (var p = new Pen(edge)) using (var e = Round(new RectangleF(0.5f, 0.5f, cardW - 1, cardH - 1), radius)) g.DrawPath(p, e);
        }

        g.DrawImage(logo, logoRect);
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

        if (primaryText != null)
            DrawButton(g, primaryRect, primaryText, hover == 1 ? Blend(accent, dark ? Color.Black : Color.White, 0.1) : accent, onAccent, null);
        if (secondaryText != null)
            DrawButton(g, secondaryRect, secondaryText, hover == 2 ? Blend(buttonBg, dark ? Color.White : Color.Black, 0.05) : buttonBg, fg, buttonBorder);
    }

    void DrawButton(Graphics g, Rectangle r, string text, Color fill, Color textColor, Color? border)
    {
        using (var b = new SolidBrush(fill)) FillRound(g, b, r, 4 * s);
        if (border.HasValue) using (var p = new Pen(border.Value)) using (var path = Round(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), 4 * s)) g.DrawPath(p, path);
        using (var b = new SolidBrush(textColor))
        using (var f = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString(text, bodyFont, b, r, f);
    }

    Point CardPoint(Point p) { return new Point(p.X - margin, p.Y - margin); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = CardPoint(e.Location);
        int h = primaryText != null && primaryRect.Contains(p) ? 1 : secondaryText != null && secondaryRect.Contains(p) ? 2 : 0;
        if (h != hover) { hover = h; Cursor = h != 0 ? Cursors.Hand : Cursors.Default; Render(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { if (hover != 0) { hover = 0; Render(); } base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        var p = CardPoint(e.Location);
        if (primaryText != null && primaryRect.Contains(p) && PrimaryClick != null) PrimaryClick();
        else if (secondaryText != null && secondaryRect.Contains(p) && SecondaryClick != null) SecondaryClick();
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
        fade.Dispose(); logo.Dispose(); titleFont.Dispose(); bodyFont.Dispose();
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

// The WideAwake logo, embedded from assets\WideAwake.ico (see Build.cmd).
static class AppIcon
{
    public static Icon Get(int size)
    {
        using (var s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("WideAwake.ico"))
            return s != null ? new Icon(s, size, size) : (Icon)SystemIcons.Application.Clone();
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
// Plain-text event log in %LOCALAPPDATA%\WideAwake\WideAwake.log, rolled over to .old at 1 MB.
// Gaps between the 5-minute heartbeats show when the PC was asleep or in standby.
static class Log
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WideAwake");
    public static readonly string FilePath = Path.Combine(Dir, "WideAwake.log");
    static readonly object gate = new object();

    public static void Write(string message)
    {
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 1024 * 1024) { File.Copy(FilePath, FilePath + ".old", true); File.Delete(FilePath); }
                File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
            }
        }
        catch (Exception) { } // logging must never break the app
    }
}

static class Uninstaller
{
    const string Name = "WideAwake";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WideAwake";
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Name);
    static readonly string Exe = Path.Combine(Dir, Name + ".exe");
    static readonly string Shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name + ".lnk");
    static readonly string DesktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Name + ".lnk");

    public static void Run(bool quiet)
    {
        if (!quiet && MessageBox.Show("Remove WideAwake from this PC?", Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            StopRunning();
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) if (k != null) k.DeleteValue(Name, false);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\WideAwake", false); // settings
            if (File.Exists(Shortcut)) File.Delete(Shortcut);
            if (File.Exists(DesktopShortcut)) File.Delete(DesktopShortcut);

            bool runningFromDir = string.Equals(Path.GetDirectoryName(Path.GetFullPath(Application.ExecutablePath)), Dir, StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(Dir) && !runningFromDir) Directory.Delete(Dir, true);
            try { if (Directory.Exists(Log.Dir)) Directory.Delete(Log.Dir, true); } catch (Exception) { } // logs

            if (!quiet) MessageBox.Show("WideAwake has been removed.", Name, MessageBoxButtons.OK, MessageBoxIcon.Information);

            // A running exe can't delete itself, so a hidden cmd removes the folder once we've exited.
            if (runningFromDir)
                Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + Dir + "\"")
                    { CreateNoWindow = true, UseShellExecute = false });
        }
        catch (Exception ex) { if (!quiet) MessageBox.Show("Uninstall failed: " + ex.Message, Name, MessageBoxButtons.OK, MessageBoxIcon.Error); Environment.ExitCode = 1; }
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
    [DllImport("user32.dll", SetLastError = true)] static extern bool LockWorkStation();
    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize, dwTime; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr root, IntPtr scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerReadDCValueIndex(IntPtr root, IntPtr scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr mem);
    static Guid SubVideo = new Guid("7516b95f-f776-4464-8c53-06167f40cc99"), VideoIdle = new Guid("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
    const string SettingsKey = @"Software\WideAwake";
    const int CountdownMs = 5000;

    // --- State ---
    readonly NotifyIcon tray = new NotifyIcon();
    readonly ToolStripMenuItem keepAwakeItem, lightsOutItem, autoItem, startupItem;
    // Auto lights out: while plugged in we hold the display on (so Windows' screen timeout can't
    // send a Modern Standby laptop into standby) and black it out ourselves when idle instead.
    readonly System.Windows.Forms.Timer idleTimer = new System.Windows.Forms.Timer { Interval = 1000 };
    bool autoDark, sessionLocked; // autoDark: current lights out was started by the idle timer
    uint darkInputTime;           // last-input time when auto lights out started; any newer input ends it
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
        lightsOutItem = new ToolStripMenuItem("Lights out  (Ctrl+Alt+L, hold Esc 3s to exit)", null, (s, e) => RequestLightsOut(false));
        autoItem = new ToolStripMenuItem("Lights out automatically when idle") { Checked = ReadAutoSetting(), CheckOnClick = true };
        autoItem.CheckedChanged += (s, e) => { SaveAutoSetting(autoItem.Checked); UpdateAwake(); };
        batteryFloor = ReadInt("BatteryFloor", 50);
        var batteryMenu = new ToolStripMenuItem("On battery");
        foreach (int floor in new[] { 0, 50, 30 })
        {
            int f = floor;
            var item = new ToolStripMenuItem(f == 0 ? "Sleep normally" : "Stay awake down to " + f + "%") { Checked = batteryFloor == f, Tag = f };
            item.Click += (s, e) =>
            {
                batteryFloor = f;
                foreach (ToolStripMenuItem other in batteryMenu.DropDownItems) other.Checked = (int)other.Tag == f;
                WriteInt("BatteryFloor", f);
                batteryStopped = false;
                Log.Write("on battery: " + (f == 0 ? "sleep normally" : "stay awake down to " + f + "%"));
                UpdateAwake();
            };
            batteryMenu.DropDownItems.Add(item);
        }
        startupItem = new ToolStripMenuItem("Start with Windows") { Checked = IsStartup(), CheckOnClick = true };
        startupItem.CheckedChanged += (s, e) => SetStartup(startupItem.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.Add(keepAwakeItem);
        menu.Items.Add(lightsOutItem);
        menu.Items.Add(autoItem);
        menu.Items.Add(batteryMenu);
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open log", null, (s, e) =>
        {
            Log.Write("log opened from tray");
            try { Process.Start("notepad.exe", "\"" + Log.FilePath + "\""); }
            catch (Exception ex) { MessageBox.Show("Could not open the log: " + ex.Message, "WideAwake"); }
        });
        menu.Items.Add("Exit", null, (s, e) => Close());

        tray.Icon = AppIcon.Get(SystemInformation.SmallIconSize.Width);
        tray.ContextMenuStrip = menu;
        tray.Visible = true;
        tray.DoubleClick += (s, e) => RequestLightsOut(false);

        if (!RegisterHotKey(Handle, 1, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_L))
            tray.ShowBalloonTip(3000, "WideAwake", "Ctrl+Alt+L is in use by another app; use the tray menu instead.", ToolTipIcon.Warning);

        powerTimer.Tick += (s, e) => UpdateAwake();
        powerTimer.Start();
        keyLightTimer.Tick += (s, e) => { if (KeyboardLight.Get() > 0) KeyboardLight.Set(0); };
        idleTimer.Tick += (s, e) => IdleTick();
        idleTimer.Start();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        heartbeat.Tick += (s, e) => Log.Write("heartbeat: " + StateText() + ", idle " + ((uint)Environment.TickCount - LastInputTime()) / 1000 + " s");
        heartbeat.Start();
        // Display on/off/dimmed changes arrive as WM_POWERBROADCAST / PBT_POWERSETTINGCHANGE.
        RegisterPowerSettingNotification(Handle, ref ConsoleDisplayState, 0);
        Log.Write("started " + Application.ExecutablePath + " (pid " + Process.GetCurrentProcess().Id + "), screen timeout " + DisplayTimeout(true) + " s plugged in, " + DisplayTimeout(false) + " s on battery; on battery stay awake down to " + (batteryFloor > 0 ? batteryFloor + "%" : "off"));
        UpdateAwake();
    }

    readonly System.Windows.Forms.Timer heartbeat = new System.Windows.Forms.Timer { Interval = 300000 };
    [DllImport("user32.dll")] static extern IntPtr RegisterPowerSettingNotification(IntPtr hWnd, ref Guid setting, int flags);
    static Guid ConsoleDisplayState = new Guid("6fe69556-704a-47a0-8f24-c28d936fda47");
    string lastState;

    string StateText()
    {
        bool onAC = OnAC();
        return (onAC ? "AC" : "battery " + BatteryPercent() + "%" + (batteryStopped ? " (below floor, sleeping normally)" : batteryFloor > 0 ? " (staying awake down to " + batteryFloor + "%)" : "")) +
               ", keep awake " + (keepAwakeItem.Checked ? "on" : "off") + ", auto " + (autoItem.Checked ? "on" : "off") +
               ", flags 0x" + heldFlags.ToString("X") + (sessionLocked ? ", locked" : "") +
               (hook != IntPtr.Zero ? (autoDark ? ", lights out (auto)" : ", lights out") : "") + (lockDimmed ? ", lock screen dimmed" : "");
    }

    void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        Log.Write("session: " + e.Reason);
        if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.ConsoleDisconnect || e.Reason == SessionSwitchReason.RemoteConnect) sessionLocked = true;
        else if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.ConsoleConnect) { sessionLocked = false; Undim(); }
        UpdateAwake();
    }

    // Smart charging (e.g. an 80% limit) makes Windows report "on battery" while the charger is still
    // plugged in, and nothing tells that apart from a real unplug. So on battery we keep going down to
    // a floor; below it we sleep normally until the charger is back (no flapping around the floor).
    int batteryFloor;
    bool batteryStopped;
    static bool OnAC() { return SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online; }
    static int BatteryPercent() { float f = SystemInformation.PowerStatus.BatteryLifePercent; return f > 1 ? 100 : (int)Math.Round(f * 100); }
    bool PowerOK()
    {
        if (OnAC()) { batteryStopped = false; return true; }
        if (batteryFloor <= 0) return false;
        if (!batteryStopped && BatteryPercent() <= batteryFloor) { batteryStopped = true; Log.Write("battery at floor (" + BatteryPercent() + "%): letting Windows sleep normally until plugged in"); }
        return !batteryStopped;
    }

    static int ReadInt(string name, int def)
    {
        try { using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey)) return k == null ? def : Convert.ToInt32(k.GetValue(name, def)); }
        catch (Exception) { return def; }
    }

    static void WriteInt(string name, int value)
    {
        try { using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey)) k.SetValue(name, value, RegistryValueKind.DWord); }
        catch (Exception) { }
    }

    static bool ReadAutoSetting()
    {
        try { using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey)) return k == null || Convert.ToInt32(k.GetValue("AutoLightsOut", 1)) != 0; }
        catch (Exception) { return true; }
    }

    static void SaveAutoSetting(bool on)
    {
        try { using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey)) k.SetValue("AutoLightsOut", on ? 1 : 0, RegistryValueKind.DWord); }
        catch (Exception) { }
    }

    // Windows' "turn off my screen after" (plugged in or on battery), in seconds (0 = never, -1 = unknown).
    static int DisplayTimeout(bool ac)
    {
        IntPtr scheme;
        if (PowerGetActiveScheme(IntPtr.Zero, out scheme) != 0) return -1;
        try
        {
            uint secs;
            uint rc = ac ? PowerReadACValueIndex(IntPtr.Zero, scheme, ref SubVideo, ref VideoIdle, out secs) : PowerReadDCValueIndex(IntPtr.Zero, scheme, ref SubVideo, ref VideoIdle, out secs);
            return rc == 0 ? (int)Math.Min(secs, int.MaxValue) : -1;
        }
        finally { LocalFree(scheme); }
    }

    static uint LastInputTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
        return GetLastInputInfo(ref info) ? info.dwTime : (uint)Environment.TickCount;
    }

    // Auto lights out is active while we're keeping the PC awake on AC and Windows would otherwise
    // turn the screen off. Returns the timeout in ms, or 0. While the PC is locked we can't draw over
    // the lock screen, so we just dim it (see IdleTick) - but the display is still held on, because
    // letting Windows turn it off would put the PC into standby and drop SSH / Remote Desktop.
    int AutoTimeoutMs()
    {
        if (!autoItem.Checked || !keepAwakeItem.Checked || !PowerOK()) return 0;
        int t = DisplayTimeout(OnAC());
        if (t == 0) return 0;                       // screen never turns off: nothing to do
        return Math.Max(t < 0 ? 300 : t, 30) * 1000; // unknown: 5 min; at least 30 s
    }

    void IdleTick()
    {
        uint last = LastInputTime();
        if (hook != IntPtr.Zero)
        {
            // Any mouse/touchpad movement ends an auto lights out (key presses are handled in the hook).
            if (autoDark && last != darkInputTime) { Log.Write("auto lights out ended by mouse input"); Restore(true); }
            return;
        }
        if (lockDimmed)
        {
            if (last != dimInputTime || !sessionLocked) { Log.Write(sessionLocked ? "input at lock screen" : "unlocked"); Undim(); } // someone is at the lock screen
            return;
        }
        if (countdown != null) return;
        int timeout = AutoTimeoutMs();
        if (timeout == 0) return;
        uint idle = (uint)Environment.TickCount - last;
        if (sessionLocked) { if (idle >= timeout) Dim(); }
        else if (idle >= timeout - CountdownMs) RequestLightsOut(true);
    }

    // Lock screen version of lights out: brightness 0 and keyboard light off, nothing else.
    bool lockDimmed;
    uint dimInputTime;
    void Dim()
    {
        if (lockDimmed || hook != IntPtr.Zero) return;
        lockDimmed = true;
        Log.Write("lock screen idle: dimming (display held on so the PC stays reachable)");
        savedBrightness = GetBrightness();
        SetBrightness(0);
        savedKeyboardLight = KeyboardLight.Get();
        if (savedKeyboardLight > 0) KeyboardLight.Set(0);
        keyLightTimer.Start();
        dimInputTime = LastInputTime();
    }

    void Undim()
    {
        if (!lockDimmed) return;
        lockDimmed = false;
        Log.Write("lock screen brightness restored");
        keyLightTimer.Stop();
        if (savedBrightness >= 0) SetBrightness(savedBrightness);
        if (savedKeyboardLight > 0) KeyboardLight.Set(savedKeyboardLight);
    }

    protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); } // never show a window

    // Hold the system awake only while on AC power.
    // During lights out we also hold the display "on": on Modern Standby laptops, the display
    // turning off (by timeout or SC_MONITORPOWER) puts the whole PC into standby, which drops
    // network access. Instead we black the screen ourselves (black pixels are off on OLED).
    void UpdateAwake()
    {
        bool onAC = OnAC();
        bool want = keepAwakeItem.Checked && PowerOK();
        bool dark = hook != IntPtr.Zero;
        int autoMs = AutoTimeoutMs();
        uint flags = ES_CONTINUOUS | (want ? ES_SYSTEM_REQUIRED : 0) | (want && (dark || autoMs > 0) ? ES_DISPLAY_REQUIRED : 0);
        if (flags != heldFlags)
        {
            if (SetThreadExecutionState(flags) == 0) Log.Write("SetThreadExecutionState(0x" + flags.ToString("X") + ") FAILED");
            heldFlags = flags;
        }
        string state = StateText();
        if (state != lastState) { Log.Write("state: " + state); lastState = state; }

        string text = "WideAwake - " + (want ? (onAC ? "keeping PC awake" : "awake on battery " + BatteryPercent() + "%") : onAC || !keepAwakeItem.Checked ? "paused" : "on battery, normal sleep");
        if (dark) text += " | LIGHTS OUT";
        else if (autoMs > 0) text += " | lights out after " + (autoMs >= 60000 ? autoMs / 60000 + " min" : autoMs / 1000 + " s") + " idle";
        tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
    }

    // Shows a countdown card first; Esc/Cancel aborts, Enter/"Start now" skips the wait.
    // auto: started by the idle timer. The card doesn't take focus, any input cancels it,
    // and the lights out it starts ends on any input and then locks the PC.
    void RequestLightsOut(bool auto)
    {
        if (hook != IntPtr.Zero || countdown != null) return;
        const int total = CountdownMs;
        var card = new FluentToast("Lights out in 5",
            auto ? "You've been away for a while. The screen and keyboard light will turn off and your PC stays reachable. Move the mouse or press a key to cancel."
                 : "The screen and keyboard light will turn off. Your PC stays awake and reachable. Hold Esc for 3 seconds when you're back, then sign in.",
            !auto, "Start now", "Cancel", true);
        countdown = card;
        Log.Write("lights out countdown started" + (auto ? " (idle)" : ""));
        uint inputAtStart = LastInputTime();
        var clock = Stopwatch.StartNew();
        var tick = new System.Windows.Forms.Timer { Interval = 30 };
        bool done = false;
        Action<bool> finish = go =>
        {
            if (done) return;
            done = true;
            tick.Dispose();
            countdown = null;
            Log.Write(go ? "countdown finished" : "countdown cancelled"); if (go) { card.Close(); LightsOut(auto); }
            else card.FadeOutAndClose();
        };
        tick.Tick += (s, e) =>
        {
            if (auto && LastInputTime() != inputAtStart) { finish(false); return; } // user is back
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

    void LightsOut(bool auto)
    {
        if (hook != IntPtr.Zero) return;
        autoDark = auto;
        Log.Write("lights out on" + (auto ? " (auto)" : ""));
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
        darkInputTime = LastInputTime(); // after the setup above, so nothing it does counts as input
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
        Log.Write("lights out off" + (notify ? ", locking PC" : ""));
        autoDark = false;
        UpdateAwake();
        // Lights out stands in for Windows turning the screen off (which would have locked the PC),
        // so coming back always goes through the lock screen - Windows Hello signs you straight in -
        // instead of handing over an unlocked desktop. The screen stays black while you're away.
        if (notify && !LockWorkStation()) Log.Write("LockWorkStation FAILED (error " + Marshal.GetLastWin32Error() + ")");
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
            // Automatic lights out ends on any key (the PC is then locked); the key itself is swallowed.
            if (down && autoDark) { BeginInvoke((Action)(() => { if (hook != IntPtr.Zero) Log.Write("auto lights out ended by key press"); Restore(true); })); return (IntPtr)1; }

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
        if (m.Msg == WM_HOTKEY && (int)m.WParam == 1) RequestLightsOut(false);
        else if (m.Msg == WM_POWERBROADCAST) { LogPowerBroadcast(m); UpdateAwake(); } // plugged/unplugged, display, suspend/resume
        base.WndProc(ref m);
    }

    void LogPowerBroadcast(Message m)
    {
        int ev = (int)m.WParam;
        if (ev == 0x8013 && m.LParam != IntPtr.Zero) // PBT_POWERSETTINGCHANGE: POWERBROADCAST_SETTING { GUID; DWORD len; data }
        {
            var guid = (Guid)Marshal.PtrToStructure(m.LParam, typeof(Guid));
            if (guid == ConsoleDisplayState)
            {
                int v = Marshal.ReadInt32(m.LParam, 20);
                Log.Write("display " + (v == 0 ? "OFF" : v == 1 ? "on" : v == 2 ? "dimmed" : "state " + v));
            }
        }
        else if (ev == 0x4) Log.Write("suspending (sleep)");
        else if (ev == 0x7 || ev == 0x12) Log.Write("resumed from sleep");
        else if (ev == 0xA) Log.Write("power status changed: " + SystemInformation.PowerStatus.PowerLineStatus);
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
        Undim();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        UnregisterHotKey(Handle, 1);
        SetThreadExecutionState(ES_CONTINUOUS); // let Windows sleep normally again
        Log.Write("exiting");
        tray.Visible = false;
        tray.Dispose();
        base.OnFormClosed(e);
    }
}
