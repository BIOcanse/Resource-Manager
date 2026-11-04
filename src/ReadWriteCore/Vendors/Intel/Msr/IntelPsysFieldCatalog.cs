using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Vendors.Intel;

namespace ResourceManager.ReadWrite.Vendors.Intel.Msr;

internal static class IntelPsysFieldCatalog
{
    internal const uint CounterRegister = 0x64D;
    internal const uint UnitsRegister = 0x606;

    internal static bool Contains(MetricFieldCode code)
        => code is MetricFieldCode.IntelPsysEnergyCounter or MetricFieldCode.IntelPsysEnergyUnit;

    internal static void Read(IIntelCpuHardware hardware, IReadOnlySet<MetricFieldCode> fields,
        Dictionary<MetricFieldCode, double> values, Dictionary<MetricFieldCode, ulong> unsigned,
        Func<IIntelCpuHardware, uint, ulong?> read)
    {
        if (fields.Contains(MetricFieldCode.IntelPsysEnergyCounter) && read(hardware, CounterRegister) is { } counter)
            unsigned[MetricFieldCode.IntelPsysEnergyCounter] = counter & uint.MaxValue;
        if (fields.Contains(MetricFieldCode.IntelPsysEnergyUnit) && read(hardware, UnitsRegister) is { } units)
            values[MetricFieldCode.IntelPsysEnergyUnit] = Math.ScaleB(1d, -(int)((units >> 8) & 31));
    }
}
