using System.Runtime.InteropServices;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Native.Pdh;
using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Windows.Metrics.Capture;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;
using ResourceManager.ReadWrite.Windows.Pdh;

namespace Resource_Manager_APP.Tests;

public sealed class PdhGpuAdapterSharedMemoryReaderTests
{
    private const string CounterWildcard = "\\GPU Adapter Memory(*)\\Shared Usage";

    [Fact]
    public void ReaderUsesEnglishWildcardAndMatchesExactLuidInstancesIncludingZero()
    {
        var api = new RecordedPdhApi
        {
            Values =
            [
                Valid("luid_0x00000000_0x0000E4A6_phys_0", 0),
                Valid("luid_0x00000000_0x0000E4A7_phys_0", 2048),
                Valid("luid_0x00000000_0x0000E4A6_phys_0_extra", 99),
                Valid("luid_0x00000000_0x0000E4A60_phys_0", 88),
                Valid("luid_0x00000000_0x0000E4A8_phys_0", 77)
            ]
        };
        var reader = new PdhGpuAdapterSharedMemoryReader(api);

        var result = reader.ReadByAdapterIndex(
        [
            Adapter(0, 0xE4A6),
            Adapter(1, 0xE4A7),
            Adapter(2, 0xE4A8, software: true)
        ]);

        Assert.Equal(NativePdhProviderAvailability.Available, result.ProviderAvailability);
        Assert.Equal(NativePdhResultCode.Ok, result.ResultCode);
        Assert.Equal(0UL, result.SharedUsageBytesByAdapterIndex[0]);
        Assert.Equal(2048UL, result.SharedUsageBytesByAdapterIndex[1]);
        Assert.Equal(2, result.SharedUsageBytesByAdapterIndex.Count);
        Assert.Equal(CounterWildcard, api.AddedPath);
        Assert.Equal(1, api.OpenCalls);
        Assert.Equal(1, api.CollectCalls);
        Assert.Equal(1, api.CloseCalls);
    }

    [Fact]
    public void QueryFailureRemainsUnavailableAndDoesNotBecomeZero()
    {
        var api = new RecordedPdhApi { CollectStatus = 7 };
        var reader = new PdhGpuAdapterSharedMemoryReader(api);

        var result = reader.ReadByAdapterIndex([Adapter(0, 0xE4A6)]);

        Assert.Equal(NativePdhProviderAvailability.Unavailable, result.ProviderAvailability);
        Assert.Empty(result.SharedUsageBytesByAdapterIndex);
        Assert.Null(result.ObservedAt);
        Assert.Equal(NativePdhResultCode.PdhError, result.ResultCode);
        Assert.Equal(1, api.CloseCalls);
    }

    [Fact]
    public void InvalidResultOnlyRemovesTheAffectedAdapterAndValidZeroSurvives()
    {
        var api = new RecordedPdhApi
        {
            Values =
            [
                Valid("luid_0x00000000_0x0000E4A6_phys_0", 0),
                new("luid_0x00000000_0x0000E4A7_phys_0", 2, 200)
            ]
        };
        var reader = new PdhGpuAdapterSharedMemoryReader(api);

        var result = reader.ReadByAdapterIndex([Adapter(0, 0xE4A6), Adapter(1, 0xE4A7)]);

        Assert.Equal(NativePdhProviderAvailability.Available, result.ProviderAvailability);
        Assert.Equal(0UL, result.SharedUsageBytesByAdapterIndex[0]);
        Assert.DoesNotContain(1, result.SharedUsageBytesByAdapterIndex.Keys);
        Assert.Equal(1, result.FailedCounterCount);
    }

    [Fact]
    public void DuplicateAdapterLuidIsUnavailableRatherThanAssignedToAnArbitraryIndex()
    {
        var api = new RecordedPdhApi();
        var reader = new PdhGpuAdapterSharedMemoryReader(api);

        var result = reader.ReadByAdapterIndex([Adapter(0, 0xE4A6), Adapter(1, 0xE4A6)]);

        Assert.Equal(NativePdhProviderAvailability.Unavailable, result.ProviderAvailability);
        Assert.Empty(result.SharedUsageBytesByAdapterIndex);
        Assert.Equal(0, api.OpenCalls);
    }

