using ResourceManager.App.Application.Control;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.Infrastructure.Diagnostics;
using ResourceManager.App.Infrastructure.Operations;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.PublicServices;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks.State;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

/// <summary>Connects current owner publications to the decision tree after DI has constructed it.</summary>
internal sealed class BackendDecisionStateBindings(IServiceProvider services,
    ILogger<BackendDecisionStateBindings> logger) : IAsyncDisposable
{
    private readonly List<Action> detach = [];
    private readonly List<Func<Task>> unbind = [];
    private bool initialized;
    private readonly CancellationTokenSource preparationCancellation = new();
    private Task preparation = Task.CompletedTask;

    internal async Task InitializeAsync(StateAuthority authority, CancellationToken cancellationToken)
    {
        if (initialized) return;
        initialized = true;
        var plans = services.GetRequiredService<RuntimePlanProvider>();
        var failures = services.GetRequiredService<HostedRunResultFacts>();
        var lastPlanVersion = plans.Current.Version;
        void PlanInputsChanged(ResourceManager.App.Domain.RuntimeSpecialization.CompiledRuntimePlan plan)
        {
            lock (failures)
            {
                if (plan.Version <= lastPlanVersion) return;
                lastPlanVersion = plan.Version;
                failures.PlanChanged();
            }
        }
        plans.Published += PlanInputsChanged;
        detach.Add(() => plans.Published -= PlanInputsChanged);
        if (services.GetService<ResourceManager.App.Application.ProcessAttribution.IRuntimeProcessAttributionCatalogProvider>()
            is ResourceManager.App.Infrastructure.ProcessAttribution.RuntimeProcessAttributionCatalogProvider catalogs)
        {
            Bind(StateItemUids.PortableAttributionCatalogAvailable, () => catalogs.IsAvailable,
                callback => catalogs.AvailabilityChanged += callback, callback => catalogs.AvailabilityChanged -= callback);
            void CatalogRecovered()
            {
                if (catalogs.IsAvailable) failures.Reset(BlockUids.PortableSoftwareDiscoveryService);
            }
            catalogs.AvailabilityChanged += CatalogRecovered;
            detach.Add(() => catalogs.AvailabilityChanged -= CatalogRecovered);
        }
        Plan(StateItemUids.DebugLoggingEnabled, () => plans.DecisionFacts.DebugLoggingEnabled);
        Plan(StateItemUids.AutomaticUpdatesEnabled, () => plans.DecisionFacts.AutomaticUpdatesEnabled);
        Plan(StateItemUids.OptimizationEnabled, () => plans.DecisionFacts.OptimizationEnabled);
        Plan(StateItemUids.GpuApiHistoryEnabled, () => plans.DecisionFacts.GpuApiHistoryEnabled);
        Plan(StateItemUids.BackgroundReportsEnabled, () => plans.DecisionFacts.BackgroundReportsEnabled);
        Plan(StateItemUids.PortableDiscoveryEnabled, () => plans.DecisionFacts.PortableDiscoveryEnabled);
        Plan(StateItemUids.KernelLeakObservationEnabled, () => plans.DecisionFacts.KernelLeakObservationEnabled);
        Plan(StateItemUids.PublicServiceEnabled, () => plans.DecisionFacts.PublicServiceEnabled);

        if (services.GetService<IControlPlane>() is ControlPlane plane)
        {
            Bind(StateItemUids.AppliedFanControlExists, () => plane.AppliedFanControlExists,
                callback => plane.AppliedIntentsChanged += callback, callback => plane.AppliedIntentsChanged -= callback);
            Bind(StateItemUids.AppliedControlIntentsExist, () => plane.AppliedControlIntentsExist,
                callback => plane.AppliedIntentsChanged += callback, callback => plane.AppliedIntentsChanged -= callback);
        }
        if (services.GetService<FanControlCoreClient>() is { } fan)
        {
            Bind(StateItemUids.FanCommandPending, () => fan.PendingCommands,
                callback => fan.PendingCommandsChanged += callback, callback => fan.PendingCommandsChanged -= callback);
            Bind(StateItemUids.SoftwareCurveRunNeeded, () => fan.SoftwareCurveRunNeeded,
                callback => fan.SoftwareCurveRunNeededChanged += callback, callback => fan.SoftwareCurveRunNeededChanged -= callback);
            Bind(StateItemUids.FanCoreAvailable, () => fan.IsAvailable,
                callback => fan.AvailabilityChanged += callback, callback => fan.AvailabilityChanged -= callback);
        }
        if (services.GetService<ControlActualStateReader>() is { } actual)
        {
            Bind(StateItemUids.ActualControlValuesInUse, () => actual.ActualControlValuesInUse,
                callback => actual.SubscriptionsChanged += callback, callback => actual.SubscriptionsChanged -= callback);
            Bind(StateItemUids.FanReadingsInUse, () => actual.FanReadingsInUse,
                callback => actual.FanReadingsChanged += callback, callback => actual.FanReadingsChanged -= callback);
        }
        if (services.GetService<RuntimeCpuLeaseStore>() is { } leases)
            Bind(StateItemUids.ActiveCpuLeasesExist, () => leases.ActiveCpuLeasesExist,
                callback => leases.ActiveCpuLeasesChanged += callback, callback => leases.ActiveCpuLeasesChanged -= callback);
        if (services.GetService<HostManagerSmartCoordinator>() is { } smart)
        {
            BindRead(StateItemUids.SmartOwnedStatePending, () => smart.SmartOwnedStatePendingPublication,
                callback => smart.SmartOwnedStateChanged += callback, callback => smart.SmartOwnedStateChanged -= callback);
        }
        if (services.GetService<JsonDebugDiagnosticLogWriter>() is { } debug)
            Bind(StateItemUids.DebugWriterPending, () => debug.HasPendingRecords,
                callback => debug.PendingRecordsChanged += callback, callback => debug.PendingRecordsChanged -= callback);
        if (services.GetService<TrustedAccessRequestOwner>() is { } trusted)
            Bind(StateItemUids.TrustedRequestsPending, () => trusted.HasPendingRequests,
                callback => trusted.PendingRequestsChanged += callback, callback => trusted.PendingRequestsChanged -= callback);
        if (services.GetService<HostManagerPublicServiceCoordinatorOwner>() is { } publicService)
        {
            Bind(StateItemUids.PublicServiceTasksPending, () => publicService.HasPendingTasks,
                callback => publicService.PendingTasksChanged += callback, callback => publicService.PendingTasksChanged -= callback);
        }
        if (services.GetService<HostManagerOperationCoordinatorOwner>() is { } operations)
        {
            BindRead(StateItemUids.OperationsPending, () => operations.PendingOperationsPublication,
                callback => operations.PendingOperationsChanged += callback, callback => operations.PendingOperationsChanged -= callback);
        }
        preparation = PrepareOwnersAsync(plans, preparationCancellation.Token);

        void Plan(int uid, Func<bool> read)
        {
            var publication = authority.ClaimReadFunction<bool>(uid, () => plans.Current.HostManager.IsPublished
                ? StateRead<bool>.FromValue(read()) : StateRead<bool>.NoValue(NoValueReason.NotPublished));
            void Changed(ResourceManager.App.Domain.RuntimeSpecialization.CompiledRuntimePlan plan) =>
                _ = publication.NotifyChangedAsync(Reason("plan-published"));
            plans.Published += Changed;
            detach.Add(() => plans.Published -= Changed);
            unbind.Add(() => publication.UnbindAsync(Reason("owner-disposed")));
            _ = publication.NotifyChangedAsync(Reason("initial"));
        }
        void Bind(int uid, Func<bool> read, Action<Action> subscribe, Action<Action> unsubscribe)
            => BindRead(uid, () => StateRead<bool>.FromValue(read()), subscribe, unsubscribe);
        void BindRead(int uid, Func<StateRead<bool>> read, Action<Action> subscribe, Action<Action> unsubscribe)
        {
            var publication = authority.ClaimReadFunction<bool>(uid, read);
            void Changed() => _ = publication.NotifyChangedAsync(Reason("owner-changed"));
            subscribe(Changed);
            detach.Add(() => unsubscribe(Changed));
            unbind.Add(() => publication.UnbindAsync(Reason("owner-disposed")));
            _ = publication.NotifyChangedAsync(Reason("initial"));
        }
    }

    private async Task PrepareOwnersAsync(RuntimePlanProvider plans, CancellationToken token)
    {
        try
        {
            if (!plans.Current.HostManager.IsPublished)
            {
                var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Changed(ResourceManager.App.Domain.RuntimeSpecialization.CompiledRuntimePlan plan)
                { if (plan.HostManager.IsPublished) published.TrySetResult(); }
                plans.Published += Changed;
                try
                {
                    Changed(plans.Current);
                    await published.Task.WaitAsync(token).ConfigureAwait(false);
                }
                finally { plans.Published -= Changed; }
            }
            var owners = new List<Task>();
            if (services.GetService<HostManagerSmartCoordinator>() is { } smart)
                owners.Add(PrepareOwnerAsync("Smart", smart.InitializeDecisionFactsAsync, token));
            if (services.GetService<HostManagerPublicServiceCoordinatorOwner>() is { } publicService)
                owners.Add(PrepareOwnerAsync("public services", publicService.InitializeAsync, token));
            if (services.GetService<HostManagerOperationCoordinatorOwner>() is { } operations)
                owners.Add(PrepareOwnerAsync("operations", operations.InitializeAsync, token));
            if (services.GetService<StartupCapabilitySet>()?.Allows(StartupCapability.RuntimeEffectOwners) == true
                && services.GetService<IControlPlane>() is { } plane)
                owners.Add(PrepareOwnerAsync("control", async cancellationToken =>
                {
                    // Catalog discovery is an ordinary finite subscription. Its producer's
                    // decision node observes it through the same subscription core as any reader.
                    using var catalog = services.GetService<IMetricSnapshotObservationSource>()?.AcquireSubscription(
                        "control.startup.catalog", MetricSampleRequest.CatalogProbe,
                        TimeSpan.FromMilliseconds(ResourceManager.App.Infrastructure.Optimization.OptimizationSettings.ProductDefaults.RuntimeLimits.StartupCatalogSubscriptionMilliseconds));
                    await plane.InitializeAsync(cancellationToken).ConfigureAwait(false);
                }, token));
            await Task.WhenAll(owners).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { logger.LogError(error, "Backend decision fact preparation failed."); }
    }

    private async Task PrepareOwnerAsync(string owner, Func<CancellationToken, Task> prepare, CancellationToken token)
    {
        try { await prepare(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { logger.LogError(error, "Backend {Owner} fact preparation failed.", owner); }
    }

    internal async Task StopAsync()
    {
        await preparationCancellation.CancelAsync().ConfigureAwait(false);
        await preparation.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        foreach (var unsubscribe in detach) unsubscribe();
        detach.Clear();
        foreach (var release in unbind) await release().ConfigureAwait(false);
        unbind.Clear();
        preparationCancellation.Dispose();
    }

    private static BusinessBlockReason Reason(string cause) =>
        new(BusinessBlockRequestKind.Configuration, nameof(BackendDecisionStateBindings), cause, 0);
}
