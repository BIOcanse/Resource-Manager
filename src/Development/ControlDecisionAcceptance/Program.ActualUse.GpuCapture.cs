using System.Reflection;
using System.Diagnostics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.StateItems;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Processes.Etw;

namespace ResourceManager.ControlDecisionAcceptance;

internal static partial class Program
{
    private static TimeSpan ActualUseGpuFilterBudget(IServiceProvider services)
    {
        var collector = services.GetRequiredService<GpuAllocationCollector>();
        var role = services.GetRequiredService<RuntimePlanProvider>().Current.HostManager.RequirePublished()
            .SamplingSubscription.HotPublish.Roles.Single(value => value.RoleId == 3);
        var interval = Math.Max(1000, role.MinimumIntervalMilliseconds);
        var delivery = (TimeSpan)Member(collector, "deliveryBudget")!;
        // Startup: one default/due interval, one complete native delivery budget,
        // and the role's delivery grace. Cleanup gets the same bounded allowance.
        var settle = TimeSpan.FromMilliseconds(checked(role.DefaultIntervalMilliseconds + interval + role.FreshnessGraceMilliseconds)) + delivery;
        return settle + TimeSpan.FromSeconds(10) + settle;
    }

    // queries receives every read-only query of the product GPU allocation session in this phase.
    private static async Task RunActualUseGpuFilterPhaseAsync(IServiceProvider services, IKernelEtwSessionBroker broker,
        string evidence, List<NativeKernelQuery> queries, CancellationToken token)
    {
        var collector = services.GetRequiredService<GpuAllocationCollector>();
        var sampler = services.GetRequiredService<WindowsResourceBreakdownSampler>();
        var scheduler = services.GetRequiredService<BusinessBlockScheduler>();
        var role = services.GetRequiredService<RuntimePlanProvider>().Current.HostManager.RequirePublished()
            .SamplingSubscription.HotPublish.Roles.Single(value => value.RoleId == 3);
        var interval = TimeSpan.FromMilliseconds(Math.Max(1000, role.MinimumIntervalMilliseconds));
        var deliveryBudget = (TimeSpan)Member(collector, "deliveryBudget")!;
        var settleBudget = TimeSpan.FromMilliseconds(checked(role.DefaultIntervalMilliseconds
            + (long)interval.TotalMilliseconds + role.FreshnessGraceMilliseconds)) + deliveryBudget;
        var threshold = (double)Member(Member(collector, "plan")!, "LiveTrackingThresholdSeconds")!;
        var name = ResourceManagerEtwSessionNames.Create(ResourceManagerEtwSessionNames.GpuAllocationsPrefix,
            ResourceManagerEtwSessionNames.CurrentOwner);
        var provider = GpuAllocationCollector.Provider;
        var allowed = (int[])typeof(GpuAllocationCollector).GetField("EventIds", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var measurement = new ActualUseGpuMeasurement(provider, allowed);
        measurement.ReadRundownMetadata();
        using var observation = GpuAllocationObservation.Subscribe(name, measurement);
        var rows = new List<object>();
        var started = DateTimeOffset.UtcNow;
        var finished = false;
        var consumed = 0L;
        var rejected = 0L;
        var unexpected = new HashSet<string>(StringComparer.Ordinal);
        var seenIds = new HashSet<int>();
        EtwSessionEventDrops? baseline = EtwEventDropDiagnostics.Read().Sessions.SingleOrDefault(value => value.Name == name);
        var baselineKinds = baseline?.Kinds.ToDictionary(value => value.Kind, value => value.Received, StringComparer.Ordinal)
            ?? new Dictionary<string, long>(StringComparer.Ordinal);
        var baselineDrops = baseline?.Kinds.ToDictionary(value => value.Kind, value => value.Dropped, StringComparer.Ordinal)
            ?? new Dictionary<string, long>(StringComparer.Ordinal);
        using var self = Process.GetCurrentProcess();
        var processes = new Dictionary<int, long> { [self.Id] = self.StartTime.ToUniversalTime().ToFileTimeUtc() };
        // The live ledger turns active at its EndScan event, before the capture that built it
        // disables the boundary provider and publishes. Steady state starts at that publication.
        var phaseStartPublication = collector.ReadCurrent(processes)?.Generation ?? 0;
        IDisposable? lease = null;
        Exception? failure = null;
        try
        {
            if (interval.TotalSeconds >= threshold)
                throw new InvalidDataException($"Positive GPU filter profile requires a live interval; selected {interval}, threshold {threshold}s. No finite/unsupported fallback is accepted.");
            lease = sampler.AcquireSubscription("actual-use:gpu-allocation-filter", SchedulingProcessMetricMask.GpuDedicatedMemory, interval);
            var settling = Stopwatch.StartNew();
            var stable = false;
            long sessionGeneration = 0;
            ulong firstPublication = 0;
            ulong latestPublication = 0;
            var active = new Stopwatch();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var at = DateTimeOffset.UtcNow;
                var holders = ReadActualUseHolders(services, broker);
                var use = ReadGpuCaptureUse(collector);
                var native = QueryOwnedKernel(name);
                queries.Add(native);
                var drops = EtwEventDropDiagnostics.Read().Sessions.SingleOrDefault(value => value.Name == name);
                // This is an actual read of allocation output beneath the owned
                // resource-field subscription; it never starts a capture itself.
                var current = collector.ReadCurrent(processes);
                var source = holders.Activity.Single(value => value.Uid == StateItemUids.ProcessDedicatedGpuMemoryInUse);
                rows.Add(new { at, holders, use, native, drops, reading = current,
                    nativeBlock = scheduler.Read().Blocks.Single(value => value.Id == BlockUids.GpuAllocationCollector).State });
                if (drops is not null)
                {
                    var dxg = drops.Kinds.Where(value => value.Kind.StartsWith(provider.ToString("D") + "/", StringComparison.OrdinalIgnoreCase)).ToArray();
                    consumed = 0;
                    rejected = 0;
                    foreach (var kind in dxg)
                    {
                        var count = kind.Received - (baseline?.Generation == drops.Generation
                            ? baselineKinds.GetValueOrDefault(kind.Kind) : 0);
                        if (count <= 0) continue;
                        var id = int.Parse(kind.Kind.Split('/')[1], System.Globalization.CultureInfo.InvariantCulture);
                        seenIds.Add(id);
                        consumed += count;
                        rejected += kind.Dropped - (baseline?.Generation == drops.Generation
                            ? baselineDrops.GetValueOrDefault(kind.Kind) : 0);
                        if (!allowed.Contains(id)) unexpected.Add(kind.Kind);
                    }
                    // Do not end the measurement on its first unread event.
                    // Enable/CaptureState bursts and sustained delivery need
                    // the whole ten-second profile plus filter-only variants.
                }
                var ready = source.InUse && source.HolderIds.Any(value => value.Contains("actual-use:gpu-allocation-filter", StringComparison.Ordinal))
                    && use.LiveLedgerActive && native.Exists && current is not null
                    && current.Generation > phaseStartPublication && drops is not null;
                if (!stable && ready)
                {
                    stable = true;
                    sessionGeneration = drops!.Generation;
                    firstPublication = current!.Generation;
                    active.Start();
                }
                if (stable)
                {
                    if (!ready || drops!.Generation != sessionGeneration || drops.EventsLost != 0)
                        throw new InvalidDataException($"GPU live capture did not remain available in one loss-free session: {DescribeHolders(holders)}");
                    var enabled = drops.EnabledProviders;
                    if (enabled.Count != 1 || enabled[0].Provider != provider.ToString("D") || enabled[0].Keywords != 0x41)
                        throw new InvalidDataException($"GPU live provider recording is not exactly DxgKrnl/0x41: {DescribeHolders(holders)}");
                    latestPublication = current!.Generation;
                    if (active.Elapsed >= TimeSpan.FromSeconds(10))
                    {
                        if (consumed - rejected <= 0 || latestPublication <= firstPublication)
                            throw new InvalidDataException("Positive GPU filtering had no accepted DxgKrnl events or no subsequent consumed publication; absent or entirely dropped delivery cannot pass this phase.");
                        if (measurement.Overflow != 0)
                            throw new InvalidDataException("GPU timing measurement exceeded its bounded bins/commands; incomplete evidence cannot pass.");
                        measurement.FinishObservation();
                        measurement.RequireRestoredFilter(ActualUseGpuMeasurement.RestorationComparisonGrace);
                        finished = true;
                        break;
                    }
                }
                else if (settling.Elapsed >= settleBudget)
                    throw new InvalidDataException($"GPU field's first due did not establish a live ledger within its source/delivery budget {settleBudget}; {DescribeHolders(holders)}");
                await Task.Delay(100, token);
            }
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            measurement.FinishObservation();
            observation.Dispose();
            lease?.Dispose();
            await SaveAsync(evidence, "gpu-filter-samples.json", rows);
            await SaveAsync(evidence, "gpu-filter-timing.json", measurement.Read());
            await SaveAsync(evidence, "gpu-filter-restoration.json",
                measurement.ReadRestorationEvidence(ActualUseGpuMeasurement.RestorationComparisonGrace));
            await SaveAsync(evidence, "gpu-filter-summary.json", new { started, ended = DateTimeOffset.UtcNow, passed = finished,
                source = "actual-use:gpu-allocation-filter", interval, settleBudget, threshold, allowedIds = allowed,
                deliveredIds = seenIds.Order().ToArray(), deliveredDxgEvents = consumed, collectorRejectedDxgEvents = rejected,
                unexpectedKinds = unexpected.ToArray(), failure = failure?.ToString(),
                filterCapability = finished ? "post-capture-filter-restoration-confirmed" : "not-confirmed",
                unreadKindScope = "Includes the CaptureState burst and buffered deliveries; steady post-restoration newly emitted unread IDs must be zero.",
                proofLimit = "Pass proves a complete ten-second, loss-free, owned and consumed GPU-memory-user profile plus positive needed-rundown and restored-filter evidence. The explicit one-second comparison grace and pre-cutoff emitted events are excluded only from the steady filter assertion, not from recorded delivery or drop cost. Missing rundown facts are never rebuilt." });
        }
    }

