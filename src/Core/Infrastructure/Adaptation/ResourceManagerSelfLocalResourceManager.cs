using System.ComponentModel;
using System.Diagnostics;
using ResourceManager.Adapter;
using ResourceManager.Adapter.LocalResources;
using ResourceManager.App.Application.DiskUsage;
using ResourceManager.App.Application.Optimization;
using ResourceManager.Ledger;

namespace ResourceManager.App.Infrastructure.Adaptation;

public enum ResourceManagerSelfProcessGroup { Backend, PackageClients }

public sealed record ResourceManagerSelfWorkingSetObservation(
    int ProcessId,
    DateTimeOffset StartedAt,
    ulong BytesBefore,
    ulong? BytesAfter);

/// <summary>OS residency observations and the original batch result; these are not Ledger allocations or releases.</summary>
public sealed record ResourceManagerSelfWorkingSetTrimResult(
    ResourceManagerSelfProcessGroup Group,
    AdapterResourceActionStatus Status,
    IReadOnlyList<ProcessResourcePolicyBatchRequest> Requests,
    IReadOnlyList<ProcessResourcePolicyBatchWriteResult> Results,
    IReadOnlyList<ResourceManagerSelfWorkingSetObservation> Observations,
    bool ObservationPending,
    string? Failure);

public sealed record ResourceManagerSelfLocalResourceTickResult(
    LedgerTickResult Ledger,
    LedgerTableView Memory,
    ulong? BackendWorkingSetBytes,
    ulong? PackageClientWorkingSetBytes,
    IReadOnlyList<ResourceManagerSelfWorkingSetTrimResult> TrimResults,
    IReadOnlyList<ResourceManagerSelfWorkingSetTrimResult> RecheckedTrimObservations,
    int PendingTrimObservationCount)
{
    public IReadOnlyList<ResourceManagerWorkspaceCapacity> Workspaces { get; init; } = [];
}

public sealed class ResourceManagerSelfLocalResourceManager : IDisposable, IWorkspaceCapacityRegistry
{
    private readonly IProcessResourcePolicyWriter policyWriter;
    private readonly IDiskUsageTreeStore diskUsageTrees;
    private readonly ResourceManagerLedgerOwner ledger;
    private readonly Func<IReadOnlyList<ResourceManagerSelfProcessResource>> captureProcesses;
    private readonly Func<int, DateTimeOffset, ulong?> readExactWorkingSet;
    // At most one unobserved batch per process group. Never issue another trim for that
    // group before its exact process instances have been observed or have exited.
    private readonly Dictionary<ResourceManagerSelfProcessGroup, ResourceManagerSelfWorkingSetTrimResult>
        pendingTrimObservations = [];
    private readonly SemaphoreSlim tickGate = new(1, 1);
    private readonly object modeGate = new();
    private LocalResourceSoftwareMemoryMode desiredMemoryMode = LocalResourceSoftwareMemoryMode.Normal;
    private ulong desiredModeGeneration = 1;
    private IReadOnlyList<ResourceManagerSelfWorkingSetTrimResult> lastTrimResults = [];
    private bool closing;
    private bool disposed;

    public ResourceManagerSelfLocalResourceManager(
        IProcessResourcePolicyWriter policyWriter,
        IDiskUsageTreeStore diskUsageTrees,
        ResourceManagerLedgerOwner ledger)
        : this(policyWriter, diskUsageTrees, ledger,
            () => ResourceManagerSelfProcessFamilySampler.Capture(Environment.ProcessId),
            ReadExactWorkingSetBytes)
    {
    }

