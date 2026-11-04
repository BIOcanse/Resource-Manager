using System.Text.Json;
using ResourceManager.Shared.Packages;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.Frontend.Packaging;

/// <summary>Frontend-owned upgrade adapter, linked only into installation/update composition.</summary>
internal static class LegacyStartupModuleMigration
{
    private static readonly string[] ModuleIds = ["performance-overlay-preload", "performance-overlay-vulkan"];

    internal static void Migrate(string packageRoot, string dataRoot)
    {
        var directory = Path.Combine(Path.GetFullPath(dataRoot), "UserData", "SoftwareProfiles");
        var destination = Path.Combine(directory, "startup-modules.local.json");
        // An explicitly empty generic selection is already a decision. Never re-enable it.
        if (File.Exists(destination)) return;
        var legacy = Path.Combine(directory, "performance-overlay.local.json");
        if (!File.Exists(legacy)) legacy += ".frontend-preferences-v1.bak";
        if (!File.Exists(legacy)) return;
        RejectReparse(legacy);
        using var input = WindowsNativeAtomicFileCommitter.OpenCommittedRead(legacy);
        if (input.Length > 4 * 1024 * 1024) throw new InvalidDataException("Legacy startup configuration limit exceeded.");
        using var image = JsonDocument.Parse(input);
        var software = image.RootElement.GetProperty("software");
        if (software.GetArrayLength() > 4096) throw new InvalidDataException("Legacy startup software limit exceeded.");
        var selections = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in software.EnumerateArray())
        {
            var id = entry.GetProperty("softwareId").GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (id.Length > 256) throw new InvalidDataException("Invalid legacy software ID.");
            var enabled = entry.TryGetProperty("enabled", out var value) && value.GetBoolean();
            var injected = entry.TryGetProperty("mode", out var mode)
                && string.Equals(mode.GetString(), "injected", StringComparison.OrdinalIgnoreCase);
            selections[id] = enabled && injected;
        }
        if (selections.Values.Any(static selected => selected))
        {
            var catalog = PackageRuntimeCatalog.Load(packageRoot);
            if (ModuleIds.Any(id => !catalog.TryGetStartupModule(id, out _)))
                throw new InvalidDataException("The package cannot preserve the requested startup modules.");
        }
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new Utf8JsonWriter(output))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("version", 1);
                    writer.WriteStartArray("software");
                    foreach (var (id, selected) in selections.OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        writer.WriteStartObject();
                        writer.WriteString("softwareId", id);
                        writer.WriteStartArray("moduleIds");
                        if (selected) foreach (var moduleId in ModuleIds) writer.WriteStringValue(moduleId);
                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                output.Flush(true);
            }
            // First creation is atomic and cannot overwrite a concurrent explicit selection.
            try { File.Move(temporary, destination); }
            catch (IOException) when (File.Exists(destination)) { }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void RejectReparse(string path)
    {
        for (var depth = 0; depth < 256; depth++)
        {
            ReleasePackageLayout.RejectReparse(path);
            var parent = Path.GetDirectoryName(path);
            if (parent is null || parent == path) return;
            path = parent;
        }
        throw new InvalidDataException("Legacy configuration ancestry limit exceeded.");
    }
}
