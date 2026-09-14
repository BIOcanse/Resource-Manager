namespace ResourceManager.App.Domain.GpuPlacement;

public static class GpuLaunchInterceptionStatuses
{
    public const string Disabled = "disabled";
    public const string Registered = "registered";
    public const string ExecutableMissing = "executable-missing";
    public const string ExecutableUnsupported = "executable-unsupported";
    public const string SystemPathBlocked = "system-path-blocked";
    public const string BrokerMissing = "broker-missing";
    public const string ThirdPartyConflict = "third-party-conflict";
    public const string RegistryAccessDenied = "registry-access-denied";
    public const string RegistryError = "registry-error";
    public const string PartialRegistration = "partial-registration";
}

public static class GpuLaunchExecutionOutcomes
{
    public const string PassThroughStarted = "pass-through-started";
    public const string ProviderReady = "provider-ready";
    public const string StartupConfigured = "startup-configured";
    public const string ProviderUnavailableFallback = "provider-unavailable-fallback";
    public const string BootstrapFailedFallback = "bootstrap-failed-fallback";
    public const string ProviderTimeout = "provider-timeout";
    public const string LaunchFailed = "launch-failed";
    public const string DebuggerDetachFailed = "debugger-detach-failed";
    public const string RecursionBlocked = "recursion-blocked";

    public static IReadOnlySet<string> Known { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        PassThroughStarted,
        ProviderReady,
        StartupConfigured,
        ProviderUnavailableFallback,
        BootstrapFailedFallback,
        ProviderTimeout,
        LaunchFailed,
        DebuggerDetachFailed,
        RecursionBlocked
    };
}

public sealed record GpuLaunchExecutionReport(
    string ExecutablePath,
    string? SoftwareId,
    string? ProcessKey,
    int? ProcessId,
    string Outcome,
    string Message,
    string? StartupTargetGpu,
    string? AssignedPositionId,
    string? TargetAdapterName,
    DateTimeOffset OccurredAt);

public sealed record GpuLaunchExecutionReportDocument(
    int Version,
    IReadOnlyList<GpuLaunchExecutionReport> Reports,
    DateTimeOffset UpdatedAt)
{
    public static GpuLaunchExecutionReportDocument Empty { get; } = new(1, [], DateTimeOffset.MinValue);
}

public sealed record GpuLaunchInterceptionStatus(
    string ProcessKey,
    string? ExecutablePath,
    bool Requested,
    bool Registered,
    string Status,
    string Message,
    IReadOnlyList<string> RegisteredViews,
    GpuLaunchExecutionReport? RecentLaunchResult = null);

public sealed record GpuPlacementProcessPolicySaveResult(
    GpuPlacementProcessPolicy Policy,
    GpuLaunchInterceptionStatus? StartupInterception,
    string RuntimeApplicationDisposition,
    string StartupInterceptionDisposition,
    long? RuntimePlanVersion,
    ulong? RuntimePublicationSequence,
    int RuntimeDeliveryFailureCount,
    string? FailureCode);

public sealed record GpuLaunchInterceptionCleanupResult(
    bool Success,
    int RemovedRuleCount,
    string Message);
