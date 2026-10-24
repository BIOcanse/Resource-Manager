using System.Runtime.CompilerServices;
using ResourceManager.ReadWrite.Native;

namespace Resource_Manager_APP.Tests;

public sealed partial class NativeMetricSnapshotAbiTests
{
    [Fact]
    public void CatalogGrowthRetiresScratchAtHighWaterAndShrinkReusesItWithoutLosingCurrentValues()
    {
        var configuration = CreateConfiguration();
        var regions = new List<MetricScratchRecording>();
        using var session = new NativeMetricSnapshotSession(in configuration, (length, alignment) =>
        {
            var region = new MetricScratchRecording(length, alignment);
            regions.Add(region);
            return region;
        });
        var resizeLogs = new List<string>();
        session.ResizeLog = resizeLogs.Add;
        var emptyResident = session.Capacity.ResidentByteCount;
        Assert.Equal(8UL, session.Capacity.ScratchByteCount);
        var calls = new ScratchCalls(session);
        var smallResident = session.Capacity.ResidentByteCount;
        var smallScratch = session.Capacity.ScratchByteCount;
        Assert.True(smallResident > emptyResident);
        Assert.True(smallScratch < 1024);
        calls.Begin(); calls.Submit(42);
        Assert.Equal(NativeMetricSnapshotStatus.Ok, session.FinalizeCompletion(in calls.Finalize));
        var smallRegion = regions[^1];

        ScratchCalls.ReplaceScratchCatalog(session, 8, 2, 10);
        Assert.True(session.Capacity.ScratchByteCount > smallScratch);
        Assert.True(session.Capacity.ResidentByteCount > smallResident);
        Assert.Equal(42UL, calls.Read().ValueBits);
        Plan(2, 11, 2);
        var largeRegion = regions[^1];
        Assert.NotSame(smallRegion, largeRegion);
        Assert.Equal(1, smallRegion.Retires);
        Assert.Equal(largeRegion.Pins, largeRegion.Unpins);

        ScratchCalls.ReplaceScratchCatalog(session, 1, 3, 12);
        Assert.Equal(smallResident, session.Capacity.ResidentByteCount);
        Assert.Equal(smallScratch, session.Capacity.ScratchByteCount);
        Plan(3, 13, 3);
        Assert.Same(largeRegion, regions[^1]);
        Assert.Equal(3, regions.Count); // Empty identity, small catalog, large high water.
        Assert.Equal(42UL, calls.Read().ValueBits);
        Assert.Equal(3, resizeLogs.Count(static line => line.Contains("workArea=metric-active-bank ")));
        Assert.Equal(3, resizeLogs.Count(static line => line.Contains("workArea=metric-staging-bank ")));
        Assert.Equal(2, resizeLogs.Count(static line => line.Contains("workArea=metric-completion-scratch ")));
        Assert.All(resizeLogs, static line =>
        {
            Assert.Contains("oldRows=", line);
            Assert.Contains("newRows=", line);
            Assert.Contains("reason=", line);
        });

        void Plan(ulong catalog, ulong epoch, ulong planEpoch)
        {
            var input = new NativeMetricSnapshotPlanInput
            {
                AbiVersion = NativeMetricSnapshotAbi.Version,
                StructSize = (uint)Unsafe.SizeOf<NativeMetricSnapshotPlanInput>(),
                ConfigurationGeneration = 1, CatalogGeneration = catalog,
                OperationEpoch = epoch, PlanEpoch = planEpoch, CommandAtMilliseconds = 4000,
                RequestedMetricCount = 1, SourceModeCount = 1,
                SourcePlanCapacity = 1, MetricPlanCapacity = 1
            };
            Assert.Equal(NativeMetricSnapshotStatus.Ok, session.Plan(in input,
                [new() { StructSize = (uint)Unsafe.SizeOf<NativeMetricSnapshotPlanMetricInput>(), MetricHandle = 1 }],
                [new() { StructSize = (uint)Unsafe.SizeOf<NativeMetricSnapshotSourceModeInput>(), SourceHandle = 1,
                    CapabilityGeneration = 1, ZoneMode = (uint)NativeMetricSnapshotZoneMode.Normal,
                    Availability = (uint)NativeMetricSnapshotSourceAvailability.Available }],
                out _, new NativeMetricSnapshotSourcePlanOutput[1], new NativeMetricSnapshotMetricPlanOutput[1]));
        }
    }
}
