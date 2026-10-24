using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Application.Components;

public static class ComponentCatalog
{
    public static IReadOnlyList<ComponentDefinition> Definitions { get; } =
    [
        CreateOptionalDependencyDefinition(
            "shared-webview2-runtime",
            "共享 WebView2 运行时",
            "Microsoft",
            "共享界面运行时",
            "为资源管理器和其它 WebView2 软件提供全机共享的 Chromium 界面运行时。",
            "Shared WebView2 runtime",
            ComponentProviderKinds.Runtime,
            ["webview2-runtime"]),
        CreateOptionalDependencyDefinition(
            "amd-smu-pawnio-provider",
            "AMD SMU / PawnIO Provider",
            "namazso / AMD SMU",
            "硬件传感 Provider",
            "通过官方签名 PawnIO 驱动和 AMD SMU PM table 读取 Ryzen CPU STAPM、功耗、电压、电流、温度和每核指标。",
            "AMD SMU PM table telemetry provider",
            ComponentProviderKinds.Cpu,
            ["smu-pm-table", "stapm", "power", "voltage", "current", "temperature", "per-core"]),
        CreateOptionalDependencyDefinition(
            "intel-msr-pawnio-provider",
            "Intel CPU / PawnIO",
            "namazso / Intel",
            "控制写入",
            "通过官方签名 IntelMSR 模块调节 PL1/PL2、时间窗和各电压域；每项写入回读验证，撤销恢复原值。",
            "Intel CPU control", ComponentProviderKinds.Cpu,
            ["pl1", "pl2", "time-window", "voltage-offset"]),
        CreateOptionalDependencyDefinition(
            "hardware-bridge", "硬件写入辅助进程", "Resource Manager", "控制写入",
            "处理器功耗上限和电压写入经过它。", "CPU control", ComponentProviderKinds.Cpu, ["power", "voltage"]),
        CreateOptionalDependencyDefinition(
            "hardware-sensors-pawnio-provider", "Hardware sensors / PawnIO", "namazso",
            "硬件传感驱动", "主板和内存核心读取共用官方签名 PawnIO 驱动。",
            "PawnIO sensor driver", ComponentProviderKinds.HardwareMonitor, ["fan", "temperature", "voltage"]),
        new ComponentDefinition(
            Id: "librehardwaremonitor-provider", Name: "LibreHardwareMonitor Core",
            Vendor: "LibreHardwareMonitor", Category: "硬件传感 Provider",
            Purpose: "产品内的官方核心库；仅 Motherboard/Memory，读取主板、EC、SuperIO 与 DIMM，不启动 GUI 或服务。",
            SourcePageUrl: "https://github.com/LibreHardwareMonitor/LibreHardwareMonitor",
            ExternalTermsUrl: "https://www.mozilla.org/MPL/2.0/",
            ManagementRole: SoftwareManagementRoles.Dependency, IsBundled: true,
            RequiresExternalTermsAcknowledgement: false, RequiresElevation: false,
            Capabilities:
            [
                new ComponentCapability("hardware-monitor-core", "主板与内存传感", "librehardwaremonitor-provider", ComponentProviderKinds.HardwareMonitor),
                new ComponentCapability("memory-temperature", "内存温度", "librehardwaremonitor-provider", ComponentProviderKinds.HardwareMonitor),
                new ComponentCapability("fan", "主板风扇", "librehardwaremonitor-provider", ComponentProviderKinds.HardwareMonitor)
            ],
            InstallNote: "MPL-2.0；核心库随产品提供，需已安装的 PawnIO 驱动，未验证支持不能当作实时读数。"),
        new ComponentDefinition(
            Id: "notebook-oem-fan-provider",
            Name: "Notebook OEM Fan Provider",
            Vendor: "Resource Manager",
            Category: "硬件传感 Provider",
            Purpose: "Resource Manager 内置笔记本厂商风扇适配组件；通过各 OEM 已安装官方驱动/服务/DLL 的只读接口读取 CPU/GPU 风扇转速和百分比。",
            SourcePageUrl: "",
            ExternalTermsUrl: null,
            ManagementRole: SoftwareManagementRoles.Dependency,
            IsBundled: true,
            RequiresExternalTermsAcknowledgement: false,
            RequiresElevation: false,
            Capabilities:
            [
                new ComponentCapability("notebook-oem-fan.cpu-rpm", "CPU 风扇转速", "notebook-oem-fan-provider", ComponentProviderKinds.NotebookOem),
                new ComponentCapability("notebook-oem-fan.cpu-percent", "CPU 风扇百分比", "notebook-oem-fan-provider", ComponentProviderKinds.NotebookOem),
                new ComponentCapability("notebook-oem-fan.gpu-rpm", "GPU 风扇转速", "notebook-oem-fan-provider", ComponentProviderKinds.NotebookOem),
                new ComponentCapability("notebook-oem-fan.gpu-percent", "GPU 风扇百分比", "notebook-oem-fan-provider", ComponentProviderKinds.NotebookOem),
                new ComponentCapability("notebook-oem-fan.vendor-adapters", "笔记本厂商适配", "notebook-oem-fan-provider", ComponentProviderKinds.NotebookOem)
            ],
            InstallNote: "内置组件，随 Resource Manager 默认安装。当前接入 Mechrevo/Tongfang UWACPI 只读风扇路径；后续继续接入 Lenovo/ASUS/MSI/Dell/HP/Acer 等厂商。不会静默安装或分发 OEM 驱动。"),
        new ComponentDefinition(
            Id: "nvidia-nvml-provider",
            Name: "NVIDIA NVML Provider",
            Vendor: "NVIDIA",
            Category: "硬件传感 Provider",
            Purpose: "从已安装 NVIDIA 驱动动态加载 NVML，读取 NVIDIA GPU 频率、显存、功耗和温度。",
            SourcePageUrl: "https://docs.nvidia.com/deploy/nvml-api/nvml-api-reference.html",
            ExternalTermsUrl: null,
            ManagementRole: SoftwareManagementRoles.Dependency,
            IsBundled: true,
            RequiresExternalTermsAcknowledgement: false,
            RequiresElevation: false,
            Capabilities:
            [
                new ComponentCapability("nvidia-gpu-clocks", "GPU 频率", "nvidia-nvml", ComponentProviderKinds.Gpu),
                new ComponentCapability("nvidia-gpu-memory", "显存", "nvidia-nvml", ComponentProviderKinds.Gpu),
                new ComponentCapability("nvidia-gpu-power", "GPU 功耗", "nvidia-nvml", ComponentProviderKinds.Gpu),
                new ComponentCapability("nvidia-gpu-temperature", "GPU 温度", "nvidia-nvml", ComponentProviderKinds.Gpu)
            ],
            InstallNote: "NVML 由 NVIDIA 驱动提供，基础包只动态调用，不随包分发驱动 DLL。"),
        new ComponentDefinition(
            Id: "nvidia-nvapi-provider",
            Name: "NVIDIA NVAPI Provider",
            Vendor: "NVIDIA",
            Category: "硬件传感 Provider",
            Purpose: "从已安装 NVIDIA 驱动动态加载 NVAPI，补齐 NVML 基础接口缺失的风扇/冷却器和后续电气读数。",
            SourcePageUrl: "https://docs.nvidia.com/nvapi/modules.html",
            ExternalTermsUrl: null,
            ManagementRole: SoftwareManagementRoles.Dependency,
            IsBundled: true,
            RequiresExternalTermsAcknowledgement: false,
            RequiresElevation: false,
            Capabilities:
            [
                new ComponentCapability("nvidia-gpu-cooler", "GPU 风扇/冷却器", "nvidia-nvapi", ComponentProviderKinds.Gpu),
                new ComponentCapability("nvidia-gpu-voltage", "GPU 电压", "nvidia-nvapi", ComponentProviderKinds.Gpu),
                new ComponentCapability("nvidia-gpu-current", "GPU 电流", "nvidia-nvapi", ComponentProviderKinds.Gpu)
            ],
            InstallNote: "NVAPI 由 NVIDIA Windows 驱动提供；基础包只做运行库探测和后续动态调用，不随包分发 nvapi64.dll。"),
        new ComponentDefinition(
            Id: "amd-adlx-provider",
            Name: "AMD ADLX / ADL Provider",
            Vendor: "AMD",
            Category: "硬件传感 Provider",
            Purpose: "从已安装 AMD 显示驱动动态加载 ADLX，读取 AMD GPU/iGPU 占用、频率、功耗、温度和电压。",
            SourcePageUrl: "https://github.com/GPUOpen-LibrariesAndSDKs/ADLX",
            ExternalTermsUrl: null,
            ManagementRole: SoftwareManagementRoles.Dependency,
            IsBundled: true,
            RequiresExternalTermsAcknowledgement: false,
            RequiresElevation: false,
            Capabilities:
            [
                new ComponentCapability("amd-gpu-clocks", "GPU 频率", "amd-adlx", ComponentProviderKinds.Gpu),
                new ComponentCapability("amd-gpu-power", "GPU 功耗", "amd-adlx", ComponentProviderKinds.Gpu),
                new ComponentCapability("amd-gpu-board-power", "GPU 总板功耗", "amd-adlx", ComponentProviderKinds.Gpu),
                new ComponentCapability("amd-gpu-temperature", "GPU 温度", "amd-adlx", ComponentProviderKinds.Gpu),
                new ComponentCapability("amd-gpu-hotspot-temperature", "GPU 热点温度", "amd-adlx", ComponentProviderKinds.Gpu),
                new ComponentCapability("amd-gpu-intake-temperature", "GPU 进风温度", "amd-adlx", ComponentProviderKinds.Gpu),
                new ComponentCapability("amd-gpu-fan", "GPU 风扇", "amd-adlx", ComponentProviderKinds.Gpu),
                new ComponentCapability("amd-gpu-voltage", "GPU 电压", "amd-adlx", ComponentProviderKinds.Gpu)
            ],
            InstallNote: "ADLX 库通常随 AMD 显示驱动提供，基础包只包含 Resource Manager bridge，不随包分发 AMD 驱动 DLL。")

    ];

