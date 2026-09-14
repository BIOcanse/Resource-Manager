using ResourceManager.Adapter.SharedMemory;
using ResourceManager.App.Application.Optimization.SmartControl;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.PublicResources;

namespace ResourceManager.App.Infrastructure.Optimization;

internal interface IHostManagerNativeExternalEffectSink
{
    ValueTask RunAutomaticMemoryCleanupAsync(HostManagerCycleEffectPermit permit);

    void RunPublicResourceLifecycle(HostManagerCycleEffectPermit permit);
}

internal static class HostManagerNativeExternalEffectDispatcher
{
    internal static async ValueTask RunAsync<TSink>(
        HostManagerCycleEffectAdmission effectAdmission,
        bool automaticMemoryCleanupEnabled,
        bool policyExecutionEnabled,
        HostManagerCycleEffectKind kind,
        TSink sink)
        where TSink : class, IHostManagerNativeExternalEffectSink
    {
        if (kind is not (HostManagerCycleEffectKind.PublicResourceLifecycle
            or HostManagerCycleEffectKind.AutomaticMemoryCleanup))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        if (kind == HostManagerCycleEffectKind.PublicResourceLifecycle
            && effectAdmission.TryAcquire(
                HostManagerCycleEffectKind.PublicResourceLifecycle,
                out var publicResourcePermit))
        {
            sink.RunPublicResourceLifecycle(publicResourcePermit);
        }

        if (kind == HostManagerCycleEffectKind.AutomaticMemoryCleanup
            && automaticMemoryCleanupEnabled
            && policyExecutionEnabled
            && effectAdmission.TryAcquire(
                HostManagerCycleEffectKind.AutomaticMemoryCleanup,
                out var memoryCleanupPermit))
        {
            await sink.RunAutomaticMemoryCleanupAsync(memoryCleanupPermit);
        }
    }
}

public sealed partial class HostManagerSmartCoordinator
{
    private (HostPublicResourceNormalReleaseCycle ReleaseCycle, NativeExternalEffectSink Sink)
        CreateNativeExternalEffectCycle(
        HostManagerCycleEffectAdmission effectAdmission,
        HostManagerProcessEffectValidationCycleSnapshot processEffectValidationScope,
        HostManagerSample sample,
        CompiledOptimizationModePlan modePlan,
        CompiledRuntimePlan runtimePlan,
        CompiledHostManagerPlan hostPlan,
        long runtimePlanVersion,
        ulong hostPublicationSequence,
        ulong cycleSequence,
        CancellationToken cancellationToken)
    {
        var policyExecutionEnabled = hostManagerSmartControlZones.CanRun(
            HostManagerSmartControlZoneIds.PolicyExecution);
        var cleanup = hostPlan.HotPublish.MemoryCleanup;
        sample.Hardware.TryGetCurrentDataset(
            SamplingDatasetIds.SystemMemoryUsage,
            out var memoryDataset);
        var normalReleaseCycle = new HostPublicResourceNormalReleaseCycle(
            runtimePlanVersion,
            hostPublicationSequence,
            nativeHostSessionIncarnation,
            hostPlan.PlanEpoch,
            hostPlan.PlanSha256,
            cleanup.ConfigurationGeneration,
            hostPlan.SmartCoordinator.ConfigurationGeneration,
            hostPlan.SmartCoordinator.ConfigurationSha256,
            NormalMemoryReleaseEnabled: modePlan.AutomaticMemoryCleanupEnabled
                && policyExecutionEnabled
                && !effectAdmission.IsScoreOnly
                && HostManagerProcessEffectValidationCyclePolicy.AllowsCycleEffect(
                    processEffectValidationScope,
                    HostManagerCycleEffectKind.AutomaticMemoryCleanup),
            memoryDataset?.WorkspaceIdentity ?? 0,
            memoryDataset?.ConfigurationGeneration ?? 0,
            memoryDataset?.CatalogGeneration ?? 0,
            memoryDataset?.SourceGeneration ?? 0,
            memoryDataset?.ObservedAtUtcTicks ?? 0);
        var memoryCapacityWithReleaseEvidence =
            hostPublicResourceNormalReleaseEvidence.BeginCycle(
                normalReleaseCycle,
                HostPublicResourceCapacityObserver.ObserveMemory(
                    policyExecutionEnabled,
                    cleanup,
                    sample.IdleCapacity.MemoryFreeRatio,
                    sample.IdleCapacity.MemoryFreeRatioCurrent));
        var videoMemoryCapacityCurrent = HostPublicResourceCapacityObserver.ObserveVideoMemory(
            policyExecutionEnabled,
            hostPlan.HotPublish.ResourceScheduler.Configuration,
            sample.Hardware.GpuInventory);
        var capacityFreshness =
            hostPublicResourceCapacitySampleFreshness.Observe(sample.Hardware);
        var memoryCapacity = capacityFreshness.Memory
            ? memoryCapacityWithReleaseEvidence
            : HostPublicResourceCapacityObservation.UnknownOrStale;
        var videoMemoryCapacity = capacityFreshness.VideoMemory
            ? videoMemoryCapacityCurrent.Summary
            : HostPublicResourceCapacityObservation.UnknownOrStale;
        var videoMemoryAdapters = capacityFreshness.VideoMemory
            ? videoMemoryCapacityCurrent.Adapters
            : [];
        var sink = new NativeExternalEffectSink(
            this,
            processEffectValidationScope,
            sample,
            runtimePlan,
            new HostPublicResourceCapacityShortage(
                memoryCapacity,
                videoMemoryCapacity,
                videoMemoryAdapters),
            cycleSequence,
            memoryCleanupCapacitySampleFresh: capacityFreshness.Memory,
            cancellationToken);
        // Z4 hardware placement and Z5 adapted-resource scheduling stay capability-off here.
        // The current Z3 snapshot ABI does not export their exact native handoff fields, so
        // this layer must not derive software grades or per-GPU scores in managed code.
        return (normalReleaseCycle, sink);
    }

