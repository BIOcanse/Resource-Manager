using System.Diagnostics;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.Shared.BrowserRuntimes;

namespace ResourceManager.App.Infrastructure.Dependencies;

public sealed partial class OptionalDependencyManager
{
    public async Task<OptionalDependencyLaunchResult> LaunchInstallerAsync(
        string id,
        bool acknowledgeExternalTerms,
        CancellationToken cancellationToken)
    {
        var definition = OptionalDependencyCatalog.Find(id)
            ?? throw new InvalidOperationException($"Unknown dependency: {id}");

        EnsureTerms(definition, acknowledgeExternalTerms);

        var installedSoftware = await installedSoftwareInventory.GetInstalledSoftwareAsync(cancellationToken);
        var status = BuildStatus(definition, installedSoftware);
        if (string.IsNullOrWhiteSpace(status.InstallerPath) || !File.Exists(status.InstallerPath))
        {
            throw new FileNotFoundException("托管依赖目录中没有可用安装器。", status.InstallerPath);
        }

        Directory.CreateDirectory(status.InstallDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = status.InstallerPath,
            UseShellExecute = true,
            WorkingDirectory = status.InstallDirectory,
            Arguments = BuildInstallerArguments(definition, status.InstallDirectory)
        };

        if (definition.RequiresElevation)
        {
            startInfo.Verb = "runas";
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("安装器未能启动。");
        if (definition.Id.Equals("shared-webview2-runtime", StringComparison.OrdinalIgnoreCase))
        {
            using (process)
            {
                await process.WaitForExitAsync(cancellationToken);
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException($"共享运行时安装器返回错误代码 {process.ExitCode}。");
                }
            }

            await WaitForSharedWebView2RuntimeAsync(cancellationToken);
        }
        else
        {
            process.Dispose();
        }

        return new OptionalDependencyLaunchResult(
            definition.Id,
            "installerStarted",
            status.InstallerPath,
            definition.Id.Equals("shared-webview2-runtime", StringComparison.OrdinalIgnoreCase)
                ? "共享 WebView2 Runtime 已安装并通过检测。"
                : "安装器已可见启动。使用该 Provider 前需要完成厂商安装提示。");
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
