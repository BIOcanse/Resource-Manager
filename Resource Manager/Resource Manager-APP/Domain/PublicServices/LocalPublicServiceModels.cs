namespace ResourceManager.App.Domain.PublicServices;

public static class LocalPublicServiceCapabilityIds
{
    public const string FileIndex = "file-index";
    public const string SqliteDatabase = "sqlite-database";
    public const string AiModelCatalog = "ai-model-catalog";
    public const string BrowserRuntimeCatalog = "browser-runtime-catalog";
    public const string PublicResourceDirectory = "public-resource-directory";
}

public sealed record LocalPublicServiceEndpoint(
    string Method,
    string Path);

public sealed record LocalPublicServiceCapability(
    string Id,
    string Version,
    bool Enabled,
    bool Available,
    string Description,
    IReadOnlyList<LocalPublicServiceEndpoint> Endpoints);

public sealed record LocalPublicServiceDescriptor(
    string ServiceName,
    string ApiVersion,
    string BasePath,
    bool Enabled,
    IReadOnlyList<LocalPublicServiceCapability> Capabilities,
    DateTimeOffset CapturedAt);

public sealed record LocalPublicServiceAccessDecision(
    bool Allowed,
    int StatusCode,
    string Reason,
    ulong CompletionHandle);
