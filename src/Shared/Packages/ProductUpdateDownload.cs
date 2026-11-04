using System.Text.Json;

namespace ResourceManager.Shared.Packages;

// The product service records a verified download before handing it to the updater.
// A manually selected package has no such receipt and is never removed.
public static class ProductUpdateDownload
{
    private const string ReceiptName = "product-download.json";
    private sealed record Receipt(string Contract, string InstallRoot, string Version);

    public static void RecordReady(string packageRoot, string installRoot, string version)
    {
        var workspace = Workspace(packageRoot, installRoot)
            ?? throw new InvalidDataException("Download workspace is not owned by this installation.");
        _ = OwnedPackageTree.Read(workspace);
        using var file = new FileStream(Path.Combine(workspace, ReceiptName), FileMode.CreateNew);
        JsonSerializer.Serialize(file, new Receipt("resource-manager-product-download-v1", Path.GetFullPath(installRoot), version));
    }

    public static bool RemoveCompleted(string packageRoot, string installRoot, string installedVersion)
    {
        var workspace = Workspace(packageRoot, installRoot);
        if (workspace is null) return false;
        ReleasePackageLayout.RejectReparse(workspace);
        var path = Path.Combine(workspace, ReceiptName);
        if (!File.Exists(path)) return false;
        ReleasePackageLayout.RejectReparse(path);
        if (new FileInfo(path).Length > 16_384) throw new InvalidDataException("Invalid download receipt.");
        var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path));
        if (receipt?.Contract != "resource-manager-product-download-v1"
            || !string.Equals(receipt.InstallRoot, Path.GetFullPath(installRoot), StringComparison.OrdinalIgnoreCase)
            || receipt.Version != installedVersion)
            throw new InvalidDataException("Download receipt does not match the completed update.");
        OwnedPackageTree.Delete(workspace);
        return true;
    }

    private static string? Workspace(string packageRoot, string installRoot)
    {
        var package = Path.GetFullPath(packageRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (Path.GetFileName(package) != "package") return null;
        var workspace = Path.GetDirectoryName(package)!;
        var name = Path.GetFileName(workspace);
        const string prefix = "ResourceManager-update-download-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _)
            || !string.Equals(Path.GetDirectoryName(workspace), Path.GetDirectoryName(Path.GetFullPath(installRoot)), StringComparison.OrdinalIgnoreCase))
            return null;
        ReleasePackageLayout.RejectReparse(Path.GetDirectoryName(workspace)!);
        return workspace;
    }
}
