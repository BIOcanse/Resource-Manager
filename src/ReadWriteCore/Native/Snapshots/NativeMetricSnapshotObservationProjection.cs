using ResourceManager.ReadWrite.Windows.Gpu;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Native;

namespace ResourceManager.App.Infrastructure.Monitoring;





public readonly record struct NativeMetricSnapshotTimedCounter(ulong Counter, ulong Ticks, ulong TicksPerSecond);

public static class NativeMetricSnapshotObservationProjection
{
    public static NativeMetricSnapshotObservationInput CreateFloatingCounterPairObservation(
        NativeMetricSnapshotMetricPlanOutput metric, NativeMetricSnapshotRuleCatalogBinding binding,
        NativeMetricSnapshotSourceObservationIdentity source, DateTimeOffset observedAt, NativeMetricSnapshotFloatingCounterPair? pair,
        NativeMetricSnapshotObservationStatus unavailableStatus = NativeMetricSnapshotObservationStatus.Unavailable)
    {
        if (binding.MetricKind is not ((uint)NativeMetricSnapshotMetricKind.FloatingCounterRatio or (uint)NativeMetricSnapshotMetricKind.FloatingCounterPercentage)
            || binding.ValueKind != (uint)NativeMetricSnapshotValueKind.Float64)
            throw new InvalidOperationException("Floating counter pair requires a floating-counter rule.");
        var observation = CreateObservation(metric, binding, source, observedAt, null, unavailableStatus);
        observation.Flags = (uint)NativeMetricSnapshotObservationFlags.FloatingCounterPair;
        if (pair is not { } raw || !double.IsFinite(raw.Primary) || raw.Primary < 0
            || !double.IsFinite(raw.Reference) || raw.Reference < 0) return observation;
        observation.ValueBits = BitConverter.DoubleToUInt64Bits(raw.Primary);
        observation.ValueHighBits = BitConverter.DoubleToUInt64Bits(raw.Reference);
        observation.ValidMask |= (ulong)NativeMetricSnapshotObservationValidity.Value;
        observation.Status = (uint)NativeMetricSnapshotObservationStatus.Current;
        return observation;
    }

