using System.Text.Json;

namespace ResourceManager.Shared.Packages;

public interface IUninstallationRuntime
{
    void RequireOwned(UninstallationPlan plan);
    void StopProduct(UninstallationPlan plan);
    void RemoveLaunchInterception(UninstallationPlan plan);
    void RemoveService(UninstallationPlan plan);
    void RemoveRegistration(UninstallationPlan plan);
    void ProtectArchive(string archive);
    void CreateCredentialFreeLegacyDatabaseCopy(string sourcePath, string destinationPath)
        => throw new NotSupportedException("Retained legacy business databases require credential sanitization.");
}

public static class UninstallationWorkflow
{
    private const string ReceiptName = "uninstall-data-retention.json";
    private sealed record Retention(string Contract, string InstallRoot, string Archive);

    public static string? Run(UninstallationPlan plan, bool keepData, IUninstallationRuntime runtime)
    {
        runtime.RequireOwned(plan);
        _ = OwnedPackageTree.Read(plan.Root);
        if (Directory.Exists(plan.ManagerDirectory)) _ = OwnedPackageTree.Read(plan.ManagerDirectory);
        using var operation = Directory.Exists(plan.ManagerDirectory) ? PackageOperationLock.AcquireDirectory(plan.ManagerDirectory) : null;
        var workspaces = UninstallationWorkspaces.Read(plan);
        runtime.StopProduct(plan);
        runtime.RemoveLaunchInterception(plan);
        string? archive = null;
        if (keepData)
        {
            archive = ReadRetainedArchive(plan);
            var saved = Directory.EnumerateFiles(plan.Root, "ResourceManager-user-data-*.zip").ToArray();
            var hasData = UserDataArchive.DirectoryNames.Any(name => Directory.Exists(Path.Combine(plan.Root, name)));
            if (archive is null && !hasData && saved.Length > 1)
                throw new InvalidDataException("Multiple retained user-data archives exist. Preserve them before retrying uninstall.");
            if (archive is null && !hasData && saved.Length == 1) archive = saved[0];
            if (archive is not null)
            {
                UserDataArchive.Verify(archive);
                runtime.ProtectArchive(archive);
            }
            else
            {
                archive = Path.Combine(plan.Root, "ResourceManager-user-data-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".zip");
                var partial = archive + ".partial";
                try
                {
                    UserDataArchive.Create(plan.Root, partial, runtime.CreateCredentialFreeLegacyDatabaseCopy);
                    runtime.ProtectArchive(partial);
                    File.Move(partial, archive);
                }
                catch { if (File.Exists(partial)) File.Delete(partial); throw; }
            }
            // If deletion stops halfway, retry reuses the complete archive.
            // The maintenance directory survives payload deletion, including this receipt.
            Directory.CreateDirectory(plan.ManagerDirectory);
            ReleasePackageLayout.RejectReparse(plan.ManagerDirectory);
            WriteRetention(plan, archive);
        }
        foreach (var workspace in workspaces) OwnedPackageTree.Delete(workspace);
        runtime.RemoveService(plan);
        var managerInside = plan.ManagerDirectory.StartsWith(plan.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        OwnedPackageTree.Delete(plan.Root, archive, managerInside ? plan.ManagerDirectory : null);
        runtime.RemoveRegistration(plan);
        operation?.Dispose();
        if (Directory.Exists(plan.ManagerDirectory)) OwnedPackageTree.Delete(plan.ManagerDirectory);
        if (managerInside)
        {
            var parent = Path.GetDirectoryName(plan.ManagerDirectory);
            for (var depth = 0; depth < 64 && parent is not null; depth++)
            {
                if (!string.Equals(parent, plan.Root, StringComparison.OrdinalIgnoreCase)
                    && !parent.StartsWith(plan.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Maintenance cleanup escaped the installation root.");
                if (!Directory.Exists(parent) || Directory.EnumerateFileSystemEntries(parent).Any()) break;
                ReleasePackageLayout.RejectReparse(parent);
                Directory.Delete(parent, recursive: false);
                if (string.Equals(parent, plan.Root, StringComparison.OrdinalIgnoreCase)) break;
                parent = Path.GetDirectoryName(parent);
            }
        }
        return archive;
    }

    private static string? ReadRetainedArchive(UninstallationPlan plan)
    {
        var path = Path.Combine(plan.ManagerDirectory, ReceiptName);
        if (!File.Exists(path)) return null;
        ReleasePackageLayout.RejectReparse(path);
        if (new FileInfo(path).Length > 16_384) throw new InvalidDataException("Invalid uninstall retention receipt.");
        var receipt = JsonSerializer.Deserialize<Retention>(File.ReadAllText(path));
        if (receipt?.Contract != "resource-manager-uninstall-retention-v1"
            || !string.Equals(receipt.InstallRoot, plan.Root, StringComparison.OrdinalIgnoreCase)
            || receipt.Archive != Path.GetFileName(receipt.Archive)
            || !receipt.Archive.StartsWith("ResourceManager-user-data-", StringComparison.Ordinal)
            || !receipt.Archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Uninstall retention receipt does not match this installation.");
        return Path.Combine(plan.Root, receipt.Archive);
    }

    private static void WriteRetention(UninstallationPlan plan, string archive)
    {
        var path = Path.Combine(plan.ManagerDirectory, ReceiptName);
        if (File.Exists(path)) ReleasePackageLayout.RejectReparse(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Retention("resource-manager-uninstall-retention-v1", plan.Root, Path.GetFileName(archive))));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
