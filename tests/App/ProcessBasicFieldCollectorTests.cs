using System.Runtime.InteropServices;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Endpoints.Transport;
using ResourceManager.App.Infrastructure.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

public sealed partial class ProcessBasicFieldCollectorTests
{
    [Fact]
    public void OfficialSdkMemoryAndIoFramesMatchManagedLayoutAndKeepRawIntegers()
    {
        Assert.Equal(typeof(ResourceManager.ReadWrite.ReadWriteCore).Assembly, typeof(WindowsProcessBasicMetricReader).Assembly);
        Assert.Equal(typeof(WindowsProcessBasicMetricReader).Assembly, typeof(ProcessMetricFieldSnapshot).Assembly);
        using var sdk = Sdk(); var root = sdk.RootElement;
        Assert.Equal(IntPtr.Size, root.GetProperty("pointerSize").GetInt32()); Assert.Equal(80, root.GetProperty("exSize").GetInt32());
        Assert.Equal(root.GetProperty("ex2Size").GetInt32(), Marshal.SizeOf<WindowsProcessResidentMemory.ProcessMemoryCountersEx2>());
        Assert.Equal(root.GetProperty("privateWorkingSetOffset").GetInt32(), Marshal.OffsetOf<WindowsProcessResidentMemory.ProcessMemoryCountersEx2>("PrivateWorkingSetSize").ToInt32());
        Assert.Equal(root.GetProperty("sharedCommitOffset").GetInt32(), Marshal.OffsetOf<WindowsProcessResidentMemory.ProcessMemoryCountersEx2>("SharedCommitUsage").ToInt32());
        Assert.Equal(root.GetProperty("ioSize").GetInt32(), Marshal.SizeOf<WindowsProcessBasicMetricNativeApi.IoCounters>());
        var memory = Structure<WindowsProcessResidentMemory.ProcessMemoryCountersEx2>(Convert.FromHexString(root.GetProperty("memoryHex").GetString()!));
        var io = Structure<WindowsProcessBasicMetricNativeApi.IoCounters>(Convert.FromHexString(root.GetProperty("ioHex").GetString()!));
        Assert.Equal(9007199254740993UL, (ulong)memory.WorkingSetSize); Assert.Equal(ulong.MaxValue, (ulong)memory.PrivateUsage);
        Assert.Equal(3UL, (ulong)memory.PrivateWorkingSetSize); Assert.Equal(9007199254740997UL, memory.SharedCommitUsage);
        Assert.Equal(ulong.MaxValue, io.OtherTransferCount); Assert.Equal(0UL, io.WriteTransferCount);
    }

