using System.Text.Json.Serialization;
using ResourceManager.NativeUi.Preferences;

namespace ResourceManager.NativeUi.Overlay;

internal sealed record StartupModuleOperation(string SoftwareId, IReadOnlyList<string> ModuleIds);
internal sealed record StartupModuleOperationsDocument(int Version, IReadOnlyList<StartupModuleOperation> Software);

internal static class OverlayStartupModules
{
    private static readonly string[] ModuleIds = ["performance-overlay-preload", "performance-overlay-vulkan"];
    internal static bool Enabled(string softwareId, FrontendOverlayPresentation choice, StartupModuleOperationsDocument operations)
    {
        if (choice.Mode != "injected") return choice.Enabled;
        var selection = operations.Software.FirstOrDefault(item => item.SoftwareId.Equals(softwareId, StringComparison.OrdinalIgnoreCase));
        return selection is not null && ModuleIds.All(id => selection.ModuleIds.Contains(id, StringComparer.Ordinal));
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StartupModuleOperationsDocument))]
internal sealed partial class OverlayStartupModulesJson : JsonSerializerContext { }
