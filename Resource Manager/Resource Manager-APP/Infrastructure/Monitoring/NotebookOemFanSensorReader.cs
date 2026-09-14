using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal sealed class NotebookOemFanSensorReader
{
    public const string ComponentId = "notebook-oem-fan-provider";
    public const string ComponentName = "Notebook OEM Fan Provider";

    private readonly object compileGate = new();
    private readonly MechrevoUwAcpiSensorReader mechrevoUwAcpiSensorReader = new();
    private CompiledFirmwareProviderPlan? compiledPlan;
    private Func<NotebookOemFanSensorSnapshot>? compiledReadRoute;

    public NotebookOemFanSensorSnapshot Read()
    {
        return Read(CompiledFirmwareProviderPlan.Default);
    }

    public NotebookOemFanSensorSnapshot Read(CompiledFirmwareProviderPlan firmwareProviderPlan)
    {
        return GetCompiledReadRoute(firmwareProviderPlan)();
    }

    private Func<NotebookOemFanSensorSnapshot> GetCompiledReadRoute(CompiledFirmwareProviderPlan firmwareProviderPlan)
    {
        var route = compiledReadRoute;
        if (ReferenceEquals(compiledPlan, firmwareProviderPlan) && route is not null)
        {
            return route;
        }

        lock (compileGate)
        {
            if (!ReferenceEquals(compiledPlan, firmwareProviderPlan) || compiledReadRoute is null)
            {
                compiledReadRoute = CompileReadRoute(firmwareProviderPlan);
                compiledPlan = firmwareProviderPlan;
            }

            return compiledReadRoute;
        }
    }

    private Func<NotebookOemFanSensorSnapshot> CompileReadRoute(CompiledFirmwareProviderPlan firmwareProviderPlan)
    {
        return firmwareProviderPlan.NotebookOemFanProvider switch
        {
            NotebookOemFanProviderKind.MechrevoUwAcpi => ReadMechrevoUwAcpi,
            _ => () => UnsupportedFirmware(firmwareProviderPlan)
        };
    }

    private NotebookOemFanSensorSnapshot ReadMechrevoUwAcpi()
    {
        var mechrevo = mechrevoUwAcpiSensorReader.Read();
        var providerState = MergeProviderStates([mechrevo.ProviderState]);

        return new NotebookOemFanSensorSnapshot(
            providerState,
            mechrevo.CpuFanSpeedRpm,
            mechrevo.CpuFanSpeedPercent,
            mechrevo.GpuFanSensor is null ? [] : [mechrevo.GpuFanSensor]);
    }

    public static NotebookOemFanSensorSnapshot Frozen()
    {
        return new NotebookOemFanSensorSnapshot(
            new HardwareSensorProviderState(
                ComponentName,
                "Frozen",
                "Notebook OEM Fan 监控源当前处于功能区冻结。"),
            null,
            null,
            []);
    }

    public static HardwareSensorDependencyRuntimeStatus ProbeRuntime(CompiledFirmwareProviderPlan? firmwareProviderPlan = null)
    {
        var effectivePlan = firmwareProviderPlan ?? CompiledFirmwareProviderPlan.Default;
        if (effectivePlan.NotebookOemFanProvider != NotebookOemFanProviderKind.MechrevoUwAcpi)
        {
            return new HardwareSensorDependencyRuntimeStatus(
                false,
                false,
                "Installed",
                $"内置组件已随 Resource Manager 安装。启动固件计划未匹配已接入的笔记本 OEM 风扇子 Provider；当前固件：{effectivePlan.Identity.Describe()}。",
                null);
        }

        var mechrevo = MechrevoUwAcpiSensorReader.ProbeRuntime();
        if (mechrevo.RuntimeAvailable)
        {
            return new HardwareSensorDependencyRuntimeStatus(
                true,
                mechrevo.PrimaryCapabilityAvailable,
                mechrevo.State,
                $"内置组件已安装；Mechrevo/Tongfang UWACPI 子 Provider 可用。{mechrevo.Message}",
                mechrevo.RuntimePath);
        }

        return new HardwareSensorDependencyRuntimeStatus(
            false,
            false,
            "Installed",
            "内置组件已随 Resource Manager 安装。当前未检测到已接入厂商的可用只读风扇运行库；已接入 Mechrevo/Tongfang UWACPI，后续扩展 Lenovo/ASUS/MSI/Dell/HP/Acer 等路线。",
            null);
    }

    private static NotebookOemFanSensorSnapshot UnsupportedFirmware(CompiledFirmwareProviderPlan firmwareProviderPlan)
    {
        return new NotebookOemFanSensorSnapshot(
            new HardwareSensorProviderState(
                ComponentName,
                "UnsupportedFirmware",
                $"启动固件计划未匹配已接入的笔记本 OEM 风扇子 Provider；当前固件：{firmwareProviderPlan.Identity.Describe()}。"),
            null,
            null,
            []);
    }

    private static HardwareSensorProviderState MergeProviderStates(IReadOnlyList<HardwareSensorProviderState> states)
    {
        if (states.Any(static state => state.State.Equals("Active", StringComparison.OrdinalIgnoreCase)))
        {
            return new HardwareSensorProviderState(
                ComponentName,
                "Active",
                string.Join("; ", states.Select(static state => $"{state.Provider}: {state.State}")));
        }

        if (states.Any(static state =>
            state.State.Equals("RuntimeAvailable", StringComparison.OrdinalIgnoreCase)
            || state.State.Equals("InstalledUnverified", StringComparison.OrdinalIgnoreCase)))
        {
            return new HardwareSensorProviderState(
                ComponentName,
                "InstalledUnverified",
                string.Join("; ", states.Select(static state => $"{state.Provider}: {state.Message}")));
        }

        return new HardwareSensorProviderState(
            ComponentName,
            "Unavailable",
            string.Join("; ", states.Select(static state => $"{state.Provider}: {state.Message}")));
    }
}

internal sealed record NotebookOemFanSensorSnapshot(
    HardwareSensorProviderState ProviderState,
    double? CpuFanSpeedRpm,
    double? CpuFanSpeedPercent,
    IReadOnlyList<PlatformGpuSensor> GpuFanSensors);
