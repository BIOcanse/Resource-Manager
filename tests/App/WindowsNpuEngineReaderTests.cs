using System.Runtime.InteropServices;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Windows.Devices.Npu;
using ResourceManager.ReadWrite.Windows.Gpu;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsNpuEngineReaderTests
{
    private const string FirstPnp = @"PCI\VEN_8086&DEV_1234\ONE";
    private const string SecondPnp = @"ACPI\NPU0001\TWO";

    [Theory]
    [InlineData(0x8086U)]
    [InlineData(0x1022U)]
    public void EachSelectedEngineIsQueriedOnceWithOriginalUnsignedValueAndSameCallQpc(uint vendor)
    {
        var api = new RecordedApi([new() { Vendor = vendor }]);
        long ticks = 100;
        var reader = Reader(api, bridge: (_, expectedVendor, device, maximum) =>
        {
            Assert.Equal(vendor, expectedVendor); Assert.Equal(0x1234U, device); Assert.Equal(64U, maximum);
            return Bound(Physical(0, vendor == 0x8086 ? FirstPnp : @"PCI\VEN_1022&DEV_1234\ONE") with { VendorId = vendor });
        }, timestamp: () => ticks++);
        using var directory = reader.OpenDirectory(2)!;
        Assert.Equal(2, directory.Identities.Count);
        var rows = Read(reader, directory);
        Assert.Equal(new[] { 0U, 1U }, api.Queries.Select(static query => query.Engine));
        Assert.Equal(0UL, rows[0].Sample!.RunningTimeMicroseconds);
        Assert.Equal(ulong.MaxValue, rows[1].Sample!.RunningTimeMicroseconds);
        Assert.Equal(new NpuEngineSample(0, 100, 101, 10_000_000), rows[0].Sample);
        Assert.Equal(new NpuEngineSample(ulong.MaxValue, 102, 103, 10_000_000), rows[1].Sample);
        Assert.All(rows, static row => Assert.False(row.RequiresTopologyRefresh));
        Assert.Equal(WindowsNpuDxCoreEnumeration.NpuAttribute, api.Filter);
        Assert.All(api.Queries, static query =>
        { Assert.Equal(4U, query.State); Assert.Equal(0U, query.Physical); Assert.Equal((nuint)8, query.InputSize); Assert.Equal((nuint)8, query.OutputSize); });
        Assert.Empty(api.Released); // The holder's live session remains open across operations.
    }

    [Fact]
    public void SameNamesAreSeparateEnginesAndLiveDirectoryKeepsIdentityAcrossReads()
    {
        var api = new RecordedApi([new()]);
        var reader = Reader(api);
        using var directory = reader.OpenDirectory(2)!;
        var keys = directory.Identities.Select(static engine => engine.ExactIdentityKey).ToArray();
        Assert.All(directory.Identities, static engine => Assert.Equal("Compute", engine.NameDeclared));
        Assert.Equal(2, keys.Distinct().Count());
        Assert.All(directory.Identities, static engine => Assert.Equal("windows-npu|" + FirstPnp, engine.ParentDeviceIdentity));
        Read(reader, directory); Read(reader, directory);
        Assert.True(directory.IsCurrent(2));
        Assert.Equal(keys, directory.Identities.Select(static engine => engine.ExactIdentityKey));
        Assert.Equal(1, api.FactoryCalls); Assert.Equal(1, api.ListCalls); Assert.Equal(1, api.QiCalls);
        Assert.Equal(4, api.Queries.Count);
    }

    [Fact]
    public void SelectionDoesNotQueryUnselectedEngineOrDuplicateRawAndFutureRatioCall()
    {
        var api = new RecordedApi([new()]); var reader = Reader(api);
        using var directory = reader.OpenDirectory(2)!;
        var selected = directory.Identities[1];
        var rows = reader.Read(directory.Identities, new HashSet<string> { selected.ExactIdentityKey }, 2);
        Assert.Same(selected, Assert.Single(rows).Engine);
        Assert.Equal(1U, Assert.Single(api.Queries).Engine);
        Assert.Throws<ArgumentException>(() => reader.Read([selected, selected], new HashSet<string> { selected.ExactIdentityKey }, 2));
        Assert.Single(api.Queries);
    }

    [Theory]
    [InlineData("name-unsupported")]
    [InlineData("name-error")]
    [InlineData("name-throw")]
    [InlineData("name-length")]
    [InlineData("name-return-length")]
    [InlineData("name-invalid-utf16")]
    public void OptionalNameFailureDoesNotKillRawCounter(string failure)
    {
        var api = new RecordedApi([new()]) { Failure = failure }; var reader = Reader(api);
        using var directory = reader.OpenDirectory(2)!;
        Assert.All(directory.Identities, static engine => Assert.Null(engine.NameDeclared));
        Assert.All(Read(reader, directory), static row => Assert.NotNull(row.Sample));
        Assert.Equal(2, api.Queries.Count);
    }

    [Theory]
    [InlineData("factory")]
    [InlineData("list")]
    [InlineData("adapter-count")]
    [InlineData("stale")]
    [InlineData("adapter")]
    [InlineData("invalid")]
    [InlineData("zero-luid")]
    [InlineData("physical-unsupported")]
    [InlineData("physical-size")]
    [InlineData("qi")]
    [InlineData("count-unsupported")]
    [InlineData("count-error")]
    [InlineData("count-throw")]
    public void UnsupportedOrIncompleteDirectoryIsNullAndReleasesAcquiredReferences(string failure)
    {
        var api = new RecordedApi([new()]) { Failure = failure };
        Assert.Null(Reader(api).OpenDirectory(2)); Assert.Empty(api.Queries);
        var expected = failure == "factory" ? new nint[] { 1 }
            : failure is "list" or "adapter-count" or "stale" ? new nint[] { 2, 1 }
            : failure is "qi" or "count-unsupported" or "count-error" or "count-throw" ? new nint[] { 100, 10, 2, 1 }
            : new nint[] { 10, 2, 1 };
        Assert.Equal(expected, api.Released);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void SinglePhysicalAdapterIsRequiredWithoutIndexZeroTruncation(int physicalCount)
    {
        var api = new RecordedApi([new() { PhysicalCount = checked((uint)physicalCount) }]);
        Assert.Null(Reader(api).OpenDirectory(2)); Assert.Empty(api.Queries);
        Assert.Equal(0, api.QiCalls); Assert.Equal(new nint[] { 10, 2, 1 }, api.Released);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void SoftwareAndNonNpuAreNotClassifiedByEngineName(bool npu, bool hardware)
    {
        var api = new RecordedApi([new() { Npu = npu, Hardware = hardware }]);
        var reader = Reader(api, bridge: (_, _, _, _) => throw new InvalidOperationException("Filtered before bridge."));
        using var directory = reader.OpenDirectory(2)!;
        Assert.Empty(directory.Identities); Assert.Empty(api.Queries); Assert.Equal(0, api.QiCalls);
    }

    [Fact]
    public void ZeroDemandDoesNotOpenAndSuccessfulZeroCountIsNotUnsupported()
    {
        var api = new RecordedApi([new()]); var reader = Reader(api);
        using (var none = reader.OpenDirectory(0)!) Assert.Empty(none.Identities);
        Assert.Equal(0, api.FactoryCalls);
        var zero = new RecordedApi([new() { EngineCount = 0 }]);
        using (var empty = Reader(zero).OpenDirectory(2)!) Assert.Empty(empty.Identities);
        Assert.Equal(1, zero.QiCalls); Assert.Empty(zero.Queries);
        Assert.Equal(new nint[] { 100, 10, 2, 1 }, zero.Released);
        Assert.Null(Reader(new RecordedApi([new()]) { Failure = "count-unsupported" }).OpenDirectory(2));
    }

    [Fact]
    public void EnablingDemandCannotReuseAnUnobservedEmptyLeaseAndDoesNotOpenImplicitly()
    {
        var api = new RecordedApi([new()]); var reader = Reader(api);
        using var noDemand = reader.OpenDirectory(0)!;
        Assert.True(noDemand.IsCurrent(0));
        Assert.False(noDemand.IsCurrent(2));
        Assert.Equal(0, api.FactoryCalls); Assert.Equal(0, api.ListCalls);
        using var observed = noDemand.IsCurrent(2) ? throw new InvalidOperationException("Must not reuse.") : reader.OpenDirectory(2)!;
        Assert.Equal(2, observed.Identities.Count); Assert.True(observed.IsCurrent(2));
        Assert.Equal(1, api.FactoryCalls); Assert.Equal(1, api.ListCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrulyObservedEmptyDirectoryCanBeReusedAtPositiveBudgetAndStillObservesStickyStale(bool adapterWithZeroEngines)
    {
        var api = new RecordedApi(adapterWithZeroEngines ? [new() { EngineCount = 0 }] : []);
        var reader = Reader(api);
        using var directory = reader.OpenDirectory(2)!;
        Assert.Empty(directory.Identities); Assert.True(directory.IsCurrent(2)); Assert.True(directory.IsCurrent(3));
        Assert.Equal(1, api.FactoryCalls); Assert.Equal(1, api.ListCalls);
        api.Change = "stale"; Assert.False(directory.IsCurrent(2));
        api.Change = null; Assert.False(directory.IsCurrent(2));
    }

    [Theory]
    [InlineData(0U, 0, false)]
    [InlineData(7U, 2, false)]
    [InlineData(8U, 4, true)]
    public void OptionalNameBudgetIsExplicitAndZeroDoesNotQueryNames(uint maximumCharacters, int nameCalls, bool named)
    {
        var api = new RecordedApi([new()]); var reader = Reader(api, maximumEngineNameCharacters: maximumCharacters);
        using var directory = reader.OpenDirectory(2)!;
        Assert.All(directory.Identities, engine => Assert.Equal(named ? "Compute" : null, engine.NameDeclared));
        Assert.Equal(nameCalls, api.NameCalls);
        Assert.All(Read(reader, directory), static row => Assert.NotNull(row.Sample)); Assert.Equal(2, api.Queries.Count);
    }

    [Fact]
    public void ExplicitNameBudgetCanExceedTheSoftwareDefaultWithoutChangingAbiOrRawQueries()
    {
        var name = new string('n', 4096);
        var original = new RecordedApi([new()]) { EngineName = name };
        using (var directory = Reader(original).OpenDirectory(2)!)
            Assert.All(directory.Identities, static engine => Assert.Null(engine.NameDeclared));
        var expanded = new RecordedApi([new()]) { EngineName = name }; var reader = Reader(expanded, maximumEngineNameCharacters: 4097);
        using var observed = reader.OpenDirectory(2)!;
        Assert.All(observed.Identities, engine => Assert.Equal(name, engine.NameDeclared));
        Assert.All(Read(reader, observed), static row => Assert.NotNull(row.Sample));
        Assert.Equal(4, expanded.NameCalls); Assert.Equal(2, expanded.Queries.Count);
    }

    [Fact]
    public void GlobalCapacityRejectsWholeDirectoryButLargerProfileCanIncludeSameActualEngines()
    {
        var api = new RecordedApi([new() { EngineCount = 1 }, new() { Luid = 8, EngineCount = 2 }]);
        Assert.Null(Reader(api).OpenDirectory(2)); Assert.Empty(api.Queries);
        Assert.Equal(new nint[] { 101, 11, 100, 10, 2, 1 }, api.Released);
        var allowed = new RecordedApi([new() { EngineCount = 1 }, new() { Luid = 8, EngineCount = 2 }]);
        var reader = Reader(allowed);
        using var directory = reader.OpenDirectory(3)!;
        Assert.Equal(3, directory.Identities.Count);
        Assert.Equal(3, Read(reader, directory, maximum: 3).Count);
        Assert.All(directory.Identities.Where(static engine => engine.PnpInstanceId == SecondPnp), static engine => Assert.Equal(0U, engine.PhysicalAdapterIndex));
        Assert.All(Read(reader, directory, maximum: 2), static row => { Assert.Null(row.Sample); Assert.False(row.RequiresTopologyRefresh); });
        Assert.Equal(3, allowed.Queries.Count);
    }

    [Theory]
    [InlineData("bridge-unavailable")]
    [InlineData("bridge-conflict")]
    [InlineData("bridge-index")]
    [InlineData("bridge-evidence")]
    [InlineData("bridge-skipped")]
    public void ExactBridgeFailureCannotPublishEngineDirectory(string failure)
    {
        var api = new RecordedApi([new()]);
        var reader = Reader(api, bridge: (_, _, _, _) => failure switch
        {
            "bridge-unavailable" => WindowsGpuIdentityRead.Invalid,
            "bridge-conflict" => Bound(Physical(0, FirstPnp) with { Status = WindowsGpuPhysicalIdentityStatus.Conflict }),
            "bridge-index" => Bound(Physical(1, FirstPnp)),
            "bridge-evidence" => Bound(Physical(0, FirstPnp) with { Evidence = WindowsGpuIdentityEvidence.None }),
            _ => new(SamplingObservationStatus.Current, 0, [Physical(0, FirstPnp)], 1)
        });
        Assert.Null(reader.OpenDirectory(2)); Assert.Empty(api.Queries);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DuplicateLuidOrCanonicalPnpRejectsWholeDirectory(bool sameLuid)
    {
        var api = new RecordedApi([new() { EngineCount = 1 }, new() { Luid = sameLuid ? 7U : 8U, EngineCount = 1 }]);
        Assert.Null(Reader(api, bridge: (_, _, _, _) => Bound(Physical(0, FirstPnp))).OpenDirectory(2));
        Assert.Empty(api.Queries);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("stale")]
    [InlineData("luid")]
    [InlineData("driver")]
    [InlineData("count")]
    [InlineData("physical")]
    [InlineData("attribute")]
    [InlineData("hardware")]
    [InlineData("pnp")]
    [InlineData("pnp-driver")]
    public void ObservableChangeDuringStateReadDiscardsEveryPendingSampleAndRetiresSession(string change)
    {
        var api = new RecordedApi([new()]);
        var reader = Reader(api, bridge: (_, _, _, _) => Bound(Physical(0,
            api.Change == "pnp" ? SecondPnp : FirstPnp) with { DriverVersion = api.Change == "pnp-driver" ? "2" : "1" }));
        using var directory = reader.OpenDirectory(2)!;
        api.AfterQuery = () => api.Change = change;
        Assert.All(Read(reader, directory), static row => { Assert.Null(row.Sample); Assert.True(row.RequiresTopologyRefresh); });
        var count = api.Queries.Count;
        api.Change = null;
        Assert.False(directory.IsCurrent(2));
        Assert.All(Read(reader, directory), static row => Assert.True(row.RequiresTopologyRefresh));
        Assert.Equal(count, api.Queries.Count); // Returning evidence to prior values cannot revive a retired directory.
    }

    [Fact]
    public void ChangeBeforeReadDoesNotTouchStateAndNewSessionGetsANewBinding()
    {
        var api = new RecordedApi([new()]); var reader = Reader(api);
        using var first = reader.OpenDirectory(2)!;
        var key = first.Identities[0].ExactIdentityKey;
        api.Change = "stale";
        Assert.All(Read(reader, first), static row => Assert.True(row.RequiresTopologyRefresh)); Assert.Empty(api.Queries);
        api.Change = null;
        using var replacement = reader.OpenDirectory(2)!;
        Assert.NotEqual(key, replacement.Identities[0].ExactIdentityKey);
        Assert.NotEqual(first.Identities[0].BindingKey, replacement.Identities[0].BindingKey);
        Assert.Equal(first.Identities[0].ParentDeviceIdentity, replacement.Identities[0].ParentDeviceIdentity);
    }

    [Theory]
    [InlineData("state-unsupported")]
    [InlineData("state-error")]
    [InlineData("state-throw")]
    public void OrdinaryStateFailureIsUnknownWithoutRetiringDirectoryOrFabricatingZero(string failure)
    {
        var api = new RecordedApi([new()]); var reader = Reader(api);
        using var directory = reader.OpenDirectory(2)!; api.Failure = failure;
        Assert.All(Read(reader, directory), static row => { Assert.Null(row.Sample); Assert.False(row.RequiresTopologyRefresh); });
        Assert.True(directory.IsCurrent(2));
        Assert.Equal(failure == "state-unsupported" ? 0 : 2, api.Queries.Count);
    }

    [Theory]
    [InlineData("state-error")]
    [InlineData("state-throw")]
    public void OneFailedEngineDoesNotClearAnotherEngineOrRepeatEitherQuery(string failure)
    {
        var api = new RecordedApi([new()]) { FailedEngine = 0 }; var reader = Reader(api);
        using var directory = reader.OpenDirectory(2)!; api.Failure = failure;
        var rows = Read(reader, directory);
        Assert.Null(rows[0].Sample); Assert.Equal(ulong.MaxValue, rows[1].Sample!.RunningTimeMicroseconds);
        Assert.All(rows, static row => Assert.False(row.RequiresTopologyRefresh)); Assert.Equal(2, api.Queries.Count);
    }

    [Fact]
    public void EmptySelectionAndZeroBudgetDoNotSampleOrReadTheClock()
    {
        var api = new RecordedApi([new()]);
        var reader = Reader(api, timestamp: () => throw new InvalidOperationException("No selected state query."));
        using var directory = reader.OpenDirectory(2)!;
        Assert.Empty(reader.Read(directory.Identities, new HashSet<string>(), 2));
        Assert.Empty(reader.Read(directory.Identities, directory.Identities.Select(static engine => engine.ExactIdentityKey).ToHashSet(), 0));
        Assert.Empty(api.Queries);
    }

    [Theory]
    [InlineData("count-error")]
    [InlineData("count-throw")]
    public void TransientDirectoryQueryFailureIsUnknownWithoutClaimingAnAssociationChange(string failure)
    {
        var api = new RecordedApi([new()]); var reader = Reader(api);
        using var directory = reader.OpenDirectory(2)!; api.Failure = failure;
        Assert.All(Read(reader, directory), static row => { Assert.Null(row.Sample); Assert.False(row.RequiresTopologyRefresh); });
        Assert.Empty(api.Queries); api.Failure = null; Assert.True(directory.IsCurrent(2));
    }

    [Theory]
    [InlineData(0L, 100L, 101L, 0L)]
    [InlineData(10L, 100L, 101L, 11L)]
    [InlineData(10L, 100L, 99L, 10L)]
    [InlineData(10L, -1L, 0L, 10L)]
    public void InvalidQpcSampleIsUnknownAndDoesNotSubstituteWallClock(long beforeFrequency, long begin, long end, long afterFrequency)
    {
        var api = new RecordedApi([new() { EngineCount = 1 }]);
        var times = new Queue<long>([begin, end]); var frequencies = new Queue<long>([beforeFrequency, afterFrequency]);
        var reader = Reader(api, timestamp: times.Dequeue, frequency: frequencies.Dequeue);
        using var directory = reader.OpenDirectory(1)!;
        var row = Assert.Single(Read(reader, directory, maximum: 1));
        Assert.Null(row.Sample); Assert.False(row.RequiresTopologyRefresh); Assert.Single(api.Queries);
    }

    [Fact]
    public void LastBorrowKeepsResourcesLiveAndQueryInterfaceReferenceIsReleasedIndependently()
    {
        var api = new RecordedApi([new()]) { SameQiPointer = true }; var reader = Reader(api);
        var directory = reader.OpenDirectory(2)!;
        var borrow = directory.AcquireLease(); directory.Dispose(); directory.Dispose();
        Assert.Empty(api.Released); Assert.All(Read(reader, directory), static row => Assert.NotNull(row.Sample));
        borrow.Dispose(); borrow.Dispose();
        Assert.Equal(new nint[] { 10, 10, 2, 1 }, api.Released);
        Assert.False(directory.IsCurrent(2));
        Assert.Throws<ObjectDisposedException>(() => directory.AcquireLease());
    }

    [Fact]
    public void CancellationWhileOpeningReleasesReferencesAndReadCancellationKeepsHolderLeaseUntilDrain()
    {
        using var before = new CancellationTokenSource(); before.Cancel();
        var api = new RecordedApi([new()]); var reader = Reader(api);
        Assert.Throws<OperationCanceledException>(() => reader.OpenDirectory(2, before.Token)); Assert.Equal(0, api.FactoryCalls);
        using var opening = new CancellationTokenSource(); api.AfterName = opening.Cancel;
        Assert.Throws<OperationCanceledException>(() => reader.OpenDirectory(2, opening.Token));
        Assert.Equal(new nint[] { 100, 10, 2, 1 }, api.Released);
        var active = new RecordedApi([new()]); var activeReader = Reader(active);
        var directory = activeReader.OpenDirectory(2)!;
        using var during = new CancellationTokenSource(); active.AfterQuery = during.Cancel;
        Assert.Throws<OperationCanceledException>(() => Read(activeReader, directory, during.Token));
        Assert.Single(active.Queries); Assert.Empty(active.Released);
        directory.Dispose(); Assert.Equal(new nint[] { 100, 10, 2, 1 }, active.Released);
    }

    [Fact]
    public void EngineAbiMatchesOfficialHeaderAndDoesNotAddByProcessCalls()
    {
        Assert.Equal(1U, (uint)WindowsNpuDxCoreProperty.DriverVersion);
        Assert.Equal(16U, (uint)WindowsNpuDxCoreProperty.AdapterEngineCount);
        Assert.Equal(17U, (uint)WindowsNpuDxCoreProperty.AdapterEngineName);
        Assert.Equal(4U, (uint)WindowsNpuDxCoreState.AdapterEngineRunningTimeMicroseconds);
        Assert.Equal(8, Marshal.SizeOf<WindowsNpuDxCoreEngineIndex>());
        Assert.Equal(4, Marshal.OffsetOf<WindowsNpuDxCoreEngineIndex>(nameof(WindowsNpuDxCoreEngineIndex.EngineIndex)).ToInt32());
        Assert.Equal(IntPtr.Size == 8 ? 24 : 16, Marshal.SizeOf<WindowsNpuDxCoreEngineNameInput>());
        Assert.Equal(8, Marshal.OffsetOf<WindowsNpuDxCoreEngineNameInput>(nameof(WindowsNpuDxCoreEngineNameInput.EngineNameLength)).ToInt32());
        Assert.Equal(IntPtr.Size == 8 ? 16 : 12, Marshal.OffsetOf<WindowsNpuDxCoreEngineNameInput>(nameof(WindowsNpuDxCoreEngineNameInput.EngineName)).ToInt32());
        Assert.Equal(4, Marshal.SizeOf<WindowsNpuDxCoreEngineNameOutput>());
        Assert.Equal(13, (int)WindowsNpuDxCoreAdapter1Slot.GetPropertyWithInput);
        Assert.Equal(9, (int)WindowsNpuDxCoreAdapter1Slot.QueryState);
        Assert.Equal(2, (int)WindowsNpuDxCoreAdapter1Slot.Release);
        Assert.Equal(2U, (uint)WindowsNpuDxCoreState.AdapterMemoryUsageBytes);
    }

    [Fact]
    public void PublicCustomDirectoryCannotEnterTheNativeReadPath()
    {
        using var custom = new RecordedPublicDirectory();
        var identity = new NpuEngineCatalogIdentity(FirstPnp, 0, null, Guid.NewGuid(), 7, 0x8086, 0x1234, 1, 1, "1", custom);
        var api = new RecordedApi([new()]); var reader = Reader(api);
        Assert.Throws<ArgumentException>(() => reader.Read([identity], new HashSet<string> { identity.ExactIdentityKey }, 1));
        Assert.Equal(0, api.FactoryCalls); Assert.Empty(api.Queries);
        Assert.Throws<ArgumentException>(() => new NpuEngineCatalogIdentity(" ", 0, null, Guid.NewGuid(), 7, 0x8086, 0x1234, 1, 1, "1", custom));
        Assert.Throws<ArgumentException>(() => new NpuEngineCatalogIdentity(FirstPnp, 0, null, Guid.Empty, 7, 0x8086, 0x1234, 1, 1, "1", custom));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NpuEngineCatalogIdentity(FirstPnp, 1, null, Guid.NewGuid(), 7, 0x8086, 0x1234, 1, 1, "1", custom));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NpuEngineCatalogIdentity(FirstPnp, 0, null, Guid.NewGuid(), 0, 0x8086, 0x1234, 1, 1, "1", custom));
    }

    private sealed class RecordedPublicDirectory : IWindowsNpuEngineDirectory
    {
        public IReadOnlyList<NpuEngineCatalogIdentity> Identities => [];
        public bool IsCurrent(uint maximum, CancellationToken token = default) => false;
        public IDisposable AcquireLease() => throw new InvalidOperationException("No holder in this argument validation fixture.");
        public void Dispose() { }
    }

    private static IReadOnlyList<NpuEngineRead> Read(WindowsNpuEngineReader reader, WindowsNpuEngineDirectoryLease directory,
        CancellationToken token = default, uint maximum = 2)
        => reader.Read(directory.Identities, directory.Identities.Select(static engine => engine.ExactIdentityKey).ToHashSet(), maximum, token);
    private static WindowsNpuEngineReader Reader(RecordedApi api,
        Func<AdapterLuid, uint, uint, uint, WindowsGpuIdentityRead>? bridge = null, Func<long>? timestamp = null, Func<long>? frequency = null,
        uint maximumEngineNameCharacters = 4096)
        => new(api, bridge ?? ((luid, vendor, device, maximum) =>
        {
            Assert.Equal(0x8086U, vendor); Assert.Equal(0x1234U, device); Assert.Equal(64U, maximum);
            return Bound(Physical(0, luid.LowPart == 7 ? FirstPnp : SecondPnp));
        }), timestamp ?? (() => 100), frequency ?? (() => 10_000_000), maximumEngineNameCharacters);
    private static WindowsGpuIdentityRead Bound(params WindowsGpuPhysicalIdentity[] identities)
        => new(SamplingObservationStatus.Current, 0, identities, 0);
    private static WindowsGpuPhysicalIdentity Physical(uint index, string id)
        => new(index, id, id.StartsWith("PCI", StringComparison.Ordinal) ? WindowsGpuPnpKind.Pci : WindowsGpuPnpKind.Other,
            true, false, 0, 0, 0, 0, 0x8086, 0x1234, 0, 0, 0,
            WindowsGpuIdentityEvidence.KmtLuidOpen | WindowsGpuIdentityEvidence.KmtHardwarePnpKey
            | WindowsGpuIdentityEvidence.CmExactDevNode | WindowsGpuIdentityEvidence.CmCanonicalInstanceRoundTrip
            | WindowsGpuIdentityEvidence.KmtDeviceIds,
            id.StartsWith("PCI", StringComparison.Ordinal) ? WindowsGpuPhysicalIdentityStatus.Bound : WindowsGpuPhysicalIdentityStatus.NonPci, 0)
            { DriverVersion = "1" };

    private sealed record RecordedAdapter
    {
        internal bool Npu { get; init; } = true;
        internal bool Hardware { get; init; } = true;
        internal uint Luid { get; init; } = 7;
        internal uint Vendor { get; init; } = 0x8086;
        internal uint EngineCount { get; init; } = 2;
        internal uint PhysicalCount { get; init; } = 1;
    }

    private sealed class RecordedApi(RecordedAdapter[] adapters) : IWindowsNpuDxCoreEngineApi
    {
        internal string? Failure { get; set; }
        internal string? Change { get; set; }
        internal bool SameQiPointer { get; init; }
        internal uint? FailedEngine { get; init; }
        internal string EngineName { get; init; } = "Compute";
        internal Action? AfterQuery { get; set; }
        internal Action? AfterName { get; set; }
        internal Guid Filter { get; private set; }
        internal int FactoryCalls { get; private set; }
        internal int ListCalls { get; private set; }
        internal int QiCalls { get; private set; }
        internal int NameCalls { get; private set; }
        internal List<nint> Released { get; } = [];
        internal List<(uint State, uint Physical, uint Engine, nuint InputSize, nuint OutputSize)> Queries { get; } = [];
        public int CreateFactory(out nint factory) { FactoryCalls++; factory = 1; return Failure == "factory" ? -1 : 0; }
        public int CreateAdapterList(nint factory, Guid attribute, out nint list) { ListCalls++; Filter = attribute; list = 2; return Failure == "list" ? -1 : 0; }
        public uint GetAdapterCount(nint list) => Failure == "adapter-count" ? 257U : checked((uint)adapters.Length);
        public bool IsStale(nint list) => Failure == "stale" || Change == "stale";
        public int GetAdapter(nint list, uint index, out nint adapter) { adapter = checked((nint)(10 + index)); return Failure == "adapter" ? -1 : 0; }
        public bool IsValid(nint adapter) => Failure != "invalid" && Change != "invalid";
        public bool IsAttributeSupported(nint adapter, Guid attribute) => Row(adapter).Npu && Change != "attribute";
        private RecordedAdapter Row(nint adapter) => adapters[checked((int)adapter - (adapter >= 100 ? 100 : 10))];
        public bool IsPropertySupported(nint adapter, WindowsNpuDxCoreProperty property)
            => !(property == WindowsNpuDxCoreProperty.PhysicalAdapterCount && Failure == "physical-unsupported")
                && !(property == WindowsNpuDxCoreProperty.AdapterEngineCount && Failure == "count-unsupported")
                && !(property == WindowsNpuDxCoreProperty.AdapterEngineName && Failure == "name-unsupported");
        public int GetPropertySize(nint adapter, WindowsNpuDxCoreProperty property, out nuint size)
        { Assert.Equal(WindowsNpuDxCoreProperty.PhysicalAdapterCount, property); size = Failure == "physical-size" ? (nuint)8 : (nuint)4; return 0; }
        public int GetProperty(nint adapter, WindowsNpuDxCoreProperty property, nuint size, nint data)
        {
            var row = Row(adapter);
            switch (property)
            {
                case WindowsNpuDxCoreProperty.IsHardware:
                    Assert.Equal((nuint)1, size); Marshal.WriteByte(data, row.Hardware && Change != "hardware" ? (byte)1 : (byte)0); break;
                case WindowsNpuDxCoreProperty.InstanceLuid:
                    Assert.Equal((nuint)8, size); Marshal.StructureToPtr(new AdapterLuid { LowPart = Failure == "zero-luid" ? 0U : Change == "luid" ? 99U : row.Luid }, data, false); break;
                case WindowsNpuDxCoreProperty.HardwareId:
                    Assert.Equal((nuint)16, size); Marshal.StructureToPtr(new WindowsNpuDxCoreHardwareId { VendorId = row.Vendor, DeviceId = 0x1234 }, data, false); break;
                case WindowsNpuDxCoreProperty.PhysicalAdapterCount:
                    Assert.Equal((nuint)4, size); Marshal.WriteInt32(data, checked((int)(Change == "physical" ? 2U : row.PhysicalCount))); break;
                case WindowsNpuDxCoreProperty.DriverVersion:
                    Assert.Equal((nuint)8, size); Marshal.WriteInt64(data, Change == "driver" ? 2 : 1); break;
                default: throw new InvalidOperationException("Unexpected property.");
            }
            return 0;
        }
        public int QueryAdapter1(nint adapter, out nint adapter1)
        { QiCalls++; adapter1 = SameQiPointer ? adapter : adapter + 90; return Failure == "qi" ? -1 : 0; }
        public int GetPropertyWithInput(nint adapter1, WindowsNpuDxCoreProperty property, nuint inputSize, nint input, nuint outputSize, nint output)
        {
            if (property == WindowsNpuDxCoreProperty.AdapterEngineCount)
            {
                Assert.Equal((nuint)4, inputSize); Assert.Equal((nuint)4, outputSize); Assert.Equal(0, Marshal.ReadInt32(input));
                if (Failure == "count-throw") throw new COMException("Recorded count failure.");
                if (Failure == "count-error") return -1;
                Marshal.WriteInt32(output, checked((int)(Change == "count" ? 3U : Row(adapter1).EngineCount))); return 0;
            }
            Assert.Equal(WindowsNpuDxCoreProperty.AdapterEngineName, property);
            NameCalls++;
            Assert.Equal((nuint)Marshal.SizeOf<WindowsNpuDxCoreEngineNameInput>(), inputSize); Assert.Equal((nuint)4, outputSize);
            var details = Marshal.PtrToStructure<WindowsNpuDxCoreEngineNameInput>(input);
            Assert.Equal(0U, details.AdapterEngineIndex.PhysicalAdapterIndex);
            Assert.True(details.AdapterEngineIndex.EngineIndex < Row(adapter1).EngineCount);
            if (Failure == "name-throw") throw new COMException("Recorded optional name failure.");
            if (Failure == "name-error") return -1;
            Marshal.WriteInt32(output, Failure == "name-length" ? 4097 : checked(EngineName.Length + 1));
            if (details.EngineName != 0)
            {
                var name = Failure == "name-invalid-utf16" ? new[] { '\ud800', 'o', 'm', 'p', 'u', 't', 'e', '\0' } : (EngineName + '\0').ToCharArray();
                Assert.True(details.EngineNameLength >= name.Length);
                Marshal.Copy(name, 0, details.EngineName, name.Length);
                if (Failure == "name-return-length") Marshal.WriteInt32(output, checked((int)details.EngineNameLength + 1));
            }
            AfterName?.Invoke(); return 0;
        }
        public bool IsQueryStateSupported(nint adapter, WindowsNpuDxCoreState state)
        { Assert.Equal(WindowsNpuDxCoreState.AdapterEngineRunningTimeMicroseconds, state); return Failure != "state-unsupported"; }
        public int QueryState(nint adapter, WindowsNpuDxCoreState state, nuint inputSize, nint input, nuint outputSize, nint output)
        {
            var details = Marshal.PtrToStructure<WindowsNpuDxCoreEngineIndex>(input);
            Queries.Add(((uint)state, details.PhysicalAdapterIndex, details.EngineIndex, inputSize, outputSize));
            AfterQuery?.Invoke();
            if (FailedEngine is null || FailedEngine == details.EngineIndex)
            {
                if (Failure == "state-throw") throw new COMException("Recorded state failure.");
                if (Failure == "state-error") return -1;
            }
            Marshal.WriteInt64(output, details.EngineIndex == 0 ? 0 : -1); return 0;
        }
        public void Release(nint instance) => Released.Add(instance);
    }
}
