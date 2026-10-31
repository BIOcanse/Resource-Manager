using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization.FreedomPoints;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

namespace ResourceManager.ReadWriteNativeDefaultsBuild;

internal static partial class Program
{
    private readonly record struct Field(string Source, string Member, string Type);
    private static readonly Field[] Fields =
    [
        new("region_bytes", "ByteCap", "ulong"), new("cpu_bytes", "Residency.ByteCap", "ulong"),
        new("interrupt_bytes", "Interrupts.ByteCap", "ulong"), new("disk_bytes", "DiskByteCap", "ulong"),
        new("network_bytes", "NetworkByteCap", "ulong"), new("file_bytes", "FileByteCap", "ulong"), new("history_bytes", "HistoryByteCap", "ulong"),
        new("maximum_processes", "Residency.MaximumProcesses", "uint"),
        new("maximum_threads", "Residency.MaximumThreads", "uint"), new("maximum_process_name_bytes", "Residency.MaximumNameBytes", "uint"),
        new("maximum_modules", "Interrupts.MaximumModules", "uint"), new("maximum_path_units", "MaximumPathUnits", "uint"),
        new("maximum_module_identities", "Interrupts.MaximumIdentities", "uint"), new("maximum_interrupt_slices", "Interrupts.MaximumSlices", "uint"),
        new("maximum_interrupt_drivers", "Interrupts.MaximumSliceDrivers", "uint"), new("maximum_disk_pairs", "MaximumDiskPairs", "uint"),
        new("maximum_disks", "MaximumDisks", "uint"), new("maximum_cached_processes", "MaximumCachedProcesses", "uint"),
        new("maximum_network_events", "MaximumNetworkEvents", "uint"), new("maximum_network_processes", "MaximumNetworkProcesses", "uint"),
        new("maximum_file_entries", "MaximumFileEntries", "uint"), new("maximum_file_versions", "MaximumFileVersions", "uint"),
        new("maximum_history_entries", "MaximumHistoryEntries", "uint"), new("maximum_namespace_entries", "MaximumNamespaceEntries", "uint"),
        new("maximum_commands", "MaximumCommands", "uint"), new("maximum_command_name_bytes", "MaximumCommandNameBytes", "uint"),
        new("publication_banks", "PublicationBanks", "uint"), new("io_window_milliseconds", "WindowMilliseconds", "long"),
        new("io_retention_milliseconds", "RetentionMilliseconds", "long"), new("disk_bucket_milliseconds", "DiskBucketMilliseconds", "long"),
        new("maximum_pending_events", "MaximumPendingEvents", "uint"), new("maximum_pending_path_units", "MaximumPendingPathUnits", "uint"),
        new("maximum_renderer_processes", "MaximumRendererProcesses", "uint"),
        new("interrupt_bucket_milliseconds", "Interrupts.BucketMilliseconds", "uint"), new("interrupt_bucket_count", "Interrupts.BucketCount", "uint"),
        new("interrupt_reported_drivers", "Interrupts.ReportedDrivers", "uint"), new("network_initial_events", "NetworkInitialEvents", "uint")
    ];
    // Declared with the limits but sizing the TraceEvent session, not the native configuration.
    private static readonly string[] SessionBufferFields = ["session_buffer_kib", "session_minimum_buffers"];

