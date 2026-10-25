using ResourceManager.ReadWrite.Native;

namespace Resource_Manager_APP.Tests;

public sealed partial class NativeMetricSnapshotAbiTests
{
    [Fact]
    public void SelectedCurrentReadsAllocateFor100SelectedCellsInsteadOfTheWhole10000CellTable()
    {
        var config = CreateConfiguration();
        using var session = NativeMetricSnapshotSession.CreateCurrent(in config);
        var cells = Enumerable.Range(1, 10_000).Select(index =>
            CurrentCell(1, (ulong)index, NativeMetricSnapshotValueKind.Unsigned64, ulong.MaxValue - (ulong)index)).ToArray();
        session.CommitCurrent([1], [], cells, []);
        var keys = Enumerable.Range(0, 100).Select(index => new NativeMetricCurrentKey
            { DatasetHandle = 1, CellHandle = (ulong)(10_000 - index * 99) }).ToArray();
        var frame = session.ReadCurrent(keys);
        Assert.Equal(100, frame.Values.Length);
        Assert.Empty(frame.Bytes);
        for (var index = 0; index < keys.Length; index++)
            Assert.Equal(cells[(int)keys[index].CellHandle - 1].ValueBits, frame.Values[index].ValueBits);
        // Warm both methods before measuring the allocations owned by each call.
        _ = session.ReadCurrent();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 10; iteration++) _ = session.ReadCurrent(keys);
        var selectedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 10; iteration++) _ = session.ReadCurrent();
        var fullBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(selectedBytes < fullBytes / 10, $"Selected allocated {selectedBytes}, full allocated {fullBytes} bytes.");
        Assert.Empty(session.ReadCurrent([]).Values);
        Assert.Empty(session.ReadCurrent([new() { DatasetHandle = 2, CellHandle = 1 }]).Values);
    }

    [Fact]
    public void SelectedCurrentReadsKeepExactTimeNullsAndCompactBytePayloadInSelectionOrder()
    {
        var config = CreateConfiguration();
        using var session = NativeMetricSnapshotSession.CreateCurrent(in config);
        const long ticks = -638000000000000001L;
        session.CommitCurrent([1, 2], [],
            [CurrentCell(1, 1, NativeMetricSnapshotValueKind.Bytes, 0, 3),
             CurrentCell(1, 2, NativeMetricSnapshotValueKind.Signed64, unchecked((ulong)ticks)),
             CurrentCell(2, 1, NativeMetricSnapshotValueKind.Bytes, 3, 2),
             new() { DatasetHandle = 2, CellHandle = 2, ValueKind = (uint)NativeMetricSnapshotValueKind.Bytes }],
            [1, 2, 3, 128, 255]);
        NativeMetricCurrentKey[] keys =
            [new() { DatasetHandle = 2, CellHandle = 1 }, new() { DatasetHandle = 1, CellHandle = 2 },
             new() { DatasetHandle = 2, CellHandle = 1 }, new() { DatasetHandle = 2, CellHandle = 2 }];
        var frame = session.ReadCurrent(keys);
        Assert.Equal(4, frame.Values.Length);
        Assert.Equal(new byte[] { 128, 255, 128, 255 }, frame.Bytes);
        Assert.Equal(0UL, frame.Values[0].ValueBits);
        Assert.Equal(unchecked((ulong)ticks), frame.Values[1].ValueBits);
        Assert.Equal(2UL, frame.Values[2].ValueBits);
        Assert.Equal(0U, frame.Values[3].Flags);
        Assert.Equal(0UL, frame.Values[3].ValueBits);
        Assert.Throws<InvalidOperationException>(() => session.CommitCurrent([1, 2], [],
            [CurrentCell(1, 1, NativeMetricSnapshotValueKind.Bytes, 0, 3),
             CurrentCell(2, 1, NativeMetricSnapshotValueKind.Boolean, 2)], [9, 9, 9]));
        var after = session.ReadCurrent(keys);
        Assert.Equal(frame.Values, after.Values);
        Assert.Equal(frame.Bytes, after.Bytes);
        Assert.Throws<InvalidOperationException>(() => session.ReadCurrent([new() { DatasetHandle = 0, CellHandle = 1 }]));
    }

    [Fact]
    public void CurrentOnlySessionUsesSmallCollectorCapacitiesAndHonorsTheExactResidentBudget()
    {
        var config = CreateConfiguration();
        using var session = NativeMetricSnapshotSession.CreateCurrent(in config);
        Assert.Equal(1U, session.Capacity.SourceCapacity);
        Assert.Equal(1U, session.Capacity.MetricCapacity);
        Assert.Equal(1U, session.Capacity.RuleCapacity);
        Assert.InRange(session.Capacity.ResidentByteCount + session.Capacity.ScratchByteCount, 1UL, 16UL * 1024);
        session.CommitCurrent([1, 2, 3, 4], [],
            [CurrentCell(1, 1, NativeMetricSnapshotValueKind.Unsigned64, 1),
             CurrentCell(2, 1, NativeMetricSnapshotValueKind.Unsigned64, 2),
             CurrentCell(3, 1, NativeMetricSnapshotValueKind.Unsigned64, 3),
             CurrentCell(4, 1, NativeMetricSnapshotValueKind.Unsigned64, 4)], []);
        Assert.InRange(session.Capacity.ResidentByteCount + session.Capacity.ScratchByteCount, 1UL, 16UL * 1024);
        // Recreate the same minimal configuration with exactly four cell slots.
        config.ResidentByteBudget = session.Capacity.ResidentByteCount + session.Capacity.ScratchByteCount;
        using var exact = NativeMetricSnapshotSession.CreateCurrent(in config);
        exact.CommitCurrent([1, 2, 3, 4], [], session.ReadCurrent().Values, []);
        Assert.Equal(config.ResidentByteBudget, exact.Capacity.ResidentByteCount + exact.Capacity.ScratchByteCount);
        Assert.Throws<InvalidOperationException>(() => exact.CommitCurrent([5], [],
            [CurrentCell(5, 1, NativeMetricSnapshotValueKind.Unsigned64, 5)], []));
        Assert.Equal(4, exact.ReadCurrent().Values.Length);
    }

    [Fact]
    public void CurrentTransactionReplacesOnlyRequestedDatasetsAndRetainsValuesAcrossCollectorReset()
    {
        var config = CreateConfiguration();
        var resources = new List<MetricScratchRecording>();
        var session = new NativeMetricSnapshotSession(in config, (length, alignment) =>
        {
            var resource = new MetricScratchRecording(length, alignment);
            resources.Add(resource);
            return resource;
        });
        try
        {
            NativeMetricCurrentValue[] first =
            [
                CurrentCell(1, 1, NativeMetricSnapshotValueKind.Signed64, unchecked((ulong)-638000000000000000L)),
                CurrentCell(2, 1, NativeMetricSnapshotValueKind.Unsigned128, ulong.MaxValue, 0x123456789abcdef0),
                CurrentCell(3, 1, NativeMetricSnapshotValueKind.Bytes, 0, 3),
                CurrentCell(4, 1, NativeMetricSnapshotValueKind.Boolean, 1)
            ];
            session.CommitCurrent([1, 2, 3, 4], [], first, [0, 128, 255]);
            Array.Clear(first);
            var initial = session.ReadCurrent();
            Assert.Equal(4, initial.Values.Length);
            Assert.Equal(unchecked((ulong)-638000000000000000L), initial.Values[0].ValueBits);
            Assert.Equal(0x123456789abcdef0UL, initial.Values[1].ValueHighBits);
            Assert.Equal(new byte[] { 0, 128, 255 }, initial.Bytes);
            var resident = Assert.Single(resources.Skip(1));
            Assert.Equal(1, resident.Acquires);
            Assert.Equal(1, resident.Pins);
            Assert.Equal(0, resident.Unpins);

            session.CommitCurrent([1, 4], [],
                [CurrentCell(1, 1, NativeMetricSnapshotValueKind.Signed64, 42),
                 new() { DatasetHandle = 4, CellHandle = 1, ValueKind = (uint)NativeMetricSnapshotValueKind.Boolean }], []);
            var partial = session.ReadCurrent();
            Assert.Equal(4, partial.Values.Length);
            Assert.Equal(42UL, partial.Values[0].ValueBits);
            Assert.Equal(initial.Values[1].ValueHighBits, partial.Values[1].ValueHighBits);
            Assert.Equal(initial.Bytes, partial.Bytes);
            Assert.Equal(0U, partial.Values[3].Flags);
            Assert.Equal(1, resident.Acquires);

            Assert.Throws<InvalidOperationException>(() => session.CommitCurrent([1, 4], [],
                [CurrentCell(1, 1, NativeMetricSnapshotValueKind.Signed64, 99),
                 CurrentCell(4, 1, NativeMetricSnapshotValueKind.Boolean, 2)], []));
            Assert.Equal(42UL, session.ReadCurrent().Values[0].ValueBits);

            var reset = new NativeMetricSnapshotControlInput
            {
                AbiVersion = NativeMetricSnapshotAbi.Version,
                StructSize = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<NativeMetricSnapshotControlInput>(),
                ConfigurationGeneration = config.Generation, OperationEpoch = 1, CommandAtMilliseconds = 100
            };
            Assert.Equal(NativeMetricSnapshotStatus.Ok, session.Reset(in reset));
            Assert.Equal(4, session.ReadCurrent().Values.Length);
            session.CommitCurrent([], [2, 3], [], []);
            Assert.Equal(new ulong[] { 1, 4 }, session.ReadCurrent().Values.Select(row => row.DatasetHandle));
            Assert.Empty(session.ReadCurrent().Bytes);
        }
        finally { session.Dispose(); }
        Assert.All(resources, resource =>
        {
            Assert.Equal(resource.Pins, resource.Unpins);
            Assert.Equal(resource.Acquires, resource.Returns);
            Assert.Equal(1, resource.Retires);
        });
    }

    [Fact]
    public void CurrentGrowthFailureKeepsPriorStorageAndPublicationAndSuccessfulGrowthReleasesTheOldLoan()
    {
        var config = CreateConfiguration();
        var resources = new List<MetricScratchRecording>();
        using var session = new NativeMetricSnapshotSession(in config, (length, alignment) =>
        {
            var resource = new MetricScratchRecording(length, alignment);
            resources.Add(resource);
            return resource;
        });
        session.CommitCurrent([1], [], [CurrentCell(1, 1, NativeMetricSnapshotValueKind.Unsigned64, ulong.MaxValue)], []);
        var old = resources[1];
        Assert.Throws<InvalidOperationException>(() => session.CommitCurrent([2], [],
            [CurrentCell(2, 1, NativeMetricSnapshotValueKind.Boolean, 2)], []));
        Assert.Single(session.ReadCurrent().Values);
        Assert.Equal(ulong.MaxValue, session.ReadCurrent().Values[0].ValueBits);
        Assert.Equal(0, old.Returns);
        Assert.Equal(1, resources[2].Returns);
        Assert.Equal(1, resources[2].Retires);
        session.CommitCurrent([2], [], [CurrentCell(2, 1, NativeMetricSnapshotValueKind.Unsigned64, 7)], []);
        Assert.Equal(2, session.ReadCurrent().Values.Length);
        Assert.Equal(1, old.Returns);
        Assert.Equal(1, old.Unpins);
        Assert.Equal(1, old.Retires);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentPostCommitStorageFailureAlignsDeclarationsAndCapacityBeforeSurfacingTheFailure(bool completion)
    {
        var config = CreateConfiguration();
        var resources = new List<MetricScratchRecording>();
        var armed = false;
        using var session = NativeMetricSnapshotSession.CreateCurrent(in config, (length, alignment) =>
        {
            var resource = new MetricScratchRecording(length, alignment) { FailComplete = armed && completion };
            resources.Add(resource);
            return resource;
        });
        session.CommitCurrent([1], [], [CurrentCell(1, 1, NativeMetricSnapshotValueKind.Unsigned64, 1)], []);
        var old = resources[1];
        old.FailRetire = !completion;
        armed = true;
        var committed = false;
        var failure = Assert.Throws<InvalidOperationException>(() => session.CommitCurrent([2], [],
            [CurrentCell(2, 1, NativeMetricSnapshotValueKind.Unsigned64, 2)], [], () => committed = true));
        Assert.Same(completion ? resources[2].CompleteFailure : old.RetireFailure, failure);
        Assert.True(committed);
        Assert.Equal(new ulong[] { 1, 2 }, session.ReadCurrent().Values.Select(value => value.ValueBits));
        armed = false;
        foreach (var resource in resources) { resource.FailRetire = false; resource.FailComplete = false; }
        session.CommitCurrent([3], [], [CurrentCell(3, 1, NativeMetricSnapshotValueKind.Unsigned64, 3)], []);
        Assert.Equal(3, session.ReadCurrent().Values.Length);
        Assert.Equal(1, old.Retires);
    }

    private static NativeMetricCurrentValue CurrentCell(ulong dataset, ulong cell,
        NativeMetricSnapshotValueKind kind, ulong bits, ulong high = 0)
        => new() { DatasetHandle = dataset, CellHandle = cell, ValueKind = (uint)kind,
            Flags = 1, ValueBits = bits, ValueHighBits = high };
}
