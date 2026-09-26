using ResourceManager.App.Application.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;

internal sealed record GpuWindowActionPrepared(GpuWindowPreparation Window, GpuWindowActionProcessIdentity Worker);
internal sealed record GpuWindowActionResult(GpuWindowActionOutcome Outcome, GpuWindowActionPrepared? Prepared,
    GpuWindowCompletion? Completion, GpuWindowRejection? Rejection, bool AuthorizationMayHaveBeenSent, bool PersistencePending,
    GpuWindowActionProcessCleanup Cleanup, string? Failure)
{
    internal static GpuWindowActionOutcome ResolveOutcome(GpuWindowActionPrepared? prepared,
        GpuWindowCompletion? completed, GpuWindowRejection? rejection, bool authorized,
        GpuWindowActionProcessCleanup cleanup, string? failure)
    {
        var normal = failure is null && cleanup.Complete && cleanup.ExitCode == 0 && !cleanup.TerminationRequested
            && cleanup.TerminationError is null && cleanup.ObservationError is null;
        var outcome = !authorized ? GpuWindowActionOutcome.NotExecuted : GpuWindowActionOutcome.Unresolved;
        if (normal && rejection is not null) return GpuWindowActionOutcome.NotExecuted;
        if (!normal || completed is null) return outcome;
        return prepared!.Window.Request.Method == GpuWindowActionMethod.Redraw
            ? (completed.Change.State == GpuWindowCallState.Accepted ? GpuWindowActionOutcome.RedrawRequested : GpuWindowActionOutcome.NotExecuted)
            : completed.Restore.State == GpuWindowCallState.Accepted && completed.AfterRestore.Value == prepared.Window.Before
                ? GpuWindowActionOutcome.WindowRestored : GpuWindowActionOutcome.RestorationUnconfirmed;
    }
}

internal sealed class WindowsGpuWindowActionExecutor : IAsyncDisposable
{
    private WindowsGpuWindowActionProcess? process;
    private readonly GpuWindowActionProcessLimits limits;
    private readonly CancellationToken cancellationToken;
    private readonly ulong deadlineMilliseconds;
    private readonly string executable;
    private readonly string? desktop;
    private int started;

    internal WindowsGpuWindowActionExecutor(string executable, GpuWindowActionProcessLimits limits,
        CancellationToken cancellationToken, string? desktop = null, ulong? deadlineMilliseconds = null)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (limits.MaximumFrameBytes < GpuWindowActionProtocol.MaximumMessageBytes)
            throw new ArgumentOutOfRangeException(nameof(limits), "The frame bound cannot truncate the fixed window protocol.");
        this.executable = Path.GetFullPath(executable);
        this.desktop = desktop;
        this.limits = limits;
        this.cancellationToken = cancellationToken;
        var maximumDeadline = checked((ulong)Environment.TickCount64 + (ulong)limits.TotalBudget.TotalMilliseconds);
        this.deadlineMilliseconds = deadlineMilliseconds ?? maximumDeadline;
        if (this.deadlineMilliseconds > maximumDeadline) throw new ArgumentOutOfRangeException(nameof(deadlineMilliseconds));
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    }

    internal GpuWindowActionProcessIdentity? Worker => process?.Identity;
    internal Task? PersistenceCompletion { get; private set; }
    internal Task<GpuWindowActionProcessCleanup>? CleanupCompletion { get; private set; }

    internal async Task<GpuWindowActionResult> RunAsync(GpuWindowActionRequest request,
        Func<GpuWindowActionPrepared, Task> persistBeforeExecute)
    {
        request.Validate();
        ArgumentNullException.ThrowIfNull(persistBeforeExecute);
        if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("This window action is single-use.");
        GpuWindowActionPrepared? prepared = null;
        GpuWindowCompletion? completed = null;
        GpuWindowRejection? rejection = null;
        var authorized = false;
        string? failure = null;
        GpuWindowActionProcessCleanup cleanup;
        try
        {
            process = WindowsGpuWindowActionProcess.CreateForTarget(request, limits, cancellationToken, deadlineMilliseconds);
            process.StartSuspended(executable, [], desktop);
            var parentHandle = process.TransferParentReadHandle();
            process.Resume();
            await process.ConnectAsync().ConfigureAwait(false);
            await process.WriteFrameAsync(GpuWindowActionProtocol.Parent(parentHandle)).ConfigureAwait(false);
            await process.WriteFrameAsync(GpuWindowActionProtocol.Prepare(request)).ConfigureAwait(false);
            var response = await process.ReadFrameAsync().ConfigureAwait(false);
            if (GpuWindowActionProtocol.ReadKind(response) == GpuWindowActionProtocol.Kind.Rejected)
                rejection = GpuWindowActionProtocol.ReadRejection(response);
            else
            {
                prepared = new(GpuWindowActionProtocol.ReadPreparation(response, request), process.Identity!);
                PersistenceCompletion = persistBeforeExecute(prepared) ?? throw new InvalidOperationException("The owner did not return its persistence task.");
                await PersistenceCompletion.WaitAsync(process.WorkCancellation).ConfigureAwait(false);
                process.WorkCancellation.ThrowIfCancellationRequested();
                // A partially written authorization is not proof that no target effect occurred.
                authorized = true;
                await process.WriteFrameAsync(GpuWindowActionProtocol.Execute()).ConfigureAwait(false);
                response = await process.ReadFrameAsync().ConfigureAwait(false);
                if (GpuWindowActionProtocol.ReadKind(response) == GpuWindowActionProtocol.Kind.Rejected)
                    rejection = GpuWindowActionProtocol.ReadRejection(response);
                else completed = GpuWindowActionProtocol.ReadCompletion(response, request.Method);
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception exception) { failure = exception.ToString(); }
        finally
        {
            cleanup = process is null ? new(false, false, null, false, null, 0, true, null)
                : await process.StopAsync().ConfigureAwait(false);
        }

        CleanupCompletion = process?.CompleteCleanupAsync() ?? Task.FromResult(cleanup);

        var outcome = GpuWindowActionResult.ResolveOutcome(prepared, completed, rejection, authorized, cleanup, failure);
        return new(outcome, prepared, completed, rejection, authorized, PersistenceCompletion is { IsCompleted: false }, cleanup, failure);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.CompareExchange(ref started, 1, 0);
        return process?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
