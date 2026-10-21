using System.ComponentModel;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.CpuTopology;
using T = ResourceManager.ReadWrite.Windows.Cpu.WindowsCpuOsTopologyReader.ProcessorTimes;

namespace Resource_Manager_APP.Tests;

public sealed class CpuBusyInformationCollectorTests
{
    [Fact]
    public void OneReadPerCaptureFirstRoundIsEmptyAndPrecisionIsNotRounded()
    {
        var calls = 0;
        var samples = new Queue<IReadOnlyDictionary<int, T>>([
            new Dictionary<int, T> { [1] = new(10, 20, 30), [135] = new(20, 30, 40) },
            new Dictionary<int, T> { [1] = new(11, 22, 31), [135] = new(23, 35, 45) }]);
        var reader = new WindowsCpuTopologyReader(null!, () => { calls++; return samples.Dequeue(); });
        var notes = new List<string>();
        Assert.All(reader.ReadLogicalProcessorUsage(136, notes), value => Assert.Null(value));
        Assert.Equal(1, calls);
        var values = reader.ReadLogicalProcessorUsage(136, notes);
        Assert.Equal(200d / 3, values[1]); Assert.Equal(70d, values[135]);
        Assert.Null(values[0]); Assert.Null(values[134]); Assert.Equal(2, calls); Assert.Empty(notes);
    }

    [Theory]
    [InlineData(0, 0, 0, 100, 100, 0, 0d)]
    [InlineData(0, 0, 0, 0, 50, 50, 100d)]
    [InlineData(50, 100, 70, 75, 150, 120, 75d)]
    [InlineData(0, 0, 0, 25, 100, 0, 75d)]
    public void BusyTimeUsesKernelIncludingIdleAndUserTime(long oldIdle, long oldKernel, long oldUser,
        long idle, long kernel, long user, double expected)
        => Assert.Equal(expected, Usage(new(oldIdle, oldKernel, oldUser), new(idle, kernel, user)));