    internal ResourceManagerSelfLocalResourceManager(
        IProcessResourcePolicyWriter policyWriter,
        IDiskUsageTreeStore diskUsageTrees,
        ResourceManagerLedgerOwner ledger,
        Func<IReadOnlyList<ResourceManagerSelfProcessResource>> captureProcesses,
        Func<int, DateTimeOffset, ulong?> readExactWorkingSet)
    {
        this.policyWriter = policyWriter ?? throw new ArgumentNullException(nameof(policyWriter));
        this.diskUsageTrees = diskUsageTrees ?? throw new ArgumentNullException(nameof(diskUsageTrees));
        this.ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        this.captureProcesses = captureProcesses ?? throw new ArgumentNullException(nameof(captureProcesses));
        this.readExactWorkingSet = readExactWorkingSet ?? throw new ArgumentNullException(nameof(readExactWorkingSet));
    }

    internal IComputeScratch CreateScratch(ulong length)
        => ledger.CreateScratch(length);

    IDisposable IWorkspaceCapacityRegistry.RegisterNativeWorkspace(
        string workArea, Func<NativeWorkspaceCapacityDescription> read)
        => ((IWorkspaceCapacityRegistry)ledger).RegisterNativeWorkspace(workArea, read);

    void IWorkspaceCapacityRegistry.LogNativeGrowth(NativeCore.NativeWorkspaceGrowthEvent growth)
        => ((IWorkspaceCapacityRegistry)ledger).LogNativeGrowth(growth);

    public IReadOnlyList<ResourceManagerWorkspaceCapacity> WorkspaceCapacities => ledger.ReadWorkspaceCapacities();

    public bool IsBackgroundWorkerRunning => false;

    // Retain the OS evidence even when a later Ledger operation throws.
    public IReadOnlyList<ResourceManagerSelfWorkingSetTrimResult> LastTrimResults => Volatile.Read(ref lastTrimResults);

    public void SetDesiredMemoryMode(LocalResourceSoftwareMemoryMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        lock (modeGate)
        {
            ThrowIfUnavailable();
            if (mode == desiredMemoryMode) return;
            var next = unchecked(desiredModeGeneration + 1);
            if (next == 0) throw new InvalidOperationException("Resource Manager self memory mode generation exhausted.");
            desiredMemoryMode = mode;
            desiredModeGeneration = next;
        }
    }

    internal static LedgerTableMode LedgerMode(LocalResourceSoftwareMemoryMode mode) => mode switch
    {
        LocalResourceSoftwareMemoryMode.Unrestricted => LedgerTableMode.Unrestricted,
        LocalResourceSoftwareMemoryMode.Normal => LedgerTableMode.Normal,
        LocalResourceSoftwareMemoryMode.Optimize => LedgerTableMode.Optimize,
        // PagedFrozen explicitly withdraws the cache below. It is not a Ledger force mode.
        LocalResourceSoftwareMemoryMode.PagedFrozen => LedgerTableMode.Normal,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public async Task<ResourceManagerSelfLocalResourceTickResult> TickAsync(
        CancellationToken cancellationToken = default)
    {
        await tickGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LocalResourceSoftwareMemoryMode mode;
            ulong generation;
            lock (modeGate)
            {
                ThrowIfUnavailable();
                mode = desiredMemoryMode;
                generation = desiredModeGeneration;
            }
            Volatile.Write(ref lastTrimResults, Array.Empty<ResourceManagerSelfWorkingSetTrimResult>());
            var rechecked = RecheckPendingTrimObservations();
            // Only PagedFrozen consumes the census to select exact trim targets.
            // Pending readbacks already carry their own exact process identities.
            var observeWorkingSets = mode == LocalResourceSoftwareMemoryMode.PagedFrozen;
            var sample = observeWorkingSets ? captureProcesses() : [];
            var backend = sample.Where(static row => row.IsBackend).ToArray();
            var packageClients = sample.Where(static row => row.IsPackageClient || row.IsPackageClientDescendant).ToArray();
            var trims = new List<ResourceManagerSelfWorkingSetTrimResult>(2);
            cancellationToken.ThrowIfCancellationRequested();
            lock (modeGate)
            {
                // A mode superseded while sampling must not submit its former OS actions.
                if (generation == desiredModeGeneration)
                {
                    ledger.SetMode(LedgerMode(mode));
                    if (mode == LocalResourceSoftwareMemoryMode.PagedFrozen) diskUsageTrees.Clear();
                }
            }
            // Optimize reclaims owned Ledger caches, not the whole processes' OS working sets.
            if (mode == LocalResourceSoftwareMemoryMode.PagedFrozen)
            {
                TrimGroup(ResourceManagerSelfProcessGroup.Backend, backend, generation, trims, cancellationToken);
                TrimGroup(ResourceManagerSelfProcessGroup.PackageClients, packageClients, generation, trims, cancellationToken);
            }
            LedgerTickResult tick;
            lock (modeGate)
            {
                // Publish the latest mode even if it changed after OS sampling.
                ledger.SetMode(LedgerMode(desiredMemoryMode));
                tick = ledger.Tick();
            }
            return new(tick, ledger.ReadTable(), observeWorkingSets ? SumWorkingSetBytes(backend) : null,
                observeWorkingSets ? SumWorkingSetBytes(packageClients) : null,
                trims, rechecked, pendingTrimObservations.Count) { Workspaces = ledger.ReadWorkspaceCapacities() };
        }
        finally { tickGate.Release(); }
    }

