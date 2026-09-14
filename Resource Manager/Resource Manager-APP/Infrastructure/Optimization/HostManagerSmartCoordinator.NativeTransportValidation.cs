using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    internal static uint ValidateNativeActionBatch(
        NativeSmartCoordinatorWorkspace workspace)
    {
        var snapshot = workspace.Snapshot;
        if (workspace.PlannedActionCount > checked(snapshot.ProcessCount + snapshot.SoftwareCount))
        {
            throw new InvalidDataException(
                "Host Manager smart coordinator action count exceeds its process/software snapshot.");
        }

        var actionIds = new HashSet<ulong>();
        var claimedDomains = new HashSet<NativeActionDomainIdentity>();
        var processEntities = new HashSet<(ulong TargetKey, uint ProcessId, ulong ProcessStartKey)>();
        var softwareEntities = new HashSet<ulong>();
        var groups = new Dictionary<ulong, NativeAtomicGroupShape>();
        var atomicGroupBySoftware = new Dictionary<ulong, ulong>();
        uint requiredFeedbackCount = 0;
        uint processActionCount = 0;
        uint softwareActionCount = 0;
        NativeSmartCoordinatorAction? previous = null;
        for (uint index = 0; index < workspace.PlannedActionCount; index++)
        {
            var action = workspace.GetPlannedAction(index);
            ValidateNativeAction(action, index, snapshot);
            if (previous.HasValue && NativeActionComesBefore(action, previous.Value))
            {
                throw new InvalidDataException(
                    $"Host Manager smart coordinator action {action.ActionId} violates native semantic ordering.");
            }
            previous = action;

            if (action.Flags.HasFlag(NativeSmartCoordinatorActionFlags.RequiresFeedback))
            {
                requiredFeedbackCount++;
            }
            if (action.Scope == NativeSmartCoordinatorActionScope.ProcessPolicy)
            {
                processActionCount++;
                if (!processEntities.Add((action.TargetKey, action.ProcessId, action.ProcessStartKey)))
                {
                    throw new InvalidDataException(
                        $"Host Manager smart coordinator repeated process entity {action.TargetKey}/{action.ProcessId}/{action.ProcessStartKey}.");
                }
            }
            else
            {
                softwareActionCount++;
                if (!softwareEntities.Add(action.SoftwareKey))
                {
                    throw new InvalidDataException(
                        $"Host Manager smart coordinator repeated software entity {action.SoftwareKey}.");
                }
            }
            if (!actionIds.Add(action.ActionId))
            {
                throw new InvalidDataException(
                    $"Host Manager smart coordinator repeated action ID {action.ActionId} in one batch.");
            }

            foreach (var domain in EnumerateActionDomains(action.DomainMask))
            {
                var identity = new NativeActionDomainIdentity(
                    action.Scope,
                    action.TargetKey,
                    action.ProcessId,
                    action.ProcessStartKey,
                    domain);
                if (!claimedDomains.Add(identity))
                {
                    throw new InvalidDataException(
                        $"Host Manager smart coordinator repeated target/domain {action.TargetKey}/{(byte)domain} in one batch.");
                }
            }

            if (action.AtomicGroupId == 0)
            {
                continue;
            }

            var shape = new NativeAtomicGroupShape(
                action.GroupMemberCount,
                action.SoftwareKey,
                BitConverter.DoubleToInt64Bits(action.CpuScore),
                new HashSet<uint>());
            if (groups.TryGetValue(action.AtomicGroupId, out var existing))
            {
                shape = existing;
                if (shape.MemberCount != action.GroupMemberCount
                    || shape.SoftwareKey != action.SoftwareKey
                    || shape.ScoreBits != BitConverter.DoubleToInt64Bits(action.CpuScore))
                {
                    throw new InvalidDataException(
                        $"Host Manager smart coordinator atomic group {action.AtomicGroupId} changed identity or size within one batch.");
                }
            }
            else
            {
                groups.Add(action.AtomicGroupId, shape);
            }

            if (!shape.MemberIndexes.Add(action.GroupMemberIndex))
            {
                throw new InvalidDataException(
                    $"Host Manager smart coordinator atomic group {action.AtomicGroupId} repeated member {action.GroupMemberIndex}.");
            }
            if (atomicGroupBySoftware.TryGetValue(action.SoftwareKey, out var softwareGroupId))
            {
                if (softwareGroupId != action.AtomicGroupId)
                {
                    throw new InvalidDataException(
                        $"Host Manager smart coordinator split software {action.SoftwareKey} across atomic groups.");
                }
            }
            else
            {
                atomicGroupBySoftware.Add(action.SoftwareKey, action.AtomicGroupId);
            }
        }

        foreach (var (groupId, group) in groups)
        {
            if (group.MemberIndexes.Count != group.MemberCount
                || Enumerable.Range(0, checked((int)group.MemberCount))
                    .Any(index => !group.MemberIndexes.Contains(checked((uint)index))))
            {
                throw new InvalidDataException(
                    $"Host Manager smart coordinator atomic group {groupId} is incomplete.");
            }
        }

        if (processActionCount > snapshot.ProcessCount
            || softwareActionCount > snapshot.SoftwareCount
            || processEntities.Count != processActionCount
            || softwareEntities.Count != softwareActionCount
            || checked(processActionCount + softwareActionCount) != workspace.PlannedActionCount
            || groups.Count > workspace.Capacity.AtomicGroupCapacity
            || requiredFeedbackCount > snapshot.InflightCount
            || requiredFeedbackCount > workspace.Capacity.FeedbackCapacity)
        {
            throw new InvalidDataException(
                "Host Manager smart coordinator action batch exceeds its native reservation capacity.");
        }

        return requiredFeedbackCount;
    }

    private static bool NativeActionComesBefore(
        NativeSmartCoordinatorAction left,
        NativeSmartCoordinatorAction right)
    {
        var leftPhase = GetNativeActionPhase(left);
        var rightPhase = GetNativeActionPhase(right);
        if (leftPhase != rightPhase)
        {
            return leftPhase < rightPhase;
        }
        if (leftPhase == 1 && left.CpuScore != right.CpuScore)
        {
            return left.CpuScore > right.CpuScore;
        }
        if (leftPhase is >= 2 and <= 5 && left.CpuScore != right.CpuScore)
        {
            return left.CpuScore < right.CpuScore;
        }
        if (left.AtomicGroupId != right.AtomicGroupId
            && (left.AtomicGroupId != 0 || right.AtomicGroupId != 0))
        {
            return left.AtomicGroupId < right.AtomicGroupId;
        }
        if (left.TargetKey != right.TargetKey)
        {
            return left.TargetKey < right.TargetKey;
        }
        if (left.ProcessStartKey != right.ProcessStartKey)
        {
            return left.ProcessStartKey < right.ProcessStartKey;
        }
        return left.ProcessId < right.ProcessId;
    }

    private static byte GetNativeActionPhase(NativeSmartCoordinatorAction action)
    {
        if (action.Disposition == NativeSmartCoordinatorActionDisposition.Restore)
        {
            return 0;
        }
        if (action.Disposition == NativeSmartCoordinatorActionDisposition.Apply
            && action.Scope == NativeSmartCoordinatorActionScope.ProcessPolicy)
        {
            return action.ToProcessGrade switch
            {
                NativeSmartCoordinatorProcessGrade.A1 => 1,
                NativeSmartCoordinatorProcessGrade.Level3 => 2,
                NativeSmartCoordinatorProcessGrade.Level2 => 3,
                NativeSmartCoordinatorProcessGrade.Level1 => 4,
                NativeSmartCoordinatorProcessGrade.Level4 => 5,
                _ => 6
            };
        }
        if (action.Disposition == NativeSmartCoordinatorActionDisposition.Apply)
        {
            return 6;
        }
        return action.Disposition == NativeSmartCoordinatorActionDisposition.Retain ? (byte)7 : (byte)8;
    }

    private static IEnumerable<NativeSmartCoordinatorGradeDomains> EnumerateActionDomains(
        NativeSmartCoordinatorGradeDomains domains)
    {
        if (domains.HasFlag(NativeSmartCoordinatorGradeDomains.Process))
        {
            yield return NativeSmartCoordinatorGradeDomains.Process;
        }
        if (domains.HasFlag(NativeSmartCoordinatorGradeDomains.Cpu))
        {
            yield return NativeSmartCoordinatorGradeDomains.Cpu;
        }
        if (domains.HasFlag(NativeSmartCoordinatorGradeDomains.Gpu))
        {
            yield return NativeSmartCoordinatorGradeDomains.Gpu;
        }
    }

    internal static unsafe void ValidateNativeAction(
        NativeSmartCoordinatorAction action,
        uint expectedOrder,
        NativeSmartCoordinatorSnapshot snapshot)
    {
        var reserved1IsZero = action.Reserved1[0] == 0 && action.Reserved1[1] == 0;
        if (action.StructSize != NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorAction>()
            || action.ActionId == 0
            || action.PlanEpoch == 0
            || action.PlanEpoch != snapshot.PlanEpoch
            || action.ConfigurationGeneration != snapshot.ConfigurationGeneration
            || action.OrderKey != expectedOrder
            || (action.ValidMask & ~NativeSmartCoordinatorActionValidity.Known) != 0
            || (action.Flags & ~NativeSmartCoordinatorActionFlags.Known) != 0
            || (action.DomainMask & ~NativeSmartCoordinatorGradeDomains.Known) != 0
            || !Enum.IsDefined(action.Scope)
            || !Enum.IsDefined(action.Disposition)
            || (action.ReasonMask & ~NativeSmartCoordinatorReason.Known) != 0
            || action.Reserved0 != 0
            || !reserved1IsZero
            || action.WakeAfterMilliseconds != snapshot.WakeAfterMilliseconds
            || (action.ValidMask.HasFlag(NativeSmartCoordinatorActionValidity.CpuScore)
                && (!double.IsFinite(action.CpuScore) || action.CpuScore < 0))
            || (action.ValidMask.HasFlag(NativeSmartCoordinatorActionValidity.GpuScore)
                && (!double.IsFinite(action.GpuScore) || action.GpuScore < 0)))
        {
            throw new InvalidDataException(
                $"Host Manager smart coordinator action {action.ActionId} failed its POD contract.");
        }

        var requiresFeedback = action.Flags.HasFlag(NativeSmartCoordinatorActionFlags.RequiresFeedback);
        var executableDisposition = action.Disposition is NativeSmartCoordinatorActionDisposition.Apply
            or NativeSmartCoordinatorActionDisposition.Restore;
        if (requiresFeedback != executableDisposition
            || (requiresFeedback
                && action.Scope == NativeSmartCoordinatorActionScope.ProcessPolicy
                && action.Disposition == NativeSmartCoordinatorActionDisposition.Apply
                && !action.ValidMask.HasFlag(NativeSmartCoordinatorActionValidity.CpuScore))
            || (!requiresFeedback
                && (action.Flags & (NativeSmartCoordinatorActionFlags.Compensation
                    | NativeSmartCoordinatorActionFlags.Retry)) != 0))
        {
            throw new InvalidDataException(
                $"Host Manager smart coordinator action {action.ActionId} has an inconsistent execution shape: scope={action.Scope}; disposition={action.Disposition}; flags={action.Flags}; valid={action.ValidMask}; domains={action.DomainMask}; cpuScore={action.CpuScore}; gpuScore={action.GpuScore}.");
        }

        var atomic = action.Flags.HasFlag(NativeSmartCoordinatorActionFlags.Atomic);
        var atomicValid = action.ValidMask.HasFlag(NativeSmartCoordinatorActionValidity.AtomicGroup);
        if (atomic != atomicValid
            || atomic != (action.AtomicGroupId != 0)
            || (atomic && (!requiresFeedback
                || action.Scope != NativeSmartCoordinatorActionScope.ProcessPolicy
                || action.Disposition != NativeSmartCoordinatorActionDisposition.Apply
                || action.DomainMask != NativeSmartCoordinatorGradeDomains.Process
                || action.ToProcessGrade != NativeSmartCoordinatorProcessGrade.Level4
                || action.SoftwareKey == 0
                || !action.ValidMask.HasFlag(
                    NativeSmartCoordinatorActionValidity.SoftwareIdentity)))
            || (atomic && (action.GroupMemberCount <= 1
                || action.GroupMemberIndex >= action.GroupMemberCount))
            || (!atomic && (action.GroupMemberIndex != 0 || action.GroupMemberCount != 0)))
        {
            throw new InvalidDataException(
                $"Host Manager smart coordinator action {action.ActionId} has an inconsistent atomic-group shape.");
        }

        if (action.Scope == NativeSmartCoordinatorActionScope.ProcessPolicy)
        {
            var required = NativeSmartCoordinatorActionValidity.ProcessIdentity
                | NativeSmartCoordinatorActionValidity.ProcessGrade;
            var allowed = required
                | NativeSmartCoordinatorActionValidity.SoftwareIdentity
                | NativeSmartCoordinatorActionValidity.CpuScore
                | NativeSmartCoordinatorActionValidity.AtomicGroup;
            if ((action.ValidMask & required) != required
                || (action.ValidMask & ~allowed) != 0
                || action.DomainMask != NativeSmartCoordinatorGradeDomains.Process
                || action.TargetKey == 0
                || action.ProcessId == 0
                || action.ProcessStartKey == 0
                || action.ValidMask.HasFlag(NativeSmartCoordinatorActionValidity.SoftwareIdentity)
                    != (action.SoftwareKey != 0)
                || !Enum.IsDefined(action.FromProcessGrade)
                || !Enum.IsDefined(action.ToProcessGrade)
                || action.FromCpuGrade != NativeSmartCoordinatorAdapterGrade.Normal
                || action.ToCpuGrade != NativeSmartCoordinatorAdapterGrade.Normal
                || action.FromGpuGrade != NativeSmartCoordinatorAdapterGrade.Normal
                || action.ToGpuGrade != NativeSmartCoordinatorAdapterGrade.Normal
                || (action.Disposition == NativeSmartCoordinatorActionDisposition.Restore
                    && action.ToProcessGrade != NativeSmartCoordinatorProcessGrade.Normal)
                || (action.Disposition == NativeSmartCoordinatorActionDisposition.Apply
                    && action.ToProcessGrade == NativeSmartCoordinatorProcessGrade.Normal))
            {
                throw new InvalidDataException(
                    $"Host Manager process action {action.ActionId} failed its identity or grade contract.");
            }
        }
        else
        {
            var allowed = NativeSmartCoordinatorActionValidity.SoftwareIdentity
                | NativeSmartCoordinatorActionValidity.CpuGrade
                | NativeSmartCoordinatorActionValidity.GpuGrade
                | NativeSmartCoordinatorActionValidity.CpuScore
                | NativeSmartCoordinatorActionValidity.GpuScore;
            if ((action.ValidMask & NativeSmartCoordinatorActionValidity.SoftwareIdentity) == 0
                || (action.ValidMask & ~allowed) != 0
                || action.TargetKey == 0
                || action.TargetKey != action.SoftwareKey
                || action.ProcessId != 0
                || action.ProcessStartKey != 0
                || action.FromProcessGrade != NativeSmartCoordinatorProcessGrade.Normal
                || action.ToProcessGrade != NativeSmartCoordinatorProcessGrade.Normal
                || (requiresFeedback && action.DomainMask == NativeSmartCoordinatorGradeDomains.None)
                || action.ValidMask.HasFlag(NativeSmartCoordinatorActionValidity.CpuGrade)
                    != action.DomainMask.HasFlag(NativeSmartCoordinatorGradeDomains.Cpu)
                || action.ValidMask.HasFlag(NativeSmartCoordinatorActionValidity.GpuGrade)
                    != action.DomainMask.HasFlag(NativeSmartCoordinatorGradeDomains.Gpu)
                || (action.ValidMask.HasFlag(NativeSmartCoordinatorActionValidity.CpuScore)
                    && !action.DomainMask.HasFlag(NativeSmartCoordinatorGradeDomains.Cpu))
                || (action.ValidMask.HasFlag(NativeSmartCoordinatorActionValidity.GpuScore)
                    && !action.DomainMask.HasFlag(NativeSmartCoordinatorGradeDomains.Gpu))
                || !Enum.IsDefined(action.FromCpuGrade)
                || !Enum.IsDefined(action.ToCpuGrade)
                || !Enum.IsDefined(action.FromGpuGrade)
                || !Enum.IsDefined(action.ToGpuGrade)
                || (action.Disposition == NativeSmartCoordinatorActionDisposition.Restore
                    && ((action.DomainMask.HasFlag(NativeSmartCoordinatorGradeDomains.Cpu)
                            && action.ToCpuGrade != NativeSmartCoordinatorAdapterGrade.Normal)
                        || (action.DomainMask.HasFlag(NativeSmartCoordinatorGradeDomains.Gpu)
                            && action.ToGpuGrade != NativeSmartCoordinatorAdapterGrade.Normal)))
                || (action.Disposition == NativeSmartCoordinatorActionDisposition.Apply
                    && ((action.DomainMask.HasFlag(NativeSmartCoordinatorGradeDomains.Cpu)
                            && action.ToCpuGrade != NativeSmartCoordinatorAdapterGrade.Normal
                            && !action.ValidMask.HasFlag(NativeSmartCoordinatorActionValidity.CpuScore))
                        || (action.DomainMask.HasFlag(NativeSmartCoordinatorGradeDomains.Gpu)
                            && action.ToGpuGrade != NativeSmartCoordinatorAdapterGrade.Normal
                            && !action.ValidMask.HasFlag(NativeSmartCoordinatorActionValidity.GpuScore))))
                || (action.Disposition == NativeSmartCoordinatorActionDisposition.Apply
                    && (!action.DomainMask.HasFlag(NativeSmartCoordinatorGradeDomains.Cpu)
                            || action.ToCpuGrade == NativeSmartCoordinatorAdapterGrade.Normal)
                    && (!action.DomainMask.HasFlag(NativeSmartCoordinatorGradeDomains.Gpu)
                            || action.ToGpuGrade == NativeSmartCoordinatorAdapterGrade.Normal)))
            {
                throw new InvalidDataException(
                    $"Host Manager adapter action {action.ActionId} failed its identity or grade contract.");
            }
        }
    }

    private readonly record struct NativeActionDomainIdentity(
        NativeSmartCoordinatorActionScope Scope,
        ulong TargetKey,
        uint ProcessId,
        ulong ProcessStartKey,
        NativeSmartCoordinatorGradeDomains Domain);

    private sealed record NativeAtomicGroupShape(
        uint MemberCount,
        ulong SoftwareKey,
        long ScoreBits,
        HashSet<uint> MemberIndexes);

    internal static unsafe void ValidateNativeFeedback(
        NativeSmartCoordinatorFeedback feedback,
        NativeSmartCoordinatorAction action,
        long reservationCreatedAtMilliseconds)
    {
        var reservedIsZero = feedback.Reserved0[0] == 0
            && feedback.Reserved0[1] == 0
            && feedback.Reserved0[2] == 0;
        var actualMask = feedback.ValidMask & (NativeSmartCoordinatorFeedbackValidity.ActualProcessGrade
            | NativeSmartCoordinatorFeedbackValidity.ActualCpuGrade
            | NativeSmartCoordinatorFeedbackValidity.ActualGpuGrade);
        var expectedActualMask = NativeSmartCoordinatorFeedbackValidity.None;
        if (action.DomainMask.HasFlag(NativeSmartCoordinatorGradeDomains.Process))
        {
            expectedActualMask |= NativeSmartCoordinatorFeedbackValidity.ActualProcessGrade;
        }
        if (action.DomainMask.HasFlag(NativeSmartCoordinatorGradeDomains.Cpu))
        {
            expectedActualMask |= NativeSmartCoordinatorFeedbackValidity.ActualCpuGrade;
        }
        if (action.DomainMask.HasFlag(NativeSmartCoordinatorGradeDomains.Gpu))
        {
            expectedActualMask |= NativeSmartCoordinatorFeedbackValidity.ActualGpuGrade;
        }

        if (feedback.StructSize != NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorFeedback>()
            || (feedback.Flags & ~NativeSmartCoordinatorFeedbackFlags.Known) != 0
            || (feedback.ValidMask & ~NativeSmartCoordinatorFeedbackValidity.Known) != 0
            || !feedback.ValidMask.HasFlag(NativeSmartCoordinatorFeedbackValidity.CompletedAt)
            || !Enum.IsDefined(feedback.Status)
            || !Enum.IsDefined(feedback.Scope)
            || feedback.ActionId != action.ActionId
            || feedback.PlanEpoch != action.PlanEpoch
            || feedback.ConfigurationGeneration != action.ConfigurationGeneration
            || feedback.Scope != action.Scope
            || feedback.TargetKey != action.TargetKey
            || feedback.SoftwareKey != action.SoftwareKey
            || feedback.ProcessId != action.ProcessId
            || feedback.ProcessStartKey != action.ProcessStartKey
            || feedback.CompletedAtMilliseconds < reservationCreatedAtMilliseconds
            || !reservedIsZero
            || feedback.Reserved1 != 0
            || (actualMask.HasFlag(NativeSmartCoordinatorFeedbackValidity.ActualProcessGrade)
                && !Enum.IsDefined(feedback.ActualProcessGrade))
            || (actualMask.HasFlag(NativeSmartCoordinatorFeedbackValidity.ActualCpuGrade)
                && !Enum.IsDefined(feedback.ActualCpuGrade))
            || (actualMask.HasFlag(NativeSmartCoordinatorFeedbackValidity.ActualGpuGrade)
                && !Enum.IsDefined(feedback.ActualGpuGrade))
            || (!actualMask.HasFlag(NativeSmartCoordinatorFeedbackValidity.ActualProcessGrade)
                && (int)feedback.ActualProcessGrade != 0)
            || (!actualMask.HasFlag(NativeSmartCoordinatorFeedbackValidity.ActualCpuGrade)
                && (int)feedback.ActualCpuGrade != 0)
            || (!actualMask.HasFlag(NativeSmartCoordinatorFeedbackValidity.ActualGpuGrade)
                && (int)feedback.ActualGpuGrade != 0))
        {
            throw new InvalidDataException(
                $"Host Manager smart coordinator feedback {feedback.ActionId} failed its POD or reservation identity contract.");
        }

        var ownershipFlags = feedback.Flags & (NativeSmartCoordinatorFeedbackFlags.ProcessOwned
            | NativeSmartCoordinatorFeedbackFlags.CpuOwned
            | NativeSmartCoordinatorFeedbackFlags.GpuOwned);
        var resultProofFlags = ownershipFlags |
            (feedback.Flags & NativeSmartCoordinatorFeedbackFlags.RollbackPayloadPersisted);
        switch (feedback.Status)
        {
            case NativeSmartCoordinatorFeedbackStatus.Succeeded:
                var expectedOwnership = NativeSmartCoordinatorFeedbackFlags.None;
                if (action.DomainMask.HasFlag(NativeSmartCoordinatorGradeDomains.Process)
                    && action.ToProcessGrade != NativeSmartCoordinatorProcessGrade.Normal)
                {
                    expectedOwnership |= NativeSmartCoordinatorFeedbackFlags.ProcessOwned;
                }
                if (action.DomainMask.HasFlag(NativeSmartCoordinatorGradeDomains.Cpu)
                    && action.ToCpuGrade != NativeSmartCoordinatorAdapterGrade.Normal)
                {
                    expectedOwnership |= NativeSmartCoordinatorFeedbackFlags.CpuOwned;
                }
                if (action.DomainMask.HasFlag(NativeSmartCoordinatorGradeDomains.Gpu)
                    && action.ToGpuGrade != NativeSmartCoordinatorAdapterGrade.Normal)
                {
                    expectedOwnership |= NativeSmartCoordinatorFeedbackFlags.GpuOwned;
                }
                if (actualMask != expectedActualMask
                    || (actualMask.HasFlag(NativeSmartCoordinatorFeedbackValidity.ActualProcessGrade)
                        && feedback.ActualProcessGrade != action.ToProcessGrade)
                    || (actualMask.HasFlag(NativeSmartCoordinatorFeedbackValidity.ActualCpuGrade)
                        && feedback.ActualCpuGrade != action.ToCpuGrade)
                    || (actualMask.HasFlag(NativeSmartCoordinatorFeedbackValidity.ActualGpuGrade)
                        && feedback.ActualGpuGrade != action.ToGpuGrade)
                    || ownershipFlags != expectedOwnership
                    || (expectedOwnership != NativeSmartCoordinatorFeedbackFlags.None
                        && !feedback.Flags.HasFlag(NativeSmartCoordinatorFeedbackFlags.RollbackPayloadPersisted)))
                {
                    throw new InvalidDataException(
                        $"Host Manager smart coordinator feedback {feedback.ActionId} has an invalid successful result.");
                }
                break;
            case NativeSmartCoordinatorFeedbackStatus.FailedUnchanged:
            case NativeSmartCoordinatorFeedbackStatus.Rejected:
                if ((actualMask & ~expectedActualMask) != 0
                    || (actualMask.HasFlag(NativeSmartCoordinatorFeedbackValidity.ActualProcessGrade)
                        && feedback.ActualProcessGrade != action.FromProcessGrade)
                    || (actualMask.HasFlag(NativeSmartCoordinatorFeedbackValidity.ActualCpuGrade)
                        && feedback.ActualCpuGrade != action.FromCpuGrade)
                    || (actualMask.HasFlag(NativeSmartCoordinatorFeedbackValidity.ActualGpuGrade)
                        && feedback.ActualGpuGrade != action.FromGpuGrade)
                    || resultProofFlags != NativeSmartCoordinatorFeedbackFlags.None)
                {
                    throw new InvalidDataException(
                        $"Host Manager smart coordinator feedback {feedback.ActionId} did not preserve the unchanged grade shape.");
                }
                break;
            case NativeSmartCoordinatorFeedbackStatus.Skipped:
            case NativeSmartCoordinatorFeedbackStatus.OwnershipLost:
            case NativeSmartCoordinatorFeedbackStatus.StateUncertain:
                if (actualMask != NativeSmartCoordinatorFeedbackValidity.None
                    || resultProofFlags != NativeSmartCoordinatorFeedbackFlags.None)
                {
                    throw new InvalidDataException(
                        $"Host Manager smart coordinator indeterminate feedback {feedback.ActionId} carried untrusted result proof.");
                }
                break;
            default:
                throw new InvalidDataException(
                    $"Host Manager smart coordinator feedback {feedback.ActionId} has an unknown status.");
        }
    }

    private static NativeSmartCoordinatorFeedback CreateNativeFeedback(
        NativeSmartCoordinatorAction action,
        NativeSmartCoordinatorFeedbackStatus status,
        DateTimeOffset completedAt,
        NativeSmartCoordinatorFeedbackFlags flags = NativeSmartCoordinatorFeedbackFlags.None,
        uint systemErrorCode = 0,
        NativeSmartCoordinatorProcessGrade? actualProcessGrade = null,
        NativeSmartCoordinatorAdapterGrade? actualCpuGrade = null,
        NativeSmartCoordinatorAdapterGrade? actualGpuGrade = null)
    {
        var validMask = NativeSmartCoordinatorFeedbackValidity.CompletedAt;
        if (actualProcessGrade.HasValue)
        {
            validMask |= NativeSmartCoordinatorFeedbackValidity.ActualProcessGrade;
        }
        if (actualCpuGrade.HasValue)
        {
            validMask |= NativeSmartCoordinatorFeedbackValidity.ActualCpuGrade;
        }
        if (actualGpuGrade.HasValue)
        {
            validMask |= NativeSmartCoordinatorFeedbackValidity.ActualGpuGrade;
        }

        return new NativeSmartCoordinatorFeedback
        {
            StructSize = checked((uint)NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorFeedback>()),
            Flags = flags,
            ValidMask = validMask,
            ActionId = action.ActionId,
            PlanEpoch = action.PlanEpoch,
            ConfigurationGeneration = action.ConfigurationGeneration,
            TargetKey = action.TargetKey,
            SoftwareKey = action.SoftwareKey,
            ProcessStartKey = action.ProcessStartKey,
            CompletedAtMilliseconds = completedAt.ToUnixTimeMilliseconds(),
            ProcessId = action.ProcessId,
            SystemErrorCode = systemErrorCode,
            Status = status,
            Scope = action.Scope,
            ActualProcessGrade = actualProcessGrade.GetValueOrDefault(),
            ActualCpuGrade = actualCpuGrade.GetValueOrDefault(),
            ActualGpuGrade = actualGpuGrade.GetValueOrDefault()
        };
    }
}
