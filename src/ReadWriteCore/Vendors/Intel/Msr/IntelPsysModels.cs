using ResourceManager.ReadWrite.Windows.Cpu.Models;

namespace ResourceManager.ReadWrite.Vendors.Intel.Msr;

internal static class IntelPsysModels
{
    // Intel SDM Vol.4 table 2-39: only the verified 06_4E/06_5E client layout.
    // A supported model permits reading register evidence; it does not prove BIOS enablement.
    internal static bool Supports(CpuIdentityInformation identity)
        => identity.Vendor == "GenuineIntel" && identity.Family == 6 && identity.Model is 0x4E or 0x5E;
}
