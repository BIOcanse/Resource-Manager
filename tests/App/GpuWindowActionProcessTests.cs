using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed class GpuWindowActionProcessTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(28000UL, 1000)]
    [InlineData(29000UL, 1000)]
    [InlineData(29500UL, 500)]
    [InlineData(30000UL, 0)]
    [InlineData(31000UL, 0)]
    public void CleanupWaitNeverRenewsTimeAlreadySpent(ulong now, int expectedMilliseconds)
        => Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds),
            WindowsGpuWindowActionProcess.RemainingWait(30000, now, TimeSpan.FromSeconds(1)));

    [WindowWorkerFact]
    public async Task LateStopRetainsOneCompletionTaskUntilTheSameWorkerAndIoExit()
    {
        await using var execution = Start("no-output");
        execution.Resume();
        await execution.ConnectAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.ReadFrameAsync());
        // Enter stop after the original five-second budget, not with a renewed reserve.
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        var stopping = execution.StopAsync();
        var initial = await stopping;
        var completion = execution.CompleteCleanupAsync();
        Assert.Same(stopping, execution.StopAsync());
        Assert.Same(completion, execution.CompleteCleanupAsync());
        var final = await completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(final.Complete);
        Assert.Equal(0U, final.ActiveProcessCount);
        Assert.True(final.TerminationRequested);
        Assert.Equal(995U, final.ExitCode);
        output.WriteLine("lateCleanupHandoff=" + JsonSerializer.Serialize(new { execution.Identity, initial, final, sameCompletionTask = true }));
    }

    private static GpuWindowActionProcessLimits Limits(int maximumFrameBytes = 4096) =>
        new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), maximumFrameBytes, 4096);

    [WindowWorkerFact]
    public async Task SuspendedWorkerIsOwnedBeforeItCanConnectAndCompletesNormally()
    {
        await using var execution = Start("normal");
        Assert.Equal(1u, execution.ReadActiveProcessCount());
        var connection = execution.ConnectAsync();
        Assert.False(connection.IsCompleted);
        execution.Resume();
        await connection;
        await CheckHello(execution);
        var payload = Encoding.UTF8.GetBytes("one explicit command");
        await execution.WriteFrameAsync(payload);
        Assert.Equal(payload, await execution.ReadFrameAsync());
        var firstWait = execution.WaitForExitAsync();
        Assert.Same(firstWait, execution.WaitForExitAsync());
        await firstWait;
        await CheckCleanup(execution, false, 0);
    }

    [WindowWorkerTheory]
    [InlineData("no-connect")]
    [InlineData("no-output")]
    [InlineData("partial-prefix")]
    [InlineData("partial-body")]
    public async Task DeadlineCancelsActualPendingPipeIoAndStopsOnlyOwnedWorker(string mode)
    {
        var elapsed = Stopwatch.StartNew();
        await using var execution = Start(mode);
        var connection = execution.ConnectAsync();
        execution.Resume();
        if (mode == "no-connect")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection);
        else
        {
            await connection;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.ReadFrameAsync());
        }
        await CheckCleanup(execution, true, 995);
        output.WriteLine("boundedElapsedMilliseconds={0}", elapsed.ElapsedMilliseconds);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(8));
    }

    [WindowWorkerTheory]
    [InlineData("oversize")]
    [InlineData("empty-frame")]
    public async Task InvalidFrameFailsBeforeAllocationAndCannotBeRetried(string mode)
    {
        await using var execution = Start(mode);
        execution.Resume();
        await execution.ConnectAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => execution.ReadFrameAsync());
        Assert.Throws<InvalidOperationException>(() => { _ = execution.ReadFrameAsync(); });
        await CheckCleanup(execution, true, 995);
    }

    [WindowWorkerFact]
    public async Task ChildExitIsObservedSeparatelyFromMissingResponse()
    {
        await using var execution = Start("crash");
        execution.Resume();
        await execution.ConnectAsync();
        await Assert.ThrowsAnyAsync<IOException>(() => execution.ReadFrameAsync());
        await execution.WaitForExitAsync();
        await CheckCleanup(execution, false, 23);
    }

    [WindowWorkerFact]
    public async Task ACompleteResponseDoesNotCountAsProcessExit()
    {
        await using var execution = Start("hang-after-result");
        execution.Resume();
        await execution.ConnectAsync();
        await CheckHello(execution);
        await execution.WriteFrameAsync("done"u8.ToArray());
        Assert.Equal("done", Encoding.UTF8.GetString(await execution.ReadFrameAsync()));
        Assert.Equal(1u, execution.ReadActiveProcessCount());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitForExitAsync());
        await CheckCleanup(execution, true, 995);
    }

    [WindowWorkerFact]
    public async Task BackPressureWriteIsActuallyCanceledAndSettled()
    {
        await using var execution = Start("no-read", Limits(1024 * 1024));
        execution.Resume();
        await execution.ConnectAsync();
        await CheckHello(execution);
        var pending = execution.WriteFrameAsync(new byte[1024 * 1024]);
        Assert.False(pending.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => { _ = execution.WriteFrameAsync(new byte[1]); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await CheckCleanup(execution, true, 995);
    }

    [WindowWorkerFact]
    public async Task ForeignPipePeerIsRejectedBeforeAnyFrameCanBeRead()
    {
        await using var execution = Start("normal");
        var connection = execution.ConnectAsync();
        using var wrongPeer = new NamedPipeClientStream(".", execution.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await wrongPeer.ConnectAsync(2000);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => connection);
        Assert.Throws<InvalidOperationException>(() => { _ = execution.ReadFrameAsync(); });
        Assert.Throws<InvalidOperationException>(() => { _ = execution.ConnectAsync(); });
        await CheckCleanup(execution, true, 995);
    }

    [WindowWorkerFact]
    public async Task CallerStopCancelsLiveIoAndWaitWithoutReleasingUnfinishedOperations()
    {
        await using var execution = Start("no-read");
        execution.Resume();
        await execution.ConnectAsync();
        await CheckHello(execution);
        var read = execution.ReadFrameAsync();
        var exit = execution.WaitForExitAsync();
        Assert.False(read.IsCompleted);
        Assert.False(exit.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => { _ = execution.ReadFrameAsync(); });
        var stop = execution.StopAsync();
        Assert.Same(stop, execution.StopAsync());
        await CheckCleanup(execution, true, 995);
        Assert.True(read.IsCompleted);
        Assert.True(exit.IsCompleted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exit);
    }

    [WindowWorkerFact]
    public async Task HostCancellationAlsoSettlesTheExactNativeWait()
    {
        using var cancellation = new CancellationTokenSource();
        await using var execution = Start("no-read", cancellationToken: cancellation.Token);
        execution.Resume();
        await execution.ConnectAsync();
        await CheckHello(execution);
        var read = execution.ReadFrameAsync();
        var exit = execution.WaitForExitAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exit);
        await CheckCleanup(execution, true, 995);
    }

    [WindowWorkerFact]
    public async Task SingleUseBoundariesAndNativeDescendantLimitAreEnforced()
    {
        await using var execution = Start("descendant");
        Assert.Throws<InvalidOperationException>(() => execution.StartSuspended(ProbePath, ["normal"]));
        execution.Resume();
        Assert.Throws<InvalidOperationException>(() => execution.Resume());
        await execution.ConnectAsync();
        await CheckHello(execution);
        await execution.WriteFrameAsync("try-child"u8.ToArray());
        using var response = JsonDocument.Parse(await execution.ReadFrameAsync());
        output.WriteLine("descendant=" + response.RootElement.GetRawText());
        Assert.False(response.RootElement.GetProperty("created").GetBoolean());
        Assert.NotEqual(0u, response.RootElement.GetProperty("error").GetUInt32());
        await execution.WaitForExitAsync();
        await CheckCleanup(execution, false, 0);
    }

    [WindowWorkerFact]
    public async Task FailedLaunchDoesNotLeaveANativeProcessAndCannotRetry()
    {
        await using var execution = WindowsGpuWindowActionProcess.Create(Limits(), CancellationToken.None);
        Assert.Throws<FileNotFoundException>(() => execution.StartSuspended(ProbePath + ".absent", ["normal"]));
        Assert.Throws<InvalidOperationException>(() => execution.StartSuspended(ProbePath, ["normal"]));
        Assert.Throws<InvalidOperationException>(() => execution.Resume());
        Assert.Throws<InvalidOperationException>(() => { _ = execution.ConnectAsync(); });
        var cleanup = await execution.StopAsync();
        output.WriteLine("cleanup=" + JsonSerializer.Serialize(cleanup));
        Assert.True(cleanup.Complete);
        Assert.False(cleanup.ProcessStarted);
        Assert.Null(cleanup.ExitCode);
        Assert.False(cleanup.TerminationRequested);
    }

    [Fact]
    public void ExplicitBoundsAndAlreadyCanceledAdmissionHaveNoLaunchSideEffect()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsGpuWindowActionProcess.Create(
            Limits() with { CleanupReserve = TimeSpan.Zero }, CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsGpuWindowActionProcess.Create(
            Limits() with { MaximumFrameBytes = 0 }, CancellationToken.None));
        Assert.Throws<OperationCanceledException>(() => WindowsGpuWindowActionProcess.Create(Limits(), new CancellationToken(true)));
    }

    [WindowWorkerFact]
    public async Task ThrowingCancellationObserverDoesNotSkipOwnedCleanup()
    {
        await using var execution = Start("no-read");
        execution.Resume();
        await execution.ConnectAsync();
        await CheckHello(execution);
        var stoppingThread = Environment.CurrentManagedThreadId;
        using var observer = execution.WorkCancellation.Register(() =>
        {
            if (Environment.CurrentManagedThreadId == stoppingThread) throw new InvalidOperationException("Explicit cancellation observer failure.");
        });
        var cleanup = await execution.StopAsync();
        output.WriteLine("cleanup=" + JsonSerializer.Serialize(cleanup));
        Assert.True(cleanup.Complete);
        Assert.True(cleanup.TerminationRequested);
        Assert.Equal(995U, cleanup.ExitCode);
        Assert.Equal(0U, cleanup.ActiveProcessCount);
        Assert.Contains("Explicit cancellation observer failure", cleanup.ObservationError);
        Assert.Null(cleanup.TerminationError);
        Assert.Same(cleanup, await execution.StopAsync());
    }

    private static string ProbePath => Path.GetFullPath(Environment.GetEnvironmentVariable("RM_GPU_WINDOW_WORKER_PROBE")!);

    private WindowsGpuWindowActionProcess Start(string mode, GpuWindowActionProcessLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        var execution = WindowsGpuWindowActionProcess.Create(limits ?? Limits(), cancellationToken);
        try { execution.StartSuspended(ProbePath, [mode]); }
        catch { execution.DisposeAsync().AsTask().GetAwaiter().GetResult(); throw; }
        using var parent = Process.GetCurrentProcess();
        Assert.True(GetProcessTimes(parent.SafeHandle, out var creation, out _, out _, out _));
        output.WriteLine("parent=" + JsonSerializer.Serialize(new { processId = Environment.ProcessId, creationFileTimeUtc = creation }));
        output.WriteLine("worker=" + JsonSerializer.Serialize(execution.Identity));
        output.WriteLine("mode=" + mode);
        return execution;
    }

    private async Task CheckHello(WindowsGpuWindowActionProcess execution)
    {
        using var hello = JsonDocument.Parse(await execution.ReadFrameAsync());
        output.WriteLine("hello=" + hello.RootElement.GetRawText());
        Assert.Equal(execution.Identity!.ProcessId, hello.RootElement.GetProperty("pid").GetInt32());
        Assert.Equal(execution.Identity.CreationFileTimeUtc, hello.RootElement.GetProperty("creationFileTimeUtc").GetInt64());
        Assert.Equal(Environment.ProcessId, hello.RootElement.GetProperty("parentPid").GetInt32());
        Assert.Equal(32771u, hello.RootElement.GetProperty("errorMode").GetUInt32());
        Assert.Equal(1u, hello.RootElement.GetProperty("activeProcessLimit").GetUInt32());
        Assert.Equal(1u, hello.RootElement.GetProperty("activeProcessCount").GetUInt32());
    }

    private async Task CheckCleanup(WindowsGpuWindowActionProcess execution, bool terminated, uint exitCode)
    {
        var cleanup = await execution.StopAsync();
        output.WriteLine("cleanup=" + JsonSerializer.Serialize(cleanup));
        Assert.True(cleanup.Complete);
        Assert.True(cleanup.ProcessStarted);
        Assert.True(cleanup.ExitObserved);
        Assert.Equal(exitCode, cleanup.ExitCode);
        Assert.Equal(terminated, cleanup.TerminationRequested);
        Assert.Null(cleanup.TerminationError);
        Assert.Null(cleanup.ObservationError);
        Assert.Equal(0u, cleanup.ActiveProcessCount);
        Assert.True(cleanup.IoCompleted);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
}

internal sealed class WindowWorkerFactAttribute : FactAttribute
{
    public WindowWorkerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RM_GPU_WINDOW_WORKER_PROBE")))
            Skip = "The explicit native window-worker probe path is required.";
    }
}

internal sealed class WindowWorkerTheoryAttribute : TheoryAttribute
{
    public WindowWorkerTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RM_GPU_WINDOW_WORKER_PROBE")))
            Skip = "The explicit native window-worker probe path is required.";
    }
}
