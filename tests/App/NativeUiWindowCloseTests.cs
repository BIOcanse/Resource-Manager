using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ResourceManager.NativeUi;
using ResourceManager.NativeUi.WebView;

namespace Resource_Manager_APP.Tests;

public sealed class NativeUiWindowCloseTests
{
    private const int WmClose = 0x0010;
    private const int WmSysCommand = 0x0112;
    private const int ScClose = 0xF060;
    private const int StaTimeoutSeconds = 15;
    private const int OffscreenCoordinate = -30000;

    [Theory]
    [InlineData("window.close")]
    [InlineData("alt-f4/system-menu")]
    [InlineData("taskbar")]
    [InlineData("tray-exit")]
    [InlineData("update.applyExit")]
    [InlineData("dispose")]
    public void EveryClosePathHidesBeforeSurfaceTeardown(string path) => RunSta(() =>
    {
        using var proxy = CreateOfflineProxy();
        using var form = CreateForm(proxy);
        var handle = form.Handle;
        var bounds = form.Bounds;
        var restoreBounds = form.RestoreBounds;
        var surface = form.Controls.OfType<Microsoft.Web.WebView2.WinForms.WebView2>().Single();
        var surfaceDisposed = false;
        var formClosed = false;
        form.FormClosing += (_, _) =>
        {
            Assert.False(form.Visible);
            Assert.False(IsWindowVisible(handle));
            Assert.Equal(bounds, form.Bounds);
            Assert.Equal(restoreBounds, form.RestoreBounds);
        };
        surface.Disposed += (_, _) =>
        {
            Assert.False(form.Visible);
            Assert.False(IsWindowVisible(handle));
            surfaceDisposed = true;
        };
        form.FormClosed += (_, _) => formClosed = true;
        var exitRequested = false;
        form.ExitRequested += (_, _) =>
        {
            Assert.False(form.Visible);
            Assert.False(IsWindowVisible(handle));
            Assert.False(surfaceDisposed);
            exitRequested = true;
            form.StopAndClose();
        };

        switch (path)
        {
            case "window.close":
            case "update.applyExit":
                SendFrontendCommand(form, path);
                // The callback has already hidden the native window, but has not
                // disposed the WebView2 control or raised the deferred exit event.
                Assert.False(form.Visible);
                Assert.False(IsWindowVisible(handle));
                Assert.False(surfaceDisposed);
                Assert.False(exitRequested);
                Application.DoEvents();
                break;
            case "alt-f4/system-menu":
                SendMessage(handle, WmSysCommand, ScClose, 0);
                break;
            case "taskbar":
                SendMessage(handle, WmClose, 0, 0);
                break;
            case "tray-exit":
                form.StopAndClose();
                break;
            case "dispose":
                form.Dispose();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(path));
        }

        Assert.True(surfaceDisposed);
        Assert.True(form.IsDisposed);
        Assert.Equal(path != "dispose", formClosed);
        Assert.Equal(path == "update.applyExit", exitRequested);
    });

    [Fact]
    public void ClosingOneMainFormAllowsANewWindowOnTheSameUiThread() => RunSta(() =>
    {
        using var proxy = CreateOfflineProxy();
        using (var first = CreateForm(proxy))
        {
            SendFrontendCommand(first, "window.close");
            Application.DoEvents();
            Assert.True(first.IsDisposed);
        }
        using var reopened = CreateForm(proxy);
        Assert.True(reopened.Visible);
        Assert.True(IsWindowVisible(reopened.Handle));
        reopened.Close();
        Assert.True(reopened.IsDisposed);
    });

    [Fact]
    public void TrayAndContextDisposalKeepHideBeforePollingAndClose()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(),
            "src/UI/Core/Shell/ResourceManagerApplicationContext.cs"));
        var exit = source[source.IndexOf("private void ExitApplication()", StringComparison.Ordinal)..];
        AssertBefore(exit, "mainForm?.Hide();", "shutdownCoordinator.TryShutdown");
        Assert.Contains("form.StopAndClose();", exit, StringComparison.Ordinal);
        var contextDisposal = source[source.IndexOf("protected override void Dispose(bool disposing)", StringComparison.Ordinal)..];
        AssertBefore(contextDisposal, "RunDisposalAction(() => mainForm?.Hide());", "RunDisposalAction(shutdown.Cancel);");
        var dispose = source[source.IndexOf("private void DisposeMainForm()", StringComparison.Ordinal)..];
        AssertBefore(dispose, "form.Hide();", "form.StopPolling();");
        AssertBefore(dispose, "form.StopPolling();", "form.Dispose();");
        // The tray lifetime still clears the closed window and recreates it on show.
        Assert.Contains("form.FormClosed += OnMainFormClosed;", source, StringComparison.Ordinal);
        Assert.Contains("mainForm = null;", source, StringComparison.Ordinal);
        Assert.Contains("var form = GetOrCreateMainForm();", source, StringComparison.Ordinal);
    }

    private static FrontendLoopbackProxy CreateOfflineProxy() => new(
        new Uri("http://127.0.0.1:19421/"),
        (_, _) => throw new InvalidOperationException("Close-order tests must not start a browser or contact a backend."));

    private static MainForm CreateForm(FrontendLoopbackProxy proxy)
    {
        // The runner sets this before NativeUiPaths' lazy initialization. Never
        // resolve the installed package, initialize WebView2, or register an instance.
        var packageRoot = Environment.GetEnvironmentVariable("RESOURCE_MANAGER_PACKAGE_ROOT");
        Assert.False(string.IsNullOrWhiteSpace(packageRoot));
        Assert.StartsWith(Path.Combine(RepositoryRoot(), ".local") + Path.DirectorySeparatorChar,
            Path.GetFullPath(packageRoot!), StringComparison.OrdinalIgnoreCase);
        var form = new MainForm(_ => { }, () => { }, proxy)
        {
            ShowInTaskbar = false,
            Location = new Point(OffscreenCoordinate, OffscreenCoordinate)
        };
        form.ShowPassively();
        Assert.True(form.Visible);
        return form;
    }

    private static void SendFrontendCommand(MainForm form, string command) =>
        typeof(MainForm).GetMethod("HandleCommandWebMessage", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(form, [command]);

    private static void AssertBefore(string source, string first, string second)
    {
        var firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = source.IndexOf(second, StringComparison.Ordinal);
        Assert.True(firstIndex >= 0 && secondIndex > firstIndex, $"Expected {first} before {second}");
    }

    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../.."));

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(StaTimeoutSeconds)), "STA close test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);
}
