namespace ResourceManager.ReadWrite.Windows.Cpu;

public static partial class WindowsCpuThreadCpuidReader
{
    private static HardwareCapabilities ReadCapabilities(string vendor, uint maximum, uint extendedMaximum,
        (int Eax, int Ebx, int Ecx, int Edx) leaf1,
        Func<int, int, (int Eax, int Ebx, int Ecx, int Edx)> read)
    {
        var intel = vendor == "GenuineIntel";
        var amd = vendor == "AuthenticAMD";
        if (!intel && !amd) return default;
        var result = default(HardwareCapabilities);
        if (extendedMaximum >= 0x80000008)
        {
            var widths = unchecked((uint)read(unchecked((int)0x80000008), 0).Eax);
            var physical = unchecked((byte)widths);
            var linear = unchecked((byte)(widths >> 8));
            var guest = unchecked((byte)(widths >> 16));
            result = result with
            {
                PhysicalAddressWidthDeclaredBits = physical, LinearAddressWidthDeclaredBits = linear,
                // AMD APM: a zero guest field refers to PhysAddrSize, only for nested-paging guests.
                AmdNestedGuestPhysicalAddressWidthBits = amd ? (guest == 0 ? physical : guest) : null
            };
        }
        if (intel && maximum is >= 1 and < 0x80000000)
            result = result with { IntelVmxSupported = (leaf1.Ecx & (1 << 5)) != 0,
                IntelSmxSupported = (leaf1.Ecx & (1 << 6)) != 0 };
        if (amd && extendedMaximum >= 0x80000001)
        {
            var supported = (read(unchecked((int)0x80000001), 0).Ecx & 4) != 0;
            result = result with { AmdSvmSupported = supported };
            if (supported && extendedMaximum >= 0x8000000A)
            {
                var svm = read(unchecked((int)0x8000000A), 0);
                result = result with { AmdSvmRevision = unchecked((byte)svm.Eax),
                    AmdSvmAsidCountDeclared = unchecked((uint)svm.Ebx), AmdSvmFeatures = DecodeSvmFeatures(svm.Ecx, svm.Edx) };
            }
        }
        return result;
    }

    private static IReadOnlyList<string> DecodeSvmFeatures(int ecx, int edx)
    {
        var features = new List<string>();
        if ((ecx & (1 << 6)) != 0) features.Add("x2AVIC_EXT");
        foreach (var (bit, name) in AmdSvmEdxFeatures)
            if ((unchecked((uint)edx) & (1U << bit)) != 0) features.Add(name);
        return features;
    }

    // AMD APM 24594 Rev 3.37 E.4.9. These are hardware capabilities, including support for locking.
    private static readonly (int Bit, string Name)[] AmdSvmEdxFeatures =
    [
        (0, "NP"), (1, "LbrVirt"), (2, "SVML"), (3, "NRIPS"), (4, "TscRateMsr"),
        (5, "VmcbClean"), (6, "FlushByAsid"), (7, "DecodeAssists"), (8, "PmcVirt"),
        (10, "PauseFilter"), (12, "PauseFilterThreshold"), (13, "AVIC"), (15, "VMSAVEvirt"),
        (16, "VGIF"), (17, "GMET"), (18, "x2AVIC"), (19, "SSSCheck"), (20, "SpecCtrl"),
        (21, "ROGPT"), (23, "HOST_MCE_OVERRIDE"), (24, "TlbiCtl"), (25, "VNMI"),
        (26, "IbsVirt"), (27, "ExtLvtAvicAccessChg"), (28, "NestedVirtVmcbAddrChk"),
        (29, "BusLockThreshold"), (30, "IdleHltIntercept")
    ];

    private readonly record struct HardwareCapabilities
    {
        internal byte? PhysicalAddressWidthDeclaredBits { get; init; }
        internal byte? LinearAddressWidthDeclaredBits { get; init; }
        internal byte? AmdNestedGuestPhysicalAddressWidthBits { get; init; }
        internal bool? IntelVmxSupported { get; init; }
        internal bool? IntelSmxSupported { get; init; }
        internal bool? AmdSvmSupported { get; init; }
        internal byte? AmdSvmRevision { get; init; }
        internal uint? AmdSvmAsidCountDeclared { get; init; }
        internal IReadOnlyList<string>? AmdSvmFeatures { get; init; }
    }
}
