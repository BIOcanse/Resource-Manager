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
        var managedRuntimeInstalled = IsDependencyInstalledAt(definition, paths.InstallDirectory);
        var managedRecordPath = DependencyReleaseRecordStore.InstalledPath(paths.InstallDirectory);
        var managedRecord = DependencyReleaseRecordStore.Read(
            managedRecordPath, logger);
        var ownsInstalledProbe = PayloadSourceOwnsInstalledProbe(definition);
        var managedReceiptExists = ownsInstalledProbe && DependencyInstallPaths.IsRegularFile(managedRecordPath);
        var liveDirectoryExists = Directory.Exists(paths.InstallDirectory);
        IReadOnlyList<string> interruptedBackups = Array.Empty<string>();
        if (!liveDirectoryExists && definition.PayloadSource is not null)
        {
            try
            {
                interruptedBackups = DependencyPayloadTransaction.FindInterruptedBackups(paths.InstallDirectory);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(error, "Could not inspect interrupted component payload backups for {ComponentId}.", definition.Id);
            }
        }
        var interruptedReplacement = interruptedBackups.Count > 0;
        var managedInstalled = managedRuntimeInstalled || managedReceiptExists;
        // Keep external runtime discovery independent from a module receipt: module payloads
        // do not include PawnIOLib.dll and cannot claim that the driver/runtime is installed.
        var externalInstall = managedRuntimeInstalled
            ? null
            : ResolveExternalInstall(definition, installedSoftware, paths.InstallDirectory);
        var installed = managedInstalled || externalInstall is not null;
        var payloadFilesPresent = definition.PayloadSource is not { } payload
            || payload.FileNames.All(name => DependencyInstallPaths.IsRegularFile(Path.Combine(paths.InstallDirectory, name)));
        // An external runtime stays external; missing owned module files may be explicitly installed in our root.
        var updateAvailable = interruptedReplacement
            || ((managedInstalled || externalInstall is not null && !payloadFilesPresent && definition.PayloadSource is not null)
                && (definition.ReleaseSource is not null || definition.PayloadSource is not null)
                && !DependencyReleaseRecordStore.MatchesPin(managedRecord, definition));
        var effectiveInstallDirectory = externalInstall?.InstallDirectory ?? paths.InstallDirectory;
        var detectionSource = interruptedReplacement
            ? "ManagedDependenciesInterruptedReplacement"
            : managedInstalled
                ? "ManagedDependencies"
                : externalInstall?.Source ?? "None";
        var sourceKind = definition.InstallerSourceKind;
        var canDownload = sourceKind != DependencyInstallerSourceKinds.Manual;
        var state = updateAvailable && !installed
            ? "update_available"
            : installed
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

    private static bool PayloadSourceOwnsInstalledProbe(OptionalDependencyDefinition definition)
        => definition.PayloadSource is { } payload
            && definition.InstalledProbeRelativePaths.Count > 0
            && definition.InstalledProbeRelativePaths.All(probe => payload.FileNames.Any(name =>
                string.Equals(name, probe, StringComparison.OrdinalIgnoreCase)));
}
