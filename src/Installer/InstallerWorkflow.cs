using ResourceManager.Shared.ServiceHosting;

namespace ResourceManager.Installer;

internal static class InstallerWorkflow
{
    public static PackageLayout TargetFor(PackageLayout source)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles)) throw new InvalidOperationException("无法定位 Program Files。");
        return PackageLayout.ForRoot(Path.Combine(programFiles, "ResourceManager"), source.Version);
    }

    public static void Install(PackageLayout source, PackageLayout target)
    {
        InstallationRegistry.RequireVacant(target);
        var parent = Path.GetDirectoryName(target.Root)!;
        var stage = Path.Combine(parent, "ResourceManager.installing-" + Guid.NewGuid().ToString("N"));
        var promoted = false;
        var serviceAttempted = false;
        var registryStarted = false;
        var shortcutStarted = false;
        try
        {
            CopyTree(source.Root, stage);
            _ = PackageLayout.Verify(stage);
            InstallationRegistry.RequireVacant(target);
            Directory.Move(stage, target.Root);
            promoted = true;
            serviceAttempted = true;
            WindowsServiceRegistration.RegisterOnly(WindowsServiceRegistration.ProductServiceName, target.Backend);
            registryStarted = true;
            InstallationRegistry.Write(target);
            shortcutStarted = true;
            StartMenuShortcut.Create(target.Start);
        }
        catch (Exception failure)
        {
            try
            {
                if (shortcutStarted && File.Exists(StartMenuShortcut.PathForAllUsers))
                    File.Delete(StartMenuShortcut.PathForAllUsers);
                if (registryStarted) InstallationRegistry.Undo(target);
                if (serviceAttempted) WindowsServiceRegistration.DeleteOwned(WindowsServiceRegistration.ProductServiceName, target.Backend);
                if (promoted) RemoveOwnTree(target.Root, parent, "ResourceManager");
                else RemoveOwnTree(stage, parent, "ResourceManager.installing-");
            }
            catch (Exception rollbackFailure)
            {
                throw new InvalidOperationException(
                    $"安装失败：{failure.Message}\n回滚也失败：{rollbackFailure.Message}\n请保留目录供检查：{target.Root}", failure);
            }
            throw;
        }
    }

    private static void CopyTree(string source, string target)
    {
        var pending = new Queue<(string Source, string Target)>();
        pending.Enqueue((source, target));
        while (pending.Count > 0)
        {
            var (currentSource, currentTarget) = pending.Dequeue();
            PackageLayout.RejectReparse(currentSource);
            Directory.CreateDirectory(currentTarget);
            foreach (var file in Directory.EnumerateFiles(currentSource))
            {
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
