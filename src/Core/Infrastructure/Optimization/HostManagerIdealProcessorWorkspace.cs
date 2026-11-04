using ResourceManager.App.Domain.RuntimeSpecialization;
using System.Runtime.InteropServices;
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
    private static readonly IdealProcessorWorkspaceOptions Trim = OptimizationSettings.ProductDefaults.IdealProcessorWorkspace;
    private int lowUsageCalls;

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
    internal readonly Dictionary<HostManagerIdealProcessorStableIdentity, StableRecordId> StableRecordIds =
        new(HostManagerIdealProcessorStableIdentityComparer.Instance);
    internal readonly List<HostManagerIdealProcessorStableIdentity> StaleStableIdentities = [];

    internal void ClearEphemeralReferences()
    {
        TrimAfterSustainedLowUsage();
        ClearReferences();
    }

    private void ClearReferences()
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
        StaleStableIdentities.Clear();
    }

    internal void ResetCall() => ClearReferences();

    private void TrimAfterSustainedLowUsage()
    {
        var liveCount = Math.Max(StableRecordIds.Count, Math.Max(Inputs.Count, ExistingRecords.Count));
        liveCount = Math.Max(liveCount, Math.Max(Plans.Count, Desired.Count));
        foreach (var input in Inputs)
        {
            liveCount = Math.Max(liveCount, input.Threads.Count);
        }
        var capacity = Math.Max(StableRecordIds.EnsureCapacity(0), ExistingRecords.EnsureCapacity(0));
        capacity = Math.Max(capacity, Math.Max(Inputs.Capacity, OrderedInputs.Capacity));
        capacity = Math.Max(capacity, Math.Max(Plans.Capacity, Desired.Capacity));
        capacity = Math.Max(capacity, Math.Max(OrderedThreads.Capacity, StaleStableIdentities.Capacity));
        if (capacity <= Trim.TrimMinimumCapacity || liveCount > capacity / Trim.TrimUsageDivisor)
        {
            lowUsageCalls = 0;
            return;
        }
        if (++lowUsageCalls < Trim.LowUsageCallsBeforeTrim) return;

        // Leave growth headroom, and count completed calls rather than the empty reset at call entry.
        var targetCapacity = Math.Max(Trim.TrimmedMinimumCapacity, liveCount * Trim.TrimmedHeadroomFactor);
        if (StableRecordIds.EnsureCapacity(0) > targetCapacity) StableRecordIds.TrimExcess(targetCapacity);
        if (ExistingRecords.EnsureCapacity(0) > targetCapacity) ExistingRecords.TrimExcess(targetCapacity);
        TrimList(Inputs, targetCapacity);
        TrimList(Plans, targetCapacity);
        TrimList(Desired, targetCapacity);
        TrimList(OrderedInputs, targetCapacity);
        TrimList(OrderedThreads, targetCapacity);
        TrimList(StaleStableIdentities, targetCapacity);
        lowUsageCalls = 0;
    }

    private static void TrimList<T>(List<T> list, int targetCapacity)
    {
        if (list.Capacity > targetCapacity) list.Capacity = Math.Max(list.Count, targetCapacity);
    }

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
                ref var record = ref CollectionsMarshal.GetValueRefOrAddDefault(StableRecordIds, identity, out var exists);
                if (!exists)
                {
                    record.RecordId = HostManagerSmartCoordinator.CreateStableId(
                        $"automatic-placement|ideal|{input.Process.TargetId}|{thread.ProcessStartKey}|{thread.ThreadId}|{thread.ThreadStartKey}");
                }
                record.IsCurrent = true;
            }
        }

        StaleStableIdentities.Clear();
        foreach (var pair in StableRecordIds)
        {
            if (!pair.Value.IsCurrent)
            {
                StaleStableIdentities.Add(pair.Key);
            }
            else
            {
                CollectionsMarshal.GetValueRefOrNullRef(StableRecordIds, pair.Key).IsCurrent = false;
            }
        }
        foreach (var identity in StaleStableIdentities)
        {
            StableRecordIds.Remove(identity);
        }
        StaleStableIdentities.Clear();
    }

    internal bool TryGetStableRecordId(
        string targetId,
        ThreadIdealProcessorPolicySnapshot thread,
        out string recordId)
    {
        if (StableRecordIds.TryGetValue(new HostManagerIdealProcessorStableIdentity(
            targetId,
            thread.ProcessStartKey,
            thread.ThreadId,
            thread.ThreadStartKey), out var record))
        {
            recordId = record.RecordId;
            return true;
        }
        recordId = null!;
        return false;
    }

    internal struct StableRecordId
    {
        internal string RecordId;
        internal bool IsCurrent;
    }

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
