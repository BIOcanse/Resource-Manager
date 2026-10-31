using System.Drawing;
using System.Windows.Forms;
using ResourceManager.NativeUi;
using ResourceManager.NativeUi.WebView;

namespace Resource_Manager_APP.Tests;

public sealed class NativeUiFrontendLifetimeTests
{
    [Fact]
    public async Task ShutdownJoinsLateInitializationAndWaitsForBrowserExit()
    {
        var initialized = Gate();
        var exited = Gate();
        var closeReached = Gate();
        var closed = false;
        var stopping = FrontendHostShutdown.DrainAsync(initialized.Task,
            () => { closed = true; closeReached.SetResult(); }, () => exited.Task);
        Assert.False(closed);
        Assert.False(stopping.IsCompleted);
        initialized.SetResult();
        await closeReached.Task;
        Assert.True(closed);
        Assert.False(stopping.IsCompleted);
        exited.SetResult();
        await stopping;
    }

    [Fact]
    public async Task FailedInitializationStillClosesSurfaceAndWaitsForBrowserExit()
    {
        var exited = Gate();
        var closed = false;
        var stopping = FrontendHostShutdown.DrainAsync(Task.FromException(new IOException("startup")),
            () => closed = true, () => exited.Task);
        Assert.True(closed);
        Assert.False(stopping.IsCompleted);
        exited.SetResult();
        await stopping;
    }

    [Fact]
    public Task CloseDuringInitializationHidesAndRetainsWindowUntilBrowserExit() => OnStaAsync(async () =>
    {
        using var host = new ControlledHost();
        using var form = new MainForm(_ => { }, host)
        {
            ShowInTaskbar = false,
            Location = new Point(-30000, -30000)
        };
        form.ShowPassively();
        var requests = 0;
        form.WindowCloseRequested += (_, _) => requests++;
        var initializing = form.InitializeFrontendForBlockAsync();
        form.Close();
        Assert.Equal(1, requests);
        Assert.False(form.Visible);
        Assert.False(form.IsDisposed);
        var stopping = form.StopFrontendAsync();
        Assert.Same(stopping, form.StopFrontendAsync());
        Assert.False(stopping.IsCompleted);
        Assert.False(host.SurfaceClosed);
        host.Initialized.SetResult();
        Assert.True(await initializing);
        await host.CloseReached.Task;
        Assert.True(host.SurfaceClosed);
        Assert.False(form.IsDisposed);
        Assert.False(stopping.IsCompleted);
        Assert.Throws<InvalidOperationException>(form.CompleteClose);
        host.BrowserExited.SetResult();
        await stopping;
        form.CompleteClose();
        Assert.True(form.IsDisposed);
        Assert.Equal(1, requests);
    });

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ControlledHost : IFrontendHost
    {
        public TaskCompletionSource Initialized { get; } = Gate();
        public TaskCompletionSource BrowserExited { get; } = Gate();
        public TaskCompletionSource CloseReached { get; } = Gate();
        public Control View { get; } = new Panel();
        public Color BackgroundColor => Color.Black;
        public bool IsReady { get; private set; }
        public string ApplicationAddress => "http://127.0.0.1/";
        public bool SurfaceClosed { get; private set; }
        private Task? stopping;
        public event EventHandler<FrontendMessageReceivedEventArgs>? MessageReceived { add { } remove { } }
        public event EventHandler<FrontendNavigationCompletedEventArgs>? NavigationCompleted { add { } remove { } }
        public async Task InitializeAsync() { await Initialized.Task; IsReady = true; }
        public Task StopAsync() => stopping ??= FrontendHostShutdown.DrainAsync(Initialized.Task,
            () => { SurfaceClosed = true; IsReady = false; View.Dispose(); CloseReached.SetResult(); },
            () => BrowserExited.Task);
        public void Dispose() => View.Dispose();
        public void Navigate(string address) { }
        public void PostStringMessage(string message) { }
        public void PostJsonMessage(string json) { }
        public void FocusContent() { }
    }

    private static async Task OnStaAsync(Func<Task> action)
    {
        var completed = Gate();
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
}
