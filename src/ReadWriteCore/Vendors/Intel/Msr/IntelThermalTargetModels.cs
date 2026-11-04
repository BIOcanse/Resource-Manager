using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using System.Collections.Frozen;

namespace ResourceManager.ReadWrite.Vendors.Intel.Msr;

internal static class IntelThermalTargetModels
{
    // Intel SDM Vol.4 sections 2.8–2.18: model tables explicitly include or inherit
    // 1A2[23:16] as the temperature target. Unlisted models are not inferred by name.
    // Atom and unverified signatures remain eligible for architectural DTS distance only.
    private static readonly FrozenSet<int> Family6Models = new[] {
        0x1A, 0x1E, 0x1F, 0x25, 0x2A, 0x2C, 0x2D, 0x2E, 0x2F,
        0x3A, 0x3C, 0x3D, 0x3E, 0x3F, 0x45, 0x46, 0x47, 0x4E, 0x4F,
        0x55, 0x56, 0x57, 0x5E, 0x66, 0x6A, 0x6C, 0x7E, 0x85,
        0x8C, 0x8D, 0x8E, 0x8F, 0x97, 0x9A, 0x9E, 0xA5, 0xA6,
        0xAA, 0xAD, 0xAE, 0xAF, 0xB7, 0xBA, 0xBD, 0xBF, 0xCF
    }.ToFrozenSet();

    internal static bool Supports(CpuIdentityInformation identity)
        => identity.Vendor == "GenuineIntel" && identity.Family == 6 && Family6Models.Contains(identity.Model);

    // SDM Vol.4 table 2-61 scopes 19C to a two-core module, not an OS physical core.
    internal static bool UsesModuleDts(CpuIdentityInformation identity)
        => identity.Vendor == "GenuineIntel" && identity.Family == 6 && identity.Model is 0x57 or 0x85;
}
