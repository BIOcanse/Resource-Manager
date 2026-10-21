using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Vendors.Intel.Msr;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed partial class IntelMsrThermalCollectorTests
{
    [Fact]
    public void IntelWorkerTemporaryRootRemovesItsOwnedSnapshotTree()
    {
        string root;
        using (var temporaryRoot = new IntelWorkerTemporaryRoot("intel-msr-worker"))
        {
            root = temporaryRoot.Root;
            var snapshotPath = Path.Combine(root, "UserData", "HostManager", "metric-snapshot", "metric-snapshot-v2.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
            File.WriteAllBytes(snapshotPath, [0x48, 0x52, 0x4D, 0x4D, 0x53, 0x4E, 0x50, 0x32]);
        }

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task RatioOriginalWorkerClearsCancelledDemandAndResubscriptionStartsNewInterval()
    {
        using var temporaryRoot = new IntelWorkerTemporaryRoot("intel-ratio-worker");
        var root = temporaryRoot.Root;
        using var slots = new HostManagerSamplingSubscriptionTestFixture();
        var target = FeedbackThread(65);
        using var owner = new HostManagerMetricSnapshotOwner(slots.Provider,
            new HostManagerMetricSnapshotRuntime(slots.Provider, slots.Deployment),
            new HostManagerMetricSnapshotCatalogTopologySource(() => Topology([target])), new IntelEnvironment(root),
            NullLogger<HostManagerMetricSnapshotOwner>.Instance, ResourceManager.ReadWrite.Native.NativeMetricSnapshotScratch.CreateOrdinary);
        await owner.StartAsync(CancellationToken.None);
        var dashboard = new DashboardMonitoringCatalogState();
        using (var lease = owner.Acquire())
            dashboard.PublishCatalogProbe(NativeMetricSnapshotCommittedProjection.Create(lease.Workspace.ReadCommitted(), "CPU", "RAM", lease.WorkspaceIdentity));
        var access = new Access { FeedbackFlags = 1, ThermalFlags = 0 }; var hardware = new Hardware(access);
        hardware.RaplRegisters[0xE7] = 0; hardware.RaplRegisters[0xE8] = 0;
        hardware.AfterRead = () => {
            if (hardware.Reads[^1].Register != 0xE7) return;
            hardware.RaplRegisters[0xE7] += 100; hardware.RaplRegisters[0xE8] += 150;
        };
        var zone = new IntelMsrMonitoringZone(new IntelMsrReader(() => hardware, () => access));
        var memory = new WindowsMemoryMonitoringZone(_ => throw new InvalidOperationException("Intel-only demand cannot read NUMA"),
            () => throw new InvalidOperationException("Intel-only demand cannot read global memory"));
        var registry = new MonitoringSourceZoneRegistry(MonitoringSourceZoneCatalog.SourceIds.Select(id =>
            id == zone.DisplayName ? zone : new MonitoringSourceZone(id)).ToArray());
        using var sampler = new WindowsHardwareMetricSampler(new WindowsCpuMonitoringZone(), memory,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, new AmdIntegratedGpuSensorRoute(), null!, owner,
            dashboard, registry, slots.Owner, slots.Provider, NullLogger<WindowsHardwareMetricSampler>.Instance, intelMsrZone: zone);
        var request = MetricDeviceFields.CreateRequest(dashboard.Current!, target.ExactIdentityKey, ["cpu_intel_aperf_mperf_interval_ratio"]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await sampler.StartAsync(timeout.Token);
        try
        {
            await using (var stream = sampler.SubscribeAsync("ratio-first", request, TimeSpan.FromMilliseconds(100), timeout.Token).GetAsyncEnumerator(timeout.Token))
            {
                Assert.True(await stream.MoveNextAsync()); Assert.Null(Value(stream.Current));
                Assert.True(await stream.MoveNextAsync()); Assert.Equal(1.5, Value(stream.Current));
            }
            await Task.Delay(150, timeout.Token); var reads = hardware.Reads.Count;
            var cached = await sampler.GetSnapshotAsync(request, timeout.Token); Assert.Equal(1.5, Value(cached));
            sampler.ReadLatest(request); await Task.Delay(150, timeout.Token); Assert.Equal(reads, hardware.Reads.Count);
            await using (var stream = sampler.SubscribeAsync("ratio-second", request, TimeSpan.FromMilliseconds(100), timeout.Token).GetAsyncEnumerator(timeout.Token))
            {
                Assert.True(await stream.MoveNextAsync()); Assert.Null(Value(stream.Current));
                Assert.True(await stream.MoveNextAsync()); Assert.Equal(1.5, Value(stream.Current));
                await sampler.StopAsync(CancellationToken.None);
                var stoppedReads = hardware.Reads.Count;
                Assert.Equal(1.5, Value(await sampler.GetSnapshotAsync(request, timeout.Token)));
                Assert.Equal(stoppedReads, hardware.Reads.Count);
                await sampler.StartAsync(timeout.Token);
                Assert.True(await stream.MoveNextAsync()); Assert.Null(Value(stream.Current));
                Assert.True(await stream.MoveNextAsync()); Assert.Equal(1.5, Value(stream.Current));
            }
            Assert.All(hardware.Reads, read => Assert.Equal(target.Processor, read.Address)); Assert.Equal(0, hardware.Writes);
        }
        finally { await sampler.StopAsync(CancellationToken.None); }
        double? Value(HardwareMetricSnapshot snapshot) => MetricDeviceFields.Read(snapshot, target.ExactIdentityKey).Single().Fields
            .Single(field => field.Name == "cpu_intel_aperf_mperf_interval_ratio").NumericValue;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task RealOriginalWorkerUsesSubscriptionTargetAndGetNeverReadsMsr(int family)
    {
        using var temporaryRoot = new IntelWorkerTemporaryRoot("intel-msr-worker");
        var root = temporaryRoot.Root;
        using var slots = new HostManagerSamplingSubscriptionTestFixture();
        var core = family >= 5 ? StatusCore() : Core(); var other = Core(1);
        var package = RaplPackage(0x906E0);
        var thread = FeedbackThread(65);
        var die = RaplDie(2);
        var target = family switch { 1 or 3 => package, 2 => thread, 4 => die, _ => core };
        using var owner = new HostManagerMetricSnapshotOwner(slots.Provider,
            new HostManagerMetricSnapshotRuntime(slots.Provider, slots.Deployment),
            new HostManagerMetricSnapshotCatalogTopologySource(() => Topology([core, other, package, thread, die])), new IntelEnvironment(root),
            NullLogger<HostManagerMetricSnapshotOwner>.Instance, ResourceManager.ReadWrite.Native.NativeMetricSnapshotScratch.CreateOrdinary);
        await owner.StartAsync(CancellationToken.None);
        var dashboard = new DashboardMonitoringCatalogState();
        using (var lease = owner.Acquire())
            dashboard.PublishCatalogProbe(NativeMetricSnapshotCommittedProjection.Create(lease.Workspace.ReadCommitted(), "CPU", "RAM", lease.WorkspaceIdentity));
        var access = new Access { FeedbackFlags = 1, Signature = target.Signature }; var hardware = new Hardware(access);
        if (family >= 5) { access.Leaf1Edx = 1 << 22; access.ThermalFlags = 209; }
        if (family == 5) hardware.CoreStatus |= 1;
        hardware.RaplRegisters[0xE8] = ulong.MaxValue;
        hardware.RaplRegisters[0x614] = ulong.MaxValue;
        var zone = new IntelMsrMonitoringZone(new IntelMsrReader(() => { hardware.Opens++; return hardware; }, () => access));
        var memory = new WindowsMemoryMonitoringZone(_ => throw new InvalidOperationException("Intel-only demand cannot read NUMA"),
            () => throw new InvalidOperationException("Intel-only demand cannot read global memory"));
        var registry = new MonitoringSourceZoneRegistry(MonitoringSourceZoneCatalog.SourceIds.Select(id =>
            id == zone.DisplayName ? zone : new MonitoringSourceZone(id)).ToArray());
        using var sampler = new WindowsHardwareMetricSampler(new WindowsCpuMonitoringZone(), memory,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, new AmdIntegratedGpuSensorRoute(), null!, owner,
            dashboard, registry, slots.Owner, slots.Provider, NullLogger<WindowsHardwareMetricSampler>.Instance, intelMsrZone: zone);
        var request = MetricDeviceFields.CreateRequest(dashboard.Current!, target.ExactIdentityKey,
            [family switch { 1 => "cpu_intel_package_energy_counter_raw", 2 => "cpu_intel_aperf_counter_raw", 3 => "cpu_intel_package_power_information_raw", 4 => "cpu_intel_die_package_energy_counter_raw", 5 or 6 => "cpu_intel_core_thermal_throttling_active", _ => "cpu_intel_core_dts_temperature" }]);
        Assert.Single(request.Ids); await sampler.GetSnapshotAsync(request, CancellationToken.None); Assert.Empty(hardware.Reads);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await sampler.StartAsync(timeout.Token);
        try
        {
            await using var stream = sampler.SubscribeAsync("intel-temperature-consumer", request, TimeSpan.FromMinutes(10), timeout.Token).GetAsyncEnumerator(timeout.Token);
            Assert.True(await stream.MoveNextAsync()); AssertSelected(stream.Current);
            Assert.Equal(family switch { 1 or 4 => new uint[] { 0x611 }, 2 => [0xE8], 3 => [0x614], 5 or 6 => [0x19C], _ => [0x1A2, 0x19C] }, hardware.Reads.Select(static read => read.Register));
            Assert.All(hardware.Reads, read => Assert.Equal(target.Processor, read.Address));
            AssertSelected(await sampler.GetSnapshotAsync(request, timeout.Token));
            sampler.ReadLatest(request); Assert.Equal(family == 0 ? 2 : 1, hardware.Reads.Count);
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
            var reads = hardware.Reads.Count;
            await sampler.GetSnapshotAsync(request, CancellationToken.None);
            Assert.Equal(reads, hardware.Reads.Count); Assert.Equal(1, hardware.Opens); Assert.Equal(1, hardware.Disposals);
        }
        void AssertSelected(ResourceManager.App.Domain.Metrics.HardwareMetricSnapshot snapshot)
        {
            if (family == 1) AssertRaplCounter(snapshot, target.ExactIdentityKey, "4096");
            else if (family == 2) AssertFeedbackCounter(snapshot, target.ExactIdentityKey, "18446744073709551615");
            else if (family == 3) AssertPowerInformation(snapshot, target.ExactIdentityKey, "18446744073709551615");
            else if (family == 4) AssertDieCounter(snapshot, target.ExactIdentityKey, "4096");
            else if (family >= 5)
            {
                var field = Assert.Single(Assert.Single(MetricDeviceFields.Read(snapshot, target.ExactIdentityKey)).Fields,
                    static field => field.Name == "cpu_intel_core_thermal_throttling_active");
                Assert.Equal(family == 5, field.BooleanValue); Assert.Null(field.NumericValue); Assert.Null(field.IntegerValue);
            }
            else AssertTemperature(snapshot, target.ExactIdentityKey, false, 80);
        }
    }

    private sealed class IntelEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Tests";
        public string ApplicationName { get; set; } = "ResourceManager";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class IntelWorkerTemporaryRoot : IDisposable
    {
        private const int MaximumDepth = 8;
        private const int MaximumEntries = 128;
        private const long MaximumBytes = 16 * 1024 * 1024;
        private readonly string prefix;
        private bool disposed;

        internal IntelWorkerTemporaryRoot(string prefix)
        {
            if (prefix is not ("intel-msr-worker" or "intel-ratio-worker"))
                throw new ArgumentException("The worker temporary-root prefix is not owned by this fixture.", nameof(prefix));
            this.prefix = prefix;

            var temporaryDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var candidate = Path.GetFullPath(Path.Combine(temporaryDirectory, $"{prefix}-{Guid.NewGuid():N}"));
                if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(candidate), temporaryDirectory))
                    throw new InvalidOperationException("The worker temporary root escaped the process temp directory.");
                if (File.Exists(candidate) || Directory.Exists(candidate))
                    continue;

                Directory.CreateDirectory(candidate);
                Root = candidate;
                return;
            }

            throw new IOException("Could not allocate a unique worker temporary root.");
        }

        internal string Root { get; }

        public void Dispose()
        {
            if (disposed)
                return;

            DeleteOwnedTree(Root, prefix);
            disposed = true;
        }

        private static void DeleteOwnedTree(string root, string expectedPrefix)
        {
            var fullRoot = Path.GetFullPath(root);
            var tempDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            var leaf = Path.GetFileName(fullRoot);
            var parent = Path.GetDirectoryName(fullRoot);
            var prefixWithSeparator = expectedPrefix + "-";
            if (!StringComparer.OrdinalIgnoreCase.Equals(parent, tempDirectory)
                || !leaf.StartsWith(prefixWithSeparator, StringComparison.Ordinal)
                || !Guid.TryParseExact(leaf[prefixWithSeparator.Length..], "N", out _))
            {
                throw new InvalidOperationException("Refusing to clean a path outside the owned worker temporary-root contract.");
            }
            if (!Directory.Exists(fullRoot))
                return;
            RequireOrdinaryDirectory(fullRoot);

            var pending = new Stack<(string Path, bool DeleteAfterChildren, int Depth)>();
            pending.Push((fullRoot, false, 0));
            var entries = 0;
            long totalBytes = 0;
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (current.DeleteAfterChildren)
                {
                    Directory.Delete(current.Path);
                    continue;
                }
                if (current.Depth > MaximumDepth)
                    throw new IOException("The worker temporary tree exceeded its cleanup depth bound.");

                RequireOrdinaryDirectory(current.Path);
                pending.Push((current.Path, true, current.Depth));
                foreach (var entry in Directory.EnumerateFileSystemEntries(current.Path))
                {
                    entries++;
                    if (entries > MaximumEntries)
                        throw new IOException("The worker temporary tree exceeded its cleanup entry bound.");

                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Refusing to follow a reparse point inside a worker temporary tree.");
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push((entry, false, current.Depth + 1));
                        continue;
                    }

                    totalBytes = checked(totalBytes + new FileInfo(entry).Length);
                    if (totalBytes > MaximumBytes)
                        throw new IOException("The worker temporary tree exceeded its cleanup byte bound.");
                    File.Delete(entry);
                }
            }
        }

        private static void RequireOrdinaryDirectory(string path)
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0
                || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Refusing to traverse a non-directory or reparse point in a worker temporary tree.");
            }
        }
    }
}
