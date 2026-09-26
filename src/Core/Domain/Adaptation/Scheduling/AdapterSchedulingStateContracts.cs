using ResourceManager.App.Domain.Adaptation;

namespace ResourceManager.App.Domain.Adaptation.Scheduling;

public static class AdapterSchedulingPolicyIds
{
    public const string HostManager = "host-manager-smart-coordinator";
}

public enum AdapterSchedulingStateExportStatus : byte
{
    Exported = 1,
    Unsupported = 2,
    Unavailable = 3,
    Conflict = 4
}

public enum AdapterSchedulingStateRestoreStatus : byte
{
    Restored = 1,
    Unsupported = 2,
    Unavailable = 3,
    Conflict = 4
}

public enum AdapterSchedulingStateOwnershipResult : byte
{
    NotEvaluated = 0,
    Restored = 1,
    AlreadyRestored = 2,
    OwnershipLost = 3
}

public sealed record AdapterSchedulingStateIdentity(
    AdapterInstanceId AdapterInstanceId,
    AdapterInstanceLeaseId LeaseId,
    ulong LeaseGeneration,
    string SoftwareId,
    string AdapterId,
    string ApplicationId);

public sealed record AdapterSchedulingStatePayload(
    uint Version,
    string Sha256Digest,
    byte[] Bytes);

public sealed record AdapterSchedulingStateExportResult(
    AdapterSchedulingStateExportStatus Status,
    AdapterSchedulingStateIdentity? Identity,
    AdapterSchedulingStatePayload? Payload,
    AdapterCpuSchedulingGrade? CurrentCpuGrade,
    AdapterGpuSchedulingGrade? CurrentGpuGrade,
    DateTimeOffset ObservedAt,
    string Message);

public sealed record AdapterSchedulingStateRestoreCommand(
    AdapterSchedulingStateIdentity Identity,
    AdapterSchedulingStatePayload Payload,
    AdapterCpuSchedulingGrade? ExpectedCpuGrade,
    AdapterGpuSchedulingGrade? ExpectedGpuGrade,
    int MaximumPayloadBytes,
    DateTimeOffset Deadline);

public sealed record AdapterSchedulingStateRestoreResult(
    AdapterSchedulingStateRestoreStatus Status,
    AdapterSchedulingStateOwnershipResult OwnershipResult,
    AdapterSchedulingStateIdentity? Identity,
    AdapterCpuSchedulingGrade? ObservedCpuGrade,
    AdapterGpuSchedulingGrade? ObservedGpuGrade,
    DateTimeOffset ObservedAt,
    string Message);
