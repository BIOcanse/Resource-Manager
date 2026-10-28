using System.Text.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.MemoryProtection;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Adaptation.Scheduling;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.Infrastructure.GpuPlacement.Preparation;
using ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.App.Application.TrustedAccess;
using ResourceManager.App.Infrastructure.Adaptation.Transport;
using ResourceManager.App.Infrastructure.ServiceHosting;

namespace ResourceManager.ControlDecisionAcceptance;

internal static class EffectBoundaries
{
    internal const string Reason = "development-no-process-or-hardware-effects";
    internal static readonly string[] Overrides = [
        "Logging: SimpleConsole only; no Windows EventLog or global log sink",
        "production process-effect scope: exact own process only; cleanup and memory transactions off",
        "IProcessResourcePolicyWriter: real Windows reads, every mutation rejected",
        "IControlWriteLayer: every hardware write/release rejected",
        "IControlObjectCatalog: all inventory/probe reads explicitly unavailable; no individual writer Describe/Probe",
        "WindowsHardwareMetricSampler: same real DI read delegates; PawnIO/SMN/MSR/HWM/OEM unavailable; no borrowed control resources; native scratch instead of Ledger for this metric sampler",
        "IHostMemoryProtectionExecutor: every target excluded",
        "IGpuLaunchInterceptionRegistry: managed no-op, no registry read/write",
        "IAdapterPolicyDispatcher: all effects rejected",
        "IRunningGpuPlacementActionService: no preparation/application; optional renderer observation unavailable",
        "IWindowsGraphicsPreferenceStore: no registry read/write",
        "FanControlCoreClient: existing session factory with empty managed session",
        "WindowsGpuWindowActionRuntime/WindowsGpuCallbackPreparationRuntime: missing private executable paths",
        "TrustedAccessInvocationHostedService: unique private pipe",
        "IInteractiveUserSessionBroker: real read-only session facts; no process launching"
    ];

    internal static void Apply(IServiceCollection services, string privateRoot, string pipeName)
    {
        services.Replace(ServiceDescriptor.Singleton<IProcessResourcePolicyWriter>(provider =>
            new ReadOnlyPolicyWriter(new WindowsProcessResourcePolicyWriter(
                new NativeProcessPolicyBatchExecutor(provider.GetRequiredService<HostManagerProcessPolicyExecutorRuntime>())))));
        DriverFreeMetrics.Apply(services);
        services.Replace(ServiceDescriptor.Singleton<IControlWriteLayer, DeniedControlWrites>());
        services.Replace(ServiceDescriptor.Singleton<IControlObjectCatalog, DeniedControlCatalog>());
        services.Replace(ServiceDescriptor.Singleton<IHostMemoryProtectionExecutor, ExcludedMemoryExecutor>());
        services.Replace(ServiceDescriptor.Singleton<IInteractiveUserSessionBroker, NoUserSessionLaunch>());
        services.Replace(ServiceDescriptor.Singleton<IGpuLaunchInterceptionRegistry, NoInterceptionRegistry>());
        services.Replace(ServiceDescriptor.Singleton<IAdapterPolicyDispatcher, DeniedAdapterDispatcher>());
        services.Replace(ServiceDescriptor.Singleton<IRunningGpuPlacementActionService, DeniedGpuActions>());
        services.Replace(ServiceDescriptor.Singleton<IWindowsGraphicsPreferenceStore, NoGraphicsPreferences>());
        services.Replace(ServiceDescriptor.Singleton<FanControlCoreClient>(provider => new FanControlCoreClient(
            provider.GetRequiredService<IHostEnvironment>(), static (_, _) => new EmptyFanSession(),
            provider.GetRequiredService<ILogger<FanControlCoreClient>>())));
        services.Replace(ServiceDescriptor.Singleton<WindowsGpuWindowActionRuntime>(_ => new(
            Path.Combine(privateRoot, "forbidden-helper", "window-action.exe"), null)));
        services.Replace(ServiceDescriptor.Singleton<WindowsGpuCallbackPreparationRuntime>(_ => new(
            Path.Combine(privateRoot, "forbidden-helper", "preparation.exe"))));
        services.Replace(ServiceDescriptor.Singleton<TrustedAccessInvocationHostedService>(provider => new(
            provider.GetRequiredService<ITrustedAccessKeyService>(),
            provider.GetRequiredService<IGpuStartupPlacementResolver>(),
            provider.GetRequiredService<IGpuLaunchExecutionReportStore>(),
            provider.GetRequiredService<IInteractiveUserSessionBroker>(),
            provider.GetRequiredService<HostManagerRuntimeIdentity>(),
            provider.GetRequiredService<ILogger<TrustedAccessInvocationHostedService>>(), pipeName)));
    }

