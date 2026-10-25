namespace ResourceManager.App.Domain.Operations;

public sealed record HostManagerOperationSubmitCommand(
    string Kind,
    HostManagerOperationSubject Subject,
    string? DomainKey,
    uint RequestSchemaId,
    uint RequestSchemaVersion,
    byte[] CanonicalRequest);

// The kind identifies the action; these are the exact business inputs identifying its target.
public sealed record HostManagerOperationSubject(
    string? TargetId = null,
    string? Scope = null,
    string? Mode = null,
    string? TargetPath = null);
