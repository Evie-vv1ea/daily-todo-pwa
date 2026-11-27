using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [STAThread]
    private static void Main(string[] args)
    {
        if (Array.IndexOf(args, "--shutdown") >= 0)
        {
            EventWaitHandle request;
            if (EventWaitHandle.TryOpenExisting(DesktopNote.ShutdownEventName, out request))
                using (request) request.Set();
            return;
        }
        bool check = Array.IndexOf(args, "--self-test") >= 0;
        bool created;
        using (var instance = new Mutex(true, check ? @"Local\to-do-list-check" : @"Local\to-do-list", out created))
        {
            if (!created) return;
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch (EntryPointNotFoundException) { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var note = new DesktopNote(check))
            {
                Application.Run();
            }
        }
    }

}

internal sealed class HotCorner
{
    private DateTime? entered;
    private DateTime? left;

    public bool ShouldShow(Point cursor, Rectangle screen, int edge, DateTime now)
    {
        var corner = new Rectangle(screen.Location, new Size(edge, edge));
        if (!corner.Contains(cursor))
        {
            entered = null;
            return false;
        }
        if (!entered.HasValue) entered = now;
        return now - entered.Value >= TimeSpan.FromMilliseconds(120);
    }

    public bool ShouldHide(Point cursor, Rectangle panel, Rectangle screen, int edge, bool keepOpen, DateTime now)
    {
        var corner = new Rectangle(screen.Location, new Size(edge, edge));
        if (keepOpen || panel.Contains(cursor) || corner.Contains(cursor))
        {
            left = null;
            return false;
        }
        if (!left.HasValue) left = now;
        return now - left.Value >= TimeSpan.FromMilliseconds(260);
    }

    public void Reset()
    {
        entered = null;
        left = null;
    }
}

internal sealed class NoteMotion
{
    public double Progress { get; private set; }
    public double Target { get; private set; }
    private double startProgress;
    private long startTick;
    private double duration;

    public void Begin(double target, long timestamp)
    {
        startProgress = Progress;
        Target = target;
        startTick = timestamp;
        duration = (target == 1 ? .160 : .180) * Math.Abs(target - startProgress);
    }

    public bool Advance(long timestamp)
    {
        double elapsed = (timestamp - startTick) / (double)Stopwatch.Frequency;
        double t = duration == 0 ? 1 : Math.Max(0, Math.Min(1, elapsed / duration));
        double remaining = 1 - t;
        Progress = t == 1 ? Target : startProgress + (Target - startProgress) * (1 - remaining * remaining * remaining);
        return t == 1;
    }

    public static Point Position(Point anchor, int travel, double progress)
    {
        return new Point(anchor.X, anchor.Y - (int)Math.Round(travel * (1 - progress)));
    }
}

