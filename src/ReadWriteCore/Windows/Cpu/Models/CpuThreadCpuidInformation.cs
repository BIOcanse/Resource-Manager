namespace ResourceManager.ReadWrite.Windows.Cpu.Models;

public sealed record CpuThreadCpuidInformation(int LogicalProcessorId, CpuThreadCpuidData? Data)
{
    public string DeviceIdentity => $"windows-cpu-thread|{LogicalProcessorId / 64}:{LogicalProcessorId % 64}";
}

public sealed record CpuThreadCpuidData(CpuIdentityInformation Identity,
    uint? Signature, byte? InitialApicId, uint? IntelX2ApicId,
    byte? IntelHardwareCoreType, uint? IntelNativeModelId, uint? AmdExtendedApicId,
    IReadOnlyList<CpuCpuidRegister> IdentityRegisters)
{
    public CpuAmdExtendedTopology? AmdExtendedTopology { get; init; }
    public bool? IntelDtsSupported { get; init; }
    public bool? IntelPackageThermalSupported { get; init; }
    public bool? IntelPerformanceFeedbackSupported { get; init; }
    public bool? IntelThermalMonitorSupported { get; init; }
    public bool? IntelPowerLimitNotificationSupported { get; init; }
    public bool? IntelHardwarePstatesSupported { get; init; }
    public ushort? IntelProcessorBaseFrequencyDeclaredMhz { get; init; }
    public ushort? IntelProcessorMaximumFrequencyDeclaredMhz { get; init; }
    public ushort? IntelBusReferenceFrequencyDeclaredMhz { get; init; }
    public uint? IntelNominalCoreCrystalFrequencyDeclaredHz { get; init; }
    public CpuTscCrystalClockRatio? IntelTscToCoreCrystalRatioDeclared { get; init; }
    public ulong? SupportedXcr0StateMask { get; init; }
    public ulong? IntelSupportedXssStateMask { get; init; }
    public uint? AmdSupportedXssStateMask { get; init; }
    public byte? IntelAvx10Version { get; init; }
    public byte? PhysicalAddressWidthDeclaredBits { get; init; }
    public byte? LinearAddressWidthDeclaredBits { get; init; }
    public byte? AmdNestedGuestPhysicalAddressWidthBits { get; init; }
    public bool? IntelVmxSupported { get; init; }
    public bool? IntelSmxSupported { get; init; }
    public bool? AmdSvmSupported { get; init; }
    public byte? AmdSvmRevision { get; init; }
    public uint? AmdSvmAsidCountDeclared { get; init; }
    public IReadOnlyList<string>? AmdSvmFeatures { get; init; }
}

public sealed record CpuCpuidRegister(uint Leaf, uint Subleaf, uint Eax, uint Ebx, uint Ecx, uint Edx);

public sealed record CpuTscCrystalClockRatio(uint Numerator, uint Denominator);
