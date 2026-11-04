namespace ResourceManager.App.Domain.Adaptation;

public static class AdapterRegistrationSchemaVersions
{
    public const string Current = "2.0.0";
}

public static class AdapterResourceMarkerTransports
{
    public const string LoopbackHttp = "loopback-http";
}

public static class AdapterResourceMarkerStates
{
    public const string Online = "online";
    public const string Unreachable = "unreachable";
    public const string Unsupported = "unsupported";
}

public sealed record AdapterSoftwareRegistrationRequest(
    string SchemaVersion,
    string AdapterId,
    string AppId,
    string DisplayName,
    string? Vendor,
    IReadOnlyList<string> ProgramRootPaths,
    IReadOnlyList<AdapterProcessDeclaration> Processes,
    IReadOnlyList<AdapterServiceDeclaration> Services,
    AdapterResourceMarkerEndpoint? ResourceMarkerEndpoint);

public sealed record AdapterProcessDeclaration(
    string Name,
    int? ProcessId,
    string? ExecutablePath);

public sealed record AdapterServiceDeclaration(
    string Name,
    string? DisplayName);

public sealed record AdapterResourceMarkerEndpoint(
    string Transport,
    string Address);

public sealed record AdapterResourceMarkerProbeResult(
    string State,
    DateTimeOffset CheckedAt,
    int? StatusCode,
    string Message)
{
    // Process proof is generated in the service, not restored from HTTP or a historical response.
    [System.Text.Json.Serialization.JsonIgnore]
    internal bool OwnershipVerified { get; init; }
}

public sealed record AdapterSoftwareRegistration(
    string Id,
    string SchemaVersion,
    string AdapterId,
    string AppId,
    string DisplayName,
    string? Vendor,
    IReadOnlyList<string> ProgramRootPaths,
    IReadOnlyList<AdapterProcessDeclaration> Processes,
    IReadOnlyList<AdapterServiceDeclaration> Services,
    AdapterResourceMarkerEndpoint ResourceMarkerEndpoint,
    AdapterResourceMarkerProbeResult LastResourceMarkerProbe,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool OwnershipVerified = false);
