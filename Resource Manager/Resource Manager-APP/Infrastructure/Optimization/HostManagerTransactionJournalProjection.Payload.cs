using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

internal static partial class HostManagerTransactionJournalProjection
{
    public static NativeTransactionJournalPayloadBinding CreatePayloadBinding(
        in NativeSmartCoordinatorAction action,
        ulong journalInstanceLow,
        ulong journalInstanceHigh,
        ulong hostSessionIncarnation,
        ulong preparedAtUtcMilliseconds,
        uint maximumRecoveryAttempts,
        ulong recoveryDeadlineUtcMilliseconds)
    {
        ValidateExecutableAction(in action);
        if ((journalInstanceLow == 0 && journalInstanceHigh == 0) ||
            hostSessionIncarnation == 0 ||
            preparedAtUtcMilliseconds == 0 ||
            maximumRecoveryAttempts == 0 ||
            recoveryDeadlineUtcMilliseconds <= preparedAtUtcMilliseconds)
        {
            throw new ArgumentException("The rollback payload binding identity or recovery budget is invalid.");
        }

        var projected = ProjectAction(in action, hostSessionIncarnation);
        return new NativeTransactionJournalPayloadBinding(
            journalInstanceLow,
            journalInstanceHigh,
            projected.Identity,
            projected.Scope,
            projected.Disposition,
            projected.Domain,
            projected.GradeValidMask,
            projected.ProcessFromGrade,
            projected.ProcessToGrade,
            projected.CpuFromGrade,
            projected.CpuToGrade,
            projected.GpuFromGrade,
            projected.GpuToGrade,
            StableSystemStatus: 0,
            StableSystemError: 0,
            maximumRecoveryAttempts,
            recoveryDeadlineUtcMilliseconds,
            projected.AtomicGroupId,
            projected.GroupMemberIndex,
            projected.GroupMemberCount);
    }

    private static ProjectedJournalAction ProjectAction(
        in NativeSmartCoordinatorAction action,
        ulong hostSessionIncarnation)
    {
        if (hostSessionIncarnation == 0)
        {
            throw new ArgumentException("The Host Manager session incarnation must be nonzero.");
        }

        var scope = MapScope(action.Scope);
        return new ProjectedJournalAction(
            new NativeTransactionJournalIdentity
            {
                ConfigurationGeneration = action.ConfigurationGeneration,
                PlanEpoch = action.PlanEpoch,
                ActionId = action.ActionId,
                HostSessionIncarnation = hostSessionIncarnation,
                TargetId = action.TargetKey,
                SoftwareId = action.SoftwareKey,
                ProcessStartKey = scope == NativeTransactionJournalScope.Process
                    ? action.ProcessStartKey
                    : 0,
                ProcessId = scope == NativeTransactionJournalScope.Process
                    ? action.ProcessId
                    : 0
            },
            scope,
            MapDisposition(action.Disposition),
            MapDomains(action.DomainMask),
            MapGradeValidity(action.Scope, action.DomainMask),
            action.Scope == NativeSmartCoordinatorActionScope.ProcessPolicy
                ? (int)MapProcessGrade(action.FromProcessGrade)
                : 0,
            action.Scope == NativeSmartCoordinatorActionScope.ProcessPolicy
                ? (int)MapProcessGrade(action.ToProcessGrade)
                : 0,
            action.DomainMask.HasFlag(NativeSmartCoordinatorGradeDomains.Cpu)
                ? (int)MapAdapterGrade(action.FromCpuGrade)
                : 0,
            action.DomainMask.HasFlag(NativeSmartCoordinatorGradeDomains.Cpu)
                ? (int)MapAdapterGrade(action.ToCpuGrade)
                : 0,
            action.DomainMask.HasFlag(NativeSmartCoordinatorGradeDomains.Gpu)
                ? (int)MapAdapterGrade(action.FromGpuGrade)
                : 0,
            action.DomainMask.HasFlag(NativeSmartCoordinatorGradeDomains.Gpu)
                ? (int)MapAdapterGrade(action.ToGpuGrade)
                : 0,
            action.AtomicGroupId,
            action.GroupMemberIndex,
            action.GroupMemberCount);
    }

    private readonly record struct ProjectedJournalAction(
        NativeTransactionJournalIdentity Identity,
        NativeTransactionJournalScope Scope,
        NativeTransactionJournalDisposition Disposition,
        NativeTransactionJournalDomain Domain,
        NativeTransactionJournalGradeValidity GradeValidMask,
        int ProcessFromGrade,
        int ProcessToGrade,
        int CpuFromGrade,
        int CpuToGrade,
        int GpuFromGrade,
        int GpuToGrade,
        ulong AtomicGroupId,
        uint GroupMemberIndex,
        uint GroupMemberCount);
}
