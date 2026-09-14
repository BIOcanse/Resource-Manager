using System.ComponentModel;
using System.Diagnostics;
using ResourceManager.Adapter;
using ResourceManager.Adapter.LocalResources;
using ResourceManager.App.Application.Optimization;

namespace ResourceManager.App.Infrastructure.Adaptation;

public sealed record ResourceManagerSelfLocalResourceTickResult(
    LocalResourceManagerTickResult Manager,
    LocalResourceTableCapacity Capacity,
    int RegisteredResourceCount,
    int PendingUncertainExecutionCount);

public sealed class ResourceManagerSelfLocalResourceManager : IDisposable
{
    private const int SelfMemoryTableCapacity = 8;
    private const uint TrimWorkingSetAction = 1;
    private const uint BackendRecoveryCostCoefficient = 3;
    private const uint NativeUiRecoveryCostCoefficient = 2;

    private static long nextConfigurationGeneration = DateTime.UtcNow.Ticks;
    private static readonly LocalResourceId SelfMemoryTableId = Id("resource-manager:memory:memory-table");
    private static readonly LocalResourceId BackendWorkingSetId =
        Id("resource-manager:memory:backend-working-set");
    private static readonly LocalResourceId NativeUiWorkingSetId =
        Id("resource-manager:memory:native-ui-working-set");

    private readonly IProcessResourcePolicyWriter policyWriter;
    private readonly NativeLocalResourceManagerSession session;
    private readonly DefaultLocalResourceManager manager;
    private readonly LocalResourceTableHandle memoryTable;
    private readonly LocalResourceCapabilityHandle trimCapability;
    private readonly Dictionary<LocalResourceId, LocalResourceHandle> resources = [];
    private readonly List<LocalResourceUncertainExecution> uncertainExecutions = [];
    private readonly SemaphoreSlim tickGate = new(1, 1);
    private readonly object modeGate = new();
    private LocalResourceSoftwareMemoryMode desiredMemoryMode =
        LocalResourceSoftwareMemoryMode.Normal;
    private ulong desiredModeGeneration = 1;
    private SelfProcessFamilySample? activeSample;
    private bool disposed;

    public ResourceManagerSelfLocalResourceManager(
        IProcessResourcePolicyWriter policyWriter)
        : this(policyWriter, CreateGuardedConfiguration())
    {
    }

    internal ResourceManagerSelfLocalResourceManager(
        IProcessResourcePolicyWriter policyWriter,
        LocalResourceManagerConfiguration configuration)
    {
        LocalResourceIntentExecutor.ThrowIfCapabilityHandlerLifecycleEntry();
        this.policyWriter = policyWriter ?? throw new ArgumentNullException(nameof(policyWriter));
        ArgumentNullException.ThrowIfNull(configuration);

        LocalResourceTableHandle configuredMemoryTable = default;
        LocalResourceCapabilityHandle configuredTrimCapability = default;
        manager = DefaultLocalResourceManager.CreateConfigured(
            configuration,
            LocalResourceCapacityStrategy.Concentrated,
            configuredManager =>
            {
                configuredMemoryTable = configuredManager.RegisterTable(new(
                    SelfMemoryTableId,
                    configuration.Generation,
                    LocalResourceDomain.Memory,
                    SelfMemoryTableCapacity));
                configuredTrimCapability = configuredManager.RegisterCapability(
                    configuredMemoryTable,
                    new(
                        CapabilityId: TrimWorkingSetAction,
                        ActionCode: TrimWorkingSetAction,
                        ExpectedEffects: LocalResourceEffects.ChangesSizeBytes,
                        Destructive: false),
                    TrimWorkingSetAsync);
                configuredManager.SetDesiredMemoryMode(new(
                    desiredMemoryMode,
                    MaximumIntentsPerTick(desiredMemoryMode),
                    desiredModeGeneration));
            });
        session = manager.Session;
        memoryTable = configuredMemoryTable;
        trimCapability = configuredTrimCapability;
    }

    public bool IsBackgroundWorkerRunning => false;

    public void SetDesiredMemoryMode(LocalResourceSoftwareMemoryMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        LocalResourceIntentExecutor.ThrowIfCapabilityHandlerModePublication();
        lock (modeGate)
        {
            ThrowIfDisposed();
            if (mode == desiredMemoryMode) return;
            var nextGeneration = unchecked(desiredModeGeneration + 1);
            if (nextGeneration == 0)
            {
                throw new InvalidOperationException(
                    "Resource Manager self memory mode generation exhausted.");
            }
            manager.SetDesiredMemoryMode(new(
                mode,
                MaximumIntentsPerTick(mode),
                nextGeneration));
            desiredModeGeneration = nextGeneration;
            desiredMemoryMode = mode;
        }
    }

