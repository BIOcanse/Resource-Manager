using ResourceManager.App.Application.Control;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    /// <summary>
    /// 显卡控制的现场事实：NVAPI 认到哪几块卡、功耗策略那两块内存里到底是什么。
    ///
    /// 有它才回答得了"这块卡的功耗为什么调不了" —— 结构体偏移写错也会得出
    /// "调不了"的结论，而且从结果上看不出区别。诊断用，不进监控链路。
    /// </summary>
    private static IEndpointRouteBuilder MapDebugGpuControlEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/debug/gpu-control", (IControlObjectCatalog catalog) =>
        {
            var bridge = new NvidiaNvapiControlBridge();
            var powerBridge = new NvidiaNvmlControlBridge();
            var rows = catalog.ReadObjects().Objects
                .Where(static entry => entry.AdapterIndex is not null)
                .Select(entry =>
                {
                    var handle = bridge.FindHandleByAdapterIndex(entry.AdapterIndex!.Value);
                    return new
                    {
                        entry.Id,
                        entry.DisplayName,
                        entry.AdapterIndex,
                        HandleFound = handle is not null,
                        Range = handle is null ? null : bridge.ReadPowerLimitRange(handle.Value),
                        CoreOffsetMhz = handle is null
                            ? null
                            : bridge.ReadClockOffsetMhz(
                                handle.Value,
                                NvidiaNvapiControlBridge.GraphicsClockDomain),
                        MemoryOffsetMhz = handle is null
                            ? null
                            : bridge.ReadClockOffsetMhz(
                                handle.Value,
                                NvidiaNvapiControlBridge.MemoryClockDomain),
                        Dump = handle is null ? null : bridge.DumpPowerPolicyVariants(handle.Value),
                        PowerLimit = powerBridge.FindHandleByAdapterIndex(entry.AdapterIndex!.Value)
                            is { } readDevice
                                ? powerBridge.ReadPowerLimit(readDevice)
                                : null
                    };
                })
                .ToArray();
            return Results.Ok(rows);
        }).AllowAnonymous();

        return app;
    }
}
