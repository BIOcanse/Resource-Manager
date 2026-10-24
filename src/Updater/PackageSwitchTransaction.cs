using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using ResourceManager.Shared.Packages;

namespace ResourceManager.Updater;

internal static class PackageSwitchTransaction
{
    internal sealed record PendingUpdate(string Workspace, string Stage, string PreviousVersion,
        string TargetVersion, bool ServiceWasRunning, bool PreserveManagement = false, bool PreserveAuthority = false);

    public static async Task<string> ApplyAsync(
        UpdatePlan plan, IUpdateRuntime runtime, CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(plan.InstallRoot)!;
        var workspace = Path.Combine(parent, "ResourceManager-update-" + Guid.NewGuid().ToString("N"));
        var next = Path.Combine(workspace, "next");
        var previous = Path.Combine(workspace, "previous");
        var failed = Path.Combine(workspace, "failed");
        Directory.CreateDirectory(workspace);
        var stage = "verified";
        var serviceStopped = false;
        var registeredVersionChanged = false;
        void Journal(string value)
        {
            WriteJournal(workspace, plan.InstallRoot, plan.PreviousVersion,
                plan.Package.Version, plan.ServiceWasRunning, value, preserveManagement: true, preserveAuthority: true,
                sourcePackageRoot: plan.Package.Root);
            stage = value;
        }
        try
        {
            CopyTree(plan.Package.Root, next);
            _ = ReleasePackageLayout.Verify(next);
            Journal("verified");
            await runtime.WaitForNativeUiExitAsync(plan.InstallRoot, cancellationToken);
            runtime.StopService(plan.InstallRoot);
            serviceStopped = true;
            CopyMutableData(plan.InstallRoot, next);
            PackageUserDirectoryAccess.Prepare(next);
            Journal("dataCopied");

            // Record intent before each potentially partial move. The root and
            // Internal/Management (including the running EXE/lock) stay in place.
            Journal("previousMoving");
            PackagePayloadSwitch.Move(plan.InstallRoot, previous, preserveAuthority: true);
            Journal("previousMoved");
            Journal("nextPromoting");
            PackagePayloadSwitch.Move(next, plan.InstallRoot, preserveAuthority: true);
            Journal("nextPromoted");

            runtime.StartService(plan.InstallRoot);
            await runtime.WaitForHealthAsync(cancellationToken);
            runtime.WriteInstalledVersion(plan.InstallRoot, plan.Package.Version);
            registeredVersionChanged = true;
            Journal("committed");
            return workspace;
        }
        catch (Exception failure)
        {
            try
            {
                if (stage is "nextPromoting" or "nextPromoted") runtime.StopService(plan.InstallRoot);
                if (stage is not ("verified" or "dataCopied"))
                    PackagePayloadSwitch.Restore(plan.InstallRoot, workspace, stage, Journal, preserveAuthority: true);
                if (registeredVersionChanged)
                    runtime.WriteInstalledVersion(plan.InstallRoot, plan.PreviousVersion);
                if (serviceStopped && plan.ServiceWasRunning)
                    runtime.StartService(plan.InstallRoot);
                Journal("rolledBack");
            }
            catch (Exception recoveryFailure)
            {
                // Retain the last durable phase so another recovery attempt
                // knows whether current members are new or already restored.
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
        var workspaces = Directory.EnumerateDirectories(parent, "ResourceManager-update-*");
        var pending = new List<PendingUpdate>();
        var inspected = 0;
        foreach (var workspace in workspaces)
        {
            // Closed history is kept separately from the count of actionable transactions.
            if (++inspected > 100_000) throw new InvalidDataException("待检查的更新事务超过上限。");
            ReleasePackageLayout.RejectReparse(workspace);
            var journalPath = Path.Combine(workspace, "update-transaction.json");
            if (!File.Exists(journalPath)) continue;
            ReleasePackageLayout.RejectReparse(journalPath);
            using var journal = JsonDocument.Parse(File.ReadAllText(journalPath));
            var data = journal.RootElement;
            var registeredRoot = data.GetProperty("InstallRoot").GetString();
            if (!string.Equals(registeredRoot, root, StringComparison.OrdinalIgnoreCase))
                continue; // Another installation owns this transaction.
            var stage = data.GetProperty("stage").GetString() ?? "";
            if (stage is "committed" or "rolledBack" or "recovered") continue;
            if (stage is not ("verified" or "dataCopied" or "previousMoving" or "previousMoved" or "nextPromoting" or "nextPromoted" or "removingNew" or "restoringPrevious" or "recoveryFailed"))
                throw new InvalidDataException($"未知的更新事务阶段：{stage}");
            var wasRunning = data.TryGetProperty("serviceWasRunning", out var running) && running.GetBoolean();
            pending.Add(new PendingUpdate(workspace, stage,
                data.GetProperty("PreviousVersion").GetString() ?? "",
                data.GetProperty("targetVersion").GetString() ?? "", wasRunning,
                data.TryGetProperty("preserveManagement", out var preserve) && preserve.GetBoolean(),
                data.TryGetProperty("preserveAuthority", out var authority) && authority.GetBoolean()));
            if (pending.Count > 1000) throw new InvalidDataException("待检查的更新事务超过上限。");
        }
        return pending;
    }



    // Cleanup runs after commit and cannot roll back the successful update.
    public static void RetainLatestBackup(string installRoot, string committedWorkspace)
    {
        var root = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetDirectoryName(root)!;
        ReleasePackageLayout.RejectReparse(parent);
        var completed = new List<string>();
        var inspected = 0;
        foreach (var workspace in Directory.EnumerateDirectories(parent, "ResourceManager-update-*"))
        {
            if (++inspected > 100_000) throw new InvalidDataException("Update history exceeds the entry limit.");
            if (!Guid.TryParseExact(Path.GetFileName(workspace)["ResourceManager-update-".Length..], "N", out _)) continue;
            ReleasePackageLayout.RejectReparse(workspace);
            var path = Path.Combine(workspace, "update-transaction.json");
            if (!File.Exists(path)) continue;
            ReleasePackageLayout.RejectReparse(path);
            if (new FileInfo(path).Length > 16_384) throw new InvalidDataException("Invalid update journal.");
            using var journal = JsonDocument.Parse(File.ReadAllText(path));
            var data = journal.RootElement;
            if (!string.Equals(data.GetProperty("InstallRoot").GetString(), root, StringComparison.OrdinalIgnoreCase)) continue;
            if (data.GetProperty("stage").GetString() == "committed") completed.Add(workspace);
        }
        var latest = Path.GetFullPath(committedWorkspace);
        if (!completed.Contains(latest, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("The retained workspace is not a committed update for this installation.");
        foreach (var workspace in completed) _ = OwnedPackageTree.Read(workspace);
        foreach (var workspace in completed)
            if (!string.Equals(workspace, latest, StringComparison.OrdinalIgnoreCase)) OwnedPackageTree.Delete(workspace);
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
                if (entry.PreserveManagement)
                {
                    if (entry.Stage is not ("verified" or "dataCopied"))
                    {
                        await runtime.WaitForNativeUiExitAsync(root, cancellationToken);
                        runtime.StopService(root);
                        PackagePayloadSwitch.Restore(root, entry.Workspace, entry.Stage, value =>
                            WriteJournal(entry.Workspace, root, entry.PreviousVersion,
                                entry.TargetVersion, entry.ServiceWasRunning, value, preserveManagement: true, preserveAuthority: entry.PreserveAuthority),
                            entry.PreserveAuthority);
                    }
                }
                else if (Directory.Exists(previous))
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
                    entry.TargetVersion, entry.ServiceWasRunning, "recovered", entry.PreserveManagement, entry.PreserveAuthority);
                recovered.Add(entry.Workspace);
            }
            catch (Exception failure)
            {
                if (!entry.PreserveManagement)
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

    internal static void CopyUserData(string installed, string next,
        ResourceManager.App.Infrastructure.Optimization.IHostManagerUserDataArrival? hostManagerArrival = null,
        string? finalInstallRoot = null)
    {
        CopyMutableData(installed, next, includeDependencies: false);
        (hostManagerArrival ?? ResourceManager.App.Infrastructure.Optimization.HostManagerUserDataArrival.Instance)
            .Prepare(next, finalInstallRoot ?? next, installed);
    }

    private static void CopyMutableData(string installed, string next, bool includeDependencies = true)
    {
        var directories = includeDependencies ? new[] { "Config", "UserData", "Dependencies", "Misc" } : new[] { "Config", "UserData", "Misc" };
        foreach (var name in directories)
        {
            var source = Path.Combine(installed, name);
            if (!Directory.Exists(source)) continue;
            var destination = Path.Combine(next, name);
            if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
                throw new InvalidDataException($"目标发行包含有不该携带的运行数据：{name}");
            CopyTree(source, destination, preserveAccessRules: true,
                preserveHostManager: includeDependencies && name == "UserData");
        }
        // Older control stores used ContentRoot (Bin/ResourceManager), which is replaced by an update.
        var legacyControl = Path.Combine(installed, "Bin", "ResourceManager", "UserData", "Control");
        if (Directory.Exists(legacyControl))
        {
            ReleasePackageLayout.RejectReparse(legacyControl);
            var control = Path.Combine(next, "UserData", "Control");
            Directory.CreateDirectory(control);
            foreach (var file in Directory.EnumerateFiles(legacyControl))
            {
                ReleasePackageLayout.RejectReparse(file);
                var destination = Path.Combine(control, Path.GetFileName(file));
                if (File.Exists(destination)) continue;
                File.Copy(file, destination, overwrite: false);
                CopyFileAccessRules(file, destination);
            }
        }
    }

    private static void CopyTree(string source, string target, bool preserveAccessRules = false, bool preserveHostManager = false)
    {
        var pending = new Queue<(string Source, string Target)>();
        var directoryPermissions = new Stack<(string Source, string Target)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(source) };
        var entries = 1;
        pending.Enqueue((source, target));
        while (pending.Count > 0)
        {
            var item = pending.Dequeue();
            ReleasePackageLayout.RejectReparse(item.Source);
            Directory.CreateDirectory(item.Target);
            if (preserveAccessRules) directoryPermissions.Push(item);
            foreach (var file in Directory.EnumerateFiles(item.Source))
            {
                if (++entries > 100_000) throw new IOException("Update copy exceeds the entry limit.");
                ReleasePackageLayout.RejectReparse(file);
                var destination = Path.Combine(item.Target, Path.GetFileName(file));
                File.Copy(file, destination, overwrite: false);
                if (preserveAccessRules) CopyFileAccessRules(file, destination);
            }
            foreach (var child in Directory.EnumerateDirectories(item.Source))
            {
                ReleasePackageLayout.RejectReparse(child);
                if (preserveHostManager && string.Equals(item.Source, source, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(child).Equals("HostManager", StringComparison.OrdinalIgnoreCase)) continue;
                if (!visited.Add(Path.GetFullPath(child))) throw new IOException("Update copy encountered a duplicate directory.");
                if (++entries > 100_000) throw new IOException("Update copy exceeds the entry limit.");
                pending.Enqueue((child, Path.Combine(item.Target, Path.GetFileName(child))));
            }
        }
        // Finish all content before restrictive directory rules can prevent
        // creating descendants. Children receive their permissions before parents.
        while (directoryPermissions.TryPop(out var item)) CopyDirectoryAccessRules(item.Source, item.Target);
    }

    private static void CopyDirectoryAccessRules(string source, string destination)
    {
        var sourceDirectory = new DirectoryInfo(source);
        var destinationDirectory = new DirectoryInfo(destination);
        var sourceSecurity = sourceDirectory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var destinationSecurity = destinationDirectory.GetAccessControl(AccessControlSections.Access);
        CopyOwnedAccessRules(sourceSecurity, destinationSecurity);
        destinationDirectory.SetAccessControl(destinationSecurity);
    }

    private static void CopyFileAccessRules(string source, string destination)
    {
        var sourceFile = new FileInfo(source);
        var destinationFile = new FileInfo(destination);
        var sourceSecurity = sourceFile.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var destinationSecurity = destinationFile.GetAccessControl(AccessControlSections.Access);
        CopyOwnedAccessRules(sourceSecurity, destinationSecurity);
        destinationFile.SetAccessControl(destinationSecurity);
    }

    internal static void CopyOwnedAccessRules(FileSystemSecurity source, FileSystemSecurity destination)
    {
        if (source.AreAccessRulesProtected)
            destination.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var owner = source.GetOwner(typeof(SecurityIdentifier));
        var descriptor = new CommonSecurityDescriptor(destination is DirectorySecurity, false,
            destination.GetSecurityDescriptorBinaryForm(), 0);
        foreach (FileSystemAccessRule rule in source.GetAccessRules(
                     includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier)))
        {
            // CREATOR OWNER grants are translated into inherited rules for the
            // original owner. The elevated copier may own the replacement tree;
            // retain that user's existing rights explicitly, without granting
            // new rights from a directory name or copying unrelated ancestor rules.
            if (rule.IsInherited && !rule.IdentityReference.Equals(owner)) continue;
            // Inherited Windows ACEs can retain GENERIC_* bits. The typed
            // FileSystemAccessRule constructor rejects those valid raw masks.
            descriptor.DiscretionaryAcl!.AddAccess(rule.AccessControlType,
                (SecurityIdentifier)rule.IdentityReference, (int)rule.FileSystemRights,
                rule.InheritanceFlags, rule.PropagationFlags);
        }
        var binary = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(binary, 0);
        destination.SetSecurityDescriptorBinaryForm(binary, AccessControlSections.Access);
    }

    private static void WriteJournal(string workspace, UpdatePlan plan, string stage)
        => WriteJournal(workspace, plan.InstallRoot, plan.PreviousVersion,
            plan.Package.Version, plan.ServiceWasRunning, stage, sourcePackageRoot: plan.Package.Root);

    private static void WriteJournal(string workspace, string installRoot, string previousVersion,
        string targetVersion, bool serviceWasRunning, string stage, bool preserveManagement = false,
        bool preserveAuthority = false, string? sourcePackageRoot = null)
    {
        var path = Path.Combine(workspace, "update-transaction.json");
        if (sourcePackageRoot is null && File.Exists(path))
        {
            ReleasePackageLayout.RejectReparse(path);
            if (new FileInfo(path).Length > 16_384) throw new InvalidDataException("Invalid update journal.");
            using var previous = JsonDocument.Parse(File.ReadAllText(path));
            if (previous.RootElement.TryGetProperty("sourcePackageRoot", out var source)
                && source.ValueKind == JsonValueKind.String)
                sourcePackageRoot = source.GetString();
        }
        var temp = path + ".writing";
        var data = JsonSerializer.SerializeToUtf8Bytes(new
        {
            stage,
            InstallRoot = installRoot,
            PreviousVersion = previousVersion,
            targetVersion,
            serviceWasRunning,
            preserveManagement,
            preserveAuthority,
            sourcePackageRoot,
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
