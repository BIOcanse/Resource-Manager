using System.Text.Json.Serialization;

namespace ResourceManager.Adapter;

public enum AdapterResourceTier : byte
{
    Vram = 0,
    PhysicalMemory = 1,
    VirtualMemory = 2
}

public enum AdapterResourceKind : byte
{
    PrimaryData = 0,
    Cache = 1,
    Index = 2,
    ModelWeights = 3,
    MediaResource = 4,
    EditingDocumentState = 5,
    StagingBuffer = 6,
    TemporaryComputeMemory = 7,
    RuntimeOverhead = 8,
    RenderSurface = 9,
    Texture = 10,
    RenderBuffer = 11,
    ComputeBuffer = 12
}

public enum AdapterResourceRecoveryKind : byte
{
    DiskCopy = 0,
    BuiltData = 1,
    LiveState = 2
}

public enum AdapterResourceGranularity : byte
{
    FullyLoaded = 0,
    PartialUsable = 1,
    NotApplicable = 2
}

public enum AdapterResourceActionRoute : byte
{
    ManagerDirect = 0,
    AdapterHandler = 1
}

public enum AdapterSoftwareSurfaceState : byte
{
    ForegroundFocused = 0,
    ForegroundUnfocused = 1,
    BackgroundWindow = 2,
    TrayBackground = 3,
    PureBackground = 4
}

[Flags]
public enum AdapterResourceActionMask : byte
{
    None = 0,
    Discard = 1 << 0,
    Trim = 1 << 1,
    MoveDown = 1 << 2,
    MoveUp = 1 << 3
}

[Flags]
public enum AdapterResourceDemandMask : byte
{
    None = 0,
    RequiredNow = 1 << 0,
    ReadySoon = 1 << 1,
    PreloadEager = 1 << 2,
    PreloadOpportunistic = 1 << 3
}

public sealed class AdapterResourceSnapshot
{
    [JsonConstructor]
    public AdapterResourceSnapshot(
        byte schemaVersion,
        string applicationId,
        string applicationName,
        int processId,
        AdapterSoftwareSurfaceState surfaceState,
        long sequence,
        DateTimeOffset capturedAt,
        IReadOnlyList<TieredResourceEntry> resources)
    {
        SchemaVersion = schemaVersion;
        ApplicationId = applicationId;
        ApplicationName = applicationName;
        ProcessId = processId;
        SurfaceState = surfaceState;
        Sequence = sequence;
        CapturedAt = capturedAt;
        Resources = resources ?? throw new ArgumentNullException(nameof(resources));
    }

    public byte SchemaVersion { get; }
    public string ApplicationId { get; }
    public string ApplicationName { get; }
    public int ProcessId { get; }
    public AdapterSoftwareSurfaceState SurfaceState { get; }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public long Sequence { get; }
    public DateTimeOffset CapturedAt { get; }
    public IReadOnlyList<TieredResourceEntry> Resources { get; }
}

public readonly struct TieredResourceEntry
{
    [JsonConstructor]
    public TieredResourceEntry(
        ulong resourceKey,
        uint resourceId,
        ulong sizeBytes,
        AdapterResourceTier tier,
        AdapterResourceKind resourceKind,
        AdapterResourceRecoveryKind recoveryKind,
        AdapterResourceGranularity granularity,
        AdapterResourceActionMask inapplicableActions,
        AdapterResourceActionRoute actionRoute,
        byte activityScore,
        AdapterResourceDemandMask frontendDemandMask)
    {
        ResourceKey = resourceKey;
        ResourceId = resourceId;
        SizeBytes = sizeBytes;
        Tier = tier;
        ResourceKind = resourceKind;
        RecoveryKind = recoveryKind;
        Granularity = granularity;
        InapplicableActions = inapplicableActions;
        ActionRoute = actionRoute;
        ActivityScore = activityScore;
        FrontendDemandMask = frontendDemandMask;
    }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public ulong ResourceKey { get; }
    public uint ResourceId { get; }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public ulong SizeBytes { get; }
    public AdapterResourceTier Tier { get; }
    public AdapterResourceKind ResourceKind { get; }
    public AdapterResourceRecoveryKind RecoveryKind { get; }
    public AdapterResourceGranularity Granularity { get; }
    public AdapterResourceActionMask InapplicableActions { get; }
    public AdapterResourceActionRoute ActionRoute { get; }
    public byte ActivityScore { get; }
    public AdapterResourceDemandMask FrontendDemandMask { get; }
}