    public static NativeMetricSnapshotObservationInput CreateObservation(
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

    public static NativeMetricSnapshotObservationInput CreateTimedCounterObservation(
        NativeMetricSnapshotMetricPlanOutput metric, NativeMetricSnapshotRuleCatalogBinding binding,
        NativeMetricSnapshotSourceObservationIdentity source, DateTimeOffset observedAt, NativeMetricSnapshotTimedCounter? counter,
        NativeMetricSnapshotObservationStatus unavailableStatus = NativeMetricSnapshotObservationStatus.Unavailable)
    {
        if (binding.MetricKind is not ((uint)NativeMetricSnapshotMetricKind.TimedCounterRate or (uint)NativeMetricSnapshotMetricKind.TimedMicrosecondCounterPercentage) ||
            binding.ValueKind != (uint)NativeMetricSnapshotValueKind.Float64)
            throw new InvalidOperationException("Timed counter requires a timed-counter rule.");
        var observation = CreateObservation(metric, binding, source, observedAt, null, unavailableStatus);
        observation.Flags = (uint)NativeMetricSnapshotObservationFlags.TimedCounter;
        if (counter is not { TicksPerSecond: > 0 } raw) return observation;
        observation.ValueBits = raw.Counter;
        observation.ValueHighBits = raw.Ticks;
        observation.CounterTicksPerSecond = raw.TicksPerSecond;
        observation.ValidMask |= (ulong)NativeMetricSnapshotObservationValidity.Value;
        observation.Status = (uint)NativeMetricSnapshotObservationStatus.Current;
        return observation;
    }

    public static NativeMetricSnapshotObservationInput CreateCounterPairObservation(
        NativeMetricSnapshotMetricPlanOutput metric, NativeMetricSnapshotRuleCatalogBinding binding,
        NativeMetricSnapshotSourceObservationIdentity source, DateTimeOffset observedAt, NativeMetricSnapshotCounterPair? pair,
        NativeMetricSnapshotObservationStatus unavailableStatus = NativeMetricSnapshotObservationStatus.Unavailable)
    {
        var isFrequency = binding.MetricKind == (uint)NativeMetricSnapshotMetricKind.CounterFrequency;
        if (binding.MetricKind is not ((uint)NativeMetricSnapshotMetricKind.CounterRatio or (uint)NativeMetricSnapshotMetricKind.CounterFrequency) ||
            binding.ValueKind != (uint)NativeMetricSnapshotValueKind.Float64)
            throw new InvalidOperationException("Counter pair requires a counter-ratio or counter-frequency rule.");
        var observation = CreateObservation(metric, binding, source, observedAt, null, unavailableStatus);
        observation.Flags = (uint)NativeMetricSnapshotObservationFlags.CounterPair;
        if (pair is not { } raw || (isFrequency && raw.ReferenceFrequencyMhz == 0)) return observation;
        observation.ValueBits = raw.Primary;
        observation.ValueHighBits = raw.Reference;
        observation.ReservedU32 = isFrequency ? (uint)raw.ReferenceFrequencyMhz : 0;
        observation.ValidMask |= (ulong)NativeMetricSnapshotObservationValidity.Value;
        observation.Status = (uint)NativeMetricSnapshotObservationStatus.Current;
        return observation;
    }

    public static NativeMetricSnapshotObservationInput CreateBooleanObservation(
        NativeMetricSnapshotMetricPlanOutput metric, NativeMetricSnapshotRuleCatalogBinding binding,
        NativeMetricSnapshotSourceObservationIdentity source, DateTimeOffset observedAt, bool? value)
    {
        var observation = CreateObservation(metric, binding, source, observedAt, null);
        if (binding.ValueKind != (uint)NativeMetricSnapshotValueKind.Boolean
            || binding.MinimumValueBits != 0 || binding.MaximumValueBits != 1)
            throw new InvalidOperationException("Boolean observation requires a boolean field with the full false/true domain.");
        if (value is not { } flag) return observation;
        observation.ValueBits = flag ? 1UL : 0UL;
        observation.ValidMask |= (ulong)NativeMetricSnapshotObservationValidity.Value;
        observation.Status = (uint)NativeMetricSnapshotObservationStatus.Current;
        return observation;
    }

    public static NativeMetricSnapshotObservationInput CreateUnsignedObservation(
        NativeMetricSnapshotMetricPlanOutput metric, NativeMetricSnapshotRuleCatalogBinding binding,
        NativeMetricSnapshotSourceObservationIdentity source, DateTimeOffset observedAt, ulong? value)
    {
        var observation = CreateObservation(metric, binding, source, observedAt, null);
        if (binding.ValueKind != (uint)NativeMetricSnapshotValueKind.Unsigned64)
            throw new InvalidOperationException("Unsigned observation requires an unsigned field.");
        if (value is not { } raw || raw < binding.MinimumValueBits || raw > binding.MaximumValueBits)
            return observation;
        observation.ValueBits = raw;
        observation.ValidMask |= (ulong)NativeMetricSnapshotObservationValidity.Value;
        observation.Status = (uint)NativeMetricSnapshotObservationStatus.Current;
        return observation;
    }

    public static NativeMetricSnapshotObservationInput CreateWideUnsignedObservation(
        NativeMetricSnapshotMetricPlanOutput metric, NativeMetricSnapshotRuleCatalogBinding binding,
        NativeMetricSnapshotSourceObservationIdentity source, DateTimeOffset observedAt, UInt128? value)
    {
        var observation = CreateObservation(metric, binding, source, observedAt, null);
        if (binding.ValueKind != (uint)NativeMetricSnapshotValueKind.Unsigned128)
            throw new InvalidOperationException("Wide unsigned observation requires an unsigned128 field.");
        if (value is not { } raw) return observation;
        observation.ValueBits = (ulong)(raw & ulong.MaxValue);
        observation.ValueHighBits = (ulong)(raw >> 64);
        observation.ValidMask |= (ulong)NativeMetricSnapshotObservationValidity.Value;
        observation.Status = (uint)NativeMetricSnapshotObservationStatus.Current;
        return observation;
    }

    public static unsafe NativeMetricSnapshotObservationInput CreateBytesObservation(
        NativeMetricSnapshotMetricPlanOutput metric, NativeMetricSnapshotRuleCatalogBinding binding,
        NativeMetricSnapshotSourceObservationIdentity source, DateTimeOffset observedAt, byte[]? value)
    {
        var observation = CreateObservation(metric, binding, source, observedAt, null);
        if (binding.ValueKind != (uint)NativeMetricSnapshotValueKind.Bytes)
            throw new InvalidOperationException("Bytes observation requires a bytes field.");
        if (value is null || value.Length < (long)binding.MinimumValueBits
            || value.Length > (long)binding.MaximumValueBits || value.Length > NativeMetricSnapshotAbi.MaximumValueBytes)
            return observation;
        value.AsSpan().CopyTo(new Span<byte>(observation.ValueBytes, NativeMetricSnapshotAbi.MaximumValueBytes));
        observation.ByteCount = checked((uint)value.Length);
        observation.ValidMask |= (ulong)NativeMetricSnapshotObservationValidity.Value;
        observation.Status = (uint)NativeMetricSnapshotObservationStatus.Current;
        return observation;
    }

    public static NativeMetricSnapshotCpuCounterInput CreateCpuCounter(
        NativeMetricSnapshotMetricPlanOutput metric,
        NativeMetricSnapshotRuleCatalogBinding binding,
        NativeMetricSnapshotSourceObservationIdentity source,
        NativeMetricSnapshotCpuCounterObservation counter)
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

    public static ImmutableArray<NativeMetricSnapshotGpuInventoryInput>
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
            var luid = WindowsGpuAdapterIdentity.Pack(adapter.Luid);
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
