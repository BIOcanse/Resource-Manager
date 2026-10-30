using System.Buffers.Binary;
using System.Runtime.InteropServices;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class NvmePayloadContractTests
{
    private const string Device = StorageInformationCollectorTests.Identity;
    private const string Counter = "storage_all_nvme_media_errors_reported";
    private const string Raw = "storage_all_nvme_health_log_raw";

    [Fact]
    public void ParserKeepsAll128BitsLittleEndianAndCopiesExactRawLog()
    {
        var log = Enumerable.Range(0, 512).Select(i => (byte)i).ToArray();
        UInt128 value = ((UInt128)0xFEDCBA9876543210UL << 64) | 0x0123456789ABCDEFUL;
        BinaryPrimitives.WriteUInt128LittleEndian(log.AsSpan(160, 16), value);
        var result = WindowsNvmeHealthReader.ParseHealthLog(log)!;
        Assert.Equal(value, result.WideIntegerValues[MetricFieldCode.NvmeMediaErrors]);
        Assert.Equal(10, result.WideIntegerValues.Count);
        Assert.Equal(log, result.BytesValues[MetricFieldCode.NvmeHealthLogRaw]);
        log[511] ^= 1;
        Assert.NotEqual(log[511], result.BytesValues[MetricFieldCode.NvmeHealthLogRaw][511]);
        Assert.Null(WindowsNvmeHealthReader.ParseHealthLog(new byte[511]));
    }

    [Fact]
    public void UnreportedDataUnitsAreEmptyWhileOtherCounterZeroIsValid()
    {
        var result = WindowsNvmeHealthReader.ParseHealthLog(new byte[512])!;
        Assert.False(result.WideIntegerValues.ContainsKey(MetricFieldCode.NvmeDataUnitsRead));
        Assert.False(result.WideIntegerValues.ContainsKey(MetricFieldCode.NvmeDataUnitsWritten));
        Assert.Equal((UInt128)0, result.WideIntegerValues[MetricFieldCode.NvmeMediaErrors]);
        Assert.Equal(8, result.WideIntegerValues.Count);
        Assert.Equal(512, result.BytesValues[MetricFieldCode.NvmeHealthLogRaw].Length);
    }

    [Fact]
    public void ExistingNativePlanPublishesOnlyRequestedPayloadsAndClearsFailureThenRecovers()
    {
        using var fixture = new Fixture();
        var current = fixture.Commit(UInt128.MaxValue, Log(), 1);
        var fields = Assert.Single(MetricDeviceFields.Read(current, Device, new HashSet<string> { Counter, Raw })).Fields;
        var counter = fields.Single(f => f.Name == Counter);
        Assert.Equal(MetricInteger.FromUnsigned128(UInt128.MaxValue), counter.IntegerValue);
        Assert.Equal("unsigned128", counter.ValueKind);
        Assert.Null(counter.NumericValue);
        var raw = fields.Single(f => f.Name == Raw);
        Assert.Equal("bytes", raw.ValueKind);
        Assert.Equal(Log(), raw.BytesValue);
        Assert.Null(raw.NumericValue);
        Assert.Null(raw.IntegerValue);
        Assert.All(fields, f => Assert.Equal(SamplingObservationStatus.Current, f.Status));
        Assert.Equal(2, fixture.LastPlan!.Metrics.Count(m => (m.Flags & (uint)NativeMetricSnapshotMetricPlanFlags.Selected) != 0));
        Assert.Equal("windows.nvme-health", fixture.Catalog.SourceIds[Assert.Single(fixture.LastPlan.Sources).SourceHandle]);
        Assert.Equal("unsigned128", MetricDeviceFields.Catalog(current).Single(d => d.DeviceIdentity == Device).Fields.Single(f => f.Name == Counter).ValueKind);
        var failed = fixture.Commit(null, null, 2);
        Assert.All(Assert.Single(MetricDeviceFields.Read(failed, Device, new HashSet<string> { Counter, Raw })).Fields, f =>
        {
            Assert.Equal(SamplingObservationStatus.Unavailable, f.Status);
            Assert.Null(f.IntegerValue); Assert.Null(f.NumericValue); Assert.Null(f.BytesValue);
        });
        var empty = fixture.Workspace.ReadCommitted().Metrics.Single(m => m.MetricHandle == fixture.RawHandle);
        Assert.Equal(0U, empty.ByteCount);
        Assert.Equal(0UL, empty.ValueHighBits);
        Assert.Equal(MetricInteger.Parse("0"), Assert.Single(MetricDeviceFields.Read(fixture.Commit(0, Log(), 3), Device,
            new HashSet<string> { Counter })).Fields.Single().IntegerValue);
    }

    [Fact]
    public void SemanticFingerprintChangesForHighLimbAndRawReservedByte()
    {
        using var fixture = new Fixture();
        fixture.Commit(1, Log(), 1);
        var first = fixture.Workspace.ReadCommitted();
        fixture.Commit(((UInt128)1 << 100) | 1, Log(), 2);
        var wide = fixture.Workspace.ReadCommitted();
        Assert.NotEqual(first.Header.SemanticFingerprint, wide.Header.SemanticFingerprint);
        var raw = Log(); raw[511] ^= 1;
        fixture.Commit(((UInt128)1 << 100) | 1, raw, 3);
        var bytes = fixture.Workspace.ReadCommitted();
        Assert.NotEqual(wide.Metrics.Single(m => m.MetricHandle == fixture.RawHandle).SemanticFingerprint,
            bytes.Metrics.Single(m => m.MetricHandle == fixture.RawHandle).SemanticFingerprint);
    }

    [Fact]
    public void DurableCatalogKeepsHandlesButDoesNotRestoreNvmePayloads()
    {
        using var fixture = new Fixture();
        fixture.Commit(UInt128.MaxValue, Log(), 1);
        var image = fixture.Workspace.ExportAndPersist(fixture.Now.AddSeconds(1));
        Assert.Contains(image.Rules, rule => rule.MetricHandle == fixture.CounterHandle && rule.ValueHighBits != 0);
        var envelope = File.ReadAllBytes(fixture.File);
        image.Rules[Array.FindIndex(image.Rules, rule => rule.MetricHandle == fixture.CounterHandle)].ValueHighBits ^= 1;
        new NativeMetricSnapshotPersistenceStore(fixture.File).Save(fixture.Compiled.HotPublish.ConfigurationGeneration, image.Catalog);
        Assert.Equal(envelope, File.ReadAllBytes(fixture.File));

        using var restarted = NativeMetricSnapshotHostConfiguration.CreateWorkspace(fixture.Compiled, fixture.File);
        var catalog = fixture.CreateCatalog(restarted);
        restarted.ReplaceCatalog(catalog, fixture.Now.AddSeconds(2));
        Assert.Equal(fixture.CounterHandle, catalog.MetricBindings.Values.Single(b => b.FieldCode == MetricFieldCode.NvmeMediaErrors).MetricHandle);
        Assert.All(restarted.ReadCommitted().Metrics.Where(m => m.MetricHandle == fixture.CounterHandle || m.MetricHandle == fixture.RawHandle), m =>
        {
            Assert.NotEqual((uint)NativeMetricSnapshotMetricStatus.Current, m.Status);
            Assert.Equal(0UL, m.ValueBits);
            Assert.Equal(0UL, m.ValueHighBits);
            Assert.Equal(0U, m.ByteCount);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NativeSubmissionRejectsMalformedPayloadWithoutChangingCommittedValue(int defect)
    {
        using var fixture = new Fixture();
        fixture.Commit(UInt128.MaxValue, Log(), 1);
        var before = fixture.Workspace.ReadCommitted().Header.SemanticFingerprint;
        var plan = fixture.Plan(2);
        var completion = fixture.Completion(plan, null, null, 2);
        var observations = completion.Observations.ToArray();
        int i = Array.FindIndex(observations, o => o.MetricHandle == fixture.RawHandle);
        if (defect == 0) observations[i].ByteCount = 513;
        if (defect == 1) observations[i].ValueHighBits = 1;
        if (defect == 2) observations[i].PayloadReserved = 1;
        if (defect == 3) MemoryMarshal.AsBytes(observations.AsSpan(i, 1))[^512] = 1;
        Assert.Throws<InvalidOperationException>(() => fixture.Workspace.CompleteSource(plan,
            completion with { Observations = [.. observations] }, fixture.Time(2)));
        Assert.Equal(before, fixture.Workspace.ReadCommitted().Header.SemanticFingerprint);
        fixture.Commit(7, Log(), 3);
    }

    private static byte[] Log() => Enumerable.Range(0, 512).Select(i => (byte)(i * 31 + 7)).ToArray();

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "nvme-payload-" + Guid.NewGuid().ToString("N"));
        internal string File { get; }
        internal DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;
        internal ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerMetricSnapshotPlan Compiled { get; }
            = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        internal NativeMetricSnapshotWorkspace Workspace { get; }
        internal NativeMetricSnapshotCatalogProjection Catalog { get; }
        internal NativeMetricSnapshotCollectionPlan? LastPlan { get; private set; }
        internal ulong CounterHandle => Catalog.MetricBindings.Values.Single(b => b.FieldCode == MetricFieldCode.NvmeMediaErrors).MetricHandle;
        internal ulong RawHandle => Catalog.MetricBindings.Values.Single(b => b.FieldCode == MetricFieldCode.NvmeHealthLogRaw).MetricHandle;
        internal Fixture()
        {
            Directory.CreateDirectory(directory);
            File = Path.Combine(directory, "cache.bin");
            Workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(Compiled, File);
            Catalog = CreateCatalog(Workspace);
            Workspace.ReplaceCatalog(Catalog, Now);
        }
        internal NativeMetricSnapshotCatalogProjection CreateCatalog(NativeMetricSnapshotWorkspace workspace)
            => NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(Compiled, [], [new(7, Device, 1) { SupportsNvmeHealth = true }], [], workspace.CreateCatalogHandleMap());
        internal DateTimeOffset Time(ulong sequence) => Now.AddMilliseconds((long)sequence * 100);
        internal NativeMetricSnapshotCollectionPlan Plan(ulong sequence)
            => Workspace.PlanCollection([CounterHandle, RawHandle], Catalog.Sources.Select(s => new NativeMetricSnapshotSourceRuntimeState(
                s.SourceHandle, 1, NativeMetricSnapshotSourceAvailability.Available)).ToArray(), false, Time(sequence));
        internal NativeMetricSnapshotSourceCompletion Completion(NativeMetricSnapshotCollectionPlan plan, UInt128? value, byte[]? raw, ulong sequence)
        {
            var disk = new DiskTemperatureSensor(7, "fixture", null, "fixture")
            {
                IdentityKey = Device,
                WideIntegerValues = value is { } count ? new Dictionary<MetricFieldCode, UInt128> { [MetricFieldCode.NvmeMediaErrors] = count } : new Dictionary<MetricFieldCode, UInt128>(),
                BytesValues = raw is { } bytes ? new Dictionary<MetricFieldCode, byte[]> { [MetricFieldCode.NvmeHealthLogRaw] = bytes } : new Dictionary<MetricFieldCode, byte[]>()
            };
            var source = Assert.Single(plan.Sources);
            return WindowsHardwareMetricSampler.CreateStorageCompletion(plan, source, new(source.SourceHandle, 1, sequence), Time(sequence),
                new(new("fixture", "Available", ""), [disk]));
        }
        internal HardwareMetricSnapshot Commit(UInt128? value, byte[]? raw, ulong sequence)
        {
            LastPlan = Plan(sequence);
            Workspace.CompleteSource(LastPlan, Completion(LastPlan, value, raw, sequence), Time(sequence));
            return NativeMetricSnapshotCommittedProjection.Create(frame: Workspace.ReadCommitted(), workspaceIdentity: 1);
        }
        public void Dispose()
        {
            Workspace.Dispose();
            foreach (var path in Directory.EnumerateFiles(directory)) System.IO.File.Delete(path);
            Directory.Delete(directory);
        }
    }
}
