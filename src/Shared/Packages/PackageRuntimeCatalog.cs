using System.Security.Cryptography;
using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResourceManager.Shared.Packages;

public sealed record PackageRuntimeAsset(string RelativePath, long Length, string Sha256);
public sealed record PackageStartupModule(string Id, string Kind, PackageRuntimeAsset Image,
    PackageRuntimeAsset? Manifest = null, string? LayerName = null);

/// <summary>Package admission facts. No client identity or rendering policy is inferred here.</summary>
public sealed class PackageRuntimeCatalog
{
    public const string ModuleManifestName = "runtime-modules.json";
    public const int MaximumModules = 16;
    private static readonly Lazy<PackageRuntimeCatalog> CurrentCatalog = new(CreateCurrent);
    public static PackageRuntimeCatalog Current => CurrentCatalog.Value;
    public string PackageRoot { get; }
    public IReadOnlyList<string> InfrastructureImages { get; }
    public IReadOnlyList<PackageStartupModule> StartupModules { get; }
    private readonly HashSet<string> infrastructure;
    private readonly FrozenSet<string> infrastructureNames;

    private PackageRuntimeCatalog(string root, IEnumerable<string> images, IReadOnlyList<PackageStartupModule> modules)
    {
        PackageRoot = Path.GetFullPath(root);
        infrastructure = new(images.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        InfrastructureImages = infrastructure.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        infrastructureNames = infrastructure.Select(static path => Path.GetFileNameWithoutExtension(path))
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        StartupModules = modules;
    }

    public bool IsInfrastructureImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return infrastructure.Contains(Path.GetFullPath(path)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { return false; }
    }

    // A census prefilter only. Trust still requires the exact admitted image path.
    public bool MayBeInfrastructureImageName(string imageName) => infrastructureNames.Contains(imageName);

    public bool TryGetStartupModule(string id, out PackageStartupModule? module)
    {
        module = StartupModules.FirstOrDefault(item => item.Id.Equals(id, StringComparison.Ordinal));
        return module is not null;
    }

    public string AssetPath(PackageRuntimeAsset asset) => MemberPath(PackageRoot, asset.RelativePath);

    public static PackageRuntimeCatalog Load(string packageRoot, string? currentImage = null)
    {
        var root = Path.GetFullPath(packageRoot);
        var images = new List<string>();
        if (!string.IsNullOrWhiteSpace(currentImage)) images.Add(Path.GetFullPath(currentImage));
        var releasePath = Path.Combine(root, "release-manifest.json");
        if (!File.Exists(releasePath)) releasePath = Path.Combine(root, "resource-manager-final-image.manifest.json");
        if (!File.Exists(releasePath)) return new(root, images, []);
        RejectReparse(root, releasePath);
        var releaseBytes = ReadBounded(releasePath, 16 * 1024 * 1024);
        using var release = JsonDocument.Parse(releaseBytes);
        var members = new Dictionary<string, PackageRuntimeAsset>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in release.RootElement.GetProperty("files").EnumerateArray())
        {
            if (members.Count >= 100_000) throw new InvalidDataException("Package member limit exceeded.");
            var relative = member.GetProperty("path").GetString() ?? "";
            var asset = new PackageRuntimeAsset(relative, member.GetProperty("length").GetInt64(),
                member.GetProperty("sha256").GetString() ?? "");
            ValidateAsset(root, asset);
            if (!members.TryAdd(relative, asset)) throw new InvalidDataException("Duplicate package member.");
            if (relative.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                VerifyAsset(root, asset);
                images.Add(MemberPath(root, relative));
            }
        }
        if (!members.TryGetValue(ModuleManifestName, out var descriptor)) return new(root, images, []);
        VerifyAsset(root, descriptor);
        using var manifest = JsonDocument.Parse(ReadBounded(MemberPath(root, descriptor.RelativePath), 128 * 1024));
        if (manifest.RootElement.GetProperty("version").GetInt32() != 1)
            throw new InvalidDataException("Unsupported startup module manifest.");
        var modules = new List<PackageStartupModule>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in manifest.RootElement.GetProperty("modules").EnumerateArray())
        {
            if (modules.Count >= MaximumModules) throw new InvalidDataException("Startup module limit exceeded.");
            var module = entry.Deserialize(PackageRuntimeCatalogJson.Default.PackageStartupModule)
                ?? throw new InvalidDataException("Invalid startup module.");
            if (string.IsNullOrWhiteSpace(module.Id) || module.Id.Length > 128
                || module.Id.Any(static ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_' or '.'))
                || !ids.Add(module.Id) || module.Image is null)
                throw new InvalidDataException("Invalid or duplicate startup module ID.");
            VerifyMember(module.Image);
            if (module.Kind == "preload")
            {
                if (module.Manifest is not null || module.LayerName is not null
                    || !module.Image.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Invalid preload module.");
            }
            else if (module.Kind == "vulkan-layer")
            {
                if (module.Manifest is null || string.IsNullOrWhiteSpace(module.LayerName) || module.LayerName.Length > 256
                    || module.LayerName.Any(static ch => !char.IsAsciiLetterOrDigit(ch) && ch != '_')
                    || !module.Manifest.RelativePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Invalid Vulkan module.");
                VerifyMember(module.Manifest);
                using var layer = JsonDocument.Parse(ReadBounded(MemberPath(root, module.Manifest.RelativePath), 128 * 1024));
                var layerObject = layer.RootElement.GetProperty("layer");
                var library = layerObject.GetProperty("library_path").GetString() ?? "";
                var libraryPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(MemberPath(root, module.Manifest.RelativePath))!, library));
                if (layerObject.GetProperty("name").GetString() != module.LayerName
                    || !libraryPath.Equals(MemberPath(root, module.Image.RelativePath), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Vulkan manifest does not describe the admitted module.");
            }
            else throw new InvalidDataException("Unsupported startup loader.");
            modules.Add(module);
        }
        return new(root, images, modules);

        void VerifyMember(PackageRuntimeAsset asset)
        {
            if (!members.TryGetValue(asset.RelativePath, out var admitted) || asset != admitted)
                throw new InvalidDataException("Startup module is not a package member.");
            VerifyAsset(root, asset);
        }
    }

    private static PackageRuntimeCatalog CreateCurrent()
    {
        var directory = Path.GetFullPath(AppContext.BaseDirectory);
        var root = directory;
        var found = false;
        for (var depth = 0; depth < 8; depth++)
        {
            if (File.Exists(Path.Combine(root, "release-manifest.json"))
                || File.Exists(Path.Combine(root, "resource-manager-final-image.manifest.json"))) { found = true; break; }
            var parent = Path.GetDirectoryName(root);
            if (parent is null) break;
            root = parent;
        }
        // Unpackaged development protects the actual host and admits no loadable module.
        // Present but invalid package metadata is an explicit startup failure.
        return Load(found ? root : directory, Environment.ProcessPath);
    }

    private static byte[] ReadBounded(string path, int maximum)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > maximum) throw new InvalidDataException("Package metadata limit exceeded.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void ValidateAsset(string root, PackageRuntimeAsset asset)
    {
        _ = MemberPath(root, asset.RelativePath);
        if (asset.Length < 0 || asset.Length > 512L * 1024 * 1024 || asset.Sha256.Length != 64 || !asset.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid package asset digest.");
    }

    private static string MemberPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 1024 || Path.IsPathRooted(relative)
            || relative.Contains('\\') || relative.Contains(':') || relative.Contains(';') || relative.Any(char.IsControl)
            || relative.Split('/').Any(static part => part is "" or "." or ".."))
            throw new InvalidDataException("Invalid package member path.");
        return Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static void VerifyAsset(string root, PackageRuntimeAsset asset)
    {
        ValidateAsset(root, asset);
        var path = MemberPath(root, asset.RelativePath);
        RejectReparse(root, path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length != asset.Length || !Convert.ToHexString(SHA256.HashData(stream)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Package runtime asset digest mismatch.");
    }

    private static void RejectReparse(string root, string path)
    {
        var current = path;
        for (var depth = 0; depth < 256; depth++)
        {
            ReleasePackageLayout.RejectReparse(current);
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent.Equals(current, StringComparison.OrdinalIgnoreCase)) return;
            current = parent;
        }
        throw new InvalidDataException("Package member ancestry limit exceeded.");
    }

}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PackageStartupModule))]
internal sealed partial class PackageRuntimeCatalogJson : JsonSerializerContext { }
