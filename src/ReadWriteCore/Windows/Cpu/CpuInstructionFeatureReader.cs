namespace ResourceManager.ReadWrite.Windows.Cpu;

/// <summary>Decodes documented instruction declarations, independently of OS enablement and execution permission.</summary>
internal static partial class CpuInstructionFeatureReader
{
    internal static IReadOnlyList<string> Read(string vendor, uint maximum, uint extendedMaximum,
        (int Eax, int Ebx, int Ecx, int Edx) identity,
        Func<int, int, (int Eax, int Ebx, int Ecx, int Edx)> read)
    {
        var intel = vendor == "GenuineIntel";
        var amd = vendor == "AuthenticAMD";
        if (!intel && !amd) return [];
        var features = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (maximum is >= 1 and < 0x80000000)
        {
            Add(identity.Edx, Leaf1Edx);
            Add(identity.Ecx, Leaf1Ecx);
            if (intel) Add(identity.Ecx, IntelLeaf1Ecx);
        }
        if (maximum is >= 7 and < 0x80000000)
        {
            var structured = read(7, 0);
            Add(structured.Ebx, Leaf7Ebx); Add(structured.Ecx, Leaf7Ecx);
            if (intel)
            {
                Add(structured.Ebx, IntelLeaf7Ebx); Add(structured.Ecx, IntelLeaf7Ecx);
                Add(structured.Edx, IntelLeaf7Edx);
            }
            // Only the documented subleaf is queried, regardless of the declared maximum.
            if (unchecked((uint)structured.Eax) >= 1)
            {
                var next = read(7, 1);
                Add(next.Eax, Leaf7Subleaf1Eax);
                if (intel)
                {
                    Add(next.Eax, IntelLeaf7Subleaf1Eax); Add(next.Ebx, IntelLeaf7Subleaf1Ebx);
                    Add(next.Edx, IntelLeaf7Subleaf1Edx);
                }
            }
        }
        if (maximum is >= 0xD and < 0x80000000 && (unchecked((uint)identity.Ecx) & (1U << 26)) != 0)
            Add(read(0xD, 1).Eax, LeafDSubleaf1Eax);
        if (extendedMaximum >= 0x80000001)
        {
            var extended = read(unchecked((int)0x80000001), 0);
            Add(extended.Ecx, Extended1Ecx); Add(extended.Edx, Extended1Edx);
            if (intel) Add(extended.Edx, IntelExtended1Edx);
            else
            {
                Add(extended.Ecx, AmdExtended1Ecx); Add(extended.Edx, AmdExtended1Edx);
            }
        }
        if (extendedMaximum >= 0x80000008)
        {
            var bits = read(unchecked((int)0x80000008), 0).Ebx;
            Add(bits, Extended8Ebx);
            if (amd) Add(bits, AmdExtended8Ebx);
        }
        if (amd && extendedMaximum >= 0x80000021)
            Add(read(unchecked((int)0x80000021), 0).Eax, AmdExtended21Eax);
        return features;

        void Add(int bits, ReadOnlySpan<Feature> definitions)
        {
            foreach (var definition in definitions)
                if ((unchecked((uint)bits) & (1U << definition.Bit)) != 0
                    && (unchecked((uint)identity.Ecx) & definition.RequiredLeaf1Ecx) == definition.RequiredLeaf1Ecx
                    && names.Add(definition.Name)) features.Add(definition.Name);
        }
    }

    private readonly record struct Feature(int Bit, string Name, uint RequiredLeaf1Ecx = 0);
}