    private sealed class NativeExternalEffectSink : IHostManagerNativeExternalEffectSink
    {
        private readonly HostManagerSmartCoordinator owner;
        private readonly HostManagerProcessEffectValidationCycleSnapshot
            processEffectValidationScope;
        private readonly HostManagerSample sample;
        private readonly CompiledRuntimePlan runtimePlan;
        private readonly HostPublicResourceCapacityShortage shortage;
        private readonly ulong cycleSequence;
        private readonly bool memoryCleanupCapacitySampleFresh;
        private readonly CancellationToken cancellationToken;

        internal NativeExternalEffectSink(
            HostManagerSmartCoordinator owner,
            HostManagerProcessEffectValidationCycleSnapshot processEffectValidationScope,
            HostManagerSample sample,
            CompiledRuntimePlan runtimePlan,
            HostPublicResourceCapacityShortage shortage,
            ulong cycleSequence,
            bool memoryCleanupCapacitySampleFresh,
            CancellationToken cancellationToken)
        {
            this.owner = owner;
            this.processEffectValidationScope = processEffectValidationScope;
            this.sample = sample;
            this.runtimePlan = runtimePlan;
            this.shortage = shortage;
            this.cycleSequence = cycleSequence;
            this.memoryCleanupCapacitySampleFresh =
                memoryCleanupCapacitySampleFresh;
            this.cancellationToken = cancellationToken;
            MemoryCleanupResult = default;
        }

        internal HostManagerMemoryCleanupExecutionResult MemoryCleanupResult { get; private set; }

        public async ValueTask RunAutomaticMemoryCleanupAsync(
            HostManagerCycleEffectPermit permit)
        {
            var authority = owner.schedulingAuthorityOwner.Capture();
            MemoryCleanupResult = await owner.ApplyAutomaticMemoryCleanupAsync(
                permit,
                processEffectValidationScope,
                sample,
                authority.Compute,
                runtimePlan,
                cycleSequence,
                memoryCleanupCapacitySampleFresh,
                cancellationToken);
        }

        public void RunPublicResourceLifecycle(HostManagerCycleEffectPermit permit)
            => owner.RunHostPublicResourceSelfManager(
                permit,
                shortage);
    }
}
