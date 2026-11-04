using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native.MetricOwner;
using ResourceManager.ReadWrite.Vendors.Amd.Smu;

namespace ResourceManager.ReadWrite.Windows.Metrics.Zig.Adapters;

public sealed partial class MetricOwnerSdkSampler
{
    [RuntimeRead(ReadVolatilityKind.Sampled)]
    private uint ReadIntelMsr(SourceRows rows, MetricOwnerRawRound raw, MetricDiscoveryRoundFacts facts)
    {
        using var call = rows.BeginCall();
        var targets = SdkScopes(call, x => x.IntelMsr ?? throw Contract());
        var values = resources.ReadIntelMsr(targets, ByKey(call, facts)); call.Dispose();
        var byKey = SdkResultMap(rows, values, static x => x.DeviceIdentity, StringComparer.Ordinal); var at = Now();
        Each(rows, row =>
        {
            var code = Field(facts, row);
            if (!byKey.TryRow(row, out var value)) { Missing(raw, row); return; }
            if (value.RequiresTopologyRefresh) { raw.InvalidateSourceIdentity(row, SdkUnavailableReason); return; }
            if (value.CounterPairs.TryGetValue(code, out var pair))
                raw.WriteCounter(row, 1, pair.Primary, pair.Reference, 0, 0, at, pair.ReferenceFrequencyMhz);
            else if (raw.ValueKind(row) == MetricOwnerValueKind.Unsigned64)
                Integer(raw, row, value.UnsignedValues.TryGetValue(code, out var unsigned) ? unsigned : null, at);
            else if (raw.ValueKind(row) == MetricOwnerValueKind.Boolean)
                Boolean(raw, row, value.BooleanValues.TryGetValue(code, out var flag) ? flag : null, at);
            else Number(raw, row, value.Values.TryGetValue(code, out var number) ? number : null, at);
        });
        return Complete;
    }

    [RuntimeRead(ReadVolatilityKind.Sampled)]
    private uint ReadCcd(SourceRows rows, MetricOwnerRawRound raw, MetricDiscoveryRoundFacts facts)
    {
        using var call = rows.BeginCall();
        var targets = SdkScopes(call, x => x.AmdCcd ?? throw Contract());
        var values = resources.ReadAmdCcd(targets, SdkDeviceKeys(call)); call.Dispose();
        var at = Now(); Each(rows, row => Number(raw, row, values.GetValueOrDefault(Scope(facts, row).DeviceKey), at));
        return Complete;
    }

    [RuntimeRead(ReadVolatilityKind.Sampled)]
    private uint ReadSmu(SourceRows rows, MetricOwnerRawRound raw, MetricDiscoveryRoundFacts facts)
    {
        bool H(params ReadOnlySpan<MetricFieldCode> codes) => Has(rows, facts, codes);
        var value = resources.ReadAmdSmu(new AmdSmuCpuSensorReadRequest(
            H(MetricFieldCode.CpuPackagePower), H(MetricFieldCode.CpuCoreVoltage), H(MetricFieldCode.CpuPackageCurrent),
            H(MetricFieldCode.CpuTemperature), H(MetricFieldCode.CpuStapmPower), H(MetricFieldCode.CpuActualPower),
            H(MetricFieldCode.CpuAveragePower), H(MetricFieldCode.CpuTdcCurrent), H(MetricFieldCode.CpuEdcCurrent),
            H(MetricFieldCode.CpuSocPower), H(MetricFieldCode.CpuSocVoltage),
            H(MetricFieldCode.CpuIgpuFrequency, MetricFieldCode.GpuGraphicsClock),
            H(MetricFieldCode.CpuIgpuVoltage, MetricFieldCode.GpuVoltage),
            H(MetricFieldCode.CpuIgpuTemperature, MetricFieldCode.GpuTemperature),
            H(MetricFieldCode.CpuFrequency, MetricFieldCode.CpuSmuFrequency)));
        var at = Now();
        Each(rows, row =>
        {
            // Routing evidence comes from the existing raw topology binding, never inferred by display order.
            var code = Field(facts, row);
            if (code == MetricFieldCode.CpuFrequencyPercent)
            { raw.WriteUnavailable(row, MetricOwnerObservationStatus.Unsupported, 0); return; }
            var gpu = code is MetricFieldCode.GpuGraphicsClock or MetricFieldCode.GpuVoltage or MetricFieldCode.GpuTemperature;
            if (gpu && Scope(facts, row).DeviceKey != topology.SmuIntegratedGpuDeviceKey)
            { Missing(raw, row, MissingOperandReason); return; }
            if (gpu) facts.DeclareIntegratedGpu(row, facts.DeviceKey(row));
            double? v = code switch
            {
                MetricFieldCode.CpuFrequency or MetricFieldCode.CpuSmuFrequency => value.SmuFrequencyMhz,
                MetricFieldCode.CpuPackagePower => value.PackagePowerWatts, MetricFieldCode.CpuCoreVoltage => value.CoreVoltageVolts,
                MetricFieldCode.CpuPackageCurrent => value.PackageCurrentAmps, MetricFieldCode.CpuTemperature => value.TemperatureCelsius,
                MetricFieldCode.CpuStapmPower => value.StapmPowerWatts, MetricFieldCode.CpuActualPower => value.ActualPowerWatts,
                MetricFieldCode.CpuAveragePower => value.AveragePowerWatts, MetricFieldCode.CpuTdcCurrent => value.TdcCurrentAmps,
                MetricFieldCode.CpuEdcCurrent => value.EdcCurrentAmps, MetricFieldCode.CpuSocPower => value.SocPowerWatts,
                MetricFieldCode.CpuSocVoltage => value.SocVoltageVolts,
                MetricFieldCode.CpuIgpuFrequency or MetricFieldCode.GpuGraphicsClock => value.ApuFrequencyMhz,
                MetricFieldCode.CpuIgpuVoltage or MetricFieldCode.GpuVoltage => value.ApuVoltageVolts,
                MetricFieldCode.CpuIgpuTemperature or MetricFieldCode.GpuTemperature => value.ApuTemperatureCelsius, _ => null
            };
            Number(raw, row, v, at);
        });
        return ProviderStatus(value.ProviderState);
    }

