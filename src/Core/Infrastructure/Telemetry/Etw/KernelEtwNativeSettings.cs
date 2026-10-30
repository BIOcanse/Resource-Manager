using System.Diagnostics;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

internal static class KernelEtwNativeSettings
{
    private const string Consumer = "KernelEtwNativeSettings.Compile";
    internal static CompiledKernelEtwLimits ProductDefaults { get; } = ReadEmbeddedDefaults();

    internal static CompiledKernelEtwLimits Compile(FreedomPointCompilation freedom)
    {
        var value = freedom.Consume<CompiledKernelEtwLimits>(BackendFreedomPointPaths.KernelEtwLimits, Consumer);
        if (!value.IsPublished) throw new InvalidDataException("Kernel ETW owner limits are invalid.");
        return value;
    }

    private static CompiledKernelEtwLimits ReadEmbeddedDefaults()
    {
        var profile = StrictHostManagerProfileLoader.LoadBytes(
            StrictHostManagerProfileLoader.ReadExpandedDefaultBytes(), "embedded/HostManager.default.json").Profile;
        return Compile(FreedomPointRegistry.LoadEmbedded().BeginCompilation(profile,
            new CompiledCpuScoringPlan(1d, CompiledCpuCoreWeights.Empty)));
    }

    internal static NativeKernelEtwSessionConfig CreateConfiguration(
        CompiledKernelEtwLimits limits, CompiledCpuCoreResidencyPlan cpu, uint processorCount)
    {
        if (!limits.IsPublished || !cpu.IsPublished || processorCount == 0)
            throw new InvalidOperationException("Kernel ETW requires published limits, residency plan and processor topology.");
        return new()
        {
            ByteCap = limits.RegionBytes, DiskByteCap = limits.DiskBytes,
            NetworkByteCap = limits.NetworkBytes, FileByteCap = limits.FileBytes,
            HistoryByteCap = limits.HistoryBytes,
            Residency = new()
            {
                QpcFrequency = Stopwatch.Frequency,
                WindowMilliseconds = checked((uint)cpu.ObservationWindowMilliseconds),
                BucketMilliseconds = checked((uint)cpu.ExecutionTimeSource.BucketWidthMilliseconds),
                MaximumPairs = checked((uint)cpu.ExecutionTimeSource.MaximumActivePairs),
                MaximumCells = limits.MaximumCells, MaximumProcesses = limits.MaximumProcesses,
                MaximumThreads = limits.MaximumThreads, MaximumProcessors = processorCount,
                MaximumNameBytes = limits.MaximumProcessNameBytes, ByteCap = limits.CpuBytes
            },
            Interrupts = new()
            {
                MaximumModules = limits.MaximumModules, MaximumPathUnits = limits.MaximumPathUnits,
                MaximumIdentities = limits.MaximumModuleIdentities,
                MaximumSlices = limits.MaximumInterruptSlices, MaximumSliceDrivers = limits.MaximumInterruptDrivers,
                ByteCap = limits.InterruptBytes
            },
            MaximumDiskPairs = limits.MaximumDiskPairs, MaximumDisks = limits.MaximumDisks,
            MaximumCachedProcesses = limits.MaximumCachedProcesses,
            MaximumNetworkEvents = limits.MaximumNetworkEvents, MaximumNetworkProcesses = limits.MaximumNetworkProcesses,
            MaximumFileEntries = limits.MaximumFileEntries, MaximumFileVersions = limits.MaximumFileVersions,
            MaximumHistoryEntries = limits.MaximumHistoryEntries, MaximumPathUnits = limits.MaximumPathUnits,
            MaximumNamespaceEntries = limits.MaximumNamespaceEntries, MaximumCommands = limits.MaximumCommands,
            MaximumCommandNameBytes = limits.MaximumCommandNameBytes, PublicationBanks = limits.PublicationBanks,
            WindowMilliseconds = limits.IoWindowMilliseconds, RetentionMilliseconds = limits.IoRetentionMilliseconds,
            DiskBucketMilliseconds = limits.DiskBucketMilliseconds,
            MaximumPendingEvents = limits.MaximumPendingEvents,
            MaximumPendingPathUnits = limits.MaximumPendingPathUnits,
            MaximumRendererProcesses = limits.MaximumRendererProcesses
        };
    }
}
