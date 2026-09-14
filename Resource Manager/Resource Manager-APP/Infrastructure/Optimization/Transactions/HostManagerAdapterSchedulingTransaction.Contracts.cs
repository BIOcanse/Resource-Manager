using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Adaptation.Scheduling;

namespace ResourceManager.App.Infrastructure.Optimization.Transactions;

internal enum HostManagerAdapterSchedulingCaptureStatus : byte
{
    Captured = 1,
    Unsupported = 2,
    Unavailable = 3,
    Conflict = 4,
    InvalidPayload = 5
}

internal sealed class HostManagerAdapterSchedulingCapturedState
{
    private readonly byte[] envelope;

    internal HostManagerAdapterSchedulingCapturedState(
        AdapterSchedulingStateIdentity identity,
        ReadOnlySpan<byte> envelope)
    {
        Identity = identity;
        this.envelope = envelope.ToArray();
    }

    internal AdapterSchedulingStateIdentity Identity { get; }

    internal byte[] Envelope => envelope.ToArray();
}

internal sealed record HostManagerAdapterSchedulingCaptureResult(
    HostManagerAdapterSchedulingCaptureStatus Status,
    HostManagerAdapterSchedulingCapturedState? CapturedState,
    string Message);

internal enum HostManagerAdapterSchedulingApplyStatus : byte
{
    Applied = 1,
    AlreadyApplied = 2,
    Rejected = 3,
    Unavailable = 4,
    OwnershipLost = 5,
    StateUncertain = 6
}

internal sealed class HostManagerAdapterSchedulingApplyCommand
{
    private readonly byte[] capturedEnvelope;

    internal HostManagerAdapterSchedulingApplyCommand(
        ReadOnlySpan<byte> capturedEnvelope,
        int maximumOpaquePayloadBytes,
        int maximumEnvelopeBytes,
        string policyId,
        DateTimeOffset generatedAt,
        string targetId,
        string displayName,
        AdapterCpuSchedulingGrade? targetCpuGrade,
        AdapterGpuSchedulingGrade? targetGpuGrade,
        double cpuScore,
        double gpuScore,
        string reason,
        DateTimeOffset deadline)
    {
        this.capturedEnvelope = capturedEnvelope.ToArray();
        MaximumOpaquePayloadBytes = maximumOpaquePayloadBytes;
        MaximumEnvelopeBytes = maximumEnvelopeBytes;
        PolicyId = policyId;
        GeneratedAt = generatedAt;
        TargetId = targetId;
        DisplayName = displayName;
        TargetCpuGrade = targetCpuGrade;
        TargetGpuGrade = targetGpuGrade;
        CpuScore = cpuScore;
        GpuScore = gpuScore;
        Reason = reason;
        Deadline = deadline;
    }

    internal byte[] CapturedEnvelope => capturedEnvelope.ToArray();

    internal int MaximumOpaquePayloadBytes { get; }

    internal int MaximumEnvelopeBytes { get; }

    internal string PolicyId { get; }

    internal DateTimeOffset GeneratedAt { get; }

    internal string TargetId { get; }

    internal string DisplayName { get; }

    internal AdapterCpuSchedulingGrade? TargetCpuGrade { get; }

    internal AdapterGpuSchedulingGrade? TargetGpuGrade { get; }

    internal double CpuScore { get; }

    internal double GpuScore { get; }

    internal string Reason { get; }

    internal DateTimeOffset Deadline { get; }
}

internal sealed record HostManagerAdapterSchedulingApplyResult(
    HostManagerAdapterSchedulingApplyStatus Status,
    AdapterSchedulingStateIdentity? ObservedIdentity,
    AdapterCpuSchedulingGrade? ObservedCpuGrade,
    AdapterGpuSchedulingGrade? ObservedGpuGrade,
    string Message);

internal enum HostManagerAdapterSchedulingRestoreStatus : byte
{
    Restored = 1,
    AlreadyRestored = 2,
    OwnershipLost = 3,
    Unavailable = 4,
    Conflict = 5,
    InvalidPayload = 6,
    StateUncertain = 7
}

internal sealed class HostManagerAdapterSchedulingRestoreCommand
{
    private readonly byte[] capturedEnvelope;

    internal HostManagerAdapterSchedulingRestoreCommand(
        ReadOnlySpan<byte> capturedEnvelope,
        int maximumOpaquePayloadBytes,
        int maximumEnvelopeBytes,
        DateTimeOffset deadline)
    {
        this.capturedEnvelope = capturedEnvelope.ToArray();
        MaximumOpaquePayloadBytes = maximumOpaquePayloadBytes;
        MaximumEnvelopeBytes = maximumEnvelopeBytes;
        Deadline = deadline;
    }

    internal byte[] CapturedEnvelope => capturedEnvelope.ToArray();

    internal int MaximumOpaquePayloadBytes { get; }

    internal int MaximumEnvelopeBytes { get; }

    internal DateTimeOffset Deadline { get; }
}

internal sealed record HostManagerAdapterSchedulingRestoreResult(
    HostManagerAdapterSchedulingRestoreStatus Status,
    AdapterSchedulingStateIdentity? ObservedIdentity,
    AdapterCpuSchedulingGrade? ObservedCpuGrade,
    AdapterGpuSchedulingGrade? ObservedGpuGrade,
    string Message);
