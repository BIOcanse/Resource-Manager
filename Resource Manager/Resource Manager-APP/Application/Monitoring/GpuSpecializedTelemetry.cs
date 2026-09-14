namespace ResourceManager.App.Application.Monitoring;

using System.Text.RegularExpressions;

public enum GpuArchitectureFamily : byte
{
    Unknown = 0,
    NvidiaCudaOnly = 1,
    NvidiaCudaTensor = 2,
    NvidiaRtCudaTensor = 3,
    AmdGcn = 10,
    AmdRdna = 11,
    AmdCdna = 12
}

public enum GpuSpecializedUsageKind : byte
{
    NvidiaRt = 1,
    NvidiaCuda = 2,
    NvidiaTensor = 3,
    AmdGcn = 10,
    AmdRdna = 11,
    AmdCdna = 12
}

public static partial class GpuSpecializedTelemetry
{
    public const uint NvidiaVendorId = 0x10DE;
    public const uint AmdVendorId = 0x1002;

    public static GpuArchitectureFamily ClassifyArchitecture(uint vendorId, string adapterName)
    {
        return ClassifyArchitecture(vendorId, adapterName, null);
    }

    public static GpuArchitectureFamily ClassifyArchitecture(uint vendorId, string adapterName, bool? isIntegrated)
    {
        var name = adapterName ?? string.Empty;
        if (vendorId == NvidiaVendorId || ContainsAny(name, "NVIDIA", "GeForce", "Quadro", "RTX", "GTX", "Tesla"))
        {
            return ClassifyNvidia(name);
        }

        if (vendorId == AmdVendorId || ContainsAny(name, "AMD", "Radeon", "Instinct", "FirePro"))
        {
            return ClassifyAmd(name, isIntegrated);
        }

        return GpuArchitectureFamily.Unknown;
    }

    public static IReadOnlyList<GpuSpecializedUsageKind> GetSupportedUsageKinds(GpuArchitectureFamily architecture)
    {
        return architecture switch
        {
            GpuArchitectureFamily.NvidiaCudaOnly => [GpuSpecializedUsageKind.NvidiaCuda],
            GpuArchitectureFamily.NvidiaCudaTensor => [GpuSpecializedUsageKind.NvidiaCuda, GpuSpecializedUsageKind.NvidiaTensor],
            GpuArchitectureFamily.NvidiaRtCudaTensor => [GpuSpecializedUsageKind.NvidiaRt, GpuSpecializedUsageKind.NvidiaCuda, GpuSpecializedUsageKind.NvidiaTensor],
            GpuArchitectureFamily.AmdGcn => [GpuSpecializedUsageKind.AmdGcn],
            GpuArchitectureFamily.AmdRdna => [GpuSpecializedUsageKind.AmdRdna],
            GpuArchitectureFamily.AmdCdna => [GpuSpecializedUsageKind.AmdCdna],
            _ => []
        };
    }

    public static string BuildCounterId(int adapterIndex, GpuSpecializedUsageKind kind)
    {
        return kind switch
        {
            GpuSpecializedUsageKind.NvidiaRt => $"gpu.{adapterIndex}.nvidia.rt.usage",
            GpuSpecializedUsageKind.NvidiaCuda => $"gpu.{adapterIndex}.nvidia.cuda.usage",
            GpuSpecializedUsageKind.NvidiaTensor => $"gpu.{adapterIndex}.nvidia.tensor.usage",
            GpuSpecializedUsageKind.AmdGcn => $"gpu.{adapterIndex}.amd.gcn.usage",
            GpuSpecializedUsageKind.AmdRdna => $"gpu.{adapterIndex}.amd.rdna.usage",
            GpuSpecializedUsageKind.AmdCdna => $"gpu.{adapterIndex}.amd.cdna.usage",
            _ => $"gpu.{adapterIndex}.unknown.specialized.usage"
        };
    }

