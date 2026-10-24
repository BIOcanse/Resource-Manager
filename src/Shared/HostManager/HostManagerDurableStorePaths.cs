using System.Text.Json;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed record HostManagerDurableStorePath(string Kind, string CanonicalPath, string ManifestPath);

/// <summary>Canonical locations shared by runtime owners and user-data acceptance.</summary>
public static class HostManagerDurableStorePaths
{
    private const string DefaultProfileResource = "ResourceManager.Shared.HostManager.default.json";
    private const string Generation = "generation-00000001";

    public static IReadOnlyList<HostManagerDurableStorePath> Resolve(string installRoot)
        => Resolve(installRoot, ReadDefaultProfileBytes());

    public static byte[] ReadDefaultProfileBytes()
    {
        // This resource links the exact profile selected by the production runtime.
        // There is no disk-profile fallback or second set of default path literals.
        using var stream = typeof(HostManagerDurableStorePaths).Assembly.GetManifestResourceStream(DefaultProfileResource)
            ?? throw new InvalidOperationException("The shared Host Manager default profile is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public static IReadOnlyList<HostManagerDurableStorePath> Resolve(string installRoot, ReadOnlyMemory<byte> profileUtf8)
    {
        if (profileUtf8.Length is <= 0 or > 4 * 1024 * 1024)
            throw new InvalidDataException("The Host Manager path profile has an invalid size.");
        try
        {
            using var document = JsonDocument.Parse(profileUtf8);
            var recreate = Required(document.RootElement, "host_recreate");
            var journal = ReadRelativePath(recreate, "transaction_journal", "journal_relative_path");
            var ledger = ReadRelativePath(recreate, "applied_ownership", "ledger_relative_path");
            var operation = ReadRelativePath(recreate, "operation_coordinator", "canonical_envelope_relative_path");
            var dataRoot = ResolveRuntimeDataRoot(installRoot);
            return
            [
                Adjacent("transaction-journal", ResolveRelativePath(dataRoot, journal)),
                Adjacent("applied-ownership", ResolveRelativePath(dataRoot, ledger)),
                Adjacent("operation-coordinator", ResolveRelativePath(Path.Combine(dataRoot, "HostManager"), operation)),
                ResolveRollbackState(installRoot),
                ResolveMemoryCleanup(installRoot)
            ];
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Host Manager path profile is invalid.", exception);
        }
    }

    public static string ResolveRuntimeDataRoot(string installRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        if (!Path.IsPathFullyQualified(installRoot))
            throw new ArgumentException("The stable Resource Manager install root must be absolute.", nameof(installRoot));
        return Path.GetFullPath(Path.Combine(installRoot, "UserData"));
    }

    public static HostManagerDurableStorePath ResolveRollbackState(string installRoot)
    {
        var root = Path.Combine(ResolveRuntimeDataRoot(installRoot), "HostManager", "RollbackState");
        return new("rollback-state", Path.Combine(root, Generation, "rollback-state.json"),
            Path.Combine(root, "rollback-state.root.manifest"));
    }

    public static HostManagerDurableStorePath ResolveMemoryCleanup(string installRoot)
        => Adjacent("memory-cleanup-attempt-journal", Path.Combine(ResolveRuntimeDataRoot(installRoot),
            "HostManager", "MemoryCleanup", Generation, "host-manager-memory-cleanup-attempts.json"));

    // Compiled paths already use '/', so runtime owners reject rather than
    // normalize malformed compiled values. Configuration normalization is separate.
    public static string ResolveRelativePath(string dataRoot, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        if (!Path.IsPathFullyQualified(dataRoot))
            throw new ArgumentException("The Host Manager data root must be absolute.", nameof(dataRoot));
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)
            || relativePath.Contains('\\') || relativePath.Length >= 2 && char.IsAsciiLetter(relativePath[0]) && relativePath[1] == ':')
            throw new InvalidDataException("The Host Manager store path must be a canonical relative path.");
        var segments = relativePath.Split('/', StringSplitOptions.None);
        if (segments.Any(static segment => string.IsNullOrEmpty(segment) || segment is "." or ".."))
            throw new InvalidDataException("The Host Manager store path contains an invalid segment.");
        var root = Path.GetFullPath(dataRoot);
        var result = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Host Manager store path escapes its data root.");
        return result;
    }

    private static HostManagerDurableStorePath Adjacent(string kind, string canonicalPath)
        => new(kind, canonicalPath, canonicalPath + ".root.manifest");

    private static string ReadRelativePath(JsonElement recreate, string owner, string property)
    {
        var value = Required(Required(recreate, owner), property);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"host_recreate.{owner}.{property} must be an explicit relative path.");
        // The profile compiler accepts either separator and publishes '/'.
        return value.GetString()!.Replace('\\', '/');
    }

    private static JsonElement Required(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The Host Manager path profile must contain objects.");
        JsonElement result = default;
        var count = 0;
        foreach (var property in parent.EnumerateObject())
            if (property.NameEquals(name)) { result = property.Value; count++; }
        if (count != 1)
            throw new InvalidDataException($"The Host Manager path profile must contain exactly one '{name}'.");
        return result;
    }
}
