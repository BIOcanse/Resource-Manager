using System.Text.Json.Serialization;

namespace ResourceManager.App.Domain.Optimization;

public static class HostManagerAppliedRecordKinds
{
    public const string AdapterPolicy = "AdapterPolicy";
    public const string GpuPreference = "GpuPreference";
    public const string GpuShimPolicy = "GpuShimPolicy";
    public const string GpuRuntimeRebuildTrigger = "GpuRuntimeRebuildTrigger";
    public const string GpuRemoteCall = "GpuRemoteCall";
    public const string CpuAffinity = "CpuAffinity";
    public const string CpuThreadCpuSets = "CpuThreadCpuSets";
    public const string ProcessPriority = "ProcessPriority";
    public const string ProcessMemoryPriority = "ProcessMemoryPriority";
    public const string A1 = "A1";
    public const string Level1 = "Level1";
    public const string Level2 = "Level2";
    public const string Level3 = "Level3";
    public const string Level4 = "Level4";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HostManagerRollbackStateDocument(
    int Version,
    DateTimeOffset? LastRunAt,
    DateTimeOffset? LastRestoreAt,
    string Message,
    IReadOnlyList<HostManagerAppliedPlacementReceipt> AppliedPlacements)
{
    public const int CurrentVersion = 2;

    public ulong NativeHostSessionIncarnation { get; init; }

    public static HostManagerRollbackStateDocument Empty { get; } = new(
        CurrentVersion,
        null,
        null,
        "普通模式",
        []);
}

public sealed record HostManagerAppliedRecord(
    string Kind,
    string RecordId,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record HostManagerAppliedPlacementReceipt(
    string TargetId,
    string DisplayName,
    string? SoftwareId,
    string ResourceKind,
    IReadOnlyList<HostManagerAppliedRecord> Records,
    DateTimeOffset AppliedAt,
    DateTimeOffset UpdatedAt);
