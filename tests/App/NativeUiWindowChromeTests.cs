using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using ResourceManager.NativeUi;
using ResourceManager.NativeUi.WebView;

namespace Resource_Manager_APP.Tests;

public sealed class NativeUiWindowChromeTests
{
    [Fact]
    public void HandleKeepsSystemCaptionAndSnapStylesWithoutAClientFrame() => RunSta(() =>
    {
        using var host = new MockHost();
        using var form = CreateForm(host);
        var style = GetWindowLong(form.Handle, -16);
        foreach (var bit in new[] { 0x00C00000, 0x00080000, 0x00040000, 0x00020000, 0x00010000 })
            Assert.Equal(bit, style & bit);
        Assert.Equal(FormBorderStyle.None, form.FormBorderStyle);
        Assert.Equal(form.Size, form.ClientSize);
    });

    [Fact]
    public void AnimationChoiceReceivedBeforeHandleCreationAppliesToNewWindow() => RunSta(() =>
    {
        using var host = new MockHost();
        using var form = CreateForm(host);
        host.Send("{\"type\":\"shell.animationStyle\",\"style\":\"ultra\"}");
        Assert.False(form.IsHandleCreated);
        _ = form.Handle;
        Assert.True(TransitionsDisabled(form));
        Assert.Equal(0, NativeWindowChrome.SetTransitions(form.Handle, true));
    });

    [Fact]
    public void NativeMaximizeLimitsPreserveTrackingSizes() => RunSta(() =>
    {
        using var host = new MockHost();
        using var form = CreateForm(host);
        var memory = Marshal.AllocHGlobal(40);
        try
        {
            var values = new[] { 0, 0, 0, 0, 0, 0, 123, 234, 3456, 4567 };
            Marshal.Copy(values, 0, memory, values.Length);
            NativeWindowChrome.ApplyMaximizedBounds(form.Handle, memory);
            var output = new int[10];
            Marshal.Copy(memory, output, 0, output.Length);
            var screen = Screen.FromHandle(form.Handle);
            var expected = NativeWindowChrome.CalculateMaximizedBounds(screen.Bounds, screen.WorkingArea);
            Assert.Equal(expected.Size, new Size(output[2], output[3]));
            Assert.Equal(expected.Location, new Point(output[4], output[5]));
            Assert.Equal(values[6..], output[6..]);
        }
        finally { Marshal.FreeHGlobal(memory); }
    });

    [Theory]
    [InlineData(8)]  // 96 DPI
    [InlineData(12)] // 144 DPI
    [InlineData(16)] // 192 DPI
    [InlineData(20)] // 240 DPI
    public void MaximizedInsetRemovesOnlyOverhangInPhysicalCoordinates(int frame)
    {
        var work = new Rectangle(-2560, -640, 2560, 1400);
        var proposed = Rectangle.Inflate(work, frame, frame);
        Assert.Equal(work, NativeWindowChrome.CalculateClientBounds(proposed, work, true));
        Assert.Equal(work, NativeWindowChrome.CalculateClientBounds(work, work, true));
        Assert.Equal(proposed, NativeWindowChrome.CalculateClientBounds(proposed, work, false));
        var asymmetric = Rectangle.FromLTRB(work.Left - frame, work.Top, work.Right + frame, work.Bottom);
        Assert.Equal(work, NativeWindowChrome.CalculateClientBounds(asymmetric, work, true));
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 1920, 1040)]
    [InlineData(-2560, -640, -2512, -600, 2512, 1400)]
    [InlineData(1920, 240, 1920, 280, 2560, 1400)]
    public void MaximizedPlacementUsesMonitorRelativeWorkArea(
        int monitorX, int monitorY, int workX, int workY, int width, int height)
    {
        var monitor = new Rectangle(monitorX, monitorY, 2560, 1440);
        var work = new Rectangle(workX, workY, width, height);
        Assert.Equal(new Rectangle(workX - monitorX, workY - monitorY, width, height),
            NativeWindowChrome.CalculateMaximizedBounds(monitor, work));
    }

    [Fact]
    public void BothNcCalcSizeFormsKeepWholeNormalClientAndPreserveOtherParameters() => RunSta(() =>
    {
        using var host = new MockHost();
        using var form = CreateForm(host);
        foreach (var full in new[] { false, true })
        {
            var memory = Marshal.AllocHGlobal(64);
            try
            {
                var input = Enumerable.Range(0, 16).Select(i => 100 + i).ToArray();
                Marshal.Copy(input, 0, memory, input.Length);
                Assert.Equal(0, SendMessage(form.Handle, 0x0083, full ? 1 : 0, memory));
                var output = new int[16];
                Marshal.Copy(memory, output, 0, output.Length);
                Assert.Equal(input, output);
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
    });

    [Fact]
    public void AnimationStyleSwitchesDwmAndSurvivesHandleRecreation() => RunSta(() =>
    {
        using var host = new MockHost();
        using var form = CreateForm(host);
        void AssertDisabled(bool expected)
        {
            Assert.Equal(expected, TransitionsDisabled(form));
            // TRANSITIONS_FORCEDISABLED is a setter-only DWM attribute. Validate
            // its real HRESULT; DwmGetWindowAttribute returns E_INVALIDARG.
            Assert.Equal(0, NativeWindowChrome.SetTransitions(form.Handle, expected));
        }
        AssertDisabled(false);
        foreach (var style in new[] { "none", "desktop", "ultra", "smooth", "terminal", "none" })
        {
            host.Send(JsonSerializer.Serialize(new { type = "shell.animationStyle", style }));
            AssertDisabled(style is "none" or "ultra");
        }
        foreach (var invalid in new[] { "{}", "{\"type\":\"shell.animationStyle\"}",
            "{\"type\":\"shell.animationStyle\",\"style\":false}",
            "{\"type\":\"shell.animationStyle\",\"style\":\"unknown\"}" })
        {
            host.Send(invalid);
            AssertDisabled(true);
        }
        typeof(Control).GetMethod("RecreateHandle", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(form, null);
        AssertDisabled(true);
        host.Send("{\"type\":\"shell.animationStyle\",\"style\":\"desktop\"}");
        AssertDisabled(false);
    });

    internal static MainForm CreateForm(MockHost host) => new(_ => { }, () => { }, host)
    {
        ShowInTaskbar = false,
        Location = new Point(-30000, -30000)
    };

    internal sealed class MockHost : IFrontendHost
    {
        public Control View { get; } = new Panel { Dock = DockStyle.Fill };
        public Color BackgroundColor => Color.FromArgb(32, 34, 36);
        public bool IsReady => false;
        public string ApplicationAddress => "about:blank";
        public event EventHandler<FrontendMessageReceivedEventArgs>? MessageReceived;
        public event EventHandler<FrontendNavigationCompletedEventArgs>? NavigationCompleted { add { } remove { } }
        internal void Send(string json) => MessageReceived?.Invoke(this, new(json));
        public Task InitializeAsync() => throw new InvalidOperationException("No browser or backend in chrome tests.");
        public Task StopAsync() => Task.CompletedTask;
        public void Dispose() => View.Dispose();
        public void Navigate(string address) { }
        public void PostStringMessage(string message) { }
        public void PostJsonMessage(string json) { }
        public void FocusContent() { }
    }

    internal static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Chrome STA test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll")]
    internal static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
    private static bool TransitionsDisabled(MainForm form) => (bool)typeof(MainForm)
        .GetField("windowTransitionsDisabled", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
}
