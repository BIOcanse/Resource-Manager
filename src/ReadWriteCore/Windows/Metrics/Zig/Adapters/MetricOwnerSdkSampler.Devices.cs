using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native.MetricOwner;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Npu;

namespace ResourceManager.ReadWrite.Windows.Metrics.Zig.Adapters;

public sealed partial class MetricOwnerSdkSampler
{
    [RuntimeRead(ReadVolatilityKind.Sampled)]
    private uint ReadNetwork(SourceRows rows, MetricOwnerRawRound raw, MetricDiscoveryRoundFacts facts)
    {
        using var call = rows.BeginCall();
        var targets = SdkScopes(call, x => x.Network ?? throw Contract());
        var values = resources.ReadNetworkInterfaces(targets, ByKey(call, facts)); call.Dispose();
        var byKey = SdkResultMap(rows, values, static x => x.DeviceIdentity, StringComparer.Ordinal); var at = Now();
        Each(rows, row =>
        {
            if (!byKey.TryRow(row, out var value)) { Missing(raw, row); return; }
            if (value.RequiresTopologyRefresh) { raw.InvalidateSourceIdentity(row, SdkUnavailableReason); return; }
            var code = Field(facts, row);
            if (value.Rates.TryGetValue(code, out var pair)) raw.WriteCounter(row, 2, pair.Counter, pair.Ticks, 0, pair.TicksPerSecond, at);
            else Integer(raw, row, value.Counters.TryGetValue(code, out var v) ? v : null, at);
        });
        return Complete;
    }

    [RuntimeRead(ReadVolatilityKind.Sampled)]
    private uint ReadBattery(SourceRows rows, MetricOwnerRawRound raw, MetricDiscoveryRoundFacts facts)
    {
        using var call = rows.BeginCall(2);
        var values = resources.ReadBattery(SdkScopes(call, x => x.Battery ?? throw Contract()), ByIndex(call, facts)); call.Dispose();
        var byKey = SdkResultMap(rows, values, static x => x.DeviceIdentity, StringComparer.Ordinal); var at = Now();
        Each(rows, row =>
        {
            if (!byKey.TryRow(row, out var value)) { Missing(raw, row); return; }
            if (value.RequiresTopologyRefresh) { raw.InvalidateSourceIdentity(row, SdkUnavailableReason); return; }
            var code = Field(facts, row);
            if (code == MetricFieldCode.BatteryPowerState) Integer(raw, row, value.PowerState, at);
            else Number(raw, row, value.Values.TryGetValue(code, out var v) ? v : null, at);
        });
        return Complete;
    }

    [RuntimeRead(ReadVolatilityKind.Sampled)]
    private uint ReadNpuEngine(SourceRows rows, MetricOwnerRawRound raw, MetricDiscoveryRoundFacts facts, int sourceRow, CancellationToken token)
    {
        var engineRows = rows.Filter(static (r, row) => r.Scope(row).NpuEngine is not null);
        using var call = engineRows.BeginCall();
        var targets = SdkScopes(call, static x => x.NpuEngine!);
        IReadOnlyList<NpuEngineRead> values = targets.Count == 0 ? [] : resources.ReadNpuEngines(targets,
            SdkDeviceKeys(call), topology.NpuEngineMaximumInstanceCount, token);
        call.Dispose();
        token.ThrowIfCancellationRequested(); var byKey = SdkResultMap(rows, values, x => SdkIdentityKey(rows, x.Engine, static scope => scope.NpuEngine, static engine => engine.ExactIdentityKey), StringComparer.Ordinal); var at = Now();
        Each(rows, row =>
        {
            var scope = Scope(facts, row);
            if (scope.NpuEngine is null) { Missing(raw, row, MissingOperandReason); return; }
            if (!byKey.TryRow(row, out var value)) { Missing(raw, row); return; }
            if (value.RequiresTopologyRefresh || !ReferenceEquals(scope.NpuEngine, value.Engine))
            { raw.InvalidateSourceIdentity(row, SdkUnavailableReason); return; }
            if (value.Sample is not { } sample) { Missing(raw, row); return; }
            if (Field(facts, row) == MetricFieldCode.NpuEngineReportedRunningTimeRatioPercent)
            {
                raw.WriteCounter(row, 2, sample.RunningTimeMicroseconds, sample.QueryReturnTicks, 0, sample.TicksPerSecond, at);
                if ((facts.SourceRoleFlags(sourceRow) & 4) != 0) facts.DeclareNpuSampleGeneration(row, facts.Observation(sourceRow));
            }
            else if (Field(facts, row) == MetricFieldCode.NpuEngineRunningTimeMicroseconds)
            {
                Integer(raw, row, sample.RunningTimeMicroseconds, at);
                // Only the timed-ratio dependency has a native NPU generation
                // slot. The absolute counter is an independent UInt64 field.
            }
            else Missing(raw, row, MissingOperandReason);
        });
        return Complete;
    }

