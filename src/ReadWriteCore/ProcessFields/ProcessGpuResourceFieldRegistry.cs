using System.Globalization;
using ResourceManager.ReadWrite.Native.Pdh;

namespace ResourceManager.ReadWrite.ProcessFields;

/// <summary>Names existing GPU observations by their exact adapter and engine scope.
/// Discovery and parsing never collect a value.</summary>
public static class ProcessGpuResourceFieldRegistry
{
    public const string ResidentBytes = "process_all_gpu_resident_bytes_attributed";
    public const string ResidentPrivateBytes = "process_all_gpu_resident_private_bytes_attributed";
    public const string ResidentSharedBytes = "process_all_gpu_resident_shared_bytes_attributed";
    public const string AllocationSourceId = "windows.etw.gpu-allocation";
    public const string AllocationDatasetId = "process.gpu.resident-fields";

    public static IReadOnlyList<ProcessMetricFieldDefinition> Describe(
        IEnumerable<(int Index, ulong Luid)> adapters, NativePdhSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        // A conflicting current topology cannot bind a stable public index to a LUID.
        var exact = adapters.Where(static adapter => adapter.Index >= 0 && adapter.Luid != 0)
            .Distinct().GroupBy(static adapter => adapter.Index).Where(static group => group.Count() == 1)
            .Select(static group => group.Single()).GroupBy(static adapter => adapter.Luid)
            .Where(static group => group.Count() == 1).Select(static group => group.Single())
            .OrderBy(static adapter => adapter.Index).ToArray();
        var fields = new List<ProcessMetricFieldDefinition>();
        foreach (var adapter in exact)
        {
            foreach (var code in new[] { ProcessMetricFieldCode.GpuResidentBytes,
                ProcessMetricFieldCode.GpuResidentPrivateBytes, ProcessMetricFieldCode.GpuResidentSharedBytes })
                fields.Add(ForResidentMemory(adapter.Index, adapter.Luid, code));
            if (snapshot is null) continue;
            foreach (var scope in snapshot.EngineRows.Where(row => row.AdapterLuid == adapter.Luid)
                .Select(static row => new ProcessGpuEngineScope(row.AdapterLuid, row.PhysicalIndex, row.EngineIndex))
                .Distinct().OrderBy(static scope => scope.PhysicalIndex).ThenBy(static scope => scope.EngineIndex))
            {
                var classes = snapshot.EngineRows.Where(row => row.AdapterLuid == scope.AdapterLuid
                    && row.PhysicalIndex == scope.PhysicalIndex && row.EngineIndex == scope.EngineIndex)
                    .Select(static row => row.EngineClass).Distinct().ToArray();
                fields.Add(ForEngine(adapter.Index, scope) with { EngineClass = classes.Length == 1 ? classes[0].ToString() : null });
            }
        }
        return fields;
    }

    public static ProcessMetricFieldDefinition ForEngine(int gpuIndex, ProcessGpuEngineScope scope)
    {
        ValidateAdapter(gpuIndex, scope.AdapterLuid);
        return new(ProcessMetricFieldCode.GpuResidency,
            FormattableString.Invariant($"{WindowsPdhProcessGpuFields.FieldName}_gpu_{gpuIndex}_luid_{scope.AdapterLuid:x16}_physical_{scope.PhysicalIndex}_engine_{scope.EngineIndex}"),
            "float64", "%", ReadVolatility.Sampled, "windows.pdh.gpu-engine", ProcessMetricDatasetIds.GpuResidency)
        {
            ResourceFactMetricId = FormattableString.Invariant($"gpu.{gpuIndex}.usage"),
            ResourceFactAdditive = true,
            GpuIndex = gpuIndex,
            GpuAdapterLuid = scope.AdapterLuid,
            GpuEngine = scope,
            SourceFieldName = WindowsPdhProcessGpuFields.FieldName
        };
    }

    public static ProcessMetricFieldDefinition ForResidentMemory(
        int gpuIndex, ulong adapterLuid, ProcessMetricFieldCode code)
    {
        ValidateAdapter(gpuIndex, adapterLuid);
        var sourceName = code switch
        {
            ProcessMetricFieldCode.GpuResidentBytes => ResidentBytes,
            ProcessMetricFieldCode.GpuResidentPrivateBytes => ResidentPrivateBytes,
            ProcessMetricFieldCode.GpuResidentSharedBytes => ResidentSharedBytes,
            _ => throw new ArgumentOutOfRangeException(nameof(code))
        };
        // SharedBytes is an apportioned share of jointly owned dedicated residency,
        // not the Windows shared-system-memory counter. Shares may be fractional bytes.
        return new(code, FormattableString.Invariant($"{sourceName}_gpu_{gpuIndex}_luid_{adapterLuid:x16}"),
            "float64", "B", ReadVolatility.Sampled, AllocationSourceId, AllocationDatasetId)
        {
            ResourceFactMetricId = FormattableString.Invariant($"gpu.{gpuIndex}.vram"),
            ResourceFactAdditive = true,
            GpuIndex = gpuIndex,
            GpuAdapterLuid = adapterLuid,
            SourceFieldName = sourceName
        };
    }

    public static ProcessMetricFieldDefinition? Find(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var marker = name.LastIndexOf("_gpu_", StringComparison.OrdinalIgnoreCase);
        if (marker <= 0) return null;
        var sourceName = name[..marker];
        var parts = name[(marker + 5)..].Split('_');
        if (parts.Length is not (3 or 7)
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var gpuIndex)
            || gpuIndex < 0 || !parts[1].Equals("luid", StringComparison.OrdinalIgnoreCase)
            || parts[2].Length != 16
            || !ulong.TryParse(parts[2], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var luid)
            || luid == 0) return null;
        ProcessMetricFieldDefinition field;
        if (sourceName.Equals(WindowsPdhProcessGpuFields.FieldName, StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length != 7 || !parts[3].Equals("physical", StringComparison.OrdinalIgnoreCase)
                || !parts[5].Equals("engine", StringComparison.OrdinalIgnoreCase)
                || !uint.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var physical)
                || !uint.TryParse(parts[6], NumberStyles.None, CultureInfo.InvariantCulture, out var engine)) return null;
            field = ForEngine(gpuIndex, new(luid, physical, engine));
        }
        else
        {
            if (parts.Length != 3) return null;
            ProcessMetricFieldCode? code = sourceName.ToLowerInvariant() switch
            {
                ResidentBytes => ProcessMetricFieldCode.GpuResidentBytes,
                ResidentPrivateBytes => ProcessMetricFieldCode.GpuResidentPrivateBytes,
                ResidentSharedBytes => ProcessMetricFieldCode.GpuResidentSharedBytes,
                _ => null
            };
            if (code is null) return null;
            field = ForResidentMemory(gpuIndex, luid, code.Value);
        }
        return field.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ? field : null;
    }

    public static bool IsBound(ProcessMetricFieldDefinition field)
        => field.GpuIndex is >= 0 && field.GpuAdapterLuid is > 0
            && (field.Code == ProcessMetricFieldCode.GpuResidency && field.GpuEngine is not null || IsVram(field));

    public static bool IsVram(ProcessMetricFieldDefinition field)
        => field.Code is ProcessMetricFieldCode.GpuResidentBytes or ProcessMetricFieldCode.GpuResidentPrivateBytes
            or ProcessMetricFieldCode.GpuResidentSharedBytes;

    private static void ValidateAdapter(int gpuIndex, ulong luid)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(gpuIndex);
        if (luid == 0) throw new ArgumentOutOfRangeException(nameof(luid));
    }
}
