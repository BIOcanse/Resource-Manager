using ResourceManager.App.Domain.Dependencies;

namespace ResourceManager.App.Application.Dependencies;

public static class OptionalDependencyCatalog
{
    public static IReadOnlyList<OptionalDependencyDefinition> Definitions { get; } =
    [
        new OptionalDependencyDefinition(
            Id: "shared-webview2-runtime",
            Name: "共享 WebView2 运行时",
            Vendor: "Microsoft",
            Category: "Shared web runtime",
            SourcePageUrl: "https://developer.microsoft.com/en-us/microsoft-edge/webview2/",
            DownloadUrl: "https://go.microsoft.com/fwlink/p/?LinkId=2124703",
            ExternalTermsUrl: "https://www.microsoft.com/en-us/legal/terms-of-use",
            InstallerFileName: "MicrosoftEdgeWebview2Setup.exe",
            InstallerFilePatterns:
            [
                "MicrosoftEdgeWebview2Setup*.exe"
            ],
            InstallDirectoryName: "shared-webview2-runtime",
            RequiresExternalTermsAcknowledgement: true,
            RequiresElevation: true,
            InstalledProbeRelativePaths: [],
            InstallNote: "Microsoft 官方 Evergreen Bootstrapper。安装后由全机 WebView2 软件共享，不随每个软件重复打包。"),
        new OptionalDependencyDefinition(
            Id: "amd-smu-pawnio-provider",
            Name: "AMD SMU / PawnIO Provider",
            Vendor: "namazso / AMD SMU",
            Category: "CPU telemetry provider",
            SourcePageUrl: "https://pawnio.eu/",
            DownloadUrl: null,
            ExternalTermsUrl: "https://pawnio.eu/",
            InstallerFileName: "PawnIO_setup.exe",
            InstallerFilePatterns:
            [
                "PawnIO_setup*.exe",
                "PawnIO*.exe"
            ],
            InstallDirectoryName: "PawnIO",
            RequiresExternalTermsAcknowledgement: true,
            RequiresElevation: true,
            InstalledProbeRelativePaths: [],
            InstallNote: "安装官方签名 PawnIO 驱动；AMD SMU Provider 后续通过 PawnIO 设备和 RyzenSMU 模块读取 PM table。该驱动必须显式安装和验证，不随基础包静默启用。",
            ReleaseSource: new GitHubReleaseSource(
                Owner: "namazso",
                Repository: "PawnIO.Setup",
                VerifiedTag: "2.2.0",
                VerifiedAssetName: "PawnIO_setup.exe",
                AssetPatterns: ["PawnIO_setup*.exe", "PawnIO*.exe"]),
            // 安装器只装驱动。读 PM table 要的模块在另一个仓库，
            // 不取这一份的话设备打得开但读数永远是空的。
            PayloadSource: new DependencyPayloadSource(
                Owner: "namazso",
                Repository: "PawnIO.Modules",
                VerifiedTag: "0.2.11",
                VerifiedAssetName: "release_0_2_11.zip",
                FileNames: ["RyzenSMU.bin"])),
        new OptionalDependencyDefinition(
            Id: "fan-control-core",
            Name: "风扇控制核心",
            Vendor: "Resource Manager",
            Category: "控制写入",
            SourcePageUrl: "https://github.com/BIOcanse/Fan-Control-Core",
            DownloadUrl: null,
            ExternalTermsUrl: null,
            InstallerFileName: null,
            InstallerFilePatterns: [],
            InstallDirectoryName: "FanControlCore",
            RequiresExternalTermsAcknowledgement: false,
            RequiresElevation: false,
            InstalledProbeRelativePaths: ["FanControlCore.exe"],
            // 它按**控制通道**分类（hwmon / WMI-ACPI / HID / Raw EC / SMM）
            // 而不按品牌：按品牌分会得到一个永远补不完的驱动库。
            // 单独一个仓库是为了别人也能直接用 ——
            // 笔记本风扇控制不该每个软件重造一遍。
            InstallNote: "机身风扇的读取与控制经过它。"
                + "它是独立程序，由主程序随开随关；"
                + "退出时会把风扇交还固件自动控制。",
            ReleaseSource: null,
            PayloadSource: new DependencyPayloadSource(
                Owner: "BIOcanse",
                Repository: "Fan-Control-Core",
                VerifiedTag: "v0.1.0",
                VerifiedAssetName: "FanControlCore-win-x64.zip",
                FileNames:
                [
                    "FanControlCore.exe",
                    "FanControlCore.dll",
                    "FanControlCore.deps.json",
                    "FanControlCore.runtimeconfig.json",
                    "System.CodeDom.dll",
                    "System.Management.dll",
                    "LICENSE"
                ])),
        new OptionalDependencyDefinition(
            Id: "hardware-bridge",
            Name: "硬件写入辅助进程",
            Vendor: "Resource Manager",
            Category: "控制写入",
            SourcePageUrl: "https://github.com/BIOcanse/Resource-Manager-HardwareBridge",
            DownloadUrl: null,
            ExternalTermsUrl: null,
            InstallerFileName: null,
            InstallerFilePatterns: [],
            InstallDirectoryName: "HardwareBridge",
            RequiresExternalTermsAcknowledgement: false,
            RequiresElevation: false,
            InstalledProbeRelativePaths: ["ResourceManager.HardwareBridge.exe"],
            // 它单独一个进程、单独一个仓库，是因为许可证：它链接 ZenStates-Core（GPL-3.0），
            // 而本程序是 Apache-2.0，直接引用会把整个程序传染成 GPL。
            // 顺带的好处是隔离 —— 碰内核的代码崩了不拖累主程序。
            InstallNote: "处理器功耗上限和电压（Curve Optimizer）的写入都经过它。"
                + "它是独立程序，按 GPL-3.0 发布，由主程序随开随关。",
            ReleaseSource: null,
            PayloadSource: new DependencyPayloadSource(
                Owner: "BIOcanse",
                Repository: "Resource-Manager-HardwareBridge",
                VerifiedTag: "v0.1.0",
                VerifiedAssetName: "ResourceManager.HardwareBridge-win-x64.zip",
                FileNames:
                [
                    "ResourceManager.HardwareBridge.exe",
                    "ResourceManager.HardwareBridge.dll",
                    "ResourceManager.HardwareBridge.deps.json",
                    "ResourceManager.HardwareBridge.runtimeconfig.json",
                    "ZenStates-Core.dll",
                    "inpoutx64.dll",
                    "System.CodeDom.dll",
                    "System.Management.dll",
                    "System.Diagnostics.EventLog.dll",
                    "System.Diagnostics.EventLog.Messages.dll",
                    "System.ServiceProcess.ServiceController.dll",
                    "LICENSE"
                ])),
        new OptionalDependencyDefinition(
            Id: "msi-afterburner",
            Name: "MSI Afterburner",
            Vendor: "MSI",
            Category: "External telemetry helper",
            SourcePageUrl: "https://www.msi.com/Landing/afterburner/graphics-cards",
            DownloadUrl: null,
            ExternalTermsUrl: "https://www.msi.com/Landing/afterburner/graphics-cards",
            InstallerFileName: "MSIAfterburnerSetup.exe",
            InstallerFilePatterns:
            [
                "MSIAfterburnerSetup*.exe",
                "*Afterburner*.exe"
            ],
            InstallDirectoryName: "MSI Afterburner",
            RequiresExternalTermsAcknowledgement: true,
            RequiresElevation: true,
            InstalledProbeRelativePaths:
            [
                "MSIAfterburner.exe"
            ],
            InstallNote: "MSI 官方直链对自动客户端不稳定，当前先使用官方来源页。"),
        new OptionalDependencyDefinition(
            Id: "amd-ryzen-master-monitoring-sdk",
            Name: "AMD Ryzen Master Monitoring SDK",
            Vendor: "AMD",
            Category: "CPU telemetry SDK",
            SourcePageUrl: "https://www.amd.com/en/developer/ryzen-master-monitoring-sdk.html",
            DownloadUrl: "https://download.amd.com/Desktop/amd-ryzen-master-monitoring-sdk_3.0.1.4971.exe",
            ExternalTermsUrl: "https://www.amd.com/en/developer/ryzen-master-monitoring-sdk/ryzen-master-monitoring-sdk-eula.html",
            InstallerFileName: "amd-ryzen-master-monitoring-sdk_3.0.1.4971.exe",
            InstallerFilePatterns:
            [
                "amd-ryzen-master-monitoring-sdk_*.exe"
            ],
            InstallDirectoryName: "AMD Ryzen Master Monitoring SDK",
            RequiresExternalTermsAcknowledgement: true,
            RequiresElevation: true,
            InstalledProbeRelativePaths:
            [
                "bin\\Device.dll",
                "bin\\Platform.dll",
                "bin\\AMDRyzenMasterDriver.sys"
            ],
            InstallNote: "安装器包含 AMD 驱动/运行时组件，必须显式处理。"),
        new OptionalDependencyDefinition(
            Id: "librehardwaremonitor-provider",
            Name: "LibreHardwareMonitor Provider",
            Vendor: "LibreHardwareMonitor",
            Category: "Hardware sensor bridge",
            SourcePageUrl: "https://github.com/LibreHardwareMonitor/LibreHardwareMonitor",
            DownloadUrl: null,
            ExternalTermsUrl: "https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/LICENSE",
            InstallerFileName: "LibreHardwareMonitor.zip",
            InstallerFilePatterns:
            [
                "LibreHardwareMonitor*.zip",
                "LibreHardwareMonitor*.exe"
            ],
            InstallDirectoryName: "LibreHardwareMonitor",
            RequiresExternalTermsAcknowledgement: true,
            RequiresElevation: true,
            InstalledProbeRelativePaths:
            [
                "LibreHardwareMonitor.exe"
            ],
            InstallNote: "通用硬件监控桥；用于 CPU/系统风扇、内存温度、主板/电压等传感缺口。运行时可通过 WMI 暴露传感器；涉及驱动/管理员权限时必须显式确认。",
            ReleaseSource: new GitHubReleaseSource(
                Owner: "LibreHardwareMonitor",
                Repository: "LibreHardwareMonitor",
                VerifiedTag: "v0.9.6",
                VerifiedAssetName: "LibreHardwareMonitor.zip",
                // 顺序即偏好：先取随 Windows 自带运行时即可跑的那份，再退到其他 zip。
                AssetPatterns: ["LibreHardwareMonitor.zip", "LibreHardwareMonitor*.zip"])),
        new OptionalDependencyDefinition(
            Id: "notebook-fancontrol-provider",
            Name: "Notebook FanControl Provider",
            Vendor: "NBFC",
            Category: "Notebook EC fan provider",
            SourcePageUrl: "https://github.com/hirschmann/nbfc",
            DownloadUrl: null,
            ExternalTermsUrl: "https://github.com/hirschmann/nbfc/blob/master/LICENSE.md",
            InstallerFileName: "NBFCInstaller.exe",
            InstallerFilePatterns:
            [
                "NBFC*.exe",
                "NoteBookFanControl*.exe",
                "NBFC*.msi",
                "NoteBookFanControl*.msi"
            ],
            InstallDirectoryName: "NoteBook FanControl",
            RequiresExternalTermsAcknowledgement: true,
            RequiresElevation: true,
            InstalledProbeRelativePaths: [],
            InstallNote: "笔记本 EC 风扇路线；依赖机型配置，用于显卡驱动和通用硬件监控都不暴露风扇时的候选 Provider。不能静默启用或自动写 EC。",
            ReleaseSource: new GitHubReleaseSource(
                Owner: "hirschmann",
                Repository: "nbfc",
                VerifiedTag: "1.6.3",
                VerifiedAssetName: "NoteBookFanControl.1.6.3.setup.exe",
                AssetPatterns: ["NoteBookFanControl*.setup.exe", "NBFC*.exe"])),
        new OptionalDependencyDefinition(
            Id: "windows-performance-toolkit",
            Name: "Windows Performance Toolkit",
            Vendor: "Microsoft",
            Category: "System trace and latency diagnostics",
            SourcePageUrl: "https://learn.microsoft.com/en-us/windows-hardware/get-started/adk-install",
            DownloadUrl: "https://go.microsoft.com/fwlink/?linkid=2289980",
            ExternalTermsUrl: "https://www.microsoft.com/en-us/legal/terms-of-use",
            InstallerFileName: "adksetup-10.1.26100.2454.exe",
            InstallerFilePatterns:
            [
                "adksetup*.exe"
            ],
            InstallDirectoryName: "windows-performance-toolkit",
            RequiresExternalTermsAcknowledgement: true,
            RequiresElevation: true,
            InstalledProbeRelativePaths:
            [
                "Windows Performance Toolkit\\wpr.exe",
                "Windows Performance Toolkit\\xperf.exe",
                "Windows Performance Toolkit\\wpa.exe",
                "Windows Performance Toolkit\\wpaexporter.exe"
            ],
            InstallNote: "Windows ADK 的 Windows Performance Toolkit 功能；安装命令应只选择 OptionId.WindowsPerformanceToolkit。Kernel trace 启动需要管理员或受限提权 helper。"),
        new OptionalDependencyDefinition(
            Id: "latencymon",
            Name: "LatencyMon",
            Vendor: "Resplendence",
            Category: "Latency diagnostics helper",
            SourcePageUrl: "https://www.resplendence.com/latencymon",
            DownloadUrl: "https://www.resplendence.com/download/LatencyMon.exe",
            ExternalTermsUrl: "https://www.resplendence.com/latencymon",
            InstallerFileName: "LatencyMon-7.31.exe",
            InstallerFilePatterns:
            [
                "LatencyMon*.exe"
            ],
            InstallDirectoryName: "LatencyMon",
            RequiresExternalTermsAcknowledgement: true,
            RequiresElevation: true,
            InstalledProbeRelativePaths:
            [
                "LatMon.exe",
                "rspLLL64.sys"
            ],
            InstallNote: "LatencyMon 可作为 ISR/DPC/hard pagefault 辅助诊断工具；它带厂商内核驱动，但没有公开稳定 API，主链路仍使用 ETW/WPT。")
    ];

    public static OptionalDependencyDefinition? Find(string id)
    {
        return Definitions.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }
}
