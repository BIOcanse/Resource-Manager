using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native.MetricOwner;
using ResourceManager.ReadWrite.Vendors.Amd;
using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.ReadWrite.Vendors.Intel;

namespace ResourceManager.ReadWrite.Windows.Metrics.Zig.Adapters;

public sealed partial class MetricOwnerSdkSampler
{
    [RuntimeRead(ReadVolatilityKind.Sampled)]
    private async ValueTask<uint> ReadNvmlAsync(SourceRows rows, MetricOwnerRawRound raw, MetricDiscoveryRoundFacts facts, CancellationToken token)
    {
        var inventory = topology.GpuInventory ?? throw Contract();
        var gpuRows = rows.Filter(static (r, row) => r.Scope(row).NvmlFan is null);
        bool H(params ReadOnlySpan<MetricFieldCode> codes) => Has(gpuRows, facts, codes);
        IReadOnlyList<GpuMetrics> values = [];
        if (gpuRows.Count != 0)
        {
            using var call = gpuRows.BeginCall(2);
            var indexes = SdkIndexes(call);
            values = await resources.ReadNvmlAsync(new(indexes,
                H(MetricFieldCode.GpuUsage), H(MetricFieldCode.GpuGraphicsClock, MetricFieldCode.GpuGraphicsClockPercent),
                H(MetricFieldCode.GpuMemoryUsed, MetricFieldCode.GpuMemoryTotal, MetricFieldCode.GpuMemoryPercent),
                H(MetricFieldCode.GpuMemoryClock), H(MetricFieldCode.GpuPower), H(MetricFieldCode.GpuPowerLimit),
                H(MetricFieldCode.GpuTemperature), H(MetricFieldCode.GpuFanPercent), false)
            { AdditionalMetrics = SdkCodes(call, NvidiaNvmlReader.IsAdditionalMetric) }, inventory, token).ConfigureAwait(false);
        }
        var fanRows = rows.Filter(static (r, row) => r.Scope(row).NvmlFan is not null);
        IReadOnlyList<NvidiaNvmlFanReading> fans = [];
        if (fanRows.Count != 0)
        {
            using var call = fanRows.BeginCall();
            var fanTargets = SdkScopes(call, scope => new NvidiaNvmlFanReadTarget(scope.NvmlFan!,
                Has(call.ForKey(scope.DeviceKey), call.Borrow.Facts, MetricFieldCode.NvmlFanSpeedPercentOfMax),
                Has(call.ForKey(scope.DeviceKey), call.Borrow.Facts, MetricFieldCode.NvmlFanSpeedRpm)));
            fans = await resources.ReadNvmlFansAsync(fanTargets, inventory, token).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
        var byIndex = SdkResultMap(gpuRows, values, static x => x.Index);
        var byFan = SdkResultMap(fanRows, fans, x => SdkIdentityKey(fanRows, x.Fan, static scope => scope.NvmlFan, static fan => fan.ExactIdentityKey), StringComparer.Ordinal);
        var at = Now();
        Each(rows, row =>
        {
            if (Scope(facts, row).NvmlFan is { } identity)
            {
                Number(raw, row, byFan.TryRow(row, out var fan) ? Field(facts, row) switch
                { MetricFieldCode.NvmlFanSpeedPercentOfMax => fan.PercentOfMaxNoiseTolerance, MetricFieldCode.NvmlFanSpeedRpm => fan.IntendedRpm, _ => null } : null, at);
            }
            else WriteGpu(raw, facts, row, byIndex.GetRow(row), at);
        });
        return values.Count == 0 && fans.Count == 0 ? Unavailable : Complete;
    }

    [RuntimeRead(ReadVolatilityKind.Sampled)]
    private async ValueTask<uint> ReadNvapiAsync(SourceRows rows, MetricOwnerRawRound raw, MetricDiscoveryRoundFacts facts, CancellationToken token)
    {
        using var call = rows.BeginCall(2);
        var indexes = SdkIndexes(call);
        var values = await resources.ReadNvapiAsync(new(indexes, Has(rows, facts, MetricFieldCode.GpuFanRpm),
            Has(rows, facts, MetricFieldCode.GpuVoltage), Has(rows, facts, MetricFieldCode.GpuCurrent)),
            topology.GpuInventory ?? throw Contract(), token).ConfigureAwait(false);
        call.Dispose();
        token.ThrowIfCancellationRequested(); var byIndex = SdkResultMap(rows, values, static x => x.DisplayIndex); var at = Now();
        Each(rows, row => Number(raw, row, byIndex.TryRow(row, out var value) ? Field(facts, row) switch
        { MetricFieldCode.GpuFanRpm => value.Sensors.FanSpeedRpm, MetricFieldCode.GpuVoltage => value.Sensors.CoreVoltageVolts,
            MetricFieldCode.GpuCurrent => value.Sensors.CurrentAmps, _ => null } : null, at));
        return values.Count == 0 ? Unavailable : Complete;
    }

    private AmdAdlxReadRequest AdlxRequest(SourceRows rows, MetricDiscoveryRoundFacts facts)
    {
        bool H(params ReadOnlySpan<MetricFieldCode> codes) => Has(rows, facts, codes);
        return new(SdkIndexes(rows),
            H(MetricFieldCode.GpuUsage, MetricFieldCode.AdlxActivity),
            H(MetricFieldCode.GpuGraphicsClock, MetricFieldCode.GpuGraphicsClockPercent),
            H(MetricFieldCode.GpuMemoryUsed, MetricFieldCode.GpuMemoryTotal, MetricFieldCode.GpuMemoryPercent, MetricFieldCode.AdlxDedicatedMemoryMb),
            H(MetricFieldCode.GpuMemoryClock), H(MetricFieldCode.GpuPower), H(MetricFieldCode.GpuBoardPower),
            H(MetricFieldCode.GpuTemperature), H(MetricFieldCode.GpuHotspotTemperature), H(MetricFieldCode.GpuIntakeTemperature),
            H(MetricFieldCode.GpuFanRpm), H(MetricFieldCode.GpuVoltage, MetricFieldCode.AdlxVoltage), H(MetricFieldCode.GpuMemoryTemperature),
            H(MetricFieldCode.AdlxSampleTimestamp), H(MetricFieldCode.AdlxNpuActivityPercent), H(MetricFieldCode.AdlxNpuFrequencyMhz));
    }

    [RuntimeRead(ReadVolatilityKind.Sampled)]
    private async ValueTask<uint> ReadAdlxAsync(SourceRows rows, MetricOwnerRawRound raw, MetricDiscoveryRoundFacts facts, CancellationToken token)
    {
        using var call = rows.BeginCall(2);
        var request = AdlxRequest(call, facts) with
        {
            // Required by the actual SDK: each device receives just its requested telemetry flags.
            NativeFlagsByDisplayIndex = SdkMapView(call, SdkIndexes(call),
                index => AmdAdlxBridgeReader.BuildNativeFlags(AdlxRequest(call.ForIndex(index), call.Borrow.Facts)))
        };
        var values = await resources.ReadAdlxAsync(request, topology.GpuInventory ?? throw Contract(), token).ConfigureAwait(false);
        call.Dispose();
        token.ThrowIfCancellationRequested(); var byIndex = SdkResultMap(rows, values, static x => x.Index); var at = Now();
        Each(rows, row => WriteGpu(raw, facts, row, byIndex.GetRow(row), at));
        return values.Count == 0 ? Unavailable : Complete;
    }

    private static void WriteGpu(MetricOwnerRawRound raw, MetricDiscoveryRoundFacts facts, int row, GpuMetrics? value, long at)
    {
        if (value is null) { Missing(raw, row); return; }
        var code = Field(facts, row);
        if (value.AdditionalObservedAt.TryGetValue(code, out var timestamp)) at = timestamp.ToUnixTimeMilliseconds();
        if (raw.ValueKind(row) == MetricOwnerValueKind.Unsigned64)
        {
            ulong? v = value.AdditionalIntegerReadings.TryGetValue(code, out var exact) ? exact : code switch
            { MetricFieldCode.GpuMemoryUsed => value.UsedMemoryBytes, MetricFieldCode.GpuMemoryTotal => value.TotalMemoryBytes, _ => null };
            Integer(raw, row, v, at); return;
        }
        double? number;
        if (!value.AdditionalReadings.TryGetValue(code, out number)) number = code switch
        {
            MetricFieldCode.GpuUsage or MetricFieldCode.AdlxActivity => value.IsUsageAvailable ? value.UsagePercent : null,
            MetricFieldCode.GpuGraphicsClock => value.GraphicsFrequencyMhz,
            MetricFieldCode.GpuGraphicsClockPercent => value.GraphicsFrequencyPercent,
            MetricFieldCode.GpuMemoryUsed => value.UsedMemoryBytes, MetricFieldCode.GpuMemoryTotal => value.TotalMemoryBytes,
            MetricFieldCode.GpuMemoryPercent => value.MemoryUsagePercent, MetricFieldCode.GpuMemoryClock => value.MemoryFrequencyMhz,
            MetricFieldCode.GpuPower => value.Sensors.PowerWatts, MetricFieldCode.GpuPowerLimit => value.Sensors.PowerLimitWatts,
            MetricFieldCode.GpuBoardPower => value.Sensors.BoardPowerWatts, MetricFieldCode.GpuTemperature => value.Sensors.TemperatureCelsius,
            MetricFieldCode.GpuHotspotTemperature => value.Sensors.HotspotTemperatureCelsius,
            MetricFieldCode.GpuIntakeTemperature => value.Sensors.IntakeTemperatureCelsius,
            MetricFieldCode.GpuFanPercent => value.Sensors.FanSpeedPercent, MetricFieldCode.GpuFanRpm => value.Sensors.FanSpeedRpm,
            MetricFieldCode.GpuVoltage or MetricFieldCode.AdlxVoltage => value.Sensors.CoreVoltageVolts,
            MetricFieldCode.GpuCurrent => value.Sensors.CurrentAmps, _ => null
        };
        Number(raw, row, number, at);
    }

    [RuntimeRead(ReadVolatilityKind.Sampled)]
    private uint ReadIgcl(SourceRows rows, MetricOwnerRawRound raw, MetricDiscoveryRoundFacts facts, CancellationToken token)
    {
        var inventory = topology.GpuInventory ?? throw Contract();
        var gpuRows = rows.Filter(static (r, row) => r.Scope(row).IgclFan is null);
        var fanRows = rows.Filter(static (r, row) => r.Scope(row).IgclFan is not null);
        IReadOnlyList<IntelGclMetricRead> values = [];
        if (gpuRows.Count != 0)
        {
            using var call = gpuRows.BeginCall(2);
            values = resources.ReadIntelGpu(inventory, ByIndex(call, facts), token);
        }
        IReadOnlyList<IntelGclFanReading> fans = [];
        if (fanRows.Count != 0)
        {
            using var call = fanRows.BeginCall();
            fans = resources.ReadIgclFans(SdkScopes(call, static x => x.IgclFan!), inventory, topology.IgclFanMaximumInstanceCount, token);
        }
        token.ThrowIfCancellationRequested(); var byIndex = SdkResultMap(gpuRows, values, static x => x.Index);
        var byFan = SdkResultMap(fanRows, fans, x => SdkIdentityKey(fanRows, x.Fan, static scope => scope.IgclFan, static fan => fan.ExactIdentityKey), StringComparer.Ordinal); var at = Now();
        Each(rows, row =>
        {
            var scope = Scope(facts, row); var code = Field(facts, row);
            if (scope.IgclFan is { } identity)
            {
                Number(raw, row, byFan.TryRow(row, out var fan) && ReferenceEquals(identity, fan.Fan) ? fan.Rpm : null, at); return;
            }
            if (!byIndex.TryRow(row, out var value)) { Missing(raw, row); return; }
            if (value.FloatingCounters.TryGetValue(code, out var pair) && pair is { } p)
                raw.WriteCounter(row, 4, BitConverter.DoubleToUInt64Bits(p.Primary), BitConverter.DoubleToUInt64Bits(p.Reference), 0, 0, at);
            else if (raw.ValueKind(row) == MetricOwnerValueKind.Unsigned64) Integer(raw, row, value.Counters.GetValueOrDefault(code), at);
            else if (raw.ValueKind(row) == MetricOwnerValueKind.Boolean) Boolean(raw, row, value.Flags.GetValueOrDefault(code), at);
            else Number(raw, row, value.Values.GetValueOrDefault(code), at);
        });
        return values.Count == 0 && fans.Count == 0 ? Unavailable : Complete;
    }
}
