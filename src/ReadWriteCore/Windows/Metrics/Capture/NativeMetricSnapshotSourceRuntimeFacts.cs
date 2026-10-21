using System.Collections.Immutable;
using System.Security.Cryptography;

using ResourceManager.ReadWrite.Native;


namespace ResourceManager.App.Infrastructure.Monitoring;

// Passive source observation identity is supplied by the independent read/write SDK.

public sealed class NativeMetricSnapshotSourceRuntimeFacts
{
    private readonly TimeSpan transientUnavailableDuration;

    private readonly object gate = new();
    private readonly Func<string, NativeMetricSnapshotZoneMode> modeProvider;
    private readonly Dictionary<ulong, SourceState> states = [];
    private ulong nextIncarnation = CreateIncarnationSeed();
    private ulong synchronizedWorkspaceIdentity;
    private bool hasSynchronizedWorkspace;

    public NativeMetricSnapshotSourceRuntimeFacts(Func<string, NativeMetricSnapshotZoneMode> modeProvider, TimeSpan transientUnavailableDuration)
    {
        this.modeProvider = modeProvider ?? throw new ArgumentNullException(nameof(modeProvider));
        if (transientUnavailableDuration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(transientUnavailableDuration));
        this.transientUnavailableDuration = transientUnavailableDuration;
    }

    public ImmutableArray<NativeMetricSnapshotSourceRuntimeMode>
        CaptureModes(
            NativeMetricSnapshotCatalogProjection catalog,
            DateTimeOffset now)
        => CaptureModesCore(catalog, now, requestedMetricHandles: null,
            includeGpuInventory: true);

    /// <summary>
    /// Captures zone state only for sources that can participate in this
    /// request. The native planner still requires one mode record per source,
    /// so unrelated sources retain their last known mode (or Normal before
    /// their first selected request).
    /// </summary>
    public ImmutableArray<NativeMetricSnapshotSourceRuntimeMode>
        CaptureModesForRequest(
            NativeMetricSnapshotCatalogProjection catalog,
            DateTimeOffset now,
            IReadOnlyCollection<ulong> requestedMetricHandles,
            bool includeGpuInventory)
    {
        ArgumentNullException.ThrowIfNull(requestedMetricHandles);
        return CaptureModesCore(catalog, now, requestedMetricHandles,
            includeGpuInventory);
    }

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
                var state = GetOrCreate(
                    source.SourceHandle,
                    NativeMetricSnapshotZoneMode.Normal);
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

    private ImmutableArray<NativeMetricSnapshotSourceRuntimeMode>
        CaptureModesCore(
            NativeMetricSnapshotCatalogProjection catalog,
            DateTimeOffset now,
            IReadOnlyCollection<ulong>? requestedMetricHandles,
            bool includeGpuInventory)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        lock (gate)
        {
            HashSet<ulong>? requestedSources = null;
            if (requestedMetricHandles is not null)
            {
                var requestedMetrics = requestedMetricHandles.ToHashSet();
                requestedSources = catalog.RuleBindings.Values
                    .Where(rule => requestedMetrics.Contains(rule.MetricHandle))
                    .Select(static rule => rule.SourceHandle)
                    .ToHashSet();
                if (includeGpuInventory)
                {
                    requestedSources.UnionWith(catalog.Sources
                        .Where(static source => source.SourceRole
                            == (uint)NativeMetricSnapshotSourceRole.GpuInventory)
                        .Select(static source => source.SourceHandle));
                }
            }

            var output = ImmutableArray.CreateBuilder<
                NativeMetricSnapshotSourceRuntimeMode>(
                catalog.Sources.Length);
            var activeHandles = new HashSet<ulong>();
            foreach (var source in catalog.Sources)
            {
                activeHandles.Add(source.SourceHandle);
                var sourceId = catalog.SourceIds[source.SourceHandle];
                var readMode = requestedSources is null
                    || requestedSources.Contains(source.SourceHandle);
                var state = states.GetValueOrDefault(source.SourceHandle);
                if (readMode)
                {
                    var selectedMode = modeProvider(sourceId);
                    state ??= GetOrCreate(source.SourceHandle, selectedMode);
                    if (!state.HasObservedMode)
                    {
                        // The committed source output persists the capability
                        // generation and status, but not the prior zone mode.
                        // Establish the first observed mode as a baseline so
                        // restart cannot be mistaken for a mode transition.
                        state.LastMode = selectedMode;
                        state.HasObservedMode = true;
                    }
                    else if (state.LastMode != selectedMode)
                    {
                        state.LastMode = selectedMode;
                        state.CapabilityGeneration = Next(
                            state.CapabilityGeneration,
                            "source capability generation");
                        state.UnavailableUntil = null;
                        state.IsUnsupportedForCapabilityGeneration = false;
                    }
                }
                else
                {
                    state ??= GetOrCreate(source.SourceHandle,
                        NativeMetricSnapshotZoneMode.Normal);
                }

                var mode = state.LastMode;
                var availability =
                    mode == NativeMetricSnapshotZoneMode.Freeze
                        ? NativeMetricSnapshotSourceAvailability.Unavailable
                        : state.IsUnsupportedForCapabilityGeneration
                            ? NativeMetricSnapshotSourceAvailability.Unsupported
                            : state.UnavailableUntil is { } unavailableUntil
                                && unavailableUntil > now
                                ? NativeMetricSnapshotSourceAvailability.Unavailable
                                : NativeMetricSnapshotSourceAvailability.Available;
                output.Add(new NativeMetricSnapshotSourceRuntimeMode(
                    source.SourceHandle,
                    state.CapabilityGeneration,
                    ToNativeMode(mode),
                    availability));
            }
            foreach (var retired in states.Keys
                         .Where(handle => !activeHandles.Contains(handle))
                         .ToArray())
            {
                states.Remove(retired);
            }
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
        DateTimeOffset completedAt)
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
                    ? completedAt + transientUnavailableDuration
                    : null;
        }
    }

    private SourceState GetOrCreate(
        ulong sourceHandle,
        NativeMetricSnapshotZoneMode mode)
    {
        if (states.TryGetValue(sourceHandle, out var existing))
        {
            return existing;
        }
        var created = new SourceState(
            Next(ref nextIncarnation, "source incarnation"),
            mode);
        states.Add(sourceHandle, created);
        return created;
    }

    private static NativeMetricSnapshotZoneMode ToNativeMode(
        NativeMetricSnapshotZoneMode mode)
        => mode switch
        {
            NativeMetricSnapshotZoneMode.Normal =>
                NativeMetricSnapshotZoneMode.Normal,
            NativeMetricSnapshotZoneMode.LowPower =>
                NativeMetricSnapshotZoneMode.LowPower,
            NativeMetricSnapshotZoneMode.Freeze =>
                NativeMetricSnapshotZoneMode.Freeze,
            _ => throw new InvalidOperationException(
                "The monitoring source zone mode is invalid.")
        };

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
        ulong sourceIncarnation,
        NativeMetricSnapshotZoneMode lastMode)
    {
        internal ulong SourceIncarnation { get; } = sourceIncarnation;

        internal ulong CapabilityGeneration { get; set; } = 1;

        internal ulong ObservationSequence { get; set; }

        internal NativeMetricSnapshotZoneMode LastMode { get; set; } =
            lastMode;

        internal bool HasObservedMode { get; set; }

        internal DateTimeOffset? UnavailableUntil { get; set; }

        internal bool IsUnsupportedForCapabilityGeneration { get; set; }
    }
}


