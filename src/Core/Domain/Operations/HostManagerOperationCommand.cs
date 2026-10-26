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
    HostManagerOperationTargetKind TargetKind,
    string? TargetId = null,
    string? Scope = null,
    string? Mode = null,
    string? TargetPath = null,
    // Recorded at acceptance, never resolved again for history. Null means no recorded name.
    string? TargetName = null);

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<HostManagerOperationTargetKind>))]
public enum HostManagerOperationTargetKind : byte
{
    Component = 1,
    Dependency = 2,
    Software = 3,
    Path = 4,
    AllVolumes = 5
}
