using System.Text.Json.Serialization;

namespace ResourceManager.App.Domain.Optimization;

public static class HostManagerAppliedRecordKinds
{
    public const string AdapterPolicy = "adapter_policy";
    public const string GpuPreference = "gpu_preference";
    public const string GpuShimPolicy = "gpu_shim_policy";
    public const string GpuRuntimeRebuildTrigger = "gpu_runtime_rebuild_trigger";
    public const string GpuRemoteCall = "gpu_remote_call";
    public const string CpuAffinity = "cpu_affinity";
    public const string CpuThreadCpuSets = "cpu_thread_cpu_sets";
    public const string CpuThreadIdealProcessor = "cpu_thread_ideal_processor";
    public const string ProcessPriority = "process_priority";
    public const string ProcessMemoryPriority = "process_memory_priority";
    public const string A1 = "a1";
    public const string Level1 = "level1";
    public const string Level2 = "level2";
    public const string Level3 = "level3";
    public const string Level4 = "level4";

    // Compatibility read for previously persisted rollback records only. Remove after supported
    // rollback state files have been rewritten or retired.
    public static string ReadStored(string? value)
    {
        var token = value?.Replace("_", string.Empty, StringComparison.Ordinal)?.ToUpperInvariant();
        return token switch
        {
            "ADAPTERPOLICY" => AdapterPolicy,
            "GPUPREFERENCE" => GpuPreference,
            "GPUSHIMPOLICY" => GpuShimPolicy,
            "GPURUNTIMEREBUILDTRIGGER" => GpuRuntimeRebuildTrigger,
            "GPUREMOTECALL" => GpuRemoteCall,
            "CPUAFFINITY" => CpuAffinity,
            "CPUTHREADCPUSETS" => CpuThreadCpuSets,
            "CPUTHREADIDEALPROCESSOR" => CpuThreadIdealProcessor,
            "PROCESSPRIORITY" => ProcessPriority,
            "PROCESSMEMORYPRIORITY" => ProcessMemoryPriority,
            "A1" => A1,
            "LEVEL1" => Level1,
            "LEVEL2" => Level2,
            "LEVEL3" => Level3,
            "LEVEL4" => Level4,
            _ => throw new InvalidDataException($"Unsupported stored Host Manager record kind '{value}'.")
        };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HostManagerRollbackStateDocument(
    int Version,
    DateTimeOffset? LastRunAt,
    DateTimeOffset? LastRestoreAt,
    IReadOnlyList<HostManagerAppliedPlacementReceipt> AppliedPlacements)
{
    public const int CurrentVersion = 3;

    public HostManagerCoordinatorFacts? Coordinator { get; init; }

    public ulong NativeHostSessionIncarnation { get; init; }

    public static HostManagerRollbackStateDocument Empty { get; } = new(
        CurrentVersion,
        null,
        null,
        []);
}

public sealed record HostManagerAppliedRecord(
    string Kind,
    string RecordId,
    IReadOnlyDictionary<string, string>? Metadata = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HostManagerAppliedPlacementReceipt(
    string TargetId,
    string? SoftwareId,
    string ResourceKind,
    IReadOnlyList<HostManagerAppliedRecord> Records,
    DateTimeOffset AppliedAt,
    DateTimeOffset UpdatedAt)
{
    public int? ProcessId { get; init; }
    public string? ProcessStartKey { get; init; }
    public string? ProcessName { get; init; }
}
