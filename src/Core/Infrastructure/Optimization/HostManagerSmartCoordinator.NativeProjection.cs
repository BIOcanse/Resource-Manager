using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Optimization;

internal interface IHostManagerNativeExternalEffectSink
{
    ValueTask RunAutomaticMemoryCleanupAsync(HostManagerCycleEffectPermit permit);
}

internal static class HostManagerNativeExternalEffectDispatcher
{
    internal static async ValueTask RunAsync<TSink>(
        HostManagerCycleEffectAdmission effectAdmission,
        bool automaticMemoryCleanupEnabled,
        TSink sink)
        where TSink : class, IHostManagerNativeExternalEffectSink
    {
        if (automaticMemoryCleanupEnabled
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
    // Z4 hardware placement and Z5 adapted-resource scheduling stay capability-off here.
    // The current Z3 snapshot ABI does not export their exact native handoff fields, so
    // this layer must not derive software grades or per-GPU scores in managed code.
    private sealed class NativeExternalEffectSink : IHostManagerNativeExternalEffectSink
    {
        private readonly HostManagerSmartCoordinator owner;
        private readonly HostManagerProcessEffectValidationCycleSnapshot
            processEffectValidationScope;
        private readonly HostManagerSample sample;
        private readonly CompiledRuntimePlan runtimePlan;
        private readonly ulong cycleSequence;
        private readonly CancellationToken cancellationToken;

        internal NativeExternalEffectSink(
            HostManagerSmartCoordinator owner,
            HostManagerProcessEffectValidationCycleSnapshot processEffectValidationScope,
            HostManagerSample sample,
            CompiledRuntimePlan runtimePlan,
            ulong cycleSequence,
            CancellationToken cancellationToken)
        {
            this.owner = owner;
            this.processEffectValidationScope = processEffectValidationScope;
            this.sample = sample;
            this.runtimePlan = runtimePlan;
            this.cycleSequence = cycleSequence;
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
                cancellationToken);
        }
    }
}
