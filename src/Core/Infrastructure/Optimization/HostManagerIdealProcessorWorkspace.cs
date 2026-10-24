using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Infrastructure.Optimization;

internal readonly record struct HostManagerIdealProcessorStableIdentity(
    string TargetId,
    ulong ProcessStartKey,
    int ThreadId,
    ulong ThreadStartKey);

internal sealed class HostManagerIdealProcessorStableIdentityComparer
    : IEqualityComparer<HostManagerIdealProcessorStableIdentity>
{
    internal static HostManagerIdealProcessorStableIdentityComparer Instance { get; } = new();

    private HostManagerIdealProcessorStableIdentityComparer()
    {
    }

    public bool Equals(
        HostManagerIdealProcessorStableIdentity left,
        HostManagerIdealProcessorStableIdentity right)
        => left.ProcessStartKey == right.ProcessStartKey
            && left.ThreadId == right.ThreadId
            && left.ThreadStartKey == right.ThreadStartKey
            && StringComparer.OrdinalIgnoreCase.Equals(left.TargetId, right.TargetId);

    public int GetHashCode(HostManagerIdealProcessorStableIdentity value)
        => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.TargetId),
            value.ProcessStartKey,
            value.ThreadId,
            value.ThreadStartKey);
}

internal sealed class HostManagerIdealProcessorWorkspace
{
    internal readonly List<HostManagerIdealProcessorInput> Inputs = [];
    internal readonly List<HostManagerIdealProcessorPlacement> Plans = [];
    internal readonly List<HostManagerPlacementDesired> Desired = [];
    internal readonly List<OrderedInput> OrderedInputs = [];
    internal readonly List<OrderedThread> OrderedThreads = [];
    internal readonly List<IdealCoreState> Cores = [];
    internal readonly List<CpuLogicalProcessorModel> CandidateProcessors = [];
    internal readonly HashSet<string> CandidateCoreIds = new(StringComparer.OrdinalIgnoreCase);
    internal readonly HashSet<string> ExcludedCoreIds = new(StringComparer.OrdinalIgnoreCase);
    internal readonly Dictionary<int, double> LogicalLoads = [];
    internal readonly Dictionary<string, ExistingIdealRecord> ExistingRecords = new(StringComparer.Ordinal);
    internal readonly Dictionary<HostManagerIdealProcessorStableIdentity, string> StableRecordIds =
        new(HostManagerIdealProcessorStableIdentityComparer.Instance);
    internal readonly HashSet<HostManagerIdealProcessorStableIdentity> CurrentStableIdentities =
        new(HostManagerIdealProcessorStableIdentityComparer.Instance);
    internal readonly List<HostManagerIdealProcessorStableIdentity> StaleStableIdentities = [];

    internal void ClearEphemeralReferences()
    {
        Inputs.Clear();
        Plans.Clear();
        Desired.Clear();
        OrderedInputs.Clear();
        OrderedThreads.Clear();
        Cores.Clear();
        CandidateProcessors.Clear();
        CandidateCoreIds.Clear();
        ExcludedCoreIds.Clear();
        LogicalLoads.Clear();
        ExistingRecords.Clear();
        CurrentStableIdentities.Clear();
        StaleStableIdentities.Clear();
    }

    internal void ResetCall() => ClearEphemeralReferences();

    internal void ResetPlannerScratch()
    {
        Plans.Clear();
        OrderedInputs.Clear();
        OrderedThreads.Clear();
        Cores.Clear();
        CandidateProcessors.Clear();
        CandidateCoreIds.Clear();
        ExcludedCoreIds.Clear();
        LogicalLoads.Clear();
    }

    internal void ReconcileStableRecordIds()
    {
        CurrentStableIdentities.Clear();
        foreach (var input in Inputs)
        {
            for (var threadIndex = 0; threadIndex < input.Threads.Count; threadIndex++)
            {
                var thread = input.Threads[threadIndex];
                if (thread.ProcessId != input.Process.ProcessId
                    || thread.ProcessStartKey != input.Process.ProcessStartKey
                    || thread.ThreadStartKey == 0)
                {
                    continue;
                }

                var identity = new HostManagerIdealProcessorStableIdentity(
                    input.Process.TargetId,
                    thread.ProcessStartKey,
                    thread.ThreadId,
                    thread.ThreadStartKey);
                CurrentStableIdentities.Add(identity);
                if (StableRecordIds.ContainsKey(identity))
                {
                    continue;
                }

                StableRecordIds.Add(identity, HostManagerSmartCoordinator.CreateStableId(
                    $"automatic-placement|ideal|{input.Process.TargetId}|{thread.ProcessStartKey}|{thread.ThreadId}|{thread.ThreadStartKey}"));
            }
        }

        StaleStableIdentities.Clear();
        foreach (var pair in StableRecordIds)
        {
            if (!CurrentStableIdentities.Contains(pair.Key))
            {
                StaleStableIdentities.Add(pair.Key);
            }
        }
        foreach (var identity in StaleStableIdentities)
        {
            StableRecordIds.Remove(identity);
        }
        StaleStableIdentities.Clear();
        CurrentStableIdentities.Clear();
    }

    internal bool TryGetStableRecordId(
        string targetId,
        ThreadIdealProcessorPolicySnapshot thread,
        out string recordId)
        => StableRecordIds.TryGetValue(new HostManagerIdealProcessorStableIdentity(
            targetId,
            thread.ProcessStartKey,
            thread.ThreadId,
            thread.ThreadStartKey), out recordId!);

    internal readonly record struct OrderedInput(HostManagerIdealProcessorInput Input, int OriginalIndex);
    internal readonly record struct OrderedThread(ThreadIdealProcessorPolicySnapshot Thread, int OriginalIndex);
    internal readonly record struct ExistingIdealRecord(
        HostManagerAppliedPlacementReceipt Placement,
        HostManagerAppliedRecord Record);

    internal struct IdealCoreState(CpuPhysicalCoreModel core, double capacity)
    {
        internal CpuPhysicalCoreModel Core = core;
        internal double Capacity = capacity;
        internal double AssignedScore;
    }

    internal sealed class OrderedInputComparer : IComparer<OrderedInput>
    {
        internal static OrderedInputComparer Instance { get; } = new();
        internal static readonly Comparison<OrderedInput> Comparison = Instance.Compare;

        private OrderedInputComparer()
        {
        }

        public int Compare(OrderedInput left, OrderedInput right)
        {
            var comparison = right.Input.Process.CanonicalCpuScore!.Value
                .CompareTo(left.Input.Process.CanonicalCpuScore!.Value);
            if (comparison != 0) return comparison;
            comparison = StringComparer.OrdinalIgnoreCase.Compare(
                left.Input.Process.TargetId,
                right.Input.Process.TargetId);
            if (comparison != 0) return comparison;
            comparison = left.Input.Process.ProcessId.CompareTo(right.Input.Process.ProcessId);
            return comparison != 0 ? comparison : left.OriginalIndex.CompareTo(right.OriginalIndex);
        }
    }

    internal sealed class OrderedThreadComparer : IComparer<OrderedThread>
    {
        internal static OrderedThreadComparer Instance { get; } = new();
        internal static readonly Comparison<OrderedThread> Comparison = Instance.Compare;

        private OrderedThreadComparer()
        {
        }

        public int Compare(OrderedThread left, OrderedThread right)
        {
            var comparison = left.Thread.ThreadId.CompareTo(right.Thread.ThreadId);
            return comparison != 0 ? comparison : left.OriginalIndex.CompareTo(right.OriginalIndex);
        }
    }
}
