using ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;
using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement.Preparation;

internal abstract record OpenGlCallbackPreparationResult(
    GpuWindowActionProcessIdentity? Worker, GpuWindowActionProcessCleanup Cleanup)
{
    internal sealed record Success(PreparedOpenGlCallbacks Source, GpuWindowActionProcessIdentity WorkerIdentity,
        GpuWindowActionProcessCleanup ProcessCleanup) : OpenGlCallbackPreparationResult(WorkerIdentity, ProcessCleanup);

    internal sealed record Failure(uint? AcquisitionError, uint? NativeCleanupError, string? ExecutionError,
        GpuWindowActionProcessIdentity? WorkerIdentity, GpuWindowActionProcessCleanup ProcessCleanup)
        : OpenGlCallbackPreparationResult(WorkerIdentity, ProcessCleanup);
}

internal sealed class WindowsOpenGlCallbackPreparation : IAsyncDisposable
{
    private readonly string executable;
    private readonly GpuWindowActionProcessLimits limits;
    private readonly CancellationToken cancellationToken;
    private readonly ulong deadlineMilliseconds;
    private WindowsGpuWindowActionProcess? process;
    private int started;

    internal WindowsOpenGlCallbackPreparation(string executable, GpuWindowActionProcessLimits limits,
        CancellationToken cancellationToken, ulong deadlineMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        if (limits.MaximumFrameBytes < OpenGlCallbackPreparationProtocol.MaximumMessageBytes)
            throw new ArgumentOutOfRangeException(nameof(limits), "The frame bound must fit the complete callback source.");
        if (deadlineMilliseconds > checked((ulong)Environment.TickCount64 + (ulong)limits.TotalBudget.TotalMilliseconds))
            throw new ArgumentOutOfRangeException(nameof(deadlineMilliseconds));
        this.executable = Path.GetFullPath(executable);
        this.limits = limits;
        this.cancellationToken = cancellationToken;
        this.deadlineMilliseconds = deadlineMilliseconds;
    }

    internal GpuWindowActionProcessIdentity? Worker => process?.Identity;
    internal Task<GpuWindowActionProcessCleanup>? CleanupCompletion { get; private set; }

    internal async Task<OpenGlCallbackPreparationResult> RunAsync(int targetProcessId,
        long targetCreationFileTimeUtc, ulong sourceAdapterLuid)
    {
        if (targetProcessId <= 4 || targetCreationFileTimeUtc <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetProcessId));
        if (Interlocked.Exchange(ref started, 1) != 0)
            throw new InvalidOperationException("This callback preparation is single-use.");
        OpenGlCallbackPreparationReply? reply = null;
        string? failure = null;
        GpuWindowActionProcessCleanup cleanup;
        try
        {
            process = WindowsGpuWindowActionProcess.CreateForTarget(targetProcessId, targetCreationFileTimeUtc,
                limits, cancellationToken, deadlineMilliseconds);
            process.StartSuspended(executable, []);
            var parentHandle = process.TransferParentReadHandle();
            process.Resume();
            await process.ConnectAsync().ConfigureAwait(false);
            await process.WriteFrameAsync(GpuWindowActionProtocol.Parent(parentHandle)).ConfigureAwait(false);
            await process.WriteFrameAsync(OpenGlCallbackPreparationProtocol.Request(sourceAdapterLuid)).ConfigureAwait(false);
            reply = OpenGlCallbackPreparationProtocol.ReadReply(await process.ReadFrameAsync().ConfigureAwait(false));
            await process.WaitForExitAsync().ConfigureAwait(false);
            process.WorkCancellation.ThrowIfCancellationRequested();
        }
        catch (Exception exception) { failure = exception.ToString(); }
        finally
        {
            cleanup = process is null ? new(false, false, null, false, null, 0, true, null)
                : await process.StopAsync().ConfigureAwait(false);
            CleanupCompletion = process?.CompleteCleanupAsync() ?? Task.FromResult(cleanup);
        }

        if (reply is OpenGlCallbackPreparationReply.Success success && failure is null
            && cleanup.Complete && cleanup.ExitObserved && cleanup.ExitCode == 0
            && !cleanup.TerminationRequested && cleanup.TerminationError is null && cleanup.ObservationError is null)
            return new OpenGlCallbackPreparationResult.Success(success.Source, process!.Identity!, cleanup);
        var nativeFailure = reply as OpenGlCallbackPreparationReply.Failure;
        return new OpenGlCallbackPreparationResult.Failure(nativeFailure?.Error, nativeFailure?.CleanupError,
            failure, process?.Identity, cleanup);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.CompareExchange(ref started, 1, 0);
        return process?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
