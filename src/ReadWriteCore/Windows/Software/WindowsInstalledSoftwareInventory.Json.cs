using System.Text.Json.Serialization;
namespace ResourceManager.App.Infrastructure.Software;
partial class WindowsInstalledSoftwareInventory
{
    private sealed record SoftwareContentDigestInput(string Id, string Name, string? Version, string? Publisher,
        string? InstallLocation, string[] RootPaths, string? UninstallString, string RegistryPath, ulong? ReportedSizeBytes);
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(SoftwareContentDigestInput[]))]
    private partial class SoftwareContentJsonContext : JsonSerializerContext;
}
