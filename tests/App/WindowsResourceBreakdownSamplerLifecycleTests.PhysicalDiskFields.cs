using System.Diagnostics;
using System.Globalization;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.ReadWrite;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Processes;
using ResourceManager.Subscription;

namespace Resource_Manager_APP.Tests;

public sealed partial class WindowsResourceBreakdownSamplerLifecycleTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CanonicalDiskRequestUsesOriginalReaderAndCurrentWithoutHardwareOrSoftwareBars(bool complete)
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var catalog = new VersionedProcessAttributionCatalogProvider();
        var hardware = new StaticMetricSampler(CreateMemorySnapshot());
        var disk = new RecordedPhysicalDiskReader(complete);
        using var sampler = new WindowsResourceBreakdownSampler(hardware, catalog,
            new StaticResourceResidualBreakdownProvider(), disk, null!, subscriptions.Provider, subscriptions.Owner,
            new PdhProcessGpuReader(), new DelegateWindowsProcessInventoryReader(static () => [Process.GetCurrentProcess()]));
        var names = ProcessMetricFieldRegistry.Definitions.Where(static field =>
            field.DatasetId == SamplingDatasetIds.ProcessObservedPhysicalDiskIo).Select(static field => field.Name).ToArray();
        var snapshot = await sampler.CaptureProcessFieldsAsync(ResourceBreakdownSampleRequest.ForResourceTable(names,
            new Dictionary<string, string>()), CancellationToken.None);
        Assert.Equal(1, disk.Reads); Assert.Equal(0, hardware.CaptureCount); Assert.Empty(snapshot.Bars);
        Assert.Equal(2, snapshot.ProcessFields!.Processes.Count);
        var backend = new HostProcessFieldBackend(sampler);
        Assert.Equal(4, backend.Catalog.Fields.Length);
        foreach (var field in backend.Catalog.Fields)
        {
            var cached = backend.ReadCached(field.Field);
            Assert.Equal(complete, cached.Value is not null);
            Assert.False(field.Write.Supported);
            Assert.Equal("windows.etw.physical-disk", cached.SourceId);
        }
        if (complete)
        {
            var read = backend.Catalog.Fields.Single(field => field.Field.DeviceIdentity.EndsWith("|etw-disk:37:0", StringComparison.Ordinal)
                && field.Definition.Name == "process_all_physical_disk_read_bytes_attributed");
            Assert.Equal(MetricInteger.Parse("9007199254740993"), backend.ReadCached(read.Field).Value!.Integer);
            await sampler.StartAsync(CancellationToken.None);
            disk.ReadBytes = "31";
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var stream = backend.SubscribeAsync("recorded-disk", [read.Field], TimeSpan.FromSeconds(1),
                new(FieldSubscriptionSamplingMode.FreshOnTrigger), cancellation.Token).GetAsyncEnumerator(cancellation.Token);
            var next = stream.MoveNextAsync().AsTask();
            Assert.True(await next);
            Assert.Equal(MetricInteger.Parse("31"), Assert.Single(stream.Current).Value!.Integer);
            Assert.Equal(Assert.Single(stream.Current), backend.ReadCached(read.Field));
            Assert.Equal(2, disk.Reads); // one explicit capture, then one fresh trigger through the original worker
            Assert.Equal(0, hardware.CaptureCount);

            var pending = stream.MoveNextAsync().AsTask();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
            await Task.Delay(TimeSpan.FromMilliseconds(1_100));
            Assert.Equal(2, disk.Reads); // retiring the last field demand prevents the next one-second query
        }
    }
    private sealed class RecordedPhysicalDiskReader(bool complete) : IPhysicalDiskIoAttributionReader
    {
        private int reads;
        internal int Reads => Volatile.Read(ref reads);
        internal string ReadBytes = "9007199254740993";
        public PhysicalDiskIoAttributionSnapshot Read() => throw new InvalidOperationException("Observed selection was lost.");
        public PhysicalDiskIoAttributionSnapshot Read(bool includeObservedTraffic)
        {
            Interlocked.Increment(ref reads); Assert.True(includeObservedTraffic);
            using var process = Process.GetCurrentProcess();
            var birth = process.StartTime.ToUniversalTime().ToFileTimeUtc().ToString(CultureInfo.InvariantCulture);
            var zero = new PhysicalDiskIoScope(37, 0); var two = new PhysicalDiskIoScope(37, 2);
            var captured = DateTimeOffset.UtcNow;
            return new(captured, new Dictionary<int, PhysicalDiskProcessAttribution>(),
                new("etw-fileio-disk", complete ? PhysicalDiskIoProviderStates.Running : PhysicalDiskIoProviderStates.Warming, "recorded"))
            { ObservedTraffic = new(captured, 2000,
                [new(zero, process.Id, birth, ReadBytes, "0"), new(two, process.Id, birth, "0", "23")])
                { Complete = complete, Disks = [zero, two] } };
        }
    }
}
