using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Cpu.Models;

namespace ResourceManager.ReadWrite.Vendors.Intel.Msr;

[Flags]
public enum IntelRaplDomains { None = 0, Package = 1, Pp0 = 2, Pp1 = 4, Dram = 8 }

public readonly record struct IntelRaplProfile(IntelRaplDomains Domains, double? FixedDramUnitJoules = null)
{
    public IntelRaplDomains PowerInformationDomains { get; init; }
    public bool DramPowerUsesRaplUnits { get; init; }
}

internal static class IntelRaplModels
{
    // Intel SDM Vol.4 tables 2-20..2-39 and the explicit model overrides in
    // 2-32, 2-36, 2-50..2-52. A read-zero reserved PP0 domain is never advertised.
    // PSys and Atom unit layouts require their own verified contracts.
    internal static IntelRaplProfile Get(CpuIdentityInformation identity, IntelMsrTargetKind kind = IntelMsrTargetKind.Package)
    {
        if (identity.Vendor != "GenuineIntel" || identity.Family != 6) return default;
        if (kind == IntelMsrTargetKind.PhysicalDie)
            return identity.Model == 0x55 ? new(IntelRaplDomains.Package | IntelRaplDomains.Dram, 15.3e-6)
                { PowerInformationDomains = IntelRaplDomains.Package } : default;
        IntelRaplProfile profile = identity.Model switch
        {
            0x2A or 0x3A => new(IntelRaplDomains.Package | IntelRaplDomains.Pp0 | IntelRaplDomains.Pp1),
            0x2D or 0x3E => new(IntelRaplDomains.Package | IntelRaplDomains.Pp0 | IntelRaplDomains.Dram),
            0x3C or 0x45 or 0x46 or 0x3D or 0x47 => new(IntelRaplDomains.Package | IntelRaplDomains.Pp0 | IntelRaplDomains.Pp1 | IntelRaplDomains.Dram),
            0x3F or 0x4F or 0x56 => new(IntelRaplDomains.Package | IntelRaplDomains.Dram, 15.3e-6),
            // 06_55H also covers CLX-AP, whose RAPL counters are die-scoped.
            // They are exposed only through the independently verified OS die targets.
            0x55 => default,
            // The modern common tables include PP0, but do not inherit table 2-22's PP1.
            // Server overrides and other unverified domains remain absent.
            0x4E or 0x5E or 0x66 or 0x8E or 0x9E or 0xA5 or 0xA6 or 0x7E
                or 0x8C or 0x8D or 0x97 or 0x9A or 0xB7 or 0xBA or 0xBF or 0xAA or 0xBD
                => new(IntelRaplDomains.Package | IntelRaplDomains.Pp0 | IntelRaplDomains.Dram),
            // These server model tables explicitly specify the DRAM unit. Do not
            // inherit client energy domains merely from the processor's marketing name.
            0x6A or 0x6C => new(IntelRaplDomains.Package | IntelRaplDomains.Dram, 15.3e-6),
            0x8F or 0xCF => new(IntelRaplDomains.Package | IntelRaplDomains.Dram, 61e-6),
            _ => default
        };
        if (profile.Domains == IntelRaplDomains.None) return profile;
        // Table 2-20 is inherited by the known package models. DRAM power-info is
        // separate: 2D/3E use the shared units; 6A/6C enumerate another unit interface.
        return profile with {
            PowerInformationDomains = IntelRaplDomains.Package | (identity.Model is 0x2D or 0x3E or 0x6A or 0x6C ? IntelRaplDomains.Dram : IntelRaplDomains.None),
            DramPowerUsesRaplUnits = identity.Model is 0x2D or 0x3E
        };
    }
}
