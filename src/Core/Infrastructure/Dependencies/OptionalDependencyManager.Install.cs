using System.IO.Compression;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.Shared.BrowserRuntimes;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.ServiceHosting;
using ResourceManager.Shared.Runtime;

using ResourceManager.App.Domain.Messages;

namespace ResourceManager.App.Infrastructure.Dependencies;

public sealed partial class OptionalDependencyManager
{
    public async Task<OptionalDependencyLaunchResult> LaunchInstallerAsync(
        string id,
        bool acknowledgeExternalTerms,
        string? versionChoice,
        CancellationToken cancellationToken,
        SoftwareOperationCaller? caller = null)
    {
        var definition = OptionalDependencyCatalog.Find(id)
            ?? throw new InvalidOperationException($"Unknown dependency: {id}");

        EnsureTerms(definition, acknowledgeExternalTerms);

        var installedSoftware = await installedSoftwareInventory.GetInstalledSoftwareAsync(caller?.WindowsSid, cancellationToken);
        var status = BuildStatus(definition, installedSoftware);
        if (definition.ReleaseSource is not null && status.Installed
            && (!status.UpdateAvailable && !string.IsNullOrWhiteSpace(versionChoice)
                || status.UpdateAvailable && !IsVerifiedChoice(versionChoice)))
            throw new InvalidOperationException("无法可靠识别已安装组件版本；不能判断升级方向，已停止版本选择。");
        if (string.IsNullOrWhiteSpace(status.InstallerPath) || !File.Exists(status.InstallerPath))
        {
            throw new FileNotFoundException("托管依赖目录中没有可用安装器。", status.InstallerPath);
        }

        var sharedRuntime = definition.Id.Equals("shared-webview2-runtime", StringComparison.OrdinalIgnoreCase);
        DependencyInstallPaths.RejectLinks(status.InstallerPath);
        DependencyInstallPaths.RejectLinks(DependencyReleaseRecordStore.DownloadPath(status.InstallerPath));
        DependencyInstallPaths.RejectLinks(status.InstallDirectory);
        DependencyInstallPaths.ValidateTree(Path.GetDirectoryName(status.InstallerPath)!);
        if (Directory.Exists(status.InstallDirectory)) DependencyInstallPaths.ValidateTree(status.InstallDirectory);
        var release = DependencyReleaseRecordStore.Read(DependencyReleaseRecordStore.DownloadPath(status.InstallerPath), logger)
            ?? throw new InvalidDataException("The cached installer has no download provenance; download it again.");
        using var asset = new FileStream(status.InstallerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!string.Equals(release.AssetSha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(asset)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The cached asset SHA-256 differs from its download record.");
        asset.Position = 0;
        if (definition.ReleaseSource is { } pinned && status.UpdateAvailable
            && !DependencyReleaseRecordStore.Matches(release, pinned.VerifiedTag, pinned.VerifiedAssetName))
            throw new InvalidDataException("The selected update is not the catalog's verified release.");
        Func<System.Diagnostics.ProcessStartInfo, bool, CancellationToken, ValueTask>? launch = null;
        var extension = Path.GetExtension(status.InstallerPath);
        var visibleInstaller = extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".msi", StringComparison.OrdinalIgnoreCase);
        if (visibleInstaller)
        {
            UserSessionCallerValidation.RequireActiveUser(caller, sessionBroker);
            launch = async (start, waitForExit, token) =>
            {
                token.ThrowIfCancellationRequested();
                var shellRequest = new UserShellExecuteRequest(
                    start.FileName,
                    start.Arguments,
                    string.IsNullOrWhiteSpace(start.Verb) ? null : start.Verb, WaitForExit: waitForExit);
                if (waitForExit)
                {
                    var exitCode = await SessionAgentLauncher.LaunchAndWaitAsync(sessionBroker, caller, shellRequest, token);
                    if (exitCode != 0) throw new InvalidOperationException($"The dependency installer returned exit code {exitCode}.");
                }
                else
                    _ = SessionAgentLauncher.Launch(sessionBroker, caller, shellRequest);
            };
        }

        var archiveInstall = extension.Equals(".zip", StringComparison.OrdinalIgnoreCase);
        if (archiveInstall)
        {
            using var archive = new ZipArchive(asset, ZipArchiveMode.Read, leaveOpen: true);
            using var transaction = new DependencyPayloadTransaction(status.InstallDirectory, replaceRelease: true, logger);
            transaction.Prepare(archive, definition.PayloadSource?.FileNames ?? definition.InstalledProbeRelativePaths,
                [], includeContents: true, definition.ReleaseSource is null ? null : release, cancellationToken);
            await using var session = definition.Id == "hardware-bridge"
                ? await hardwareBridge.SuspendForInstallationAsync(cancellationToken) : null;
            transaction.Publish(cancellationToken);
            return new(definition.Id, "filesExtracted", status.InstallerPath,
                BackendMessage.Create(BackendMessageDomains.Dependency, BackendMessageCodes.Dependency.ComponentFilesExtracted));
        }
        var installed = await new DependencyPackageInstaller(launch).InstallAsync(
            new(definition.Id, status.InstallerPath),
            new(status.InstallDirectory, definition.RequiresElevation,
                BuildInstallerArguments(definition, status.InstallDirectory), WaitForExit: definition.ReleaseSource is not null && visibleInstaller),
            cancellationToken);
        if (installed.Status == "installerCompleted")
        {
            // The child process is already owned through its actual exit. Finish
            // reconciling that outcome even when the operation's cancel flag was
            // raised while the installer was running.
            var softwareAfterInstall = await installedSoftwareInventory.GetInstalledSoftwareAsync(
                caller?.WindowsSid,
                CancellationToken.None);
            if (!IsDependencyInstalledAt(definition, status.InstallDirectory)
                && ResolveExternalInstall(definition, softwareAfterInstall, status.InstallDirectory) is null)
                throw new InvalidOperationException("The installer exited successfully but its installed runtime could not be verified.");
            if (definition.PayloadSource is null && IsDependencyInstalledAt(definition, status.InstallDirectory))
                DependencyReleaseRecordStore.Write(DependencyReleaseRecordStore.InstalledPath(status.InstallDirectory), release);
            return new(definition.Id, "installerCompleted", status.InstallerPath,
                BackendMessage.Create(BackendMessageDomains.Dependency, BackendMessageCodes.Dependency.ComponentFilesUnverified));
        }

        if (sharedRuntime)
        {
            await WaitForSharedWebView2RuntimeAsync(cancellationToken);
        }

        return new OptionalDependencyLaunchResult(
            definition.Id,
            "installerStarted",
            status.InstallerPath,
            sharedRuntime
                ? BackendMessage.Create(
                    BackendMessageDomains.Dependency,
                    BackendMessageCodes.Dependency.SharedRuntimeInstalled)
                : BackendMessage.Create(
                    BackendMessageDomains.Dependency,
                    BackendMessageCodes.Dependency.InstallerLaunched));
    }

    private static string BuildInstallerArguments(
        OptionalDependencyDefinition definition,
        string installDirectory)
    {
        if (definition.Id.Equals("shared-webview2-runtime", StringComparison.OrdinalIgnoreCase))
        {
            return "/silent /install";
        }

        return "";
    }

    private async Task WaitForSharedWebView2RuntimeAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BrowserRuntimeDiscovery.Discover(packageRoot).SharedRuntime is not null)
            {
                return;
            }

            await Task.Delay(500, cancellationToken);
        }

        throw new InvalidOperationException("安装器已结束，但没有检测到共享 WebView2 Runtime。");
    }
}
