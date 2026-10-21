using System.Runtime.InteropServices;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.ReadWrite.Windows.Cpu;

public static partial class WindowsCpuOsFeatureReader
{
    public static DeviceInformationSnapshot ReadFields()
    {
        var features = Read();
        var enabledStateMask = ReadEnabledStateMask();
        return CpuInformationFieldProjection.OsFeatures(features, enabledStateMask,
            features is null && enabledStateMask is null ? null : DateTimeOffset.UtcNow);
    }

    private static readonly (uint Flag, string Name)[] InstructionFlags =
        [(6, "SSE"), (10, "SSE2"), (13, "SSE3"), (36, "SSSE3"), (37, "SSE4.1"), (38, "SSE4.2"),
         (39, "AVX"), (40, "AVX2"), (41, "AVX512F")];

    public static IReadOnlyList<string>? Read()
    {
        try { return Read(IsProcessorFeaturePresent); }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        { return null; }
    }

    internal static IReadOnlyList<string> Read(Func<uint, bool> present)
        => InstructionFlags.Where(flag => present(flag.Flag)).Select(static flag => flag.Name).ToArray();

    public static ulong? ReadEnabledStateMask()
        => RuntimeInformation.ProcessArchitecture is Architecture.X86 or Architecture.X64
            ? ReadEnabledStateMask(GetEnabledXStateFeatures) : null;

    internal static ulong? ReadEnabledStateMask(Func<ulong> read)
    {
        try { return read(); }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        { return null; }
    }

    [LibraryImport("kernel32.dll")]
    private static partial ulong GetEnabledXStateFeatures();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessorFeaturePresent(uint processorFeature);
}
