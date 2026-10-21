using System.Text.Json;

namespace ResourceManager.Shared.Packages;

internal static class UninstallationWorkspaces
{
    internal static IReadOnlyList<string> Read(UninstallationPlan plan)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(plan.Root))!;
        ReleasePackageLayout.RejectReparse(parent);
        var owned = new List<string>();
        var count = 0;
        foreach (var directory in Directory.EnumerateDirectories(parent, "ResourceManager-update-*"))
        {
            if (++count > 100_000) throw new InvalidDataException("Update workspace count exceeds the limit.");
            var name = Path.GetFileName(directory);
            var download = name.StartsWith("ResourceManager-update-download-", StringComparison.Ordinal);
            var prefix = download ? "ResourceManager-update-download-" : "ResourceManager-update-";
            if (!Guid.TryParseExact(name[prefix.Length..], "N", out _)) continue;
            ReleasePackageLayout.RejectReparse(directory);
            var path = Path.Combine(directory, download ? "product-download.json" : "update-transaction.json");
            if (!File.Exists(path)) continue;
            ReleasePackageLayout.RejectReparse(path);
            if (new FileInfo(path).Length > 16_384) throw new InvalidDataException("Invalid update workspace receipt.");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var data = document.RootElement;
            if (!string.Equals(data.GetProperty("InstallRoot").GetString(), plan.Root, StringComparison.OrdinalIgnoreCase)) continue;
            if (download)
            {
                if (data.GetProperty("Contract").GetString() != "resource-manager-product-download-v1")
                    throw new InvalidDataException("Unknown download ownership contract.");
            }
            else if (data.GetProperty("stage").GetString() is not ("committed" or "rolledBack" or "recovered"))
                throw new InvalidOperationException("Complete the interrupted update recovery in ResourceManager.UpdateManager.exe before uninstalling.");
            _ = OwnedPackageTree.Read(directory);
            owned.Add(directory);
        }
        return owned;
    }
}
