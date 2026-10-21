using ResourceManager.ReadWrite.ProcessFields;
using System.Collections.Frozen;
using ResourceManager.ReadWrite.Windows.Processes;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using ResourceManager.Adapter;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Application.ResourceTable;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.Infrastructure.Adaptation;

namespace ResourceManager.App.Infrastructure.CpuTopology;

#pragma warning disable CS0618 // Exact integer ETW QPC boundaries are required; relative milliseconds lose precision.

internal sealed record EtwCpuCoreResidencyDiagnostics(
    string State,
    string Message,
    long BindingEpoch,
    long? SessionGeneration,
    CompiledCpuCoreResidencyPlan? SessionPlan,
    CpuResidencyAggregationDiagnostics? Aggregation);

public sealed partial class EtwCpuCoreResidencyReader(
    IKernelEtwSessionBroker kernelEtwBroker,
    IRuntimePlanProvider runtimePlanProvider,
    HostManagerSamplingSubscriptionOwner subscriptionOwner,
    ILogger<EtwCpuCoreResidencyReader> logger) : BackgroundService, ICpuCoreResidencyReader, IResourceTableProviderStateSource, IResourceManagerSelfComputeZone
{
    private static readonly ulong ZoneObjectKey = AdapterResourceKey.FromString($"resource-manager:zone:{MonitoringSourceZoneIds.EtwCpuCoreResidency}");
    private const string ResidencyItemId = "cpu-residency";
    private readonly object subscriptionGate = new();
    private readonly object resourceDisposeGate = new();
    private readonly SemaphoreSlim demandSignal = new(0, 1);
    private readonly CurrentValuePublicationSignal publicationSignal = new();
    private readonly NativeItemSamplingSubscriptionTracker<CpuResidencySampleRequest> subscriptions =
        CreateSubscriptions(subscriptionOwner, null);
    private EtwCpuResidencyCaptureOwner? captureOwner;
    private CompiledCpuCoreResidencyPlan? sessionPlan;
    private long nextLeaseId;
    private bool stopped;
    private bool ownedResourcesDisposed;
    private volatile ResourceManagerComputeZoneMode currentComputeMode = ResourceManagerComputeZoneMode.Normal;
    private EtwCpuResidencyCaptureOwner Capture { get { lock (subscriptionGate) return captureOwner ??= CreateCapture(); } }
    private string state => Capture.State;
    private string message => Capture.Message;
    private EtwCpuResidencyCaptureOwner CreateCapture()
        => new(kernelEtwBroker, () =>
        {
            sessionPlan = runtimePlanProvider.Current.HostManager.RequirePublished().CpuCoreResidency.RequirePublished();
            return MapPlan(sessionPlan);
        }, new KernelEtwDiagnostics(item => logger.LogWarning(item.Exception, "{Message}", item.MessageTemplate)), seedProcessInventory, () => publicationSignal.Publish());
    private readonly Action<CpuResidencyAggregationBuffer.InventorySeed, Func<bool>> seedProcessInventory = EtwCpuResidencyCaptureOwner.SeedInventory;

    private static NativeItemSamplingSubscriptionTracker<CpuResidencySampleRequest> CreateSubscriptions(
        HostManagerSamplingSubscriptionOwner owner,
        Func<ulong, uint, ResourceManager.Subscription.INativeSamplingOutputScratch>? createOutputScratch)
        => new(owner, 6, static request => request.SourceKey, static _ => [ResidencyItemId],
            static (items, _) => items.Contains(ResidencyItemId, StringComparer.OrdinalIgnoreCase)
                ? new CpuResidencySampleRequest("provider") : null,
            coalesceItemsIntoLatestCapture: true, createOutputScratch);

    internal EtwCpuCoreResidencyReader(
        IKernelEtwSessionBroker kernelEtwBroker,
        IRuntimePlanProvider runtimePlanProvider,
        HostManagerSamplingSubscriptionOwner subscriptionOwner,
        ILogger<EtwCpuCoreResidencyReader> logger,
        ResourceManagerLedgerOwner ledgerOwner)
        : this(kernelEtwBroker, runtimePlanProvider, subscriptionOwner, logger)
    {
        ArgumentNullException.ThrowIfNull(ledgerOwner);
        subscriptions = CreateSubscriptions(subscriptionOwner,
            (length, alignment) => SamplingOutputScratch.Create(ledgerOwner.CreateScratch, length, alignment));
    }
    internal EtwCpuCoreResidencyReader(
        IKernelEtwSessionBroker kernelEtwBroker,
        IRuntimePlanProvider runtimePlanProvider,
        HostManagerSamplingSubscriptionOwner subscriptionOwner,
        ILogger<EtwCpuCoreResidencyReader> logger,
        Action<CpuResidencyAggregationBuffer.InventorySeed, Func<bool>> seedProcessInventory)
        : this(
            kernelEtwBroker,
            runtimePlanProvider,
            subscriptionOwner,
            logger)
    {
        this.seedProcessInventory = seedProcessInventory
            ?? throw new ArgumentNullException(nameof(seedProcessInventory));
    }

    public ulong ZoneKey => ZoneObjectKey;
    public string DisplayName => MonitoringSourceZoneIds.EtwCpuCoreResidency;
    public ResourceManagerComputeZoneMode CurrentMode => currentComputeMode;

    public CpuCoreResidencySnapshot? Read() => Capture.Read();
    internal long CanonicalRevision => Capture.Revision;
    internal ProcessMetricFieldSnapshot ReadCanonical() => ReadCanonical(Read());
    internal ProcessMetricFieldSnapshot ReadCanonical(CpuCoreResidencySnapshot? snapshot)
    {
        var inventory = new ProcessMetricFieldSnapshot(snapshot?.MeasuredThrough, null, [],
            snapshot?.Processes.Select(process => new ProcessMetricFieldValues(process.ProcessId, process.ProcessStartKey, []))
                .DistinctBy(row => (row.ProcessId, row.CreationWindowsFileTime100Ns)).ToArray() ?? []);
        return WindowsCpuResidencyProcessFields.Project(["process_all_cpu_residency_attributed"], inventory, Capture.ReadScopes(), snapshot);
    }
    internal async Task<ProcessMetricFieldSnapshot> CaptureCanonicalAsync(IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var publication = publicationSignal.Subscribe();
        using var demand = AcquireSubscription("canonical.direct", TimeSpan.FromSeconds(1));
        while (true)
        {
            await publication.WaitAsync(cancellationToken);
            var snapshot = Read();
            // Keep this direct demand alive while the ORIGINAL worker forms its closed window.
            // This only awaits publication; it adds no timer, capture loop or baseline.
            if (snapshot is not null || !Capture.State.Equals("Warming", StringComparison.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ReadCanonical(snapshot).Select(names.ToHashSet(StringComparer.OrdinalIgnoreCase));
            }
        }
    }
    internal EtwCpuCoreResidencyDiagnostics GetDiagnostics()
    {
        var value = Capture.GetDiagnostics();
        return new(value.State, value.Message, value.BindingEpoch, value.SessionGeneration, sessionPlan, value.Aggregation);
    }
    public IDisposable AcquireSubscription(string subscriptionId, TimeSpan refreshInterval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        var leaseId = Interlocked.Increment(ref nextLeaseId);
        var sourceKey = $"lease:{leaseId}:{subscriptionId.Trim()}";
        lock (subscriptionGate)
        {
            if (stopped)
            {
                throw new InvalidOperationException("The CPU-residency reader has stopped.");
            }

            subscriptions.TrackPersistent(
                new CpuResidencySampleRequest(sourceKey),
                DateTimeOffset.UtcNow,
                refreshInterval);
        }

        SignalDemand();
        return new ResidencySubscriptionLease(this, sourceKey);
    }

    public async IAsyncEnumerable<CpuCoreResidencySnapshot?> SubscribeAsync(
        string subscriptionId,
        TimeSpan interval,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var publication = publicationSignal.Subscribe();
        using var lease = AcquireSubscription(subscriptionId, interval);
        while (!cancellationToken.IsCancellationRequested)
        {
            await publication.WaitAsync(cancellationToken);
            yield return Read();
        }
    }

    public IReadOnlyList<ResourceTableProviderState> GetStates(IReadOnlyList<ResourceTableColumn> columns)
    {
        if (!columns.Any(static column => column.Id.Equals(ResourceTableColumnIds.Cpu, StringComparison.OrdinalIgnoreCase)))
        {
            return [];
        }

        var currentState = state;
        if (currentState.Equals("Running", StringComparison.OrdinalIgnoreCase)
            || currentState.Equals("Warming", StringComparison.OrdinalIgnoreCase)
            || currentState.Equals("NotRequested", StringComparison.OrdinalIgnoreCase)
            || currentState.Equals("Idle", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return
        [
            new ResourceTableProviderState(
                "etw-context-switch-cpu",
                currentState,
                message)
        ];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var worker = new NativeSamplingSubscriptionWorker<CpuResidencySampleRequest, SamplingOwnerToken>(
            subscriptions, demandSignal,
            items =>
            {
                if ((items.Count == 0
                    || !subscriptions.HasPersistentDemand(static request => !request.CancellationToken.IsCancellationRequested))
                    && HasSessionLease)
                    StopSession("ETW CPU 执行时长归因已因无订阅而停止。");
            },
            static failure => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure.Error).Throw(),
            TimeSpan.Zero);
        try
        {
            await worker.RunAsync((plan, token) =>
            {
                if (!subscriptions.HasPersistentDemand(static request => !request.CancellationToken.IsCancellationRequested))
                    return Task.FromResult(SubscriptionSampleCompletion.Skipped);
                EnsureStarted();
                var sampled = Capture.Collect(plan.OwnerToken, (_, sourceQuerySucceeded) =>
                {
                    IReadOnlySet<string> queried = sourceQuerySucceeded
                        ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "process.cpu.residency" }
                        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    pendingFieldCollection = fieldDeliveries.CaptureCompleted<CpuResidencySampleRequest>(
                        plan.Triggers, queried, committed: true);
                });
                return Task.FromResult(token.IsCancellationRequested || Volatile.Read(ref stopped)
                    || !plan.OwnerToken.IsActive ? SubscriptionSampleCompletion.Skipped
                    : sampled ? SubscriptionSampleCompletion.Sampled : SubscriptionSampleCompletion.Failed);
            }, stoppingToken, planCreated: plan =>
            {
                lock (subscriptionGate)
                    if (stopped) throw new OperationCanceledException(stoppingToken);
                pendingFieldCollection = null;
                if (plan.Request is null)
                    fieldDeliveries.Deliver<CpuResidencySampleRequest>(plan.Triggers, null, freshOnly: false);
            }, collectionCompleted: (plan, completion) =>
            {
                var result = pendingFieldCollection;
                pendingFieldCollection = null;
                if (completion == SubscriptionSampleCompletion.Canceled) return;
                fieldDeliveries.Deliver<CpuResidencySampleRequest>(plan.Triggers, result,
                    freshOnly: plan.Request is null ? true : null);
            });
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || Volatile.Read(ref stopped)) { }
        finally
        {
            try { StopSession("ETW CPU 执行时长归因正在随程序停止。"); }
            finally
            {
                fieldDeliveries.RetireAll();
                lock (subscriptionGate) subscriptions.Clear(DateTimeOffset.UtcNow);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        BeginStop();
        await base.StopAsync(cancellationToken);
        if (ExecuteTask is null || ExecuteTask.IsCompleted)
            captureOwner?.Dispose();
    }

    public override void Dispose()
    {
        BeginStop();
        try
        {
            base.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (ExecuteTask?.IsCompleted == true)
        {
        }
        catch (Exception error)
        {
            logger.LogError(error, "ETW CPU-residency worker failed while draining for disposal.");
        }
        lock (resourceDisposeGate)
        {
            if (ownedResourcesDisposed) return;
            if (ExecuteTask is null || ExecuteTask.IsCompleted)
                subscriptions.Clear(DateTimeOffset.UtcNow);
            subscriptions.CloseOutputScratch();
            captureOwner?.Dispose();
            demandSignal.Dispose();
            base.Dispose();
            ownedResourcesDisposed = true;
        }
    }



    private void ReleaseLease(string sourceKey)
    {
        lock (subscriptionGate)
        {
            if (stopped)
            {
                return;
            }

            subscriptions.Remove(sourceKey, DateTimeOffset.UtcNow);
        }
        SignalDemand();
    }

    public void ApplyMode(ResourceManagerComputeZoneMode mode)
    {
        lock (subscriptionGate)
        {
            if (stopped) return; currentComputeMode = mode;
            Capture.SetCaptureEnabled(mode != ResourceManagerComputeZoneMode.Freeze, "ETW CPU 执行时长归因已按功能区冻结停止。");
        }
        if (mode != ResourceManagerComputeZoneMode.Freeze) SignalDemand();
    }
    private bool HasSessionLease => Capture.HasSessionLease;
    private void EnsureStarted() { if (currentComputeMode != ResourceManagerComputeZoneMode.Freeze) Capture.EnsureStarted(); }
    private void StopSession(string idleMessage) => Capture.Stop(idleMessage);
    private void BeginStop()
    {
        var retireDeliveries = false;
        lock (subscriptionGate)
        {
            if (stopped) return; stopped = true; retireDeliveries = true;
            if (ExecuteTask is null || ExecuteTask.IsCompleted) subscriptions.Clear(DateTimeOffset.UtcNow);
        }
        if (retireDeliveries) fieldDeliveries.RetireAll();
        SignalDemand();
    }
    private static CpuResidencyCaptureConfiguration MapPlan(CompiledCpuCoreResidencyPlan plan)
        => new(plan.ObservationWindowMilliseconds, plan.ExecutionTimeSource.MaximumClosedSlices,
            plan.PhysicalCoreByLogicalProcessor.ToFrozenDictionary(pair => pair.Key, pair => new CpuResidencyCoreAccounting(
                pair.Value.PhysicalCoreId, pair.Value.CcdId, pair.Value.LogicalProcessorCount, pair.Value.LogicalProcessorShare)),
            "Host published typed core/CCD accounting plan");
    internal static CpuResidencyAggregationBuffer CreateAggregationBuffer(CompiledCpuCoreResidencyPlan plan, long qpcFrequency)
        => EtwCpuResidencyCaptureOwner.CreateAggregationBuffer(MapPlan(plan.RequirePublished()), qpcFrequency);
    internal static void BindKernelEventCallbacks(KernelEtwEventSource source, CpuResidencyAggregationBuffer buffer,
        Func<bool> observe, Func<int, long?> birth, Func<int, int?>? map = null)
        => EtwCpuResidencyCaptureOwner.BindKernelEventCallbacks(source, buffer, observe, birth, map);
    internal static int? ResolveEventLogicalProcessorId(int index, IReadOnlyList<int> ids)
        => EtwCpuResidencyCaptureOwner.ResolveEventLogicalProcessorId(index, ids);
    internal static IReadOnlyList<CpuProcessCoreResidency> BuildProcessSnapshot(CompiledCpuCoreResidencyPlan plan,
        long generation, CpuResidencyAggregationSnapshot aggregate)
        => EtwCpuResidencyCaptureOwner.BuildProcessSnapshot(MapPlan(plan), generation, aggregate);
    private void SignalDemand()
    {
        if (demandSignal.CurrentCount != 0)
        {
            return;
        }

        try
        {
            demandSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private sealed class ResidencySubscriptionLease(
        EtwCpuCoreResidencyReader owner,
        string sourceKey) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.ReleaseLease(sourceKey);
            }
        }
    }

    private sealed record CpuResidencySampleRequest(string SourceKey, CancellationToken CancellationToken = default);
}
#pragma warning restore CS0618
