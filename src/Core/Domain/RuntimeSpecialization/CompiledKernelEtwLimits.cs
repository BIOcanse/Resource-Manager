using System.Text.Json.Serialization;

namespace ResourceManager.App.Domain.RuntimeSpecialization;

// Values are supplied by the compiled backend declaration, never by ABI defaults.
public sealed record CompiledKernelEtwLimits(
    [property: JsonRequired, JsonPropertyName("region_bytes")] ulong RegionBytes,
    [property: JsonRequired, JsonPropertyName("cpu_bytes")] ulong CpuBytes,
    [property: JsonRequired, JsonPropertyName("interrupt_bytes")] ulong InterruptBytes,
    [property: JsonRequired, JsonPropertyName("disk_bytes")] ulong DiskBytes,
    [property: JsonRequired, JsonPropertyName("network_bytes")] ulong NetworkBytes,
    [property: JsonRequired, JsonPropertyName("file_bytes")] ulong FileBytes,
    [property: JsonRequired, JsonPropertyName("history_bytes")] ulong HistoryBytes,
    [property: JsonRequired, JsonPropertyName("maximum_cells")] uint MaximumCells,
    [property: JsonRequired, JsonPropertyName("maximum_processes")] uint MaximumProcesses,
    [property: JsonRequired, JsonPropertyName("maximum_threads")] uint MaximumThreads,
    [property: JsonRequired, JsonPropertyName("maximum_process_name_bytes")] uint MaximumProcessNameBytes,
    [property: JsonRequired, JsonPropertyName("maximum_modules")] uint MaximumModules,
    [property: JsonRequired, JsonPropertyName("maximum_path_units")] uint MaximumPathUnits,
    [property: JsonRequired, JsonPropertyName("maximum_module_identities")] uint MaximumModuleIdentities,
    [property: JsonRequired, JsonPropertyName("maximum_interrupt_slices")] uint MaximumInterruptSlices,
    [property: JsonRequired, JsonPropertyName("maximum_interrupt_drivers")] uint MaximumInterruptDrivers,
    [property: JsonRequired, JsonPropertyName("maximum_disk_pairs")] uint MaximumDiskPairs,
    [property: JsonRequired, JsonPropertyName("maximum_disks")] uint MaximumDisks,
    [property: JsonRequired, JsonPropertyName("maximum_cached_processes")] uint MaximumCachedProcesses,
    [property: JsonRequired, JsonPropertyName("maximum_network_events")] uint MaximumNetworkEvents,
    [property: JsonRequired, JsonPropertyName("maximum_network_processes")] uint MaximumNetworkProcesses,
    [property: JsonRequired, JsonPropertyName("maximum_file_entries")] uint MaximumFileEntries,
    [property: JsonRequired, JsonPropertyName("maximum_file_versions")] uint MaximumFileVersions,
    [property: JsonRequired, JsonPropertyName("maximum_history_entries")] uint MaximumHistoryEntries,
    [property: JsonRequired, JsonPropertyName("maximum_namespace_entries")] uint MaximumNamespaceEntries,
    [property: JsonRequired, JsonPropertyName("maximum_commands")] uint MaximumCommands,
    [property: JsonRequired, JsonPropertyName("maximum_command_name_bytes")] uint MaximumCommandNameBytes,
    [property: JsonRequired, JsonPropertyName("publication_banks")] uint PublicationBanks,
    [property: JsonRequired, JsonPropertyName("io_window_milliseconds")] long IoWindowMilliseconds,
    [property: JsonRequired, JsonPropertyName("io_retention_milliseconds")] long IoRetentionMilliseconds,
    [property: JsonRequired, JsonPropertyName("disk_bucket_milliseconds")] long DiskBucketMilliseconds,
    [property: JsonRequired, JsonPropertyName("maximum_pending_events")] uint MaximumPendingEvents,
    [property: JsonRequired, JsonPropertyName("maximum_pending_path_units")] uint MaximumPendingPathUnits,
    [property: JsonRequired, JsonPropertyName("maximum_renderer_processes")] uint MaximumRendererProcesses)
{
    public bool IsPublished => RegionBytes > 0 && CpuBytes > 0 && InterruptBytes > 0
        && DiskBytes > 0 && NetworkBytes > 0 && FileBytes > 0 && HistoryBytes > 0
        && CpuBytes <= RegionBytes && InterruptBytes <= RegionBytes
        && DiskBytes <= RegionBytes && NetworkBytes <= RegionBytes
        && FileBytes <= RegionBytes && HistoryBytes <= RegionBytes
        && MaximumCells > 0 && MaximumProcesses > 0 && MaximumThreads > 0 && MaximumProcessNameBytes > 0
        && MaximumModules > 0 && MaximumPathUnits > 0 && MaximumModuleIdentities > 0
        && MaximumInterruptSlices > 0 && MaximumInterruptDrivers > 0
        && MaximumDiskPairs > 0 && MaximumDisks > 0 && MaximumCachedProcesses > 0
        && MaximumNetworkEvents > 0 && MaximumNetworkProcesses > 0
        && MaximumFileEntries > 0 && MaximumFileVersions > 0 && MaximumHistoryEntries > 0
        && MaximumNamespaceEntries > 0 && MaximumCommands > 0 && MaximumCommandNameBytes > 0
        && MaximumPendingEvents > 0 && MaximumPendingPathUnits > 0
        && MaximumRendererProcesses > 0
        && PublicationBanks is >= 2 and <= 8
        && IoWindowMilliseconds > 0 && IoRetentionMilliseconds >= IoWindowMilliseconds
        && DiskBucketMilliseconds > 0;
}