    [Fact]
    public void DuplicateCounterInstanceDoesNotDoubleCountTheAdapter()
    {
        const string duplicate = "luid_0x00000000_0x0000E4A6_phys_0";
        var api = new RecordedPdhApi
        {
            Values =
            [
                Valid(duplicate, 100),
                Valid(duplicate, 200),
                Valid("luid_0x00000000_0x0000E4A7_phys_0", 300)
            ]
        };

        var result = new PdhGpuAdapterSharedMemoryReader(api)
            .ReadByAdapterIndex([Adapter(0, 0xE4A6), Adapter(1, 0xE4A7)]);

        Assert.Equal(NativePdhProviderAvailability.Available, result.ProviderAvailability);
        Assert.DoesNotContain(0, result.SharedUsageBytesByAdapterIndex.Keys);
        Assert.Equal(300UL, result.SharedUsageBytesByAdapterIndex[1]);
        Assert.Equal(1, result.FailedCounterCount);
    }

    [Fact]
    public void NativeArrayDecoderBoundsBufferAndInstanceNameReads()
    {
        const int allocatedBytes = 128;
        var buffer = Marshal.AllocHGlobal(allocatedBytes);
        try
        {
            var itemSize = Marshal.SizeOf<PdhFmtCounterValueItem>();
            var name = IntPtr.Add(buffer, itemSize);
            const string instanceName = "luid_0x00000000_0x0000E4A6_phys_0";
            for (var index = 0; index <= instanceName.Length; index++)
                Marshal.WriteInt16(name, index * sizeof(char),
                    index == instanceName.Length ? (short)0 : unchecked((short)instanceName[index]));
            Marshal.StructureToPtr(new PdhFmtCounterValueItem
            {
                Name = name,
                Value = new PdhFmtCounterValue { CStatus = 0, LargeValue = 4096 }
            }, buffer, false);

            Assert.True(PdhGpuAdapterSharedMemoryArrayDecoder.TryDecode(buffer, allocatedBytes, allocatedBytes,
                1, out var values));
            var value = Assert.Single(values);
            Assert.Equal(instanceName, value.InstanceName);
            Assert.Equal(4096, value.LargeValue);
            Assert.False(PdhGpuAdapterSharedMemoryArrayDecoder.TryDecode(buffer, allocatedBytes,
                allocatedBytes + 1, 1, out _));

            Marshal.StructureToPtr(new PdhFmtCounterValueItem
            {
                Name = IntPtr.Add(buffer, itemSize - sizeof(char)),
                Value = new PdhFmtCounterValue { CStatus = 0, LargeValue = 4096 }
            }, buffer, false);
            Assert.False(PdhGpuAdapterSharedMemoryArrayDecoder.TryDecode(buffer, allocatedBytes,
                allocatedBytes, 1, out _));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProductionCaptureKeepsGpuDomainsIndependent(bool engineFails)
    {
        const uint luid = 0xE4A6;
        var inventory = RecordedInventory(luid);
        var gpuIdentities = WindowsHardwareMetricTopologySource.CaptureGpuIdentities(inventory);
        Assert.Null(gpuIdentities.FailureCode);

        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        var workspacePath = Path.Combine(Path.GetTempPath(), $"gpu-shared-capture-{Guid.NewGuid():N}.bin");
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled, workspacePath);
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled,
            gpuIdentities.Rows, [], [], workspace.CreateCatalogHandleMap());
        workspace.ReplaceCatalog(catalog, DateTimeOffset.UtcNow);
        var sharedBinding = Assert.Single(catalog.MetricBindings.Values,
            static binding => binding.Field?.Name == "gpu_all_shared_memory_used_bytes");
        var usageBinding = Assert.Single(catalog.MetricBindings.Values,
            static binding => binding.Field?.Code == MetricFieldCode.GpuUsage);
        var api = new RecordedPdhApi
        {
            CollectStatus = engineFails ? 0 : 7,
            Values = [Valid($"luid_0x00000000_0x{luid:X8}_phys_0", 0)]
        };
        var reader = new PdhGpuAdapterSharedMemoryReader(api);
        var engineReads = 0;
        var selectedAdapterCount = 0;
        using var resources = CreateResources(inventory,
            adapters =>
            {
                engineReads++;
                if (engineFails) throw new IOException("recorded engine read failure");
                return new(new Dictionary<int, double> { [0] = 37.5 },
                    NativePdhProviderAvailability.Available, DateTimeOffset.UtcNow);
            }, adapters =>
            {
                selectedAdapterCount = adapters.Count;
                return reader.ReadByAdapterIndex(adapters);
            });
        var failures = new List<(Exception Error, string Source)>();
        var capture = new WindowsHardwareMetricCapture(resources, TimeSpan.Zero, new AmdIntegratedGpuSensorRoute(), () => { },
            (error, source) => failures.Add((error, source)));
        try
        {
            var request = MetricSampleRequest.ForIds([sharedBinding.MetricId, usageBinding.MetricId]);
            var snapshot = Assert.IsType<HardwareMetricSnapshot>(await capture.CaptureUnderGateAsync(
                request, workspace, 1, null, commit => { commit(); return true; }, default));

            Assert.Equal(1, selectedAdapterCount);
            Assert.Equal(1, engineReads);
            if (engineFails)
                Assert.Equal(0UL, snapshot.Items[sharedBinding.MetricId].IntegerValue?.LowBits);
            else
                Assert.Null(snapshot.Items[sharedBinding.MetricId].IntegerValue);
            Assert.Equal(engineFails ? SamplingObservationStatus.Unavailable : SamplingObservationStatus.Current,
                snapshot.Datasets[usageBinding.MetricId].Status);
            Assert.Equal(engineFails ? SamplingObservationStatus.Current : SamplingObservationStatus.Unavailable,
                snapshot.Datasets[sharedBinding.MetricId].Status);
            if (!engineFails)
                Assert.Equal(37.5, snapshot.Items[usageBinding.MetricId].NumericValue);
            Assert.All(failures, static failure => Assert.Equal(NativeMetricSnapshotSources.PdhGpuEngine, failure.Source));
        }
        finally { capture.SampleGate.Dispose(); }
    }

