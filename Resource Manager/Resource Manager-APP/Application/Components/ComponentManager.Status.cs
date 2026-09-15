using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.Metrics;

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
            providerActive
                ? "Provider 已通过实时指标验证。"
                : providerRuntimeAvailable
                    ? "运行库可用，但 Provider 桥接或实时读数验证尚未完成。"
                    : "Provider 桥接尚未接入，或当前硬件/驱动未返回可验证读数。");
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
            providerActive
                ? "内置组件已通过实时指标验证。"
                : string.IsNullOrWhiteSpace(providerMessage)
                    ? "内置组件已安装；当前硬件或 OEM 运行库未返回可验证读数。"
                    : providerMessage);
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
            ? "Provider 已通过实时指标验证。"
            : providerRuntimeAvailable
                ? "检测到系统已安装的 Provider 运行库，但还没有通过实时读数验证。"
            : state == "InstalledUnverified"
                ? "组件文件已就绪，但 Provider 还没有通过实时读数验证。"
            : state == "Installed"
                ? dependencyStatus.Message
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
