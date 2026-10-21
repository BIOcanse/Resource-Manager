namespace ResourceManager.ReadWrite.Windows.Cpu;

internal static partial class CpuInstructionFeatureReader
{
    // Intel SDM processor identification chapter; AMD APM 24594 Rev 3.37 Appendix E.
    // Common tables contain only meanings verified for both vendors. Reserved bits are not decoded.
    private static readonly Feature[] Leaf1Edx =
    [
        new(0, "x87"), new(4, "RDTSC"), new(5, "MSR"), new(8, "CMPXCHG8B"),
        new(11, "SYSENTER_SYSEXIT"), new(15, "CMOV"), new(19, "CLFLUSH"), new(23, "MMX"),
        new(24, "FXSR"), new(25, "SSE"), new(26, "SSE2")
    ];
    private static readonly Feature[] Leaf1Ecx =
    [
        new(0, "SSE3"), new(1, "PCLMULQDQ"), new(3, "MONITOR"), new(3, "MWAIT"), new(9, "SSSE3"),
        new(12, "FMA"), new(13, "CMPXCHG16B"), new(19, "SSE4.1"), new(20, "SSE4.2"),
        new(22, "MOVBE"), new(23, "POPCNT"), new(25, "AES"), new(26, "XSAVE"),
        new(28, "AVX"), new(29, "F16C"), new(30, "RDRAND")
    ];
    private static readonly Feature[] IntelLeaf1Ecx = [new(5, "VMX"), new(6, "SMX")];
    private static readonly Feature[] Leaf7Ebx =
    [
        new(0, "FSGSBASE"), new(3, "BMI1"), new(5, "AVX2"), new(8, "BMI2"), new(10, "INVPCID"),
        new(16, "AVX512F"), new(17, "AVX512DQ"), new(18, "RDSEED"), new(19, "ADX"),
        new(21, "AVX512IFMA"), new(23, "CLFLUSHOPT"), new(24, "CLWB"), new(28, "AVX512CD"),
        new(29, "SHA"), new(30, "AVX512BW"), new(31, "AVX512VL")
    ];
    private static readonly Feature[] Leaf7Ecx =
    [
        new(1, "AVX512VBMI"), new(6, "AVX512VBMI2"), new(8, "GFNI"),
        new(9, "VAES", 1U << 28), new(10, "VPCLMULQDQ", 1U << 28),
        new(11, "AVX512VNNI"), new(12, "AVX512BITALG"), new(14, "AVX512VPOPCNTDQ"),
        new(22, "RDPID"), new(27, "MOVDIRI"), new(28, "MOVDIR64B")
    ];
    private static readonly Feature[] IntelLeaf7Ebx =
    [new(4, "HLE"), new(11, "RTM"), new(14, "MPX"), new(26, "AVX512PF"), new(27, "AVX512ER")];
    private static readonly Feature[] IntelLeaf7Ecx =
    [new(0, "PREFETCHWT1"), new(5, "WAITPKG"), new(25, "CLDEMOTE"), new(29, "ENQCMD")];
    private static readonly Feature[] IntelLeaf7Edx =
    [
        new(2, "AVX5124VNNIW"), new(3, "AVX5124FMAPS"), new(5, "UINTR"),
        new(8, "AVX512VP2INTERSECT"), new(14, "SERIALIZE"), new(16, "TSXLDTRK"), new(18, "PCONFIG"),
        new(22, "AMXBF16"), new(23, "AVX512FP16"), new(24, "AMXTILE"), new(25, "AMXINT8")
    ];
    private static readonly Feature[] Leaf7Subleaf1Eax = [new(4, "AVXVNNI"), new(5, "AVX512BF16")];
    private static readonly Feature[] IntelLeaf7Subleaf1Eax =
    [
        new(0, "SHA512"), new(1, "SM3"), new(2, "SM4"), new(7, "CMPCCXADD"), new(18, "LKGS"),
        new(19, "WRMSRNS"), new(21, "AMXFP16"), new(22, "HRESET"), new(23, "AVXIFMA"), new(27, "MSRLIST")
    ];
    private static readonly Feature[] IntelLeaf7Subleaf1Ebx = [new(1, "PBNDKB")];
    private static readonly Feature[] IntelLeaf7Subleaf1Edx =
    [
        new(4, "AVXVNNIINT8"), new(5, "AVXNECONVERT"), new(10, "AVXVNNIINT16"),
        new(14, "PREFETCHI"), new(15, "USER_MSR"), new(19, "AVX10"),
        new(21, "APX_F"), new(23, "MWAIT")
    ];
    private static readonly Feature[] LeafDSubleaf1Eax =
        [new(0, "XSAVEOPT"), new(1, "XSAVEC"), new(2, "XGETBV1"), new(3, "XSAVES")];
    private static readonly Feature[] Extended1Ecx = [new(0, "LAHF_SAHF_64"), new(5, "LZCNT"), new(8, "PREFETCHW")];
    private static readonly Feature[] Extended1Edx = [new(27, "RDTSCP")];
    private static readonly Feature[] IntelExtended1Edx = [new(11, "SYSCALL_SYSRET_64")];
    private static readonly Feature[] AmdExtended1Ecx =
    [
        new(2, "SVM"), new(6, "SSE4A"), new(8, "PREFETCH"), new(11, "XOP"), new(12, "SKINIT_STGI"), new(15, "LWP"),
        new(16, "FMA4"), new(21, "TBM"), new(29, "MONITORX"), new(29, "MWAITX")
    ];
    private static readonly Feature[] AmdExtended1Edx =
        [new(11, "SYSCALL_SYSRET"), new(22, "MMXEXT"), new(30, "3DNOWEXT"), new(31, "3DNOW")];
    private static readonly Feature[] Extended8Ebx = [new(9, "WBNOINVD")];
    private static readonly Feature[] AmdExtended8Ebx =
    [
        new(0, "CLZERO"), new(3, "INVLPGB"), new(3, "TLBSYNC"), new(4, "RDPRU"),
        new(8, "MCOMMIT")
    ];
    private static readonly Feature[] AmdExtended21Eax = [new(20, "PREFETCHI")];
}
