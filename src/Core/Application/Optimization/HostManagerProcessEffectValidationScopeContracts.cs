namespace ResourceManager.App.Application.Optimization;

public sealed record HostManagerProcessEffectValidationIdentity(
    int ProcessId,
    long ProcessStartTimeFileTimeUtc);

public sealed record HostManagerProcessEffectValidationScopeOpenRequest(
    Guid RunNonce,
    string JobName,
    DateTimeOffset ExpiresAt,
    string ReleaseToken,
    IReadOnlyList<HostManagerProcessEffectValidationIdentity> AllowedProcesses,
    bool AllowAutomaticMemoryCleanup = false,
    bool AllowNonAdaptedMemoryTransaction = false);

public sealed record HostManagerProcessEffectValidationScopeCloseRequest(
    Guid ScopeId,
    Guid RunNonce,
    string ReleaseToken);

public sealed record HostManagerProcessEffectValidationScopeAuditRecord(
    long Sequence,
    DateTimeOffset RecordedAt,
    string EffectFamily,
    string Stage,
    int ProcessId,
    long ProcessStartTimeFileTimeUtc,
    string Decision,
    int SystemError);

public sealed record HostManagerProcessEffectValidationScopeStatus(
    int SchemaVersion,
    string Contract,
    string State,
    Guid? ScopeId,
    Guid? RunNonce,
    DateTimeOffset? ExpiresAt,
    string? JobNameSha256,
    long Generation,
    int AllowedProcessCount,
    string? DocumentSha256,
    string? Failure,
    IReadOnlyList<HostManagerProcessEffectValidationScopeAuditRecord> AuditRecords);

public interface IHostManagerProcessEffectValidationScopeControl
{
    Task<HostManagerProcessEffectValidationScopeStatus> OpenProcessEffectValidationScopeAsync(
        HostManagerProcessEffectValidationScopeOpenRequest request,
        CancellationToken cancellationToken);

    Task<HostManagerProcessEffectValidationScopeStatus> GetProcessEffectValidationScopeAsync(
        CancellationToken cancellationToken);

    Task<HostManagerProcessEffectValidationScopeStatus> CloseProcessEffectValidationScopeAsync(
        HostManagerProcessEffectValidationScopeCloseRequest request,
        CancellationToken cancellationToken);
}