    [Fact]
    public async Task AllBasicFieldsShareOneIdentityVerifiedReadAndPreserveCumulativeAndMemoryMeanings()
    {
        var api = new Api(); using var fixture = Fixture(api); var sample = await fixture.Sampler.CaptureSnapshotAsync(Request(), CancellationToken.None);
        Assert.Equal(34, ProcessMetricFieldRegistry.Definitions.Count); Assert.Equal(10, sample.ProcessFields!.Fields.Count);
        Assert.Equal(0, api.ImageCalls + api.SidCalls + api.ParentCalls);
        Assert.Equal(9.75, Value(sample, Names[0]).NumericValue); Assert.Null(Value(sample, Names[0]).IntegerValue);
        Assert.Equal(MetricInteger.Parse("9007199254740993"), Value(sample, Names[1]).IntegerValue); Assert.Equal(MetricInteger.Parse("3"), Value(sample, Names[2]).IntegerValue);
        Assert.Equal(MetricInteger.Parse("9007199254740997"), Value(sample, Names[3]).IntegerValue); Assert.Equal(MetricInteger.Parse("18446744073709551615"), Value(sample, Names[4]).IntegerValue);
        Assert.Equal(MetricInteger.Parse("9007199254740993"), Value(sample, Names[5]).IntegerValue); Assert.Equal(MetricInteger.Parse("0"), Value(sample, Names[6]).IntegerValue);
        Assert.Equal(MetricInteger.Parse("18446744073709551615"), Value(sample, Names[7]).IntegerValue); Assert.Equal(MetricInteger.Parse("55340232221128654845"), Value(sample, Names[8]).IntegerValue);
        Assert.Equal(MetricInteger.Parse("0"), Value(sample, Names[9]).IntegerValue); Assert.Empty(sample.Bars);
        Assert.Equal(1, api.Opens); Assert.Equal(1, api.TimeCalls); Assert.Equal(1, api.MemoryCalls); Assert.Equal(1, api.IoCalls); Assert.Equal(1, api.HandleCalls); Assert.Equal(1, api.Closes);
        var source = Assert.Single(sample.ProcessFields.Sources); Assert.Equal("windows.process.query", source.SourceId); Assert.Null(source.WindowMilliseconds);
        Assert.Equal(sample.ProcessFields.CapturedAt, source.CapturedAt); Assert.Equal(Birth.ToString(), Assert.Single(sample.ProcessFields.Processes).CreationWindowsFileTime100Ns);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)] [InlineData(1, 1, 0, 0)] [InlineData(2, 1, 0, 0)]
    [InlineData(3, 1, 0, 0)] [InlineData(4, 1, 0, 0)] [InlineData(5, 0, 1, 0)]
    [InlineData(6, 0, 1, 0)] [InlineData(7, 0, 1, 0)] [InlineData(8, 0, 1, 0)] [InlineData(9, 0, 0, 1)]
    public async Task ExplicitFieldSelectionDoesNotReadOtherCounterCategories(int field, int memory, int io, int handles)
    {
        var api = new Api(); using var fixture = Fixture(api);
        var sample = await fixture.Sampler.CaptureSnapshotAsync(Request(Names[field]), CancellationToken.None);
        Assert.Single(sample.ProcessFields!.Fields); Assert.Single(Assert.Single(sample.ProcessFields.Processes).Values);
        Assert.Equal(memory, api.MemoryCalls); Assert.Equal(io, api.IoCalls); Assert.Equal(handles, api.HandleCalls);
        if (memory != 0) Assert.Equal(field == 2, Assert.Single(api.PrivateRequests));
        Assert.Equal(SamplingDatasetIds.ProcessBasicMetrics, SamplingDatasetIds.ForProcessMetric(Names[field]));
        Assert.Equal(new[] { SamplingDatasetIds.ProcessBasicMetrics, SamplingDatasetIds.ProcessInventory }.Order(), WindowsResourceBreakdownSampler.GetSubscriptionDatasetIds(Request(Names[field])).Order());
        Assert.False(WindowsResourceBreakdownSampler.RequiresProcessAttribution(Request(Names[field])));
    }

    [Theory]
    [InlineData("open")] [InlineData("invalid-handle")] [InlineData("times-before")]
    [InlineData("identity-before")] [InlineData("close")] [InlineData("throw-close")]
    public async Task IdentityTimesOrHandleFailureEmptiesOnlyThisProcessValuesAndReleasesAcquiredHandle(string fault)
    {
        var api = new Api { Fault = fault }; using var fixture = Fixture(api);
        var sample = await fixture.Sampler.CaptureSnapshotAsync(Request(), CancellationToken.None);
        Assert.Equal(10, Assert.Single(sample.ProcessFields!.Processes).Values.Count);
        Assert.All(sample.ProcessFields.Processes[0].Values, value => { Assert.Null(value.IntegerValue); Assert.Null(value.NumericValue); });
        Assert.Equal(fault is "open" or "invalid-handle" ? 0 : 1, api.Closes);
        if (fault is "times-before" or "identity-before") Assert.Equal(0, api.MemoryCalls + api.IoCalls + api.HandleCalls);
    }

    [Theory]
    [InlineData("memory", 1)] [InlineData("throw-memory", 1)] [InlineData("io", 5)]
    [InlineData("throw-io", 5)] [InlineData("handles", 9)] [InlineData("throw-handles", 9)]
    public async Task CounterFunctionFailureDoesNotClearIndependentTimeMemoryIoOrHandleValues(string fault, int failed)
    {
        var api = new Api { Fault = fault }; using var fixture = Fixture(api);
        var sample = await fixture.Sampler.CaptureSnapshotAsync(Request(), CancellationToken.None);
        Assert.Null(Value(sample, Names[failed]).IntegerValue); Assert.Equal(9.75, Value(sample, Names[0]).NumericValue);
        if (failed != 1) Assert.Equal(MetricInteger.Parse("3"), Value(sample, Names[2]).IntegerValue);
        if (failed != 5) Assert.Equal(MetricInteger.Parse("55340232221128654845"), Value(sample, Names[8]).IntegerValue);
        if (failed != 9) Assert.Equal(MetricInteger.Parse("0"), Value(sample, Names[9]).IntegerValue);
        Assert.Equal(1, api.Closes);
    }

    [Fact]
    public async Task ZeroUnsupportedAndInconsistentPrivateResidentAreNotInventedFromCommitOrDifferences()
    {
        var api = new Api { MemoryValue = new(0, 0, 0, 0), IoValue = new(0, 0, 0, 0, 0, 0), Kernel = 0, User = 0 };
        using var fixture = Fixture(api); var zero = await fixture.Sampler.CaptureSnapshotAsync(Request(), CancellationToken.None);
        Assert.Equal(0, Value(zero, Names[0]).NumericValue); Assert.All(zero.ProcessFields!.Processes[0].Values.Skip(1), value => Assert.Equal(MetricInteger.Parse("0"), value.IntegerValue));
        api.MemoryValue = new(7, 50, 70, null); var unsupported = await fixture.Sampler.CaptureSnapshotAsync(Request(), CancellationToken.None);
        Assert.Null(Value(unsupported, Names[2]).IntegerValue); Assert.Equal(MetricInteger.Parse("7"), Value(unsupported, Names[1]).IntegerValue); Assert.Equal(MetricInteger.Parse("50"), Value(unsupported, Names[3]).IntegerValue);
        api.MemoryValue = new(7, 50, 70, 8); var inconsistent = await fixture.Sampler.CaptureSnapshotAsync(Request(), CancellationToken.None);
        Assert.Null(Value(inconsistent, Names[2]).IntegerValue); Assert.Equal(MetricInteger.Parse("70"), Value(inconsistent, Names[4]).IntegerValue);
        var reader = new WindowsProcessBasicMetricReader(api); var count = api.Opens;
        Assert.Equal(ProcessBasicMetricObservation.Unavailable, reader.Read(Environment.ProcessId, (long)Birth, ProcessMetricReadMask.None));
        Assert.Equal(ProcessBasicMetricObservation.Unavailable, reader.Read(-1, (long)Birth, ProcessMetricReadMask.Memory));
        Assert.Equal(ProcessBasicMetricObservation.Unavailable, reader.Read(Environment.ProcessId, 0, ProcessMetricReadMask.Memory)); Assert.Equal(count, api.Opens);
    }

    [Fact]
    public async Task EmptyInventoryClearsObjectsWhileSourceAndDefinitionsRemainCurrent()
    {
        var api = new Api(); using var fixture = Fixture(api, inventory: () => []);
        var sample = await fixture.Sampler.CaptureSnapshotAsync(Request(), CancellationToken.None);
        Assert.Empty(sample.ProcessFields!.Processes); Assert.Equal(10, sample.ProcessFields.Fields.Count); Assert.NotNull(sample.ProcessFields.CapturedAt); Assert.Equal(0, api.Opens);
    }
}
