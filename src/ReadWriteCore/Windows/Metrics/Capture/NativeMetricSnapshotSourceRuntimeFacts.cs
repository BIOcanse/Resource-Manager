using System.Collections.Immutable;
using System.Security.Cryptography;

using ResourceManager.ReadWrite.Native;


namespace ResourceManager.App.Infrastructure.Monitoring;

// Passive source observation identity is supplied by the independent read/write SDK.

public sealed class NativeMetricSnapshotSourceRuntimeFacts
{
    private readonly TimeSpan transientUnavailableDuration;

    private readonly object gate = new();
    private readonly Dictionary<ulong, SourceState> states = [];
    private ulong nextIncarnation = CreateIncarnationSeed();
    private ulong synchronizedWorkspaceIdentity;
    private bool hasSynchronizedWorkspace;

    public NativeMetricSnapshotSourceRuntimeFacts(TimeSpan transientUnavailableDuration)
    {
        if (transientUnavailableDuration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(transientUnavailableDuration));
        this.transientUnavailableDuration = transientUnavailableDuration;
    }

    public ImmutableArray<NativeMetricSnapshotSourceRuntimeState> CaptureStates(
        NativeMetricSnapshotCatalogProjection catalog, DateTimeOffset now)
        => CaptureStatesCore(catalog, now);

    public bool NeedsWorkspaceSourceSynchronization(ulong workspaceIdentity)
    {
        lock (gate)
        {
            return !hasSynchronizedWorkspace
                || synchronizedWorkspaceIdentity != workspaceIdentity;
        }
    }

    public void SynchronizeWorkspaceSources(
        ulong workspaceIdentity,
        NativeMetricSnapshotCatalogProjection catalog,
        IReadOnlyCollection<NativeMetricSnapshotSourceOutput> committedSources)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(committedSources);
        lock (gate)
        {
            if (hasSynchronizedWorkspace
                && synchronizedWorkspaceIdentity == workspaceIdentity)
            {
                return;
            }

            var committedByHandle = committedSources
                .ToDictionary(static source => source.SourceHandle);
            states.Clear();
            foreach (var source in catalog.Sources)
            {
                var state = GetOrCreate(source.SourceHandle);
                if (!committedByHandle.TryGetValue(
                        source.SourceHandle,
                        out var committed)
                    || committed.CapabilityGeneration == 0)
                {
                    continue;
                }

                state.CapabilityGeneration = committed.CapabilityGeneration;
                state.IsUnsupportedForCapabilityGeneration =
                    committed.Status
                    == (uint)NativeMetricSnapshotSourceStatus.Unsupported;
            }

            synchronizedWorkspaceIdentity = workspaceIdentity;
            hasSynchronizedWorkspace = true;
        }
    }

    private ImmutableArray<NativeMetricSnapshotSourceRuntimeState> CaptureStatesCore(
        NativeMetricSnapshotCatalogProjection catalog, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        lock (gate)
        {
            var output = ImmutableArray.CreateBuilder<NativeMetricSnapshotSourceRuntimeState>(catalog.Sources.Length);
            var activeHandles = new HashSet<ulong>();
            foreach (var source in catalog.Sources)
            {
                activeHandles.Add(source.SourceHandle);
                var state = GetOrCreate(source.SourceHandle);
                var availability = state.IsUnsupportedForCapabilityGeneration
                    ? NativeMetricSnapshotSourceAvailability.Unsupported
                    : state.UnavailableUntil is { } until && until > now
                        ? NativeMetricSnapshotSourceAvailability.Unavailable
                        : NativeMetricSnapshotSourceAvailability.Available;
                output.Add(new(source.SourceHandle, state.CapabilityGeneration, availability));
            }
            foreach (var retired in states.Keys.Where(handle => !activeHandles.Contains(handle)).ToArray())
                states.Remove(retired);
            return output.MoveToImmutable();
        }
    }

    public NativeMetricSnapshotSourceObservationIdentity BeginObservation(
        ulong sourceHandle)
    {
        lock (gate)
        {
            if (!states.TryGetValue(sourceHandle, out var state))
            {
                throw new InvalidOperationException(
                    "The source runtime fact was not captured before observation.");
            }
            state.ObservationSequence = Next(
                state.ObservationSequence,
                "source observation sequence");
            return new NativeMetricSnapshotSourceObservationIdentity(
                sourceHandle,
                state.SourceIncarnation,
                state.ObservationSequence);
        }
    }

    public void RecordCompletion(
        ulong sourceHandle,
        NativeMetricSnapshotSourceStatus status,
        DateTimeOffset completedAt,
        bool applyUnavailableCooldown = true)
    {
        lock (gate)
        {
            var state = states.GetValueOrDefault(sourceHandle)
                ?? throw new InvalidOperationException(
                    "The source runtime fact does not exist.");
            if (status == NativeMetricSnapshotSourceStatus.Unsupported)
            {
                state.IsUnsupportedForCapabilityGeneration = true;
            }
            state.UnavailableUntil = status
                == NativeMetricSnapshotSourceStatus.Unavailable
                    && applyUnavailableCooldown
                    ? completedAt + transientUnavailableDuration
                    : null;
        }
    }

    private SourceState GetOrCreate(
        ulong sourceHandle)
    {
        if (states.TryGetValue(sourceHandle, out var existing))
        {
            return existing;
        }
        var created = new SourceState(
            Next(ref nextIncarnation, "source incarnation"));
        states.Add(sourceHandle, created);
        return created;
    }

    private static ulong CreateIncarnationSeed()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        RandomNumberGenerator.Fill(bytes);
        var value = BitConverter.ToUInt64(bytes);
        return value is 0 or ulong.MaxValue ? 1UL : value;
    }

    private static ulong Next(ref ulong value, string name)
    {
        value = Next(value, name);
        return value;
    }

    private static ulong Next(ulong value, string name)
        => value < ulong.MaxValue
            ? value + 1
            : throw new InvalidOperationException($"{name} exhausted.");

    private sealed class SourceState(
        ulong sourceIncarnation)
    {
        internal ulong SourceIncarnation { get; } = sourceIncarnation;

        internal ulong CapabilityGeneration { get; set; } = 1;

        internal ulong ObservationSequence { get; set; }

        internal DateTimeOffset? UnavailableUntil { get; set; }

        internal bool IsUnsupportedForCapabilityGeneration { get; set; }
    }
}


