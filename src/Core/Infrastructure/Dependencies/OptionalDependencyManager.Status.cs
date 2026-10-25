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
        var payloadFilesPresent = definition.PayloadSource is not { } payload
            || payload.FileNames.All(name => DependencyInstallPaths.IsRegularFile(Path.Combine(paths.InstallDirectory, name)));
        // An external runtime stays external; missing owned module files may be explicitly installed in our root.
        var updateAvailable = (managedInstalled || externalInstall is not null && !payloadFilesPresent && definition.PayloadSource is not null)
            && (definition.ReleaseSource is not null || definition.PayloadSource is not null)
            && !DependencyReleaseRecordStore.MatchesPin(
                DependencyReleaseRecordStore.Read(DependencyReleaseRecordStore.InstalledPath(paths.InstallDirectory), logger), definition);
        var effectiveInstallDirectory = externalInstall?.InstallDirectory ?? paths.InstallDirectory;
        var detectionSource = managedInstalled ? "ManagedDependencies" : externalInstall?.Source ?? "None";
        var sourceKind = definition.InstallerSourceKind;
        var canDownload = sourceKind != DependencyInstallerSourceKinds.Manual;
        var state = installed
            ? !payloadFilesPresent ? "payload_missing" : updateAvailable ? "update_available" : "installed"
            : installerAvailable
                ? "readyToInstall"
                : canDownload
                    ? "downloadable"
                    : "manualDownloadRequired";
        var messageCode = updateAvailable ? BackendMessageCodes.Dependency.UpdateAvailable : state switch
        {
            "payload_missing" => BackendMessageCodes.Dependency.PayloadMissing,
            "update_available" => BackendMessageCodes.Dependency.UpdateAvailable,
            "installed" => BackendMessageCodes.Dependency.InstalledInManagedRoot,
            "readyToInstall" => BackendMessageCodes.Dependency.InstallerCached,
            "downloadable" => sourceKind == DependencyInstallerSourceKinds.GitHubRelease
                ? BackendMessageCodes.Dependency.DownloadableWithVersionChoice
                : BackendMessageCodes.Dependency.Downloadable,
            _ => BackendMessageCodes.Dependency.ManualAcquisition
        };
        var message = externalInstall is null || state == "payload_missing"
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
            (!installed || updateAvailable) && canDownload,
            (!installed || updateAvailable) && installerAvailable,
            effectiveInstallDirectory,
            externalInstall?.InstallDirectory,
            detectionSource,
            message,
            sourceKind,
            payloadFilesPresent,
            updateAvailable);
    }
}
