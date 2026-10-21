using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace Resource_Manager_APP.Tests;

public sealed partial class DiskIdentityInformationCollectorTests
{
    [Fact]
    public void OneWindowProjectsSevenFieldsWithCurrentQueueAndWeightedLatency()
    {
        var start = Counters(0, 0, 0, 0, 0, 0, 0, 0);
        var end = Counters(4096, 2048, 100_000, 900_000, 5_000_000, 1, 9, 7);
        var metrics = WindowsDiskPerformanceProjection.Project(start, end, TimeSpan.FromSeconds(1))!;

        Assert.Equal(4096d, metrics.ValueFor(MetricFieldCode.PhysicalDiskReadBytesPerSecond));
        Assert.Equal(2048d, metrics.ValueFor(MetricFieldCode.PhysicalDiskWriteBytesPerSecond));
        Assert.Equal(1d, metrics.ValueFor(MetricFieldCode.PhysicalDiskReadIops));
        Assert.Equal(9d, metrics.ValueFor(MetricFieldCode.PhysicalDiskWriteIops));
        Assert.Equal(50d, metrics.ValueFor(MetricFieldCode.PhysicalDiskActivePercent));
        Assert.Equal(7d, metrics.ValueFor(MetricFieldCode.PhysicalDiskQueueLength));
        Assert.Equal(10d, metrics.ValueFor(MetricFieldCode.PhysicalDiskLatency));
    }