    public static ComponentDefinition? Find(string id)
    {
        return Definitions.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsOptionalDependency(string id)
    {
        return OptionalDependencyCatalog.Find(id) is not null;
    }

    private static ComponentDefinition CreateOptionalDependencyDefinition(
        string id,
        string name,
        string vendor,
        string category,
        string purpose,
        string fallbackInstallNote,
        string providerKind,
        IReadOnlyList<string> capabilities)
    {
        var dependency = OptionalDependencyCatalog.Find(id)
            ?? throw new InvalidOperationException($"Missing optional dependency definition: {id}");

        return new ComponentDefinition(
            id,
            name,
            vendor,
            category,
            purpose,
            dependency.SourcePageUrl,
            dependency.ExternalTermsUrl,
            SoftwareManagementRoles.Dependency,
            false,
            dependency.RequiresExternalTermsAcknowledgement,
            dependency.RequiresElevation,
            capabilities.Select(capability => new ComponentCapability(
                $"{id}.{capability}",
                CapabilityLabel(capability),
                id,
                providerKind)).ToArray(),
            string.IsNullOrWhiteSpace(dependency.InstallNote)
                ? fallbackInstallNote
                : dependency.InstallNote);
    }

    private static string CapabilityLabel(string capability)
    {
        return capability switch
        {
            "power" => "功耗",
            "voltage" => "电压",
            "current" => "电流",
            "temperature" => "温度",
            "fan" => "风扇",
            "fan-rpm" => "风扇转速",
            "fan-percent" => "风扇百分比",
            "fan-control" => "风扇控制",
            "memory-temperature" => "内存温度",
            "motherboard-temperature" => "主板温度",
            "vrm-temperature" => "供电温度",
            "chipset-temperature" => "芯片组温度",
            "motherboard-voltage" => "主板电压",
            "disk-temperature" => "磁盘温度",
            "smu-pm-table" => "SMU PM table",
            "stapm" => "STAPM",
            "per-core" => "每核指标",
            "webview2-runtime" => "Web 界面运行时",
            _ => capability
        };
    }
}
