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
            InstalledProbeRelativePaths: ["PawnIOLib.dll"],
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
                FileNames: ["RyzenSMU.bin", "AMDFamily17.bin"])),
        new OptionalDependencyDefinition(
            Id: "intel-msr-pawnio-provider",
            Name: "Intel CPU / PawnIO",
            Vendor: "namazso / Intel",
            Category: "控制写入",
            SourcePageUrl: "https://pawnio.eu/",
            DownloadUrl: null,
            ExternalTermsUrl: "https://pawnio.eu/",
            InstallerFileName: "PawnIO_setup.exe",
            InstallerFilePatterns: ["PawnIO_setup*.exe"],
            InstallDirectoryName: "PawnIO",
            RequiresExternalTermsAcknowledgement: true,
            RequiresElevation: true,
            InstalledProbeRelativePaths: ["PawnIOLib.dll"],
            InstallNote: "显式安装官方签名 PawnIO 驱动后，点击验证获取 IntelMSR 模块；用于 Intel PL1/PL2、时间窗和 OC 邮箱电压偏移。固件锁定和签名模块未开放的寄存器不会被绕过。与 AMD 通道共享系统驱动，模块按组件独立管理。",
            ReleaseSource: new GitHubReleaseSource(
                Owner: "namazso", Repository: "PawnIO.Setup", VerifiedTag: "2.2.0",
                VerifiedAssetName: "PawnIO_setup.exe", AssetPatterns: ["PawnIO_setup*.exe"]),
            PayloadSource: new DependencyPayloadSource(
                Owner: "namazso", Repository: "PawnIO.Modules", VerifiedTag: "0.2.11",
                VerifiedAssetName: "release_0_2_11.zip", FileNames: ["IntelMSR.bin"])),
        new OptionalDependencyDefinition(
            Id: "hardware-sensors-pawnio-provider", Name: "Hardware sensors / PawnIO",
            Vendor: "namazso", Category: "Hardware sensor driver",
            SourcePageUrl: "https://pawnio.eu/", DownloadUrl: null,
            ExternalTermsUrl: "https://pawnio.eu/", InstallerFileName: "PawnIO_setup.exe",
            InstallerFilePatterns: ["PawnIO_setup*.exe"], InstallDirectoryName: "PawnIO",
            RequiresExternalTermsAcknowledgement: true, RequiresElevation: true,
            InstalledProbeRelativePaths: ["PawnIOLib.dll"],
            InstallNote: "官方核心库随产品提供，仅采集主板与内存；共享显式安装的 PawnIO 驱动，不启动 GUI 或网络服务。签名 LPC/EC/SMBus 模块由官方核心库自带。",
            ReleaseSource: new GitHubReleaseSource(
                Owner: "namazso", Repository: "PawnIO.Setup", VerifiedTag: "2.2.0",
                VerifiedAssetName: "PawnIO_setup.exe", AssetPatterns: ["PawnIO_setup*.exe"])),
        new OptionalDependencyDefinition(
            Id: "hardware-bridge",
            Name: "硬件写入辅助进程",
            Vendor: "Resource Manager",
            Category: "控制写入",
            SourcePageUrl: "https://github.com/BIOcanse/Resource-Manager-HardwareBridge",
            DownloadUrl: null,
            ExternalTermsUrl: null,
            InstallerFileName: "ResourceManager.HardwareBridge-win-x64.zip",
            InstallerFilePatterns: ["ResourceManager.HardwareBridge-win-x64.zip"],
            InstallDirectoryName: "HardwareBridge",
            RequiresExternalTermsAcknowledgement: false,
            RequiresElevation: false,
            InstalledProbeRelativePaths: ["ResourceManager.HardwareBridge.exe"],
            // 它单独一个进程、单独一个仓库，是因为许可证：它链接 ZenStates-Core（GPL-3.0），
            // 而本程序是 Apache-2.0，直接引用会把整个程序传染成 GPL。
            // 顺带的好处是隔离 —— 碰内核的代码崩了不拖累主程序。
            InstallNote: "处理器功耗上限和电压（Curve Optimizer）的写入都经过它。"
                + "它是独立程序，按 GPL-3.0 发布，由主程序随开随关。",
            ReleaseSource: new GitHubReleaseSource(
                Owner: "BIOcanse", Repository: "Resource-Manager-HardwareBridge", VerifiedTag: "v0.2.1",
                VerifiedAssetName: "ResourceManager.HardwareBridge-win-x64.zip", AssetPatterns: ["ResourceManager.HardwareBridge-win-x64.zip"]),
            PayloadSource: new DependencyPayloadSource(
                Owner: "BIOcanse",
                Repository: "Resource-Manager-HardwareBridge",
                VerifiedTag: "v0.2.1",
                VerifiedAssetName: "ResourceManager.HardwareBridge-win-x64.zip",
                FileNames: ["ResourceManager.HardwareBridge.exe", "LICENSE"], IncludeArchiveContents: true))

    ];

    public static OptionalDependencyDefinition? Find(string id)
    {
        return Definitions.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }
}