    [Theory]
    [InlineData(10, 20, 30, 10, 20, 30)] // No elapsed time.
    [InlineData(10, 20, 30, 9, 40, 50)]
    [InlineData(10, 20, 30, 10, 19, 100)] // Kernel reset masked by increasing user time.
    [InlineData(10, 20, 30, 10, 100, 29)] // User reset masked by increasing kernel time.
    [InlineData(10, 20, 30, 40, 30, 80)]
    [InlineData(10, 30, 30, 30, 40, 80)] // Idle delta exceeds kernel delta, though below total delta.
    [InlineData(-1, 20, 30, 10, 40, 50)]
    [InlineData(30, 20, 30, 40, 50, 70)]
    [InlineData(10, 20, -1, 10, 40, 50)]
    [InlineData(0, 0, 0, -1, 20, 30)]
    [InlineData(0, 0, 0, 0, -1, 30)]
    [InlineData(0, 0, 0, 0, 20, -1)]
    [InlineData(0, 0, 0, 0, long.MaxValue, 1)]
    public void InvalidCounterCellsRemainEmpty(long oldIdle, long oldKernel, long oldUser,
        long idle, long kernel, long user)
        => Assert.Null(Usage(new(oldIdle, oldKernel, oldUser), new(idle, kernel, user)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceFailureOrExplicitResetNeedsTwoFreshSamples(bool failSource)
    {
        var next = new Dictionary<int, T> { [0] = new(0, 100, 0) };
        var fail = false;
        var reader = new WindowsCpuTopologyReader(null!, () => fail ? throw new Win32Exception(5) : next);
        var notes = new List<string>();
        Assert.Null(reader.ReadLogicalProcessorUsage(1, notes)[0]);
        next = new() { [0] = new(20, 200, 0) };
        Assert.Equal(80d, reader.ReadLogicalProcessorUsage(1, notes)[0]);
        if (failSource)
        {
            fail = true;
            Assert.Null(reader.ReadLogicalProcessorUsage(1, notes)[0]);
            Assert.Single(notes);
            fail = false;
        }
        else reader.ResetUsageBaseline();
        next = new() { [0] = new(50_000, 100_000, 0) };
        Assert.Null(reader.ReadLogicalProcessorUsage(1, notes)[0]);
        next = new() { [0] = new(50_020, 100_100, 0) };
        Assert.Equal(80d, reader.ReadLogicalProcessorUsage(1, notes)[0]);
    }

    [Fact]
    public void ChangedReturnedMembershipReestablishesTheWholeCaptureWindow()
    {
        var samples = new Queue<IReadOnlyDictionary<int, T>>([
            new Dictionary<int, T> { [0] = new(0, 100, 0), [64] = new(0, 100, 0) },
            new Dictionary<int, T> { [0] = new(20, 200, 0) },
            new Dictionary<int, T> { [0] = new(40, 300, 0) },
            new Dictionary<int, T> { [0] = new(60, 400, 0), [64] = new(10, 500, 0) },
            new Dictionary<int, T> { [0] = new(80, 500, 0), [64] = new(20, 600, 0) }]);
        var reader = new WindowsCpuTopologyReader(null!, samples.Dequeue);
        Assert.All(reader.ReadLogicalProcessorUsage(65, []), value => Assert.Null(value));
        Assert.All(reader.ReadLogicalProcessorUsage(65, []), value => Assert.Null(value));
        var partial = reader.ReadLogicalProcessorUsage(65, []);
        Assert.Equal(80d, partial[0]); Assert.Null(partial[64]);
        Assert.All(reader.ReadLogicalProcessorUsage(65, []), value => Assert.Null(value));
        var complete = reader.ReadLogicalProcessorUsage(65, []);
        Assert.Equal(80d, complete[0]); Assert.Equal(90d, complete[64]);
    }

    [Fact]
    public void CanonicalFieldsIgnoreLegacyValuesAndPreserveFullSmtMean()
    {
        var source = CpuTopologyInformationFieldTests.Snapshot();
        var information = CpuTopologyInformationProjection.Create(source, DeviceInformationSnapshot.Empty);
        Assert.Equal(20d, Field(information, "windows-cpu-thread|0:1", "cpu_all_logical_busy_ratio").Value!.Value.GetDouble());
        Assert.Equal(50d, Field(information, "windows-cpu-core|1,3", "cpu_all_physical_core_busy_ratio").Value!.Value.GetDouble());
        Assert.Equal(37.123456789, Field(information, "windows-cpu-core|135", "cpu_all_physical_core_busy_ratio").Value!.Value.GetDouble());
        var definitions = information.Devices.SelectMany(device => device.Fields).Select(field => field.Definition)
            .Where(definition => definition.SourceId == "windows.processor-time").DistinctBy(definition => definition.Name).ToArray();
        Assert.Equal(2, definitions.Length);
        Assert.All(definitions, definition =>
        {
            Assert.Equal("cpu", definition.Resource); Assert.Equal("all", definition.Vendor);
            Assert.Equal("%", definition.Unit); Assert.Equal("number", definition.ValueKind);
        });
        Assert.Equal("cpu.logical_busy_time_percent", definitions.Single(definition => definition.Name == "cpu_all_logical_busy_ratio").Quantity);
        Assert.Equal("cpu.physical_core_smt_mean_busy_time_percent", definitions.Single(definition => definition.Name == "cpu_all_physical_core_busy_ratio").Quantity);
        Assert.DoesNotContain(information.Devices, device => device.ObjectType == "cpu_ccd");
        var absent = source with { OsTopologyInformation = source.OsTopologyInformation! with { LogicalProcessorBusyPercent = null } };
        var cleared = CpuTopologyInformationProjection.Create(absent, information);
        Assert.All(cleared.Devices.SelectMany(device => device.Fields).Where(field => field.Definition.SourceId == "windows.processor-time"),
            field => Assert.Null(field.Value));
        Assert.NotNull(Field(cleared, "windows-cpu-core|1,3", "cpu_all_physical_core_id").Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1d)]
    [InlineData(100.01d)]
    public void MissingOrInvalidSmtMemberCannotBecomePartialMean(double? invalid)
    {
        var source = CpuTopologyInformationFieldTests.Snapshot();
        var values = CpuTopologyInformationFieldTests.BusyValues(); values[3] = invalid;
        var partial = source with { OsTopologyInformation = source.OsTopologyInformation! with { LogicalProcessorBusyPercent = values } };
        var information = CpuTopologyInformationProjection.Create(partial, DeviceInformationSnapshot.Empty);
        Assert.Null(Field(information, "windows-cpu-thread|0:3", "cpu_all_logical_busy_ratio").Value);
        Assert.Null(Field(information, "windows-cpu-core|1,3", "cpu_all_physical_core_busy_ratio").Value);
        Assert.Equal(20d, Field(information, "windows-cpu-thread|0:1", "cpu_all_logical_busy_ratio").Value!.Value.GetDouble());
        Assert.Equal(37.123456789, Field(information, "windows-cpu-core|135", "cpu_all_physical_core_busy_ratio").Value!.Value.GetDouble());
    }

    [Fact]
    public void ShortSourceAndUnverifiedOsCoreRelationsCannotInventMembership()
    {
        var source = CpuTopologyInformationFieldTests.Snapshot();
        var shortValues = new double?[] { 99, 20 };
        var shortSnapshot = source with { OsTopologyInformation = source.OsTopologyInformation! with { LogicalProcessorBusyPercent = shortValues } };
        var current = CpuTopologyInformationProjection.Create(shortSnapshot, DeviceInformationSnapshot.Empty);
        Assert.Null(Field(current, "windows-cpu-core|1,3", "cpu_all_physical_core_busy_ratio").Value);
        Assert.Null(Field(current, "windows-cpu-thread|2:7", "cpu_all_logical_busy_ratio").Value);
        var noRelations = shortSnapshot with { OsTopologyInformation = shortSnapshot.OsTopologyInformation! with { PhysicalCores = null } };
        Assert.DoesNotContain(CpuTopologyInformationProjection.Create(noRelations, DeviceInformationSnapshot.Empty).Devices,
            device => device.ObjectType is "cpu_core" or "cpu_thread");
        var cleared = CpuTopologyInformationProjection.Create(noRelations, current);
        Assert.All(cleared.Devices.Where(device => device.ObjectType is "cpu_core" or "cpu_thread").SelectMany(device => device.Fields),
            field => Assert.Null(field.Value));
    }

    [Fact]
    public async Task RealWorkerUsesOriginalTimeBaselineAndRestartsAfterCanceledPausedDemand()
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        var nativeReads = 0;
        IReadOnlyDictionary<int, T> times = new Dictionary<int, T> { [1] = new(0, 100, 0), [3] = new(0, 100, 0), [135] = new(0, 100, 0) };
        var reader = new WindowsCpuTopologyReader(null!, () => { Interlocked.Increment(ref nativeReads); return times; });
        fixture.Sampler.UsageBaselineReset = reader.ResetUsageBaseline;
        fixture.Sampler.CaptureTransform = snapshot => snapshot with { OsTopologyInformation = snapshot.OsTopologyInformation! with
            { LogicalProcessorBusyPercent = reader.ReadLogicalProcessorUsage(136, []) } };
        using var cancellation = new CancellationTokenSource();
        await using var stream = fixture.Reader.SubscribeInformationAsync("busy", TimeSpan.FromSeconds(1), cancellation.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await fixture.Reader.StartAsync(CancellationToken.None);
        Assert.Equal(1, await fixture.Sampler.WaitForCaptureAsync());
        fixture.Sampler.Complete(CpuTopologyInformationFieldTests.Snapshot());
        Assert.True(await first.WaitAsync(CpuTopologyProviderTestFixture.Deadline));
        Assert.Null(Field(stream.Current, "windows-cpu-core|1,3", "cpu_all_physical_core_busy_ratio").Value);
        Assert.Equal(1, nativeReads);
        var second = stream.MoveNextAsync().AsTask();
        Assert.Equal(2, await fixture.Sampler.WaitForCaptureAsync());
        times = new Dictionary<int, T> { [1] = new(20, 200, 0), [3] = new(80, 200, 0), [135] = new(10, 200, 0) };
        fixture.Sampler.Complete(CpuTopologyInformationFieldTests.Snapshot());
        Assert.True(await second.WaitAsync(CpuTopologyProviderTestFixture.Deadline));
        var retained = stream.Current;
        Assert.Equal(50d, Field(retained, "windows-cpu-core|1,3", "cpu_all_physical_core_busy_ratio").Value!.Value.GetDouble());
        for (var i = 0; i < 10; i++) Assert.Same(retained, fixture.Reader.GetInformationSnapshot());
        Assert.Equal(2, nativeReads);
        var resets = fixture.Sampler.ResetCount;
        await cancellation.CancelAsync();
        using var deadline = new CancellationTokenSource(CpuTopologyProviderTestFixture.Deadline);
        while (fixture.Sampler.ResetCount == resets) await Task.Delay(10, deadline.Token);
        Assert.Equal(2, nativeReads); Assert.Equal(1u, fixture.ReadDemand().SourceCount);
        Assert.Same(retained, fixture.Reader.GetInformationSnapshot());

        using var resumedCancellation = new CancellationTokenSource();
        await using var resumed = fixture.Reader.SubscribeInformationAsync("again", TimeSpan.FromSeconds(1), resumedCancellation.Token).GetAsyncEnumerator();
        var resumedFirst = resumed.MoveNextAsync().AsTask();
        Assert.Equal(3, await fixture.Sampler.WaitForCaptureAsync());
        times = new Dictionary<int, T> { [1] = new(50_000, 100_000, 0), [3] = new(50_000, 100_000, 0), [135] = new(50_000, 100_000, 0) };
        fixture.Sampler.Complete(CpuTopologyInformationFieldTests.Snapshot());
        Assert.True(await resumedFirst.WaitAsync(CpuTopologyProviderTestFixture.Deadline));
        Assert.Null(Field(resumed.Current, "windows-cpu-core|1,3", "cpu_all_physical_core_busy_ratio").Value);
        var resumedSecond = resumed.MoveNextAsync().AsTask();
        Assert.Equal(4, await fixture.Sampler.WaitForCaptureAsync());
        times = new Dictionary<int, T> { [1] = new(50_025, 100_100, 0), [3] = new(50_025, 100_100, 0), [135] = new(50_025, 100_100, 0) };
        fixture.Sampler.Complete(CpuTopologyInformationFieldTests.Snapshot());
        Assert.True(await resumedSecond.WaitAsync(CpuTopologyProviderTestFixture.Deadline));
        Assert.Equal(75d, Field(resumed.Current, "windows-cpu-core|1,3", "cpu_all_physical_core_busy_ratio").Value!.Value.GetDouble());
        Assert.Equal(4, nativeReads);
        await resumedCancellation.CancelAsync();
    }

    private static double? Usage(T previous, T current)
        => WindowsCpuOsTopologyReader.CalculateProcessorUsage(new Dictionary<int, T> { [0] = previous },
            new Dictionary<int, T> { [0] = current }, 1)[0];

    private static DeviceInformationField Field(DeviceInformationSnapshot information, string identity, string name)
        => CpuTopologyInformationFieldTests.Field(Assert.Single(information.Devices, device => device.DeviceIdentity == identity), name);
}
