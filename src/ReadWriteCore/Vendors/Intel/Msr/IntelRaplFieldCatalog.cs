using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Vendors.Intel;

namespace ResourceManager.ReadWrite.Vendors.Intel.Msr;

internal sealed record IntelRaplDomainFields(IntelRaplDomains Domain, uint Register,
    MetricFieldCode Counter, MetricFieldCode Joules, MetricFieldCode Unit, MetricFieldCode WrapRange)
{
    internal bool IsDie { get; init; }
    internal bool Contains(MetricFieldCode code) => code == Counter || code == Joules || code == Unit || code == WrapRange;
}

internal static class IntelRaplFieldCatalog
{
    private static readonly IntelRaplDomainFields Package = new(IntelRaplDomains.Package, 0x611,
        MetricFieldCode.IntelPackageEnergyCounter, MetricFieldCode.IntelPackageEnergyJoules,
        MetricFieldCode.IntelPackageEnergyUnit, MetricFieldCode.IntelPackageEnergyWrapRange);
    private static readonly IntelRaplDomainFields Pp0 = new(IntelRaplDomains.Pp0, 0x639,
        MetricFieldCode.IntelPp0EnergyCounter, MetricFieldCode.IntelPp0EnergyJoules,
        MetricFieldCode.IntelPp0EnergyUnit, MetricFieldCode.IntelPp0EnergyWrapRange);
    private static readonly IntelRaplDomainFields Pp1 = new(IntelRaplDomains.Pp1, 0x641,
        MetricFieldCode.IntelPp1EnergyCounter, MetricFieldCode.IntelPp1EnergyJoules,
        MetricFieldCode.IntelPp1EnergyUnit, MetricFieldCode.IntelPp1EnergyWrapRange);
    private static readonly IntelRaplDomainFields Dram = new(IntelRaplDomains.Dram, 0x619,
        MetricFieldCode.IntelDramEnergyCounter, MetricFieldCode.IntelDramEnergyJoules,
        MetricFieldCode.IntelDramEnergyUnit, MetricFieldCode.IntelDramEnergyWrapRange);
    private static readonly IntelRaplDomainFields DiePackage = new(IntelRaplDomains.Package, 0x611,
        MetricFieldCode.IntelDiePackageEnergyCounter, MetricFieldCode.IntelDiePackageEnergyJoules,
        MetricFieldCode.IntelDiePackageEnergyUnit, MetricFieldCode.IntelDiePackageEnergyWrapRange) { IsDie = true };
    private static readonly IntelRaplDomainFields DieDram = new(IntelRaplDomains.Dram, 0x619,
        MetricFieldCode.IntelDieDramEnergyCounter, MetricFieldCode.IntelDieDramEnergyJoules,
        MetricFieldCode.IntelDieDramEnergyUnit, MetricFieldCode.IntelDieDramEnergyWrapRange) { IsDie = true };
    internal static IReadOnlyList<IntelRaplDomainFields> Domains { get; } = [Package, Pp0, Pp1, Dram, DiePackage, DieDram];

    internal static IntelRaplDomainFields? Find(MetricFieldCode code) => code switch
    {
        MetricFieldCode.IntelPackageEnergyCounter or MetricFieldCode.IntelPackageEnergyJoules
            or MetricFieldCode.IntelPackageEnergyUnit or MetricFieldCode.IntelPackageEnergyWrapRange => Package,
        MetricFieldCode.IntelPp0EnergyCounter or MetricFieldCode.IntelPp0EnergyJoules
            or MetricFieldCode.IntelPp0EnergyUnit or MetricFieldCode.IntelPp0EnergyWrapRange => Pp0,
        MetricFieldCode.IntelPp1EnergyCounter or MetricFieldCode.IntelPp1EnergyJoules
            or MetricFieldCode.IntelPp1EnergyUnit or MetricFieldCode.IntelPp1EnergyWrapRange => Pp1,
        MetricFieldCode.IntelDramEnergyCounter or MetricFieldCode.IntelDramEnergyJoules
            or MetricFieldCode.IntelDramEnergyUnit or MetricFieldCode.IntelDramEnergyWrapRange => Dram,
        MetricFieldCode.IntelDiePackageEnergyCounter or MetricFieldCode.IntelDiePackageEnergyJoules
            or MetricFieldCode.IntelDiePackageEnergyUnit or MetricFieldCode.IntelDiePackageEnergyWrapRange => DiePackage,
        MetricFieldCode.IntelDieDramEnergyCounter or MetricFieldCode.IntelDieDramEnergyJoules
            or MetricFieldCode.IntelDieDramEnergyUnit or MetricFieldCode.IntelDieDramEnergyWrapRange => DieDram,
        _ => null
    };

    internal static void Read(IIntelCpuHardware hardware, IntelRaplProfile profile,
        IReadOnlySet<MetricFieldCode> fields, Dictionary<MetricFieldCode, double> values,
        Dictionary<MetricFieldCode, ulong> unsigned, Func<IIntelCpuHardware, uint, ulong?> read)
    {
        ulong? units = null;
        var unitsRead = false;
        foreach (var domain in Domains)
        {
            if ((profile.Domains & domain.Domain) == 0 || !fields.Any(domain.Contains)) continue;
            double? unit = null;
            if (fields.Contains(domain.Joules) || fields.Contains(domain.Unit) || fields.Contains(domain.WrapRange))
            {
                if (domain.Domain == IntelRaplDomains.Dram && profile.FixedDramUnitJoules is { } fixedUnit) unit = fixedUnit;
                else
                {
                    if (!unitsRead) { units = read(hardware, IntelRaplEncoding.UnitsRegister); unitsRead = true; }
                    if (units is { } rawUnits) unit = Math.ScaleB(1d, -(int)((rawUnits >> 8) & 31));
                }
            }
            if (fields.Contains(domain.Unit) && unit.HasValue) values[domain.Unit] = unit.Value;
            if (fields.Contains(domain.WrapRange) && unit.HasValue) values[domain.WrapRange] = unit.Value * 4294967296d;
            if (!fields.Contains(domain.Counter) && !fields.Contains(domain.Joules)) continue;
            if (read(hardware, domain.Register) is not { } raw) continue;
            var counter = raw & uint.MaxValue;
            if (fields.Contains(domain.Counter)) unsigned[domain.Counter] = counter;
            if (fields.Contains(domain.Joules) && unit.HasValue) values[domain.Joules] = counter * unit.Value;
        }
    }
}
