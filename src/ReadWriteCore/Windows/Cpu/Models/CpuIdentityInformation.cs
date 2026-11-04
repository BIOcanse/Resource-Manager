using System.Text.Json.Serialization;

namespace ResourceManager.ReadWrite.Windows.Cpu.Models;

public sealed record CpuIdentityInformation(
    [property: JsonPropertyName("cpu_all_cpuid_vendor")] string Vendor,
    [property: JsonPropertyName("cpu_all_cpuid_family")] int Family,
    [property: JsonPropertyName("cpu_all_cpuid_model")] int Model,
    [property: JsonPropertyName("cpu_all_cpuid_stepping")] int Stepping,
    [property: JsonPropertyName("cpu_all_brand")] string? Brand,
    [property: JsonPropertyName("cpu_all_instruction_feature")] IReadOnlyList<string> HardwareFeatures,
    [property: JsonPropertyName("cpu_all_os_usable_feature")] IReadOnlyList<string> OsUsableFeatures);

public sealed record CpuCacheInformation(
    string DeviceIdentity,
    [property: JsonPropertyName("cpu_all_cache_level")] int Level,
    [property: JsonPropertyName("cpu_all_cache_capacity")] uint CapacityBytes,
    [property: JsonPropertyName("cpu_all_cache_line_size")] ushort LineSizeBytes,
    [property: JsonPropertyName("cpu_all_cache_associativity")] byte Associativity,
    [property: JsonPropertyName("cpu_all_cache_type")] int NativeCacheType,
    [property: JsonPropertyName("cpu_all_cache_shared_logical_processor_ids")] IReadOnlyList<int> LogicalProcessorIds);
