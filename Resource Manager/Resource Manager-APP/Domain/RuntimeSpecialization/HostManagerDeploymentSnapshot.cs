namespace ResourceManager.App.Domain.RuntimeSpecialization;

public enum HostManagerModuleKind : byte
{
    SharedResources = 1,
    ResourceScheduler = 2,
    SmartCoordinator = 3,
    MemoryCleanup = 4,
    PlacementCoordinator = 5,
    AdapterPrivateResourceLedger = 6,
    ProcessPolicyExecutor = 8,
    PdhCollector = 9,
    TransactionJournal = 10,
    AppliedOwnership = 11,
    SamplingSubscription = 12,
    PortableSoftwareRegistry = 13,
    SoftwareIdentityCatalog = 14,
    SoftwareIdentityResolution = 15,
    ReportCoordinator = 16,
    FileQuery = 17,
    PublicServiceCoordinator = 18,
    DisplayCoordinator = 19,
    OperationCoordinator = 20,
    MetricSnapshot = 21
}

public enum HostManagerDeploymentStatus : byte
{
    Unpublished = 0,
    Initializing = 1,
    Pending = 2,
    InSync = 3
}

public enum HostManagerPendingLifecycle : byte
{
    None = 0,
    InitialCreate = 1,
    HotPublish = 2,
    HostRecreate = 3,
    ProcessRestart = 4
}

public enum HostManagerDeploymentHealth : byte
{
    Unknown = 0,
    Healthy = 1,
    Failed = 2
}

public sealed record HostManagerNativeResultSnapshot(
    string Domain,
    long RawCode);

public sealed record HostManagerModuleFailureSnapshot(
    bool Active,
    ulong AttemptId,
    ulong PublicationSequence,
    string StableCode,
    HostManagerNativeResultSnapshot? NativeResult,
    HostManagerDeploymentOperation AttemptedOperation,
    HostManagerPendingLifecycle AttemptedLifecycle,
    ulong AttemptedPlanEpoch,
    string AttemptedBuildSha256,
    string AttemptedRecreateSha256,
    string AttemptedHotPublishSha256,
    DateTimeOffset FirstOccurredAtUtc,
    DateTimeOffset LastOccurredAtUtc,
    uint OccurrenceCount,
    HostManagerFailureResolution Resolution,
    DateTimeOffset? ResolvedAtUtc);

public sealed record HostManagerModuleDeploymentSnapshot(
    HostManagerModuleKind Module,
    HostManagerDeploymentStatus Status,
    HostManagerPendingLifecycle PendingLifecycle,
    HostManagerDeploymentHealth Health,
    ulong DesiredPublicationSequence,
    ulong DesiredPlanEpoch,
    string DesiredBuildSha256,
    string DesiredRecreateSha256,
    string DesiredHotPublishSha256,
    ulong AppliedPublicationSequence,
    ulong AppliedPlanEpoch,
    string AppliedBuildSha256,
    string AppliedRecreateSha256,
    string AppliedHotPublishSha256,
    DateTimeOffset? AppliedAtUtc,
    ulong PendingSincePlanEpoch,
    HostManagerDeploymentAttemptSnapshot? ActiveAttempt,
    ulong LastSettledAttemptId,
    uint StaleCompletionCount,
    HostManagerModuleFailureSnapshot? LastFailure);

public sealed record HostManagerDeploymentSnapshot(
    ulong PublicationSequence,
    long RuntimePlanVersion,
    ulong PublishedPlanEpoch,
    DateTimeOffset CapturedAtUtc,
    HostManagerModuleDeploymentSnapshot SharedResources,
    HostManagerModuleDeploymentSnapshot ResourceScheduler,
    HostManagerModuleDeploymentSnapshot AdapterPrivateResourceLedger,
    HostManagerModuleDeploymentSnapshot SmartCoordinator,
    HostManagerModuleDeploymentSnapshot MemoryCleanup,
    HostManagerModuleDeploymentSnapshot PlacementCoordinator,
    HostManagerModuleDeploymentSnapshot AppliedOwnership,
    HostManagerModuleDeploymentSnapshot TransactionJournal,
    HostManagerModuleDeploymentSnapshot SamplingSubscription,
    HostManagerModuleDeploymentSnapshot PortableSoftwareRegistry,
    HostManagerModuleDeploymentSnapshot SoftwareIdentityCatalog,
    HostManagerModuleDeploymentSnapshot SoftwareIdentityResolution,
    HostManagerModuleDeploymentSnapshot ReportCoordinator,
    HostManagerModuleDeploymentSnapshot FileQuery,
    HostManagerModuleDeploymentSnapshot PublicServiceCoordinator,
    HostManagerModuleDeploymentSnapshot DisplayCoordinator,
    HostManagerModuleDeploymentSnapshot OperationCoordinator,
    HostManagerModuleDeploymentSnapshot MetricSnapshot,
    HostManagerModuleDeploymentSnapshot ProcessPolicyExecutor,
    HostManagerModuleDeploymentSnapshot PdhCollector);