    public static string GetDisplayName(GpuSpecializedUsageKind kind)
    {
        return kind switch
        {
            GpuSpecializedUsageKind.NvidiaRt => "RT 占用",
            GpuSpecializedUsageKind.NvidiaCuda => "CUDA 占用",
            GpuSpecializedUsageKind.NvidiaTensor => "Tensor 占用",
            GpuSpecializedUsageKind.AmdGcn => "GCN 占用",
            GpuSpecializedUsageKind.AmdRdna => "RDNA 占用",
            GpuSpecializedUsageKind.AmdCdna => "CDNA 占用",
            _ => "GPU specialized usage"
        };
    }

    public static string GetArchitectureName(GpuArchitectureFamily architecture)
    {
        return architecture switch
        {
            GpuArchitectureFamily.NvidiaCudaOnly => "NVIDIA CUDA-only",
            GpuArchitectureFamily.NvidiaCudaTensor => "NVIDIA CUDA + Tensor",
            GpuArchitectureFamily.NvidiaRtCudaTensor => "NVIDIA RT + CUDA + Tensor",
            GpuArchitectureFamily.AmdGcn => "AMD GCN",
            GpuArchitectureFamily.AmdRdna => "AMD RDNA",
            GpuArchitectureFamily.AmdCdna => "AMD CDNA",
            _ => "Unknown"
        };
    }

    public static bool ShouldIncludeCounter(IReadOnlySet<string>? requestedCounterIds, int adapterIndex, GpuSpecializedUsageKind kind)
    {
        return requestedCounterIds is null || requestedCounterIds.Count == 0 || requestedCounterIds.Contains(BuildCounterId(adapterIndex, kind));
    }

    public static bool IsRtEngineType(string engineType)
    {
        return ContainsAny(engineType, "raytracing", "ray_tracing", "ray tracing", "rt");
    }

    public static bool IsCudaEngineType(string engineType)
    {
        return ContainsAny(engineType, "compute", "cuda");
    }

    private static GpuArchitectureFamily ClassifyNvidia(string name)
    {
        if (ContainsAny(name, "RTX", "GeForce RTX", "Quadro RTX", "RTX A", "RTX PRO"))
        {
            return GpuArchitectureFamily.NvidiaRtCudaTensor;
        }

        if (ContainsAny(name, "A100", "A800", "H100", "H200", "B100", "B200", "V100", "Tesla V", "Tesla A", "Tesla H"))
        {
            return GpuArchitectureFamily.NvidiaCudaTensor;
        }

        return GpuArchitectureFamily.NvidiaCudaOnly;
    }

    private static GpuArchitectureFamily ClassifyAmd(string name, bool? isIntegrated)
    {
        if (ContainsAny(name, "Instinct", "MI100", "MI200", "MI250", "MI300", "MI325", "CDNA"))
        {
            return GpuArchitectureFamily.AmdCdna;
        }

        if (isIntegrated == true)
        {
            return IsKnownRdnaIntegratedAmdGpu(name)
                ? GpuArchitectureFamily.AmdRdna
                : GpuArchitectureFamily.AmdGcn;
        }

        var rxMatch = AmdRxModelPattern().Match(name);
        if (rxMatch.Success && int.TryParse(rxMatch.Groups[1].Value, out var rxModel))
        {
            return rxModel >= 5000 ? GpuArchitectureFamily.AmdRdna : GpuArchitectureFamily.AmdGcn;
        }

        if (ContainsAny(name, "RDNA") || IsKnownRdnaIntegratedAmdGpu(name))
        {
            return GpuArchitectureFamily.AmdRdna;
        }

        return GpuArchitectureFamily.AmdGcn;
    }

    private static bool IsKnownRdnaIntegratedAmdGpu(string name)
    {
        return ContainsAny(
            name,
            "610M",
            "660M",
            "680M",
            "740M",
            "760M",
            "780M",
            "840M",
            "860M",
            "880M",
            "890M");
    }

    private static bool ContainsAny(string text, params ReadOnlySpan<string> tokens)
    {
        foreach (var token in tokens)
        {
            if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"\bRX\s+(\d{3,4})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AmdRxModelPattern();
}
