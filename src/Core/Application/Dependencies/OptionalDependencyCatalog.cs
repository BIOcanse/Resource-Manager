using ResourceManager.App.Domain.Dependencies;

namespace ResourceManager.App.Application.Dependencies;

public static class OptionalDependencyCatalog
{
    public static IReadOnlyList<OptionalDependencyDefinition> Definitions { get; } =
    [
        new OptionalDependencyDefinition(
            Id: "shared-webview2-runtime",
            Vendor: "Microsoft",
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
            InstalledProductNames: ["Microsoft Edge WebView2 Runtime"]),
        new OptionalDependencyDefinition(
            Id: "amd-smu-pawnio-provider",
            Vendor: "namazso / AMD SMU",
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
            InstalledProductNames: ["PawnIO"],
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
            Vendor: "namazso / Intel",
            SourcePageUrl: "https://pawnio.eu/",
            DownloadUrl: null,
            ExternalTermsUrl: "https://pawnio.eu/",
            InstallerFileName: "PawnIO_setup.exe",
            InstallerFilePatterns: ["PawnIO_setup*.exe"],
            InstallDirectoryName: "PawnIO",
            RequiresExternalTermsAcknowledgement: true,
            RequiresElevation: true,
            InstalledProbeRelativePaths: ["PawnIOLib.dll"],
            InstalledProductNames: ["PawnIO"],
            ReleaseSource: new GitHubReleaseSource(
                Owner: "namazso", Repository: "PawnIO.Setup", VerifiedTag: "2.2.0",
                VerifiedAssetName: "PawnIO_setup.exe", AssetPatterns: ["PawnIO_setup*.exe"]),
            PayloadSource: new DependencyPayloadSource(
                Owner: "namazso", Repository: "PawnIO.Modules", VerifiedTag: "0.2.11",
                VerifiedAssetName: "release_0_2_11.zip", FileNames: ["IntelMSR.bin"])),
        new OptionalDependencyDefinition(
            Id: "hardware-sensors-pawnio-provider",
            Vendor: "namazso",
            SourcePageUrl: "https://pawnio.eu/", DownloadUrl: null,
            ExternalTermsUrl: "https://pawnio.eu/", InstallerFileName: "PawnIO_setup.exe",
            InstallerFilePatterns: ["PawnIO_setup*.exe"], InstallDirectoryName: "PawnIO",
            RequiresExternalTermsAcknowledgement: true, RequiresElevation: true,
            InstalledProbeRelativePaths: ["PawnIOLib.dll"],
            InstalledProductNames: ["PawnIO"],
            ReleaseSource: new GitHubReleaseSource(
                Owner: "namazso", Repository: "PawnIO.Setup", VerifiedTag: "2.2.0",
                VerifiedAssetName: "PawnIO_setup.exe", AssetPatterns: ["PawnIO_setup*.exe"])),
        new OptionalDependencyDefinition(
            Id: "hardware-bridge",
            Vendor: "Resource Manager",
            SourcePageUrl: "https://github.com/BIOcanse/Resource-Manager-HardwareBridge",
            DownloadUrl: null,
            ExternalTermsUrl: null,
            InstallerFileName: "ResourceManager.HardwareBridge-win-x64.zip",
            InstallerFilePatterns: ["ResourceManager.HardwareBridge-win-x64.zip"],
            InstallDirectoryName: "HardwareBridge",
            RequiresExternalTermsAcknowledgement: false,
            RequiresElevation: false,
            InstalledProbeRelativePaths: ["ResourceManager.HardwareBridge.exe"],
            InstalledProductNames: ["ResourceManager.HardwareBridge"],
            // 它单独一个进程、单独一个仓库，是因为许可证：它链接 ZenStates-Core（GPL-3.0），
            // 而本程序是 Apache-2.0，直接引用会把整个程序传染成 GPL。
            // 顺带的好处是隔离 —— 碰内核的代码崩了不拖累主程序。
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
