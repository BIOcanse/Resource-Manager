using System.Diagnostics;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Monitoring.GpuTelemetry;

public sealed class GpuTelemetryLocalProbe(
    WindowsGpuAdapterOrderMonitoringZone adapterOrderZone,
    PdhGpuEngineMonitoringZone gpuEngineZone,
    NvidiaNvmlMonitoringZone nvidiaNvmlZone)
{
    public async Task<GpuTelemetryWorkerSnapshot> CaptureAsync(
        GpuTelemetryWorkerRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();
        var capturedAt = DateTimeOffset.UtcNow;
        var inventory = adapterOrderZone.ReadInventory(requested: true);
        var adapters = inventory.Adapters;
        var engineUsage = gpuEngineZone.ReadSpecializedUsageByAdapterIndex(adapters);
        var requestedCounters = request.CounterIds.Length == 0
            ? null
            : new HashSet<string>(request.CounterIds, StringComparer.OrdinalIgnoreCase);

        var needsNvidiaCudaFallback = requestedCounters is null
            || requestedCounters.Any(static counterId =>
                counterId.EndsWith(".nvidia.cuda.usage", StringComparison.OrdinalIgnoreCase));
        var nvidiaMetrics = needsNvidiaCudaFallback
            ? await nvidiaNvmlZone.ReadAsync(
                new NvidiaNvmlReadRequest(
                    null,
                    IncludeUsage: true,
                    IncludeGraphicsClocks: false,
                    IncludeMemory: false,
                    IncludeMemoryClock: false,
                    IncludePowerUsage: false,
                    IncludePowerLimit: false,
                    IncludeTemperature: false,
                    IncludeFan: false,
                    IncludeElectricalState: false),
                inventory,
                cancellationToken)
            : [];

        var nvidiaByIndex = nvidiaMetrics.ToDictionary(static gpu => gpu.Index);
        var snapshots = adapters
            .Where(static adapter => !adapter.IsSoftware)
            .Select(adapter => CreateAdapterSnapshot(adapter, engineUsage, nvidiaByIndex, requestedCounters))
            .ToArray();

        stopwatch.Stop();
        return new GpuTelemetryWorkerSnapshot(
            GpuTelemetryWorkerSnapshot.CurrentProtocolVersion,
            0,
            capturedAt,
            0,
            GpuTelemetryWorkerStatus.Online,
            snapshots,
            CreateProviderStates(engineUsage, nvidiaMetrics, needsNvidiaCudaFallback, stopwatch.ElapsedMilliseconds),
            snapshots.Length > 0
                ? "GPU specialized telemetry snapshot captured."
                : "No specialized GPU telemetry counters were available.");
    }

    private static GpuTelemetryAdapterSnapshot CreateAdapterSnapshot(
        WindowsGpuAdapter adapter,
        IReadOnlyDictionary<int, GpuEngineSpecializedUsage> engineUsage,
        IReadOnlyDictionary<int, GpuMetrics> nvidiaMetrics,
        IReadOnlySet<string>? requestedCounters)
    {
        var architecture = GpuSpecializedTelemetry.ClassifyArchitecture(
            adapter.VendorId,
            adapter.Name,
            adapter.Kind == WindowsGpuAdapterKind.Integrated);
        var counters = new List<GpuTelemetryCounterSample>();
        var hasEngineUsage = engineUsage.TryGetValue(adapter.Index, out var engine);
        nvidiaMetrics.TryGetValue(adapter.Index, out var nvidia);

        foreach (var kind in GpuSpecializedTelemetry.GetSupportedUsageKinds(architecture))
        {
            if (!GpuSpecializedTelemetry.ShouldIncludeCounter(requestedCounters, adapter.Index, kind))
            {
                continue;
            }

            counters.Add(CreateCounter(adapter.Index, kind, engine, hasEngineUsage, nvidia));
        }

        return new GpuTelemetryAdapterSnapshot(
            adapter.Index,
            NativePdhAdapterIdentity.Pack(adapter.Luid),
            adapter.Name,
            FormatVendorId(adapter.VendorId),
            GpuSpecializedTelemetry.GetArchitectureName(architecture),
            counters.ToArray());
    }

    private static GpuTelemetryCounterSample CreateCounter(
        int adapterIndex,
        GpuSpecializedUsageKind kind,
        GpuEngineSpecializedUsage engine,
        bool hasEngineUsage,
        GpuMetrics? nvidia)
    {
        var (value, providerId, engineName, isIntrusive) = kind switch
        {
            GpuSpecializedUsageKind.NvidiaRt => (
                NullIfZero(engine.RtUsagePercent),
                "windows.pdh.gpu-engine",
                "RayTracing",
                false),
            GpuSpecializedUsageKind.NvidiaCuda => ResolveNvidiaCudaUsage(engine, nvidia),
            GpuSpecializedUsageKind.NvidiaTensor => (
                (double?)null,
                "nvidia.profiling",
                (string?)null,
                true),
            GpuSpecializedUsageKind.AmdGcn => ResolveAmdUsage(engine, hasEngineUsage, "GCN"),
            GpuSpecializedUsageKind.AmdRdna => ResolveAmdUsage(engine, hasEngineUsage, "RDNA"),
            GpuSpecializedUsageKind.AmdCdna => ResolveAmdUsage(engine, hasEngineUsage, "CDNA"),
            _ => (
                (double?)null,
                "unknown",
                (string?)null,
                false)
        };

        return new GpuTelemetryCounterSample(
            GpuSpecializedTelemetry.BuildCounterId(adapterIndex, kind),
            GpuSpecializedTelemetry.GetDisplayName(kind),
            kind == GpuSpecializedUsageKind.NvidiaTensor
                ? GpuTelemetryCounterClass.ProfilerMetric
                : GpuTelemetryCounterClass.HardwareCounter,
            value,
            "%",
            providerId,
            isIntrusive,
            engineName);
    }

    private static (double? Value, string ProviderId, string? EngineName, bool IsIntrusive) ResolveNvidiaCudaUsage(
        GpuEngineSpecializedUsage engine,
        GpuMetrics? nvidia)
    {
        var compute = NullIfZero(engine.CudaUsagePercent);
        if (compute is not null)
        {
            return (compute, "windows.pdh.gpu-engine", "Compute", false);
        }

        return nvidia is { IsUsageAvailable: true }
            ? (Math.Clamp(nvidia.UsagePercent, 0, 100), "nvidia.nvml", "SM/GPU utilization", false)
            : ((double?)null, "nvidia.nvml", "SM/GPU utilization", false);
    }

    private static (double? Value, string ProviderId, string? EngineName, bool IsIntrusive) ResolveAmdUsage(
        GpuEngineSpecializedUsage engine,
        bool hasEngineUsage,
        string engineName)
    {
        return hasEngineUsage
            ? (Math.Clamp(engine.TotalUsagePercent, 0, 100), "windows.pdh.gpu-engine", engineName, false)
            : ((double?)null, "windows.pdh.gpu-engine", engineName, false);
    }

    private static GpuTelemetryProviderState[] CreateProviderStates(
        IReadOnlyDictionary<int, GpuEngineSpecializedUsage> engineUsage,
        IReadOnlyList<GpuMetrics> nvidiaMetrics,
        bool nvidiaFallbackRequested,
        long elapsedMilliseconds)
    {
        return
        [
            new GpuTelemetryProviderState(
                "windows.pdh.gpu-engine",
                engineUsage.Count > 0 ? GpuTelemetryWorkerStatus.Online : GpuTelemetryWorkerStatus.Unavailable,
                engineUsage.Count > 0 ? "Windows GPU Engine counters returned specialized engine buckets." : "Windows GPU Engine counters did not return adapter-matched samples.",
                false,
                false,
                checked((int)Math.Min(int.MaxValue, elapsedMilliseconds))),
            new GpuTelemetryProviderState(
                "nvidia.nvml",
                !nvidiaFallbackRequested
                    ? GpuTelemetryWorkerStatus.Disabled
                    : nvidiaMetrics.Count > 0
                        ? GpuTelemetryWorkerStatus.Online
                        : GpuTelemetryWorkerStatus.Unavailable,
                !nvidiaFallbackRequested
                    ? "NVIDIA NVML was not requested by the active specialized counters."
                    : nvidiaMetrics.Count > 0
                        ? "NVIDIA NVML returned CUDA/SM fallback usage."
                        : "NVIDIA NVML did not return a matching adapter sample.",
                false,
                false,
                checked((int)Math.Min(int.MaxValue, elapsedMilliseconds))),
            new GpuTelemetryProviderState(
                "nvidia.profiling",
                GpuTelemetryWorkerStatus.NotConfigured,
                "Tensor usage requires CUPTI/DCGM/Nsight profiling; this low-intrusion probe does not fake it from total GPU usage.",
                true,
                true,
                0),
        ];
    }

    private static double? NullIfZero(double value)
    {
        return value > 0 ? Math.Clamp(value, 0, 100) : null;
    }

    private static string FormatVendorId(uint vendorId)
    {
        return vendorId == 0 ? string.Empty : $"0x{vendorId:X4}";
    }
}
