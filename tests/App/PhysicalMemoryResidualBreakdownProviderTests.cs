using System.Buffers.Binary;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

public sealed class PhysicalMemoryResidualBreakdownProviderTests
{
    internal static PhysicalMemoryResidualCapture Fixture() => new(new(100, 200, 30, 20, 900, 800),
        [new(0x4346744E, 80)], 10, "ready");
    internal static ResourceResidualBreakdownRequest Request(double bytes = 1000) => new("memory.usage", bytes, 2000, bytes / 20, new HashSet<int>());
    internal static IResourceResidualBreakdownProvider Provider(PhysicalMemoryResidualCapture capture,
        Action<string>? state = null) => new PhysicalMemoryResidualBreakdownProvider(capture,
            new StaticResourceResidualBreakdownProvider(), state ?? (static _ => { }));

    [Fact]
    public void MeasuredLeavesPartitionResidualWithoutCountingPoolParentTwice()
    {
        var rows = Provider(Fixture()).CreateResidualSegments(Request());
        Assert.Equal(1000, rows.Sum(row => row.Value));
        Assert.Equal(650, Assert.Single(rows, row => row.AttributionKind == PhysicalMemoryResidualKinds.Unclassified).Value);
        Assert.DoesNotContain(rows, row => row.AttributionKind == PhysicalMemoryResidualKinds.NonPagedPool);
        Assert.Equal(100, rows.Where(row => row.AttributionKind.StartsWith(PhysicalMemoryResidualKinds.NonPagedPool + "/")).Sum(row => row.Value));
        var tag = Assert.Single(rows, row => row.AttributionKind == PhysicalMemoryResidualKinds.PoolTagPrefix + "4346744E");
        Assert.Equal("NtFC", tag.Name);
        Assert.Equal(80, tag.Value);
        Assert.Equal(10, Assert.Single(rows, row => row.AttributionKind == PhysicalMemoryResidualKinds.OtherPoolTags).Value);
        Assert.Equal(10, Assert.Single(rows, row => row.AttributionKind == PhysicalMemoryResidualKinds.UnclassifiedPool).Value);
        Assert.All(rows, row => { Assert.Equal(-1, row.ProcessId); Assert.Null(row.ProcessStartKey); Assert.Equal(row.Value / 20, row.SystemPercent); Assert.Equal(row.Value / 10, row.SoftwarePercent); });
    }

    [Fact]
    public void OverlappingAllocatedPoolAndCacheNeverEnterPartition()
    {
        var small = Fixture();
        var inflated = small with { Counters = small.Counters! with { PagedPoolBytes = ulong.MaxValue, ResidentCacheBytes = ulong.MaxValue } };
        Assert.Equal(Provider(small).CreateResidualSegments(Request()), Provider(inflated).CreateResidualSegments(Request()));
    }

    [Theory]
    [InlineData(349)]
    [InlineData(1)]
    public void IncompatibleResidualWithholdsAllDetailsInsteadOfScaling(double residual)
    {
        var states = new List<string>();
        var row = Assert.Single(Provider(Fixture(), states.Add).CreateResidualSegments(Request(residual)));
        Assert.Equal(PhysicalMemoryResidualKinds.Unclassified, row.AttributionKind);
        Assert.Equal(residual, row.Value);
        Assert.Equal(["components-exceed-residual"], states);
    }

    [Fact]
    public void UnavailableCaptureHasOneExplicitRemainderAndNoEstimatedComponents()
    {
        var row = Assert.Single(Provider(new(null, null, 0, "performance:unavailable")).CreateResidualSegments(Request()));
        Assert.Equal(PhysicalMemoryResidualKinds.Unclassified, row.AttributionKind);
        Assert.Equal(1000, row.Value);
        Assert.Empty(row.Name);
        var partial = Provider(new(new(100, null, null, 0), null, 0, "pool-tags:unavailable")).CreateResidualSegments(Request());
        Assert.Equal(100, Assert.Single(partial, row => row.AttributionKind == PhysicalMemoryResidualKinds.NonPagedPool).Value);
        Assert.Equal(900, Assert.Single(partial, row => row.AttributionKind == PhysicalMemoryResidualKinds.Unclassified).Value);
        Assert.Equal(2, partial.Count);
    }

    [Fact]
    public void TagQueryExceedingPoolWithholdsTagsAndKeepsExactPoolCounter()
    {
        var fixture = Fixture() with { Tags = [new(0x4346744E, 101)] };
        var states = new List<string>();
        var rows = Provider(fixture, states.Add).CreateResidualSegments(Request());
        Assert.Equal(100, Assert.Single(rows, row => row.AttributionKind == PhysicalMemoryResidualKinds.NonPagedPool).Value);
        Assert.DoesNotContain(rows, row => row.AttributionKind.StartsWith(PhysicalMemoryResidualKinds.PoolTagPrefix));
        Assert.Equal(["pool-tags-exceed-nonpaged-pool"], states);
    }

