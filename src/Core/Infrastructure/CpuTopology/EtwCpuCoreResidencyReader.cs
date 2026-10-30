using ResourceManager.ReadWrite.ProcessFields;
using System.Collections.Frozen;
using ResourceManager.ReadWrite.Windows.Processes;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.ResourceTable;
using ResourceManager.App.Application.RuntimeSpecialization;
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
    ILogger<EtwCpuCoreResidencyReader> logger) : BackgroundService, ICpuCoreResidencyReader, IResourceTableProviderStateSource
{
    private const string ResidencyItemId = "cpu-residency";
    private readonly object subscriptionGate = new();
    private readonly object resourceDisposeGate = new();
    private readonly SemaphoreSlim demandSignal = new(0, 1);
    private readonly CurrentValuePublicationSignal publicationSignal = new();
    private readonly NativeItemSamplingSubscriptionTracker<CpuResidencySampleRequest> subscriptions =
        CreateSubscriptions(subscriptionOwner, null);
    private EtwCpuResidencyCaptureOwner? captureOwner;
    private readonly Func<Func<Func<int, CaptureBucketMemory>, Task>, Task> runBucketStorage = RunManagedBucketStorageAsync;
    private CompiledCpuCoreResidencyPlan? sessionPlan;
    private long nextLeaseId;
    private bool stopped;
    private bool ownedResourcesDisposed;
    private EtwCpuResidencyCaptureOwner Capture { get { lock (subscriptionGate) return captureOwner ??= CreateCapture(); } }
    private string state => Capture.State;
    private string message => Capture.Message;
    private EtwCpuResidencyCaptureOwner CreateCapture()
        => new(kernelEtwBroker, () =>
        {
            sessionPlan = runtimePlanProvider.Current.HostManager.RequirePublished().CpuCoreResidency.RequirePublished();
            return MapPlan(sessionPlan);
        }, new KernelEtwDiagnostics(item => logger.LogWarning(item.Exception, "{Message}", item.MessageTemplate)), seedProcessInventory, () => publicationSignal.Publish());

    private static Task RunManagedBucketStorageAsync(Func<Func<int, CaptureBucketMemory>, Task> run)
        => run(CaptureBucketMemory.AllocateManaged);
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
        runBucketStorage = run => LedgerCaptureBucketStorage.RunAsync(ledgerOwner, run);
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


    public CpuCoreResidencySnapshot? Read() => Capture.Read();
    public CpuCoreResidencyObservation ReadObservation() => Capture.ReadObservation();
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
            if (ownedResourcesDisposed)
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

    public async IAsyncEnumerable<CpuCoreResidencyObservation> SubscribeAsync(
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
            yield return ReadObservation();
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

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        lock (subscriptionGate)
        {
            ObjectDisposedException.ThrowIf(ownedResourcesDisposed, this);
            stopped = false;
        }
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var worker = new NativeSamplingSubscriptionWorker<CpuResidencySampleRequest, SamplingOwnerToken>(
            subscriptions, demandSignal,
            static _ => { },
            static failure => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure.Error).Throw(), waitForNextPlan: WaitForNextWindowPlanAsync);
        try
        {
            await Task.Yield();
            stoppingToken.ThrowIfCancellationRequested();
            // Continuous history is used across collections until this run stops
            // or changes to finite windows. Pause returns it immediately.
            await runBucketStorage(async allocate =>
            {
                using var runStorage = new CaptureBucketStorage(allocate);
                try
                {
                    Capture.SetBucketStorage(runStorage);
                    Capture.EnsureStarted();
                    await worker.RunAsync(async (plan, token) =>
                    {
                        if (!subscriptions.HasPersistentDemand(static request => !request.CancellationToken.IsCancellationRequested))
                            return SubscriptionSampleCompletion.Skipped;
                        var windowed = WindowedCapture;
                        if (!windowed)
                        {
                            Capture.BeginWindow();
                            return await SampleCurrentWindowAsync();
                        }
                        var completion = SubscriptionSampleCompletion.Skipped;
                        await runBucketStorage(async windowAllocate =>
                        {
                            using var windowStorage = new CaptureBucketStorage(windowAllocate);
                            try
                            {
                                completion = await Capture.RunWindowAsync(windowStorage, SampleCurrentWindowAsync);
                            }
                            finally { Capture.SetBucketStorage(runStorage); }
                        });
                        return completion;

                        async Task<SubscriptionSampleCompletion> SampleCurrentWindowAsync()
                        {
                            if (windowed && await WaitForWindowOrDemandAsync(token))
                                return SubscriptionSampleCompletion.Skipped;
                            var committed = false;
                            Capture.Collect(plan.OwnerToken, (_, sourceQuerySucceeded) =>
                            {
                                committed = true;
                                IReadOnlySet<string> queried = sourceQuerySucceeded
                                    ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "process.cpu.residency" }
                                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                pendingFieldCollection = fieldDeliveries.CaptureCompleted<CpuResidencySampleRequest>(
                                    plan.Triggers, queried, committed: true);
                            });
                            return token.IsCancellationRequested || Volatile.Read(ref stopped)
                                || !plan.OwnerToken.IsActive ? SubscriptionSampleCompletion.Skipped
                                : committed ? SubscriptionSampleCompletion.Sampled : SubscriptionSampleCompletion.Skipped;
                        }
                    }, stoppingToken, planCreated: plan =>
                    {
                        lock (subscriptionGate)
                            if (stopped) throw new OperationCanceledException(stoppingToken);
                        UpdateCaptureFrequency(plan);
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
                finally { StopSession("ETW CPU 执行时长归因正在随程序停止。"); }
            });
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || Volatile.Read(ref stopped)) { }
        finally
        {
            pendingFieldCollection = null;
            fieldDeliveries.DiscardRunRounds();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        BeginStop();
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        BeginStop();
        fieldDeliveries.RetireAll();
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
            if (ownedResourcesDisposed)
            {
                return;
            }

            subscriptions.Remove(sourceKey, DateTimeOffset.UtcNow);
        }
        SignalDemand();
    }

    private void StopSession(string idleMessage)
    {
        Capture.Stop(idleMessage);
    }
    private void BeginStop()
    {
        lock (subscriptionGate)
        {
            if (stopped) return; stopped = true;
        }
        SignalDemand();
    }
    private static CpuResidencyCaptureConfiguration MapPlan(CompiledCpuCoreResidencyPlan plan)
        => new(plan.ObservationWindowMilliseconds, plan.ExecutionTimeSource.MaximumActivePairs,
            plan.PhysicalCoreByLogicalProcessor.ToFrozenDictionary(pair => pair.Key, pair => new CpuResidencyCoreAccounting(
                pair.Value.PhysicalCoreId, pair.Value.CcdId, pair.Value.LogicalProcessorCount, pair.Value.LogicalProcessorShare)),
            "Host published typed core/CCD accounting plan", plan.ExecutionTimeSource.BucketWidthMilliseconds);
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
