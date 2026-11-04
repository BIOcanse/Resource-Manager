using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Application.Operations;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Operations;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.ServiceHosting;
using ResourceManager.Shared.Runtime;
using Microsoft.Extensions.Logging;

namespace ResourceManager.App.Infrastructure.Software;

public sealed class SoftwareOperationManager(
    IOptionalDependencyManager dependencyManager,
    IInstalledSoftwareInventory installedSoftwareInventory,
    IFileChangeTracker fileChangeTracker,
    IInteractiveUserSessionBroker sessionBroker,
    ISoftwareRegistryView? softwareRegistryView = null,
    ILogger<SoftwareOperationManager>? logger = null) : ISoftwareOperationManager
{
    private const string DependencyPrefix = "dependency:";

    public async Task<SoftwareOperationResult> UninstallAsync(
        SoftwareOperationRequest request,
        SoftwareOperationCaller? caller,
        CancellationToken cancellationToken)
    {
        if (!request.ConfirmOperation)
        {
            throw new InvalidOperationException("执行卸载/清理前需要明确确认。");
        }

        if (request.Id.StartsWith(DependencyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var dependencyId = request.Id[DependencyPrefix.Length..];
            var result = await CleanupDependencyRootAsync(dependencyId, cancellationToken);
            if (softwareRegistryView is not null)
            {
                try
                {
                    _ = await softwareRegistryView.GetSoftwareAsync(caller?.WindowsSid, cancellationToken);
                }
                catch (Exception exception)
                {
                    logger?.LogWarning(exception,
                        "Could not refresh software inventory after managed dependency cleanup {DependencyId}.",
                        dependencyId);
                }
            }
            return result;
        }

        if (request.Id.StartsWith("windows-installed:", StringComparison.OrdinalIgnoreCase))
        {
            return await LaunchWindowsUninstallerAsync(request.Id, caller, cancellationToken);
        }

        throw new InvalidOperationException("该软件当前没有可执行的卸载策略。");
    }

    private async Task<SoftwareOperationResult> CleanupDependencyRootAsync(
        string dependencyId,
        CancellationToken cancellationToken)
    {
        var status = await dependencyManager.GetStatusAsync(dependencyId, cancellationToken)
            ?? throw new InvalidOperationException("未知依赖。");

        var dependencyRoot = NormalizePath(status.DependencyRoot);
        var installDirectory = NormalizePath(status.InstallDirectory);
        if (!IsStrictChildPath(installDirectory, dependencyRoot))
        {
            throw new InvalidOperationException("拒绝清理：目标不在受管 Dependencies 根目录下。");
        }

        var scope = new FileChangeTrackingScope(
            $"dependency-uninstall:{dependencyId}",
            [installDirectory]);
        var before = await fileChangeTracker.CaptureAsync(scope, cancellationToken);

        if (Directory.Exists(installDirectory))
        {
            var attributes = File.GetAttributes(installDirectory);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("拒绝清理：受管依赖根目录是 reparse point/junction。");
            }

            Directory.Delete(installDirectory, recursive: true);
            // The removed root is no longer what the held dependency probe described.
            dependencyManager.InvalidateStatuses();
        }

        var after = await fileChangeTracker.CaptureAsync(scope, cancellationToken);
        var fileChanges = fileChangeTracker.Compare(before, after);

        return new SoftwareOperationResult(
            $"dependency:{dependencyId}",
            "RemovedManagedRoot",
            BackendMessage.Create(BackendMessageDomains.Software, BackendMessageCodes.Software.ManagedRootRemoved),
            fileChanges);
    }

    private async Task<SoftwareOperationResult> LaunchWindowsUninstallerAsync(
        string id,
        SoftwareOperationCaller? caller,
        CancellationToken cancellationToken)
    {
        ValidateUninstallerCaller(caller, sessionBroker);
        var installedSoftware = await installedSoftwareInventory.GetInstalledSoftwareAsync(caller!.WindowsSid, cancellationToken);
        var entry = installedSoftware.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("未在 Windows 已安装软件清单中找到该条目。");

        if (string.IsNullOrWhiteSpace(entry.UninstallString))
        {
            throw new InvalidOperationException("缺少卸载入口，Resource Manager 不会删除未知软件根目录。");
        }

        var command = WindowsInstalledSoftwareInventory.ParseExecutableCommandLine(entry.UninstallString);
        cancellationToken.ThrowIfCancellationRequested();
        var shellRequest = new UserShellExecuteRequest(command.FileName, command.Arguments);
        _ = SessionAgentLauncher.Launch(sessionBroker, caller, shellRequest);

        return new SoftwareOperationResult(
            id,
            "UninstallerLaunchRequested",
            BackendMessage.Create(BackendMessageDomains.Software, BackendMessageCodes.Software.UninstallerLaunchRequested));
    }

    internal static void ValidateUninstallerCaller(SoftwareOperationCaller? caller, IInteractiveUserSessionBroker broker)
    {
        UserSessionCallerValidation.RequireActiveUser(caller, broker);
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsStrictChildPath(string candidate, string root)
    {
        return !candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
            && candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

}
