using System.Diagnostics;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Application.Operations;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Operations;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Infrastructure.Software;

public sealed class SoftwareOperationManager(
    IOptionalDependencyManager dependencyManager,
    IInstalledSoftwareInventory installedSoftwareInventory,
    IFileChangeTracker fileChangeTracker) : ISoftwareOperationManager
{
    private const string DependencyPrefix = "dependency:";

    public async Task<SoftwareOperationResult> UninstallAsync(
        SoftwareOperationRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.ConfirmOperation)
        {
            throw new InvalidOperationException("执行卸载/清理前需要明确确认。");
        }

        if (request.Id.StartsWith(DependencyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var dependencyId = request.Id[DependencyPrefix.Length..];
            return await CleanupDependencyRootAsync(dependencyId, cancellationToken);
        }

        if (request.Id.StartsWith("windows-installed:", StringComparison.OrdinalIgnoreCase))
        {
            return await LaunchWindowsUninstallerAsync(request.Id, cancellationToken);
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
        }

        var after = await fileChangeTracker.CaptureAsync(scope, cancellationToken);
        var fileChanges = fileChangeTracker.Compare(before, after);

        return new SoftwareOperationResult(
            $"dependency:{dependencyId}",
            "RemovedManagedRoot",
            "已清理该依赖的受管安装根目录。驱动自带或系统全局运行库不会被卸载。",
            fileChanges);
    }

    private async Task<SoftwareOperationResult> LaunchWindowsUninstallerAsync(
        string id,
        CancellationToken cancellationToken)
    {
        var installedSoftware = await installedSoftwareInventory.GetInstalledSoftwareAsync(cancellationToken);
        var entry = installedSoftware.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("未在 Windows 已安装软件清单中找到该条目。");

        if (string.IsNullOrWhiteSpace(entry.UninstallString))
        {
            throw new InvalidOperationException("缺少卸载入口，Resource Manager 不会删除未知软件根目录。");
        }

        var command = WindowsCommandLine.Parse(entry.UninstallString);
        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            Arguments = command.Arguments,
            UseShellExecute = true,
            WorkingDirectory = ResolveWorkingDirectory(command.FileName)
        };

        Process.Start(startInfo);

        return new SoftwareOperationResult(
            id,
            "UninstallerStarted",
            "已启动 Windows 注册表提供的卸载器。完成后软件会在下一次清单刷新时消失；当前阶段不根据全盘空间差推断释放容量。");
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

    private static string? ResolveWorkingDirectory(string fileName)
    {
        try
        {
            var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(fileName));
            return File.Exists(fullPath) ? Path.GetDirectoryName(fullPath) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private sealed record WindowsCommandLine(string FileName, string Arguments)
    {
        public static WindowsCommandLine Parse(string commandLine)
        {
            var text = commandLine.Trim();
            if (text.Length == 0)
            {
                throw new InvalidOperationException("卸载命令为空。");
            }

            if (text[0] == '"')
            {
                var endQuote = text.IndexOf('"', 1);
                if (endQuote < 0)
                {
                    throw new InvalidOperationException("卸载命令格式无效。");
                }

                return new WindowsCommandLine(
                    text[1..endQuote],
                    text[(endQuote + 1)..].TrimStart());
            }

            var firstSpace = text.IndexOf(' ');
            return firstSpace < 0
                ? new WindowsCommandLine(text, "")
                : new WindowsCommandLine(text[..firstSpace], text[(firstSpace + 1)..].TrimStart());
        }
    }
}
