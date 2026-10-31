using ResourceManager.Shared.ServiceHosting;
using ResourceManager.Shared.Packages;
using ResourceManager.Shared.Desktop;
using System.Security.AccessControl;

namespace ResourceManager.Installer;

internal static class InstallerWorkflow
{
    public static PackageLayout TargetFor(PackageLayout source, UninstallationPlan? previous = null)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles)) throw new InvalidOperationException("Unable to locate Program Files.");
        return SelectTarget(source, programFiles, previous);
    }

    internal static PackageLayout SelectTarget(PackageLayout source, string programFiles, UninstallationPlan? previous)
    {
        var defaultRoot = Path.Combine(programFiles, "ResourceManager");
        if (previous is null || (!Directory.Exists(defaultRoot) && !File.Exists(defaultRoot)))
            return PackageLayout.ForRoot(defaultRoot, source.Version);
        if (source.Version.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-')))
            throw new InvalidDataException("Invalid installation version.");
        for (var number = 1; number <= 100; number++)
        {
            var candidate = Path.Combine(programFiles, "ResourceManager-" + source.Version + (number == 1 ? "" : "-" + number));
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return PackageLayout.ForRoot(candidate, source.Version);
        }
        throw new IOException("No vacant installation directory is available.");
    }

    public static void Install(PackageLayout source, PackageLayout target, InstallationReplacement? previous = null)
    {
        if (previous is null) InstallationRegistry.RequireVacant(target);
        var parent = Path.GetDirectoryName(target.Root)!;
        var stage = Path.Combine(parent, "ResourceManager.installing-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyTree(source.Root, stage);
            _ = PackageLayout.Verify(stage);
            if (previous is null)
            {
                PackageUserDirectoryAccess.Prepare(stage);
                InstallStaged(stage, target);
            }
            else previous.Run(stage, () => InstallStaged(stage, target), target.Root);
        }
        finally { if (Directory.Exists(stage)) RemoveOwnTree(stage, parent, "ResourceManager.installing-"); }
    }

    internal static void RegisterDirectory(PackageLayout package)
    {
        _ = PackageLayout.Verify(package.Root);
        InstallationRegistry.RequireVacant(package, verifiedExistingDirectory: true);
        PackageUserDirectoryAccess.Prepare(package.Root);
        InstallStaged(package.Root, package, existingDirectory: true);
    }

    private static void InstallStaged(string stage, PackageLayout target, bool existingDirectory = false)
    {
        InstallationRegistry.RequireVacant(target, existingDirectory);
        var parent = Path.GetDirectoryName(target.Root)!;
        var managerRoot = UpdateManagerPaths.InstalledDirectory(target.Root);
        var managerExecutable = UpdateManagerPaths.InstalledExecutable(target.Root);
        var promoted = false;
        var managerCreated = false;
        var serviceAttempted = false;
        var registryStarted = false;
        var shortcutStarted = false;
        var managerShortcutStarted = false;
        var retainedArchive = existingDirectory ? null : UserDataArchive.RetainedArchiveAt(target.Root);
        var retainedFolder = Path.Combine(parent, "ResourceManager.installing-retained-" + Guid.NewGuid().ToString("N"));
        var retainedMoved = false;
        try
        {
            InstallationRegistry.RequireVacant(target, existingDirectory);
            if (retainedArchive is not null)
            {
                Directory.Move(target.Root, retainedFolder);
                retainedMoved = true;
            }
            if (!existingDirectory) { Directory.Move(stage, target.Root); promoted = true; }
            if (retainedArchive is not null)
            {
                var archived = Path.Combine(retainedFolder, Path.GetFileName(retainedArchive));
                PreserveRetainedArchive(archived, target.Root);
            }
            if (Directory.Exists(managerRoot))
                throw new InvalidOperationException("更新管理器目录已被其他进程创建，拒绝覆盖。");
            Directory.CreateDirectory(managerRoot);
            managerCreated = true;
            PackageUseGuard.Prepare(target.Root);
            File.Copy(UpdateManagerPaths.PackagedExecutable(target.Root), managerExecutable, overwrite: false);
            File.Copy(target.Start, Path.Combine(managerRoot, "Start.exe"), overwrite: false);
            File.Copy(Path.Combine(target.Root, UninstallationPlan.ExecutableName), Path.Combine(managerRoot, UninstallationPlan.ExecutableName), overwrite: false);
            ResourceManager.Frontend.Packaging.LegacyStartupModuleMigration.Migrate(target.Root, target.Root);
            TrustedAccessInstallLifecycle.InitializeBeforeServiceRegistration(
                target.Backend,
                TrustedAccessInstallLifecycle.RunBackendAdministration,
                () =>
                {
                    serviceAttempted = true;
                    WindowsServiceRegistration.RegisterOnly(
                        WindowsServiceRegistration.ProductServiceName,
                        target.Backend);
                });
            registryStarted = true;
            InstallationRegistry.Write(target);
            shortcutStarted = true;
            StartMenuShortcut.Create(Path.Combine(managerRoot, "Start.exe"));
            managerShortcutStarted = true;
            StartMenuShortcut.CreateManager(managerExecutable);
        }
        catch (Exception failure)
        {
            try
            {
                if (managerShortcutStarted && File.Exists(StartMenuShortcut.ManagerPathForAllUsers))
                    File.Delete(StartMenuShortcut.ManagerPathForAllUsers);
                if (shortcutStarted && File.Exists(StartMenuShortcut.PathForAllUsers))
                    File.Delete(StartMenuShortcut.PathForAllUsers);
                if (registryStarted) InstallationRegistry.Undo(target);
                if (serviceAttempted) WindowsServiceRegistration.DeleteOwned(WindowsServiceRegistration.ProductServiceName, target.Backend);
                if (managerCreated) RemoveOwnTree(managerRoot, Path.Combine(target.Root, "Internal"), "Management");
                if (promoted) RemoveOwnTree(target.Root, parent, "ResourceManager");
                else if (!existingDirectory) RemoveOwnTree(stage, parent, "ResourceManager.installing-");
                if (retainedMoved && Directory.Exists(retainedFolder)) Directory.Move(retainedFolder, target.Root);
            }
            catch (Exception rollbackFailure)
            {
                throw new InvalidOperationException(
                    $"安装失败：{failure.Message}\n回滚也失败：{rollbackFailure.Message}\n请保留目录供检查：{target.Root}", failure);
            }
            throw;
        }
        // Installation is committed. Failure to remove the held copy must never
        // roll back the new installation and its verified retained archive.
        if (retainedMoved) OwnedPackageTree.Delete(retainedFolder);
    }

    internal static string PreserveRetainedArchive(string archive, string targetRoot)
    {
        UserDataArchive.Verify(archive);
        var directory = Path.Combine(targetRoot, "UserData", "Archives");
        Directory.CreateDirectory(directory);
        ReleasePackageLayout.RejectReparse(directory);
        var destination = Path.Combine(directory, Path.GetFileName(archive));
        File.Copy(archive, destination, overwrite: false);
        new FileInfo(destination).SetAccessControl(new FileInfo(archive).GetAccessControl());
        UserDataArchive.Verify(destination);
        return destination;
    }

    private static void CopyTree(string source, string target)
    {
        var pending = new Queue<(string Source, string Target)>();
        pending.Enqueue((source, target));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = 0;
        while (pending.Count > 0)
        {
            var (currentSource, currentTarget) = pending.Dequeue();
            if (!visited.Add(Path.GetFullPath(currentSource)) || ++entries > 100_000) throw new IOException("Installation copy exceeds the directory limit.");
            PackageLayout.RejectReparse(currentSource);
            Directory.CreateDirectory(currentTarget);
            foreach (var file in Directory.EnumerateFiles(currentSource))
            {
                if (++entries > 100_000) throw new IOException("Installation copy exceeds the entry limit.");
                PackageLayout.RejectReparse(file);
                File.Copy(file, Path.Combine(currentTarget, Path.GetFileName(file)), false);
            }
            foreach (var child in Directory.EnumerateDirectories(currentSource))
                pending.Enqueue((child, Path.Combine(currentTarget, Path.GetFileName(child))));
        }
    }

    private static void RemoveOwnTree(string path, string expectedParent, string expectedNamePrefix)
    {
        if (!Directory.Exists(path)) return;
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(expectedParent), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith(expectedNamePrefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"拒绝删除非本次安装目录：{full}");
        var pending = new Queue<string>();
        pending.Enqueue(full);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            PackageLayout.RejectReparse(current);
            foreach (var child in Directory.EnumerateDirectories(current)) pending.Enqueue(child);
            foreach (var file in Directory.EnumerateFiles(current)) PackageLayout.RejectReparse(file);
        }
        Directory.Delete(full, true);
    }
}