    [Fact]
    public async Task SharedOnlyRequestSkipsEngineReadAndPublishesCanonicalCatalogCandidate()
    {
        const uint luid = 0xE4A6;
        var inventory = RecordedInventory(luid);
        var gpuIdentities = WindowsHardwareMetricTopologySource.CaptureGpuIdentities(inventory);
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled,
            Path.Combine(Path.GetTempPath(), $"gpu-shared-only-{Guid.NewGuid():N}.bin"));
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled,
            gpuIdentities.Rows, [], [], workspace.CreateCatalogHandleMap());
        workspace.ReplaceCatalog(catalog, DateTimeOffset.UtcNow);
        var sharedBinding = Assert.Single(catalog.MetricBindings.Values,
            static binding => binding.Field?.Name == "gpu_all_shared_memory_used_bytes");
        var api = new RecordedPdhApi
        {
            Values = [Valid($"luid_0x00000000_0x{luid:X8}_phys_0", 0)]
        };
        var reader = new PdhGpuAdapterSharedMemoryReader(api);
        var engineReads = 0;
        var selectedAdapterCount = 0;
        using var resources = CreateResources(inventory, _ =>
        {
            engineReads++;
            throw new IOException("shared-only demand must not read engine usage");
        }, adapters =>
        {
            selectedAdapterCount = adapters.Count;
            return reader.ReadByAdapterIndex(adapters);
        });
        var capture = new WindowsHardwareMetricCapture(resources, TimeSpan.Zero, new AmdIntegratedGpuSensorRoute(), () => { });
        try
        {
            var request = MetricSampleRequest.ForIds([sharedBinding.MetricId]);
            var snapshot = Assert.IsType<HardwareMetricSnapshot>(await capture.CaptureUnderGateAsync(
                request, workspace, 1, null, commit => { commit(); return true; }, default));

            Assert.Equal(0, engineReads);
            Assert.Equal(1, selectedAdapterCount);
            Assert.Equal(0UL, snapshot.Items[sharedBinding.MetricId].IntegerValue?.LowBits);
            Assert.Equal(SamplingObservationStatus.Current, snapshot.Datasets[sharedBinding.MetricId].Status);
            var definition = Assert.Single(MetricCatalog.FromSnapshot(snapshot),
                item => item.Id == sharedBinding.MetricId);
            Assert.True(definition.Selectable);
            Assert.Equal("gpu_all_shared_memory_used_bytes", definition.FieldName);
            Assert.Equal(NativeMetricSnapshotSources.PdhGpuEngine, definition.SourceId);
            var candidate = Assert.Single(definition.CandidateSources);
            Assert.Equal("gpu.adapter_shared_memory_used", candidate.Reading?.Quantity);
            Assert.Equal(10, candidate.Reading?.SourceRank);
            Assert.Equal("available", candidate.SourceAvailability);
            Assert.False(candidate.IsSelected);
            Assert.True(candidate.Reading!.IsPreferredSource);
        }
        finally { capture.SampleGate.Dispose(); }
    }

    private static WindowsHardwareMetricResources CreateResources(WindowsGpuAdapterInventoryRead inventory,
        Func<IReadOnlyList<WindowsGpuAdapter>, PdhGpuEngineUsageRead> readGpuUsage,
        Func<IReadOnlyList<WindowsGpuAdapter>, PdhGpuAdapterSharedMemoryRead> readSharedMemory)
        => new()
        {
            ReadCpuCounters = () => throw Unselected(),
            ReadMemory = () => throw Unselected(),
            ReadVirtualMemory = () => throw Unselected(),
            ReadNumaAvailable = (_, _) => throw Unselected(),
            ReadNumaIdentities = () => throw Unselected(),
            ReadGpuInventory = () => inventory,
            ReadCpuFrequency = () => throw Unselected(),
            ReadGpuUsage = readGpuUsage,
            ReadGpuSharedMemory = readSharedMemory,
            ReadNvmlAsync = (_, _, _) => throw Unselected(),
            ReadNvapiAsync = (_, _, _) => throw Unselected(),
            ReadAdlxAsync = (_, _, _) => throw Unselected(),
            ReadAmdSmu = _ => throw Unselected(),
            ReadIntelGpu = (_, _, _) => throw Unselected(),
            ReadIntelMsr = (_, _) => throw Unselected(),
            ReadBatteryIdentities = () => throw Unselected(),
            ReadBattery = (_, _) => throw Unselected(),
            ReadNetworkIdentities = () => throw Unselected(),
            ReadNetworkInterfaces = (_, _) => throw Unselected(),
            ReadSystemIo = _ => throw Unselected(),
            ReadStorageIdentities = _ => throw Unselected(),
            ReadFanIdentities = _ => throw Unselected(),
            ReadStorageSensors = () => throw Unselected(),
            ReadNvmeSensors = _ => throw Unselected(),
            ReadAtaSensors = _ => throw Unselected(),
            ReadHardwareMonitor = () => throw Unselected(),
            ReadNotebookFans = () => throw Unselected(),
            SynchronizeNotebookDemand = _ => { }
        };

    private static WindowsGpuAdapterInventoryRead RecordedInventory(uint luid)
    {
        const string pnp = @"PCI\VEN_1002&DEV_164E&SUBSYS_12345678&REV_D8\4&1a2b3c4d&0&0041";
        var adapter = new WindowsGpuAdapter(0, "Recorded integrated GPU", 0x1002, 0x164E, 0,
            new AdapterLuid { LowPart = luid }, false, 0, WindowsGpuAdapterKind.Integrated)
        {
            PhysicalIdentityStatus = SamplingObservationStatus.Current,
            PhysicalIdentities =
            [
                new WindowsGpuPhysicalIdentity(0, pnp, WindowsGpuPnpKind.Pci, true, false, 0, 0, 0, 0,
                    0x1002, 0x164E, 0, 0, 1,
                    WindowsGpuIdentityEvidence.KmtLuidOpen | WindowsGpuIdentityEvidence.KmtHardwarePnpKey
                    | WindowsGpuIdentityEvidence.CmExactDevNode | WindowsGpuIdentityEvidence.CmCanonicalInstanceRoundTrip
                    | WindowsGpuIdentityEvidence.KmtDeviceIds | WindowsGpuIdentityEvidence.CmPciAddress,
                    WindowsGpuPhysicalIdentityStatus.Bound, 0)
            ]
        };
        return new(SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks, 1, 0, 0, 17, [adapter]);
    }

    private static WindowsGpuAdapter Adapter(int index, uint lowLuid, bool software = false)
        => new(index, $"GPU {index}", 0x1002, 0x164E, 0,
            new AdapterLuid { LowPart = lowLuid }, software, 0, WindowsGpuAdapterKind.Integrated);

    private static PdhGpuAdapterSharedMemoryCounterValue Valid(string instance, long value)
        => new(instance, 0, value);

    private static InvalidOperationException Unselected()
        => new("The source callback was not selected.");

    private sealed class RecordedPdhApi : IPdhGpuAdapterSharedMemoryApi
    {
        public int OpenStatus { get; init; }
        public int AddStatus { get; init; }
        public int CollectStatus { get; init; }
        public int ArrayStatus { get; init; }
        public int CloseStatus { get; init; }
        public IReadOnlyList<PdhGpuAdapterSharedMemoryCounterValue> Values { get; init; } = [];
        public int OpenCalls { get; private set; }
        public int CollectCalls { get; private set; }
        public int CloseCalls { get; private set; }
        public string? AddedPath { get; private set; }

        public int OpenQuery(out IntPtr query)
        {
            OpenCalls++;
            query = new IntPtr(1);
            return OpenStatus;
        }

        public int AddEnglishCounter(IntPtr query, string counterPath, out IntPtr counter)
        {
            AddedPath = counterPath;
            counter = new IntPtr(2);
            return AddStatus;
        }

        public int CollectQueryData(IntPtr query)
        {
            CollectCalls++;
            return CollectStatus;
        }

        public int GetFormattedCounterArray(IntPtr counter, uint format,
            out IReadOnlyList<PdhGpuAdapterSharedMemoryCounterValue> values)
        {
            values = Values;
            return ArrayStatus;
        }

        public int CloseQuery(IntPtr query)
        {
            CloseCalls++;
            return CloseStatus;
        }
    }
}
