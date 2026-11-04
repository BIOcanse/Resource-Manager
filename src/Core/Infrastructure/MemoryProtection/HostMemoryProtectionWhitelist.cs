using ResourceManager.App.Application.MemoryProtection;

namespace ResourceManager.App.Infrastructure.MemoryProtection;

internal static class HostMemoryProtectionWhitelist
{
    internal static bool Matches(HostMemoryProcess process, IReadOnlyList<string> entries)
    {
        var name = Path.GetFileName(process.ExecutablePath ?? process.Name);
        if (!Path.HasExtension(name)) name += ".exe";
        return entries.Any(entry => Path.IsPathFullyQualified(entry)
            ? process.ExecutablePath is { } path && string.Equals(Path.GetFullPath(entry), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)
            : string.Equals(entry, name, StringComparison.OrdinalIgnoreCase));
    }
}
