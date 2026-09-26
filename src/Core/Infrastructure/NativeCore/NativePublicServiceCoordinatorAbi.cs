using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal static class NativePublicServiceCoordinatorAbi
{
    public const uint Version = 0x0003_0000;
}

internal enum NativePublicServiceCoordinatorStatus : int
{
    Ok = 0,
    InvalidArgument = 1,
    AbiMismatch = 2,
    Unavailable = 3,
    NoData = 4,
    BufferTooSmall = 5,
    StaleFrame = 6,
    OutOfMemory = 7
}

[Flags]
internal enum NativePublicServiceCoordinatorConfigurationFlags : ulong
{
    None = 0,
    LoopbackOnly = 1UL << 0,
    Known = LoopbackOnly
}

[Flags]
internal enum NativePublicServiceCapabilityFlags : uint
{
    None = 0,
    Enabled = 1U << 0,
    Available = 1U << 1,
    Known = Enabled | Available
}

[Flags]
internal enum NativePublicServiceRouteFlags : uint
{
    None = 0,
    ExactPath = 1U << 0,
    CatalogRoute = 1U << 1,
    BypassRateLimit = 1U << 2,
    Known = ExactPath | CatalogRoute | BypassRateLimit
}

[Flags]
internal enum NativePublicServiceModelFlags : uint
{
    None = 0,
    Available = 1U << 0,
    LoadedInstance = 1U << 1,
    Known = Available | LoadedInstance
}

[Flags]
internal enum NativePublicServiceMethodMask : uint
{
    None = 0,
    Get = 1U << 0,
    Head = 1U << 1,
    Post = 1U << 2,
    Put = 1U << 3,
    Delete = 1U << 4,
    Patch = 1U << 5,
    Known = Get | Head | Post | Put | Delete | Patch
}

[Flags]
internal enum NativePublicServiceRequestFlags : uint
{
    None = 0,
    Known = None
}

[Flags]
internal enum NativePublicServiceSubscriptionFlags : uint
{
    None = 0,
    ContinuousUse = 1U << 0,
    Known = ContinuousUse
}

internal enum NativePublicServiceRemoteScope : uint
{
    Invalid = 0,
    Loopback = 1,
    Remote = 2,
    Unknown = 3
}

internal enum NativePublicServiceHttpMethod : uint
{
    Invalid = 0,
    Get = 1,
    Head = 2,
    Post = 3,
    Put = 4,
    Delete = 5,
    Patch = 6,
    Other = 7
}

internal enum NativePublicServiceAccessReason : uint
{
    Invalid = 0,
    Allowed = 1,
    RemoteForbidden = 2,
    ServiceDisabled = 3,
    RouteNotFound = 4,
    CapabilityDisabled = 5,
    MethodNotSupported = 6,
    RateLimited = 7,
    CallerInflightLimit = 8,
    CoordinatorCapacity = 9
}

internal enum NativePublicServiceModelResolveStatus : uint
{
    Invalid = 0,
    Matched = 1,
    NotFound = 2,
    CatalogUnavailable = 3
}

internal enum NativePublicServiceModelAcquisitionPlanStatus : uint
{
    Invalid = 0,
    NotDue = 1,
    Start = 2,
    Running = 3
}

internal enum NativePublicServiceModelAcquisitionCompletionStatus : uint
{
    Invalid = 0,
    Unavailable = 1,
    Failed = 2
}

[Flags]
internal enum NativePublicServiceCoordinatorSnapshotFlags : ulong
{
    None = 0,
    ServiceEnabled = 1UL << 0,
    NextWakeValid = 1UL << 1,
    ModelCatalogUsable = 1UL << 2,
    ModelAcquisitionRunning = 1UL << 3,
    Known = ServiceEnabled | NextWakeValid | ModelCatalogUsable | ModelAcquisitionRunning
}

internal enum NativePublicServiceTaskKind : uint
{
    Invalid = 0,
    Load = 1,
    Unload = 2
}

internal enum NativePublicServiceTaskState : uint
{
    Empty = 0,
    Queued = 1,
    Running = 2
}

internal enum NativePublicServiceTaskEffectOutcome : uint
{
    Invalid = 0,
    Succeeded = 1,
    ProviderUnavailable = 2,
    Timeout = 3,
    TransportFailure = 4,
    HttpResponse = 5,
    Rejected = 6,
    Failed = 7,
    Cancelled = 8
}

