using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Application.Components;

public sealed partial class ComponentManager
{
    private IReadOnlyList<ComponentProviderStatus> BuildProviderStatus(
        ComponentDefinition definition,
        HardwareMetricSnapshot snapshot,
        OptionalDependencyStatus? dependencyStatus)
    {
        var active = IsProviderActive(definition.Id, snapshot);
        var bundled = IsBundledComponent(definition);
        var runtimeProbe = (!active || bundled) && SupportsRuntimeProbe(definition.Id)
            ? providerRuntimeProbe.Probe(definition.Id)
            : null;
        var state = active
            ? ComponentProviderStates.Active
            : runtimeProbe?.RuntimeAvailable == true
                ? runtimeProbe.State
            : bundled
                ? runtimeProbe?.State ?? ComponentProviderStates.Installed
            : dependencyStatus?.Installed == true
                ? ComponentProviderStates.Installed
                : ComponentProviderStates.Missing;

        var message = active
            ? "已从实时指标读到该 Provider 数据。"
            : runtimeProbe is not null && (runtimeProbe.RuntimeAvailable || bundled)
                ? string.IsNullOrWhiteSpace(runtimeProbe.RuntimePath)
                    ? runtimeProbe.Message
                    : $"{runtimeProbe.Message} 路径：{runtimeProbe.RuntimePath}"
            : definition.Id switch
            {
                "shared-webview2-runtime" => "未检测到共享 WebView2 运行时，可从运行时管理页受控安装。",
                "amd-smu-pawnio-provider" => "PawnIO 驱动可受控安装；未检测到运行库，或 AMD SMU Provider 读数尚未通过验证。",
                "amd-adlx-provider" => "AMD ADLX 运行库或实时读数尚未通过验证。",
                "nvidia-nvapi-provider" => "NVIDIA NVAPI 由显卡驱动提供；运行库未检测到，或 NVAPI Provider 读数尚未通过验证。",
                "librehardwaremonitor-provider" => "LibreHardwareMonitor 核心库随产品提供；需显式安装 PawnIO，实时传感能力按本机实际返回。",
                "notebook-oem-fan-provider" => "内置笔记本 OEM 风扇组件已安装；当前机型尚未返回可验证风扇读数。",
                _ => "Provider 未返回可验证读数。"
            };

        return
        [
            new ComponentProviderStatus(
                definition.Capabilities.FirstOrDefault()?.ProviderId ?? definition.Id,
                definition.Name,
                definition.Vendor,
                definition.Capabilities.FirstOrDefault()?.ProviderKind ?? "Provider",
                state,
                message,
                definition.Capabilities.Select(static capability => capability.Label).Distinct().ToArray())
        ];
    }

    private static bool IsProviderActive(string componentId, HardwareMetricSnapshot snapshot)
    {
        return componentId switch
        {
            "nvidia-nvml-provider" => HasNumericMetricFromProvider(snapshot, "NVIDIA NVML"),
            "amd-adlx-provider" => HasNumericMetricFromProvider(snapshot, "AMD ADLX"),
            "amd-smu-pawnio-provider" => HasNumericMetricFromProvider(snapshot, "AMD SMU / PawnIO"),
            "librehardwaremonitor-provider" => HasNumericMetricFromProvider(snapshot, "LibreHardwareMonitor Core"),
            "nvidia-nvapi-provider" => HasNumericMetricFromProvider(snapshot, "NVIDIA NVAPI"),
            "notebook-oem-fan-provider" => HasNotebookOemFanMetric(snapshot),
            _ => false
        };
    }

    private static bool SupportsRuntimeProbe(string componentId)
    {
        return componentId.Equals("shared-webview2-runtime", StringComparison.OrdinalIgnoreCase)
            || componentId.Equals("amd-adlx-provider", StringComparison.OrdinalIgnoreCase)
            || componentId.Equals("amd-smu-pawnio-provider", StringComparison.OrdinalIgnoreCase)
            || componentId.Equals("intel-msr-pawnio-provider", StringComparison.OrdinalIgnoreCase)
            || componentId.Equals("nvidia-nvapi-provider", StringComparison.OrdinalIgnoreCase)
            || componentId.Equals("librehardwaremonitor-provider", StringComparison.OrdinalIgnoreCase)
            || componentId.Equals("notebook-oem-fan-provider", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasNotebookOemFanMetric(HardwareMetricSnapshot snapshot)
    {
        return HasNumericMetricFromProvider(snapshot, "Mechrevo UWACPI")
            || HasNumericMetricFromProvider(snapshot, "Notebook OEM Fan Provider");
    }

    private static bool HasNumericMetricFromProvider(HardwareMetricSnapshot snapshot, string providerName)
    {
        return snapshot.Items.Values.Any(value =>
            value.NumericValue is not null
            && value.Detail?.Contains(providerName, StringComparison.OrdinalIgnoreCase) == true)
            || HasCpuSensorMetricFromProvider(snapshot, providerName)
            || HasGpuSensorMetricFromProvider(snapshot, providerName);
    }

    private static bool HasCpuSensorMetricFromProvider(HardwareMetricSnapshot snapshot, string providerName)
    {
        var sensors = snapshot.Cpu.Sensors;
        return sensors.ProviderState.Provider.Contains(providerName, StringComparison.OrdinalIgnoreCase)
            && (sensors.PackagePowerWatts is not null
                || sensors.CoreVoltageVolts is not null
                || sensors.PackageCurrentAmps is not null
                || sensors.TemperatureCelsius is not null
                || sensors.StapmPowerWatts is not null
                || sensors.ActualPowerWatts is not null
                || sensors.AveragePowerWatts is not null
                || sensors.TdcCurrentAmps is not null
                || sensors.EdcCurrentAmps is not null
                || sensors.SocPowerWatts is not null
                || sensors.SocVoltageVolts is not null
                || sensors.ApuFrequencyMhz is not null
                || sensors.ApuVoltageVolts is not null
                || sensors.ApuTemperatureCelsius is not null
                || sensors.SmuFrequencyMhz is not null);
    }

    private static bool HasGpuSensorMetricFromProvider(HardwareMetricSnapshot snapshot, string providerName)
    {
        return snapshot.Gpus.Any(gpu =>
            gpu.Sensors.ProviderState.Provider.Contains(providerName, StringComparison.OrdinalIgnoreCase)
            && (gpu.Sensors.PowerWatts is not null
                || gpu.Sensors.PowerLimitWatts is not null
                || gpu.Sensors.BoardPowerWatts is not null
                || gpu.Sensors.TemperatureCelsius is not null
                || gpu.Sensors.HotspotTemperatureCelsius is not null
                || gpu.Sensors.IntakeTemperatureCelsius is not null
                || gpu.Sensors.FanSpeedPercent is not null
                || gpu.Sensors.FanSpeedRpm is not null
                || gpu.Sensors.CoreVoltageVolts is not null
                || gpu.Sensors.CurrentAmps is not null));
    }
}
