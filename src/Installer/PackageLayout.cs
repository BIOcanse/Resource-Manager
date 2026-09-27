using System.Security.Cryptography;
using System.Text.Json;

namespace ResourceManager.Installer;

internal sealed record PackageLayout(string Root, string Version, string Backend, string NativeUi,
    string InternalLauncher, string Start, string Installer)
{
    public static PackageLayout Verify(string packageRoot)
    {
        var root = Path.GetFullPath(packageRoot).TrimEnd(Path.DirectorySeparatorChar);
        var manifestPath = Path.Combine(root, "release-manifest.json");
        if (!Directory.Exists(root) || !File.Exists(manifestPath))
            throw new InvalidDataException("发行包或 release-manifest.json 不存在。");
        RejectReparse(root);
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var top = manifest.RootElement;
        var version = top.GetProperty("version").GetString();
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(top.GetProperty("sourceCommit").GetString()))
            throw new InvalidDataException("发行包缺少版本或来源提交信息。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in top.GetProperty("files").EnumerateArray())
        {
            var relative = entry.GetProperty("path").GetString() ?? "";
            if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.Contains('\\') ||
                relative.Split('/').Any(part => part is "" or "." or "..") || !seen.Add(relative))
                throw new InvalidDataException($"发行清单路径不安全或重复：{relative}");
            var file = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file)) throw new InvalidDataException($"发行文件缺失：{relative}");
            RejectReparse(file);
            var info = new FileInfo(file);
            var expectedLength = entry.GetProperty("length").GetInt64();
            var expectedHash = entry.GetProperty("sha256").GetString();
            using var stream = File.OpenRead(file);
            if (info.Length != expectedLength ||
                !Convert.ToHexString(SHA256.HashData(stream)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"发行文件校验失败：{relative}");
        }
        var actual = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        pending.Enqueue(root);
        while (pending.Count > 0)
        {
            var directory = pending.Dequeue();
            RejectReparse(directory);
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                RejectReparse(child);
                if (Directory.Exists(child)) pending.Enqueue(child);
                else actual.Add(Path.GetRelativePath(root, child).Replace('\\', '/'));
            }
        }
        actual.Remove("release-manifest.json");
        if (!actual.SetEquals(seen)) throw new InvalidDataException("发行包中有清单外文件或成员缺失。");
        foreach (var name in new[] { "UserData", "Dependencies", "Misc" })
            if (Directory.Exists(Path.Combine(root, name)))
                throw new InvalidDataException($"发行包包含运行数据目录：{name}");
        var config = Path.Combine(root, "Config");
        if (!Directory.Exists(config) || Directory.EnumerateFileSystemEntries(config).Any())
            throw new InvalidDataException("发行包 Config 必须存在且为空。");
        var layout = ForRoot(root, version);
        foreach (var path in new[] { layout.Backend, layout.NativeUi, layout.InternalLauncher,
                     layout.Start, layout.Installer, Path.Combine(root, "Bin", "ResourceManager", "wwwroot", "index.html") })
            if (!File.Exists(path)) throw new InvalidDataException($"发行包缺少程序：{path}");
        return layout;
    }

    public static PackageLayout ForRoot(string root, string version) => new(root, version,
        Path.Combine(root, "Bin", "ResourceManager", "ResourceManager.exe"),
        Path.Combine(root, "Bin", "ResourceManagerNativeUi", "ResourceManager.NativeUi.exe"),
        Path.Combine(root, "Bin", "ResourceManagerLauncher", "ResourceManager.Launcher.exe"),
        Path.Combine(root, "Start.exe"), Path.Combine(root, "Install.exe"));

    public static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"安装路径含重解析点：{path}");
    }
}