internal sealed class DesktopNote : Form
{
    private const string LocalPage = "https://desktop.to-do-list.local/index.html";
    private readonly string install = AppDomain.CurrentDomain.BaseDirectory;
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 60 };
    private readonly System.Windows.Forms.Timer animTimer = new System.Windows.Forms.Timer { Interval = 16 };
    private readonly NoteMotion motion = new NoteMotion();
    private double targetOpacity = .88;
    private readonly HotCorner corner = new HotCorner();
    private readonly WebView2 web = new WebView2();
    private readonly bool selfTest;
    private FileSystemWatcher pageWatcher;
    private long lastPageChange;
    private bool ready;
    private bool keepOpen;
    internal const string ShutdownEventName = @"Local\to-do-list-shutdown";
    private readonly ContextMenuStrip noteMenu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
    private readonly EventWaitHandle shutdownRequest;
    private bool pinned;
    private bool secondaryPanel;
    private bool sheetOpen;
    private DateTime menuGraceUntil;
    private Color calendarBorder;
    private Color calendarAccent;
    private Color calendarHighlight;
    private DateTime calendarViewDate = DateTime.Today;
    private DateTime? calendarTaskDate;
    private string noteTaskKey = "";
    private string noteText = "";
    private HashSet<DateTime> calendarDots = new HashSet<DateTime>();
    private Color ink = Color.FromArgb(82, 58, 66);
    private int preferredHeight = 72;
    private int screenDpi = 96;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var options = base.CreateParams;
            options.ExStyle |= 0x00000080;
            return options;
        }
    }

    internal DesktopNote(bool check)
    {
        selfTest = check;
        if (!check) shutdownRequest = new EventWaitHandle(false, EventResetMode.AutoReset, ShutdownEventName);
        Text = "to-do-list";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        noteMenu.Font = new Font("Microsoft YaHei UI", 9F);
        SetPaper(Color.FromArgb(255, 240, 243), Color.FromArgb(223, 178, 190),
            Color.FromArgb(82, 58, 66), Color.FromArgb(184, 107, 130));
        Opacity = targetOpacity;
        web.Dock = DockStyle.Fill;
        Controls.Add(web);
        timer.Tick += OnTick;
        noteMenu.Closed += delegate { ApplyPanelBounds(); };
        animTimer.Tick += AnimateNote;
        Deactivate += CloseSheetOnDeactivate;
        FormClosed += delegate { Application.ExitThread(); };
        Application.Idle += OnFirstIdle;
        ApplyPanelBounds();
    }

    private async void OnFirstIdle(object sender, EventArgs args)
    {
        Application.Idle -= OnFirstIdle;
        var formHandle = Handle;
        var webHandle = web.Handle;
        ApplyPanelBounds();
        await StartWebView();
    }

    private async System.Threading.Tasks.Task StartWebView()
    {
        try
        {
            var profile = Path.Combine(install, selfTest ? "TestProfileCream-" + Guid.NewGuid().ToString("N") : "Profile");
            var environment = await CoreWebView2Environment.CreateAsync(null, profile);
            await web.EnsureCoreWebView2Async(environment);
            var core = web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs e)
            {
                if (e.Uri != LocalPage) e.Cancel = true;
            };
            core.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs e)
            {
                e.Handled = true;
            };
            core.SetVirtualHostNameToFolderMapping("desktop.to-do-list.local", Path.Combine(install, "app"),
                CoreWebView2HostResourceAccessKind.Deny);
            core.WebMessageReceived += OnMessage;
            core.NavigationCompleted += async delegate(object sender, CoreWebView2NavigationCompletedEventArgs e)
            {
                if (!e.IsSuccess)
                {
                    Environment.ExitCode = 1;
                    WriteStatus("error " + e.WebErrorStatus);
                    return;
                }
                ready = true;
                if (selfTest)
                {
                    try { await CheckDesktop(); }
                    catch (Exception ex) { Environment.ExitCode = 1; WriteStatus("error " + ex); }
                    Close();
                }
                else
                {
                    WriteStatus("ready");
                    StartPageWatcher();
                    timer.Start();
                    if (pinned && !Visible) ShowNote();
                }
            };
            core.Navigate(LocalPage);
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            WriteStatus("error " + ex);
            Application.ExitThread();
        }
    }

    private void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (e.Source != LocalPage) return;
        var message = e.TryGetWebMessageAsString();
        if (message.StartsWith("paper:", StringComparison.Ordinal))
        {
            var palette = message.Split(':');
            Color paper, border, text, accent;
            if (palette.Length == 5 && TryRgb(palette[1], out paper) && TryHex(palette[2], out border) &&
                TryHex(palette[3], out text) && TryHex(palette[4], out accent))
                SetPaper(paper, border, text, accent);
            return;
        }
        if (message.StartsWith("opacity:", StringComparison.Ordinal))
        {
            var opacity = message.Substring(8);
            if (opacity == "0.80" || opacity == "0.88" || opacity == "0.95")
            {
                targetOpacity = Double.Parse(opacity, CultureInfo.InvariantCulture);
                if (animTimer.Enabled) Opacity = targetOpacity * motion.Progress;
                else if (Visible || selfTest) Opacity = targetOpacity;
            }
            return;
        }
        if (message == "pin:1" || message == "pin:0")
        {
            pinned = message == "pin:1";
            corner.Reset();
            if (pinned && ready && !Visible) ShowNote();
            return;
        }
        if (message == "panel:1" || message == "panel:0")
        {
            secondaryPanel = message == "panel:1";
            corner.Reset();
            return;
        }
        if (message == "sheet:1" || message == "sheet:0")
        {
            sheetOpen = message == "sheet:1";
            corner.Reset();
            return;
        }
        if (message.StartsWith("notemeta:", StringComparison.Ordinal))
        {
            SetNoteMetadata(message.Substring(9));
            return;
        }
        if (message.StartsWith("calmeta:", StringComparison.Ordinal))
        {
            SetCalendarMetadata(message);
            return;
        }
        if (message.StartsWith("menu:", StringComparison.Ordinal))
        {
            ShowNoteMenu(message);
            return;
        }
        if (message == "exit")
        {
            Close();
            return;
        }
        if (message == "focus")
        {
            if (Visible)
            {
                keepOpen = true;
                SetForegroundWindow(Handle);
                Activate();
                web.Focus();
                web.CoreWebView2.PostWebMessageAsString("focus-ready");
            }
            return;
        }
        if (message == "hold")
        {
            keepOpen = true;
            return;
        }
        if (message == "release")
        {
            keepOpen = false;
            return;
        }
        int height;
        if (message.StartsWith("height:") && Int32.TryParse(message.Substring(7), out height))
        {
            preferredHeight = Math.Max(72, Math.Min(620, height));
            if (Visible) ApplyPanelBounds();
        }
    }

    private static bool TryRgb(string value, out Color color)
    {
        color = Color.Empty;
        var components = value.Split(',');
        byte red, green, blue;
        if (components.Length != 3 || !Byte.TryParse(components[0], out red) ||
            !Byte.TryParse(components[1], out green) || !Byte.TryParse(components[2], out blue)) return false;
        color = Color.FromArgb(red, green, blue);
        return true;
    }

    private static bool TryHex(string value, out Color color)
    {
        color = Color.Empty;
        int hex;
        if (value.Length != 7 || value[0] != '#' ||
            !Int32.TryParse(value.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hex)) return false;
        color = Color.FromArgb((hex >> 16) & 255, (hex >> 8) & 255, hex & 255);
        return true;
    }

    private void SetPaper(Color paper, Color border, Color text, Color accent)
    {
        BackColor = paper;
        web.DefaultBackgroundColor = paper;
        ink = text;
        noteMenu.BackColor = paper;
        noteMenu.ForeColor = ink;
        var menuColors = new CreamMenuColors(paper, border, accent);
        calendarBorder = border;
        calendarAccent = accent;
        calendarHighlight = menuColors.MenuItemSelected;
        noteMenu.Renderer = new ToolStripProfessionalRenderer(menuColors) {
            RoundedEdges = false
        };
    }

    private void ShowNoteMenu(string message)
    {
        var parts = message.Split(':');
        int x, y;
        if (!Visible || parts.Length != 8 || !Int32.TryParse(parts[1], out x) || !Int32.TryParse(parts[2], out y)) return;
        if (noteMenu.Visible) noteMenu.Close();
        while (noteMenu.Items.Count > 0)
        {
            var previous = noteMenu.Items[0];
            noteMenu.Items.RemoveAt(0);
            previous.Dispose();
        }

        var items = noteMenu.Items;
        if (parts[3] == "1")
        {
            items.Add(NoteFlyoutItem());
            items.Add(CalendarMenuItem("taskDay", "切换时间", calendarTaskDate ?? calendarViewDate));
            items.Add(MenuItem("delay", "延期一天"));
            items.Add(MenuItem("delete", "删除任务"));
            items.Add(new ToolStripSeparator());
        }
        if (parts[7] == "1") items.Add(MenuItem("tasks", "返回待办"));
        items.Add(MenuItem("new", "新增任务"));
        items.Add(MenuItem("clearDone", "清理已完成"));
        items.Add(Submenu("theme", "便签颜色", parts[4], new [] {
            "rose", "butter", "sage", "lilac", "oat", "sky"
        }, new [] {
            "奶油藕粉", "黄油奶黄", "抹茶鼠尾草", "芋泥淡紫", "海盐燕麦", "冰川奶蓝"
        }));
        items.Add(Submenu("opacity", "透明度", parts[5], new [] {
            "0.80", "0.88", "0.95"
        }, new [] { "轻透 (80%)", "柔和 (88%)", "清晰 (95%)" }));
        items.Add(Submenu("filter", "筛选任务", parts[6], new [] {
            "today", "all", "postponed"
        }, new [] { "当日", "全部", "延期" }));
        items.Add(CalendarMenuItem("calendar", "切换日期", calendarViewDate));
        items.Add(MenuItem("pin", "固定在左上角", pinned));
        items.Add(new ToolStripSeparator());
        items.Add(MenuItem("add", "新增选项（本周每天）"));
        items.Add(MenuItem("clipboard", "剪贴板"));
        items.Add(Submenu("tool", "工具", null, new [] { "add30", "usd" }, new [] { "划线价", "换美金" }));
        items.Add(new ToolStripSeparator());
        items.Add(MenuItem("exit", "退出桌面便签"));

        var point = PointToScreen(new Point(
            Math.Max(0, Math.Min(ClientSize.Width - 1, Scale(x))),
            Math.Max(0, Math.Min(ClientSize.Height - 1, Scale(y)))
        ));
        // Reserve space for the root menu and its calendar to the right of the note.
        var work = Screen.FromPoint(point).WorkingArea;
        int menuWidth = noteMenu.GetPreferredSize(Size.Empty).Width;
        int flyoutWidth = Scale(PixelCalendarPanel.LogicalWidth + 6);
        if (work.Right - Right >= menuWidth + flyoutWidth + Scale(2))
            point.X = Right + Scale(2);
        else
            point.Y = Bottom + Scale(2);
        SetForegroundWindow(Handle);
        noteMenu.Show(point);
    }

    private void SetCalendarMetadata(string message)
    {
        var fields = message.Substring(8).Split('|');
        if (fields.Length != 3) return;
        calendarViewDate = ParseCalendarDate(fields[0]) ?? DateTime.Today;
        calendarTaskDate = ParseCalendarDate(fields[1]);
        var dates = new HashSet<DateTime>();
        foreach (string part in fields[2].Split(','))
        {
            var date = ParseCalendarDate(part);
            if (date.HasValue) dates.Add(date.Value);
        }
        calendarDots = dates;
    }

    private static DateTime? ParseCalendarDate(string value)
    {
        DateTime date;
        return DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out date) ? (DateTime?)date.Date : null;
    }

    private void SetNoteMetadata(string payload)
    {
        var fields = payload.Split(new [] { '|' }, 2);
        noteTaskKey = "";
        noteText = "";
        if (fields.Length != 2 || fields[0].Length == 0) return;
        try
        {
            noteText = Encoding.UTF8.GetString(Convert.FromBase64String(fields[1]));
            noteTaskKey = fields[0];
        }
        catch (FormatException) { }
    }

    private ToolStripMenuItem NoteFlyoutItem()
    {
        // Bind the editor to the original task even if a new context menu is requested while it closes.
        string taskKey = noteTaskKey;
        var panel = new PixelNotePanel(screenDpi, BackColor, calendarBorder, ink, calendarAccent, noteText,
            text =>
            {
                if (taskKey.Length > 0)
                    web.CoreWebView2.PostWebMessageAsString("notesave:" + taskKey + "|" +
                        Convert.ToBase64String(Encoding.UTF8.GetBytes(text)));
            }, () => noteMenu.Close());
        var item = HostSidePanel("note", "备注", panel);
        item.DropDown.Opened += delegate
        {
            panel.BeginInvoke(new Action(() =>
            {
                if (panel.IsDisposed || !item.DropDown.Visible) return;
                SetForegroundWindow(item.DropDown.Handle);
                panel.FocusInput();
            }));
        };
        item.DropDown.Closing += delegate
        {
            panel.Flush();
            menuGraceUntil = DateTime.UtcNow.AddMilliseconds(500);
        };
        return item;
    }

    private ToolStripMenuItem CalendarMenuItem(string command, string label, DateTime selected)
    {
        var panel = new PixelCalendarPanel(screenDpi, BackColor, calendarBorder, ink, calendarAccent,
            calendarHighlight, selected, calendarDots, date =>
            {
                menuGraceUntil = DateTime.UtcNow.AddMilliseconds(500);
                noteMenu.Close();
                web.CoreWebView2.PostWebMessageAsString("command:" + command + "/" +
                    date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            });
        return HostSidePanel(command, label, panel);
    }

    private ToolStripMenuItem HostSidePanel(string command, string label, Control panel)
    {
        var item = new ToolStripMenuItem(label) {
            Name = command, ForeColor = ink, Margin = new Padding(0, 1, 0, 1), Padding = new Padding(6, 3, 8, 3),
            DropDownDirection = ToolStripDropDownDirection.Right
        };
        var dropdown = (ToolStripDropDownMenu)item.DropDown;
        dropdown.Renderer = noteMenu.Renderer;
        dropdown.BackColor = BackColor;
        dropdown.ShowImageMargin = false;
        dropdown.ShowCheckMargin = false;
        dropdown.Padding = new Padding(1);

        item.DropDownItems.Add(new ToolStripControlHost(panel) {
            Padding = Padding.Empty, Margin = Padding.Empty, AutoSize = false, Size = panel.Size
        });
        item.Click += delegate { item.ShowDropDown(); };
        return item;
    }

    private ToolStripMenuItem MenuItem(string command, string label, bool isChecked = false)
    {
        var item = new ToolStripMenuItem(label) {
            Name = command, Checked = isChecked, ForeColor = ink, Margin = new Padding(0, 1, 0, 1), Padding = new Padding(6, 3, 8, 3)
        };
        item.Click += delegate
        {
            if (command != "pin" && command != "exit") menuGraceUntil = DateTime.UtcNow.AddMilliseconds(400);
            web.CoreWebView2.PostWebMessageAsString("command:" + command);
        };
        return item;
    }

    private ToolStripMenuItem Submenu(string key, string label, string selected, string[] values, string[] labels)
    {
        var item = new ToolStripMenuItem(label) {
            Name = key, ForeColor = ink, Margin = new Padding(0, 1, 0, 1), Padding = new Padding(6, 3, 8, 3)
        };
        var dropdown = (ToolStripDropDownMenu)item.DropDown;
        dropdown.Renderer = noteMenu.Renderer;
        dropdown.ShowImageMargin = false;
        dropdown.ShowCheckMargin = true;
        for (var i = 0; i < values.Length; i++)
            item.DropDownItems.Add(MenuItem(key + "/" + values[i], labels[i], selected == values[i]));
        return item;
    }

    private void StartPageWatcher()
    {
        if (selfTest || pageWatcher != null) return;
        pageWatcher = new FileSystemWatcher(Path.Combine(install, "app"), "index.html")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        pageWatcher.Changed += QueuePageReload;
        pageWatcher.Created += QueuePageReload;
        pageWatcher.Renamed += QueuePageReload;
        pageWatcher.EnableRaisingEvents = true;
    }

    private void QueuePageReload(object sender, FileSystemEventArgs args)
    {
        Interlocked.Exchange(ref lastPageChange, Stopwatch.GetTimestamp());
    }

    private void OnTick(object sender, EventArgs args)
    {
        if (shutdownRequest != null && shutdownRequest.WaitOne(0)) { Close(); return; }
        // The WinForms timer runs on the UI thread, unlike the file watcher.
        long changedAt = Interlocked.Read(ref lastPageChange);
        if (changedAt != 0 && Stopwatch.GetTimestamp() - changedAt >= Stopwatch.Frequency / 4 &&
            Interlocked.CompareExchange(ref lastPageChange, 0, changedAt) == changedAt)
        {
            web.CoreWebView2.Reload();
        }
        TrackCursor(Cursor.Position, DateTime.UtcNow);
    }

    private void CloseSheetOnDeactivate(object sender, EventArgs args)
    {
        if (sheetOpen && !noteMenu.Visible)
            web.CoreWebView2.PostWebMessageAsString("close-sheet");
    }

    private void TrackCursor(Point pointer, DateTime now)
    {
        if (!ready) return;
        var primary = Screen.PrimaryScreen;
        int edge = Math.Max(8, Scale(14));
        if (!Visible)
        {
            if (!corner.ShouldShow(pointer, primary.Bounds, edge, now)) return;
            ShowNote();
            return;
        }
        bool hold = pinned || secondaryPanel || sheetOpen || noteMenu.Visible || (keepOpen && ContainsFocus) || now < menuGraceUntil;
        var anchor = new Rectangle(primary.WorkingArea.Location, Size);
        if (Bounds.Contains(pointer) || anchor.Contains(pointer)) hold = true;
        if (animTimer.Enabled && motion.Target == 0)
        {
            var hot = new Rectangle(primary.Bounds.Location, new Size(edge, edge));
            if (hold || hot.Contains(pointer))
            {
                corner.Reset();
                StartNoteMotion(1);
            }
            return;
        }
        if (corner.ShouldHide(pointer, Bounds, primary.Bounds, edge, hold, now)) StartNoteMotion(0);
    }

    private void ShowNote()
    {
        if (Visible) return;
        ApplyPanelBounds();
        motion.Begin(0, Stopwatch.GetTimestamp());
        motion.Advance(Stopwatch.GetTimestamp());
        if (!selfTest) DrawMotionFrame();
        Show();
        StartNoteMotion(1);
        web.CoreWebView2.PostWebMessageAsString("shown");
        corner.Reset();
    }

    private void StartNoteMotion(double target)
    {
        motion.Begin(target, Stopwatch.GetTimestamp());
        if (selfTest)
        {
            motion.Advance(Stopwatch.GetTimestamp() + Stopwatch.Frequency);
            FinishNoteMotion();
            return;
        }
        animTimer.Start();
    }

    private void AnimateNote(object sender, EventArgs args)
    {
        if (motion.Target == 0) TrackCursor(Cursor.Position, DateTime.UtcNow);
        bool complete = motion.Advance(Stopwatch.GetTimestamp());
        if (complete) FinishNoteMotion();
        else DrawMotionFrame();
    }

    private void DrawMotionFrame()
    {
        var anchor = Screen.PrimaryScreen.WorkingArea.Location;
        var frame = NoteMotion.Position(anchor, Scale(8), motion.Progress);
        const uint moveOnly = 0x0001 | 0x0004 | 0x0010; // NOSIZE | NOZORDER | NOACTIVATE
        SetWindowPos(Handle, IntPtr.Zero, frame.X, frame.Y, 0, 0, moveOnly);
        Opacity = targetOpacity * motion.Progress;
    }

    private void FinishNoteMotion()
    {
        animTimer.Stop();
        if (motion.Target == 1)
        {
            ApplyPanelBounds();
            Opacity = targetOpacity;
            return;
        }
        if (!selfTest) DrawMotionFrame();
        Hide();
        keepOpen = false;
        web.CoreWebView2.PostWebMessageAsString("hidden");
        ApplyPanelBounds();
        corner.Reset();
    }

    private int Scale(int value)
    {
        return (value * screenDpi + 48) / 96;
    }

    private void ApplyPanelBounds()
    {
        if (animTimer.Enabled || noteMenu.Visible) return;
        var primary = Screen.PrimaryScreen;
        var area = primary.WorkingArea;
        if (IsHandleCreated)
        {
            try { screenDpi = (int)GetDpiForWindow(Handle); }
            catch (EntryPointNotFoundException) { }
            if (screenDpi == 0) screenDpi = 96;
        }
        int width = Math.Min(Scale(268), area.Width);
        int height = Math.Min(Scale(preferredHeight), area.Height);
        var target = new Rectangle(area.Location, new Size(width, height));
        if (Bounds != target) Bounds = target;
        if (web.Bounds != ClientRectangle) web.Bounds = ClientRectangle;
    }

    private async System.Threading.Tasks.Task CheckDesktop()
    {
        CheckMotion();
        var origin = Screen.PrimaryScreen.Bounds.Location;
        var external = new Point(Screen.PrimaryScreen.Bounds.Right - 2, Screen.PrimaryScreen.Bounds.Bottom - 2);
        var now = DateTime.UtcNow;
        TrackCursor(origin, now);
        if (Visible) throw new InvalidOperationException("Opened without dwell.");
        TrackCursor(origin, now.AddMilliseconds(210));
        if (!Visible || !TopMost || ShowInTaskbar || Bounds.Location != Screen.PrimaryScreen.WorkingArea.Location)
            throw new InvalidOperationException("Desktop window did not open at the primary corner.");

        var core = web.CoreWebView2;
        await core.ExecuteScriptAsync(@"document.fonts.load('12px ""Fusion Pixel SC""', '整理今日待办'); 'requested'");
        const string checkFont = @"JSON.stringify({
            fonts: [...document.fonts].map(font => [font.family, font.status]),
            family: getComputedStyle(document.querySelector('main')).fontFamily,
            size: getComputedStyle(document.querySelector('#firstDraft')).fontSize
        })";
        string renderedFont = null;
        for (int pass = 0; pass < 30; pass++)
        {
            renderedFont = await core.ExecuteScriptAsync(checkFont);
            if (renderedFont.Contains("loaded") && !renderedFont.Contains("unloaded")) break;
            await System.Threading.Tasks.Task.Delay(35);
        }
        if (renderedFont == null || !renderedFont.Contains("Fusion Pixel SC") ||
            !renderedFont.Contains("loaded") || renderedFont.Contains("unloaded") || !renderedFont.Contains("12px"))
            throw new InvalidOperationException("The local 12px pixel font did not load. " + renderedFont);

        await CheckCompactHeight(core, 72, 2);
        var prepare = @"(() => {
            const note = document.querySelector('#noteApp');
            const main = document.querySelector('#noteApp main');
            const first = document.querySelector('#firstDraft');
            const second = document.querySelector('#secondDraft');
            const firstRow = document.querySelector('#firstBlank');
            const secondRow = document.querySelector('#quickBlank');
            if (note.hidden || !note.hasAttribute('data-quick')) return 'not-quick';
            if (main.clientHeight !== 56 || firstRow.hidden || secondRow.hidden) return 'rows:' + main.clientHeight;
            if (first.offsetHeight === 0 || second.offsetHeight === 0) return 'height';
            if (first.hasAttribute('placeholder') || getComputedStyle(first).borderTopWidth !== '0px') return 'input-style';
            const blankBoxes = [...document.querySelectorAll('.quick-blank .quick-square')];
            if (blankBoxes.length !== 2 || blankBoxes.some(box => box.offsetWidth !== 7 || box.offsetHeight !== 7)) return 'blank-square';
            return 'ok';
        })()";
        var prepRes = await core.ExecuteScriptAsync(prepare);
        if (prepRes != "\"ok\"")
            throw new InvalidOperationException("Initial two rows or input styling did not work: " + prepRes);

        if (Math.Abs(Opacity - 0.88) > 0.01 || Math.Abs(targetOpacity - .88) > .001)
            throw new InvalidOperationException("Default opacity is not 0.88: " + Opacity);

        await System.Threading.Tasks.Task.Delay(80);
        await CaptureAndCheckPaper(core, Path.Combine(install, "preview-empty.png"), BackColor);

        // Sequential entry flow: row 1 -> Enter -> focus moves to row 2 -> row 2 -> Enter -> row 3 blank focused
        await core.ExecuteScriptAsync("document.querySelector('#firstDraft').focus()");
        await core.CallDevToolsProtocolMethodAsync("Input.insertText", "{\"text\":\"整理今日待办\"}");
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
            "{\"type\":\"keyDown\",\"key\":\"Enter\",\"code\":\"Enter\",\"windowsVirtualKeyCode\":13}");
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
            "{\"type\":\"keyUp\",\"key\":\"Enter\",\"code\":\"Enter\",\"windowsVirtualKeyCode\":13}");

        var afterFirst = @"(() => {
            const cards = [...document.querySelectorAll('.task-card')];
            if (cards.length !== 1 || cards[0].querySelector('.task-inline-input')?.value !== '整理今日待办') return 'task1';
            if (!document.querySelector('#quickBlank').hidden || document.querySelector('#firstBlank').hidden) return 'blanks';
            if (document.activeElement !== document.querySelector('#firstDraft')) return 'focus1';
            return 'ok';
        })()";
        var res1 = await core.ExecuteScriptAsync(afterFirst);
        if (res1 != "\"ok\"") throw new InvalidOperationException("First entry failed: " + res1);
        await CheckCompactHeight(core, 72, 2);

        await core.CallDevToolsProtocolMethodAsync("Input.insertText", "{\"text\":\"回复客户邮件\"}");
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
            "{\"type\":\"keyDown\",\"key\":\"Enter\",\"code\":\"Enter\",\"windowsVirtualKeyCode\":13}");
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
            "{\"type\":\"keyUp\",\"key\":\"Enter\",\"code\":\"Enter\",\"windowsVirtualKeyCode\":13}");

        await CheckCompactHeight(core, 100, 3);
        var afterSecond = @"(() => {
            const titles = [...document.querySelectorAll('.task-inline-input')].map(el => el.value);
            if (titles.length !== 2 || titles[0] !== '整理今日待办' || titles[1] !== '回复客户邮件') return 'order:' + JSON.stringify(titles);
            if (document.activeElement !== document.querySelector('#firstDraft')) return 'focus2';
            const button = document.querySelector('.task-card button.check');
            const square = button.querySelector('.check-box');
            if (button.offsetWidth !== 14 || button.offsetHeight !== 24 ||
                square.offsetWidth !== 7 || square.offsetHeight !== 7 ||
                getComputedStyle(square).borderTopLeftRadius !== '0px') return 'square';
            button.click();
            const done = document.querySelector('.task-card.done');
            if (!done || !getComputedStyle(done.querySelector('.task-inline-input')).textDecorationLine.includes('line-through')) return 'completion';
            const mark = done.querySelector('.pixel-check');
            if (mark.getBoundingClientRect().width !== 5 || mark.getBoundingClientRect().height !== 5 ||
                mark.getAttribute('shape-rendering') !== 'crispEdges') return 'pixel-check';
            document.querySelector('#noteApp main').scrollTop = 0;
            return 'ok';
        })()";
        var res2 = await core.ExecuteScriptAsync(afterSecond);
        if (res2 != "\"ok\"") throw new InvalidOperationException("Second entry or completion failed: " + res2);

        await System.Threading.Tasks.Task.Delay(120);
        await CaptureAndCheckPaper(core, Path.Combine(install, "preview-tasks.png"), BackColor);

        // Add tasks 3 and 4 and verify the full five-row viewport.
        await core.ExecuteScriptAsync("document.querySelector('#firstDraft').focus()");
        for (var taskNumber = 3; taskNumber <= 4; taskNumber++)
        {
            await core.CallDevToolsProtocolMethodAsync("Input.insertText", "{\"text\":\"待办事项 " + taskNumber + "\"}");
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
                "{\"type\":\"keyDown\",\"key\":\"Enter\",\"code\":\"Enter\",\"windowsVirtualKeyCode\":13}");
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
                "{\"type\":\"keyUp\",\"key\":\"Enter\",\"code\":\"Enter\",\"windowsVirtualKeyCode\":13}");
        }
        await CheckCompactHeight(core, 156, 5);
        var scrollCheck = @"(() => {
            const main = document.querySelector('#noteApp main');
            return document.querySelectorAll('.task-card').length === 4 && main.scrollHeight === main.clientHeight;
        })()";
        if (await core.ExecuteScriptAsync(scrollCheck) != "true")
            throw new InvalidOperationException("Four tasks and the draft did not fit without scrolling.");

        await CheckInlineEditing(core);
        await CheckCompactHeight(core, 156, 5);

        var beforeMenu = Bounds;
        await OpenSelfTestMenu(core, ".task-inline-input");
        var note = noteMenu.Items["note"] as ToolStripMenuItem;
        var taskDay = noteMenu.Items["taskDay"] as ToolStripMenuItem;
        var calendar = noteMenu.Items["calendar"] as ToolStripMenuItem;
        var toolsItem = noteMenu.Items["tool"] as ToolStripMenuItem;
        if (Bounds != beforeMenu || note == null || note.Text != "备注" || GetNotePanel(note) == null ||
            noteMenu.Items["clearDone"] == null ||
            taskDay == null || taskDay.Text != "切换时间" || GetCalendarPanel(taskDay) == null ||
            calendar == null || calendar.Text != "切换日期" || GetCalendarPanel(calendar) == null ||
            toolsItem == null || toolsItem.Text != "工具" ||
            !MenuHasChildren(toolsItem, new [] { "add30", "usd" }, new [] { "划线价", "换美金" }))
            throw new InvalidOperationException("Unexpected native task, date, or tools menu.");

        await core.ExecuteScriptAsync(@"window.movedTaskId = document.querySelector('.task-card').dataset.taskId;
            window.calendarUsedSheet = false;
            new MutationObserver(records => {
                if (els.sheetMask.classList.contains('open') ||
                    records.some(record => (record.oldValue || '').split(' ').includes('open'))) {
                    window.calendarUsedSheet = true;
                }
            }).observe(els.sheetMask, {attributes:true, attributeOldValue:true, attributeFilter:['class']});");
        await CheckNoteFlyout(core, note, beforeMenu);
        await CheckCompactHeight(core, 156, 5);
        var tomorrow = DateTime.Today.AddDays(1);
        var taskPanel = await OpenSelfTestCalendar(core, ".task-inline-input", "taskDay");
        var firstMonth = taskPanel.FirstOfMonth;
        taskPanel.ActivateAt(Center(taskPanel.NextButton));
        CheckFlyoutBounds(taskPanel, beforeMenu);
        if (taskPanel.FirstOfMonth != firstMonth.AddMonths(1))
            throw new InvalidOperationException("The next-month button did not advance the calendar.");
        taskPanel.ActivateAt(Center(taskPanel.PreviousButton));
        CheckFlyoutBounds(taskPanel, beforeMenu);
        if (taskPanel.FirstOfMonth != firstMonth)
            throw new InvalidOperationException("The previous-month button did not restore the calendar.");
        SelectSelfTestDate(taskPanel, tomorrow, beforeMenu);
        await WaitForPage(core, @"findTask(window.movedTaskId)?.date === addDays(todayISO(), 1) &&
            JSON.parse(localStorage.getItem(storageKey)).find(task => task.id === window.movedTaskId)?.date === addDays(todayISO(), 1) &&
            state.viewDate === todayISO() && !window.calendarUsedSheet", "native task calendar saves tomorrow without opening a web sheet");
        await CheckCompactHeight(core, 128, 4);

        var globalBounds = Bounds;
        var globalPanel = await OpenSelfTestCalendar(core, "#firstDraft", "calendar");
        if (noteMenu.Items["note"] != null || noteMenu.Items["taskDay"] != null)
            throw new InvalidOperationException("Task-only entries appeared on the blank input's menu.");
        using (var preview = new Bitmap(globalPanel.Width, globalPanel.Height))
        {
            globalPanel.DrawToBitmap(preview, globalPanel.ClientRectangle);
            preview.Save(Path.Combine(install, "preview-calendar-flyout.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        SelectSelfTestDate(globalPanel, tomorrow, globalBounds);
        await WaitForPage(core, @"state.viewDate === addDays(todayISO(), 1) && !window.calendarUsedSheet &&
            !els.taskViewHint.hidden && els.taskViewHint.offsetHeight === 20 && els.taskViewHint.textContent.includes('返回今天')",
            "native global calendar updates the list and its 20px date hint");
        await CheckCompactHeight(core, 92, 2, true);

        var returnBounds = Bounds;
        var returnPanel = await OpenSelfTestCalendar(core, ".task-inline-input", "taskDay");
        SelectSelfTestDate(returnPanel, DateTime.Today, returnBounds);
        await WaitForPage(core, "findTask(window.movedTaskId)?.date === todayISO() && !window.calendarUsedSheet",
            "native task calendar can move the task back");
        await core.ExecuteScriptAsync("els.taskViewHint.click();");
        await CheckCompactHeight(core, 156, 5);

        await CheckToolCard(core, "add30", "preview-tool-add30.png");
        await CheckToolCard(core, "usd", "preview-tool-usd.png");
        await CheckCompactHeight(core, 156, 5);

        core.PostWebMessageAsString("command:theme/sage");
        core.PostWebMessageAsString("command:opacity/0.80");
        for (var i = 0; i < 20 && (BackColor.R != 239 || Math.Abs(Opacity - 0.80) > 0.01); i++)
            await System.Threading.Tasks.Task.Delay(30);
        if (BackColor != Color.FromArgb(239, 246, 238) || Math.Abs(Opacity - 0.80) > 0.01 || Math.Abs(targetOpacity - .80) > .001 || Bounds != beforeMenu)
            throw new InvalidOperationException("Theme/opacity switch failed or resized window: " + BackColor + " opacity=" + Opacity);

        await core.ExecuteScriptAsync("document.querySelector('#noteApp main').scrollTop = 0");
        await System.Threading.Tasks.Task.Delay(120);
        await CaptureAndCheckPaper(core, Path.Combine(install, "preview-sage.png"), BackColor);

        await core.ExecuteScriptAsync("runDesktopCommand('filter/all');");
        await CheckCompactHeight(core, 176, 5, true);
        if (await core.ExecuteScriptAsync("els.taskViewHint.textContent === '全部 · 返回当日'") != "true")
            throw new InvalidOperationException("The non-default filter did not show its reset hint.");
        await core.ExecuteScriptAsync("els.taskViewHint.click();");
        await CheckCompactHeight(core, 156, 5);

        await core.ExecuteScriptAsync(@"(() => {
            const done = state.tasks.find(task => task.status === 'done');
            state.tasks.push({...done, id: 'outside-view', date: addDays(todayISO(), -1)});
            runDesktopCommand('clearDone');
        })()");
        if (await core.ExecuteScriptAsync(@"state.tasks.filter(task => task.status === 'done').map(task => task.id).join() === 'outside-view'") != "true")
            throw new InvalidOperationException("Clear Completed failed to respect the visible task filter.");

        await core.ExecuteScriptAsync("for (let i = 0; i < 9; i++) addTaskFromText('Overflow task ' + i);");
        await CheckCompactHeight(core, 352, 12);
        if (await core.ExecuteScriptAsync("els.main.scrollHeight > els.main.clientHeight") != "true")
            throw new InvalidOperationException("More than twelve rows should scroll.");
        await core.ExecuteScriptAsync("for (const task of [...state.tasks].slice(1)) deleteTask(task.id);");
        await CheckCompactHeight(core, 72, 2);

        // Pin keeps open on departure, unpin hides on departure
        core.PostWebMessageAsString("command:pin");
        for (var i = 0; i < 15 && !pinned; i++) await System.Threading.Tasks.Task.Delay(25);
        if (!pinned) throw new InvalidOperationException("Pin did not activate.");
        keepOpen = false;
        menuGraceUntil = DateTime.MinValue;
        TrackCursor(external, now.AddMilliseconds(300));
        TrackCursor(external, now.AddMilliseconds(700));
        if (!Visible) throw new InvalidOperationException("Pinned window hid on pointer departure.");

        core.PostWebMessageAsString("command:pin");
        for (var i = 0; i < 15 && pinned; i++) await System.Threading.Tasks.Task.Delay(25);
        if (pinned) throw new InvalidOperationException("Pin did not deactivate.");
        menuGraceUntil = DateTime.MinValue;
        TrackCursor(external, now.AddMilliseconds(800));
        TrackCursor(external, now.AddMilliseconds(1200));
        if (Visible) throw new InvalidOperationException("Desktop window did not hide on departure after unpin.");

        WriteStatus("passed: inline edit and persistence, keyboard navigation and deletion, multiline auto-save in a native notes flyout, no web-sheet coverage, pixel calendar, task date commands, separate tools with unchanged calculations, interruptible window motion, seven previews, unchanged menu/theme/opacity bounds, pin/unpin departure");
    }

    private async System.Threading.Tasks.Task CheckInlineEditing(CoreWebView2 core)
    {
        const string test = @"(() => {
            const fields = () => [...document.querySelectorAll('.task-inline-input')];
            const change = (field, value) => {
                field.value = value;
                field.dispatchEvent(new Event('input', {bubbles:true}));
            };
            const key = (field, name) => field.dispatchEvent(new KeyboardEvent('keydown', {bubbles:true,key:name}));
            const saved = field => JSON.parse(localStorage.getItem(storageKey)).find(task =>
                task.id === field.closest('[data-task-id]').dataset.taskId)?.title;
            const [first, second] = fields();
            first.focus();
            change(first, '回复客户并确认');
            key(first,'Enter');
            if (saved(first) !== '回复客户并确认' || document.activeElement !== second) return 'enter';
            change(second, '检查资料清单');
            second.blur();
            if (saved(second) !== '检查资料清单') return 'blur';
            second.focus();
            change(second, '');
            second.blur();
            if (second.value !== '检查资料清单' || saved(second) !== '检查资料清单') return 'blank-blur';
            second.focus();
            change(second, '暂存修改');
            key(second,'Escape');
            if (document.activeElement === second || saved(second) !== '检查资料清单') return 'escape';
            first.focus();
            key(first,'ArrowDown');
            if (document.activeElement !== second) return 'down';
            key(second,'ArrowUp');
            if (document.activeElement !== first) return 'up';
            els.firstDraft.focus();
            key(els.firstDraft,'ArrowUp');
            if (document.activeElement !== fields().at(-1)) return 'up-from-draft';
            key(fields().at(-1),'ArrowDown');
            if (document.activeElement !== els.firstDraft) return 'down-to-draft';
            addTaskFromText('临时验收');
            const current = fields();
            const index = current.findIndex(field => field.value === '临时验收');
            const prior = current[index - 1].value;
            const pending = current[index];
            pending.focus();
            change(pending,'');
            key(pending,'Backspace');
            return fields().length === 4 && !fields().some(field => field.value === '临时验收') &&
                document.activeElement.value === prior ? 'ok' : 'backspace';
        })()";
        var result = await core.ExecuteScriptAsync(test);
        if (result != "\"ok\"") throw new InvalidOperationException("Inline editing failed: " + result);
    }

    private async System.Threading.Tasks.Task CheckNoteFlyout(CoreWebView2 core, ToolStripMenuItem item, Rectangle before)
    {
        var panel = GetNotePanel(item);
        if (panel == null) throw new InvalidOperationException("The notes item has no native text editor.");
        item.PerformClick();
        for (int i = 0; i < 30 && !item.DropDown.Visible; i++) await System.Threading.Tasks.Task.Delay(30);
        CheckFlyoutBounds(panel, before);

        var input = (TextBox)panel.Controls[0];
        if (!input.Multiline || !input.AcceptsReturn || input.MaxLength != 500 || input.ScrollBars != ScrollBars.None)
            throw new InvalidOperationException("The notes editor does not support multiline input.");
        const string note = "客户周五确认\r\n发报价单: A|B";
        input.Text = note;
        const string saved = @"JSON.parse(localStorage.getItem(storageKey)).find(task =>
            task.id === window.movedTaskId)?.note === '客户周五确认\n发报价单: A|B' &&
            document.querySelector('.task-card .task-note-dot')?.title === '客户周五确认\n发报价单: A|B' &&
            !window.calendarUsedSheet";
        await WaitForPage(core, saved, "multiline UTF-8 notes save and gain an indicator while the flyout is open");
        CheckFlyoutBounds(panel, before);
        using (var preview = new Bitmap(panel.Width, panel.Height))
        {
            panel.DrawToBitmap(preview, panel.ClientRectangle);
            preview.Save(Path.Combine(install, "preview-note-flyout.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        noteMenu.Close();
        await WaitForPage(core, saved, "the closed flyout preserves the exact note and never uses a web sheet");
        if (noteMenu.Visible || item.DropDown.Visible || Bounds != before || sheetOpen)
            throw new InvalidOperationException("Closing the notes flyout affected the main note.");

        // Let Windows finish dismissing the focused native editor before opening another menu.
        await System.Threading.Tasks.Task.Delay(30);
        await OpenSelfTestMenu(core, ".task-inline-input");
        item = (ToolStripMenuItem)noteMenu.Items["note"];
        panel = GetNotePanel(item);
        item.PerformClick();
        for (int i = 0; i < 30 && !item.DropDown.Visible; i++) await System.Threading.Tasks.Task.Delay(30);
        CheckFlyoutBounds(panel, before);
        input = (TextBox)panel.Controls[0];
        if (input.Text.Replace("\r\n", "\n") != note.Replace("\r\n", "\n"))
            throw new InvalidOperationException("The flyout did not reload the saved note.");
        input.Clear();
        noteMenu.Close();
        await WaitForPage(core, @"JSON.parse(localStorage.getItem(storageKey)).find(task =>
            task.id === window.movedTaskId)?.note === '' &&
            !document.querySelector('.task-note-dot') && !window.calendarUsedSheet", "clearing notes also clears the indicator");
        if (Bounds != before) throw new InvalidOperationException("The main note resized after clearing notes.");
    }

    private static PixelNotePanel GetNotePanel(ToolStripMenuItem item)
    {
        if (item.DropDownItems.Count != 1) return null;
        var host = item.DropDownItems[0] as ToolStripControlHost;
        return host == null ? null : host.Control as PixelNotePanel;
    }

    private async System.Threading.Tasks.Task OpenSelfTestMenu(CoreWebView2 core, string selector)
    {
        // Saving a note rerenders the task rows, so resolve a fresh DOM target for every opening.
        var dispatched = await core.ExecuteScriptAsync("(() => { const target = document.querySelector('" + selector +
            "'); if (!target?.isConnected) return false; target.dispatchEvent(new MouseEvent('contextmenu'," +
            "{bubbles:true,clientX:16,clientY:18})); return true; })()");
        if (dispatched != "true") throw new InvalidOperationException("Native menu target was not present: " + selector);
        for (int i = 0; i < 30 && !noteMenu.Visible; i++) await System.Threading.Tasks.Task.Delay(30);
        if (!noteMenu.Visible) throw new InvalidOperationException("Native note menu did not open.");
    }

    private static PixelCalendarPanel GetCalendarPanel(ToolStripMenuItem item)
    {
        if (item.DropDownItems.Count != 1) return null;
        var host = item.DropDownItems[0] as ToolStripControlHost;
        return host == null ? null : host.Control as PixelCalendarPanel;
    }

    private async System.Threading.Tasks.Task<PixelCalendarPanel> OpenSelfTestCalendar(CoreWebView2 core, string selector, string key)
    {
        var before = Bounds;
        await OpenSelfTestMenu(core, selector);
        var item = noteMenu.Items[key] as ToolStripMenuItem;
        var panel = item == null ? null : GetCalendarPanel(item);
        if (panel == null) throw new InvalidOperationException("The date item does not host a native pixel calendar.");
        item.PerformClick();
        for (int i = 0; i < 30 && !item.DropDown.Visible; i++) await System.Threading.Tasks.Task.Delay(30);
        if (!item.DropDown.Visible) throw new InvalidOperationException("Click did not open the side calendar.");
        CheckFlyoutBounds(panel, before);
        return panel;
    }

    private void CheckFlyoutBounds(Control panel, Rectangle before)
    {
        if (!noteMenu.Visible || !panel.Visible || Bounds != before || sheetOpen ||
            panel.RectangleToScreen(panel.ClientRectangle).IntersectsWith(before))
            throw new InvalidOperationException("The flyout covered or resized the main note, or closed the native menu.");
    }

    private void SelectSelfTestDate(PixelCalendarPanel panel, DateTime date, Rectangle before)
    {
        CheckFlyoutBounds(panel, before);
        var cell = panel.CellForDate(date);
        if (cell.IsEmpty) throw new InvalidOperationException("Date outside the visible calendar grid.");
        panel.ActivateAt(Center(cell));
        if (noteMenu.Visible || Bounds != before || sheetOpen)
            throw new InvalidOperationException("Selecting a date did not close the native calendar cleanly.");
    }

    private static Point Center(Rectangle area)
    {
        return new Point(area.Left + area.Width / 2, area.Top + area.Height / 2);
    }

    private static bool MenuHasChildren(ToolStripMenuItem item, string[] keys, string[] labels)
    {
        if (item.DropDownItems.Count != keys.Length) return false;
        for (int i = 0; i < keys.Length; i++)
        {
            if (item.DropDownItems[i].Name != item.Name + "/" + keys[i] || item.DropDownItems[i].Text != labels[i])
                return false;
        }
        return true;
    }

    private async System.Threading.Tasks.Task WaitForPage(CoreWebView2 core, string condition, string description)
    {
        for (int i = 0; i < 30; i++)
        {
            if (await core.ExecuteScriptAsync(condition) == "true") return;
            await System.Threading.Tasks.Task.Delay(30);
        }
        throw new InvalidOperationException("Timed out checking " + description + ".");
    }

    private async System.Threading.Tasks.Task CheckToolCard(CoreWebView2 core, string tool, string screenshot)
    {
        await OpenSelfTestMenu(core, "#firstDraft");
        ((ToolStripMenuItem)noteMenu.Items["tool"]).DropDownItems["tool/" + tool].PerformClick();
        noteMenu.Close();
        bool add = tool == "add30";
        string checks = add
            ? "!els.add30Card.hidden && els.usdCard.hidden"
            : "els.add30Card.hidden && !els.usdCard.hidden";
        await WaitForPage(core, "!els.toolsView.hidden && " + checks, "one visible " + tool + " tool card");
        string input = add ? "add30Input" : "usdInput";
        await core.ExecuteScriptAsync("els." + input + ".value = '100'; els." + input + ".dispatchEvent(new Event('input',{bubbles:true}));");
        string result = add ? "els.add30Output.value === '130'" :
            "els.usdIntOutput.value === '134' && els.usdDecOutput.value === '134.05'";
        await WaitForPage(core, result, tool + " calculation and result formatting");
        await System.Threading.Tasks.Task.Delay(150);
        await CaptureAndCheckPaper(core, Path.Combine(install, screenshot), BackColor);
        await core.ExecuteScriptAsync("els.toolsView.querySelector('article:not([hidden]) .tool-back').click();");
        await CheckCompactHeight(core, 156, 5);
    }

    private static void CheckMotion()
    {
        var curve = new NoteMotion();
        var origin = new Point(90, 200);
        if (NoteMotion.Position(origin, 8, 0) != new Point(90, 192))
            throw new InvalidOperationException("Incorrect animation start position.");
        long clock = Stopwatch.Frequency;
        curve.Begin(1, clock);
        if (!curve.Advance(clock * 2) || NoteMotion.Position(origin, 8, curve.Progress) != origin)
            throw new InvalidOperationException("The opening animation did not settle at the work area.");
        curve.Begin(0, clock * 2);
        if (curve.Advance(clock * 2 + clock / 20) || curve.Progress <= 0 || curve.Progress >= 1)
            throw new InvalidOperationException("The close transition did not produce an intermediate frame.");
        var frame = curve.Progress;
        var reversalTime = clock * 2 + clock / 20;
        curve.Begin(1, reversalTime);
        curve.Advance(reversalTime);
        if (curve.Progress != frame || !curve.Advance(clock * 3) || curve.Progress != 1)
            throw new InvalidOperationException("Interrupted hide did not reverse from its current frame.");
        curve.Begin(0, clock * 3);
        if (!curve.Advance(clock * 4) || NoteMotion.Position(origin, 8, curve.Progress) != new Point(90, 192))
            throw new InvalidOperationException("The close animation did not finish above the work area.");
    }

    private async System.Threading.Tasks.Task CheckCompactHeight(CoreWebView2 core, int height, int rows, bool hint = false)
    {
        string correctViewport = "document.querySelector('#noteApp main').clientHeight === " + (rows * 28 + (hint ? 20 : 0));
        for (int pass = 0; pass < 30; pass++)
        {
            if (Width == Scale(268) && Height == Scale(height) && web.ClientSize == ClientSize &&
                await core.ExecuteScriptAsync(correctViewport) == "true") return;
            await System.Threading.Tasks.Task.Delay(30);
        }
        throw new InvalidOperationException("Expected " + rows + " complete rows at 268x" + height +
            " (logical px), native window was " + Width + "x" + Height + ".");
    }

    private static async System.Threading.Tasks.Task CaptureAndCheckPaper(CoreWebView2 core, string path, Color expectedPaper, bool sheet = false)
    {
        using (var preview = new MemoryStream())
        {
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, preview);
            preview.Position = 0;
            using (var bitmap = new Bitmap(preview))
            {
                int marginX = sheet ? 2 : bitmap.Width - 6;
                var top = bitmap.GetPixel(marginX, 4);
                var bottom = bitmap.GetPixel(marginX, bitmap.Height - 5);
                if (top.ToArgb() != bottom.ToArgb() || top.R != expectedPaper.R || top.G != expectedPaper.G || top.B != expectedPaper.B)
                    throw new InvalidOperationException("The panel background is not uniform (" + top + " vs " + bottom + " expected " + expectedPaper + ").");
            }
            File.WriteAllBytes(path, preview.ToArray());
        }
    }

    private void WriteStatus(string status)
    {
        File.WriteAllText(Path.Combine(install, selfTest ? "self-test.txt" : "status.txt"), status);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (pageWatcher != null) pageWatcher.Dispose();
            animTimer.Dispose();
            timer.Dispose();
            noteMenu.Dispose();
            if (shutdownRequest != null) shutdownRequest.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class PixelNotePanel : Control
{
    private readonly TextBox noteBox;
    private readonly Font noteFont = new Font("Microsoft YaHei UI", 8.5F);
    private readonly Color border, accent;
    private readonly int dpi;
    private readonly Action<string> onSave;
    private string lastSaved;

    internal PixelNotePanel(int dpi, Color paper, Color border, Color ink, Color accent,
        string note, Action<string> onSave, Action onDismiss)
    {
        this.dpi = dpi;
        this.border = border;
        this.accent = accent;
        this.onSave = onSave;
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = paper;
        Size = new Size(Scale(PixelCalendarPanel.LogicalWidth), Scale(112));
        lastSaved = NormalizeNewlines(note);
        noteBox = new NoteTextBox {
            Multiline = true, BorderStyle = BorderStyle.None, AcceptsReturn = true, MaxLength = 500,
            WordWrap = true, ScrollBars = ScrollBars.None, Font = noteFont,
            BackColor = Color.FromArgb((paper.R * 4 + 255) / 5, (paper.G * 4 + 255) / 5, (paper.B * 4 + 255) / 5),
            ForeColor = ink, Text = lastSaved.Replace("\n", "\r\n"), AccessibleName = "任务备注"
        };
        noteBox.TextChanged += delegate { Flush(); };
        noteBox.GotFocus += delegate { Invalidate(); };
        noteBox.LostFocus += delegate { Invalidate(); };
        noteBox.KeyDown += delegate(object sender, KeyEventArgs args)
        {
            if (args.KeyCode != Keys.Escape) return;
            Flush();
            args.SuppressKeyPress = true;
            onDismiss();
        };
        Controls.Add(noteBox);
        LayoutInput();
    }

    private int Scale(int value) { return (value * dpi + 48) / 96; }

    private static string NormalizeNewlines(string value) { return value.Replace("\r\n", "\n"); }

    internal void FocusInput()
    {
        noteBox.Focus();
        noteBox.SelectionStart = noteBox.TextLength;
        noteBox.SelectionLength = 0;
    }

    internal void Flush()
    {
        string text = NormalizeNewlines(noteBox.Text);
        if (text == lastSaved) return;
        onSave(text);
        lastSaved = text;
    }

    private void LayoutInput()
    {
        if (noteBox == null) return;
        var area = Rectangle.Inflate(ClientRectangle, -Scale(11), -Scale(11));
        noteBox.Bounds = area;
    }

    protected override void OnResize(EventArgs args)
    {
        base.OnResize(args);
        LayoutInput();
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        if (noteBox == null) return;
        var frame = Rectangle.Inflate(ClientRectangle, -Scale(8), -Scale(8));
        using (var brush = new SolidBrush(noteBox.BackColor)) args.Graphics.FillRectangle(brush, frame);
        using (var pen = new Pen(noteBox.Focused ? accent : border))
            args.Graphics.DrawRectangle(pen, frame.X, frame.Y, frame.Width - 1, frame.Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) noteFont.Dispose();
    }

    private sealed class NoteTextBox : TextBox
    {
        private int wheelRemainder;

        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Enter || key == Keys.Escape || base.IsInputKey(keyData);
        }

        protected override void WndProc(ref Message message)
        {
            const int wmMouseWheel = 0x020A;
            const int emLineScroll = 0x00B6;
            const int wheelDelta = 120;
            if (message.Msg != wmMouseWheel)
            {
                base.WndProc(ref message);
                return;
            }

            wheelRemainder += unchecked((short)(message.WParam.ToInt64() >> 16));
            int notches = wheelRemainder / wheelDelta;
            wheelRemainder %= wheelDelta;
            int lines = SystemInformation.MouseWheelScrollLines;
            if (lines < 0) lines = Math.Max(1, ClientSize.Height / Font.Height - 1);
            if (notches != 0 && lines != 0)
            {
                var scroll = Message.Create(Handle, emLineScroll, IntPtr.Zero, new IntPtr(-notches * lines));
                DefWndProc(ref scroll);
            }
            message.Result = IntPtr.Zero;
        }
    }
}

internal sealed class PixelCalendarPanel : Control
{
    internal const int LogicalWidth = 216;
    private const int LogicalHeight = 196;
    private static readonly string[] Weekdays = { "一", "二", "三", "四", "五", "六", "日" };
    private readonly int dpi;
    private readonly Color paper, border, ink, accent, highlight, muted;
    private readonly Font calendarFont = new Font("Microsoft YaHei UI", 8.5F);
    private readonly HashSet<DateTime> pendingDates;
    private readonly DateTime today = DateTime.Today;
    private readonly DateTime selected;
    private readonly Action<DateTime> onSelect;
    private DateTime month;
    private Point hover = new Point(-1, -1);

    internal PixelCalendarPanel(int dpi, Color paper, Color border, Color ink, Color accent, Color highlight,
        DateTime selected, HashSet<DateTime> pendingDates, Action<DateTime> onSelect)
    {
        this.dpi = dpi;
        this.paper = paper;
        this.border = border;
        this.ink = ink;
        this.accent = accent;
        this.highlight = highlight;
        this.selected = selected.Date;
        this.pendingDates = pendingDates;
        this.onSelect = onSelect;
        muted = Color.FromArgb((ink.R + paper.R) / 2, (ink.G + paper.G) / 2, (ink.B + paper.B) / 2);
        month = new DateTime(selected.Year, selected.Month, 1);
        DoubleBuffered = true;
        ResizeRedraw = true;
        SetStyle(ControlStyles.StandardClick, false);
        BackColor = paper;
        Size = new Size(Scale(LogicalWidth), Scale(LogicalHeight));
        AccessibleName = "日期月历";
    }

    internal DateTime FirstOfMonth { get { return month; } }
    internal Rectangle PreviousButton { get { return new Rectangle(Scale(4), Scale(3), Scale(23), Scale(22)); } }
    internal Rectangle NextButton { get { return new Rectangle(ClientSize.Width - Scale(65), Scale(3), Scale(23), Scale(22)); } }
    private Rectangle TodayButton { get { return new Rectangle(ClientSize.Width - Scale(39), Scale(3), Scale(35), Scale(22)); } }

    private int Scale(int value) { return (value * dpi + 48) / 96; }

    private DateTime GridStart()
    {
        return month.AddDays(-Math.Min(((int)month.DayOfWeek + 6) % 7, (month - DateTime.MinValue).Days));
    }

    private Rectangle Cell(int column, int y, int height)
    {
        int width = ClientSize.Width - 2 * Scale(4);
        int left = Scale(4) + width * column / 7;
        int right = Scale(4) + width * (column + 1) / 7;
        return new Rectangle(left, Scale(y), right - left, Scale(y + height) - Scale(y));
    }

    private Rectangle DayCell(int index) { return Cell(index % 7, 46 + (index / 7) * 24, 24); }

    internal Rectangle CellForDate(DateTime date)
    {
        int index = (date.Date - GridStart()).Days;
        return index < 0 || index >= 42 ? Rectangle.Empty : DayCell(index);
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        var g = args.Graphics;
        g.Clear(paper);
        PaintBox(g, PreviousButton, "<", ink, false, null);
        PaintBox(g, NextButton, ">", ink, false, null);
        PaintBox(g, TodayButton, "今天", ink, false, border);
        var heading = Rectangle.FromLTRB(PreviousButton.Right, 0, NextButton.Left, Scale(26));
        DrawLabel(g, month.ToString("yyyy年M月", CultureInfo.InvariantCulture), heading, ink);
        for (int column = 0; column < 7; column++) DrawLabel(g, Weekdays[column], Cell(column, 26, 20), muted);

        var first = GridStart();
        int count = Math.Min(42, (DateTime.MaxValue.Date - first).Days + 1);
        for (int index = 0; index < count; index++)
        {
            var date = first.AddDays(index);
            var area = Rectangle.Inflate(DayCell(index), -1, -1);
            bool active = date == selected;
            var color = active ? paper : date.Month == month.Month ? ink : muted;
            PaintBox(g, area, date.Day.ToString(CultureInfo.InvariantCulture), color, active,
                date == today ? (Color?)border : null);
            if (pendingDates.Contains(date))
            {
                int size = Scale(2);
                using (var brush = new SolidBrush(active ? paper : accent))
                    g.FillRectangle(brush, area.Left + (area.Width - size) / 2, area.Bottom - size - 1, size, size);
            }
        }
    }

    private void PaintBox(Graphics g, Rectangle area, string text, Color foreground, bool active, Color? edge)
    {
        if (active || area.Contains(hover))
            using (var brush = new SolidBrush(active ? accent : highlight)) g.FillRectangle(brush, area);
        if (edge.HasValue)
            using (var pen = new Pen(edge.Value)) g.DrawRectangle(pen, area.X, area.Y, area.Width - 1, area.Height - 1);
        DrawLabel(g, text, area, foreground);
    }

    private void DrawLabel(Graphics g, string text, Rectangle area, Color color)
    {
        TextRenderer.DrawText(g, text, calendarFont, area, color, TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }

    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        hover = args.Location;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs args)
    {
        base.OnMouseLeave(args);
        hover = new Point(-1, -1);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs args)
    {
        base.OnMouseUp(args);
        if (args.Button == MouseButtons.Left) ActivateAt(args.Location);
    }

    internal void ActivateAt(Point point)
    {
        int step = PreviousButton.Contains(point) ? -1 : NextButton.Contains(point) ? 1 : 0;
        if (step != 0)
        {
            if (step < 0 && month.Year == 1 && month.Month == 1 ||
                step > 0 && month.Year == 9999 && month.Month == 12) return;
            month = month.AddMonths(step);
            Invalidate();
            return;
        }
        if (TodayButton.Contains(point))
        {
            onSelect(today);
            return;
        }
        var first = GridStart();
        int count = Math.Min(42, (DateTime.MaxValue.Date - first).Days + 1);
        for (int index = 0; index < count; index++)
            if (DayCell(index).Contains(point))
            {
                onSelect(first.AddDays(index));
                return;
            }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) calendarFont.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class CreamMenuColors : ProfessionalColorTable
{
    private readonly Color paper, line, accent;
    public CreamMenuColors(Color paper, Color line, Color accent)
    {
        this.paper = paper; this.line = line; this.accent = accent;
        UseSystemColors = false;
    }

    private Color Highlight
    {
        get
        {
            return Color.FromArgb((paper.R * 4 + accent.R) / 5,
                (paper.G * 4 + accent.G) / 5, (paper.B * 4 + accent.B) / 5);
        }
    }

    public override Color ToolStripDropDownBackground { get { return paper; } }
    public override Color MenuBorder { get { return line; } }
    public override Color MenuItemBorder { get { return line; } }
    public override Color ImageMarginGradientBegin { get { return paper; } }
    public override Color ImageMarginGradientMiddle { get { return paper; } }
    public override Color ImageMarginGradientEnd { get { return paper; } }
    public override Color MenuItemSelected { get { return Highlight; } }
    public override Color MenuItemSelectedGradientBegin { get { return Highlight; } }
    public override Color MenuItemSelectedGradientEnd { get { return Highlight; } }
    public override Color MenuItemPressedGradientBegin { get { return Highlight; } }
    public override Color MenuItemPressedGradientMiddle { get { return Highlight; } }
    public override Color MenuItemPressedGradientEnd { get { return Highlight; } }
    public override Color SeparatorDark { get { return line; } }
    public override Color SeparatorLight { get { return paper; } }
    public override Color CheckBackground { get { return Highlight; } }
    public override Color CheckSelectedBackground { get { return Highlight; } }
}
