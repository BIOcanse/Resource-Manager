using System.Collections.Immutable;
using System.Security.Cryptography;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal readonly record struct NativeMetricSnapshotSourceObservationIdentity(
    ulong SourceHandle,
    ulong SourceIncarnation,
    ulong SourceObservationSequence);

internal sealed class NativeMetricSnapshotSourceRuntimeFacts
{
    private static readonly TimeSpan TransientUnavailableDuration =
        TimeSpan.FromSeconds(5);

    private readonly object gate = new();
    private readonly IMonitoringSourceZoneRegistry registry;
    private readonly Dictionary<ulong, SourceState> states = [];
    private ulong nextIncarnation = CreateIncarnationSeed();

    internal NativeMetricSnapshotSourceRuntimeFacts(
        IMonitoringSourceZoneRegistry registry)
    {
        this.registry = registry;
    }

    internal ImmutableArray<NativeMetricSnapshotSourceRuntimeMode>
        CaptureModes(
            NativeMetricSnapshotCatalogProjection catalog,
            DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        lock (gate)
        {
            var output = ImmutableArray.CreateBuilder<
                NativeMetricSnapshotSourceRuntimeMode>(
                catalog.Sources.Length);
            var activeHandles = new HashSet<ulong>();
            foreach (var source in catalog.Sources)
            {
                activeHandles.Add(source.SourceHandle);
                var sourceId = catalog.SourceIds[source.SourceHandle];
                var zone = registry.GetZone(
                    NativeMetricSnapshotSourceCatalog.ResolveZoneId(sourceId));
                var mode = zone.CurrentMode;
                var state = GetOrCreate(source.SourceHandle, mode);
                if (state.LastMode != mode)
                {
                    state.LastMode = mode;
                    state.CapabilityGeneration = Next(
                        state.CapabilityGeneration,
                        "source capability generation");
                    state.UnavailableUntil = null;
                }
                var availability =
                    mode == ResourceManagerComputeZoneMode.Freeze
                    || state.UnavailableUntil is { } unavailableUntil
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

    internal NativeMetricSnapshotSourceObservationIdentity BeginObservation(
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

    internal void RecordCompletion(
        ulong sourceHandle,
        NativeMetricSnapshotSourceStatus status,
        DateTimeOffset completedAt)
    {
        lock (gate)
        {
            var state = states.GetValueOrDefault(sourceHandle)
                ?? throw new InvalidOperationException(
                    "The source runtime fact does not exist.");
            state.UnavailableUntil = status
                == NativeMetricSnapshotSourceStatus.Unavailable
                    ? completedAt + TransientUnavailableDuration
                    : null;
        }
    }

    private SourceState GetOrCreate(
        ulong sourceHandle,
        ResourceManagerComputeZoneMode mode)
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
        ResourceManagerComputeZoneMode mode)
        => mode switch
        {
            ResourceManagerComputeZoneMode.Normal =>
                NativeMetricSnapshotZoneMode.Normal,
            ResourceManagerComputeZoneMode.LowPower =>
                NativeMetricSnapshotZoneMode.LowPower,
            ResourceManagerComputeZoneMode.Freeze =>
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
        ResourceManagerComputeZoneMode lastMode)
    {
        internal ulong SourceIncarnation { get; } = sourceIncarnation;

        internal ulong CapabilityGeneration { get; set; } = 1;

        internal ulong ObservationSequence { get; set; }

        internal ResourceManagerComputeZoneMode LastMode { get; set; } =
            lastMode;

        internal DateTimeOffset? UnavailableUntil { get; set; }
    }
}
