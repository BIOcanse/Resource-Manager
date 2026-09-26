using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;

namespace Resource_Manager_APP.Tests;

public sealed partial class GpuPlacementWindowNativeThreadTests
{
    private sealed class WindowExecutorFactAttribute : FactAttribute
    {
        public WindowExecutorFactAttribute()
        {
            if (!OperatingSystem.IsWindows() || !File.Exists(Environment.GetEnvironmentVariable("RM_GPU_WINDOW_ACTION")) || !File.Exists(Environment.GetEnvironmentVariable("RM_GPU_WINDOW_TARGET")))
                Skip = "An explicitly built native window action executable is required.";
        }
    }

    private sealed class WindowExecutorTheoryAttribute : TheoryAttribute
    {
        public WindowExecutorTheoryAttribute()
        {
            if (!OperatingSystem.IsWindows() || !File.Exists(Environment.GetEnvironmentVariable("RM_GPU_WINDOW_ACTION")) || !File.Exists(Environment.GetEnvironmentVariable("RM_GPU_WINDOW_TARGET")))
                Skip = "An explicitly built native window action executable is required.";
        }
    }

    private static GpuWindowActionProcessLimits WindowLimits() => new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), 4096, 4096);
    private static string WindowWorkerPath => Environment.GetEnvironmentVariable("RM_GPU_WINDOW_ACTION")!;

    private static GpuWindowActionRequest WindowRequest(PrivateWindowProcess window, GpuWindowActionMethod method = GpuWindowActionMethod.Resize)
    {
        return new(window.Identity.ProcessId, window.Identity.CreationFileTimeUtc, checked((ulong)window.Handle), method);
    }

    private static Task<GpuWindowState> ReadOwnedWindow(PrivateWindowProcess window) => window.ReadAsync();

    private static Task<bool> ResizeOwnedWindow(PrivateWindowProcess window, int width) => window.ResizeAsync(width);

    private static string InputDesktopName()
    {
        var desktop = OpenInputDesktop(0, false, 1);
        Assert.NotEqual(0, desktop);
        try
        {
            var name = new StringBuilder(256);
            Assert.True(GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _));
            return name.ToString();
        }
        finally { Assert.True(CloseDesktop(desktop)); }
    }

    private void WriteWindowRun(PrivateWindowProcess window, GpuWindowActionRequest request, GpuWindowActionResult result, WindowsGpuWindowActionExecutor executor)
    {
        window.Refresh();
        using var current = Process.GetCurrentProcess();
        Assert.True(GetProcessTimes(current.SafeHandle, out var born, out _, out _, out _));
        output.WriteLine("windowAction=" + JsonSerializer.Serialize(new {
            request, result, worker = executor.Worker, testHost = new { ProcessId = Environment.ProcessId, CreationFileTimeUtc = born }, privateDesktop = window.DesktopName,
            changes = window.Changes.Select(value => new { value.Width, value.Height }),
            requests = window.Requests.Select(value => new { value.Width, value.Height, value.Flags })
        }));
        Assert.True(result.Cleanup.Complete);
        Assert.Equal(0U, result.Cleanup.ActiveProcessCount);
        Assert.Null(result.Cleanup.ObservationError);
        Assert.Null(result.Cleanup.TerminationError);
    }

    private void ClosePrivateWindow(PrivateWindowProcess window, string inputDesktop)
    {
        window.Dispose();
        Assert.True(window.DesktopClosed);
        Assert.True(window.NativeThreadExited);
        Assert.Equal(inputDesktop, InputDesktopName());
        output.WriteLine("desktopCleanup=" + JsonSerializer.Serialize(new { window.DesktopName, window.NativeThreadId, window.NativeThreadCreationFileTimeUtc, window.NativeThreadExited, window.DesktopClosed, inputDesktopUnchanged = true, userWindowUsed = false }));
    }

    [WindowExecutorTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowExecutorWaitsForExplicitPersistenceAndCompletesOnce(bool redraw)
    {
        var input = InputDesktopName();
        using var window = new PrivateWindowProcess(output.WriteLine);
        window.Start();
        var request = WindowRequest(window, redraw ? GpuWindowActionMethod.Redraw : GpuWindowActionMethod.Resize);
        var original = await ReadOwnedWindow(window);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var executor = new WindowsGpuWindowActionExecutor(WindowWorkerPath, WindowLimits(), CancellationToken.None, window.DesktopName);
        var count = 0;
        var running = executor.RunAsync(request, async prepared =>
        {
            ++count;
            Assert.Equal(original, prepared.Window.Before);
            Assert.Equal(window.NativeThreadId, prepared.Window.WindowThreadId);
            ready.SetResult();
            await allow.Task;
        });
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.Equal(original, await ReadOwnedWindow(window));
        Assert.Empty(window.Requests);
        Assert.False(running.IsCompleted);
        allow.SetResult();
        var result = await running;
        WriteWindowRun(window, request, result, executor);
        Assert.Equal(1, count);
        Assert.Equal(redraw ? GpuWindowActionOutcome.RedrawRequested : GpuWindowActionOutcome.WindowRestored, result.Outcome);
        Assert.True(result.AuthorizationMayHaveBeenSent);
        Assert.Null(result.Failure);
        Assert.Equal(0U, result.Cleanup.ExitCode);
        Assert.False(result.Cleanup.TerminationRequested);
        Assert.Equal(original, await ReadOwnedWindow(window));
        Assert.Equal(redraw ? Array.Empty<(int, int)>() : new[] { (81, 60), (80, 60) }, window.Changes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.RunAsync(request, _ => Task.CompletedTask));
        ClosePrivateWindow(window, input);
    }

    [WindowExecutorTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowExecutorDoesNotExecuteWhenPersistenceFailsOrIsCanceled(bool cancel)
    {
        var input = InputDesktopName();
        using var window = new PrivateWindowProcess(output.WriteLine);
        window.Start();
        var original = await ReadOwnedWindow(window);
        var request = WindowRequest(window);
        using var stop = new CancellationTokenSource();
        await using var executor = new WindowsGpuWindowActionExecutor(WindowWorkerPath, WindowLimits(), stop.Token, window.DesktopName);
        var result = await executor.RunAsync(request, _ =>
        {
            if (cancel) { stop.Cancel(); stop.Token.ThrowIfCancellationRequested(); }
            throw new IOException("Explicit owner persistence failure.");
        });
        WriteWindowRun(window, request, result, executor);
        Assert.Equal(GpuWindowActionOutcome.NotExecuted, result.Outcome);
        Assert.False(result.AuthorizationMayHaveBeenSent);
        Assert.NotNull(result.Prepared);
        Assert.NotNull(result.Failure);
        Assert.Empty(window.Requests);
        Assert.Equal(original, await ReadOwnedWindow(window));
        ClosePrivateWindow(window, input);
    }

    [WindowExecutorTheory]
    [InlineData("creation")]
    [InlineData("window")]
    public async Task WindowExecutorRejectsWrongIdentityBeforePersistence(string field)
    {
        var input = InputDesktopName();
        using var window = new PrivateWindowProcess(output.WriteLine);
        window.Start();
        var request = WindowRequest(window);
        request = field == "creation" ? request with { CreationFileTimeUtc = request.CreationFileTimeUtc + 1 } : request with { Window = ulong.MaxValue };
        await using var executor = new WindowsGpuWindowActionExecutor(WindowWorkerPath, WindowLimits(), CancellationToken.None, window.DesktopName);
        var result = await executor.RunAsync(request, _ => throw new Xunit.Sdk.XunitException("An invalid target reached persistence."));
        WriteWindowRun(window, request, result, executor);
        Assert.Equal(GpuWindowActionOutcome.NotExecuted, result.Outcome);
        Assert.Null(result.Prepared);
        Assert.False(result.AuthorizationMayHaveBeenSent);
        Assert.False(result.Cleanup.TerminationRequested);
        if (field == "creation")
        {
            Assert.NotNull(result.Failure);
            Assert.Null(result.Rejection);
            Assert.Null(executor.Worker);
            Assert.False(result.Cleanup.ProcessStarted);
            Assert.Null(result.Cleanup.ExitCode);
        }
        else
        {
            Assert.NotNull(result.Rejection);
            Assert.Null(result.Failure);
            Assert.Equal(0U, result.Cleanup.ExitCode);
        }
        Assert.Empty(window.Requests);
        ClosePrivateWindow(window, input);
    }

    [WindowExecutorFact]
    public async Task WindowExecutorRechecksChangedStateAfterPersistence()
    {
        var input = InputDesktopName();
        using var window = new PrivateWindowProcess(output.WriteLine);
        window.Start();
        var request = WindowRequest(window);
        await using var executor = new WindowsGpuWindowActionExecutor(WindowWorkerPath, WindowLimits(), CancellationToken.None, window.DesktopName);
        var result = await executor.RunAsync(request, async _ => { await ResizeOwnedWindow(window, 90); });
        WriteWindowRun(window, request, result, executor);
        Assert.Equal(GpuWindowActionOutcome.NotExecuted, result.Outcome);
        Assert.Equal(GpuWindowRejectionReason.StateChanged, result.Rejection?.Reason);
        Assert.Null(result.Failure);
        Assert.Equal(new[] { (90, 60) }, window.Changes);
        Assert.Equal(90, (await ReadOwnedWindow(window)).Width);
        ClosePrivateWindow(window, input);
    }

    [WindowExecutorFact]
    public async Task WindowExecutorRechecksClosedWindowAfterPersistence()
    {
        var input = InputDesktopName();
        using var window = new PrivateWindowProcess(output.WriteLine);
        window.Start();
        var request = WindowRequest(window);
        await using var executor = new WindowsGpuWindowActionExecutor(WindowWorkerPath, WindowLimits(), CancellationToken.None, window.DesktopName);
        var result = await executor.RunAsync(request, _ => { window.CloseWindow(); return Task.CompletedTask; });
        WriteWindowRun(window, request, result, executor);
        Assert.Equal(GpuWindowActionOutcome.NotExecuted, result.Outcome);
        Assert.Equal(GpuWindowRejectionReason.WindowMismatch, result.Rejection?.Reason);
        Assert.Null(result.Failure);
        ClosePrivateWindow(window, input);
    }

    [WindowExecutorFact]
    public async Task WindowExecutorRetainsRealRestorationRefusalWithoutRetry()
    {
        var input = InputDesktopName();
        using var window = new PrivateWindowProcess(output.WriteLine);
        window.Start();
        window.RefuseResizeTo(80);
        var request = WindowRequest(window);
        await using var executor = new WindowsGpuWindowActionExecutor(WindowWorkerPath, WindowLimits(), CancellationToken.None, window.DesktopName);
        var result = await executor.RunAsync(request, _ => Task.CompletedTask);
        WriteWindowRun(window, request, result, executor);
        Assert.Equal(GpuWindowActionOutcome.RestorationUnconfirmed, result.Outcome);
        Assert.Equal(GpuWindowCallState.Accepted, result.Completion?.Restore.State);
        Assert.Equal(81, result.Completion?.AfterRestore.Value?.Width);
        Assert.Equal(2, window.Requests.Length);
        Assert.False(result.Cleanup.TerminationRequested);
        Assert.Equal(0U, result.Cleanup.ExitCode);
        window.RefuseResizeTo(0);
        await ResizeOwnedWindow(window, 80);
        output.WriteLine("explicitTestRecovery=true; productRestorationResultUnchanged=true");
        ClosePrivateWindow(window, input);
    }

    [WindowExecutorTheory]
    [InlineData(0x46U)]
    [InlineData(0x47U)]
    public async Task WindowExecutorTimeoutDoesNotInventRestorationOrRetry(uint pauseMessage)
    {
        var input = InputDesktopName();
        using var window = new PrivateWindowProcess(output.WriteLine);
        window.Start();
        window.BlockNextResize(pauseMessage);
        var request = WindowRequest(window);
        await using var executor = new WindowsGpuWindowActionExecutor(WindowWorkerPath, WindowLimits(), CancellationToken.None, window.DesktopName);
        var elapsed = Stopwatch.StartNew();
        var running = executor.RunAsync(request, _ => Task.CompletedTask);
        try
        {
            Assert.True(window.ChangingEntered.Wait(TimeSpan.FromSeconds(4)));
            var result = await running.WaitAsync(TimeSpan.FromSeconds(6));
            WriteWindowRun(window, request, result, executor);
            Assert.Equal(GpuWindowActionOutcome.Unresolved, result.Outcome);
            Assert.Null(result.Completion);
            Assert.NotNull(result.Prepared);
            Assert.True(result.AuthorizationMayHaveBeenSent);
            Assert.True(result.Cleanup.TerminationRequested);
            Assert.Equal(995U, result.Cleanup.ExitCode);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(6));
            window.AllowResize();
            var after = await ReadOwnedWindow(window);
            if (pauseMessage == 0x47) Assert.Equal(81, after.Width);
            Assert.Single(window.Requests);
            output.WriteLine("afterHelperTermination=" + JsonSerializer.Serialize(new { pauseMessage, after, elapsedMilliseconds = elapsed.ElapsedMilliseconds }));
            await ResizeOwnedWindow(window, 80);
            output.WriteLine("explicitTestRecovery=true; productResultRemainsUnresolved=true");
        }
        finally { window.AllowResize(); }
        ClosePrivateWindow(window, input);
    }

    [WindowExecutorTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowExecutorDeadlineDoesNotWaitForeverForUncooperativePersistence(bool lateFailure)
    {
        var input = InputDesktopName();
        using var window = new PrivateWindowProcess(output.WriteLine);
        window.Start();
        var request = WindowRequest(window);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var executor = new WindowsGpuWindowActionExecutor(WindowWorkerPath, WindowLimits(), CancellationToken.None, window.DesktopName);
        var running = executor.RunAsync(request, _ => { entered.SetResult(); return pending.Task; });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            var result = await running.WaitAsync(TimeSpan.FromSeconds(5));
            WriteWindowRun(window, request, result, executor);
            Assert.Equal(GpuWindowActionOutcome.NotExecuted, result.Outcome);
            Assert.False(result.AuthorizationMayHaveBeenSent);
            Assert.False(pending.Task.IsCompleted);
            Assert.True(result.PersistencePending);
            Assert.Same(pending.Task, executor.PersistenceCompletion);
            Assert.Empty(window.Requests);
        }
        finally
        {
            if (lateFailure)
            {
                pending.TrySetException(new IOException("Original owner save failed after the window deadline."));
                await Assert.ThrowsAsync<IOException>(() => executor.PersistenceCompletion!);
            }
            else
            {
                pending.TrySetResult();
                await executor.PersistenceCompletion!;
            }
            var cleanup = await running.WaitAsync(TimeSpan.FromSeconds(5));
            output.WriteLine("persistenceProbeFinal=" + JsonSerializer.Serialize(cleanup));
            window.Refresh();
            Assert.Empty(window.Requests);
            output.WriteLine("ownerPersistenceSettled=" + JsonSerializer.Serialize(new { lateFailure, status = executor.PersistenceCompletion!.Status.ToString(), authorizationSent = cleanup.AuthorizationMayHaveBeenSent }));
            ClosePrivateWindow(window, input);
        }
    }

    [WindowExecutorFact]
    public async Task WindowExecutorMalformedAuthorizationNeverChangesTarget()
    {
        var input = InputDesktopName();
        using var window = new PrivateWindowProcess(output.WriteLine);
        window.Start();
        var request = WindowRequest(window);
        await using var worker = WindowsGpuWindowActionProcess.Create(WindowLimits(), CancellationToken.None);
        worker.StartSuspended(WindowWorkerPath, [], window.DesktopName);
        var parent = worker.TransferParentReadHandle();
        worker.Resume();
        await worker.ConnectAsync();
        await worker.WriteFrameAsync(GpuWindowActionProtocol.Parent(parent));
        await worker.WriteFrameAsync(GpuWindowActionProtocol.Prepare(request));
        var ready = GpuWindowActionProtocol.ReadPreparation(await worker.ReadFrameAsync(), request);
        var invalid = GpuWindowActionProtocol.Execute();
        BinaryPrimitives.WriteUInt32LittleEndian(invalid, 1);
        await worker.WriteFrameAsync(invalid);
        var rejection = GpuWindowActionProtocol.ReadRejection(await worker.ReadFrameAsync());
        await worker.WaitForExitAsync();
        var cleanup = await worker.StopAsync();
        output.WriteLine("malformedAuthorization=" + JsonSerializer.Serialize(new { request, worker = worker.Identity, ready, rejection, cleanup }));
        Assert.Equal(GpuWindowRejectionReason.InvalidRequest, rejection.Reason);
        Assert.Empty(window.Requests);
        Assert.True(cleanup.Complete);
        Assert.False(cleanup.TerminationRequested);
        Assert.Equal(0U, cleanup.ExitCode);
        ClosePrivateWindow(window, input);
    }

    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(Microsoft.Win32.SafeHandles.SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder data, int length, out int needed);
}
