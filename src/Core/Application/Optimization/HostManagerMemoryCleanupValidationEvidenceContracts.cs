using ResourceManager.App.Domain.Optimization.MemoryCleanup;

namespace ResourceManager.App.Application.Optimization;

public sealed record HostManagerMemoryCleanupValidationActionEvidence(
    int ActionIndex,
    int SourceInputIndex,
    int ProcessId,
    long ProcessStartTimeFileTimeUtc,
    uint ReservationStateSlot,
    uint ReservationStateGeneration,
    bool ResultReturned,
    bool Succeeded,
    uint? Win32Error);

public sealed record HostManagerMemoryCleanupValidationBatchEvidence(
    long Sequence,
    ulong CycleSequence,
    Guid BatchId,
    ulong AttemptGeneration,
    ulong PlannerStateRevision,
    ulong PlannerConfigurationGeneration,
    double GuardedFreeRatio,
    double PhysicalEmergencyFreeRatio,
    double VirtualEmergencyFreeRatio,
    ulong CapacityWorkspaceIdentity,
    ulong CapacityConfigurationGeneration,
    ulong CapacityCatalogGeneration,
    ulong BaselineCapacityCommittedGeneration,
    ulong FinalCapacityCommittedGeneration,
    long BaselineCapacityCapturedAtUtcTicks,
    long FinalCapacityCapturedAtUtcTicks,
    AutomaticMemoryCleanupRequestKind RequestKind,
    double OrdinaryMemoryFreeRatio,
    bool OrdinaryMemoryFreeRatioCurrent,
    double PhysicalMemoryFreeRatio,
    bool PhysicalMemoryFreeRatioCurrent,
    double VirtualMemoryFreeRatio,
    bool VirtualMemoryFreeRatioCurrent,
    AutomaticMemoryCleanupMode SelectedMode,
    int CandidateCount,
    int RequestedCount,
    bool ResultShapeExact,
    bool OutcomeKnown,
    bool JournalSettled,
    DateTimeOffset WriterStartedAt,
    long WriterStartedAtQpcTicks,
    DateTimeOffset CompletedAt,
    long CompletedAtQpcTicks,
    IReadOnlyList<HostManagerMemoryCleanupValidationActionEvidence> Actions);

public sealed record HostManagerMemoryCleanupValidationEvidenceSnapshot(
    int SchemaVersion,
    string Contract,
    Guid? ScopeId,
    long ScopeGeneration,
    long CaptureSequence,
    DateTimeOffset CapturedAt,
    long CapturedAtQpcTicks,
    long QpcFrequency,
    bool Sealed,
    string? Failure,
    IReadOnlyList<HostManagerMemoryCleanupValidationBatchEvidence> Batches);

public interface IHostManagerMemoryCleanupValidationEvidenceControl
{
    Task<HostManagerMemoryCleanupValidationEvidenceSnapshot>
        GetMemoryCleanupValidationEvidenceAsync(CancellationToken cancellationToken);
}
