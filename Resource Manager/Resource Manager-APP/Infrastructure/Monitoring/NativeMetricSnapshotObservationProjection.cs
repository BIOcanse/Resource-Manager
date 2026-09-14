using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal static class NativeMetricSnapshotObservationProjection
{
    internal static NativeMetricSnapshotObservationInput CreateObservation(
        NativeMetricSnapshotMetricPlanOutput metric,
        NativeMetricSnapshotRuleCatalogBinding binding,
        NativeMetricSnapshotSourceObservationIdentity source,
        DateTimeOffset observedAt,
        double? value,
        NativeMetricSnapshotObservationStatus unavailableStatus =
            NativeMetricSnapshotObservationStatus.Unavailable,
        uint quality = 100,
        ulong sampleDurationMilliseconds = 0)
    {
        if (metric.RuleHandle != binding.RuleHandle
            || metric.MetricHandle != binding.MetricHandle
            || metric.SourceHandle != binding.SourceHandle
            || metric.ScopeHandle != binding.ScopeHandle
            || source.SourceHandle != binding.SourceHandle)
        {
            throw new InvalidOperationException(
                "The observation does not match the selected native rule.");
        }
        var valueBits = 0UL;
        var isCurrent = value is { } number
            && TryEncode(binding, number, out valueBits);
        return new NativeMetricSnapshotObservationInput
        {
            StructSize =
                SizeOf<NativeMetricSnapshotObservationInput>(),
            Flags = 0,
            RuleHandle = metric.RuleHandle,
            MetricHandle = metric.MetricHandle,
            SourceHandle = metric.SourceHandle,
            ScopeHandle = metric.ScopeHandle,
            SourceObservationSequence =
                source.SourceObservationSequence,
            ObservedAtMilliseconds = Timestamp(observedAt),
            ValueBits = isCurrent ? valueBits : 0,
            CapabilityMask = binding.CapabilityMask,
            ValidMask = isCurrent
                ? (ulong)(
                    NativeMetricSnapshotObservationValidity.Required
                    | NativeMetricSnapshotObservationValidity.Value)
                : (ulong)NativeMetricSnapshotObservationValidity.Required,
            Status = isCurrent
                ? (uint)NativeMetricSnapshotObservationStatus.Current
                : (uint)unavailableStatus,
            ValueKind = metric.ValueKind,
            Quality = quality,
            ReservedU32 = 0,
            SampleDurationMilliseconds = isCurrent
                ? sampleDurationMilliseconds
                : 0
        };
    }

    internal static NativeMetricSnapshotCpuCounterInput CreateCpuCounter(
        NativeMetricSnapshotMetricPlanOutput metric,
        NativeMetricSnapshotRuleCatalogBinding binding,
        NativeMetricSnapshotSourceObservationIdentity source,
        WindowsCpuMonitoringZone.CpuCounterObservation counter)
    {
        if (!counter.IsAvailable
            || metric.RuleHandle != binding.RuleHandle
            || metric.MetricHandle != binding.MetricHandle
            || metric.SourceHandle != binding.SourceHandle
            || source.SourceHandle != binding.SourceHandle
            || metric.MetricKind
                != (uint)NativeMetricSnapshotMetricKind.CpuUsage)
        {
            throw new InvalidOperationException(
                "The CPU counter does not match the selected native rule.");
        }
        return new NativeMetricSnapshotCpuCounterInput
        {
            StructSize =
                SizeOf<NativeMetricSnapshotCpuCounterInput>(),
            Flags = 0,
            RuleHandle = metric.RuleHandle,
            MetricHandle = metric.MetricHandle,
            SourceHandle = metric.SourceHandle,
            SourceIncarnation = source.SourceIncarnation,
            SourceObservationSequence =
                source.SourceObservationSequence,
            ObservedAtMilliseconds = Timestamp(counter.ObservedAt),
            MonotonicTicks = counter.MonotonicTicks,
            MonotonicTicksPerSecond =
                counter.MonotonicTicksPerSecond,
            IdleTicks = counter.IdleTicks,
            KernelTicks = counter.KernelTicks,
            UserTicks = counter.UserTicks,
            CapabilityMask = binding.CapabilityMask,
            ValidMask =
                (ulong)NativeMetricSnapshotCpuCounterValidity.Required,
            Status =
                (uint)NativeMetricSnapshotObservationStatus.Current,
            CounterContractVersion =
                NativeMetricSnapshotAbi.CpuCounterContractVersion
        };
    }

    internal static ImmutableArray<NativeMetricSnapshotGpuInventoryInput>
        CreateGpuInventory(
            NativeMetricSnapshotCatalogProjection catalog,
            NativeMetricSnapshotSourceObservationIdentity source,
            WindowsGpuAdapterInventoryRead inventory,
            ulong sourceCapabilityMask,
            DateTimeOffset capturedAt)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (inventory.Status != SamplingObservationStatus.Current
            || inventory.Generation == 0
            || inventory.ObservedCount != inventory.Adapters.Count
            || inventory.SkippedCount != 0
            || inventory.OverflowCount != 0)
        {
            return [];
        }
        var byLuid = catalog.GpuScopeBindings.Values.ToDictionary(
            static binding => binding.AdapterLuid);
        var rows = ImmutableArray.CreateBuilder<
            NativeMetricSnapshotGpuInventoryInput>(
                inventory.Adapters.Count);
        foreach (var adapter in inventory.Adapters)
        {
            var luid = NativePdhAdapterIdentity.Pack(adapter.Luid);
            if (!byLuid.TryGetValue(luid, out var binding)
                || binding.InventoryGeneration != inventory.Generation)
            {
                throw new InvalidOperationException(
                    "The live GPU inventory differs from the active native catalog.");
            }
            rows.Add(new NativeMetricSnapshotGpuInventoryInput
            {
                StructSize =
                    SizeOf<NativeMetricSnapshotGpuInventoryInput>(),
                Flags = 0,
                AdapterHandle = binding.ScopeHandle,
                SourceHandle = source.SourceHandle,
                SourceObservationSequence =
                    source.SourceObservationSequence,
                ObservedAtMilliseconds = Timestamp(capturedAt),
                AdapterLuidLow = luid,
                AdapterLuidHigh = 0,
                StableKeyHandle = binding.ScopeHandle,
                CapabilityMask = sourceCapabilityMask,
                TopologyFingerprint = inventory.TopologyFingerprint,
                ValidMask =
                    (ulong)NativeMetricSnapshotInventoryValidity.Required,
                Status =
                    (uint)NativeMetricSnapshotInventoryStatus.Current,
                ReservedU32 = 0
            });
        }
        return rows
            .OrderBy(static row => row.AdapterHandle)
            .ToImmutableArray();
    }

    private static bool TryEncode(
        NativeMetricSnapshotRuleCatalogBinding binding,
        double value,
        out ulong valueBits)
    {
        valueBits = 0;
        if (!double.IsFinite(value))
        {
            return false;
        }

        var flags =
            (NativeMetricSnapshotMetricFlags)binding.MetricFlags;
        switch ((NativeMetricSnapshotValueKind)binding.ValueKind)
        {
            case NativeMetricSnapshotValueKind.Float64:
                {
                    var minimum = BitConverter.Int64BitsToDouble(
                        unchecked((long)binding.MinimumValueBits));
                    var maximum = BitConverter.Int64BitsToDouble(
                        unchecked((long)binding.MaximumValueBits));
                    if (value < minimum
                        || value > maximum
                        || (flags & NativeMetricSnapshotMetricFlags.Nonnegative)
                            != 0
                            && value < 0
                        || (flags & NativeMetricSnapshotMetricFlags.Percentage)
                            != 0
                            && (value < 0 || value > 100))
                    {
                        return false;
                    }
                    valueBits = BitConverter.DoubleToUInt64Bits(value);
                    return true;
                }
            case NativeMetricSnapshotValueKind.Signed64:
                {
                    long encoded;
                    try
                    {
                        encoded = checked((long)Math.Round(value));
                    }
                    catch (OverflowException)
                    {
                        return false;
                    }
                    var minimum = unchecked((long)binding.MinimumValueBits);
                    var maximum = unchecked((long)binding.MaximumValueBits);
                    if (encoded < minimum
                        || encoded > maximum
                        || (flags & NativeMetricSnapshotMetricFlags.Nonnegative)
                            != 0
                            && encoded < 0)
                    {
                        return false;
                    }
                    valueBits = unchecked((ulong)encoded);
                    return true;
                }
            case NativeMetricSnapshotValueKind.Unsigned64:
                {
                    ulong encoded;
                    try
                    {
                        encoded = checked((ulong)Math.Round(value));
                    }
                    catch (OverflowException)
                    {
                        return false;
                    }
                    if (encoded < binding.MinimumValueBits
                        || encoded > binding.MaximumValueBits)
                    {
                        return false;
                    }
                    valueBits = encoded;
                    return true;
                }
            default:
                return false;
        }
    }

    private static ulong Timestamp(DateTimeOffset value)
        => checked((ulong)value.ToUnixTimeMilliseconds());

    private static uint SizeOf<T>()
        where T : unmanaged
        => checked((uint)Unsafe.SizeOf<T>());
}