    private sealed class NoUserSessionLaunch : IInteractiveUserSessionBroker
    {
        private readonly WindowsInteractiveUserSessionBroker windows = new();
        public IReadOnlyList<uint> GetActiveSessionIds() => windows.GetActiveSessionIds();
        public string? GetUserSid(uint sessionId) => windows.GetUserSid(sessionId);
    }

    private sealed class DeniedControlCatalog : IControlObjectCatalog
    {
        public ControlObjectCatalog ReadObjects() => throw new NotSupportedException(Reason + ":control-metadata-provider-unavailable");
        public Task<ControlObjectCatalog> ReadObjectsAsync(CancellationToken token)
            => Task.FromException<ControlObjectCatalog>(new NotSupportedException(Reason + ":control-metadata-provider-unavailable"));
    }

    private sealed class DeniedControlWrites : IControlWriteLayer
    {
        public Task<ControlApplyReport> WriteAsync(ControlDesiredState desired, CancellationToken token) => Task.FromResult(Rejected(desired));
        public Task<ControlApplyReport> ReleaseAsync(ControlDesiredState removed, ControlDesiredState remaining, CancellationToken token) => Task.FromResult(Rejected(removed));
        private static ControlApplyReport Rejected(ControlDesiredState desired) => new(
            desired.Objects.SelectMany(target => target.Settings.Select(setting => new ControlApplyOutcome(
                target.ObjectId, setting.CapabilityId, ControlApplyStatuses.Failed))).ToArray(), DateTimeOffset.UtcNow);
    }

    private sealed class ExcludedMemoryExecutor : IHostMemoryProtectionExecutor
    {
        public IHostMemoryProtectionTarget Open(HostMemoryProcess process) => new Target();
        private sealed class Target : IHostMemoryProtectionTarget
        {
            public string ExclusionReason => Reason;
            public HostMemoryActionResult Execute(IHostMemoryProtectionReserve reserve, HostMemoryActionOptions? options = null)
                => new("excluded", Reason, [], 0);
            public void Dispose() { }
        }
    }

    private sealed class EmptyFanSession : IFanControlCoreSession
    {
        public JsonElement Handle(JsonElement request) => JsonSerializer.SerializeToElement(new
            { available = false, reason = Reason, fans = Array.Empty<object>() });
        public void Dispose() { }
    }

    private sealed class NoInterceptionRegistry : IGpuLaunchInterceptionRegistry
    {
        public GpuLaunchInterceptionStatus Apply(GpuPlacementProcessPolicy policy)
            => throw new InvalidOperationException(Reason);
        public GpuLaunchInterceptionStatus GetStatus(GpuPlacementProcessPolicy policy)
            => throw new InvalidOperationException(Reason);
        public IReadOnlyList<GpuLaunchInterceptionStatus> Reconcile(GpuPlacementPolicyDocument document,
            IReadOnlyCollection<string> moduleExecutablePaths) => [];
        public GpuLaunchInterceptionCleanupResult RemoveAllOwnedRules() => new(true, 0, Reason);
    }

    private sealed class DeniedAdapterDispatcher : IAdapterPolicyDispatcher
    {
        public Task<AdapterSoftwareSchedulingResult> ApplySoftwareSchedulingAsync(string softwareId,
            AdapterSoftwareSchedulingEnvelope envelope, CancellationToken token) => throw new InvalidOperationException(Reason);
        public Task<AdapterSchedulingStateExportResult> ExportSoftwareSchedulingStateAsync(string softwareId,
            int maximumPayloadBytes, DateTimeOffset deadline, CancellationToken token) => throw new InvalidOperationException(Reason);
        public Task<AdapterSchedulingStateRestoreResult> RestoreSoftwareSchedulingStateAsync(string softwareId,
            AdapterSchedulingStateRestoreCommand command, CancellationToken token) => throw new InvalidOperationException(Reason);
    }

    private sealed class NoGraphicsPreferences : IWindowsGraphicsPreferenceStore
    {
        public RecoveryReadResult<string> ReadValueForRecovery(string path) => RecoveryReadResult<string>.Unavailable(50, Reason);
        public void WriteValue(string path, string value) => throw new InvalidOperationException(Reason);
        public void DeleteValue(string path) => throw new InvalidOperationException(Reason);
        public string BuildPreferIntegratedGpuValue(string? value) => throw new InvalidOperationException(Reason);
        public string BuildPreferHighPerformanceGpuValue(string? value) => throw new InvalidOperationException(Reason);
        public string DescribePreference(string? value) => Reason;
    }

