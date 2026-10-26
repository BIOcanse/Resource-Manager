using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using ResourceManager.NativeUi.WebView;

namespace Resource_Manager_APP.Tests;

public sealed class BrowserRuntimeCancellationTests
{
    [Fact]
    public Task HostStopDuringConsentJoinsModalResolutionBeforeClosingSurface() => OnStaAsync(async () =>
    {
        var scratch = Environment.GetEnvironmentVariable("RM_REVIEW_SCRATCH")
            ?? throw new InvalidOperationException("The review runner must provide its registered scratch directory.");
        Assert.True(Directory.Exists(scratch));
        using var proxy = new FrontendLoopbackProxy(new Uri("http://127.0.0.1:9321/"),
            (_, _) => throw new InvalidOperationException("Consent does not use the backend."));
        await proxy.StartAsync();
        var context = SynchronizationContext.Current!;
        WebView2FrontendHost? host = null;
        Task? stopping = null;
        var closed = false;
        var retainedDuringConsent = false;
        host = new WebView2FrontendHost(new WebView2HostConfiguration(
            scratch, Path.Combine(scratch, "unused-preferences.json"), proxy), Color.Black,
            token => BrowserRuntimeResolver.ResolveAsync(() => null,
                () => BrowserRuntimeResolver.ConfirmInstallation(token,
                    page =>
                    {
                        context.Post(_ =>
                        {
                            stopping = host!.StopAsync();
                            retainedDuringConsent = !host.View.IsDisposed;
                        }, null);
                        var deadline = Stopwatch.StartNew();
                        while (!closed && deadline.Elapsed < TimeSpan.FromSeconds(2))
                        {
                            Application.DoEvents();
                            Thread.Yield();
                        }
                        return page.Buttons[1];
                    }, _ => closed = true),
                () => throw new InvalidOperationException("Stopping consent must never install."), token));
        using (host)
        {
            var initializing = host.InitializeAsync();
            try
            {
                var result = await Record.ExceptionAsync(() => initializing.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.True(closed, "Host stop must dismiss its runtime confirmation.");
                Assert.True(retainedDuringConsent, "Surface disposal must join the pending modal resolution.");
                Assert.IsAssignableFrom<OperationCanceledException>(result);
                Assert.NotNull(stopping);
                await stopping.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.True(host.View.IsDisposed);
                Assert.Null(host.EnvironmentForDiagnostics);
            }
            finally
            {
                await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
                try { await initializing; } catch { }
                await proxy.DisposeAsync();
            }
        }
    });

    [Fact]
    public Task HostStopCancelsRuntimeResolutionAndJoinsItBeforeClosingSurface() => OnStaAsync(async () =>
    {
        var scratch = Environment.GetEnvironmentVariable("RM_REVIEW_SCRATCH")
            ?? throw new InvalidOperationException("The review runner must provide its registered scratch directory.");
        Assert.True(Directory.Exists(scratch));
        using var proxy = new FrontendLoopbackProxy(new Uri("http://127.0.0.1:9321/"),
            (_, _) => throw new InvalidOperationException("No backend request belongs to runtime cancellation."));
        await proxy.StartAsync();
        var runtime = new TaskCompletionSource<BrowserRuntimeResolution>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken runtimeStop = default;
        using var host = new WebView2FrontendHost(new WebView2HostConfiguration(
            scratch, Path.Combine(scratch, "unused-preferences.json"), proxy), Color.Black,
            token => { runtimeStop = token; resolving.SetResult(); return runtime.Task.WaitAsync(token); });
        var initializing = host.InitializeAsync();
        await resolving.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(initializing.IsCompleted);
        var stopping = host.StopAsync();
        try
        {
            Assert.True(runtimeStop.IsCancellationRequested,
                "Host stop must reach the runtime resolver, including its installation confirmation.");
            await stopping.WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initializing);
            Assert.True(host.View.IsDisposed);
            Assert.Null(host.EnvironmentForDiagnostics);
            Assert.False(runtime.Task.IsCompleted);
        }
        finally
        {
            runtime.TrySetCanceled();
            await stopping.WaitAsync(TimeSpan.FromSeconds(2));
            try { await initializing; } catch { }
            await proxy.DisposeAsync();
        }
    });

    [Fact]
    public Task StopClosesRuntimeConsentOnItsUiThread() => OnStaAsync(() =>
    {
        using var stop = new CancellationTokenSource();
        var uiThread = Environment.CurrentManagedThreadId;
        var closed = false;
        var shown = false;
        var result = Record.Exception(() => BrowserRuntimeResolver.ConfirmInstallation(stop.Token,
            page =>
            {
                shown = true;
                // Stand in for ShowDialog's nested message loop. This does not create
                // a visible dialog or touch desktop focus, and has a bounded red path.
                Task.Run(stop.Cancel).GetAwaiter().GetResult();
                var deadline = Stopwatch.StartNew();
                while (!closed && deadline.Elapsed < TimeSpan.FromSeconds(2))
                {
                    Application.DoEvents();
                    Thread.Yield();
                }
                return page.Buttons[1];
            },
            _ =>
            {
                Assert.Equal(uiThread, Environment.CurrentManagedThreadId);
                closed = true;
            }));
        Assert.True(shown);
        Assert.True(closed, "Stopping the host must close the pending installation confirmation.");
        Assert.IsAssignableFrom<OperationCanceledException>(result);
        return Task.CompletedTask;
    });

    [Fact]
    public async Task StopAfterConsentStopsWaitingWithoutCancelingAcceptedInstaller()
    {
        using var stop = new CancellationTokenSource();
        var installer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probes = 0;
        var installing = false;
        var resolution = BrowserRuntimeResolver.ResolveAsync(
            () => { probes++; return null; },
            () => true,
            () => { installing = true; return installer.Task; }, stop.Token);
        Assert.True(installing);
        stop.Cancel();
        Exception? result;
        try
        {
            result = await Record.ExceptionAsync(() => resolution.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(installer.Task.IsCompleted);
            Assert.IsAssignableFrom<OperationCanceledException>(result);
            Assert.Equal(1, probes);
        }
        finally
        {
            // Release the simulated system installer even when the old resolver
            // incorrectly keeps waiting. No real installer is launched or killed.
            installer.TrySetResult();
            try { await resolution; } catch { }
        }
    }

    [Fact]
    public async Task StopBeforeRuntimeResolutionNeverPromptsOrInstalls()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var probes = 0;
        var prompts = 0;
        var installs = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BrowserRuntimeResolver.ResolveAsync(
            () => { probes++; return null; },
            () => { prompts++; return true; },
            () => { installs++; return Task.CompletedTask; }, stop.Token));
        Assert.Equal(0, probes);
        Assert.Equal(0, prompts);
        Assert.Equal(0, installs);
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
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }
}
