using ResourceManager.ReadWrite.Windows.Cpu.Models;

namespace ResourceManager.ReadWrite.Windows.Cpu;

public static partial class WindowsCpuThreadCpuidReader
{
    private static CpuAmdExtendedTopology? ReadAmdExtendedTopology(string vendor, uint maximum,
        Func<int, int, (int Eax, int Ebx, int Ecx, int Edx)> read)
    {
        if (vendor != "AuthenticAMD" || maximum < 0x80000026) return null;
        var levels = new CpuAmdTopologyLevel?[4];
        uint? apic = null;
        var previousType = 0;
        byte previousShift = 0;
        ushort previousCount = 0;
        for (var subleaf = 0; subleaf < 32; subleaf++)
        {
            var value = read(unchecked((int)0x80000026), subleaf);
            var type = (unchecked((uint)value.Ecx) >> 8) & 0xFF;
            var count = unchecked((ushort)value.Ebx);
            if (type == 0 || count == 0)
                return type == 0 && count == 0 && apic.HasValue
                    && levels[0] is { } core && levels[1] is { } complex
                    && levels[2] is { } ccd && levels[3] is { } socket
                        ? new(apic.Value, core, complex, ccd, socket) : null;
            var shift = (byte)(value.Eax & 0x1F);
            var currentApic = unchecked((uint)value.Edx);
            if (type is < 1 or > 4 || (value.Ecx & 0xFF) != subleaf
                || type <= previousType || shift < previousShift || count < previousCount
                || count > (1UL << shift) || (apic.HasValue && apic.Value != currentApic))
                return null;
            levels[type - 1] = new(shift, count);
            apic = currentApic;
            previousType = (int)type;
            previousShift = shift;
            previousCount = count;
        }
        return null;
    }
}
