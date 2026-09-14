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
            "Runtime",
            ["webview2-runtime"]),
        CreateOptionalDependencyDefinition(
            "amd-smu-pawnio-provider",
            "AMD SMU / PawnIO Provider",
            "namazso / AMD SMU",
            "硬件传感 Provider",
            "通过官方签名 PawnIO 驱动和 AMD SMU PM table 读取 Ryzen CPU STAPM、功耗、电压、电流、温度和每核指标。",
            "AMD SMU PM table telemetry provider",
            "CPU",
            ["smu-pm-table", "stapm", "power", "voltage", "current", "temperature", "per-core"]),
        CreateOptionalDependencyDefinition(
            "amd-ryzen-master-monitoring-sdk",
            "AMD Ryzen Master Monitoring SDK",
            "AMD",
            "硬件传感 Provider",
            "AMD CPU 功耗、电压、电流、温度 Provider 候选。",
            "CPU telemetry SDK",
            "CPU",
            ["power", "voltage", "current", "temperature"]),
        CreateOptionalDependencyDefinition(
            "msi-afterburner",
            "MSI Afterburner",
            "MSI",
            "支持软件/辅助工具",
            "外部遥测参考和可选辅助工具，不作为基础运行依赖。",
            "External telemetry helper",
            "Helper",
            ["reference-telemetry", "shared-memory"]),
        CreateOptionalDependencyDefinition(
            "librehardwaremonitor-provider",
            "LibreHardwareMonitor Provider",
            "LibreHardwareMonitor",
            "硬件传感 Provider",
            "通用硬件监控桥，补齐 CPU/系统风扇、内存温度、主板传感、电压等 Windows/厂商基础接口缺口。",
            "Hardware monitor WMI bridge",
            "HardwareMonitor",
            ["hardware-monitor-wmi", "fan", "temperature", "voltage", "memory-temperature", "motherboard-temperature", "vrm-temperature", "chipset-temperature", "motherboard-voltage", "disk-temperature"]),
        CreateOptionalDependencyDefinition(
            "notebook-fancontrol-provider",
            "Notebook FanControl Provider",
            "NBFC",
            "硬件传感 Provider",
            "笔记本 EC 风扇路线，用于普通硬件监控和显卡驱动都不暴露风扇时的候选 Provider。",
            "Notebook EC fan provider",
            "NotebookEC",
            ["notebook-ec", "fan", "fan-rpm", "fan-control"]),
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
                new ComponentCapability("notebook-oem-fan.cpu-rpm", "CPU 风扇转速", "notebook-oem-fan-provider", "NotebookOEM"),
                new ComponentCapability("notebook-oem-fan.cpu-percent", "CPU 风扇百分比", "notebook-oem-fan-provider", "NotebookOEM"),
                new ComponentCapability("notebook-oem-fan.gpu-rpm", "GPU 风扇转速", "notebook-oem-fan-provider", "NotebookOEM"),
                new ComponentCapability("notebook-oem-fan.gpu-percent", "GPU 风扇百分比", "notebook-oem-fan-provider", "NotebookOEM"),
                new ComponentCapability("notebook-oem-fan.vendor-adapters", "笔记本厂商适配", "notebook-oem-fan-provider", "NotebookOEM")
            ],
            InstallNote: "内置组件，随 Resource Manager 默认安装。当前接入 Mechrevo/Tongfang UWACPI 只读风扇路径；后续继续接入 Lenovo/ASUS/MSI/Dell/HP/Acer 等厂商。不会静默安装或分发 OEM 驱动。"),
        CreateOptionalDependencyDefinition(
            "windows-performance-toolkit",
            "Windows Performance Toolkit",
            "Microsoft",
            "系统追踪/延迟诊断",
            "官方 WPR/Xperf/WPA 工具链，用于 ETW kernel trace、DPC/ISR、磁盘和网络深度分析。",
            "System trace toolkit",
            "ETW",
            ["kernel-trace", "dpc-isr", "wpr", "xperf", "wpa-export"]),
        CreateOptionalDependencyDefinition(
            "latencymon",
            "LatencyMon",
            "Resplendence",
            "系统追踪/延迟诊断",
            "ISR/DPC/hard pagefault 辅助诊断工具，用于对照验证中断延迟和驱动归因。",
            "Latency diagnostics helper",
            "Helper",
            ["dpc-isr", "hard-pagefault", "driver-latency", "latency-report"]),
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
                new ComponentCapability("nvidia-gpu-clocks", "GPU 频率", "nvidia-nvml", "GPU"),
                new ComponentCapability("nvidia-gpu-memory", "显存", "nvidia-nvml", "GPU"),
                new ComponentCapability("nvidia-gpu-power", "GPU 功耗", "nvidia-nvml", "GPU"),
                new ComponentCapability("nvidia-gpu-temperature", "GPU 温度", "nvidia-nvml", "GPU")
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
                new ComponentCapability("nvidia-gpu-cooler", "GPU 风扇/冷却器", "nvidia-nvapi", "GPU"),
                new ComponentCapability("nvidia-gpu-voltage", "GPU 电压", "nvidia-nvapi", "GPU"),
                new ComponentCapability("nvidia-gpu-current", "GPU 电流", "nvidia-nvapi", "GPU")
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
                new ComponentCapability("amd-gpu-clocks", "GPU 频率", "amd-adlx", "GPU"),
                new ComponentCapability("amd-gpu-power", "GPU 功耗", "amd-adlx", "GPU"),
                new ComponentCapability("amd-gpu-board-power", "GPU 总板功耗", "amd-adlx", "GPU"),
                new ComponentCapability("amd-gpu-temperature", "GPU 温度", "amd-adlx", "GPU"),
                new ComponentCapability("amd-gpu-hotspot-temperature", "GPU 热点温度", "amd-adlx", "GPU"),
                new ComponentCapability("amd-gpu-intake-temperature", "GPU 进风温度", "amd-adlx", "GPU"),
                new ComponentCapability("amd-gpu-fan", "GPU 风扇", "amd-adlx", "GPU"),
                new ComponentCapability("amd-gpu-voltage", "GPU 电压", "amd-adlx", "GPU")
            ],
            InstallNote: "ADLX 库通常随 AMD 显示驱动提供，基础包只包含 Resource Manager bridge，不随包分发 AMD 驱动 DLL。"),
        new ComponentDefinition(
            Id: "intel-pcm-provider",
            Name: "Intel PCM / MSR Provider",
            Vendor: "Intel",
            Category: "硬件传感 Provider",
            Purpose: "Intel CPU 底层功耗、频率和 MSR 相关 Provider 候选。",
            SourcePageUrl: "https://github.com/intel/pcm",
            ExternalTermsUrl: null,
            ManagementRole: SoftwareManagementRoles.Dependency,
            IsBundled: true,
            RequiresExternalTermsAcknowledgement: false,
            RequiresElevation: true,
            Capabilities:
            [
                new ComponentCapability("intel-cpu-power", "CPU 功耗", "intel-pcm", "CPU"),
                new ComponentCapability("intel-cpu-clocks", "CPU 频率", "intel-pcm", "CPU"),
                new ComponentCapability("intel-msr", "MSR 访问", "intel-pcm", "CPU")
            ],
            InstallNote: "Windows MSR driver/service 路线必须显式安装和验证，不能静默启用。")
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
        string fallbackProviderKind,
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
                ? fallbackProviderKind
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
            "hardware-monitor-wmi" => "Hardware Monitor WMI",
            "memory-temperature" => "内存温度",
            "motherboard-temperature" => "主板温度",
            "vrm-temperature" => "供电温度",
            "chipset-temperature" => "芯片组温度",
            "motherboard-voltage" => "主板电压",
            "disk-temperature" => "磁盘温度",
            "notebook-ec" => "笔记本 EC",
            "reference-telemetry" => "参考遥测",
            "shared-memory" => "共享内存",
            "kernel-trace" => "内核追踪",
            "dpc-isr" => "DPC/ISR 分析",
            "hard-pagefault" => "Hard Pagefault 分析",
            "driver-latency" => "驱动延迟归因",
            "latency-report" => "延迟报告",
            "smu-pm-table" => "SMU PM table",
            "stapm" => "STAPM",
            "per-core" => "每核指标",
            "webview2-runtime" => "Web 界面运行时",
            "wpr" => "Windows Performance Recorder",
            "xperf" => "Xperf",
            "wpa-export" => "WPA 导出",
            _ => capability
        };
    }
}