    private sealed class DeniedGpuActions : IRunningGpuPlacementActionService
    {
        public IDisposable? AcquireRendererObservation() => null;
        public Task<RunningGpuPlacementPreparation> PrepareAsync(RunningGpuPlacementActionRequest request, CancellationToken token)
            => Task.FromResult(new RunningGpuPlacementPreparation(null, Reason));
        public Task<RunningGpuPlacementPreparation> PrepareWithFirstApiObservationAsync(RunningGpuPlacementActionRequest request,
            Func<GpuPlacementProcessInstance, CancellationToken, Task<GpuGraphicsApi?>> observe, CancellationToken token)
            => PrepareAsync(request, token);
        public Task<RunningGpuPlacementPreparation> PrepareWithFirstApiObservationAsync(RunningGpuPlacementActionRequest request,
            RunningGpuApiObservationExecution execution, CancellationToken token) => PrepareAsync(request, token);
        public Task<RunningGpuPlacementActionResult> TryApplyAsync(RunningGpuPlacementActionPlan plan,
            RunningGpuPlacementExecution execution, CancellationToken token)
            => Task.FromResult(new RunningGpuPlacementActionResult([], Reason, RunningGpuPlacementActionStatuses.Skipped));
    }

    private sealed class ReadOnlyPolicyWriter(IProcessResourcePolicyWriter windows) : IProcessResourcePolicyWriter
    {
        private static ProcessResourcePolicyWriteResult Rejected() => new(false, Reason);
        public IThreadIdealProcessorReadCycle CreateThreadIdealProcessorReadCycle() => windows.CreateThreadIdealProcessorReadCycle();
        public RecoveryReadResult<IReadOnlyList<ThreadIdealProcessorPolicySnapshot>> ReadProcessThreadIdealProcessors(int pid, ulong start, int max)
            => windows.ReadProcessThreadIdealProcessors(pid, start, max);
        public RecoveryReadResult<ThreadIdealProcessorPolicySnapshot> ReadThreadIdealProcessor(int pid, ulong start, int tid)
            => windows.ReadThreadIdealProcessor(pid, start, tid);
        public ProcessResourcePolicySnapshot? TryReadProcess(int pid) => windows.TryReadProcess(pid);
        public DateTimeOffset? TryReadProcessStartedAt(int pid) => windows.TryReadProcessStartedAt(pid);
        public RecoveryReadResult<ProcessInstanceRecoverySnapshot> ReadProcessInstanceForRecovery(int pid) => windows.ReadProcessInstanceForRecovery(pid);
        public RecoveryReadResult<ProcessMemoryPriorityRecoverySnapshot> ReadMemoryPriorityStateForRecovery(int pid) => windows.ReadMemoryPriorityStateForRecovery(pid);
        public RecoveryReadResult<ProcessPlacementRecoverySnapshot> ReadPlacementStateForRecovery(int pid, ProcessPlacementReadFields fields)
            => windows.ReadPlacementStateForRecovery(pid, fields);
        public RecoveryReadResult<ThreadCpuSetPolicySnapshot> ReadThreadPlacementStateForRecovery(int pid, int tid)
            => windows.ReadThreadPlacementStateForRecovery(pid, tid);
        public IReadOnlyList<uint>? TryReadProcessDefaultCpuSets(int pid) => windows.TryReadProcessDefaultCpuSets(pid);
        public ThreadCpuSetPolicySnapshot? TryReadThreadSelectedCpuSets(int pid, int tid) => windows.TryReadThreadSelectedCpuSets(pid, tid);
        public ProcessMemoryPrioritySnapshot? TryReadMemoryPriority(int pid) => windows.TryReadMemoryPriority(pid);
        public ProcessPowerThrottlingSnapshot? TryReadPowerThrottling(int pid) => windows.TryReadPowerThrottling(pid);
        public ProcessResourcePolicyWriteResult TrySetThreadIdealProcessor(ThreadIdealProcessorPolicySnapshot expected, ThreadIdealProcessor desired) => Rejected();
        public ProcessResourcePolicyWriteResult TrySetPriorityClass(int pid, string priority) => Rejected();
        public ProcessResourcePolicyWriteResult TrySetProcessorAffinity(int pid, long affinity) => Rejected();
        public ProcessResourcePolicyWriteResult TrySetProcessDefaultCpuSets(int pid, IReadOnlyList<uint> sets) => Rejected();
        public ProcessResourcePolicyWriteResult TrySetThreadSelectedCpuSets(int pid, int tid, DateTimeOffset started, IReadOnlyList<uint> sets) => Rejected();
        public ProcessResourcePolicyWriteResult TrySetMemoryPriority(int pid, DateTimeOffset start, uint expected, uint value) => Rejected();
        public ProcessResourcePolicyWriteResult TrySetPowerThrottling(int pid, uint control, uint state) => Rejected();
        public IReadOnlyList<ProcessResourcePolicyBatchWriteResult> TryApplyBatch(IReadOnlyList<ProcessResourcePolicyBatchRequest> requests)
            => throw new InvalidOperationException(Reason);
    }
}





