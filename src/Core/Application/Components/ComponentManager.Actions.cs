using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Application.BrowserRuntimes;

using ResourceManager.App.Domain.Messages;

namespace ResourceManager.App.Application.Components;

public sealed partial class ComponentManager
{
    public async Task<ComponentActionResult> DownloadAsync(
        string id,
        bool acknowledgeExternalTerms,
        string? versionChoice,
        CancellationToken cancellationToken,
        IProgress<DependencyDownloadProgress>? progress = null)
    {
        EnsureOptionalDependency(id);
        var result = await dependencyManager.DownloadAsync(id, acknowledgeExternalTerms, versionChoice, cancellationToken, progress);
        var status = await GetStatusAsync(id, cancellationToken);
        return new ComponentActionResult(
            id,
            result.State,
            result.Message,
            result.FilePath,
            result.BytesWritten,
            status,
            result.FileChanges);
    }

    public async Task<ComponentActionResult> InstallAsync(
        string id,
        bool acknowledgeExternalTerms,
        string? versionChoice,
        CancellationToken cancellationToken,
        ResourceManager.App.Domain.Software.SoftwareOperationCaller? caller = null,
        IProgress<DependencyDownloadProgress>? progress = null)
    {
        EnsureOptionalDependency(id);
        // An install request decides from the files as they are now, not from the held probe.
        dependencyManager.InvalidateStatuses();
        var statusBeforeInstall = await dependencyManager.GetStatusAsync(id, caller?.WindowsSid, cancellationToken)
            ?? throw new InvalidOperationException($"Unknown component: {id}");
        if (statusBeforeInstall.Installed && !statusBeforeInstall.UpdateAvailable)
        {
            var currentStatus = await GetStatusAsync(id, cancellationToken);
            return new ComponentActionResult(
                id,
                statusBeforeInstall.State,
                BackendMessage.Create(
                    BackendMessageDomains.Dependency,
                    BackendMessageCodes.Dependency.AlreadyInstalledReuse),
                statusBeforeInstall.InstallerPath,
                null,
                currentStatus);
        }

        var definition = statusBeforeInstall.Definition;
        OptionalDependencyCatalog.EnsureTerms(definition, acknowledgeExternalTerms);
        using var change = inventoryRevisionOwner?.BeginComponentChange(id);
        if (statusBeforeInstall.UpdateAvailable && !string.IsNullOrWhiteSpace(versionChoice)
            && versionChoice != DependencyVersionChoices.Verified)
            throw new InvalidOperationException("Component updates require the catalog's verified release.");
        if (statusBeforeInstall.UpdateAvailable && definition.PayloadSource is not null)
        {
            // Modules, helpers, and interrupted owned replacements use the catalog's pinned payload path.
            var payload = await payloadAcquisition.InstallAsync(definition, cancellationToken);
            if (!payload.Satisfied)
                return new(id, "runtimeFilesUnavailable",
                    BackendMessage.Create(BackendMessageDomains.Dependency, BackendMessageCodes.Dependency.RuntimeFilesUnavailable),
                    Status: await GetStatusAsync(id, cancellationToken));
            var updated = await ReadStatusAsync(id, caller?.WindowsSid, cancellationToken, contentChanged: payload.Changed);
            return new(id, updated!.State, updated.Message, Status: updated);
        }

        if (!statusBeforeInstall.InstallerAvailable || definition.ReleaseSource is not null)
        {
            if (!statusBeforeInstall.CanDownload)
            {
                throw new FileNotFoundException("托管依赖目录中没有可用安装器，且该组件没有稳定官方下载地址。", statusBeforeInstall.InstallerPath);
            }

            await dependencyManager.DownloadAsync(id, acknowledgeExternalTerms, versionChoice, cancellationToken, progress);
        }

        var result = await dependencyManager.LaunchInstallerAsync(id, acknowledgeExternalTerms, versionChoice, cancellationToken, caller);
        // A completed owned installer may outlive the caller's cancellation request. Once it
        // reports completion, finish payload publication and status reconciliation before
        // releasing the component-change reservation.
        var completionToken = result.State == "installerCompleted" ? CancellationToken.None : cancellationToken;
        if (definition.PayloadSource is { IncludeArchiveContents: false })
        {
            var payload = await payloadAcquisition.InstallAsync(definition, completionToken);
            if (!payload.Satisfied)
                return new(id, "runtimeFilesUnavailable",
                    BackendMessage.Create(BackendMessageDomains.Dependency, BackendMessageCodes.Dependency.RuntimeFilesUnavailable),
                    Status: await ReadStatusAsync(id, caller?.WindowsSid, completionToken, contentChanged: true));
        }
        if (id.Equals(BrowserRuntimeCatalogIds.InstallComponentId, StringComparison.OrdinalIgnoreCase)
            && browserRuntimeCatalog is not null)
        {
            try
            {
                _ = await browserRuntimeCatalog.GetSnapshotAsync(forceRefresh: true, completionToken);
            }
            catch (Exception exception)
            {
                logger?.LogWarning(exception,
                    "Could not refresh browser runtime inventory after component install {ComponentId}.", id);
            }
        }
        var status = await ReadStatusAsync(id, caller?.WindowsSid, completionToken, contentChanged: true);
        return new ComponentActionResult(
            id,
            result.State,
            result.Message,
            result.InstallerPath,
            null,
            status);
    }

    public async Task<ComponentActionResult> VerifyAsync(
        string id,
        CancellationToken cancellationToken)
    {
        var definition = OptionalDependencyCatalog.Find(id);
        var changed = false;
        // Verify is the explicit re-probe: it never answers from the held probe result.
        dependencyManager.InvalidateStatuses();
        using var change = inventoryRevisionOwner?.BeginComponentChange(id);
        if (definition is not null)
        {
            var dependency = await dependencyManager.GetStatusAsync(id, cancellationToken);
            if (dependency?.UpdateAvailable == true)
                return new(id, ComponentStatusStates.UpdateAvailable,
                    BackendMessage.Create(BackendMessageDomains.Dependency, BackendMessageCodes.Dependency.UpdateAvailable),
                    Status: await GetStatusAsync(id, cancellationToken));
            var payload = await payloadAcquisition.EnsureAsync(definition, cancellationToken);
            changed = payload.Changed;
            if (!payload.Satisfied)
            {
                return new ComponentActionResult(id, "runtimeFilesUnavailable",
                    BackendMessage.Create(BackendMessageDomains.Dependency,
                        BackendMessageCodes.Dependency.RuntimeFilesUnavailable),
                    Status: await GetStatusAsync(id, cancellationToken));
            }
        }

        var status = await ReadStatusAsync(id, null, cancellationToken, contentChanged: changed)
            ?? throw new InvalidOperationException($"Unknown component: {id}");
        return new ComponentActionResult(
            id,
            status.State,
            status.ProviderActive
                ? BackendMessage.Create(
                    BackendMessageDomains.Dependency,
                    BackendMessageCodes.Dependency.ProviderVerified)
                : definition?.PayloadSource is not null
                    ? BackendMessage.Create(BackendMessageDomains.Dependency,
                        BackendMessageCodes.Dependency.ComponentFilesUnverified)
                    : status.Message,
            status.InstallerPath,
            null,
            status);
    }
}
