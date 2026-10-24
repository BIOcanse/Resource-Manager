using System.Text.Json.Serialization;
using ResourceManager.App.Domain.BrowserRuntimes;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Software;
namespace ResourceManager.App.Application.Management;
partial class ManagementInventoryRevisionOwner
{
    private sealed record BrowserRuntimeDigestInput(string InstallComponentId, BrowserRuntimeEntry? SharedRuntime,
        BrowserRuntimeEntry? BrowserFallback, BrowserRuntimeEntry[] Candidates);
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(SoftwareRecord[]))]
    [JsonSerializable(typeof(ComponentStatus))]
    [JsonSerializable(typeof(BrowserRuntimeDigestInput))]
    private partial class ManagementInventoryJsonContext : JsonSerializerContext;
}
