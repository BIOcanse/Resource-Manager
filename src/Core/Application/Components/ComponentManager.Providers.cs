using ResourceManager.App.Domain.Messages;
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

        var message = BackendMessage.Create(BackendMessageDomains.Dependency,
            active ? BackendMessageCodes.Dependency.ProviderVerified
                : runtimeProbe?.RuntimeAvailable == true
                    ? BackendMessageCodes.Dependency.ProviderRuntimeUnverified
                    : BackendMessageCodes.Dependency.ProviderBridgeMissing);

        return
        [
            new ComponentProviderStatus(
                definition.Capabilities.FirstOrDefault()?.ProviderId ?? definition.Id,
                definition.Vendor,
                definition.Capabilities.FirstOrDefault()?.ProviderKind ?? ComponentProviderKinds.Provider,
                state,
                message,
                runtimeProbe?.RuntimePath,
                definition.Capabilities.Select(static capability => capability.Id).Distinct().ToArray())
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
