using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using System.Runtime.ExceptionServices;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed partial class WindowsGpuPlacementInjector
{
    internal const string StartApiObservationExportName = "ResourceManagerGpuPlacementStartApiObservation";
    internal const string ReadApiObservationExportName = "ResourceManagerGpuPlacementReadApiObservation";
    internal const string StopApiObservationExportName = "ResourceManagerGpuPlacementStopApiObservation";

    internal Task<GpuApiObservationReadResult> ObserveApiOnceAsync(
        GpuPlacementProcessInstance target, int durationMilliseconds,
        Func<GpuRemoteCallExecution, CancellationToken, Task<GpuRemoteCallSnapshot>> execute,
        Func<TimeSpan, CancellationToken, Task> waitAsync, CancellationToken token, CancellationToken cleanupToken)
        => ObserveApiOncePreparedAsync(target, null, durationMilliseconds, execute, waitAsync, token, cleanupToken);

    internal Task<GpuApiObservationReadResult> ObserveApiOnceAsync(
        GpuPlacementProcessInstance target, GpuGraphicsApi candidates, int durationMilliseconds,
        Func<GpuRemoteCallExecution, CancellationToken, Task<GpuRemoteCallSnapshot>> execute,
        Func<TimeSpan, CancellationToken, Task> waitAsync, CancellationToken token, CancellationToken cleanupToken)
        => ObserveApiOncePreparedAsync(target, candidates, durationMilliseconds, execute, waitAsync, token, cleanupToken);

    private async Task<GpuApiObservationReadResult> ObserveApiOncePreparedAsync(
        GpuPlacementProcessInstance target, GpuGraphicsApi? candidates, int durationMilliseconds,
        Func<GpuRemoteCallExecution, CancellationToken, Task<GpuRemoteCallSnapshot>> execute,
        Func<TimeSpan, CancellationToken, Task> waitAsync, CancellationToken token, CancellationToken cleanupToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationMilliseconds);
        ArgumentNullException.ThrowIfNull(waitAsync);
        ArgumentNullException.ThrowIfNull(execute);
        token.ThrowIfCancellationRequested();
        if (candidates is { } mask && (mask == 0 || (mask & ~GpuApiObservationProtocol.AllApis) != 0))
            throw new ArgumentOutOfRangeException(nameof(candidates));
        using var owner = await OpenProviderProcessAsync(target, execute,
            (handle, process, cancellation) => EnsureObservationProviderAsync(handle, process, candidates, cancellation), token).ConfigureAwait(false);
        if (!owner.Result.Success) return new(null, owner.Result.Status, owner.Result.Win32Error);
        return await owner.ObserveApiOnceAsync(durationMilliseconds, waitAsync, token, cleanupToken).ConfigureAwait(false);
    }

    private static async Task<GpuApiObservationReadResult> ObserveApiOnceCoreAsync(
        ProviderProcess owner, int durationMilliseconds, Func<TimeSpan, CancellationToken, Task> waitAsync,
        CancellationToken token, CancellationToken cleanupToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationMilliseconds);
        ArgumentNullException.ThrowIfNull(waitAsync);
        token.ThrowIfCancellationRequested();
        var entries = owner.ApiObservations;
        if (entries.Count == 0) return new(null, "api-observation-not-prepared");
        var startedCount = 0;
        GpuApiObservationReadResult? startFailure = null;
        Exception? actionError = null;
        try
        {
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                var started = await owner.StartApiObservationAsync(entry, durationMilliseconds, token).ConfigureAwait(false);
                if (!started.Completed || started.ExitCode != 1)
                {
                    startFailure = !started.Completed
                        ? new(null, started.Status, started.NativeError)
                        : new(null, "api-observation-start-rejected");
                    break;
                }
                startedCount++;
            }
            if (startFailure is null)
            {
                token.ThrowIfCancellationRequested();
                await waitAsync(TimeSpan.FromMilliseconds(durationMilliseconds), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
        }
        catch (Exception error) { actionError = error; }

        var observed = (GpuGraphicsApi)0;
        GpuApiObservationReadResult? stopFailure = null;
        List<Exception> cleanupErrors = [];
        for (var index = 0; index < startedCount; index++)
        {
            try
            {
                var stopped = await owner.StopApiObservationAsync(entries[index], cleanupToken).ConfigureAwait(false);
                if (stopped.Snapshot is { } snapshot) observed |= snapshot.Apis;
                else
                {
                    stopFailure ??= stopped;
                    cleanupErrors.Add(ObservationFailure(stopped));
                }
            }
            catch (Exception error) { cleanupErrors.Add(error); }
        }
        if (actionError is not null)
        {
            if (cleanupErrors.Count != 0) throw new AggregateException([actionError, .. cleanupErrors]);
            ExceptionDispatchInfo.Capture(actionError).Throw();
        }
        if (startFailure is not null)
        {
            if (cleanupErrors.Count != 0) throw new AggregateException([ObservationFailure(startFailure), .. cleanupErrors]);
            return startFailure;
        }
        if (cleanupErrors.Count == 1 && stopFailure is not null) return stopFailure;
        if (cleanupErrors.Count == 1) ExceptionDispatchInfo.Capture(cleanupErrors[0]).Throw();
        if (cleanupErrors.Count > 1) throw new AggregateException(cleanupErrors);
        return new(new(observed, false), "stopped");
    }

    private static InvalidOperationException ObservationFailure(GpuApiObservationReadResult failure)
        => new($"API observation did not complete: {failure.Status} (native error: {failure.NativeError}).");

    internal Task<ProviderProcess> OpenForApiObservationAsync(
        GpuPlacementProcessInstance target,
        GpuGraphicsApi candidates,
        Func<GpuRemoteCallExecution, CancellationToken, Task<GpuRemoteCallSnapshot>> execute,
        CancellationToken cancellationToken)
    {
        if (candidates == 0 || (candidates & ~GpuApiObservationProtocol.AllApis) != 0)
            throw new ArgumentOutOfRangeException(nameof(candidates));
        return OpenProviderProcessAsync(target, execute,
            (handle, owner, token) => EnsureObservationProviderAsync(handle, owner, candidates, token), cancellationToken);
    }

    private static async Task<GpuApiObservationReadResult> ReadApiObservationCoreAsync(
        ProviderProcess owner, ApiObservationEntries entries, bool stop, CancellationToken token)
    {
        var result = await owner.InvokeAsync(
            stop ? GpuRemoteCallKind.StopApiObservation : GpuRemoteCallKind.ReadApiObservation,
            stop ? entries.Stop : entries.Read, GpuApiObservationProtocol.CreateSnapshotBuffer(), true, token).ConfigureAwait(false);
        if (!result.Completed) return new(null, result.Status, result.NativeError);
        if (result.ExitCode != 1) return new(null, "api-observation-copy-rejected");
        if (!GpuApiObservationProtocol.TryDecode(result.Response ?? [], entries.Apis, out var snapshot)
            || (stop && snapshot.Recording)) return new(null, "api-observation-response-invalid");
        return new(snapshot, stop ? "stopped" : "read");
    }

    internal sealed record ApiObservationEntries(IntPtr Start, IntPtr Read, IntPtr Stop, GpuGraphicsApi Apis);
}

internal sealed record GpuApiObservationReadResult(GpuApiObservationSnapshot? Snapshot, string Status, int? NativeError = null);
