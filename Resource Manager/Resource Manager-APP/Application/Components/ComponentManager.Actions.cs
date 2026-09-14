using ResourceManager.App.Domain.Components;

namespace ResourceManager.App.Application.Components;

public sealed partial class ComponentManager
{
    public async Task<ComponentActionResult> DownloadAsync(
        string id,
        bool acknowledgeExternalTerms,
        CancellationToken cancellationToken)
    {
        EnsureOptionalDependency(id);
        var result = await dependencyManager.DownloadAsync(id, acknowledgeExternalTerms, cancellationToken);
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
                "组件已安装，直接复用现有安装。",
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

            await dependencyManager.DownloadAsync(id, acknowledgeExternalTerms, cancellationToken);
        }

        var result = await dependencyManager.LaunchInstallerAsync(id, acknowledgeExternalTerms, cancellationToken);
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
        var status = await GetStatusAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Unknown component: {id}");
        return new ComponentActionResult(
            id,
            status.State,
            status.ProviderActive ? "Provider 已通过实时指标验证。" : status.Message,
            status.InstallerPath,
            null,
            status);
    }
}