    [Fact]
    public void CapturePublishesFreshEmptyValueAndLogsOnlyStateChangesWhileReadsDoNoWork()
    {
        var calls = 0;
        var logger = new StateLogger();
        var owner = new WindowsResourceResidualBreakdownProvider(new StaticResourceResidualBreakdownProvider(), logger,
            _ => { calls++; return calls == 1 ? Fixture() : new(null, null, 0, "performance:denied"); });
        var earlier = owner.CaptureResiduals(default);
        Assert.Equal(1, calls);
        for (var index = 0; index < 3; index++) owner.CreateResidualSegments(Request());
        Assert.Equal(1, calls);
        var failed = owner.CaptureResiduals(default);
        Assert.Single(failed.CreateResidualSegments(Request()));
        var events = logger.States.Count;
        owner.CaptureResiduals(default).CreateResidualSegments(Request());
        Assert.Equal(events, logger.States.Count);
        Assert.True(earlier.CreateResidualSegments(Request()).Count > 1); // immutable earlier window
        Assert.Equal(3, calls);
    }

    [Fact]
    public void OtherMetricsRetainTheirExistingResidualAndCancellationPreventsNativeCalls()
    {
        var row = Assert.Single(Provider(Fixture()).CreateResidualSegments(Request() with { MetricId = "gpu.0.vram" }));
        Assert.Equal(ResourceProcessAttributionKinds.SystemResidual, row.AttributionKind);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new WindowsPhysicalMemoryResidualReader().Capture(canceled.Token));
        Assert.Empty(Provider(Fixture()).CreateResidualSegments(Request(0)));
    }

    [Fact]
    public void NativeLayoutsReadResidentFieldsAndBoundTagRanking()
    {
        var performance = new byte[512];
        foreach (var offset in new[] {112,116,140,164,168,172})
            BinaryPrimitives.WriteUInt32LittleEndian(performance.AsSpan(offset), (uint)offset);
        var counters = WindowsPhysicalMemoryResidualReader.DecodePerformance(performance);
        Assert.Equal(168UL * (ulong)Environment.SystemPageSize, counters.ResidentPagedPoolBytes);
        Assert.Equal(116UL * (ulong)Environment.SystemPageSize, counters.NonPagedPoolBytes);
        var tags = new byte[8 + 40 * 12];
        BinaryPrimitives.WriteUInt32LittleEndian(tags, 12);
        for (var index = 0; index < 12; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(tags.AsSpan(8 + 40 * index), (uint)index);
            BinaryPrimitives.WriteUInt64LittleEndian(tags.AsSpan(8 + 40 * index + 16), ulong.MaxValue); // paged allocation must be ignored
            BinaryPrimitives.WriteUInt64LittleEndian(tags.AsSpan(8 + 40 * index + 32), (ulong)(index + 1));
        }
        var result = WindowsPhysicalMemoryResidualReader.DecodeTags(tags);
        Assert.Equal(8, result.Tags!.Count);
        Assert.Equal(Enumerable.Range(5,8).Reverse().Select(i => (ulong)i), result.Tags.Select(tag => tag.NonPagedBytes));
        Assert.Equal(10UL, result.OtherBytes);
        Assert.Null(WindowsPhysicalMemoryResidualReader.DecodeTags(tags.AsSpan(0, tags.Length - 1)).Tags);
    }

    [ElevatedMemoryFact]
    public void RealMachineCountersAndPoolTagsProduceBoundedMeasuredParts()
    {
        var capture = new WindowsPhysicalMemoryResidualReader().Capture(default);
        Assert.NotNull(capture.Counters);
        Assert.NotNull(capture.Tags);
        Assert.Equal("ready", capture.State);
        var measured = capture.Counters.NonPagedPoolBytes!.Value + capture.Counters.ResidentPagedPoolBytes!.Value
            + capture.Counters.ResidentCodeBytes!.Value + capture.Counters.ResidentDriverBytes!.Value;
        var rows = Provider(capture).CreateResidualSegments(Request(measured + 4096));
        Assert.Equal(measured + 4096, rows.Sum(row => row.Value));
        Assert.True(rows.Count > 1);
    }

    private sealed class StateLogger : ILogger<WindowsResourceResidualBreakdownProvider>
    {
        internal List<string> States { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
            => States.Add(formatter(state, error));
    }
}

public sealed class ElevatedMemoryFactAttribute : FactAttribute
{
    public ElevatedMemoryFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) { Skip = "Windows memory APIs required."; return; }
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            Skip = "Elevated Windows token required for real memory API smoke test.";
    }
}
