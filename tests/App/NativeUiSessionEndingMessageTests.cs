using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using ResourceManager.NativeUi;
using ResourceManager.NativeUi.WebView;

namespace Resource_Manager_APP.Tests;

public sealed class NativeUiSessionEndingMessageTests
{
    private const int WmQueryEndSession = 0x0011;
    private const int WmEndSession = 0x0016;
    private const int EndSessionLogoff = unchecked((int)0x80000000);

    [Theory]
    [InlineData(0, SessionEndReasons.SystemShutdown)]
    [InlineData(EndSessionLogoff, SessionEndReasons.Logoff)]
    public Task QueryEndSessionAcceptsWindowsExitAndRequestsApplicationShutdown(
        int flags, SessionEndReasons reason) => OnStaAsync(() =>
    {
        using var host = new ControlledHost();
        using var form = CreateForm(host);
        var windowCloseRequests = 0;
        EventArgs? exitArgs = null;
        form.WindowCloseRequested += (_, _) => windowCloseRequests++;
        form.ExitRequested += (_, args) => exitArgs = args;

        var result = SendMessage(form.Handle, WmQueryEndSession, 0, flags);

        Assert.NotEqual(nint.Zero, result);
        Assert.Equal(reason, Assert.IsType<SessionEndingEventArgs>(exitArgs).Reason);
        Assert.Equal(0, windowCloseRequests);
        Assert.False(form.IsDisposed);
        Assert.False(host.Disposed);
        return Task.CompletedTask;
    });

    [Fact]
    public Task CanceledEndSessionDoesNotRequestExitOrDestroyWindow() => OnStaAsync(() =>
    {
        using var host = new ControlledHost();
        using var form = CreateForm(host);
        var exitRequests = 0;
        var windowCloseRequests = 0;
        form.WindowCloseRequested += (_, _) => windowCloseRequests++;
        form.ExitRequested += (_, _) => exitRequests++;

        var result = SendMessage(form.Handle, WmEndSession, 0, EndSessionLogoff);

        Assert.Equal(nint.Zero, result);
        Assert.Equal(0, exitRequests);
        Assert.Equal(0, windowCloseRequests);
        Assert.False(form.IsDisposed);
        Assert.False(host.Disposed);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(0, SessionEndReasons.SystemShutdown)]
    [InlineData(EndSessionLogoff, SessionEndReasons.Logoff)]
    public Task ConfirmedEndSessionRetainsWindowUntilOwnedFrontendDrainCompletes(
        int flags, SessionEndReasons reason) => OnStaAsync(async () =>
    {
        using var host = new ControlledHost();
        using var form = CreateForm(host);
        Task? shutdown = null;
        var exitRequests = 0;
        var windowCloseRequests = 0;
        EventArgs? exitArgs = null;
        form.WindowCloseRequested += (_, _) => windowCloseRequests++;
        form.ExitRequested += (_, args) =>
        {
            exitArgs = args;
            exitRequests++;
            shutdown = DrainAndCloseAsync(form);
        };

        var result = SendMessage(form.Handle, WmEndSession, 1, flags);

        Assert.Equal(nint.Zero, result);
        Assert.Equal(1, exitRequests);
        Assert.Equal(reason, Assert.IsType<SessionEndingEventArgs>(exitArgs).Reason);
        Assert.Equal(0, windowCloseRequests);
        Assert.NotNull(shutdown);
        Assert.True(host.StopStarted);
        Assert.False(shutdown.IsCompleted);
        Assert.False(form.IsDisposed);
        Assert.False(host.Disposed);
        Assert.Throws<InvalidOperationException>(form.CompleteClose);
        host.Drained.SetResult();
        await shutdown;
        Assert.True(form.IsDisposed);
        Assert.True(host.Disposed);
    });

    [Theory]
    [InlineData(CloseReason.WindowsShutDown)]
    [InlineData(CloseReason.ApplicationExitCall)]
    public Task NonUserFormClosingIsNotCanceledAsAWindowClose(CloseReason reason) => OnStaAsync(() =>
    {
        using var host = new ControlledHost();
        using var form = CreateForm(host);
        var requests = 0;
        form.WindowCloseRequested += (_, _) => requests++;
        var args = new FormClosingEventArgs(reason, false);

        typeof(MainForm).GetMethod("OnFormClosing", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(form, [args]);

        Assert.False(args.Cancel);
        Assert.Equal(0, requests);
        return Task.CompletedTask;
    });

    [Fact]
    public Task RawWindowCloseRemainsOwnedByWindowBranch() => OnStaAsync(() =>
    {
        using var host = new ControlledHost();
        using var form = CreateForm(host);
        var windowCloseRequests = 0;
        var exitRequests = 0;
        form.WindowCloseRequested += (_, _) => windowCloseRequests++;
        form.ExitRequested += (_, _) => exitRequests++;

        // WinForms labels a raw taskbar WM_CLOSE as TaskManagerClosing.
        SendMessage(form.Handle, 0x0010, 0, 0);

        Assert.Equal(1, windowCloseRequests);
        Assert.Equal(0, exitRequests);
        Assert.False(form.IsDisposed);
        Assert.False(host.Disposed);
        return Task.CompletedTask;
    });

    private static async Task DrainAndCloseAsync(MainForm form)
    {
        await form.StopFrontendAsync();
        form.CompleteClose();
    }

    private static MainForm CreateForm(IFrontendHost host)
    {
        var form = new MainForm(_ => { }, () => { }, host) { ShowInTaskbar = false };
        // Create an actual hidden HWND. No browser, backend, user window, or real
        // Windows logoff is involved; SendMessage targets only this test form.
        _ = form.Handle;
        Assert.False(form.Visible);
        return form;
    }

    private sealed class ControlledHost : IFrontendHost
    {
        public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Control View { get; } = new Panel();
        public Color BackgroundColor => Color.Black;
        public bool IsReady => false;
        public string ApplicationAddress => "http://127.0.0.1/";
        public bool StopStarted { get; private set; }
        public bool Disposed { get; private set; }
        public event EventHandler<FrontendMessageReceivedEventArgs>? MessageReceived { add { } remove { } }
        public event EventHandler<FrontendNavigationCompletedEventArgs>? NavigationCompleted { add { } remove { } }
        public Task InitializeAsync() => throw new InvalidOperationException("No browser is started.");
        public Task StopAsync() { StopStarted = true; return Drained.Task; }
        public void Dispose() { Disposed = true; View.Dispose(); }
        public void Navigate(string address) { }
        public void PostStringMessage(string message) { }
        public void PostJsonMessage(string json) { }
        public void FocusContent() { }
    }

    private static async Task OnStaAsync(Func<Task> action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var dispatcher = new Control();
            using var context = new ApplicationContext();
            _ = dispatcher.Handle;
            using var synchronization = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(synchronization);
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completed.TrySetResult(); }
                catch (Exception error) { completed.TrySetException(error); }
                finally { context.ExitThread(); }
            }));
            Application.Run(context);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
}