[Flags]
internal enum NativePublicServiceTaskOutcomeMask : uint
{
    None = 0,
    ProviderUnavailable = 1U << (int)NativePublicServiceTaskEffectOutcome.ProviderUnavailable,
    Timeout = 1U << (int)NativePublicServiceTaskEffectOutcome.Timeout,
    TransportFailure = 1U << (int)NativePublicServiceTaskEffectOutcome.TransportFailure,
    Rejected = 1U << (int)NativePublicServiceTaskEffectOutcome.Rejected,
    Failed = 1U << (int)NativePublicServiceTaskEffectOutcome.Failed,
    Known = ProviderUnavailable
        | Timeout
        | TransportFailure
        | Rejected
        | Failed
}

[Flags]
internal enum NativePublicServiceTaskHttpRetryPolicyMask : uint
{
    None = 0,
    RequestTimeout = 1U << 0,
    Throttled = 1U << 1,
    ServerError = 1U << 2,
    Known = RequestTimeout | Throttled | ServerError
}

internal enum NativePublicServiceTaskCompletionDisposition : uint
{
    Invalid = 0,
    Succeeded = 1,
    TerminalFailure = 2,
    RetryScheduled = 3
}

internal enum NativePublicServiceRequestCompletionDisposition : uint
{
    Invalid = 0,
    Completed = 1,
    Expired = 2
}