    public void SetDesiredMode(LocalResourceCleanupMode mode)
        => SetDesiredMemoryMode(mode switch
        {
            LocalResourceCleanupMode.Unrestricted =>
                LocalResourceSoftwareMemoryMode.Unrestricted,
            LocalResourceCleanupMode.Normal => LocalResourceSoftwareMemoryMode.Normal,
            LocalResourceCleanupMode.Optimize => LocalResourceSoftwareMemoryMode.Optimize,
            LocalResourceCleanupMode.ReleaseAll => LocalResourceSoftwareMemoryMode.PagedFrozen,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        });

    public async Task<ResourceManagerSelfLocalResourceTickResult> TickAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfLifecycleReentry();
        await tickGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var sample = CaptureProcessFamilySample();
            activeSample = sample;
            try
            {
                SynchronizeResources(sample);
                LocalResourceManagerTickResult result;
                try
                {
                    result = await manager.TickAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (LocalResourceManagerTickFailedException exception)
                {
                    RetainUncertainExecutions(exception.PartialResult.UncertainExecutions);
                    throw;
                }
                RetainUncertainExecutions(result.UncertainExecutions);
                if (result.CancellationObserved)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return new(
                    result,
                    session.ReadCapacity(memoryTable),
                    resources.Count,
                    uncertainExecutions.Count);
            }
            finally
            {
                activeSample = null;
            }
        }
        finally
        {
            tickGate.Release();
        }
    }

    public LocalResourceExecutionResult Reconcile(
        LocalResourceUncertainExecution execution,
        LocalResourceEffect confirmedEffect)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ThrowIfLifecycleReentry();
        tickGate.Wait();
        try
        {
            ThrowIfDisposed();
            var result = manager.Reconcile(execution, confirmedEffect);
            if (execution.IsResolved)
            {
                uncertainExecutions.Remove(execution);
            }
            return result;
        }
        finally
        {
            tickGate.Release();
        }
    }

    public IReadOnlyList<LocalResourceUncertainExecution> GetPendingUncertainExecutions()
    {
        ThrowIfLifecycleReentry();
        tickGate.Wait();
        try
        {
            ThrowIfDisposed();
            RetainUncertainExecutions(manager.GetRecoveryRequiredExecutions());
            RemoveResolvedUncertainExecutions();
            return uncertainExecutions.ToArray();
        }
        finally
        {
            tickGate.Release();
        }
    }

    private SelfProcessFamilySample CaptureProcessFamilySample()
    {
        var rows = ResourceManagerSelfProcessFamilySampler.Capture(Environment.ProcessId);
        var facts = rows
            .Select(row => new SelfProcessFact(
                row,
                policyWriter.TryReadProcessStartedAt(row.ProcessId)))
            .ToArray();
        return new(
            facts.Where(static fact => fact.Resource.IsBackend).ToArray(),
            facts.Where(static fact =>
                fact.Resource.IsNativeUi || fact.Resource.IsNativeUiDescendant).ToArray());
    }

    private void SynchronizeResources(SelfProcessFamilySample sample)
    {
        SynchronizeResource(
            BackendWorkingSetId,
            sample.Backend,
            BackendRecoveryCostCoefficient);
        SynchronizeResource(
            NativeUiWorkingSetId,
            sample.NativeUi,
            NativeUiRecoveryCostCoefficient);
    }

    private void SynchronizeResource(
        LocalResourceId resourceId,
        IReadOnlyList<SelfProcessFact> rows,
        uint recoveryCost)
    {
        if (rows.Count == 0)
        {
            if (resources.TryGetValue(resourceId, out var stale))
            {
                manager.UnregisterResource(stale);
                resources.Remove(resourceId);
            }
            return;
        }

        var definition = new LocalResourceDefinition(
            resourceId,
            SumWorkingSetBytes(rows.Select(static fact => fact.Resource)),
            recoveryCost,
            LocalResourceRecoverability.Recoverable,
            LocalResourceAccessLossImpact.UnobservableNow);
        if (resources.TryGetValue(resourceId, out var existing))
        {
            var current = session.ReadResource(existing);
            if (!MatchesDefinition(current, definition))
            {
                manager.UpdateResource(existing, definition);
            }
        }
        else
        {
            resources.Add(resourceId, manager.RegisterResource(memoryTable, definition));
        }

    }

    private static bool MatchesDefinition(
        LocalResourceSnapshot current,
        LocalResourceDefinition expected)
        => current.SizeBytes == expected.SizeBytes
            && current.RecoveryCostCoefficient == expected.RecoveryCostCoefficient
            && current.Recoverability == expected.Recoverability
            && current.AccessLossImpact == expected.AccessLossImpact;

    private ValueTask<LocalResourceEffect> TrimWorkingSetAsync(
        LocalResourceExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sample = activeSample ?? throw new InvalidOperationException(
            "A self local resource action requires the active Tick sample.");
        var targets = context.ResourceUid == BackendWorkingSetId
            ? sample.Backend
            : context.ResourceUid == NativeUiWorkingSetId
                ? sample.NativeUi
                : [];
        var requests = new List<ProcessResourcePolicyBatchRequest>(targets.Count);
        foreach (var target in targets)
        {
            if (target.StartedAt.HasValue
                && policyWriter.TryReadProcessStartedAt(target.Resource.ProcessId)
                    == target.StartedAt)
            {
                requests.Add(new(
                    target.Resource.ProcessId,
                    target.StartedAt,
                    TrimWorkingSet: true));
            }
        }
        if (requests.Count == 0)
        {
            return ValueTask.FromResult(LocalResourceEffect.NoEffect);
        }

        var results = policyWriter.TryApplyBatch(requests);
        var status = ClassifyTrimBatchResults(
            requests,
            results);
        if (status == AdapterResourceActionStatus.ResourceBusy)
        {
            return ValueTask.FromResult(LocalResourceEffect.Unknown);
        }
        if (status != AdapterResourceActionStatus.Completed)
        {
            return ValueTask.FromResult(LocalResourceEffect.NoEffect);
        }

        var sizeAfter = ReadWorkingSetBytesAfter(targets, requests);
        sizeAfter = Math.Min(context.SizeBytes, sizeAfter);
        if (sizeAfter == context.SizeBytes)
        {
            return ValueTask.FromResult(LocalResourceEffect.NoEffect);
        }
        return ValueTask.FromResult(LocalResourceEffect.Applied(
            LocalResourceEffects.ChangesSizeBytes,
            sizeAfter,
            context.SizeBytes - sizeAfter));
    }

    internal static AdapterResourceActionStatus ClassifyTrimBatchResults(
        IReadOnlyList<ProcessResourcePolicyBatchRequest> requests,
        IReadOnlyList<ProcessResourcePolicyBatchWriteResult> results)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(results);
        if (requests.Count == 0 || results.Count != requests.Count)
        {
            return AdapterResourceActionStatus.ResourceBusy;
        }
        var expected = new HashSet<int>();
        foreach (var request in requests)
        {
            if (request.ProcessId <= 0
                || !request.TrimWorkingSet
                || !expected.Add(request.ProcessId))
            {
                return AdapterResourceActionStatus.ResourceBusy;
            }
        }
        var represented = new HashSet<int>();
        var succeeded = 0;
        foreach (var result in results)
        {
            if (!expected.Contains(result.ProcessId)
                || !represented.Add(result.ProcessId)
                || result.Fields.Count != 1
                || result.Fields[0].Field !=
                    ProcessResourcePolicyBatchFields.TrimWorkingSet)
            {
                return AdapterResourceActionStatus.ResourceBusy;
            }
            if (result.Fields[0].Succeeded)
            {
                succeeded++;
            }
        }
        if (represented.Count != expected.Count)
        {
            return AdapterResourceActionStatus.ResourceBusy;
        }
        return succeeded == requests.Count
            ? AdapterResourceActionStatus.Completed
            : succeeded == 0
                ? AdapterResourceActionStatus.Failed
                : AdapterResourceActionStatus.ResourceBusy;
    }

    private static ulong ReadWorkingSetBytesAfter(
        IReadOnlyList<SelfProcessFact> targets,
        IReadOnlyList<ProcessResourcePolicyBatchRequest> requests)
    {
        var requestedProcessIds = requests
            .Select(static request => request.ProcessId)
            .ToHashSet();
        ulong total = 0;
        foreach (var target in targets)
        {
            var bytes = target.Resource.WorkingSetBytes;
            if (requestedProcessIds.Contains(target.Resource.ProcessId)
                && target.StartedAt.HasValue
                && TryReadExactWorkingSetBytes(
                    target.Resource.ProcessId,
                    target.StartedAt.Value,
                    out var refreshedBytes))
            {
                bytes = refreshedBytes;
            }
            total = checked(total + bytes);
        }
        return total;
    }

    private static bool TryReadExactWorkingSetBytes(
        int processId,
        DateTimeOffset expectedStartedAt,
        out ulong workingSetBytes)
    {
        workingSetBytes = 0;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited
                || new DateTimeOffset(process.StartTime) != expectedStartedAt)
            {
                return true;
            }
            workingSetBytes = (ulong)Math.Max(0, process.WorkingSet64);
            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Exception exception)
            when (exception is NotSupportedException or Win32Exception)
        {
            return false;
        }
    }

    private static LocalResourceManagerConfiguration CreateGuardedConfiguration()
    {
        LocalResourceIntentExecutor.ThrowIfCapabilityHandlerLifecycleEntry();
        return CreateConfiguration();
    }

    private static LocalResourceManagerConfiguration CreateConfiguration()
    {
        var generation = unchecked((ulong)Interlocked.Increment(
            ref nextConfigurationGeneration));
        if (generation == 0)
        {
            throw new InvalidOperationException(
                "Resource Manager self local resource generation exhausted.");
        }
        return LocalResourceManagerConfiguration.CreateDefault(
            tableCapacity: 1,
            resourceCapacity: SelfMemoryTableCapacity,
            generation);
    }

    private static int MaximumIntentsPerTick(LocalResourceSoftwareMemoryMode mode)
        => mode switch
        {
            LocalResourceSoftwareMemoryMode.Unrestricted or
                LocalResourceSoftwareMemoryMode.Normal => 0,
            LocalResourceSoftwareMemoryMode.Optimize => 1,
            LocalResourceSoftwareMemoryMode.PagedFrozen => SelfMemoryTableCapacity,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

    private static LocalResourceId Id(string value)
    {
        var low = AdapterResourceKey.FromString(value);
        return low != 0
            ? new LocalResourceId(low)
            : throw new InvalidOperationException("A self local resource identifier is zero.");
    }

    private static ulong SumWorkingSetBytes(
        IEnumerable<ResourceManagerSelfProcessResource> rows)
    {
        ulong total = 0;
        foreach (var row in rows)
        {
            total = checked(total + row.WorkingSetBytes);
        }
        return total;
    }

    private sealed record SelfProcessFact(
        ResourceManagerSelfProcessResource Resource,
        DateTimeOffset? StartedAt);

    private sealed record SelfProcessFamilySample(
        IReadOnlyList<SelfProcessFact> Backend,
        IReadOnlyList<SelfProcessFact> NativeUi);

    private void RetainUncertainExecutions(
        IReadOnlyList<LocalResourceUncertainExecution> executions)
    {
        RemoveResolvedUncertainExecutions();
        foreach (var execution in executions)
        {
            if (!execution.IsResolved && !uncertainExecutions.Contains(execution))
            {
                uncertainExecutions.Add(execution);
            }
        }
    }

    private void RemoveResolvedUncertainExecutions()
        => uncertainExecutions.RemoveAll(static execution => execution.IsResolved);

    private static void ThrowIfLifecycleReentry()
        => LocalResourceIntentExecutor.ThrowIfCapabilityHandlerLifecycleEntry();

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    public LocalResourceManagerCloseResult TryClose()
        => TryClose(out _);

    private LocalResourceManagerCloseResult TryClose(out int pendingExecutionCount)
    {
        ThrowIfLifecycleReentry();
        tickGate.Wait();
        try
        {
            lock (modeGate)
            {
                if (disposed)
                {
                    pendingExecutionCount = 0;
                    return LocalResourceManagerCloseResult.Closed;
                }
                RemoveResolvedUncertainExecutions();
                var closeResult = manager.TryClose();
                if (closeResult == LocalResourceManagerCloseResult.RecoveryRequired)
                {
                    pendingExecutionCount = manager.PendingExecutionCount;
                    return closeResult;
                }
                disposed = true;
                resources.Clear();
                uncertainExecutions.Clear();
                pendingExecutionCount = 0;
                return LocalResourceManagerCloseResult.Closed;
            }
        }
        finally
        {
            tickGate.Release();
        }

        // A waiter can already hold a reference to the semaphore while Dispose
        // is waiting. Keep the gate alive so that waiter can observe disposed.
    }

    public void Dispose()
    {
        if (TryClose(out var pendingExecutionCount) ==
            LocalResourceManagerCloseResult.RecoveryRequired)
        {
            throw new LocalResourceRecoveryRequiredException(pendingExecutionCount);
        }
    }
}