    private static readonly string[] NativePolicyFields =
    [
        "data_area_borrow_drain_timeout_ms", "initial_record_capacity", "initial_operation_capacity",
        "display_initial_text_bytes", "display_initial_diff_entries", "adapter_lease_maximum_capacity", "adapter_lease_id_attempts",
        "maximum_network_interfaces", "maximum_gpu_engine_rows", "calibration_maximum_corpus_bytes", "calibration_maximum_row_bytes",
        "selected_source_limit", "shared_read_retry_limit", "interrupt_initial_capacity", "interrupt_growth_factor",
        "etw_allocator_cap_divisor", "etw_allocator_requested_divisor", "etw_allocator_alignment_divisor",
        "etw_initial_pending_path_units", "diagnostic_minimum_inventory_bytes", "residency_ring_chunk_bytes", "query_dos_device_units",
        "runtime_rank_focused", "runtime_rank_unfocused", "runtime_rank_background_window", "runtime_rank_tray",
        "runtime_rank_background_process", "runtime_rank_not_running"
    ];

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("Usage: <repository-root> <generated-source-path> | --zig <repository-root> | --check-zig <repository-root>");
            var zigMode = args[0] is "--zig" or "--check-zig";
            var root = Path.GetFullPath(args[zigMode ? 1 : 0]);
            var registry = BackendFreedomPointRegistry.Parse(File.ReadAllBytes(Path.Combine(root, "src/Core/Configuration/FreedomPoints/backend.json")));
            var point = registry.Declarations.EnumeratePoints().Single(p => p.Path.Raw == BackendFreedomPointPositions.KernelEtwLimits);
            if (point.Id != "kernel_etw_limits" || point.Status != "active" || point.ValueType != "object" || point.ValueSource != "registry")
                throw new InvalidDataException("The native kernel ETW limits declaration no longer matches its SDK projection.");
            using var profile = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "src/Core/Configuration/HostManager/default.json")));
            using var cpuDefaults = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "src/Core/Configuration/Cpu/baseline-defaults.json")));
            var cpu = JsonSerializer.SerializeToElement(new CpuInput(cpuDefaults.RootElement.GetProperty("unmatchedRatio").GetDouble(), CompiledCpuCoreWeights.Empty),
                new CpuContext(new JsonSerializerOptions()).Options);
            var facts = registry.Sdk.Resolve(profile.RootElement, cpu);
            var nativePolicyPoint = registry.Declarations.EnumeratePoints().Single(p => p.Id == "native_build_policy");
            var nativePolicy = facts.Require(nativePolicyPoint.Path).Value.Complex!.Value;
            if (nativePolicyPoint.Status != "active" || nativePolicyPoint.ValueSource != "registry" || nativePolicy.ValueKind != JsonValueKind.Object ||
                !nativePolicy.EnumerateObject().Select(static p => p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(NativePolicyFields))
                throw new InvalidDataException("Native build policy fields changed; update the typed SDK projection with its declaration.");
            foreach (var field in NativePolicyFields)
            {
                var number = nativePolicy.GetProperty(field).GetUInt32();
                var rank = field.StartsWith("runtime_rank_", StringComparison.Ordinal);
                if ((rank && number > int.MaxValue) ||
                    (!rank && field != "data_area_borrow_drain_timeout_ms" && number == 0) ||
                    (field == "interrupt_growth_factor" && number < 2) ||
                    (field == "residency_ring_chunk_bytes" && number < 4 * sizeof(long)))
                    throw new InvalidDataException($"Invalid native build policy field: {field}.");
            }
            if (zigMode)
            {
                var zig = new StringBuilder("// <auto-generated/>\n// From backend native_build_policy through FreedomPoint SDK resolution.\n");
                foreach (var field in NativePolicyFields)
                    zig.Append("pub const ").Append(field).Append(" = ").Append(nativePolicy.GetProperty(field).GetUInt32().ToString(CultureInfo.InvariantCulture)).Append(";\n");
                WriteProjection(Path.Combine(root, "src/Core/NativeCore/src/configuration/NativePolicy.g.zig"), zig.ToString(), args[0] == "--check-zig");
                WriteProjection(Path.Combine(root, "src/Shared/SharedSection/src/NativePolicy.g.zig"), zig.ToString(), args[0] == "--check-zig");
                return 0;
            }
            var value = facts.Require(point.Path).Value.Complex!.Value;
            var expected = Fields.Select(static f => f.Source).Concat(SessionBufferFields).ToHashSet(StringComparer.Ordinal);
            if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Select(static p => p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(expected))
                throw new InvalidDataException("Kernel ETW limits fields changed; update the typed SDK projection with its declaration.");
            var source = new StringBuilder("// <auto-generated/>\nnamespace ResourceManager.App.Infrastructure.Telemetry.Etw;\n\ninternal static class KernelEtwNativeDefaults\n{\n");
            source.Append("    [FreedomPoint.FreedomPointBinding(0x").Append(point.Path.Raw.ToString("X16", CultureInfo.InvariantCulture)).Append("UL)]\n");
            source.Append("    internal static NativeKernelEtwSessionConfig SelectedLimits => selectedLimits;\n    private static readonly NativeKernelEtwSessionConfig selectedLimits = Create();\n    private static NativeKernelEtwSessionConfig Create()\n    {\n        NativeKernelEtwSessionConfig value = default;\n");
            foreach (var field in Fields)
            {
                var input = value.GetProperty(field.Source);
                var literal = field.Type switch
                {
                    "ulong" => input.GetUInt64().ToString(CultureInfo.InvariantCulture) + "UL",
                    "uint" => input.GetUInt32().ToString(CultureInfo.InvariantCulture) + "U",
                    "long" => input.GetInt64().ToString(CultureInfo.InvariantCulture) + "L",
                    _ => throw new InvalidDataException("Unsupported native projection type.")
                };
                source.Append("        value.").Append(field.Member).Append(" = ").Append(literal).Append(";\n");
            }
            source.Append("        value.Interrupts.MaximumPathUnits = value.MaximumPathUnits;\n        return value;\n    }\n\n");
            // Part of the same kernel_etw_limits value that SelectedLimits binds; the point has no second generation target.
            source.Append("    // From the same bound kernel_etw_limits value as SelectedLimits.\n");
            source.Append("    internal static EtwSessionBuffers SelectedSessionBuffers { get; } = new(")
                .Append(value.GetProperty(SessionBufferFields[0]).GetInt32().ToString(CultureInfo.InvariantCulture)).Append(", ")
                .Append(value.GetProperty(SessionBufferFields[1]).GetInt32().ToString(CultureInfo.InvariantCulture)).Append(");\n}\n");
            source.Append("\ninternal static class NativePolicyDefaults\n{\n");
            foreach (var field in NativePolicyFields)
            {
                source.Append("    internal const uint ").Append(field).Append(" = ")
                    .Append(nativePolicy.GetProperty(field).GetUInt32().ToString(CultureInfo.InvariantCulture)).Append("U;\n");
            }
            source.Append("    [FreedomPoint.FreedomPointBinding(0x").Append(nativePolicyPoint.Path.Raw.ToString("X16", CultureInfo.InvariantCulture)).Append("UL)]\n")
                .Append("    internal static uint SelectedBorrowDrainTimeoutMilliseconds => data_area_borrow_drain_timeout_ms;\n");
            source.Append("}\n");
            var output = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var text = source.ToString();
            if (!File.Exists(output) || File.ReadAllText(output) != text) File.WriteAllText(output, text, new UTF8Encoding(false));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void WriteProjection(string output, string text, bool check)
    {
        if (check)
        {
            if (!File.Exists(output) || File.ReadAllText(output) != text)
                throw new InvalidDataException($"Stale native policy projection: {output}. Run this generator with --zig.");
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        if (!File.Exists(output) || File.ReadAllText(output) != text)
            File.WriteAllText(output, text, new UTF8Encoding(false));
    }

    private sealed record CpuInput(double baseline_ratio, CompiledCpuCoreWeights core_performance_weights);
    [JsonSerializable(typeof(CpuInput))]
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    private partial class CpuContext : JsonSerializerContext;
}
