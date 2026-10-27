using System.Buffers.Binary;
using System.Runtime.Intrinsics.X86;
using System.Text;
using ResourceManager.ReadWrite.Windows.Cpu.Models;

namespace ResourceManager.ReadWrite.Windows.Cpu;

public static class CpuIdentityInformationReader
{
    [RuntimeRead(ReadVolatilityKind.Fixed)]
    public static CpuIdentityInformation? Read()
        => Read(WindowsCpuOsFeatureReader.Read() ?? []);

    [RuntimeRead(ReadVolatilityKind.Fixed)]
    public static CpuIdentityInformation? Read(IReadOnlyList<string> osFeatures)
        => X86Base.IsSupported ? ReadRegisters(static (leaf, subleaf) => X86Base.CpuId(leaf, subleaf), osFeatures) : null;

    public static CpuIdentityInformation ReadRegisters(
        Func<int, int, (int Eax, int Ebx, int Ecx, int Edx)> read, IReadOnlyList<string> usable)
    {
        var leaf0 = read(0, 0);
        var vendor = Decode([leaf0.Ebx, leaf0.Edx, leaf0.Ecx]);
        var maximum = unchecked((uint)leaf0.Eax);
        var identity = maximum is >= 1 and < 0x80000000 ? read(1, 0) : default;
        var signature = unchecked((uint)identity.Eax);
        var baseFamily = (int)((signature >> 8) & 15);
        var family = baseFamily == 15 ? baseFamily + (int)((signature >> 20) & 255) : baseFamily;
        var model = (int)((signature >> 4) & 15);
        if (baseFamily == 15 || baseFamily == 6 && vendor == "GenuineIntel") model += (int)((signature >> 12) & 240);
        string? brand = null;
        var extended = read(unchecked((int)0x80000000), 0);
        var hardware = CpuInstructionFeatureReader.Read(vendor, maximum, unchecked((uint)extended.Eax), identity, read);
        if (unchecked((uint)extended.Eax) >= 0x80000004)
        {
            var words = new List<int>(12);
            for (uint leaf = 0x80000002; leaf <= 0x80000004; leaf++)
            {
                var value = read(unchecked((int)leaf), 0);
                words.AddRange([value.Eax, value.Ebx, value.Ecx, value.Edx]);
            }
            brand = Decode(words).Trim();
        }
        return new(vendor, family, model, (int)(signature & 15), brand, hardware, usable);
    }

    private static string Decode(IReadOnlyList<int> words)
    {
        var buffer = new byte[words.Count * 4];
        for (var i = 0; i < words.Count; i++) BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(i*4,4), words[i]);
        return Encoding.ASCII.GetString(buffer).TrimEnd('\0');
    }

}
