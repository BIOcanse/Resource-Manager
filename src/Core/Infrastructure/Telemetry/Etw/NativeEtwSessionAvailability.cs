using System.ComponentModel;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.StateItems;
using ResourceManager.ReadWrite.Windows.Processes.Etw;
using ResourceManager.Shared.BusinessBlocks.State;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public enum NativeEtwSessionUnavailableCause { ReleaseUnconfirmed, NoPermission, Capacity }

public sealed record NativeEtwSessionFailure(NativeEtwSessionUnavailableCause Cause,
    Exception Exception, IReadOnlyList<Guid> Providers);

/// <summary>Process-lifetime owner of native session availability, independent of producer runs.</summary>
public sealed class NativeEtwSessionAvailability
{
    private readonly object gate = new();
    private readonly Dictionary<int, Entry> entries;

    public NativeEtwSessionAvailability(StateAuthority authority)
    {
        entries = new()
        {
            [BlockUids.PresentFrameEtwSource] = Create(StateItemUids.PresentNativeSessionAvailable),
            [BlockUids.GpuAllocationCollector] = Create(StateItemUids.GpuAllocationNativeSessionAvailable),
            [BlockUids.DxgkrnlVidMmEtwTelemetryZone] = Create(StateItemUids.VidMmNativeSessionAvailable)
        };
        Entry Create(int uid)
        {
            var cell = authority.ClaimCell<bool>(uid);
            _ = cell.PublishAsync(true, Reason("native-session-initial"));
            return new(cell);
        }
    }

    public void Report(int blockUid, NativeEtwSessionUnavailableCause cause, Exception exception,
        IReadOnlyList<Guid>? providers = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (cause == NativeEtwSessionUnavailableCause.Capacity && providers is not { Count: > 0 })
            throw new ArgumentException("Capacity failures require the block's complete provider set.", nameof(providers));
        lock (gate)
        {
            if (!entries.TryGetValue(blockUid, out var entry)) throw new ArgumentOutOfRangeException(nameof(blockUid));
            // Permissions cannot recover during this process, even if later cleanup also fails.
            if (entry.Failure?.Cause == NativeEtwSessionUnavailableCause.NoPermission) return;
            if (entry.Failure?.Cause == NativeEtwSessionUnavailableCause.ReleaseUnconfirmed
                && cause == NativeEtwSessionUnavailableCause.Capacity) return;
            entry.Failure = new(cause, exception, Array.AsReadOnly(providers?.Distinct().ToArray() ?? []));
            // Publication commits before returning. Awaiting reader alignment here could
            // wait for the reporting producer's own run to finish its cleanup.
            _ = entry.Cell.PublishAsync(false, Reason("native-session-" + cause));
        }
    }

    public NativeEtwSessionFailure? ReadFailure(int blockUid)
    {
        lock (gate) return entries.TryGetValue(blockUid, out var entry)
            ? entry.Failure : throw new ArgumentOutOfRangeException(nameof(blockUid));
    }

    internal void ReportFailure(int blockUid, Exception exception, IReadOnlyList<Guid> providers)
    {
        var cause = Classify(exception);
        if (cause is { } value) Report(blockUid, value, exception,
            value == NativeEtwSessionUnavailableCause.Capacity ? providers : null);
    }

    internal static NativeEtwSessionUnavailableCause? Classify(Exception exception)
    {
        var pending = new Stack<Exception>();
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(exception);
        var release = false;
        var capacity = false;
        while (pending.Count > 0 && seen.Count < 256)
        {
            var error = pending.Pop();
            if (!seen.Add(error)) continue;
            if (error is UnauthorizedAccessException || error.HResult == unchecked((int)0x80070005)
                || error is Win32Exception { NativeErrorCode: 5 }) return NativeEtwSessionUnavailableCause.NoPermission;
            release |= error is KernelEtwSessionCreationCleanupException;
            capacity |= error.HResult == unchecked((int)0x800705AA)
                || error is Win32Exception { NativeErrorCode: 1450 };
            if (error is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
            else if (error.InnerException is { } inner) pending.Push(inner);
        }
        return release ? NativeEtwSessionUnavailableCause.ReleaseUnconfirmed
            : capacity ? NativeEtwSessionUnavailableCause.Capacity : null;
    }

    private static BusinessBlockReason Reason(string cause)
        => new(BusinessBlockRequestKind.Dependency, nameof(NativeEtwSessionAvailability), cause, 0);
    private sealed class Entry(StateCell<bool> cell)
    {
        internal StateCell<bool> Cell { get; } = cell;
        internal NativeEtwSessionFailure? Failure;
    }
}