    private static uint ProviderStatus(HardwareSensorProviderState state) => state.State switch
    {
        HardwareSensorProviderStates.Active => Complete,
        HardwareSensorProviderStates.Current => Complete,
        HardwareSensorProviderStates.Partial => 2,
        HardwareSensorProviderStates.NotRequested => 5,
        HardwareSensorProviderStates.UnsupportedFirmware => Unsupported,
        _ => Unavailable
    };

    [RuntimeRead(ReadVolatilityKind.Sampled)]
    private uint ReadHardwareMonitor(SourceRows rows, MetricOwnerRawRound raw, MetricDiscoveryRoundFacts facts)
    {
        var value = resources.ReadHardwareMonitor(); var at = Now();
        var fans = SdkResultMap(rows, value.SystemFans, static fan => fan.IdentityKey, StringComparer.Ordinal);
        Each(rows, row =>
        {
            if (value.CatalogComplete == false) { Missing(raw, row); return; }
            var key = Scope(facts, row).DeviceKey; var code = Field(facts, row);
            var fan = code is MetricFieldCode.FanRpm or MetricFieldCode.FanPercent
                ? fans.GetRow(row) : null;
            if (value.CatalogComplete == true)
            {
                if ((code == MetricFieldCode.CpuTemperature && value.CpuTemperatureSensorIdentifier is not null)
                    || (code == MetricFieldCode.FanRpm && fan is not null)) facts.DeclareSupported(row);
                if (code == MetricFieldCode.CpuTemperature && value.CpuTemperatureReadingKind is { } quantity)
                    DeclareQuantity(facts, row, quantity);
            }
            Number(raw, row, code switch
            {
                MetricFieldCode.MemoryTemperature => value.MemoryTemperatureCelsius, MetricFieldCode.CpuTemperature => value.CpuTemperatureCelsius,
                MetricFieldCode.MotherboardTemperature => value.MotherboardTemperature?.Value, MetricFieldCode.VrmTemperature => value.VrmTemperature?.Value,
                MetricFieldCode.ChipsetTemperature => value.ChipsetTemperature?.Value, MetricFieldCode.MotherboardVoltage => value.MotherboardVoltage?.Value,
                MetricFieldCode.CpuFanRpm => value.CpuFanSpeedRpm, MetricFieldCode.CpuFanPercent => value.CpuFanSpeedPercent,
                MetricFieldCode.FanRpm => fan?.SpeedRpm, MetricFieldCode.FanPercent => fan?.SpeedPercent, _ => null
            }, at);
        });
        return value.CatalogComplete == false ? Unavailable : ProviderStatus(value.ProviderState);
    }

    [RuntimeRead(ReadVolatilityKind.Sampled)]
    private uint ReadNotebook(SourceRows rows, MetricOwnerRawRound raw, MetricDiscoveryRoundFacts facts)
    {
        var value = resources.ReadNotebookFans(); var at = Now();
        var status = ProviderStatus(value.ProviderState);
        if (facts.Notebook is { } input)
        {
            RequireInputCapacity(value.GpuFanSensors.Count, input.Capacity);
            input.SetCpu(value.CpuFanSpeedRpm ?? 0, value.CpuFanSpeedPercent ?? 0,
                (value.CpuFanSpeedRpm.HasValue ? 1U : 0U) | (value.CpuFanSpeedPercent.HasValue ? 2U : 0U));
            var i = 0;
            while (i < value.GpuFanSensors.Count)
            {
                var sensor = value.GpuFanSensors[i];
                if (sensor.Index < 0) throw Contract();
                input.SetRow(i++, (uint)sensor.Index, sensor.FanSpeedRpm ?? 0, sensor.FanSpeedPercent ?? 0,
                    (sensor.FanSpeedRpm.HasValue ? 1U : 0U) | (sensor.FanSpeedPercent.HasValue ? 2U : 0U));
            }
            input.Complete(checked((uint)i), status, at);
        }
        var fans = SdkResultMap(rows, value.PhysicalFans, static fan => fan.IdentityKey, StringComparer.Ordinal);
        Each(rows, row =>
        {
            var code = Field(facts, row); var scope = Scope(facts, row);
            if (facts.Notebook is not null && code is (MetricFieldCode.GpuFanRpm or MetricFieldCode.GpuFanPercent)) return;
            var fan = fans.GetRow(row);
            Number(raw, row, code switch
            {
                MetricFieldCode.CpuFanRpm => value.CpuFanSpeedRpm, MetricFieldCode.CpuFanPercent => value.CpuFanSpeedPercent,
                MetricFieldCode.FanRpm => fan?.SpeedRpm, MetricFieldCode.FanReportedPwmDuty => fan?.ReportedPwmDutyPercent,
                _ => null // A GPU fan requires exact physical identity; no single-GPU index inference.
            }, at);
        });
        return status;
    }
}