[Flags]
internal enum NativePublicServiceTaskPlanFlags : ulong
{
    None = 0,
    NextWakeValid = 1UL << 0,
    MoreReady = 1UL << 1,
    Known = NextWakeValid | MoreReady
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePublicServiceTextSpan
{
    public uint Offset;
    public uint Length;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePublicServiceCoordinatorConfiguration
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong Generation;
    public ulong SessionInstanceLow;
    public ulong SessionInstanceHigh;
    public uint MaximumCapabilityCount;
    public uint MaximumRouteCount;
    public uint MaximumModelCount;
    public uint MaximumModelAliasCount;
    public uint MaximumRequestCount;
    public uint MaximumRateBucketCount;
    public uint MaximumLeaseCount;
    public uint MaximumSubscriptionCount;
    public uint MaximumTaskCount;
    public uint CapabilityIndexCapacity;
    public uint ModelIndexCapacity;
    public uint AliasIndexCapacity;
    public uint RequestIndexCapacity;
    public uint RateBucketIndexCapacity;
    public uint LeaseIndexCapacity;
    public uint SubscriptionIndexCapacity;
    public uint TaskIndexCapacity;
    public uint MaximumCatalogTextBytes;
    public uint MaximumModelTextBytes;
    public uint MaximumConcurrentModelTasks;
    public uint MaximumRequestsPerRateWindow;
    public uint MaximumInflightRequestsPerCaller;
    public uint RetryableTaskOutcomeMask;
    public ulong RateWindowMilliseconds;
    public ulong RequestTimeoutMilliseconds;
    public ulong LeaseTimeoutMilliseconds;
    public ulong SubscriptionTimeoutMilliseconds;
    public ulong TaskTimeoutMilliseconds;
    public ulong RetryDelayMilliseconds;
    public ulong ResidentByteBudget;
    public ulong Flags;
    public ulong ModelCatalogAcquisitionIntervalMilliseconds;
    public ulong ModelCatalogLastGoodLifetimeMilliseconds;
    public ulong ModelCatalogAcquisitionTimeoutMilliseconds;
    public uint MaximumTaskAttemptCount;
    public uint RetryableHttpStatusPolicyMask;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePublicServiceCoordinatorCapacity
{
    public uint StructSize;
    public uint CapabilityCapacity;
    public uint RouteCapacity;
    public uint ModelCapacity;
    public uint ModelAliasCapacity;
    public uint RequestCapacity;
    public uint RateBucketCapacity;
    public uint LeaseCapacity;
    public uint SubscriptionCapacity;
    public uint TaskCapacity;
    public uint CapabilityIndexCapacity;
    public uint ModelIndexCapacity;
    public uint AliasIndexCapacity;
    public uint RequestIndexCapacity;
    public uint RateBucketIndexCapacity;
    public uint LeaseIndexCapacity;
    public uint SubscriptionIndexCapacity;
    public uint TaskIndexCapacity;
    public uint CatalogTextCapacity;
    public uint ModelTextCapacity;
    public ulong ResidentByteCount;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePublicServiceCapabilityInput
{
    public uint StructSize;
    public uint Flags;
    public ulong CapabilityHandle;
    public ulong PayloadHandle;
    public ulong Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePublicServiceRouteInput
{
    public uint StructSize;
    public uint Flags;
    public ulong RouteHandle;
    public ulong CapabilityHandle;
    public NativePublicServiceTextSpan Path;
    public uint MethodMask;
    public uint ReservedU32;
    public ulong PayloadHandle;
    public ulong Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePublicServiceCatalogReplaceInput
{
    public uint StructSize;
    public uint ServiceEnabled;
    public ulong CommandEpoch;
    public ulong CommandMonotonicMilliseconds;
    public ulong CatalogGeneration;
    public uint CapabilityCount;
    public uint RouteCount;
    public uint TextByteCount;
    public uint ReservedU32;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePublicServiceModelInput
{
    public uint StructSize;
    public uint Flags;
    public ulong ModelHandle;
    public ulong ProviderHandle;
    public ulong PayloadHandle;
    public ulong Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePublicServiceModelAliasInput
{
    public uint StructSize;
    public uint Flags;
    public ulong ModelHandle;
    public NativePublicServiceTextSpan Alias;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePublicServiceModelReplaceInput
{
    public uint StructSize;
    public uint ReservedU32;
    public ulong CommandEpoch;
    public ulong CommandMonotonicMilliseconds;
    public ulong ModelGeneration;
    public uint ModelCount;
    public uint AliasCount;
    public uint TextByteCount;
    public uint ReservedU32_2;
    public ulong AcquisitionAttemptHandle;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePublicServiceModelAcquisitionPlanInput
{
    public uint StructSize;
    public uint ReservedU32;
    public ulong CommandEpoch;
    public ulong CommandMonotonicMilliseconds;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePublicServiceModelAcquisitionPlanOutput
{
    public uint StructSize;
    public uint Status;
    public ulong AttemptHandle;
    public ulong StartedAtMonotonicMilliseconds;
    public ulong DeadlineMonotonicMilliseconds;
    public ulong NextWakeMonotonicMilliseconds;
    public ulong ModelGenerationAtStart;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePublicServiceModelAcquisitionCompletionInput
{
    public uint StructSize;
    public uint Status;
    public ulong CommandEpoch;
    public ulong CommandMonotonicMilliseconds;
    public ulong AttemptHandle;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePublicServiceAccessInput
{
    public uint StructSize;
    public uint Flags;
    public ulong CommandEpoch;
    public ulong CommandMonotonicMilliseconds;
    public ulong CallerHandle;
    public uint RemoteScope;
    public uint Method;
    public NativePublicServiceTextSpan Path;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePublicServiceAccessOutput
{
    public uint StructSize;
    public uint Allowed;
    public uint StatusCode;
    public uint Reason;
    public ulong RequestHandle;
    public ulong RouteHandle;
    public ulong CapabilityHandle;
    public ulong CatalogGeneration;
    public ulong ExpiresAtMonotonicMilliseconds;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePublicServiceRequestCompleteInput
{
    public uint StructSize;
    public uint ReservedU32;
    public ulong CommandEpoch;
    public ulong CommandMonotonicMilliseconds;
    public ulong RequestHandle;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePublicServiceRequestCompleteOutput
{
    public uint StructSize;
    public uint Disposition;
    public ulong RequestHandle;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePublicServiceModelResolveInput
{
    public uint StructSize;
    public uint ReservedU32;
    public ulong CommandEpoch;
    public ulong CommandMonotonicMilliseconds;
    public NativePublicServiceTextSpan Alias;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePublicServiceModelResolveOutput
{
    public uint StructSize;
    public uint Status;
    public ulong ModelHandle;
    public ulong ProviderHandle;
    public ulong PayloadHandle;
    public ulong ModelGeneration;
    public uint ModelFlags;
    public uint ReservedU32;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePublicServiceLeaseBeginInput
{
    public uint StructSize;
    public uint ReservedU32;
    public ulong CommandEpoch;
    public ulong CommandMonotonicMilliseconds;
    public ulong CallerHandle;
    public ulong ModelHandle;
    public ulong RequestedTimeoutMilliseconds;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePublicServiceLeaseOutput
{
    public uint StructSize;
    public uint ReservedU32;
    public ulong LeaseHandle;
    public ulong ModelHandle;
    public ulong CallerHandle;
    public ulong ExpiresAtMonotonicMilliseconds;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePublicServiceLeaseEndInput
{
    public uint StructSize;
    public uint ReservedU32;
    public ulong CommandEpoch;
    public ulong CommandMonotonicMilliseconds;
    public ulong LeaseHandle;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePublicServiceSubscriptionUpsertInput
{
    public uint StructSize;
    public uint Flags;
    public ulong CommandEpoch;
    public ulong CommandMonotonicMilliseconds;
    public ulong SubscriptionHandle;
    public ulong CallerHandle;
    public ulong ModelHandle;
    public long BaseScore;
    public ulong RequestedTimeoutMilliseconds;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePublicServiceSubscriptionOutput
{
    public uint StructSize;
    public uint Flags;
    public ulong SubscriptionHandle;
    public ulong CallerHandle;
    public ulong ModelHandle;
    public long BaseScore;
    public ulong ExpiresAtMonotonicMilliseconds;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePublicServiceSubscriptionRemoveInput
{
    public uint StructSize;
    public uint ReservedU32;
    public ulong CommandEpoch;
    public ulong CommandMonotonicMilliseconds;
    public ulong SubscriptionHandle;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePublicServiceTaskEnqueueInput
{
    public uint StructSize;
    public uint Flags;
    public ulong CommandEpoch;
    public ulong CommandMonotonicMilliseconds;
    public ulong CallerHandle;
    public ulong ModelHandle;
    public ulong PayloadHandle;
    public long BaseScore;
    public uint Kind;
    public uint ReservedU32;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePublicServiceTaskOutput
{
    public uint StructSize;
    public uint Flags;
    public ulong TaskHandle;
    public ulong CallerHandle;
    public ulong ModelHandle;
    public ulong PayloadHandle;
    public long BaseScore;
    public ulong EnqueuedAtMonotonicMilliseconds;
    public ulong DeadlineMonotonicMilliseconds;
    public uint Kind;
    public uint State;
    public uint Attempt;
    public uint ReservedU32;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePublicServiceTaskPlanInput
{
    public uint StructSize;
    public uint MaximumOutputCount;
    public ulong CommandEpoch;
    public ulong CommandMonotonicMilliseconds;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePublicServiceTaskPlanOutput
{
    public uint StructSize;
    public uint OutputCount;
    public ulong NextWakeMonotonicMilliseconds;
    public ulong Flags;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePublicServiceTaskCompletionInput
{
    public uint StructSize;
    public uint Outcome;
    public ulong CommandEpoch;
    public ulong CommandMonotonicMilliseconds;
    public ulong TaskHandle;
    public uint Attempt;
    public uint HttpStatusCode;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePublicServiceTaskCompletionOutput
{
    public uint StructSize;
    public uint Disposition;
    public ulong TaskHandle;
    public uint Attempt;
    public uint ReservedU32;
    public ulong NextWakeMonotonicMilliseconds;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePublicServiceTaskCancelInput
{
    public uint StructSize;
    public uint MaximumOutputCount;
    public ulong CommandEpoch;
    public ulong CommandMonotonicMilliseconds;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePublicServiceTaskCancelOutput
{
    public uint StructSize;
    public uint OutputCount;
    public uint RemainingTaskCount;
    public uint ReservedU32;
    public fixed ulong Reserved[4];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePublicServiceCapabilityOutput
{
    public uint StructSize;
    public uint Flags;
    public ulong CapabilityHandle;
    public ulong PayloadHandle;
    public ulong CatalogGeneration;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePublicServiceCoordinatorSnapshot
{
    public uint StructSize;
    public uint ReservedU32;
    public ulong Generation;
    public ulong SessionInstanceLow;
    public ulong SessionInstanceHigh;
    public ulong LastCommandEpoch;
    public ulong LastCommandMonotonicMilliseconds;
    public ulong CatalogGeneration;
    public ulong ModelGeneration;
    public uint CapabilityCount;
    public uint RouteCount;
    public uint ModelCount;
    public uint AliasCount;
    public uint RequestCount;
    public uint RateBucketCount;
    public uint LeaseCount;
    public uint SubscriptionCount;
    public uint QueuedTaskCount;
    public uint RunningTaskCount;
    public ulong NextWakeMonotonicMilliseconds;
    public ulong Flags;
    public fixed ulong Reserved[3];
}
