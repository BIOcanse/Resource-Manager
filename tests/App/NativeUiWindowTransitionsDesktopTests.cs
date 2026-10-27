using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using ResourceManager.NativeUi;

namespace Resource_Manager_APP.Tests;

// Explicitly opt in: this shows only our mock NativeUi. Prefer a secondary
// monitor; the user explicitly permits this machine's desktop window checks.
public sealed class NativeUiWindowTransitionsDesktopTests
{
    [DesktopFact]
    public void RealWindowKeepsFramelessGeometryAcrossSystemTransitions() => NativeUiWindowChromeTests.RunSta(() =>
    {
        var root = Environment.GetEnvironmentVariable("RM_WINDOW_CAPTURE_ROOT")
            ?? throw new InvalidOperationException("A registered capture root is required.");
        Directory.CreateDirectory(root);
        // Run this class in its own test process: other HWND tests can cache
        // DPI-virtualized Screen data before the application's DPI mode is set.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var dpi = new DpiScope();
        using var host = new NativeUiWindowChromeTests.MockHost();
        using var form = NativeUiWindowChromeTests.CreateForm(host);
        var screen = Screen.AllScreens.FirstOrDefault(s => !s.Primary) ?? Screen.PrimaryScreen!;
        var foreground = GetForegroundWindow();
        form.ShowInTaskbar = true;
        form.Text = "NativeUi transition validation (isolated mock)";
        form.Bounds = new Rectangle(screen.WorkingArea.Left + 80, screen.WorkingArea.Top + 80, 900, 600);
        foreach (Control control in form.Controls) control.Visible = false;
        using var surface = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(32, 34, 36) };
        surface.Paint += (_, e) =>
        {
            e.Graphics.FillRectangle(Brushes.Cyan, 0, 0, surface.Width, 4);
            e.Graphics.FillRectangle(Brushes.Lime, 0, surface.Height - 4, surface.Width, 4);
            e.Graphics.DrawString("NativeUi: window-only mock, no backend", SystemFonts.DefaultFont,
                Brushes.White, 24, 24);
        };
        form.Controls.Add(surface);
        surface.BringToFront();
        form.ShowPassively();
        Pump();
        using var probe = new ChromeMessageProbe(form.Handle);
        var observations = new List<object>();
        void Record(string name, bool capture = true)
        {
            Pump();
            var client = form.RectangleToScreen(form.ClientRectangle);
            observations.Add(new { name, state = form.WindowState.ToString(), form.Bounds,
                client, visibleBounds = VisibleBounds(form), form.DeviceDpi, workArea = Screen.FromHandle(form.Handle).WorkingArea,
                awareness = GetAwarenessFromDpiAwarenessContext(GetWindowDpiAwarenessContext(form.Handle)) });
            if (capture) Capture(form, Path.Combine(root, name + ".png"));
            File.WriteAllText(Path.Combine(root, "progress-observations.json"),
                JsonSerializer.Serialize(new { observations, probe.Messages }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Record("01-normal");
        var restore = form.Bounds;
        host.Send("\"window.maximize\"");
        Record("02-page-maximize");
        AssertMaximizedVisibleBounds(form, screen.WorkingArea);
        ValidateMaximizedNcCalc(form, screen.WorkingArea);
        var corner = form.PointToScreen(new Point(1, 1));
        Assert.Equal((nint)1, NativeUiWindowChromeTests.SendMessage(form.Handle, 0x0084, 0, Pack(corner)));
        host.Send("\"window.maximize\"");
        Record("03-page-restore");
        Assert.Equal(restore, form.Bounds);
        Assert.Equal((nint)13, NativeUiWindowChromeTests.SendMessage(form.Handle, 0x0084, 0,
            Pack(form.PointToScreen(new Point(1, 1)))));
        // These are the HWND's native commands used by the system menu/taskbar
        // and the OS maximize/restore paths; no global keyboard injection.
        NativeUiWindowChromeTests.SendMessage(form.Handle, 0x0112, 0xF030, 0);
        Record("04-system-maximize");
        Assert.Equal(screen.WorkingArea, form.RectangleToScreen(form.ClientRectangle));
        NativeUiWindowChromeTests.SendMessage(form.Handle, 0x0112, 0xF120, 0);
        Record("05-system-restore");
        Assert.Equal(restore, form.Bounds);
        NativeUiWindowChromeTests.SendMessage(form.Handle, 0x00A3, 2, Pack(form.PointToScreen(new Point(400, 24))));
        Record("06-caption-doubleclick");
        Assert.Equal(FormWindowState.Maximized, form.WindowState);
        WindowPlacement.RestoreMaximizedForCaptionDrag(form,
            new Point(screen.WorkingArea.Left + 400, screen.WorkingArea.Top + 24));
        Record("07-caption-drag-restore");
        Assert.Equal(FormWindowState.Normal, form.WindowState);
        Assert.Equal(restore.Size, form.Size);
        // A/B keeps the same real HWND, UI, DWM settings, and native command.
        // The timings are diagnostic, not a timing-based pass criterion.
        foreach (var variant in new[] { "caption", "without-caption", "none", "ultra", "desktop" })
        {
            var style = GetWindowLong(form.Handle, -16);
            SetWindowLong(form.Handle, -16, variant == "without-caption" ? style & ~0x00C00000 : style | 0x00C00000);
            Assert.True(SetWindowPos(form.Handle, 0, 0, 0, 0, 0, 0x0027)); // FRAMECHANGED, no move/size/z-order.
            if (variant is "none" or "ultra" or "desktop")
                host.Send(JsonSerializer.Serialize(new { type = "shell.animationStyle", style = variant }));
            var timer = Stopwatch.StartNew();
            host.Send("\"window.minimize\"");
            var minimizeMs = timer.Elapsed.TotalMilliseconds;
            Assert.True(IsIconic(form.Handle));
            Assert.Equal(variant != "without-caption", (GetWindowLong(form.Handle, -16) & 0x00C00000) == 0x00C00000);
            Record("08-" + variant + "-minimized", capture: false);
            timer.Restart();
            NativeUiWindowChromeTests.SendMessage(form.Handle, 0x0112, 0xF120, 0);
            var restoreMs = timer.Elapsed.TotalMilliseconds;
            Record("09-" + variant + "-restored");
            Assert.False(IsIconic(form.Handle));
            observations.Add(new { variant, minimizeMs, restoreMs });
        }
        // Re-run on every physical secondary display, preserving its actual DPI.
        foreach (var target in Screen.AllScreens)
        {
            form.WindowState = FormWindowState.Normal;
            form.Bounds = new Rectangle(target.WorkingArea.Left + 60, target.WorkingArea.Top + 60, 900, 600);
            host.Send("\"window.maximize\"");
            AssertMaximizedVisibleBounds(form, target.WorkingArea);
            Record("10-monitor-" + target.DeviceName.Replace('\\', '_').Replace('.', '_'));
        }
        var animation = new AnimationInfo { Size = Marshal.SizeOf<AnimationInfo>() };
        Assert.True(SystemParametersInfo(0x0048, animation.Size, ref animation, 0));
        File.WriteAllText(Path.Combine(root, "observations.json"), JsonSerializer.Serialize(
            new { osVersion = Environment.OSVersion.VersionString,
                priority = Process.GetCurrentProcess().PriorityClass.ToString(),
                animation.MinAnimate, foregroundBefore = foreground.ToInt64(),
                foregroundAfter = GetForegroundWindow().ToInt64(), observations },
            new JsonSerializerOptions { WriteIndented = true }));
        form.Hide();
    });

    private static void Pump()
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 350) { Application.DoEvents(); Thread.Sleep(10); }
    }

