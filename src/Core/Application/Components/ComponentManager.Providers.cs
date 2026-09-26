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
            ? "Active"
            : runtimeProbe?.RuntimeAvailable == true
                ? runtimeProbe.State
            : bundled
                ? runtimeProbe?.State ?? "Installed"
            : dependencyStatus?.Installed == true
                ? "Installed"
                : "Missing";

        var message = active
            ? "已从实时指标读到该 Provider 数据。"
            : runtimeProbe is not null && (runtimeProbe.RuntimeAvailable || bundled)
                ? string.IsNullOrWhiteSpace(runtimeProbe.RuntimePath)
                    ? runtimeProbe.Message
                    : $"{runtimeProbe.Message} 路径：{runtimeProbe.RuntimePath}"
            : definition.Id switch
            {
                "shared-webview2-runtime" => "未检测到共享 WebView2 运行时，可从运行时管理页受控安装。",
                "amd-ryzen-master-monitoring-sdk" => "SDK 可受控安装；未检测到运行库，或实时读数尚未通过验证。",
                "amd-smu-pawnio-provider" => "PawnIO 驱动可受控安装；未检测到运行库，或 AMD SMU Provider 读数尚未通过验证。",
                "amd-adlx-provider" => "AMD ADLX 运行库或实时读数尚未通过验证。",
                "nvidia-nvapi-provider" => "NVIDIA NVAPI 由显卡驱动提供；运行库未检测到，或 NVAPI Provider 读数尚未通过验证。",
                "librehardwaremonitor-provider" => "LibreHardwareMonitor 可受控安装或手动放入依赖目录；未检测到 WMI/运行库，或实时读数尚未通过验证。",
                "notebook-fancontrol-provider" => "NBFC 依赖机型配置和 EC 访问；未检测到服务/运行库，或风扇读数尚未通过验证。",
                "notebook-oem-fan-provider" => "内置笔记本 OEM 风扇组件已安装；当前机型尚未返回可验证风扇读数。",
                "intel-pcm-provider" => "Intel PCM/MSR Provider 尚未接入，Windows 驱动/service 需要显式安装。",
                "windows-performance-toolkit" when dependencyStatus?.Installed == true => "WPR/Xperf/WPA 已安装；kernel trace 启动需要管理员权限或后续受限提权 helper。",
                "windows-performance-toolkit" => "Windows Performance Toolkit 可受控安装；用于 WPR/Xperf/WPA 官方 ETW trace 分析。",
                "latencymon" when dependencyStatus?.Installed == true => "LatencyMon 已安装；可作为 ISR/DPC/hard pagefault 辅助诊断对照，不作为基础 API。",
                "latencymon" => "LatencyMon 可受控安装；用于 ISR/DPC/hard pagefault 辅助诊断对照。",
                "msi-afterburner" when dependencyStatus?.Installed == true => "已检测到 MSI Afterburner，可作为外部遥测参考或可选辅助工具。",
                "msi-afterburner" => "MSI Afterburner 只作为可选辅助/参考，不作为基础 telemetry Provider。",
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
            "amd-ryzen-master-monitoring-sdk" => HasNumericMetricFromProvider(snapshot, "AMD Ryzen Master Monitoring SDK"),
            "librehardwaremonitor-provider" => HasNumericMetricFromProvider(snapshot, "LibreHardwareMonitor WMI")
                || HasNumericMetricFromProvider(snapshot, "OpenHardwareMonitor WMI"),
            "nvidia-nvapi-provider" => HasNumericMetricFromProvider(snapshot, "NVIDIA NVAPI"),
            "notebook-fancontrol-provider" => HasNumericMetricFromProvider(snapshot, "Notebook FanControl"),
            "notebook-oem-fan-provider" => HasNotebookOemFanMetric(snapshot),
            "intel-pcm-provider" => HasNumericMetricFromProvider(snapshot, "Intel PCM"),
            _ => false
        };
    }

    private static bool SupportsRuntimeProbe(string componentId)
    {
        return componentId.Equals("shared-webview2-runtime", StringComparison.OrdinalIgnoreCase)
            || componentId.Equals("amd-adlx-provider", StringComparison.OrdinalIgnoreCase)
            || componentId.Equals("amd-smu-pawnio-provider", StringComparison.OrdinalIgnoreCase)
            || componentId.Equals("amd-ryzen-master-monitoring-sdk", StringComparison.OrdinalIgnoreCase)
            || componentId.Equals("nvidia-nvapi-provider", StringComparison.OrdinalIgnoreCase)
            || componentId.Equals("librehardwaremonitor-provider", StringComparison.OrdinalIgnoreCase)
            || componentId.Equals("notebook-fancontrol-provider", StringComparison.OrdinalIgnoreCase)
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
