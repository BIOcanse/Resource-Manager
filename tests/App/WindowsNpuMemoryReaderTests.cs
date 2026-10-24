using System.Runtime.InteropServices;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Windows.Devices.Npu;
using ResourceManager.ReadWrite.Windows.Gpu;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsNpuMemoryReaderTests
{
    private const string FirstId = @"PCI\VEN_8086&DEV_1234\ONE";
    private const string SecondId = @"PCI\VEN_8086&DEV_1234\TWO";
    private static readonly NpuMemoryCatalogIdentity First = new(FirstId);

    [Theory]
    [InlineData(0x8086U)]
    [InlineData(0x1022U)]
    public void ExactNpuHardwareReadsTwoDomainsOnceAndKeepsZeroAndFullUnsignedWidth(uint vendor)
    {
        var api = new RecordedApi([new() { VendorId = vendor }]);
        var reader = new WindowsNpuMemoryReader(api, (luid, expectedVendor, device, maximum) =>
        {
            Assert.Equal(7U, luid.LowPart); Assert.Equal(vendor, expectedVendor);
            Assert.Equal(0x1234U, device); Assert.Equal(64U, maximum);
            return Bound(Physical(0, FirstId));
        });
        var row = Assert.Single(Read(reader, NpuMemoryFields.All));

        Assert.Equal(First.ExactIdentityKey, row.DeviceIdentity);
        Assert.Equal(0UL, row.DedicatedCommitted); Assert.Equal(ulong.MaxValue, row.DedicatedResident);
        Assert.Equal(9_007_199_254_740_993UL, row.SharedCommitted); Assert.Equal(17UL, row.SharedResident);
        Assert.False(row.RequiresTopologyRefresh);
        Assert.Equal(WindowsNpuDxCoreEnumeration.NpuAttribute, api.Filter);
        Assert.Equal(new[] { WindowsNpuDxCoreMemoryType.Dedicated, WindowsNpuDxCoreMemoryType.Shared }, api.Queries.Select(static query => query.Type));
        Assert.All(api.Queries, static query => { Assert.Equal(2U, query.State); Assert.Equal(0U, query.Index);
            Assert.Equal((nuint)8, query.InputSize); Assert.Equal((nuint)16, query.OutputSize); });
        Assert.Equal(new nint[] { 10, 2, 1 }, api.Released);
    }

    [Theory]
    [InlineData(NpuMemoryFields.DedicatedCommitted, 0U)]
    [InlineData(NpuMemoryFields.DedicatedResident, 0U)]
    [InlineData(NpuMemoryFields.SharedCommitted, 1U)]
    [InlineData(NpuMemoryFields.SharedResident, 1U)]
    public void ASelectedScalarReadsItsDomainOnceAndDoesNotPublishUnrequestedSibling(NpuMemoryFields field,
        uint domain)
    {
        var api = new RecordedApi([new()]);
        var row = Assert.Single(Read(Reader(api), field));
        Assert.Equal((WindowsNpuDxCoreMemoryType)domain, Assert.Single(api.Queries).Type);
        Assert.Equal(field.HasFlag(NpuMemoryFields.DedicatedCommitted), row.DedicatedCommitted.HasValue);
        Assert.Equal(field.HasFlag(NpuMemoryFields.DedicatedResident), row.DedicatedResident.HasValue);
        Assert.Equal(field.HasFlag(NpuMemoryFields.SharedCommitted), row.SharedCommitted.HasValue);
        Assert.Equal(field.HasFlag(NpuMemoryFields.SharedResident), row.SharedResident.HasValue);
    }

    [Theory]
    [InlineData(0U, false)]
    [InlineData(1U, false)]
    [InlineData(0U, true)]
    public void FailedMemoryDomainDoesNotClearOtherDomainOrRetry(uint failureType, bool throws)
    {
        var failure = (WindowsNpuDxCoreMemoryType)failureType;
        var api = new RecordedApi([new()]) { FailedDomain = failure, ThrowDomainFailure = throws };
        var row = Assert.Single(Read(Reader(api), NpuMemoryFields.All));
        Assert.Equal(2, api.Queries.Count);
        Assert.Equal(failure != WindowsNpuDxCoreMemoryType.Dedicated, row.DedicatedCommitted.HasValue);
        Assert.Equal(failure != WindowsNpuDxCoreMemoryType.Shared, row.SharedResident.HasValue);
        Assert.False(row.RequiresTopologyRefresh);
        Assert.Equal(new nint[] { 10, 2, 1 }, api.Released);
    }

    [Theory]
    [InlineData("count-unsupported")]
    [InlineData("count-size")]
    [InlineData("count-read")]
    [InlineData("query-unsupported")]
    public void UnsupportedMemoryQueryNeverInventsZeroOrQueriesState(string failure)
    {
        var api = new RecordedApi([new()]) { Failure = failure };
        var row = Assert.Single(Read(Reader(api), NpuMemoryFields.All));
        AssertEmpty(row); Assert.False(row.RequiresTopologyRefresh);
        Assert.Empty(api.Queries); Assert.Equal(new nint[] { 10, 2, 1 }, api.Released);
    }

    [Theory]
    [InlineData("factory")]
    [InlineData("list")]
    [InlineData("count-bound")]
    [InlineData("stale")]
    [InlineData("adapter")]
    [InlineData("invalid")]
    [InlineData("zero-luid")]
    public void NativeEnumerationFailureReleasesEveryAcquiredObject(string failure)
    {
        var api = new RecordedApi([new()]) { Failure = failure };
        var row = Assert.Single(Read(Reader(api), NpuMemoryFields.All));
        AssertEmpty(row); Assert.Equal(failure is "stale" or "invalid" or "zero-luid", row.RequiresTopologyRefresh); Assert.Empty(api.Queries);
        Assert.Equal(failure == "factory" ? new nint[] { 1 }
            : failure is "list" or "count-bound" or "stale" ? new nint[] { 2, 1 }
            : new nint[] { 10, 2, 1 }, api.Released);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void SoftwareAndNonNpuCannotPublishMemory(bool npu, bool hardware)
    {
        var api = new RecordedApi([new() { Npu = npu, Hardware = hardware }]);
        var reader = new WindowsNpuMemoryReader(api, (_, _, _, _) => throw new InvalidOperationException("Rejected before bridge."));
        AssertEmpty(Assert.Single(Read(reader, NpuMemoryFields.All))); Assert.Empty(api.Queries);
    }

    [Theory]
    [InlineData("invalid-after")]
    [InlineData("stale-after")]
    [InlineData("luid-after")]
    [InlineData("count-after")]
    [InlineData("hardware-after")]
    [InlineData("attribute-after")]
    public void IdentityOrDirectoryChangeAfterQueryDiscardsAllValuesAndReleases(string failure)
    {
        var api = new RecordedApi([new()]) { Failure = failure };
        var row = Assert.Single(Read(Reader(api), NpuMemoryFields.All));
        AssertEmpty(row); Assert.True(row.RequiresTopologyRefresh);
        Assert.Equal(new nint[] { 10, 2, 1 }, api.Released);
    }

    [Fact]
    public void ChangedCanonicalBridgeAfterQueryCannotPublishEvenWithSameVendorAndName()
    {
        var api = new RecordedApi([new()]);
        var reader = new WindowsNpuMemoryReader(api, (_, _, _, _) => Bound(Physical(0, api.Queries.Count == 0 ? FirstId : SecondId)));
        var row = Assert.Single(Read(reader, NpuMemoryFields.All));
        AssertEmpty(row); Assert.True(row.RequiresTopologyRefresh);
    }

    [Theory]
    [InlineData(0U, 0U)]
    [InlineData(2U, 0U)]
    [InlineData(1U, 1U)]
    public void CountMismatchOrNonzeroPhysicalIndexCannotQuery(uint count, uint index)
    {
        var api = new RecordedApi([new() { PhysicalCount = count }]);
        var reader = new WindowsNpuMemoryReader(api, (_, _, _, _) => Bound(Physical(index, FirstId)));
        AssertEmpty(Assert.Single(Read(reader, NpuMemoryFields.All))); Assert.Empty(api.Queries);
    }

    [Fact]
    public void StablePhysicalCountLimitationDoesNotRequestAnUnchangedDirectoryAgain()
    {
        var api = new RecordedApi([new() { PhysicalCount = 2 }]);
        var reader = new WindowsNpuMemoryReader(api, (_, _, _, _) => Bound(Physical(0, FirstId)));
        for (var i = 0; i < 20; i++)
        {
            var row = Assert.Single(Read(reader, NpuMemoryFields.All));
            AssertEmpty(row); Assert.False(row.RequiresTopologyRefresh);
        }
        Assert.Empty(api.Queries);
    }

    [Fact]
    public void MultiplePhysicalMembersRemainKnownButHaveNoDynamicValue()
    {
        var api = new RecordedApi([new() { PhysicalCount = 2 }]);
        var reader = new WindowsNpuMemoryReader(api, (_, _, _, _) => Bound(Physical(0, FirstId), Physical(1, SecondId)));
        var row = Assert.Single(Read(reader, NpuMemoryFields.All));
        AssertEmpty(row); Assert.False(row.RequiresTopologyRefresh); Assert.Empty(api.Queries);
    }

    [Fact]
    public void TemplateBudget64Rejects65ButBudget65CanRead65ExactSinglePhysicalTargets()
    {
        var api = new RecordedApi([new() { PhysicalCount = 65 }]);
        var row = Assert.Single(Read(Reader(api), NpuMemoryFields.All));
        AssertEmpty(row); Assert.False(row.RequiresTopologyRefresh); Assert.Empty(api.Queries);
        var devices = Enumerable.Range(0, 65).Select(index => new NpuMemoryCatalogIdentity(FirstId + index)).ToArray();
        var requested = devices.ToDictionary(static device => device.ExactIdentityKey, static _ => NpuMemoryFields.All);
        var unopened = new RecordedApi([new()]);
        var reads = Reader(unopened).Read(devices, requested, 64);
        Assert.Equal(65, reads.Count); Assert.Equal(0, unopened.FactoryCalls);
        Assert.All(reads, static read => { AssertEmpty(read); Assert.False(read.RequiresTopologyRefresh); });
        var allowed = new RecordedApi(Enumerable.Range(0, 65).Select(index => new RecordedAdapter
            { LuidLow = checked(7U + (uint)index) }).ToArray());
        var reader = new WindowsNpuMemoryReader(allowed, (luid, _, _, _) => Bound(Physical(0, FirstId + (luid.LowPart - 7))));
        var current = reader.Read(devices, requested, 65);
        Assert.Equal(65, current.Count); Assert.Equal(130, allowed.Queries.Count);
        Assert.All(current, static read => { Assert.Equal(0UL, read.DedicatedCommitted);
            Assert.Equal(ulong.MaxValue, read.DedicatedResident); Assert.NotNull(read.SharedCommitted);
            Assert.NotNull(read.SharedResident); Assert.False(read.RequiresTopologyRefresh); });
        Assert.Equal(67, allowed.Released.Count);
    }

    [Fact]
    public void ZeroEffectiveTemplateBudgetMeansNoDemandAndDoesNotQuery()
    {
        var unopened = new RecordedApi([new()]);
        var unknown = Reader(unopened).Read([First],
            new Dictionary<string, NpuMemoryFields> { [First.ExactIdentityKey] = NpuMemoryFields.All }, 0);
        Assert.Equal(0, unopened.FactoryCalls);
        Assert.Empty(unknown);
    }

    [Fact]
    public void ExactCmRoundTripEvidenceIsRequiredAndFailedBridgeCannotReadMemory()
    {
        foreach (var read in new[] { WindowsGpuIdentityRead.Invalid, Bound(),
            Bound(Physical(0, FirstId) with { Evidence = WindowsGpuIdentityEvidence.KmtLuidOpen }),
            Bound(Physical(0, FirstId)) with { SkippedCount = 1 },
            Bound(Physical(0, FirstId) with { Status = WindowsGpuPhysicalIdentityStatus.Conflict }) })
        {
            var api = new RecordedApi([new()]);
            AssertEmpty(Assert.Single(Read(new WindowsNpuMemoryReader(api, (_, _, _, _) => read), NpuMemoryFields.All)));
            Assert.Empty(api.Queries);
        }
    }

    [Fact]
    public void TwoSameNamedTargetsStaySeparateAndNonPciUsesSameExactBridge()
    {
        const string nonPci = @"ACPI\NPU1234\0";
        var second = new NpuMemoryCatalogIdentity(nonPci.ToLowerInvariant());
        var api = new RecordedApi([new(), new() { LuidLow = 8 }]);
        var reader = new WindowsNpuMemoryReader(api, (luid, _, _, _) => Bound(luid.LowPart == 7
            ? Physical(0, FirstId) : Physical(0, nonPci) with { Status = WindowsGpuPhysicalIdentityStatus.NonPci, PnpKind = WindowsGpuPnpKind.Other }));
        var result = reader.Read([First, second], new Dictionary<string, NpuMemoryFields>
        { [First.ExactIdentityKey] = NpuMemoryFields.All, [second.ExactIdentityKey] = NpuMemoryFields.SharedCommitted }, 64);
        Assert.Equal(2, result.Count); Assert.Equal(2, result.Select(static row => row.DeviceIdentity).Distinct().Count());
        Assert.All(result, static row => Assert.NotNull(row.SharedCommitted)); Assert.Equal(3, api.Queries.Count);
        Assert.Equal(new nint[] { 10, 11, 2, 1 }, api.Released);
    }

    [Fact]
    public void DuplicateLogicalMappingsAreRejectedBeforeAnyMemoryQuery()
    {
        foreach (var low in new[] { 7U, 8U })
        {
            var api = new RecordedApi([new(), new() { LuidLow = low }]);
            AssertEmpty(Assert.Single(Read(Reader(api), NpuMemoryFields.All)));
            Assert.Empty(api.Queries); Assert.Equal(4, api.Released.Count);
        }
    }

    [Fact]
    public void UnrequestedAndCanceledReadsDoNotOpenAndMidQueryCancellationReleases()
    {
        var api = new RecordedApi([new()]); var reader = Reader(api);
        Assert.Empty(reader.Read([First], new Dictionary<string, NpuMemoryFields>(), 64)); Assert.Equal(0, api.FactoryCalls);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => Read(reader, NpuMemoryFields.All, cancellation.Token));
        Assert.Equal(0, api.FactoryCalls);
        using var duringQuery = new CancellationTokenSource(); api.AfterQuery = duringQuery.Cancel;
        Assert.Throws<OperationCanceledException>(() => Read(reader, NpuMemoryFields.All, duringQuery.Token));
        Assert.Single(api.Queries); Assert.Equal(new nint[] { 10, 2, 1 }, api.Released);
    }

    [Fact]
    public void MissingTargetAndSuccessfulEmptyEnumerationCannotRetainAValue()
    {
        foreach (var api in new[] { new RecordedApi([]), new RecordedApi([new()]) })
        {
            var reader = new WindowsNpuMemoryReader(api, (_, _, _, _) => Bound(Physical(0, SecondId)));
            var row = Assert.Single(Read(reader, NpuMemoryFields.All));
            AssertEmpty(row); Assert.True(row.RequiresTopologyRefresh); Assert.Empty(api.Queries);
        }
    }

    [Fact]
    public void MemoryAbiMatchesOfficialSdkHeaderWithoutAdapter1OrByProcess()
    {
        Assert.Equal(15U, (uint)WindowsNpuDxCoreProperty.PhysicalAdapterCount);
        Assert.Equal(2U, (uint)WindowsNpuDxCoreState.AdapterMemoryUsageBytes);
        Assert.Equal(0U, (uint)WindowsNpuDxCoreMemoryType.Dedicated); Assert.Equal(1U, (uint)WindowsNpuDxCoreMemoryType.Shared);
        Assert.Equal(8, Marshal.SizeOf<WindowsNpuDxCoreMemoryQueryInput>());
        Assert.Equal(4, Marshal.OffsetOf<WindowsNpuDxCoreMemoryQueryInput>(nameof(WindowsNpuDxCoreMemoryQueryInput.MemoryType)).ToInt32());
        Assert.Equal(16, Marshal.SizeOf<WindowsNpuDxCoreMemoryUsage>());
        Assert.Equal(8, Marshal.OffsetOf<WindowsNpuDxCoreMemoryUsage>(nameof(WindowsNpuDxCoreMemoryUsage.Resident)).ToInt32());
        Assert.Equal(7, (int)WindowsNpuDxCoreAdapterSlot.GetPropertySize);
        Assert.Equal(8, (int)WindowsNpuDxCoreAdapterSlot.IsQueryStateSupported);
        Assert.Equal(9, (int)WindowsNpuDxCoreAdapterSlot.QueryState);
    }

    private static IReadOnlyList<NpuMemoryRead> Read(WindowsNpuMemoryReader reader, NpuMemoryFields fields, CancellationToken token = default)
        => reader.Read([First], new Dictionary<string, NpuMemoryFields> { [First.ExactIdentityKey] = fields }, 64, token);
    private static WindowsNpuMemoryReader Reader(RecordedApi api)
        => new(api, (_, _, _, _) => Bound(Physical(0, FirstId)));
    private static void AssertEmpty(NpuMemoryRead row)
    { Assert.Null(row.DedicatedCommitted); Assert.Null(row.DedicatedResident); Assert.Null(row.SharedCommitted); Assert.Null(row.SharedResident); }
    private static WindowsGpuIdentityRead Bound(params WindowsGpuPhysicalIdentity[] identities)
        => new(SamplingObservationStatus.Current, 0, identities, 0);
    private static WindowsGpuPhysicalIdentity Physical(uint index, string id)
        => new(index, id, WindowsGpuPnpKind.Pci, true, false, 0, 0, 0, 0, 0x8086, 0x1234, 0, 0, 0,
            WindowsGpuIdentityEvidence.KmtLuidOpen | WindowsGpuIdentityEvidence.KmtHardwarePnpKey
            | WindowsGpuIdentityEvidence.CmExactDevNode | WindowsGpuIdentityEvidence.CmCanonicalInstanceRoundTrip
            | WindowsGpuIdentityEvidence.KmtDeviceIds, WindowsGpuPhysicalIdentityStatus.Bound, 0);

    private sealed record RecordedAdapter
    {
        internal bool Npu { get; init; } = true;
        internal bool Hardware { get; init; } = true;
        internal uint LuidLow { get; init; } = 7;
        internal uint VendorId { get; init; } = 0x8086;
        internal uint PhysicalCount { get; init; } = 1;
    }

    private sealed class RecordedApi(RecordedAdapter[] adapters) : IWindowsNpuDxCoreMemoryApi
    {
        internal string? Failure { get; init; }
        internal WindowsNpuDxCoreMemoryType? FailedDomain { get; init; }
        internal bool ThrowDomainFailure { get; init; }
        internal Action? AfterQuery { get; set; }
        internal Guid Filter { get; private set; }
        internal int FactoryCalls { get; private set; }
        internal List<nint> Released { get; } = [];
        internal List<(uint State, uint Index, WindowsNpuDxCoreMemoryType Type, nuint InputSize, nuint OutputSize)> Queries { get; } = [];
        public int CreateFactory(out nint factory) { FactoryCalls++; factory = 1; return Failure == "factory" ? -1 : 0; }
        public int CreateAdapterList(nint factory, Guid attribute, out nint list) { Filter = attribute; list = 2; return Failure == "list" ? -1 : 0; }
        public uint GetAdapterCount(nint list) => Failure == "count-bound" ? 257U : checked((uint)adapters.Length);
        public bool IsStale(nint list) => Failure == "stale" || (Queries.Count != 0 && Failure == "stale-after");
        public int GetAdapter(nint list, uint index, out nint adapter) { adapter = checked((nint)(10 + index)); return Failure == "adapter" ? -1 : 0; }
        public bool IsValid(nint adapter) => Failure != "invalid" && !(Queries.Count != 0 && Failure == "invalid-after");
        public bool IsAttributeSupported(nint adapter, Guid attribute)
            => adapters[checked((int)adapter - 10)].Npu && !(Queries.Count != 0 && Failure == "attribute-after");
        public bool IsPropertySupported(nint adapter, WindowsNpuDxCoreProperty property)
            => !(property == WindowsNpuDxCoreProperty.PhysicalAdapterCount && Failure == "count-unsupported");
        public int GetPropertySize(nint adapter, WindowsNpuDxCoreProperty property, out nuint size)
        { Assert.Equal(WindowsNpuDxCoreProperty.PhysicalAdapterCount, property); size = Failure == "count-size" ? (nuint)8 : (nuint)4; return 0; }
        public int GetProperty(nint adapter, WindowsNpuDxCoreProperty property, nuint size, nint data)
        {
            var row = adapters[checked((int)adapter - 10)];
            switch (property)
            {
                case WindowsNpuDxCoreProperty.IsHardware:
                    Assert.Equal((nuint)1, size); Marshal.WriteByte(data, row.Hardware && !(Queries.Count != 0 && Failure == "hardware-after") ? (byte)1 : (byte)0); break;
                case WindowsNpuDxCoreProperty.InstanceLuid:
                    Assert.Equal((nuint)8, size); Marshal.StructureToPtr(new AdapterLuid { LowPart = Failure == "zero-luid" ? 0U
                        : Queries.Count != 0 && Failure == "luid-after" ? 99U : row.LuidLow }, data, false); break;
                case WindowsNpuDxCoreProperty.HardwareId:
                    Assert.Equal((nuint)16, size); Marshal.StructureToPtr(new WindowsNpuDxCoreHardwareId { VendorId = row.VendorId, DeviceId = 0x1234 }, data, false); break;
                case WindowsNpuDxCoreProperty.PhysicalAdapterCount:
                    Assert.Equal((nuint)4, size); if (Failure == "count-read") return -1;
                    Marshal.WriteInt32(data, checked((int)(Queries.Count != 0 && Failure == "count-after" ? 2U : row.PhysicalCount))); break;
                default: throw new InvalidOperationException("Unexpected property.");
            }
            return 0;
        }
        public bool IsQueryStateSupported(nint adapter, WindowsNpuDxCoreState state)
        { Assert.Equal(WindowsNpuDxCoreState.AdapterMemoryUsageBytes, state); return Failure != "query-unsupported"; }
        public int QueryState(nint adapter, WindowsNpuDxCoreState state, nuint inputSize, nint input, nuint outputSize, nint output)
        {
            var details = Marshal.PtrToStructure<WindowsNpuDxCoreMemoryQueryInput>(input);
            Queries.Add(((uint)state, details.PhysicalAdapterIndex, details.MemoryType, inputSize, outputSize));
            AfterQuery?.Invoke();
            if (FailedDomain == details.MemoryType)
            { if (ThrowDomainFailure) throw new COMException("Recorded domain unsupported."); return -1; }
            Marshal.StructureToPtr(details.MemoryType == WindowsNpuDxCoreMemoryType.Dedicated
                ? new WindowsNpuDxCoreMemoryUsage { Committed = 0, Resident = ulong.MaxValue }
                : new WindowsNpuDxCoreMemoryUsage { Committed = 9_007_199_254_740_993, Resident = 17 }, output, false);
            return 0;
        }
        public void Release(nint instance) => Released.Add(instance);
    }
}