    [RuntimeRead(ReadVolatilityKind.Sampled)]
    private uint ReadNpuMemory(SourceRows rows, MetricOwnerRawRound raw, MetricDiscoveryRoundFacts facts, CancellationToken token)
    {
        static NpuMemoryFields Flags(MetricFieldCode code) => code switch
        {
            MetricFieldCode.NpuDedicatedMemoryCommitted => NpuMemoryFields.DedicatedCommitted,
            MetricFieldCode.NpuDedicatedMemoryResident => NpuMemoryFields.DedicatedResident,
            MetricFieldCode.NpuSharedMemoryCommitted => NpuMemoryFields.SharedCommitted,
            MetricFieldCode.NpuSharedMemoryResident => NpuMemoryFields.SharedResident, _ => NpuMemoryFields.None
        };
        using var call = rows.BeginCall();
        var fields = ByKey(call, facts);
        var requested = SdkMapView(call, SdkDeviceKeys(call),
            key => fields[key].Aggregate(NpuMemoryFields.None, static (flags, code) => flags | Flags(code)));
        var values = resources.ReadNpuMemory(SdkScopes(call, x => x.NpuMemory ?? throw Contract()),
            requested, topology.NpuMemoryMaximumInstanceCount, token);
        call.Dispose();
        token.ThrowIfCancellationRequested(); var byKey = SdkResultMap(rows, values, static x => x.DeviceIdentity, StringComparer.Ordinal); var at = Now();
        Each(rows, row =>
        {
            if (!byKey.TryRow(row, out var value)) { Missing(raw, row); return; }
            if (value.RequiresTopologyRefresh) { raw.InvalidateSourceIdentity(row, SdkUnavailableReason); return; }
            Integer(raw, row, Field(facts, row) switch
            {
                MetricFieldCode.NpuDedicatedMemoryCommitted => value.DedicatedCommitted,
                MetricFieldCode.NpuDedicatedMemoryResident => value.DedicatedResident,
                MetricFieldCode.NpuSharedMemoryCommitted => value.SharedCommitted,
                MetricFieldCode.NpuSharedMemoryResident => value.SharedResident, _ => null
            }, at);
        });
        return Complete;
    }

    [RuntimeRead(ReadVolatilityKind.Sampled)]
    private uint ReadStorage(MetricOwnerSdkSource source, SourceRows rows, MetricOwnerRawRound raw, MetricDiscoveryRoundFacts facts)
    {
        using var call = rows.BeginCall(2);
        var indexes = SdkIndexes(call);
        var devices = SdkMapView(call, indexes, index => call.Scope(call.ForIndex(index).First()).DeviceKey);
        var ata = SdkMapView(call, indexes, index => new AtaSmartDeviceReadRequest(devices[index],
            call.ForIndex(index).Aggregate(AtaSmartPages.None, (pages, row) => pages | WindowsAtaSmartReader.PageFor(call.Field(row)))));
        var sensors = source switch
        {
            MetricOwnerSdkSource.Storage => resources.ReadStorageSensors(),
            MetricOwnerSdkSource.Nvme => resources.ReadNvmeSensors(new(devices)),
            MetricOwnerSdkSource.Ata => resources.ReadAtaSensors(new(ata)),
            _ => throw Contract()
        };
        call.Dispose();
        var byIndex = SdkResultMap(rows, sensors.Disks, static x => x.Index); var at = Now();
        Each(rows, row =>
        {
            var scope = Scope(facts, row); var code = Field(facts, row);
            if (!byIndex.TryRow(row, out var value)) { Missing(raw, row); return; }
            if (value.IdentityKey != scope.DeviceKey) { raw.InvalidateSourceIdentity(row, SdkUnavailableReason); return; }
            switch (raw.ValueKind(row))
            {
                case MetricOwnerValueKind.Unsigned64:
                    Integer(raw, row, value.IntegerValues.TryGetValue(code, out var integer) ? integer : null, at); break;
                case MetricOwnerValueKind.Unsigned128:
                    if (value.WideIntegerValues.TryGetValue(code, out var wide))
                        raw.WriteScalar(row, MetricOwnerValueKind.Unsigned128, unchecked((ulong)wide), (ulong)(wide >> 64), at);
                    else Missing(raw, row);
                    break;
                case MetricOwnerValueKind.Bytes:
                    if (value.BytesValues.TryGetValue(code, out var bytes))
                    {
                        // Dictionary membership is presence; a null payload is an explicit typed null.
                        if (bytes is null) raw.ReserveBytes(row, 0, at, isNull: true);
                        else raw.WriteBytes(row, bytes, at);
                    }
                    else Missing(raw, row);
                    break;
                default:
                    Number(raw, row, code == MetricFieldCode.DiskTemperature ? value.TemperatureCelsius
                        : value.Values.TryGetValue(code, out var number) ? number : null, at); break;
            }
        });
        return ProviderStatus(sensors.ProviderState);
    }

    [RuntimeRead(ReadVolatilityKind.Sampled)]
    private async ValueTask<uint> ReadDiskAsync(SourceRows rows, MetricOwnerRawRound raw, MetricDiscoveryRoundFacts facts, CancellationToken token)
    {
        using var call = rows.BeginCall();
        var targets = SdkScopes(call, x => x.DiskPerformance ?? throw Contract());
        var values = await resources.ReadDiskPerformanceAsync(targets, token).ConfigureAwait(false);
        call.Dispose();
        token.ThrowIfCancellationRequested(); var at = Now();
        Each(rows, row =>
        {
            var target = Scope(facts, row).DiskPerformance ?? throw Contract();
            var value = values.Readings.GetValueOrDefault(target);
            Number(raw, row, value?.ValueFor(Field(facts, row)), value?.ObservedAt?.ToUnixTimeMilliseconds() ?? at);
        });
        return Complete;
    }
}