    private void TrimGroup(
        ResourceManagerSelfProcessGroup group,
        IReadOnlyList<ResourceManagerSelfProcessResource> targets,
        ulong generation,
        List<ResourceManagerSelfWorkingSetTrimResult> completed,
        CancellationToken cancellationToken)
    {
        if (pendingTrimObservations.ContainsKey(group)) return;
        var requests = new List<ProcessResourcePolicyBatchRequest>(targets.Count);
        var observations = new List<ResourceManagerSelfWorkingSetObservation>(targets.Count);
        foreach (var target in targets)
        {
            if (policyWriter.TryReadProcessStartedAt(target.ProcessId) != target.StartedAt) continue;
            requests.Add(new(target.ProcessId, target.StartedAt, TrimWorkingSet: true));
            observations.Add(new(target.ProcessId, target.StartedAt, target.WorkingSetBytes, null));
        }
        if (requests.Count == 0) return;
        if (cancellationToken.IsCancellationRequested) return;
        IReadOnlyList<ProcessResourcePolicyBatchWriteResult> results = [];
        string? failure = null;
        lock (modeGate)
        {
            if (generation != desiredModeGeneration) return;
            // The exact-instance writer owns OS mutation. No SDK resource is registered here.
            try { results = policyWriter.TryApplyBatch(requests).ToArray(); }
            catch (Exception exception)
            {
                // A writer may throw after submitting some requests. Keep the uncertainty and
                // exact identities instead of calling the entire batch a no-effect result.
                failure = $"{exception.GetType().Name}: {exception.Message}";
            }
        }
        var status = failure is null
            ? ClassifyTrimBatchResults(requests, results)
            : AdapterResourceActionStatus.ResourceBusy;
        var observed = ObserveWorkingSets(observations);
        var pending = observed.Any(static row => !row.BytesAfter.HasValue);
        var result = new ResourceManagerSelfWorkingSetTrimResult(group, status, requests.ToArray(), results,
            observed, pending, failure);
        // Even a mixed/malformed batch with a completed immediate readback is returned intact.
        // An unreadable process needs a later observation; that observation never proves trim causation.
        if (pending) pendingTrimObservations[group] = result;
        completed.Add(result);
        RetainTrimResult(result);
    }

    private IReadOnlyList<ResourceManagerSelfWorkingSetTrimResult> RecheckPendingTrimObservations()
    {
        var rechecked = new List<ResourceManagerSelfWorkingSetTrimResult>(pendingTrimObservations.Count);
        foreach (var pair in pendingTrimObservations.ToArray())
        {
            var observed = ObserveWorkingSets(pair.Value.Observations);
            var result = pair.Value with
            {
                Observations = observed,
                ObservationPending = observed.Any(static row => !row.BytesAfter.HasValue)
            };
            // Publish the completed observation before removing its pending entry:
            // a later sample, cancellation or Ledger failure must not erase it.
            RetainTrimResult(result);
            if (result.ObservationPending) pendingTrimObservations[pair.Key] = result;
            else pendingTrimObservations.Remove(pair.Key);
            rechecked.Add(result);
        }
        return rechecked;
    }