    [Fact]
    public void ZeroDeltasAreCurrentButZeroOperationsHasNoLatency()
    {
        var zero = Counters(0, 0, 0, 0, 0, 0, 0, 0);
        var idleAfterWindow = Counters(0, 0, 0, 0, 10_000_000, 0, 0, 0);
        var metrics = WindowsDiskPerformanceProjection.Project(zero, idleAfterWindow, TimeSpan.FromSeconds(1))!;

        Assert.Equal(0, metrics.ReadBytesPerSecond);
        Assert.Equal(0, metrics.WriteBytesPerSecond);
        Assert.Equal(0, metrics.ReadIops);
        Assert.Equal(0, metrics.WriteIops);
        Assert.Equal(0, metrics.ActivePercent);
        Assert.Equal(0, metrics.QueueLength);
        Assert.Null(metrics.LatencyMilliseconds);
        Assert.Null(WindowsDiskPerformanceProjection.Project(zero, zero, TimeSpan.Zero));
        Assert.Null(WindowsDiskPerformanceProjection.Project(Counters(0, 0, 0, 0, 0, 0, 1, 0), zero, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void NoReadOrWriteOperationsDoesNotImplyTheDiskWasIdle()
    {
        var start = Counters(0, 0, 0, 0, 0, 0, 0, 0);
        var end = Counters(0, 0, 0, 0, 0, 0, 0, 0);
        var metrics = WindowsDiskPerformanceProjection.Project(start, end, TimeSpan.FromSeconds(1))!;

        Assert.Equal(100, metrics.ActivePercent);
        Assert.Null(metrics.LatencyMilliseconds);
    }

    [Fact]
    public void StorageManagerProviderNameUsesTheDocumentedCaseSensitiveValue()
    {
        var start = Counters(0, 0, 0, 0, 0, 0, 0, 0, "partmgr");
        var end = Counters(10, 10, 10, 10, 0, 1, 1, 1, "partmgr");

        Assert.Null(WindowsDiskPerformanceProjection.Project(start, end, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task OpenedDiskHandleSuppliesExactVpdIdentityAndPairsBothEnableCalls()
    {
        var expectedObservedAt = new DateTimeOffset(2026, 10, 9, 12, 34, 56, TimeSpan.Zero);
        var calls = new PerformanceCalls
        {
            Samples = [Counters(0, 0, 0, 0, 0, 0, 0, 1), Counters(4096, 2048, 100_000, 900_000, 0, 1, 9, 7,
                queryTime: expectedObservedAt.UtcDateTime.ToFileTimeUtc())]
        };
        var read = await new WindowsDiskLayoutInformationNativeApi(calls).ReadPerformanceAsync(
            DiskLayoutInformationCollectorTests.DiskA, Candidates([Hardware()]), SelectedKeys(), TimeSpan.FromMilliseconds(1),
            TimeProvider.System, CancellationToken.None);

        Assert.Equal(HardwareId, read!.IdentityKey);
        Assert.Equal(7d, read.Metrics!.ValueFor(MetricFieldCode.PhysicalDiskQueueLength));
        Assert.Equal(10d, read.Metrics.ValueFor(MetricFieldCode.PhysicalDiskLatency));
        Assert.Equal(expectedObservedAt, read.Metrics.ObservedAt);
        Assert.Equal(new[] { "number", "identity0", "identity2", "performance", "performance", "off", "off", "number" }, calls.Order);
        Assert.Equal(new[] { 0x00070020U, 0x00070020U }, calls.PerformanceCodes);
        Assert.Equal(new[] { 0x00070060U, 0x00070060U }, calls.OffCodes);
        Assert.Same(calls.Handle, calls.LastIoctlHandle);
        Assert.True(calls.Handle!.IsClosed);
    }

    [Fact]
    public async Task AmbiguousOrMismatchedVpdIdentityNeverStartsPerformanceSampling()
    {
        var duplicate = Hardware() with
        {
            DeviceIdentity = "windows-storage|unique|OTHER",
            HardwareIdentityEvidence = new("SERIAL-A", "OTHER", 0)
        };
        var calls = new PerformanceCalls();
        var ambiguous = await new WindowsDiskLayoutInformationNativeApi(calls).ReadPerformanceAsync(
            DiskLayoutInformationCollectorTests.DiskA, Candidates([Hardware(), duplicate]), SelectedKeys(), TimeSpan.FromMilliseconds(1),
            TimeProvider.System, CancellationToken.None);
        Assert.Null(ambiguous);
        Assert.Empty(calls.PerformanceCodes);

        calls = new PerformanceCalls { VpdFrame = Frame("binary") };
        var mismatch = await new WindowsDiskLayoutInformationNativeApi(calls).ReadPerformanceAsync(
            DiskLayoutInformationCollectorTests.DiskA, Candidates([Hardware()]), SelectedKeys(), TimeSpan.FromMilliseconds(1),
            TimeProvider.System, CancellationToken.None);
        Assert.Null(mismatch);
        Assert.Empty(calls.PerformanceCodes);
        Assert.True(calls.Handle!.IsClosed);
    }

    [Fact]
    public async Task UnselectedExactIdentityIsNeverPerformanceSampled()
    {
        var calls = new PerformanceCalls();
        var read = await new WindowsDiskLayoutInformationNativeApi(calls).ReadPerformanceAsync(
            DiskLayoutInformationCollectorTests.DiskA, Candidates([Hardware()]),
            new HashSet<string>(["windows-storage|unique|UNSELECTED"], StringComparer.Ordinal),
            TimeSpan.FromMilliseconds(1), TimeProvider.System, CancellationToken.None);

        Assert.Null(read);
        Assert.Empty(calls.PerformanceCodes);
        Assert.True(calls.Handle!.IsClosed);
    }

    [Fact]
    public async Task CaptureReaderReturnsOnlySelectedExactIdentity()
    {
        var api = new PerformanceInformationApi(duplicateTargetMatch: false);
        var reader = new WindowsDiskPerformanceCaptureReader(new WindowsDiskLayoutInformationReader(api),
            TimeProvider.System, TimeSpan.FromMilliseconds(1));
        var target = new WindowsDiskPerformanceTarget(2, HardwareId, SupportsNvmeHealth: false, SupportsAtaSmart: true);
        var result = await reader.ReadAsync([target], [Hardware()], CancellationToken.None);

        Assert.Equal(new HashSet<string>([HardwareId], StringComparer.Ordinal), api.SelectedIdentityKeys);
        Assert.Equal(23d, result.Readings[target]!.ValueFor(MetricFieldCode.PhysicalDiskQueueLength));
    }

    [Fact]
    public async Task MultipleExactInterfaceMatchesRemainUnavailableInsteadOfSelectingOne()
    {
        var api = new PerformanceInformationApi(duplicateTargetMatch: true);
        var reader = new WindowsDiskPerformanceCaptureReader(new WindowsDiskLayoutInformationReader(api),
            TimeProvider.System, TimeSpan.FromMilliseconds(1));
        var target = new WindowsDiskPerformanceTarget(2, HardwareId, SupportsNvmeHealth: false, SupportsAtaSmart: true);
        var result = await reader.ReadAsync([target], [Hardware()], CancellationToken.None);

        Assert.Null(result.Readings[target]);
    }

    [Fact]
    public async Task FailedSecondSampleReturnsNullAndReleasesEachSuccessfulEnable()
    {
        var calls = new PerformanceCalls { FailPerformanceCall = 2, Samples = [Counters(0, 0, 0, 0, 0, 0, 0, 0)] };
        var read = await new WindowsDiskLayoutInformationNativeApi(calls).ReadPerformanceAsync(
            DiskLayoutInformationCollectorTests.DiskA, Candidates([Hardware()]), SelectedKeys(), TimeSpan.FromMilliseconds(1),
            TimeProvider.System, CancellationToken.None);

        Assert.Equal(HardwareId, read!.IdentityKey);
        Assert.Null(read.Metrics);
        Assert.Single(calls.OffCodes);
        Assert.True(calls.Handle!.IsClosed);
    }

    [Theory]
    [InlineData(83U)]
    [InlineData(89U)]
    public async Task TruncatedOrOversizedNativeExtentCannotProduceMetricsAndStillReleasesEnable(uint returnedBytes)
    {
        var calls = new PerformanceCalls
        {
            Samples = [Counters(0, 0, 0, 0, 0, 0, 0, 0)],
            ReturnedBytes = returnedBytes
        };
        var read = await new WindowsDiskLayoutInformationNativeApi(calls).ReadPerformanceAsync(
            DiskLayoutInformationCollectorTests.DiskA, Candidates([Hardware()]), SelectedKeys(), TimeSpan.FromMilliseconds(1),
            TimeProvider.System, CancellationToken.None);

        Assert.Equal(HardwareId, read!.IdentityKey);
        Assert.Null(read.Metrics);
        Assert.Single(calls.PerformanceCodes);
        Assert.Single(calls.OffCodes);
        Assert.True(calls.Handle!.IsClosed);
    }

    [Fact]
    public async Task CancellationAfterFirstEnableReleasesHandleAndEnableBeforePropagating()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = new PerformanceCalls
        {
            Samples = [Counters(0, 0, 0, 0, 0, 0, 0, 0)],
            AfterPerformance = call => { if (call == 1) cancellation.Cancel(); }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WindowsDiskLayoutInformationNativeApi(calls)
            .ReadPerformanceAsync(DiskLayoutInformationCollectorTests.DiskA, Candidates([Hardware()]), SelectedKeys(), TimeSpan.FromSeconds(1),
                TimeProvider.System, cancellation.Token));

        Assert.Single(calls.OffCodes);
        Assert.True(calls.Handle!.IsClosed);
    }

    [Fact]
    public async Task NonPhysicalProviderOrFailedOffMakesTheWholeWindowUnavailable()
    {
        var calls = new PerformanceCalls
        {
            Samples = [Counters(0, 0, 0, 0, 0, 0, 0, 0, "FTDISK"), Counters(10, 10, 10, 10, 0, 1, 1, 1, "FTDISK")]
        };
        var read = await new WindowsDiskLayoutInformationNativeApi(calls).ReadPerformanceAsync(
            DiskLayoutInformationCollectorTests.DiskA, Candidates([Hardware()]), SelectedKeys(), TimeSpan.FromMilliseconds(1),
            TimeProvider.System, CancellationToken.None);
        Assert.Null(read!.Metrics);
        Assert.Equal(2, calls.OffCodes.Count);

        calls = new PerformanceCalls
        {
            Samples = [Counters(0, 0, 0, 0, 0, 0, 0, 0), Counters(10, 10, 10, 10, 0, 1, 1, 1)],
            FailOff = true
        };
        read = await new WindowsDiskLayoutInformationNativeApi(calls).ReadPerformanceAsync(
            DiskLayoutInformationCollectorTests.DiskA, Candidates([Hardware()]), SelectedKeys(), TimeSpan.FromMilliseconds(1),
            TimeProvider.System, CancellationToken.None);
        Assert.Null(read!.Metrics);
        Assert.Equal(2, calls.OffCodes.Count);
        Assert.True(calls.Handle!.IsClosed);
    }

    private static WindowsDiskPerformanceCounters Counters(long bytesRead, long bytesWritten, long readTime,
        long writeTime, long idleTime, uint reads, uint writes, uint queue, string provider = "PARTMGR", long queryTime = 0)
        => new(bytesRead, bytesWritten, readTime, writeTime, idleTime, reads, writes, queue, provider, queryTime);

    private static IReadOnlyDictionary<(StorageIdentifyProtocol Protocol, string Serial), DeviceTopologyStorageInformation[]> Candidates(
        IReadOnlyList<DeviceTopologyStorageInformation> identities)
        => WindowsInformationFieldProjection.PhysicalDiskRelationCandidates(identities)!;

    private static IReadOnlySet<string> SelectedKeys() => new HashSet<string>([HardwareId], StringComparer.Ordinal);

    private sealed class PerformanceCalls : IWindowsDiskLayoutNativeCalls
    {
        private int performanceCall;
        private int sampleIndex;
        internal SafeFileHandle? Handle;
        internal SafeFileHandle? LastIoctlHandle;
        internal readonly List<string> Order = [];
        internal readonly List<uint> PerformanceCodes = [];
        internal readonly List<uint> OffCodes = [];
        internal IReadOnlyList<WindowsDiskPerformanceCounters> Samples = [];
        internal byte[]? VpdFrame;
        internal int FailPerformanceCall = -1;
        internal bool FailOff;
        internal uint? ReturnedBytes;
        internal Action<int>? AfterPerformance;

        public SafeFileHandle Open(string devicePath)
        {
            Assert.Equal(DiskLayoutInformationCollectorTests.DiskA.DevicePath, devicePath);
            return Handle = new(new IntPtr(42), ownsHandle: false);
        }

        public bool Number(SafeFileHandle handle, byte[] buffer, out uint returned)
        {
            Check(handle);
            Order.Add("number");
            DiskLayoutInformationCollectorTests.NumberFrame(2).CopyTo(buffer, 0);
            returned = 12;
            return true;
        }

        public bool Layout(SafeFileHandle handle, byte[] buffer, out uint returned)
        { Check(handle); returned = 0; return false; }
        public bool AccessAlignment(SafeFileHandle handle, byte[] query, byte[] buffer, out uint returned)
        { Check(handle); returned = 0; return false; }

        public bool DeviceIdentity(SafeFileHandle handle, byte[] query, byte[] buffer, out uint returned)
        {
            Check(handle);
            var property = BinaryPrimitives.ReadUInt32LittleEndian(query);
            Order.Add("identity" + property);
            var frame = property == 0 ? Frame("ata") : VpdFrame ?? Frame("vpd");
            frame.CopyTo(buffer, 0);
            returned = (uint)frame.Length;
            return true;
        }

        public bool Performance(SafeFileHandle handle, byte[] buffer, out uint returned)
        {
            Check(handle);
            LastIoctlHandle = handle;
            Order.Add("performance");
            PerformanceCodes.Add(WindowsDiskLayoutInformationNativeApi.DiskPerformanceIoctl);
            var call = ++performanceCall;
            AfterPerformance?.Invoke(call);
            if (call == FailPerformanceCall) { returned = 0; return false; }
            var counters = Samples[sampleIndex++];
            WritePerformance(buffer, counters);
            returned = ReturnedBytes ?? 88;
            return true;
        }

        public bool PerformanceOff(SafeFileHandle handle)
        {
            Check(handle);
            LastIoctlHandle = handle;
            Order.Add("off");
            OffCodes.Add(WindowsDiskLayoutInformationNativeApi.DiskPerformanceOffIoctl);
            return !FailOff;
        }

        public int LastError() => 5;

        private static void WritePerformance(byte[] buffer, WindowsDiskPerformanceCounters sample)
        {
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(0), sample.BytesRead);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(8), sample.BytesWritten);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(16), sample.ReadTime);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(24), sample.WriteTime);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(32), sample.IdleTime);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(40), sample.ReadCount);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(44), sample.WriteCount);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(48), sample.QueueDepth);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(56), sample.QueryTime);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(64), 123);
            Encoding.Unicode.GetBytes(sample.StorageManagerName).CopyTo(buffer, 68);
        }

        private void Check(SafeFileHandle handle)
        {
            Assert.Same(Handle, handle);
            Assert.False(handle.IsClosed);
            Assert.False(handle.IsInvalid);
        }
    }

    private sealed class PerformanceInformationApi(bool duplicateTargetMatch) : IWindowsDiskLayoutInformationApi
    {
        internal IReadOnlySet<string> SelectedIdentityKeys = new HashSet<string>(StringComparer.Ordinal);

        public IReadOnlyList<WindowsDiskInterface>? Enumerate()
            => [DiskLayoutInformationCollectorTests.DiskA, DiskLayoutInformationCollectorTests.DiskB];

        public WindowsDiskLayoutCapture? Read(WindowsDiskInterface device) => null;

        public Task<WindowsDiskPerformanceNativeRead?> ReadPerformanceAsync(WindowsDiskInterface device,
            IReadOnlyDictionary<(StorageIdentifyProtocol Protocol, string Serial), DeviceTopologyStorageInformation[]> candidates,
            IReadOnlySet<string> selectedIdentityKeys, TimeSpan sampleWindow, TimeProvider clock, CancellationToken cancellationToken)
        {
            SelectedIdentityKeys = selectedIdentityKeys;
            var matches = device == DiskLayoutInformationCollectorTests.DiskA || duplicateTargetMatch;
            WindowsDiskPerformanceNativeRead? read = matches
                ? new(HardwareId, new(1, 2, 3, 4, 5, 23, 7))
                : null;
            return Task.FromResult(read);
        }
    }
}
