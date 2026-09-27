using System.Text.Json;
using ResourceManager.Shared.Packages;

namespace ResourceManager.Updater;

internal static class PackageSwitchTransaction
{
    internal sealed record PendingUpdate(string Workspace, string Stage, string PreviousVersion,
        string TargetVersion, bool ServiceWasRunning);

    public static async Task<string> ApplyAsync(
        UpdatePlan plan, IUpdateRuntime runtime, CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(plan.InstallRoot)!;
        var workspace = Path.Combine(parent, "ResourceManager-update-" + Guid.NewGuid().ToString("N"));
        var next = Path.Combine(workspace, "next");
        var previous = Path.Combine(workspace, "previous");
        var failed = Path.Combine(workspace, "failed");
        Directory.CreateDirectory(workspace);
        var previousMoved = false;
        var nextPromoted = false;
        var serviceStopped = false;
        var registeredVersionChanged = false;
        try
        {
            CopyTree(plan.Package.Root, next);
            _ = ReleasePackageLayout.Verify(next);
            WriteJournal(workspace, plan, "verified");
            await runtime.WaitForNativeUiExitAsync(plan.InstallRoot, cancellationToken);
            runtime.StopService(plan.InstallRoot);
            serviceStopped = true;
            CopyMutableData(plan.InstallRoot, next);
            WriteJournal(workspace, plan, "dataCopied");

            Directory.Move(plan.InstallRoot, previous);
            previousMoved = true;
            WriteJournal(workspace, plan, "previousMoved");
            Directory.Move(next, plan.InstallRoot);
            nextPromoted = true;
            WriteJournal(workspace, plan, "nextPromoted");

            runtime.StartService(plan.InstallRoot);
            await runtime.WaitForHealthAsync(cancellationToken);
            runtime.WriteInstalledVersion(plan.InstallRoot, plan.Package.Version);
            registeredVersionChanged = true;
            WriteJournal(workspace, plan, "committed");
            return workspace;
        }
        catch (Exception failure)
        {
            try
            {
                if (nextPromoted)
                {
                    runtime.StopService(plan.InstallRoot);
                    Directory.Move(plan.InstallRoot, AvailableFailedPath(failed));
                }
                if (previousMoved)
                    Directory.Move(previous, plan.InstallRoot);
                if (registeredVersionChanged)
                    runtime.WriteInstalledVersion(plan.InstallRoot, plan.PreviousVersion);
                if (serviceStopped && plan.ServiceWasRunning)
                    runtime.StartService(plan.InstallRoot);
                WriteJournal(workspace, plan, "rolledBack");
            }
            catch (Exception recoveryFailure)
            {
                WriteJournal(workspace, plan, "recoveryFailed");
                throw new InvalidOperationException(
                    $"更新失败且恢复未完成。保留工作目录：{workspace}。更新错误：{failure.Message}；恢复错误：{recoveryFailure.Message}",
                    failure);
            }
            throw new InvalidOperationException($"更新失败，旧程序和数据已恢复。工作目录：{workspace}。{failure.Message}", failure);
        }
    }

