using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Domain.Components;

using ResourceManager.App.Domain.Messages;

namespace ResourceManager.App.Application.Components;

public sealed partial class ComponentManager
{
    public async Task<ComponentActionResult> DownloadAsync(
        string id,
        bool acknowledgeExternalTerms,
        string? versionChoice,
        CancellationToken cancellationToken)
    {
        EnsureOptionalDependency(id);
        var result = await dependencyManager.DownloadAsync(id, acknowledgeExternalTerms, versionChoice, cancellationToken);
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
        CancellationToken cancellationToken)
    {
        EnsureOptionalDependency(id);
        var statusBeforeInstall = await dependencyManager.GetStatusAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Unknown component: {id}");
        if (statusBeforeInstall.Installed)
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

        if (!statusBeforeInstall.InstallerAvailable)
        {
            if (!statusBeforeInstall.CanDownload)
            {
                throw new FileNotFoundException("托管依赖目录中没有可用安装器，且该组件没有稳定官方下载地址。", statusBeforeInstall.InstallerPath);
            }

            await dependencyManager.DownloadAsync(id, acknowledgeExternalTerms, versionChoice, cancellationToken);
        }

        var result = await dependencyManager.LaunchInstallerAsync(id, acknowledgeExternalTerms, versionChoice, cancellationToken);
        var status = await GetStatusAsync(id, cancellationToken);
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
        // 验证之前先把安装器之外的运行时文件补齐。
        // 用户点「验证」的意思就是"把这条路弄通"，而不是"告诉我还差什么文件"。
        var definition = OptionalDependencyCatalog.Find(id);
        if (definition is not null)
        {
            await payloadAcquisition.EnsureAsync(definition, cancellationToken);
        }

        var status = await GetStatusAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Unknown component: {id}");
        return new ComponentActionResult(
            id,
            status.State,
            status.ProviderActive
                ? BackendMessage.Create(
                    BackendMessageDomains.Dependency,
                    BackendMessageCodes.Dependency.ProviderVerified)
                : status.Message,
            status.InstallerPath,
            null,
            status);
    }
}