    private void RetainTrimResult(ResourceManagerSelfWorkingSetTrimResult result)
        => Volatile.Write(ref lastTrimResults, lastTrimResults.Append(result).ToArray());
    private ResourceManagerSelfWorkingSetObservation[] ObserveWorkingSets(
        IReadOnlyList<ResourceManagerSelfWorkingSetObservation> observations)
        => observations.Select(row => row.BytesAfter.HasValue ? row : row with
        {
            BytesAfter = readExactWorkingSet(row.ProcessId, row.StartedAt)
        }).ToArray();

    internal static AdapterResourceActionStatus ClassifyTrimBatchResults(
        IReadOnlyList<ProcessResourcePolicyBatchRequest> requests,
        IReadOnlyList<ProcessResourcePolicyBatchWriteResult> results)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(results);
        if (requests.Count == 0 || results.Count != requests.Count) return AdapterResourceActionStatus.ResourceBusy;
        var expected = new HashSet<int>();
        foreach (var request in requests)
            if (request.ProcessId <= 0 || !request.ExpectedStartedAt.HasValue || !request.TrimWorkingSet || !expected.Add(request.ProcessId))
                return AdapterResourceActionStatus.ResourceBusy;
        var represented = new HashSet<int>();
        var succeeded = 0;
        foreach (var result in results)
        {
            if (!expected.Contains(result.ProcessId) || !represented.Add(result.ProcessId)
                || result.Fields.Count != 1
                || result.Fields[0].Field != ProcessResourcePolicyBatchFields.TrimWorkingSet)
                return AdapterResourceActionStatus.ResourceBusy;
            if (result.Fields[0].Succeeded) succeeded++;
        }
        if (represented.Count != expected.Count) return AdapterResourceActionStatus.ResourceBusy;
        return succeeded == requests.Count ? AdapterResourceActionStatus.Completed
            : succeeded == 0 ? AdapterResourceActionStatus.Failed : AdapterResourceActionStatus.ResourceBusy;
    }

    private static ulong? ReadExactWorkingSetBytes(int processId, DateTimeOffset expectedStartedAt)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited || new DateTimeOffset(process.StartTime) != expectedStartedAt) return 0;
            return (ulong)Math.Max(0, process.WorkingSet64);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { return 0; }
        catch (Exception exception) when (exception is NotSupportedException or Win32Exception) { return null; }
    }

    private static ulong SumWorkingSetBytes(IEnumerable<ResourceManagerSelfProcessResource> rows)
    {
        ulong total = 0;
        foreach (var row in rows) total = checked(total + row.WorkingSetBytes);
        return total;
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (closing) throw new InvalidOperationException("Resource Manager self memory owner is closing; new ticks are stopped.");
    }

    public LedgerStatus TryClose()
    {
        tickGate.Wait();
        try
        {
            lock (modeGate)
            {
                if (disposed) return LedgerStatus.Ok;
                closing = true;
            }
            diskUsageTrees.Clear();
            var status = ledger.TryClose();
            if (status is LedgerStatus.Completed or LedgerStatus.Ok)
            {
                lock (modeGate) disposed = true;
            }
            return status;
        }
        finally { tickGate.Release(); }
        // Keep the semaphore alive so existing waiters can observe the terminal state.
    }

    public void Dispose()
    {
        var status = TryClose();
        if (status is not (LedgerStatus.Completed or LedgerStatus.Ok))
            throw new LedgerException(status, "close Resource Manager self memory owner");
    }
}