    public static IReadOnlyList<PendingUpdate> ListPending(string installRoot)
    {
        var root = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetDirectoryName(root)
            ?? throw new InvalidOperationException("安装目录缺少父目录。");
        ReleasePackageLayout.RejectReparse(parent);
        var workspaces = Directory.EnumerateDirectories(parent, "ResourceManager-update-*")
            .Take(1001).ToArray();
        if (workspaces.Length > 1000) throw new InvalidDataException("待检查的更新事务超过上限。");
        var pending = new List<PendingUpdate>();
        foreach (var workspace in workspaces)
        {
            ReleasePackageLayout.RejectReparse(workspace);
            var journalPath = Path.Combine(workspace, "update-transaction.json");
            if (!File.Exists(journalPath)) continue;
            ReleasePackageLayout.RejectReparse(journalPath);
            using var journal = JsonDocument.Parse(File.ReadAllText(journalPath));
            var data = journal.RootElement;
            var registeredRoot = data.GetProperty("InstallRoot").GetString();
            if (!string.Equals(registeredRoot, root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"事务目标与当前安装登记不一致：{workspace}");
            var stage = data.GetProperty("stage").GetString() ?? "";
            if (stage is "committed" or "rolledBack" or "recovered") continue;
            if (stage is not ("verified" or "dataCopied" or "previousMoved" or "nextPromoted" or "recoveryFailed"))
                throw new InvalidDataException($"未知的更新事务阶段：{stage}");
            var wasRunning = data.TryGetProperty("serviceWasRunning", out var running) && running.GetBoolean();
            pending.Add(new PendingUpdate(workspace, stage,
                data.GetProperty("PreviousVersion").GetString() ?? "",
                data.GetProperty("targetVersion").GetString() ?? "", wasRunning));
        }
        return pending;
    }

    public static async Task<IReadOnlyList<string>> RecoverPendingAsync(string installRoot,
        IUpdateRuntime runtime, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar);
        var recovered = new List<string>();
        foreach (var entry in ListPending(root))
        {
            try
            {
                var previous = Path.Combine(entry.Workspace, "previous");
                if (Directory.Exists(previous))
                {
                    ReleasePackageLayout.RejectReparse(previous);
                    await runtime.WaitForNativeUiExitAsync(root, cancellationToken);
                    runtime.StopService(root);
                    if (Directory.Exists(root))
                    {
                        ReleasePackageLayout.RejectReparse(root);
                        Directory.Move(root, AvailableFailedPath(Path.Combine(entry.Workspace, "failed")));
                    }
                    Directory.Move(previous, root);
                }
                else if (!Directory.Exists(root))
                {
                    throw new InvalidOperationException("旧程序和当前安装目录都不存在。");
                }
                runtime.WriteInstalledVersion(root, entry.PreviousVersion);
                if (entry.ServiceWasRunning) runtime.StartService(root);
                WriteJournal(entry.Workspace, root, entry.PreviousVersion,
                    entry.TargetVersion, entry.ServiceWasRunning, "recovered");
                recovered.Add(entry.Workspace);
            }
            catch (Exception failure)
            {
                WriteJournal(entry.Workspace, root, entry.PreviousVersion,
                    entry.TargetVersion, entry.ServiceWasRunning, "recoveryFailed");
                throw new InvalidOperationException($"中断事务恢复失败；保留目录：{entry.Workspace}。{failure.Message}", failure);
            }
        }
        return recovered;
    }

    private static string AvailableFailedPath(string first)
    {
        if (!Directory.Exists(first) && !File.Exists(first)) return first;
        for (var number = 2; number <= 100; number++)
        {
            var candidate = first + "-" + number;
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }
        throw new IOException("更新失败版本的保留目录已满。");
    }

    private static void CopyMutableData(string installed, string next)
    {
        foreach (var name in new[] { "Config", "UserData", "Dependencies", "Misc" })
        {
            var source = Path.Combine(installed, name);
            if (!Directory.Exists(source)) continue;
            var destination = Path.Combine(next, name);
            if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
                throw new InvalidDataException($"目标发行包含有不该携带的运行数据：{name}");
            CopyTree(source, destination);
        }
    }

    private static void CopyTree(string source, string target)
    {
        var pending = new Queue<(string Source, string Target)>();
        pending.Enqueue((source, target));
        while (pending.Count > 0)
        {
            var item = pending.Dequeue();
            ReleasePackageLayout.RejectReparse(item.Source);
            Directory.CreateDirectory(item.Target);
            foreach (var file in Directory.EnumerateFiles(item.Source))
            {
                ReleasePackageLayout.RejectReparse(file);
                File.Copy(file, Path.Combine(item.Target, Path.GetFileName(file)), overwrite: false);
            }
            foreach (var child in Directory.EnumerateDirectories(item.Source))
                pending.Enqueue((child, Path.Combine(item.Target, Path.GetFileName(child))));
        }
    }

    private static void WriteJournal(string workspace, UpdatePlan plan, string stage)
        => WriteJournal(workspace, plan.InstallRoot, plan.PreviousVersion,
            plan.Package.Version, plan.ServiceWasRunning, stage);

    private static void WriteJournal(string workspace, string installRoot, string previousVersion,
        string targetVersion, bool serviceWasRunning, string stage)
    {
        var path = Path.Combine(workspace, "update-transaction.json");
        var temp = path + ".writing";
        var data = JsonSerializer.SerializeToUtf8Bytes(new
        {
            stage,
            InstallRoot = installRoot,
            PreviousVersion = previousVersion,
            targetVersion,
            serviceWasRunning,
            updatedAt = DateTimeOffset.UtcNow
        });
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(data);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }
}
