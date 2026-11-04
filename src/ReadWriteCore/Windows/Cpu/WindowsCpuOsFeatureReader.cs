using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Native.Cpu;

namespace ResourceManager.ReadWrite.Windows.Cpu;

public static partial class WindowsCpuOsFeatureReader
{
    public static DeviceInformationSnapshot ReadFields()
    {
        var observation = NativeCpuOsFeatures.Read(NativeCpuOsFeatures.Probe.Features | NativeCpuOsFeatures.Probe.XState);
        return CpuInformationFieldProjection.OsFeatures(observation.Features, observation.EnabledXStateMask,
            observation.Features is null && observation.EnabledXStateMask is null ? null : DateTimeOffset.UtcNow);
    }

    [RuntimeRead(ReadVolatilityKind.Fixed)]
    public static IReadOnlyList<string>? Read()
        => NativeCpuOsFeatures.Read(NativeCpuOsFeatures.Probe.Features).Features;

    internal static IReadOnlyList<string> Read(Func<uint, bool> present)
        => NativeCpuOsFeatures.ReadFeatures(present);

    [RuntimeRead(ReadVolatilityKind.Fixed)]
    public static ulong? ReadEnabledStateMask()
        => NativeCpuOsFeatures.Read(NativeCpuOsFeatures.Probe.XState).EnabledXStateMask;

    internal static ulong? ReadEnabledStateMask(Func<ulong> read)
        => NativeCpuOsFeatures.ReadEnabledStateMask(read);
}
