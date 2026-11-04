using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal static class GpuProviderMessages
{
    internal static string? Describe(string provider, string state) => (provider, state) switch
    {
        ("NVIDIA NVML", HardwareSensorProviderStates.Partial) => "NVIDIA NVML 未返回电压/电流；需要 NVAPI 或可靠的厂商字段接口。",
        ("NVIDIA NVML", HardwareSensorProviderStates.Unavailable) => "NVIDIA NVML 可用，但当前硬件/驱动未返回传感读数。",
        ("NVIDIA NVAPI", HardwareSensorProviderStates.Partial) => "NVIDIA NVAPI 返回部分补充传感读数；风扇和电流取决于驱动是否暴露可靠字段。",
        ("NVIDIA NVAPI", HardwareSensorProviderStates.Unavailable) => "NVIDIA NVAPI 可加载，但当前驱动/硬件未返回请求的补充传感读数。",
        ("AMD ADLX", HardwareSensorProviderStates.Partial) => "AMD ADLX: 当前已接入可用传感项；电流仍不推导。",
        ("AMD ADLX", HardwareSensorProviderStates.Unavailable) => "AMD ADLX 可用，但当前硬件/驱动未返回请求的传感读数。",
        ("NVIDIA NVML", HardwareSensorProviderStates.NotRequested) => "当前快照未请求 NVIDIA 传感指标。",
        _ => null
    };
}
