// WideAwakeSetup - setup wizard with WideAwake.exe embedded as a resource.
// Build (after building WideAwake.exe):
//   csc /target:winexe /out:WideAwakeSetup.exe /resource:WideAwake.exe,WideAwake.exe Setup.cs
// Per-user install, no admin rights needed. Uninstall via Settings > Apps (runs WideAwake.exe /uninstall).
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

static class SetupProgram
{
    [STAThread]
    static int Main(string[] args)
    {
        // /silent (also /S, /quiet): install with defaults and no UI - used by winget.
        foreach (var a in args)
        {
            string x = a.ToLowerInvariant();
            if (x == "/silent" || x == "/s" || x == "/quiet" || x == "/verysilent")
            {
                try { Wizard.Install(true, false); return 0; }
                catch (Exception ex) { Console.Error.WriteLine("Install failed: " + ex.Message); return 1; }
            }
        }
        Application.EnableVisualStyles();
        Application.Run(new Wizard());
        return 0;
    }
}

class Wizard : Form
{
    // Must match the paths/keys used by WideAwake.exe /uninstall.
    const string AppName = "WideAwake", Version = "1.8";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WideAwake";
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
    static readonly string Exe = Path.Combine(Dir, AppName + ".exe");
    static readonly string StartMenuLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
    static readonly string DesktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    readonly Label title = new Label(), subtitle = new Label();
    readonly Panel[] pages = new Panel[3];
    readonly Button back = new Button(), next = new Button(), cancel = new Button();
    readonly CheckBox startup = new CheckBox(), desktop = new CheckBox(), launch = new CheckBox();
    int page;

    public Wizard()
    {
        Text = AppName + " Setup";
        Icon = LoadIcon(32);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 340);
        Font = new Font("Segoe UI", 9f);

