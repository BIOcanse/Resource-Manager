namespace ResourceManager.App.Domain.Operations;

public sealed record HostManagerOperationSubmitCommand(
    string Kind,
    string Title,
    string? DomainKey,
    uint RequestSchemaId,
    uint RequestSchemaVersion,
    byte[] CanonicalRequest);
