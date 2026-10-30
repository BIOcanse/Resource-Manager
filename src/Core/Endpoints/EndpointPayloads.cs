// Explicit payload shapes preserve the property names and order of the original endpoint responses.
namespace ResourceManager.App.Endpoints;
public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    internal sealed record EndpointErrorPayload(
        string error);

    private sealed record MarkerProbeErrorPayload(
        string error,
        global::ResourceManager.App.Domain.Adaptation.AdapterResourceMarkerProbeResult markerProbe);

    private sealed record CredentialRevocationPayload(
        bool revoked);

    private sealed record EndpointErrorMessagePayload(
        string error,
        string message);

    private sealed record EndpointCodePayload(
        string code);

    private sealed record ComponentLevelPayload(
        string level);

    private sealed record ComponentLevelsPayload(
        string level,
        global::System.Collections.Generic.IReadOnlyList<string> levels);

    private sealed record AcknowledgementPayload(
        bool acknowledged);

    private sealed record AcceptancePayload(
        bool accepted);

    private sealed record ModuleHistoryRequirementsPayload(
        string module,
        global::System.Collections.Immutable.ImmutableArray<global::ResourceManager.App.Domain.Monitoring.HistoryRequirement> requirements);

    private sealed record HistoryRequirementsPayload(
        global::System.Collections.Immutable.ImmutableArray<global::ResourceManager.App.Domain.Monitoring.HistoryRequirement> requirements,
        ModuleHistoryRequirementsPayload[] consumers);

    private sealed record DiagnosticLinesPayload(
        bool ok,
        global::System.Collections.Generic.IReadOnlyList<string> lines);

    // GET /api/debug/native-memory. Ledger: bytes registered in the product Ledger's tables and the system pages
    // behind them. Workspaces: each registered compute scratch (inLedger true when its region lives in the Ledger)
    // and NativeCore workspace (inLedger false) with its capacity rows and bytes. A workspace grows to the largest
    // demand it has seen and does not shrink while registered, so its capacity is also its peak.
    private sealed record NativeMemoryDebugPayload(
        NativeMemoryLedgerTablePayload Ledger,
        global::System.Collections.Generic.IReadOnlyList<global::ResourceManager.App.Infrastructure.Adaptation.ResourceManagerWorkspaceCapacity> Workspaces)
    {
        public global::ResourceManager.ReadWrite.ReadVolatility Volatility => global::ResourceManager.ReadWrite.ReadVolatility.Sampled;
    }

    // GET /api/debug/subscriptions. Sampling: every bound sampling role's current holders, read from its
    // tracker's published source table. Present and Kernel: the ETW holder tables those owners already keep.
    // Live debug read, no lease, no sampling; it may be stale by the time it is used.
    private sealed record SubscriptionHoldersDebugPayload(
        global::System.Collections.Generic.IReadOnlyList<global::ResourceManager.Subscription.SubscriptionRoleHolders> Sampling,
        global::System.Collections.Generic.IReadOnlyList<global::ResourceManager.App.Infrastructure.Telemetry.Etw.PresentFrameEtwSource.PresentHolder> Present,
        KernelEtwHoldersPayload Kernel)
    {
        public global::ResourceManager.ReadWrite.ReadVolatility Volatility
            => global::ResourceManager.ReadWrite.ReadVolatility.EventDriven("subscriptions-changed");
    }

    private sealed record KernelEtwHoldersPayload(
        string State,
        string EnabledKeywords,
        global::System.Collections.Generic.IReadOnlyList<global::ResourceManager.App.Infrastructure.Telemetry.Etw.KernelEtwHolderView> Holders);

    private sealed record NativeMemoryLedgerTablePayload(
        ulong RegisteredBytes,
        ulong SystemPageBytes);

    private sealed record DebugSchedulingPayload(
        global::ResourceManager.App.Domain.Metrics.SchedulingGpuInventorySnapshot? gpuInventory,
        global::ResourceManager.App.Domain.ResourceBreakdown.SchedulingProcessFactSnapshot? processFacts,
        GpuPlacementFailurePayload[]? gpuPlacementFailures);

    private sealed record GpuPlacementFailurePayload(
        int ProcessId,
        ulong ProcessStartKey,
        global::ResourceManager.App.Infrastructure.GpuPlacement.RuntimeGpuProviderInjectionResult? failure);

    private sealed record FrameTimingStatePayload(
        bool running,
        global::ResourceManager.ReadWrite.Windows.Frames.FrameTimingSnapshot snapshot);

    private sealed record RunningStatePayload(
        bool running);

    private sealed record GpuRendererConfirmationPayload(
        string source,
        GpuRendererProcessPayload[]? processes);

    private sealed record GpuRendererProcessPayload(
        int ProcessId,
        ulong ProcessStartKey,
        string ProcessName,
        global::System.Collections.Generic.IReadOnlyList<global::ResourceManager.App.Infrastructure.GpuPlacement.External.WindowsGpuRendererConfirmation.AdapterClient> adapters);

    private sealed record NvidiaControlDiagnosticPayload(
        string Id,
        string DisplayName,
        int? AdapterIndex,
        bool HandleFound,
        global::ResourceManager.ReadWrite.Vendors.Nvidia.NvidiaPowerLimitRange? Range,
        double? CoreOffsetMhz,
        double? MemoryOffsetMhz,
        global::System.Collections.Generic.IReadOnlyList<object>? Dump,
        global::ResourceManager.ReadWrite.Vendors.Nvidia.NvidiaPowerLimitWatts? PowerLimit);

    private sealed record MetricScalePayload(
        string MetricId,
        string ScaleMode);

    private sealed record GpuApplicationIdentityPayload(
        string ProcessKey,
        string? ExecutablePath,
        string? Architecture,
        global::ResourceManager.App.Domain.GpuPlacement.GpuGraphicsApi? GraphicsApi);

    private sealed record SqliteErrorPayload(
        string error,
        int code,
        int extendedCode,
        string message);

    private sealed record EditableReportRulePayload(
        string Name,
        string ReportType,
        string Comparison,
        string DisplayUnit,
        double Scale,
        double Step,
        double ThresholdMinimum,
        double ThresholdMaximum,
        uint CountMaximum,
        double? ZeroActivityMaximum,
        global::System.Collections.Immutable.ImmutableArray<string> EditableFields,
        global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerReportRuleValues Defaults);

    private sealed record EditableReportRulesPayload(
        global::System.Collections.Generic.IEnumerable<EditableReportRulePayload> Rules);

    private sealed record EndpointMessagePayload(
        string message);

    private sealed record EndpointErrorCodePayload(
        string errorCode);

    // GET /api/settings/memory-cpu-tier (design memory_cpu_tiers_2026-10-30 section 5).
    private sealed record MemoryCpuTierPayload(
        int Tier,
        global::System.Collections.Generic.IReadOnlyList<MemoryCpuTierOptionPayload> Options,
        MemoryCpuTierRuntimeConfigPayload RuntimeConfig,
        global::System.Collections.Generic.IReadOnlyList<MemoryCpuTierIgnoredPayload> Ignored);

    private sealed record MemoryCpuTierOptionPayload(
        string Name,
        global::System.Text.Json.JsonElement Value,
        string? Unit,
        MemoryCpuTierRangePayload Range,
        global::System.Collections.Generic.IReadOnlyList<MemoryCpuTierCellPayload> Tiers,
        string Category,
        string Source,
        string Apply,
        [property: global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        global::System.Text.Json.JsonElement? RunningValue,
        [property: global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        bool? PendingRestart);

    private sealed record MemoryCpuTierRangePayload(
        string Kind,
        [property: global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        long? Min,
        [property: global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        long? Max,
        [property: global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        bool? Unset,
        [property: global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        global::System.Collections.Generic.IReadOnlyList<string>? Choices);

    private sealed record MemoryCpuTierCellPayload(
        global::System.Text.Json.JsonElement Value,
        string Category);

    private sealed record MemoryCpuTierRuntimeConfigPayload(
        bool Writable,
        string? Reason,
        global::System.Collections.Generic.IReadOnlyList<MemoryCpuTierRuntimeConfigFilePayload> Files,
        string NativeUiRunningValue,
        string? LastFailure);

    private sealed record MemoryCpuTierRuntimeConfigFilePayload(
        string Process,
        string Path);

    private sealed record MemoryCpuTierIgnoredPayload(
        string Code,
        string Path);

}
