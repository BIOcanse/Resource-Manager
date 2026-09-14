namespace ResourceManager.Adapter.SharedMemory;

public sealed class DefaultHostPublicResourceManager
{
    private readonly NativeSharedResourceSession session;
    private readonly HostPublicResourceManagerOptions options;
    private readonly object sync = new();
    private ulong sampleGeneration;

    public DefaultHostPublicResourceManager(
        NativeSharedResourceSession session,
        HostPublicResourceManagerOptions? options = null)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.options = options ?? new HostPublicResourceManagerOptions();
    }

    public ulong SampleGeneration
    {
        get
        {
            lock (sync)
            {
                return sampleGeneration;
            }
        }
    }

    public HostPublicResourceManagementPlan Tick(
        HostPublicResourceCapacityShortage shortage)
    {
        lock (sync)
        {
            var nextGeneration = checked(sampleGeneration + 1);
            var unloadPlan = session.PlanHostPublicResourceUnloads(
                nextGeneration,
                shortage,
                options);
            var resources = session.ListResources(out var topologyGeneration);
            if (topologyGeneration != unloadPlan.TopologyGeneration)
            {
                throw new InvalidOperationException(
                    "The public-resource topology changed while recall candidates were being captured.");
            }

            var unloadCandidates = FilterUnloadCandidates(
                unloadPlan.Candidates,
                resources,
                shortage);
            var zeroSubscriberUnloadCount = unloadCandidates.Count(static candidate =>
                candidate.Reason == HostPublicResourceUnloadReason.ZeroSubscribers);
            var capacityUnloadCount = unloadCandidates.Length - zeroSubscriberUnloadCount;

            var recallCandidates = session.SupportsHostPublicRecallTransactions
                ? resources
                    .Where(resource => IsRecallCandidate(resource, shortage))
                .Select(resource => new HostPublicResourceRecallCandidate(
                    resource.Id,
                    resource.SchedulingRevision,
                    resource.GateEpoch,
                    resource.PayloadGeneration,
                    resource.AdapterKey,
                    RetentionScore(
                        checked((uint)resource.ActiveSubscriberCount),
                        resource.ActivityScore),
                    resource.ActiveSubscriberCount,
                    resource.ActivityScore,
                    resource.Tier,
                    resource.Flags))
                .ToArray()
                : [];
            Array.Sort(recallCandidates, CompareRecallCandidates);
            sampleGeneration = nextGeneration;
            return new HostPublicResourceManagementPlan(
                unloadPlan.TopologyGeneration,
                unloadPlan.SampleGeneration,
                unloadPlan.ResourceCount,
                unloadPlan.ProtectedResourceCount,
                zeroSubscriberUnloadCount,
                capacityUnloadCount,
                unloadCandidates,
                recallCandidates);
        }
    }

    private static HostPublicResourceUnloadCandidate[] FilterUnloadCandidates(
        IReadOnlyList<HostPublicResourceUnloadCandidate> candidates,
        IReadOnlyList<SharedResourceSnapshot> resources,
        HostPublicResourceCapacityShortage shortage)
    {
        var resourcesById = resources.ToDictionary(static resource => resource.Id);
        return candidates
            .Where(candidate =>
                candidate.Reason != HostPublicResourceUnloadReason.CapacityShortage
                || IsCapacityUnloadAllowed(candidate, resourcesById, shortage))
            .ToArray();
    }

    private static bool IsCapacityUnloadAllowed(
        HostPublicResourceUnloadCandidate candidate,
        IReadOnlyDictionary<SharedResourceId, SharedResourceSnapshot> resources,
        HostPublicResourceCapacityShortage shortage)
    {
        if (!resources.TryGetValue(candidate.ResourceId, out var resource)
            || resource.Flags != candidate.Flags)
        {
            return false;
        }

        var gpuBacked = resource.Flags.HasFlag(SharedResourceFlags.GpuBacked);
        return gpuBacked
            ? resource.AdapterKey != 0
                && shortage.VideoMemoryForAdapter(resource.AdapterKey)
                    .AfterNormalReleaseRounds
            : shortage.MemoryAfterNormalReleaseRounds;
    }

    private static bool IsRecallCandidate(
        SharedResourceSnapshot resource,
        HostPublicResourceCapacityShortage shortage)
    {
        if (!resource.ConsistencyStable
            || resource.DestructiveActionActive
            || resource.ProtectedUseCount != 0
            || resource.ActiveSubscriberCount <= 0
            || resource.SizeBytes != 0
            || resource.PayloadMappingId != 0
            || resource.Availability != SharedResourceAvailability.Unavailable)
        {
            return false;
        }

        var gpuBacked = resource.Flags.HasFlag(SharedResourceFlags.GpuBacked);
        return gpuBacked
            ? resource.AdapterKey != 0
                && shortage.VideoMemoryForAdapter(resource.AdapterKey).RecallAllowed
            : shortage.Memory.RecallAllowed;
    }

    private uint RetentionScore(uint subscribers, byte activityScore)
    {
        var subscriberQ16 = SaturationQ16(
            subscribers,
            options.SubscriberHalfSaturation);
        var activityQ16 = SaturationQ16(
            activityScore,
            options.ActivityHalfSaturation);
        var weighted = (UInt128)subscriberQ16 * options.SubscriberWeight
            + (UInt128)activityQ16 * options.ActivityWeight;
        return checked((uint)(weighted / options.WeightScale));
    }

    private static uint SaturationQ16(uint value, uint halfSaturation)
        => checked((uint)(
            ((ulong)value * ushort.MaxValue)
            / ((ulong)value + halfSaturation)));

    private static int CompareRecallCandidates(
        HostPublicResourceRecallCandidate left,
        HostPublicResourceRecallCandidate right)
    {
        var leftGpu = left.Flags.HasFlag(SharedResourceFlags.GpuBacked);
        var rightGpu = right.Flags.HasFlag(SharedResourceFlags.GpuBacked);
        var comparison = leftGpu.CompareTo(rightGpu);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = right.RetentionScoreQ16.CompareTo(
            left.RetentionScoreQ16);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = left.ResourceId.PublicResourceId.CompareTo(
            right.ResourceId.PublicResourceId);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = left.ResourceId.ResourceGeneration.CompareTo(
            right.ResourceId.ResourceGeneration);
        return comparison != 0
            ? comparison
            : left.ResourceId.ResourceSlot.CompareTo(
                right.ResourceId.ResourceSlot);
    }
}
