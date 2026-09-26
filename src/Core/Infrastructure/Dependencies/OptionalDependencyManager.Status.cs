using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.Messages;
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
        var sourceKind = definition.InstallerSourceKind;
        var canDownload = sourceKind != DependencyInstallerSourceKinds.Manual;
        var state = installed
            ? "installed"
            : installerAvailable
                ? "readyToInstall"
                : canDownload
                    ? "downloadable"
                    : "manualDownloadRequired";
        var messageCode = state switch
        {
            "installed" => BackendMessageCodes.Dependency.InstalledInManagedRoot,
            "readyToInstall" => BackendMessageCodes.Dependency.InstallerCached,
            "downloadable" => sourceKind == DependencyInstallerSourceKinds.GitHubRelease
                ? BackendMessageCodes.Dependency.DownloadableWithVersionChoice
                : BackendMessageCodes.Dependency.Downloadable,
            _ => BackendMessageCodes.Dependency.ManualAcquisition
        };
        var message = externalInstall is null
            ? BackendMessage.Create(BackendMessageDomains.Dependency, messageCode)
            : BackendMessage.Create(
                BackendMessageDomains.Dependency,
                BackendMessageCodes.Dependency.ReusingExternalInstall,
                externalInstall.InstallDirectory);

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
            message,
            sourceKind);
    }
}
