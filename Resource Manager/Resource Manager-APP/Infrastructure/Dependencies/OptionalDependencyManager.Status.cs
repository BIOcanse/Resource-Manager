using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Infrastructure.Dependencies;

public sealed partial class OptionalDependencyManager
{
    private OptionalDependencyStatus BuildStatus(
        OptionalDependencyDefinition definition,
        IReadOnlyList<InstalledSoftwareEntry> installedSoftware)
    {
        var paths = GetPaths(definition);
        var installerPath = ResolveInstallerPath(definition, paths.InstallerDirectory);
        var installerAvailable = installerPath is not null;
        var managedInstalled = IsDependencyInstalledAt(definition, paths.InstallDirectory);
        var externalInstall = managedInstalled
            ? null
            : ResolveExternalInstall(definition, installedSoftware, paths.InstallDirectory);
        var installed = managedInstalled || externalInstall is not null;
        var effectiveInstallDirectory = externalInstall?.InstallDirectory ?? paths.InstallDirectory;
        var detectionSource = managedInstalled ? "ManagedDependencies" : externalInstall?.Source ?? "None";
        var canDownload = !string.IsNullOrWhiteSpace(definition.DownloadUrl);
        var state = installed
            ? "installed"
            : installerAvailable
                ? "readyToInstall"
                : canDownload
                    ? "downloadable"
                    : "manualDownloadRequired";
        var message = state switch
        {
            "installed" => "已安装在 Dependencies 软件根目录。",
            "readyToInstall" => "安装器已在 Misc 安装器缓存中，可安装到 Dependencies。",
            "downloadable" => "可从官方来源下载安装器。",
            _ => "打开官方来源页，并将安装器放入 Misc 安装器缓存。"
        };
        if (externalInstall is not null)
        {
            message = $"检测到系统已有安装，直接复用：{externalInstall.InstallDirectory}";
        }

        return new OptionalDependencyStatus(
            definition,
            state,
            paths.DependencyRoot,
            paths.InstallerDirectory,
            paths.InstallDirectory,
            installerPath,
            installerAvailable,
            installed,
            !installed && canDownload,
            !installed && installerAvailable,
            effectiveInstallDirectory,
            externalInstall?.InstallDirectory,
            detectionSource,
            message);
    }
}