    private sealed record ActualUseGpuCapture(bool HasRun, int AcceptedCaptureRequests, bool CaptureGateOwned,
        bool ScanActive, bool LiveLedgerActive)
    {
        public bool NeedsNativeSession => LiveLedgerActive || HasRun && AcceptedCaptureRequests > 0 && CaptureGateOwned;
    }
    private static ActualUseGpuCapture ReadGpuCaptureUse(GpuAllocationCollector collector)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = typeof(GpuAllocationCollector);
        object Field(string name) => type.GetField(name, flags)!.GetValue(collector)!;
        var lifecycle = Field("lifecycle");
        var gate = Field("gate");
        bool hasRun;
        var accepted = 0;
        lock (lifecycle)
        {
            var run = type.GetField("activeRun", flags)!.GetValue(collector);
            hasRun = run is not null;
            if (run is not null) accepted = (int)run.GetType().GetField("captureRequests", flags)!.GetValue(run)!;
        }
        bool scan, live;
        lock (gate)
        {
            scan = type.GetField("scan", flags)!.GetValue(collector) is not null;
            live = type.GetField("liveLedger", flags)!.GetValue(collector) is not null;
        }
        var captureGateOwned = ((SemaphoreSlim)Field("captureGate")).CurrentCount == 0;
        // Independent admitted work/live state, never session/ClosingSession or
        // native existence. No collector lock spans the native query.
        return new(hasRun, accepted, captureGateOwned, scan, live);
    }
}
