using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Vendors.Intel;

namespace ResourceManager.ReadWrite.Vendors.Intel.Msr;

[Flags]
public enum IntelThermalStatusCapabilities { None = 0, Raw = 1, Thermal = 2, Power = 4, Current = 8, CrossDomain = 16 }

internal readonly record struct IntelThermalStatusField(MetricFieldCode Code, bool IsPackage,
    int Bit, IntelThermalStatusCapabilities Capability);

internal static class IntelThermalStatusCatalog
{
    private const IntelThermalStatusCapabilities Raw = IntelThermalStatusCapabilities.Raw;
    private const IntelThermalStatusCapabilities Thermal = IntelThermalStatusCapabilities.Thermal;
    private const IntelThermalStatusCapabilities Power = IntelThermalStatusCapabilities.Power;
    private const IntelThermalStatusCapabilities Current = IntelThermalStatusCapabilities.Current;
    private const IntelThermalStatusCapabilities Cross = IntelThermalStatusCapabilities.CrossDomain;
    internal static readonly IReadOnlyList<IntelThermalStatusField> Fields = Array.AsReadOnly(new[] {
        new IntelThermalStatusField(MetricFieldCode.IntelCoreThermalStatus, false, -1, Raw),
        new(MetricFieldCode.IntelCoreThermalActive, false, 0, Thermal), new(MetricFieldCode.IntelCoreThermalLogged, false, 1, Thermal),
        new(MetricFieldCode.IntelCorePowerActive, false, 10, Power), new(MetricFieldCode.IntelCorePowerLogged, false, 11, Power),
        new(MetricFieldCode.IntelCoreCurrentActive, false, 12, Current), new(MetricFieldCode.IntelCoreCurrentLogged, false, 13, Current),
        new(MetricFieldCode.IntelCoreCrossDomainActive, false, 14, Cross), new(MetricFieldCode.IntelCoreCrossDomainLogged, false, 15, Cross),
        new(MetricFieldCode.IntelPackageThermalStatus, true, -1, Raw),
        new(MetricFieldCode.IntelPackageThermalActive, true, 0, Thermal), new(MetricFieldCode.IntelPackageThermalLogged, true, 1, Thermal),
        new(MetricFieldCode.IntelPackagePowerActive, true, 10, Power), new(MetricFieldCode.IntelPackagePowerLogged, true, 11, Power)
    });

    internal static IntelThermalStatusField? Find(MetricFieldCode code) => code switch {
        MetricFieldCode.IntelCoreThermalStatus => Fields[0], MetricFieldCode.IntelCoreThermalActive => Fields[1],
        MetricFieldCode.IntelCoreThermalLogged => Fields[2], MetricFieldCode.IntelCorePowerActive => Fields[3],
        MetricFieldCode.IntelCorePowerLogged => Fields[4], MetricFieldCode.IntelCoreCurrentActive => Fields[5],
        MetricFieldCode.IntelCoreCurrentLogged => Fields[6], MetricFieldCode.IntelCoreCrossDomainActive => Fields[7],
        MetricFieldCode.IntelCoreCrossDomainLogged => Fields[8], MetricFieldCode.IntelPackageThermalStatus => Fields[9],
        MetricFieldCode.IntelPackageThermalActive => Fields[10], MetricFieldCode.IntelPackageThermalLogged => Fields[11],
        MetricFieldCode.IntelPackagePowerActive => Fields[12], MetricFieldCode.IntelPackagePowerLogged => Fields[13],
        _ => null
    };

    internal static IntelThermalStatusCapabilities Capabilities(CpuThreadCpuidData data, IntelMsrTargetKind kind)
    {
        if (data.Identity.Vendor != "GenuineIntel") return IntelThermalStatusCapabilities.None;
        if (kind == IntelMsrTargetKind.Package)
            return data.IntelPackageThermalSupported == true ? Raw | Thermal | Power : IntelThermalStatusCapabilities.None;
        if (kind != IntelMsrTargetKind.PhysicalCore || data.IntelDtsSupported != true
            || IntelThermalTargetModels.UsesModuleDts(data.Identity)) return IntelThermalStatusCapabilities.None;
        return Raw | (data.IntelThermalMonitorSupported == true ? Thermal : 0)
            | (data.IntelPowerLimitNotificationSupported == true ? Power : 0)
            | (data.IntelHardwarePstatesSupported == true ? Current | Cross : 0);
    }

    internal static void Decode(bool isPackage, ulong raw, IReadOnlySet<MetricFieldCode> requested,
        IDictionary<MetricFieldCode, ulong> unsigned, IDictionary<MetricFieldCode, bool> booleans)
    {
        foreach (var field in Fields)
        {
            if (field.IsPackage != isPackage || !requested.Contains(field.Code)) continue;
            if (field.Bit < 0) unsigned[field.Code] = raw;
            else booleans[field.Code] = (raw & (1UL << field.Bit)) != 0;
        }
    }
}
