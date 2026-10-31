using System.Diagnostics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Endpoints.Transport;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite.Native.Pdh;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Windows.Processes;
using ResourceManager.ReadWrite.Windows.Pdh;

namespace Resource_Manager_APP.Tests;

public sealed partial class WindowsResourceBreakdownSamplerLifecycleTests
{
    [Fact]
    public async Task SharedGpuSystemMemoryRunsThroughSubscribedFieldsAndReleasesEveryPdhRead()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var catalog = new VersionedProcessAttributionCatalogProvider();
        var captured = DateTimeOffset.UtcNow;
        var pid = Environment.ProcessId;
        var birth = (long)ProcessBasicFieldCollectorTests.Birth;
        var raw = new NativePdhSnapshot(captured, 1, 1, [], [], default);
        var api = new SharedGpuPdhApi(pid);
        var reader = new PdhProcessGpuReader(new MeasureGpuSource(raw),
            identities => identities.ToHashSet(), new PdhGpuProcessSharedMemoryReader(api));
        var adapter = new WindowsGpuAdapter(0, "fixture", 0x10DE, 1, 0,
            new AdapterLuid { LowPart = 23 }, false, 100, WindowsGpuAdapterKind.Dedicated);
        var inventory = new WindowsGpuAdapterInventoryRead(SamplingObservationStatus.Current, 1, captured.UtcTicks, 1, 0, 0, 1, [adapter]);
        using var sampler = new WindowsResourceBreakdownSampler(null!, catalog,
            new StaticResourceResidualBreakdownProvider(), null!, null!, subscriptions.Provider, subscriptions.Owner,
            reader, new DelegateWindowsProcessInventoryReader(static () => [Process.GetCurrentProcess()]),
            new WindowsProcessBasicMetricReader(new ProcessBasicFieldCollectorTests.Api()), gpuAdapterInventory: () => inventory);
        var field = ProcessGpuResourceFieldRegistry.ForSharedSystemMemory(0, 23);
        var request = ResourceBreakdownSampleRequest.ForResourceTable([field.Name], new Dictionary<string, string>());
        Assert.Equal(0, api.Opens);
        await sampler.StartAsync(default);
        try
        {
            using (sampler.AcquireSubscription("gpu-shared-system-memory", request, TimeSpan.FromMilliseconds(100)))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                ResourceBreakdownSnapshot? current;
                do { await Task.Delay(20, timeout.Token); current = sampler.ReadLatest(request); }
                while (current?.ProcessFields?.Processes.Any(row => row.ProcessId == pid && row.Values.Any(value => value.Field == field.Name)) != true);
                var value = current.ProcessFields!.Processes.Single(row => row.ProcessId == pid).Values.Single(value => value.Field == field.Name);
                Assert.Equal(1234d, value.NumericValue);
                Assert.True(api.Opens > 0);
                Assert.Equal(api.Opens, api.Closes);
                Assert.Equal(birth.ToString(), current.ProcessFields.Processes.Single(row => row.ProcessId == pid).CreationWindowsFileTime100Ns);
            }
            // Drain the already-dispatched publication, then prove that no next query opens.
            await Task.Delay(200);
            var opens = api.Opens;
            _ = sampler.ReadLatest(request);
            await Task.Delay(250);
            Assert.Equal(opens, api.Opens);
            Assert.Equal(api.Opens, api.Closes);
        }
        finally { await sampler.StopAsync(default); }
    }

    private sealed class SharedGpuPdhApi(int pid) : IPdhFormattedCounterArrayApi
    {
        internal int Opens, Closes;
        public int OpenQuery(out IntPtr query) { Interlocked.Increment(ref Opens); query = 1; return 0; }
        public int AddEnglishCounter(IntPtr query, string path, out IntPtr counter)
        { Assert.Equal(@"\GPU Process Memory(*)\Shared Usage", path); counter = 2; return 0; }
        public int CollectQueryData(IntPtr query) => 0;
        public int GetFormattedCounterArray(IntPtr counter, uint format, out IReadOnlyList<PdhFormattedCounterValue> values)
        { values = [new($"pid_{pid}_luid_0x00000000_0x00000017_phys_0", 0, 1234)]; return 0; }
        public int CloseQuery(IntPtr query) { Interlocked.Increment(ref Closes); return 0; }
    }

    [Fact]
    public async Task CanonicalGpuDemandDiscoversScopedMeasuresAndChosenEngineUsesTheSameOwner()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var catalog = new VersionedProcessAttributionCatalogProvider();
        var birth = (long)ProcessBasicFieldCollectorTests.Birth;
        var captured = DateTimeOffset.UtcNow;
        var pid = (uint)Environment.ProcessId;
        var native = new NativePdhSnapshot(captured, 1, 1,
            [new() { AdapterLuid = 23, ProcessId = pid, EngineIndex = 2, EngineClass = NativePdhEngineClass.Compute, UsagePercent = 37 }], [], default)
        {
            EngineWindows = [new() { AdapterLuid = 23, ProcessId = pid, EngineIndex = 2,
                BeginFileTime = birth + 1, EndFileTime = birth + 1000001,
                BeginCounter = 10, EndCounter = 110, CounterFrequency = 1000 }]
        };
        var source = new MeasureGpuSource(native);
        var adapter = new WindowsGpuAdapter(0, "fixture", 0x10DE, 1, 0,
            new AdapterLuid { LowPart = 23 }, false, 100, WindowsGpuAdapterKind.Dedicated);
        var inventory = new WindowsGpuAdapterInventoryRead(SamplingObservationStatus.Current, 1, captured.UtcTicks, 1, 0, 0, 1, [adapter]);
        using var sampler = new WindowsResourceBreakdownSampler(null!, catalog,
            new StaticResourceResidualBreakdownProvider(), null!, null!, subscriptions.Provider, subscriptions.Owner,
            new PdhProcessGpuReader(source), new DelegateWindowsProcessInventoryReader(static () => [Process.GetCurrentProcess()]),
            new WindowsProcessBasicMetricReader(new ProcessBasicFieldCollectorTests.Api()), gpuAdapterInventory: () => inventory);
        var raw = ResourceBreakdownSampleRequest.ForResourceTable([WindowsPdhProcessGpuFields.FieldName], new Dictionary<string, string>());
        Assert.Empty(sampler.ResourceFactFields);
        Assert.Equal(0, source.Reads);
        _ = await sampler.CaptureSnapshotAsync(raw, default);
        var fields = sampler.ResourceFactFields;
        var field = Assert.Single(fields, field => field.GpuEngine is not null);
        Assert.Equal("gpu.0.usage", field.ResourceFactTotalMetricId);
        Assert.Equal("Compute", field.EngineClass);
        // The engine field, the three GPU-resident fields and the PDH shared system memory field of the one adapter.
        Assert.Equal(5, fields.Count);
        var reads = source.Reads;
        _ = sampler.ResourceFactFields; _ = sampler.ReadLatest(raw);
        Assert.Equal(reads, source.Reads);
        var chosen = raw with { MetricIds = [field.Name], ProcessMemoryFactIds = [field.Name] };
        await sampler.StartAsync(default);
        try
        {
            using (sampler.AcquireSubscription("gpu-measure", chosen, TimeSpan.FromMilliseconds(100)))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                ResourceBreakdownSnapshot? current;
                do { await Task.Delay(20, timeout.Token); current = sampler.ReadLatest(chosen); }
                while (current?.ProcessMemoryFacts.Count != 1);
                var foundations = sampler.ReadLatest(new SchedulingProcessFactRequest(SchedulingProcessMetricMask.None,
                    new(SamplingObservationStatus.NotRequested, 0, 0, 0, 0, 0, 0, [])));
                var fact = Assert.Single(ResourceFactsSnapshot.Create(current, [field.Name], foundations).Metrics);
                Assert.Equal(37, fact.TotalValue);
                Assert.Equal(37, fact.ProcessSumValue);
                Assert.Equal(0, fact.ResidualValue);
                Assert.Equal(field.GpuEngine, fact.GpuEngine);
            }
            await Task.Delay(200);
            reads = source.Reads;
            await Task.Delay(250);
            Assert.Equal(reads, source.Reads);
        }
        finally { await sampler.StopAsync(default); }
    }

    private sealed class MeasureGpuSource(NativePdhSnapshot snapshot) : INativePdhSnapshotSource
    {
        internal int Reads;
        public NativePdhReadResult Read(ulong? adapterTopologyFingerprint = null)
        { Interlocked.Increment(ref Reads); return new(snapshot, NativePdhProviderAvailability.Available, NativePdhResultCode.Ok, 0); }
    }
}
