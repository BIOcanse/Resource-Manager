using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Vendors.Intel;

namespace ResourceManager.ReadWrite.Vendors.Intel.Msr;

internal sealed record IntelRaplPowerInformationFields(IntelRaplDomains Domain, uint Register,
    MetricFieldCode Raw, MetricFieldCode ThermalSpecification, MetricFieldCode MinimumSetting, MetricFieldCode MaximumSetting)
{
    internal bool IsDie { get; init; }
    internal bool Contains(MetricFieldCode code) => code == Raw || code == ThermalSpecification || code == MinimumSetting || code == MaximumSetting;
}

internal static class IntelRaplPowerInformation
{
    private static readonly IntelRaplPowerInformationFields Package = new(IntelRaplDomains.Package, 0x614,
        MetricFieldCode.IntelPackagePowerInformation, MetricFieldCode.IntelPackageThermalSpecPower,
        MetricFieldCode.IntelPackageMinimumPowerSetting, MetricFieldCode.IntelPackageMaximumPowerSetting);
    private static readonly IntelRaplPowerInformationFields Dram = new(IntelRaplDomains.Dram, 0x61C,
        MetricFieldCode.IntelDramPowerInformation, MetricFieldCode.IntelDramThermalSpecPower,
        MetricFieldCode.IntelDramMinimumPowerSetting, MetricFieldCode.IntelDramMaximumPowerSetting);
    private static readonly IntelRaplPowerInformationFields DiePackage = new(IntelRaplDomains.Package, 0x614,
        MetricFieldCode.IntelDiePackagePowerInformation, MetricFieldCode.IntelDiePackageThermalSpecPower,
        MetricFieldCode.IntelDiePackageMinimumPowerSetting, MetricFieldCode.IntelDiePackageMaximumPowerSetting) { IsDie = true };
    internal static IReadOnlyList<IntelRaplPowerInformationFields> Domains { get; } = [Package, Dram, DiePackage];

    internal static IntelRaplPowerInformationFields? Find(MetricFieldCode code) => code switch
    {
        MetricFieldCode.IntelPackagePowerInformation or MetricFieldCode.IntelPackageThermalSpecPower
            or MetricFieldCode.IntelPackageMinimumPowerSetting or MetricFieldCode.IntelPackageMaximumPowerSetting => Package,
        MetricFieldCode.IntelDramPowerInformation or MetricFieldCode.IntelDramThermalSpecPower
            or MetricFieldCode.IntelDramMinimumPowerSetting or MetricFieldCode.IntelDramMaximumPowerSetting => Dram,
        MetricFieldCode.IntelDiePackagePowerInformation or MetricFieldCode.IntelDiePackageThermalSpecPower
            or MetricFieldCode.IntelDiePackageMinimumPowerSetting or MetricFieldCode.IntelDiePackageMaximumPowerSetting => DiePackage,
        _ => null
    };

    internal static bool Supports(IntelRaplProfile profile, MetricFieldCode code) => Find(code) is { } domain
        && (profile.PowerInformationDomains & domain.Domain) != 0
        && (domain.Domain == IntelRaplDomains.Package || code == domain.Raw || profile.DramPowerUsesRaplUnits);

    internal static void Read(IIntelCpuHardware hardware, IntelRaplProfile profile, IReadOnlySet<MetricFieldCode> fields,
        Dictionary<MetricFieldCode, double> values, Dictionary<MetricFieldCode, ulong> unsigned,
        Func<IIntelCpuHardware, uint, ulong?> read)
    {
        foreach (var domain in Domains)
        {
            if (!fields.Any(field => domain.Contains(field) && Supports(profile, field))) continue;
            if (read(hardware, domain.Register) is not { } raw) continue;
            if (fields.Contains(domain.Raw)) unsigned[domain.Raw] = raw;
            if (!(fields.Contains(domain.ThermalSpecification) && Supports(profile, domain.ThermalSpecification)) &&
                !(fields.Contains(domain.MinimumSetting) && Supports(profile, domain.MinimumSetting)) &&
                !(fields.Contains(domain.MaximumSetting) && Supports(profile, domain.MaximumSetting))) continue;
            if (read(hardware, IntelRaplEncoding.UnitsRegister) is not { } units) continue;
            var powerUnit = Math.ScaleB(1d, -(int)(units & 15));
            if (fields.Contains(domain.ThermalSpecification)) values[domain.ThermalSpecification] = (raw & 32767) * powerUnit;
            if (fields.Contains(domain.MinimumSetting)) values[domain.MinimumSetting] = ((raw >> 16) & 32767) * powerUnit;
            if (fields.Contains(domain.MaximumSetting)) values[domain.MaximumSetting] = ((raw >> 32) & 32767) * powerUnit;
        }
    }
}
