using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ResourceManager.Shared.Packages;

namespace ResourceManager.Updater;

internal static class ManagerSelfUpdate
{
    public static bool Schedule(ReleasePackageLayout package, string installRoot, string installedExecutable)
    {
        var source = UpdateManagerPaths.PackagedExecutable(package.Root);
        if (SameFileContents(source, installedExecutable)) return false;
        var start = new ProcessStartInfo(source)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = package.Root,
            ArgumentList = { "--replace-manager", installRoot, Environment.ProcessId.ToString() }
        };
        using var helper = Process.Start(start)
            ?? throw new InvalidOperationException("新版更新管理器帮助进程未能启动。");
        return true;
    }

    public static async Task<string> ReplaceAsync(string executablePath, string installRoot,
        int oldProcessId, CancellationToken cancellationToken)
    {
        var target = UpdatePlan.RequireRegisteredTarget(installRoot);
        var managerDirectory = UpdateManagerPaths.InstalledDirectory(target);
        var installedExecutable = UpdateManagerPaths.InstalledExecutable(target);
        ReleasePackageLayout.RejectReparse(managerDirectory);
        ReleasePackageLayout.RejectReparse(installedExecutable);
        var resultPath = Path.Combine(managerDirectory, "self-update-result.json");
        try
        {
            var source = Path.GetFullPath(executablePath);
            var packageRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));
            var package = ReleasePackageLayout.Verify(packageRoot);
            if (!source.Equals(UpdateManagerPaths.PackagedExecutable(packageRoot), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("自身更新只能从已校验发行包的管理器执行。");
            await WaitForOldManagerExitAsync(installedExecutable, oldProcessId, cancellationToken);
            using var operationLock = UpdateManagerCommand.AcquireOperationLock(target);
            _ = ReleasePackageLayout.Verify(packageRoot);
            using (var current = JsonDocument.Parse(File.ReadAllText(Path.Combine(target, "release-manifest.json"))))
                if (!string.Equals(current.RootElement.GetProperty("version").GetString(), package.Version,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("管理器发行包与已提交的主程序版本不一致。");
            byte[] expectedHash;
            using (var verifiedSource = File.OpenRead(source)) expectedHash = SHA256.HashData(verifiedSource);
            var staged = Path.Combine(managerDirectory, "next-" + Guid.NewGuid().ToString("N") + ".exe");
            var backup = Path.Combine(managerDirectory, "previous-" + Guid.NewGuid().ToString("N") + ".exe");
            try
            {
                File.Copy(source, staged, overwrite: false);
                byte[] stagedHash;
                using (var stagedStream = File.OpenRead(staged)) stagedHash = SHA256.HashData(stagedStream);
                if (!expectedHash.AsSpan().SequenceEqual(stagedHash))
                    throw new InvalidDataException("新版管理器复制后校验失败。");
                File.Replace(staged, installedExecutable, backup);
            }
            finally
            {
                if (File.Exists(staged)) File.Delete(staged);
            }
            WriteResult(resultPath, true, $"更新管理器已替换；旧 EXE 备份：{backup}");
            return "更新管理器自身更新完成。";
        }
        catch (Exception exception)
        {
            WriteResult(resultPath, false, exception.ToString());
            throw;
        }
    }

    public static string? ReadLastFailure(string installRoot)
    {
        var path = Path.Combine(UpdateManagerPaths.InstalledDirectory(installRoot), "self-update-result.json");
        if (!File.Exists(path)) return null;
        ReleasePackageLayout.RejectReparse(path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("success").GetBoolean()
            ? null : document.RootElement.GetProperty("message").GetString()?.Split('\n')[0];
    }

    private static async Task WaitForOldManagerExitAsync(string installedExecutable, int oldProcessId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var active = false;
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId) continue;
                    try
                    {
                        if (process.Id != oldProcessId &&
                            !process.ProcessName.StartsWith("ResourceManager", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var path = process.MainModule?.FileName;
                        if (process.Id == oldProcessId ||
                            string.Equals(path, installedExecutable, StringComparison.OrdinalIgnoreCase))
                            active = true;
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        if (process.Id == oldProcessId || process.ProcessName.StartsWith("ResourceManager.UpdateManager",
                                StringComparison.OrdinalIgnoreCase))
                            active = true;
                    }
                    catch (InvalidOperationException) { }
                }
            }
            if (!active) return;
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
        throw new TimeoutException("等待旧更新管理器退出超时。");
    }

    private static bool SameFileContents(string first, string second)
    {
        using var source = File.OpenRead(first);
        using var target = File.OpenRead(second);
        if (source.Length != target.Length) return false;
        return SHA256.HashData(source).AsSpan().SequenceEqual(SHA256.HashData(target));
    }

    private static void WriteResult(string path, bool success, string message)
    {
        var temp = path + ".writing";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(new
        {
            success, message, finishedAt = DateTimeOffset.UtcNow
        }));
        File.Move(temp, path, overwrite: true);
    }
}