    private static void Capture(Form form, string path)
    {
        using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        using var graphics = Graphics.FromImage(bitmap);
        var dc = graphics.GetHdc();
        try { Assert.True(PrintWindow(form.Handle, dc, 3)); } // CLIENTONLY | RENDERFULLCONTENT
        finally { graphics.ReleaseHdc(dc); }
        bitmap.Save(path, ImageFormat.Png);
    }

    private static void AssertMaximizedVisibleBounds(Form form, Rectangle workArea)
    {
        Assert.Equal(workArea, form.RectangleToScreen(form.ClientRectangle));
        Assert.Equal(workArea, VisibleBounds(form));
    }

    private static Rectangle VisibleBounds(Form form)
    {
        Assert.Equal(0, DwmGetWindowAttribute(form.Handle, 9, out NativeRect value, Marshal.SizeOf<NativeRect>()));
        return Rectangle.FromLTRB(value.Left, value.Top, value.Right, value.Bottom);
    }

    private static void ValidateMaximizedNcCalc(Form form, Rectangle workArea)
    {
        foreach (var full in new[] { false, true })
        {
            var proposed = Rectangle.Inflate(workArea, 12, 12);
            var input = new[] { proposed.Left, proposed.Top, proposed.Right, proposed.Bottom,
                101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112 };
            var memory = Marshal.AllocHGlobal(64);
            try
            {
                Marshal.Copy(input, 0, memory, input.Length);
                Assert.Equal(0, NativeUiWindowChromeTests.SendMessage(form.Handle, 0x0083, full ? 1 : 0, memory));
                var output = new int[16];
                Marshal.Copy(memory, output, 0, output.Length);
                Assert.Equal(new[] { workArea.Left, workArea.Top, workArea.Right, workArea.Bottom }, output[..4]);
                Assert.Equal(input[4..], output[4..]);
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
    }

    private static nint Pack(Point point) => (nint)((point.X & 0xFFFF) | ((point.Y & 0xFFFF) << 16));
    private sealed class DesktopFactAttribute : FactAttribute
    {
        public DesktopFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("RM_NATIVE_WINDOW_TRANSITIONS_DESKTOP") != "1")
                Skip = "Set RM_NATIVE_WINDOW_TRANSITIONS_DESKTOP=1 for the isolated window check.";
        }
    }
    private sealed class DpiScope : IDisposable
    {
        private readonly nint previous = SetThreadDpiAwarenessContext((nint)(-4));
        public void Dispose() => SetThreadDpiAwarenessContext(previous);
    }
    private sealed class ChromeMessageProbe : NativeWindow, IDisposable
    {
        internal List<object> Messages { get; } = [];
        internal ChromeMessageProbe(nint hwnd) => AssignHandle(hwnd);
        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if (message.Msg is 0x0024 or 0x0083 && message.LParam != 0 && Messages.Count < 256)
            {
                var values = new int[message.Msg == 0x0024 ? 10 : 4];
                Marshal.Copy(message.LParam, values, 0, values.Length);
                Messages.Add(new { message = message.Msg, values });
            }
        }
        public void Dispose() => ReleaseHandle();
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct AnimationInfo { public int Size, MinAnimate; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint window, nint dc, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out NativeRect rect, int size);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern nint GetWindowDpiAwarenessContext(nint window);
    [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(nint context);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint window, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, int size, ref AnimationInfo info, uint flags);
}
