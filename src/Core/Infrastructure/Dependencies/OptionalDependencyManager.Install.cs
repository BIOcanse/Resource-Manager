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
        if (definition.ReleaseSource is not null && status.Installed && !string.IsNullOrWhiteSpace(versionChoice))
            throw new InvalidOperationException("无法可靠识别已安装组件版本；不能判断升级方向，已停止版本选择。");
        if (string.IsNullOrWhiteSpace(status.InstallerPath) || !File.Exists(status.InstallerPath))
        {
            throw new FileNotFoundException("托管依赖目录中没有可用安装器。", status.InstallerPath);
        }

        var sharedRuntime = definition.Id.Equals("shared-webview2-runtime", StringComparison.OrdinalIgnoreCase);
        Func<System.Diagnostics.ProcessStartInfo, bool, CancellationToken, ValueTask>? launch = null;
        var extension = Path.GetExtension(status.InstallerPath);
        var visibleInstaller = extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".msi", StringComparison.OrdinalIgnoreCase);
        if (visibleInstaller)
        {
            UserSessionCallerValidation.RequireActiveUser(caller, sessionBroker);
            launch = (start, waitForExit, token) =>
            {
                token.ThrowIfCancellationRequested();
                if (waitForExit)
                    throw new InvalidOperationException("A visible user-session installer cannot be synchronously waited by the service.");
                var shellRequest = new UserShellExecuteRequest(
                    start.FileName,
                    start.Arguments,
                    string.IsNullOrWhiteSpace(start.Verb) ? null : start.Verb);
                _ = sessionBroker.LaunchUserShell(caller!.WindowsSessionId, caller.WindowsSid,
                    NativeUiLaunchHostedService.ResolveNativeUiPath(),
                    UserShellExecuteRequest.Argument + " " + shellRequest.Encode());
                return ValueTask.CompletedTask;
            };
        }

        var installed = await new DependencyPackageInstaller(launch).InstallAsync(
            new(definition.Id, status.InstallerPath),
            new(status.InstallDirectory, definition.RequiresElevation,
                BuildInstallerArguments(definition, status.InstallDirectory), WaitForExit: sharedRuntime && !visibleInstaller),
            cancellationToken);
        if (installed.Status == "filesExtracted")
        {
            return new OptionalDependencyLaunchResult(
                definition.Id,
                "filesExtracted",
                status.InstallerPath,
                BackendMessage.Create(
                    BackendMessageDomains.Dependency,
                    BackendMessageCodes.Dependency.ComponentFilesExtracted));
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
        if (definition.Id.Equals("windows-performance-toolkit", StringComparison.OrdinalIgnoreCase))
        {
            return $"/quiet /norestart /installpath \"{installDirectory}\" /features OptionId.WindowsPerformanceToolkit";
        }

        if (definition.Id.Equals("latencymon", StringComparison.OrdinalIgnoreCase))
        {
            return $"/SILENT /NORESTART /SUPPRESSMSGBOXES /DIR=\"{installDirectory}\"";
        }

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
