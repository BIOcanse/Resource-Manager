using System.Runtime.Intrinsics.X86;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Cpu.Models;

namespace ResourceManager.ReadWrite.Windows.Cpu;

internal interface ICpuThreadCpuidAccess
{
    bool Select(CpuProcessorAddress address);
    CpuProcessorAddress Current();
    (int Eax, int Ebx, int Ecx, int Edx) Read(int leaf, int subleaf);
}

public static partial class WindowsCpuThreadCpuidReader
{
    /// <summary>Captures only the requested OS processor addresses; OS enablement is a separate source.</summary>
    public static DeviceInformationSnapshot ReadFields(IReadOnlyList<int> ids, ICollection<string> notes)
    {
        var captures = Read(ids, notes);
        return CpuInformationFieldProjection.Cpuid(captures,
            captures.Count == 0 || captures.Any(static capture => capture.Data is not null) ? DateTimeOffset.UtcNow : null);
    }

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]

    public static IReadOnlyList<CpuThreadCpuidInformation> Read(IReadOnlyList<int> ids, ICollection<string> notes)
        => X86Base.IsSupported ? Read(ids, new NativeAccess(), notes)
            : ids.Select(static id => new CpuThreadCpuidInformation(id, null)).ToArray();

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]

    internal static IReadOnlyList<CpuThreadCpuidInformation> Read(IReadOnlyList<int> ids, ICpuThreadCpuidAccess access,
        ICollection<string> notes)
    {
        if (ids.Any(static id => id < 0 || id / 64 > ushort.MaxValue)) throw new ArgumentOutOfRangeException(nameof(ids));
        var processors = ids.Distinct().Order().ToArray();
        if (processors.Length == 0) return [];
        var results = processors.Select(static id => new CpuThreadCpuidInformation(id, null)).ToArray();
        Exception? failure = null;
        // Group affinity changes are confined to this disposable thread, including on Windows 11 across-group defaults.
        var thread = new Thread(() =>
        {
            try
            {
                for (var index = 0; index < processors.Length; index++)
                {
                    var id = processors[index];
                    var address = new CpuProcessorAddress((ushort)(id / 64), (byte)(id % 64));
                    try
                    {
                        if (!access.Select(address) || access.Current() != address) continue;
                        var data = ReadRegisters(access.Read);
                        if (access.Current() == address) results[index] = new(id, data);
                    }
                    catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                        or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
                    { /* This processor is unavailable; the remaining OS addresses remain independently readable. */ }
                }
            }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true, Name = "Resource Manager CPUID capture" };
        try { thread.Start(); thread.Join(); }
        catch (Exception exception) when (exception is ThreadStateException or OutOfMemoryException)
        { failure = exception; }
        if (failure is not null)
        {
            notes.Add($"读取逐处理器 CPUID 失败：{failure.Message}");
            return processors.Select(static id => new CpuThreadCpuidInformation(id, null)).ToArray();
        }
        return results;
    }

    public static CpuThreadCpuidData ReadRegisters(Func<int, int, (int Eax, int Ebx, int Ecx, int Edx)> read)
    {
        var captured = new Dictionary<(int Leaf, int Subleaf), (int Eax, int Ebx, int Ecx, int Edx)>();
        (int Eax, int Ebx, int Ecx, int Edx) ReadOnce(int leaf, int subleaf)
        {
            if (!captured.TryGetValue((leaf, subleaf), out var value)) captured.Add((leaf, subleaf), value = read(leaf, subleaf));
            return value;
        }
        var identity = CpuIdentityInformationReader.ReadRegisters(ReadOnce, []);
        identity = identity with { Brand = string.IsNullOrWhiteSpace(identity.Brand) ? null : identity.Brand };
        var maximum = unchecked((uint)ReadOnce(0, 0).Eax);
        var extendedMaximum = unchecked((uint)ReadOnce(unchecked((int)0x80000000), 0).Eax);
        var hasIdentity = maximum is >= 1 and < 0x80000000;
        var leaf1 = hasIdentity ? ReadOnce(1, 0) : default;
        bool? intelDts = null, intelPackageThermal = null, intelPerformanceFeedback = null;
        bool? intelThermalMonitor = identity.Vendor == "GenuineIntel" && hasIdentity
            ? (leaf1.Edx & (1 << 22)) != 0 : null;
        bool? intelPowerLimit = null, intelHardwarePstates = null;
        if (identity.Vendor == "GenuineIntel" && maximum is >= 6 and < 0x80000000)
        {
            var thermal = ReadOnce(6, 0);
            intelDts = (thermal.Eax & 1) != 0;
            intelPackageThermal = (thermal.Eax & (1 << 6)) != 0;
            intelPerformanceFeedback = (thermal.Ecx & 1) != 0;
            intelPowerLimit = (thermal.Eax & (1 << 4)) != 0;
            intelHardwarePstates = (thermal.Eax & (1 << 7)) != 0;
        }
        ulong? supportedXcr0 = null; ulong? intelXss = null; uint? amdXss = null;
        ushort? baseMhz = null, maximumMhz = null, busMhz = null;
        uint? crystalHz = null;
        CpuTscCrystalClockRatio? tscRatio = null;
        if (identity.Vendor == "GenuineIntel" && maximum is >= 0x15 and < 0x80000000)
        {
            var clocks = ReadOnce(0x15, 0);
            if (clocks.Ecx != 0) crystalHz = unchecked((uint)clocks.Ecx);
            if (clocks.Eax != 0 && clocks.Ebx != 0)
                tscRatio = new(unchecked((uint)clocks.Ebx), unchecked((uint)clocks.Eax));
            if (maximum >= 0x16)
            {
                var specification = ReadOnce(0x16, 0);
                baseMhz = Frequency(specification.Eax);
                maximumMhz = Frequency(specification.Ebx);
                busMhz = Frequency(specification.Ecx);
            }
        }
        if (identity.Vendor is "GenuineIntel" or "AuthenticAMD" && maximum is >= 0xD and < 0x80000000
            && (unchecked((uint)leaf1.Ecx) & (1U << 26)) != 0)
        {
            var state = ReadOnce(0xD, 0);
            supportedXcr0 = (ulong)unchecked((uint)state.Edx) << 32 | unchecked((uint)state.Eax);
            var supervisor = ReadOnce(0xD, 1);
            if ((supervisor.Eax & 8) != 0)
            {
                if (identity.Vendor == "GenuineIntel") intelXss = (ulong)unchecked((uint)supervisor.Edx) << 32 | unchecked((uint)supervisor.Ecx);
                else amdXss = unchecked((uint)supervisor.Ecx); // AMD APM: D.1.EDX is undefined/reserved.
            }
        }
        byte? avx10Version = null;
        if (identity.Vendor == "GenuineIntel" && maximum is >= 0x24 and < 0x80000000
            && unchecked((uint)ReadOnce(7, 0).Eax) >= 1 && (ReadOnce(7, 1).Edx & (1 << 19)) != 0)
            avx10Version = unchecked((byte)ReadOnce(0x24, 0).Ebx);
        uint? x2apic = null; byte? coreType = null; uint? nativeModel = null; uint? amdApic = null;
        if (identity.Vendor == "GenuineIntel" && maximum < 0x80000000)
        {
            if (maximum >= 0x1F) x2apic = Apic(ReadOnce(0x1F, 0));
            if (x2apic is null && maximum >= 0x0B) x2apic = Apic(ReadOnce(0x0B, 0));
            if (maximum >= 0x1A)
            {
                var model = unchecked((uint)ReadOnce(0x1A, 0).Eax);
                if (model != 0) { coreType = (byte)(model >> 24); nativeModel = model & 0x00FFFFFF; }
            }
        }
        if (identity.Vendor == "AuthenticAMD" && extendedMaximum >= 0x8000001E
            && (unchecked((uint)ReadOnce(unchecked((int)0x80000001), 0).Ecx) & (1U << 22)) != 0)
            amdApic = unchecked((uint)ReadOnce(unchecked((int)0x8000001E), 0).Eax);
        var capabilities = ReadCapabilities(identity.Vendor, maximum, extendedMaximum, leaf1, ReadOnce);
        var amdTopology = ReadAmdExtendedTopology(identity.Vendor, extendedMaximum, ReadOnce);
        if (amdApic.HasValue && amdTopology?.ExtendedApicId != amdApic.Value)
            amdTopology = null;
        var registers = captured.OrderBy(static row => unchecked((uint)row.Key.Leaf)).ThenBy(static row => row.Key.Subleaf)
            .Select(static row => new CpuCpuidRegister(unchecked((uint)row.Key.Leaf), unchecked((uint)row.Key.Subleaf),
                unchecked((uint)row.Value.Eax), unchecked((uint)row.Value.Ebx), unchecked((uint)row.Value.Ecx), unchecked((uint)row.Value.Edx))).ToArray();
        return new(identity, hasIdentity ? unchecked((uint)leaf1.Eax) : null,
            hasIdentity ? (byte)(unchecked((uint)leaf1.Ebx) >> 24) : null, x2apic, coreType, nativeModel, amdApic, registers)
        { AmdExtendedTopology = amdTopology,
            IntelDtsSupported = intelDts, IntelPackageThermalSupported = intelPackageThermal,
            IntelPerformanceFeedbackSupported = intelPerformanceFeedback,
            IntelThermalMonitorSupported = intelThermalMonitor, IntelPowerLimitNotificationSupported = intelPowerLimit,
            IntelHardwarePstatesSupported = intelHardwarePstates,
            IntelProcessorBaseFrequencyDeclaredMhz = baseMhz, IntelProcessorMaximumFrequencyDeclaredMhz = maximumMhz,
            IntelBusReferenceFrequencyDeclaredMhz = busMhz, IntelNominalCoreCrystalFrequencyDeclaredHz = crystalHz,
            IntelTscToCoreCrystalRatioDeclared = tscRatio,
            SupportedXcr0StateMask = supportedXcr0, IntelSupportedXssStateMask = intelXss,
            AmdSupportedXssStateMask = amdXss, IntelAvx10Version = avx10Version,
            PhysicalAddressWidthDeclaredBits = capabilities.PhysicalAddressWidthDeclaredBits,
            LinearAddressWidthDeclaredBits = capabilities.LinearAddressWidthDeclaredBits,
            AmdNestedGuestPhysicalAddressWidthBits = capabilities.AmdNestedGuestPhysicalAddressWidthBits,
            IntelVmxSupported = capabilities.IntelVmxSupported, IntelSmxSupported = capabilities.IntelSmxSupported,
            AmdSvmSupported = capabilities.AmdSvmSupported, AmdSvmRevision = capabilities.AmdSvmRevision,
            AmdSvmAsidCountDeclared = capabilities.AmdSvmAsidCountDeclared, AmdSvmFeatures = capabilities.AmdSvmFeatures };

        static uint? Apic((int Eax, int Ebx, int Ecx, int Edx) value)
            => (value.Ebx & 0xFFFF) != 0 && (value.Ecx & 0xFF00) != 0 ? unchecked((uint)value.Edx) : null;
        static ushort? Frequency(int register) => unchecked((ushort)register) is var value && value != 0 ? value : null;
    }
}
