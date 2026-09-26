using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.Metrics;

using ResourceManager.App.Domain.Messages;

namespace ResourceManager.App.Application.Components;

public sealed partial class ComponentManager
{
    private ComponentStatus BuildStatus(
        ComponentDefinition definition,
        IReadOnlyList<OptionalDependencyStatus> dependencyStatuses,
        HardwareMetricSnapshot snapshot)
    {
        var dependencyStatus = dependencyStatuses.FirstOrDefault(item =>
            item.Definition.Id.Equals(definition.Id, StringComparison.OrdinalIgnoreCase));
        var providerStatus = BuildProviderStatus(definition, snapshot, dependencyStatus);
        var providerActive = providerStatus.Any(static provider => provider.State == "Active");
        var providerRuntimeAvailable = providerStatus.Any(static provider =>
            provider.State is "RuntimeAvailable" or "InstalledUnverified");

        if (IsBundledComponent(definition))
        {
            return BuildBundledComponentStatus(
                definition,
                providerStatus,
                providerActive,
                providerRuntimeAvailable);
        }

        if (dependencyStatus is not null)
        {
            return BuildOptionalDependencyStatus(
                definition,
                dependencyStatus,
                providerStatus,
                providerActive,
                providerRuntimeAvailable);
        }

        var state = providerActive
            ? "Active"
            : providerRuntimeAvailable
                ? "InstalledUnverified"
                : "ProviderMissing";
        return new ComponentStatus(
            definition,
            state,
            StateLabel(state),
            "Driver/runtime supplied",
            "",
            null,
            false,
            providerActive || providerRuntimeAvailable,
            providerActive,
            false,
            false,
            true,
            providerStatus,
            BackendMessage.Create(
                BackendMessageDomains.Dependency,
                providerActive
                    ? BackendMessageCodes.Dependency.ProviderVerified
                    : providerRuntimeAvailable
                        ? BackendMessageCodes.Dependency.RuntimeAvailableBridgePending
                        : BackendMessageCodes.Dependency.ProviderBridgeMissing));
    }

    private static ComponentStatus BuildBundledComponentStatus(
        ComponentDefinition definition,
        IReadOnlyList<ComponentProviderStatus> providerStatus,
        bool providerActive,
        bool providerRuntimeAvailable)
    {
        var state = providerActive
            ? "Active"
            : providerRuntimeAvailable
                ? "InstalledUnverified"
                : "Installed";
        var providerMessage = providerStatus.FirstOrDefault()?.Message;

        return new ComponentStatus(
            definition,
            state,
            StateLabel(state),
            "Bundled with Resource Manager",
            "",
            null,
            false,
            true,
            providerActive,
            false,
            false,
            true,
            providerStatus,
            BackendMessage.Create(
                BackendMessageDomains.Dependency,
                providerActive
                    ? BackendMessageCodes.Dependency.BundledVerified
                    : BackendMessageCodes.Dependency.BundledUnverified));
    }

    private static ComponentStatus BuildOptionalDependencyStatus(
        ComponentDefinition definition,
        OptionalDependencyStatus dependencyStatus,
        IReadOnlyList<ComponentProviderStatus> providerStatus,
        bool providerActive,
        bool providerRuntimeAvailable)
    {
        var state = providerActive
            ? "Active"
            : providerRuntimeAvailable
                ? "InstalledUnverified"
                : dependencyStatus.State switch
                {
                    "installed" => "Installed",
                    "readyToInstall" => "ReadyToInstall",
                    "downloadable" => "Available",
                    "manualDownloadRequired" => "ManualDownloadRequired",
                    _ => dependencyStatus.State
                };
        var message = providerActive
            ? BackendMessage.Create(
                BackendMessageDomains.Dependency,
                BackendMessageCodes.Dependency.ProviderVerified)
            : providerRuntimeAvailable
                ? BackendMessage.Create(
                    BackendMessageDomains.Dependency,
                    BackendMessageCodes.Dependency.ProviderRuntimeUnverified)
                : state == "InstalledUnverified"
                    ? BackendMessage.Create(
                        BackendMessageDomains.Dependency,
                        BackendMessageCodes.Dependency.ComponentFilesUnverified)
                    : dependencyStatus.Message;

        var effectiveInstalled = dependencyStatus.Installed || providerRuntimeAvailable;

        return new ComponentStatus(
            definition,
            state,
            StateLabel(state),
            dependencyStatus.EffectiveInstallDirectory,
            dependencyStatus.InstallerDirectory,
            dependencyStatus.InstallerPath,
            dependencyStatus.InstallerAvailable,
            effectiveInstalled,
            providerActive,
            !effectiveInstalled && dependencyStatus.CanDownload,
            !effectiveInstalled && (dependencyStatus.CanLaunchInstaller || dependencyStatus.CanDownload),
            true,
            providerStatus,
            message,
            dependencyStatus.InstallerSourceKind);
    }
}
