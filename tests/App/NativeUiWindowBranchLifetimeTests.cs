using System.Drawing;
using System.Windows.Forms;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.NativeUi;
using ResourceManager.NativeUi.Blocks;
using ResourceManager.NativeUi.WebView;

namespace Resource_Manager_APP.Tests;

public sealed class NativeUiWindowBranchLifetimeTests
{
    [Fact]
    public Task ShowDuringBrowserCleanupWaitsForRealWindowAndProxyRetirement() => OnStaAsync(async dispatcher =>
    {
        var releaseBrowser = Signal();
        await using var owner = new WindowOwner(dispatcher, releaseBrowser.Task);
        try
        {
            await owner.Trees.StartAsync(false);
            await owner.ShowAsync();
            var first = owner.Current!;
            var firstAddress = first.Proxy.Address;
            first.Form.Close();
            Assert.False(first.Form.Visible);
            await first.Host.SurfaceClosed.Task;
            Assert.False(first.Form.IsDisposed);
            Assert.False(owner.Requests.WaitForCloseAsync().IsCompleted);
            var showAgain = owner.ShowAsync();
            Assert.False(showAgain.IsCompleted);
            Assert.Same(first, owner.Current);
            Assert.Single(owner.Branches);
            Assert.Equal(BusinessBlockState.Running, owner.Trees.ReadState(NativeUiBlockIds.MainWindow));

            releaseBrowser.SetResult();
            await showAgain;
            var second = owner.Current!;
            Assert.NotSame(first.Form, second.Form);
            Assert.NotSame(first.Proxy, second.Proxy);
            Assert.NotSame(first.Host, second.Host);
            Assert.True(first.Form.IsDisposed);
            Assert.True(first.Host.View.IsDisposed);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => first.Proxy.StartAsync());
            Assert.True(second.Form.Visible);
            Assert.Equal(2, owner.Branches.Count);
            Assert.NotEmpty(firstAddress);
            Assert.NotEmpty(second.Proxy.Address);
            Assert.NotEqual(first.Proxy.Capability, second.Proxy.Capability);
            Assert.Equal(BusinessBlockState.Running, owner.Trees.ReadState(NativeUiBlockIds.TrayRoot));
            Assert.Equal(BusinessBlockState.Running, owner.Trees.ReadState(NativeUiBlockIds.BackendConnection));
        }
        finally { releaseBrowser.TrySetResult(); }
    });

    private sealed class WindowOwner : IAsyncDisposable
    {
        private readonly Control dispatcher;
        private readonly Task firstBrowserExit;
        internal NativeUiWindowRequests Requests { get; } = new();
        internal List<Branch> Branches { get; } = [];
        internal Branch? Current { get; private set; }
        internal NativeUiBlockTrees Trees { get; }

        internal WindowOwner(Control dispatcher, Task firstBrowserExit)
        {
            this.dispatcher = dispatcher;
            this.firstBrowserExit = firstBrowserExit;
            Trees = new(id => new Block(context => OnUiAsync(() => RunAsync(id, context))),
                NullLogger.Instance, NullLogger<BusinessBlockController>.Instance);
        }

        internal async Task ShowAsync()
        {
            await Requests.WaitForCloseAsync();
            await Trees.SetWindowWantedAsync(true, "show");
            await Trees.WaitForStateAsync(NativeUiBlockIds.MainWindow, BusinessBlockState.Running);
            await Trees.WaitForStateAsync(NativeUiBlockIds.WebViewHost, BusinessBlockState.Running);
            Current!.Form.ShowPassively();
        }

        private async Task RunAsync(string id, BusinessBlockRunContext context)
        {
            if (id == NativeUiBlockIds.MainWindow)
            {
                var proxy = new FrontendLoopbackProxy(new Uri("http://127.0.0.1:19421/"),
                    (_, _) => throw new InvalidOperationException("No backend traffic expected."));
                var host = new GatedHost(proxy, Branches.Count == 0 ? firstBrowserExit : Task.CompletedTask);
                var form = new MainForm(_ => { }, () => { }, host)
                {
                    ShowInTaskbar = false, Location = new Point(-30000, -30000)
                };
                var branch = new Branch(form, proxy, host);
                Current = branch;
                Branches.Add(branch);
                form.WindowCloseRequested += (_, _) => _ = Requests.CloseAsync(async () =>
                {
                    await Trees.SetWindowWantedAsync(false, "close");
                    await Trees.WaitForStateAsync(NativeUiBlockIds.MainWindow, BusinessBlockState.Stopped);
                });
                context.ReportRunning();
                try { await StoppedAsync(context.StopNotification); }
                finally
                {
                    await form.StopFrontendAsync();
                    await proxy.DisposeAsync();
                    form.CompleteClose();
                    Current = null;
                }
                return;
            }
            if (id == NativeUiBlockIds.LoopbackProxy)
            {
                var proxy = Current!.Proxy;
                try
                {
                    await proxy.StartAsync().WaitAsync(context.StopNotification);
                    context.ReportRunning();
                    await StoppedAsync(context.StopNotification);
                }
                finally { await proxy.DisposeAsync(); }
                return;
            }
            if (id == NativeUiBlockIds.WebViewHost)
            {
                var form = Current!.Form;
                try
                {
                    await form.InitializeFrontendForBlockAsync().WaitAsync(context.StopNotification);
                    context.ReportRunning();
                    await StoppedAsync(context.StopNotification);
                }
                finally { await form.StopFrontendAsync(); }
                return;
            }
            context.ReportRunning();
            await StoppedAsync(context.StopNotification);
        }

        private Task OnUiAsync(Func<Task> action)
        {
            if (!dispatcher.InvokeRequired) return action();
            var completed = Signal();
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completed.TrySetResult(); }
                catch (Exception failure) { completed.TrySetException(failure); }
            }));
            return completed.Task;
        }

        public async ValueTask DisposeAsync() => await Trees.DisposeAsync();
    }

    private sealed record Branch(MainForm Form, FrontendLoopbackProxy Proxy, GatedHost Host);
    private sealed class Block(Func<BusinessBlockRunContext, Task> run) : IBusinessBlock
    {
        public Task RunAsync(BusinessBlockRunContext context) => run(context);
    }

    private sealed class GatedHost(FrontendLoopbackProxy proxy, Task browserExit) : IFrontendHost
    {
        private Task? initialization;
        private Task? stopping;
        internal TaskCompletionSource SurfaceClosed { get; } = Signal();
        public Control View { get; } = new Panel();
        public Color BackgroundColor => Color.Black;
        public bool IsReady { get; private set; }
        public string ApplicationAddress => proxy.ApplicationAddress;
        public event EventHandler<FrontendMessageReceivedEventArgs>? MessageReceived { add { } remove { } }
        public event EventHandler<FrontendNavigationCompletedEventArgs>? NavigationCompleted { add { } remove { } }
        public Task InitializeAsync() => initialization ??= InitializeCoreAsync();
        private async Task InitializeCoreAsync()
        {
            await proxy.WaitForAddressAsync(CancellationToken.None);
            IsReady = true;
        }
        public Task StopAsync() => stopping ??= FrontendHostShutdown.DrainAsync(initialization,
            () => { IsReady = false; View.Dispose(); SurfaceClosed.SetResult(); }, () => browserExit);
        public void Dispose() => View.Dispose();
        public void Navigate(string address) { }
        public void PostStringMessage(string message) { }
        public void PostJsonMessage(string json) { }
        public void FocusContent() { }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task StoppedAsync(CancellationToken token)
    {
        try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static async Task OnStaAsync(Func<Control, Task> action)
    {
        var completed = Signal();
        var thread = new Thread(() =>
        {
            using var dispatcher = new Control();
            using var context = new ApplicationContext();
            _ = dispatcher.Handle;
            using var synchronization = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(synchronization);
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(dispatcher); completed.TrySetResult(); }
                catch (Exception failure) { completed.TrySetException(failure); }
                finally { context.ExitThread(); }
            }));
            Application.Run(context);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }
}