        // Header
        var header = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Color.White };
        title.SetBounds(20, 12, 480, 22); title.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        subtitle.SetBounds(20, 36, 480, 20);
        var icon = new PictureBox { Image = LoadIcon(40).ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom };
        icon.SetBounds(456, 12, 48, 40);
        header.Controls.AddRange(new Control[] { title, subtitle, icon });
        var line = new Label { Dock = DockStyle.Top, Height = 1, BackColor = SystemColors.ControlDark };

        // Buttons
        back.Text = "< Back"; back.SetBounds(256, 302, 80, 26); back.Click += (s, e) => ShowPage(page - 1);
        next.SetBounds(338, 302, 80, 26); next.Click += (s, e) => Next();
        cancel.Text = "Cancel"; cancel.SetBounds(428, 302, 80, 26); cancel.Click += (s, e) => Close();
        AcceptButton = next;

        bool upgrade = File.Exists(Exe);

        pages[0] = Page(
            Text1("This will install " + AppName + " " + Version + " on your computer.\n\n" +
                  AppName + " keeps your PC awake while it's plugged in. Its Lights out mode turns the screen off " +
                  "and ignores the keyboard (Ctrl+Alt+L to start, hold Esc for 3 seconds to exit).\n\n" +
                  (upgrade ? "An existing installation was found and will be updated.\n\n" : "") +
                  "Install location:\n" + Dir + "\n\nNo administrator rights are needed.", 230));

        startup.Text = "Start " + AppName + " when Windows starts"; startup.Checked = true; startup.SetBounds(0, 0, 440, 24);
        desktop.Text = "Create a desktop shortcut"; desktop.Checked = File.Exists(DesktopLnk); desktop.SetBounds(0, 28, 440, 24);
        var note = Text1("A Start menu shortcut is always created. You can uninstall " + AppName +
                         " later from Settings > Apps > Installed apps.", 60);
        note.Top = 70;
        pages[1] = Page(startup, desktop, note);

        launch.Text = "Launch " + AppName + " now"; launch.Checked = true; launch.SetBounds(0, 110, 440, 24);
        pages[2] = Page(Text1(AppName + " has been installed.\n\nIt runs in the system tray (look for the eye icon " +
                              "near the clock; it may be under the ^ arrow).", 100), launch);

        Controls.AddRange(pages);
        Controls.AddRange(new Control[] { back, next, cancel, line, header });
        ShowPage(0);
    }

    static Icon LoadIcon(int size)
    {
        using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("WideAwake.ico"))
            return st != null ? new Icon(st, size, size) : (Icon)SystemIcons.Application.Clone();
    }

    static Panel Page(params Control[] items)
    {
        var p = new Panel();
        p.SetBounds(40, 85, 450, 205);
        p.Controls.AddRange(items);
        return p;
    }

    static Label Text1(string text, int height)
    {
        var l = new Label { Text = text };
        l.SetBounds(0, 0, 440, height);
        return l;
    }

    void ShowPage(int n)
    {
        page = n;
        for (int i = 0; i < pages.Length; i++) pages[i].Visible = i == n;
        string[] titles = { "Welcome to " + AppName + " Setup", "Choose options", "Setup complete" };
        string[] subs = { "Keep your PC awake while plugged in.", "Pick how " + AppName + " starts.", "" };
        title.Text = titles[n]; subtitle.Text = subs[n];
        back.Visible = n == 1;
        next.Text = n == 0 ? "Next >" : n == 1 ? "Install" : "Finish";
        cancel.Enabled = n != 2;
    }

    void Next()
    {
        if (page == 0) { ShowPage(1); return; }
        if (page == 1)
        {
            UseWaitCursor = true; Enabled = false;
            try { Install(startup.Checked, desktop.Checked); ShowPage(2); }
            catch (Exception ex) { MessageBox.Show(this, "Install failed: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { UseWaitCursor = false; Enabled = true; }
            return;
        }
        if (launch.Checked) Process.Start(Exe);
        Close();
    }

    public static void Install(bool runAtStartup, bool desktopShortcut)
    {
        // Close a running copy so the exe can be replaced.
        foreach (var p in Process.GetProcessesByName(AppName))
            using (p) { try { p.Kill(); p.WaitForExit(5000); } catch (Exception) { } }

        Directory.CreateDirectory(Dir);
        // Retry for a few seconds: right after an uninstall, antivirus or Explorer can briefly
        // hold the old file (pending delete), which makes the write fail with "access denied".
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream(AppName + ".exe"))
                {
                    if (res == null) throw new InvalidOperationException("Setup is damaged (WideAwake.exe is missing from it).");
                    using (var f = File.Create(Exe)) res.CopyTo(f);
                }
                break;
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is UnauthorizedAccessException) || attempt >= 20) throw;
                System.Threading.Thread.Sleep(500);
            }
        }

        CreateShortcut(StartMenuLnk, Exe);
        if (desktopShortcut) CreateShortcut(DesktopLnk, Exe);
        else if (File.Exists(DesktopLnk)) File.Delete(DesktopLnk);

        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (runAtStartup) k.SetValue(AppName, "\"" + Exe + "\"");
            else k.DeleteValue(AppName, false);
        }

        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", AppName);
            k.SetValue("DisplayVersion", Version);
            k.SetValue("Publisher", "Kyle Pantall");
            k.SetValue("DisplayIcon", Exe);
            k.SetValue("InstallLocation", Dir);
            k.SetValue("UninstallString", "\"" + Exe + "\" /uninstall");
            k.SetValue("QuietUninstallString", "\"" + Exe + "\" /uninstall /quiet");
            k.SetValue("URLInfoAbout", "https://github.com/kylepantall/WideAwake");
            k.SetValue("EstimatedSize", (int)Math.Max(1, new FileInfo(Exe).Length / 1024), RegistryValueKind.DWord);
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
    }

    static void CreateShortcut(string lnkPath, string target)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(shellType);
        object lnk = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
        Type t = lnk.GetType();
        t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
        t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(target) });
        t.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
        Marshal.FinalReleaseComObject(lnk);
        Marshal.FinalReleaseComObject(shell);
    }
}
