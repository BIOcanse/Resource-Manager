using System.Collections.Immutable;
using System.Text.Json;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Native.Pdh;
using ResourceManager.ReadWrite.Windows.Metrics.Capture;
using ResourceManager.ReadWrite.Windows.Pdh;

namespace Resource_Manager_APP.Tests;

public sealed class MetricDeviceFieldTests
{
    [Fact]
    public void CanonicalNamesMatchDeclaredResourceAndVendorWithoutInstanceTokens()
    {
        Assert.Equal(MetricFieldRegistry.Definitions.Count,
            MetricFieldRegistry.Definitions.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count());
        foreach (var field in MetricFieldRegistry.Definitions)
        {
            if (field.Code is MetricFieldCode.NvmlFanSpeedPercentOfMax or MetricFieldCode.NvmlFanSpeedRpm)
            {
                Assert.Equal(MetricResource.Fan, field.Resource);
                Assert.Equal(MetricVendor.Nvidia, field.Vendor);
                Assert.Equal(field.Code == MetricFieldCode.NvmlFanSpeedPercentOfMax
                    ? "gpu_nvidia_fan_speed_percent_of_max"
                    : "gpu_nvidia_fan_speed_rpm", field.Name);
            }
            else if (field.Code == MetricFieldCode.IgclFanSpeedRpm)
            {
                Assert.Equal(MetricResource.Fan, field.Resource);
                Assert.Equal(MetricVendor.Intel, field.Vendor);
                Assert.Equal("gpu_intel_fan_speed_rpm", field.Name);
            }
            else
            {
                Assert.StartsWith($"{field.Resource.ToString().ToLowerInvariant()}_{field.Vendor.ToString().ToLowerInvariant()}_",field.Name);
            }
            Assert.Matches("^[a-z]+_(all|intel|amd|nvidia)_[a-z0-9_]+$",field.Name);
            Assert.Equal(field,MetricFieldRegistry.FindByName(field.Name));
        }
    }

    [Fact]
    public void DefaultPlanPublishesOptionalFieldsThroughNativeCommitAndExactIntegerWire()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled,
            Path.Combine(Path.GetTempPath(), $"metric-fields-{Guid.NewGuid():N}.bin"));
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled,
            [new(9, "NVIDIA fixture", "pnp-nvidia-fixture", 0xA1, 1, 1, true)], [], [], workspace.CreateCatalogHandleMap());
        var now = DateTimeOffset.UtcNow;
        workspace.ReplaceCatalog(catalog, now);
        var numbers = new Dictionary<MetricFieldCode, double>
        {
            [MetricFieldCode.MemoryAvailable] = 1024,
            [MetricFieldCode.CommitPeak] = 8192,
            [MetricFieldCode.ProcessCount] = 7,
            [MetricFieldCode.GpuMemoryTotal] = 16384,
            [MetricFieldCode.NvmlPowerInstant] = 8,
            [MetricFieldCode.NvmlPowerAverage] = 6
        };
        var selected = catalog.MetricBindings.Values.Where(binding => numbers.ContainsKey(binding.FieldCode)
            || binding.FieldCode is MetricFieldCode.NvmlClocksEvents or MetricFieldCode.NvmlFbUsed).ToArray();
        var modes = catalog.Sources.Select(source => new NativeMetricSnapshotSourceRuntimeMode(source.SourceHandle,
            1, NativeMetricSnapshotZoneMode.Normal, NativeMetricSnapshotSourceAvailability.Available)).ToArray();
        var plan = workspace.PlanCollection(selected.Select(static binding => binding.MetricHandle).ToArray(), modes, false, now);
        const ulong mask = (1UL << 63) | 7;
        const ulong byteCount = (1UL << 53) | 3;
        foreach (var source in plan.Sources)
        {
            var identity = new NativeMetricSnapshotSourceObservationIdentity(source.SourceHandle, 1, 1);
            var observations = plan.Metrics.Where(metric => metric.SourceHandle == source.SourceHandle
                && (metric.Flags & (uint)NativeMetricSnapshotMetricPlanFlags.Selected) != 0)
                .Select(metric =>
                {
                    var binding = catalog.RuleBindings[metric.RuleHandle];
                    return binding.FieldCode is MetricFieldCode.NvmlClocksEvents or MetricFieldCode.NvmlFbUsed
                        ? NativeMetricSnapshotObservationProjection.CreateUnsignedObservation(metric, binding, identity, now,
                            binding.FieldCode == MetricFieldCode.NvmlClocksEvents ? mask : byteCount)
                        : NativeMetricSnapshotObservationProjection.CreateObservation(metric, binding, identity, now, numbers[binding.FieldCode]);
                }).ToImmutableArray();
            workspace.CompleteSource(plan, new(source.SourceHandle, 1, 1, now,
                NativeMetricSnapshotSourceStatus.Complete, observations, null, [], 0, 0), now);
        }
        var snapshot = NativeMetricSnapshotCommittedProjection.Create(workspace.ReadCommitted(), "fixture", "fixture", 1);
        Assert.All(snapshot.FieldBindings.Values.Where(binding => binding.Field.Resource == MetricResource.Gpu),
            binding => Assert.Equal(0xA1UL, binding.AdapterKey));
        Assert.Equal(1024UL, snapshot.Memory.AvailableBytes);
        Assert.Equal(8192UL, snapshot.VirtualMemory.CommitPeakBytes);
        Assert.Equal(7U, snapshot.VirtualMemory.ProcessCount);
        foreach (var binding in selected)
        {
            var request = MetricDeviceFields.CreateRequest(snapshot, binding.DeviceIdentity, [binding.Field!.Name]);
            Assert.Equal([binding.MetricId], request.Ids);
            var value = Assert.Single(Assert.Single(MetricDeviceFields.Read(snapshot, binding.DeviceIdentity,
                new HashSet<string> { binding.Field.Name })).Fields);
            Assert.Equal(SamplingObservationStatus.Current, value.Status);
            Assert.Equal(binding.Field.Unit, value.Unit);
            Assert.Equal(binding.SourceId, value.SourceId);
            if (binding.FieldCode == MetricFieldCode.NvmlClocksEvents)
            {
                Assert.Equal(mask.ToString(), value.IntegerValue);
                Assert.Null(value.NumericValue);
                var json = JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                Assert.Contains($"\"integerValue\":\"{mask}\"", json);
            }
            else if (binding.FieldCode == MetricFieldCode.NvmlFbUsed)
                Assert.Equal(byteCount.ToString(), value.IntegerValue);
            else Assert.Equal(numbers[binding.FieldCode], value.NumericValue);
        }
        // One adapter exposes separate PDH/NVML definitions, not interchangeable observations.
        Assert.Contains(catalog.MetricBindings.Values, binding => binding.Field?.Name == "gpu_all_busiest_engine_busy_percent");
        Assert.Contains(catalog.MetricBindings.Values, binding => binding.Field?.Name == "gpu_nvidia_processor_busy_percent");
        Assert.DoesNotContain(catalog.MetricBindings.Values, binding => binding.Field?.Name == "gpu_amd_activity_percent");
        var fbTotal = Assert.Single(catalog.MetricBindings.Values, binding => binding.Field?.Name == "gpu_nvidia_fb_total_bytes");
        Assert.Equal("nvidia.nvml",fbTotal.SourceId);
        Assert.NotEqual(MetricFieldRegistry.FindByTemplate("gpu.{gpu}.vramTotal")!.Name,fbTotal.Field!.Name);
        Assert.DoesNotContain(catalog.RuleBindings.Values, binding => binding.SourceId == "hardware-monitor.core"
            && binding.Field?.Resource == MetricResource.Gpu);
    }

    [Fact]
    public void BoundUnavailableFieldRemainsSubscribableAndFailureKeepsItsDirectory()
    {
        var snapshot = CreateSnapshot(SamplingObservationStatus.Current);
        var binding = Assert.Single(snapshot.FieldBindings).Value;
        var state = new DashboardMonitoringCatalogState();
        state.PublishCatalogProbe(snapshot with { Items = new Dictionary<string, MetricValue>() });
        var request = MetricDeviceFields.CreateRequest(state.Current, binding.DeviceIdentity, [binding.Field.Name]);
        Assert.Equal(request.Ids, state.SelectSubscribable(request).Ids);
        var cache = new LastSuccessfulHardwareMetricSnapshot();
        cache.Publish(snapshot, request);
        cache.RecordFailure(request, snapshot.CapturedAt.AddSeconds(1), "fixture-failed");
        var failed = Assert.Single(Assert.Single(MetricDeviceFields.Read(cache.Read()!, binding.DeviceIdentity)).Fields);
        Assert.Equal(binding.Field.Name, failed.Name);
        Assert.Equal(SamplingObservationStatus.Unavailable, failed.Status);
        Assert.Null(failed.NumericValue);
        Assert.Null(failed.IntegerValue);
        cache.Publish(snapshot with { CapturedAt = snapshot.CapturedAt.AddSeconds(2) }, request);
        Assert.Equal(SamplingObservationStatus.Current,
            Assert.Single(Assert.Single(MetricDeviceFields.Read(cache.Read()!, binding.DeviceIdentity)).Fields).Status);
    }

    [Fact]
    public void UnknownDeviceFieldAndUnreadyCatalogCannotBecomeSilentEmptySubscriptions()
    {
        var snapshot = CreateSnapshot(SamplingObservationStatus.Current);
        Assert.Throws<InvalidOperationException>(() => MetricDeviceFields.CreateRequest(null, "device", ["field"]));
        Assert.Throws<ArgumentException>(() => MetricDeviceFields.CreateRequest(snapshot, "other", ["memory_all_physical_available_bytes"]));
        Assert.Throws<ArgumentException>(() => MetricDeviceFields.CreateRequest(snapshot, "memory", ["typo"]));
        Assert.Throws<ArgumentException>(() => MetricDeviceFields.CreateRequest(snapshot, "memory", []));
    }

    [Fact]
    public void IntelDeviceDoesNotExposeAmdCpuFields()
    {
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(HostManagerTestPlanFactory.CreatePlan().MetricSnapshot,
            [], [], [], NativeMetricSnapshotCatalogHandleMap.Empty, MetricVendor.Intel);
        Assert.DoesNotContain(catalog.RuleBindings.Values, binding => binding.Field?.Resource == MetricResource.Cpu
            && (binding.Field.Vendor == MetricVendor.Amd || binding.SourceId == "amd.smu"));
        Assert.Contains(catalog.RuleBindings.Values, binding => binding.Field?.Name == "cpu_all_usage_percent");
    }

    [Fact]
    public void SmuTemperatureDoesNotMergeIntoUnclassifiedWmiTemperature()
    {
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(HostManagerTestPlanFactory.CreatePlan().MetricSnapshot,
            [], [], [], NativeMetricSnapshotCatalogHandleMap.Empty, MetricVendor.Amd);
        var smu = Assert.Single(catalog.MetricBindings.Values, binding => binding.Field?.Name == "cpu_amd_smu_temperature_reported_celsius");
        var wmi = Assert.Single(catalog.MetricBindings.Values, binding => binding.Field?.Name == "cpu_all_temperature_reported_celsius");
        Assert.NotEqual(smu.MetricHandle, wmi.MetricHandle);
        Assert.Contains("not established", smu.Field!.Meaning);
    }

    [Fact]
    public void UsedVramBytesRemainCurrentWhenCapacityIsUnavailable()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled,
            Path.Combine(Path.GetTempPath(), $"metric-fields-vram-{Guid.NewGuid():N}.bin"));
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled,
            [new(9, "NVIDIA fixture", "pnp-nvidia-fixture", 0xA1, 1, 1, true)], [], [], workspace.CreateCatalogHandleMap());
        var binding = Assert.Single(catalog.MetricBindings.Values, row => row.Field?.Name == "gpu_nvidia_fb_used_v1_including_reserved_bytes");
        var now = DateTimeOffset.UtcNow;
        workspace.ReplaceCatalog(catalog, now);
        var modes = catalog.Sources.Select(source => new NativeMetricSnapshotSourceRuntimeMode(source.SourceHandle,
            1, NativeMetricSnapshotZoneMode.Normal, NativeMetricSnapshotSourceAvailability.Available)).ToArray();
        var handles = NativeMetricSnapshotRequestProjection.CreateMetricHandles(MetricSampleRequest.ForIds([binding.MetricId]), catalog);
        var plan = workspace.PlanCollection(handles, modes, false, now);
        foreach (var source in plan.Sources)
        {
            var identity = new NativeMetricSnapshotSourceObservationIdentity(source.SourceHandle, 1, 1);
            var observations = plan.Metrics.Where(metric => metric.SourceHandle == source.SourceHandle
                && (metric.Flags & (uint)NativeMetricSnapshotMetricPlanFlags.Selected) != 0)
                .Select(metric => NativeMetricSnapshotObservationProjection.CreateObservation(metric,
                    catalog.RuleBindings[metric.RuleHandle], identity, now,
                    catalog.RuleBindings[metric.RuleHandle].FieldCode == MetricFieldCode.GpuMemoryUsed ? 123456d : null)).ToImmutableArray();
            workspace.CompleteSource(plan, new(source.SourceHandle,1,1,now,
                NativeMetricSnapshotSourceStatus.Complete,observations,null,[],0,0),now);
        }
        var snapshot = NativeMetricSnapshotCommittedProjection.Create(workspace.ReadCommitted(),"fixture","fixture",1);
        var bytes = Assert.Single(Assert.Single(MetricDeviceFields.Read(snapshot,binding.DeviceIdentity,
            new HashSet<string> { binding.Field!.Name })).Fields);
        Assert.Equal(SamplingObservationStatus.Current, bytes.Status);
        Assert.Equal(123456d, bytes.NumericValue);
        Assert.Null(snapshot.Items[binding.MetricId].Percent);
    }

    [Fact]
    public void GpuEngineBusyUsesSamePdhReadAndNeverPublishesTheAdapterTrigger()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled,
            Path.Combine(Path.GetTempPath(), $"metric-fields-gpu-engine-{Guid.NewGuid():N}.bin"));
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled,
            [new(9, "NVIDIA fixture", "pnp-nvidia-fixture", 0xA1, 1, 1, true)], [], [], workspace.CreateCatalogHandleMap());
        var triggerMetric = Assert.Single(catalog.MetricBindings.Values,
            binding => binding.FieldCode == MetricFieldCode.GpuEngineBusy
                && binding.SourceId == NativeMetricSnapshotSources.PdhGpuEngine);
        var definition = MetricFieldRegistry.FindByName("gpu_all_engine_busy_percent")!;
        var now = DateTimeOffset.UtcNow;
        var identity = new NativePdhGpuEngineIdentity(0xA1, 2, 3, NativePdhEngineClass.Compute);
        var triggerAddress = triggerMetric.MetricId;
        var triggerSnapshot = CreateSnapshot(SamplingObservationStatus.Current) with
        {
            FieldBindings = new Dictionary<string, MetricDeviceFieldBinding>(StringComparer.OrdinalIgnoreCase)
            {
                [triggerAddress] = new("gpu-adapter|00000000000000A1", definition, triggerAddress,
                    NativeMetricSnapshotSources.PdhGpuEngine) { AdapterKey = 0xA1 }
            },
            Items = new Dictionary<string, MetricValue>(StringComparer.OrdinalIgnoreCase)
            {
                [triggerAddress] = new(triggerAddress, definition.Name, "gpu_adapter", "99.0%", 99, "%", 99, null)
                    { SourceId = NativeMetricSnapshotSources.PdhGpuEngine }
            },
            Datasets = new Dictionary<string, HardwareMetricDatasetObservation>(StringComparer.OrdinalIgnoreCase)
            {
                [triggerAddress] = new(triggerAddress, SamplingObservationStatus.Current, 1, now.UtcTicks,
                    1, 1, 1, 1)
            },
            CapturedAt = now,
            WorkspaceIdentity = 1,
            ConfigurationGeneration = 1,
            CatalogGeneration = 1,
            CommittedGeneration = 1
        };
        var read = new PdhGpuEngineUsageRead(new Dictionary<int, double>(),
            NativePdhProviderAvailability.Available, now)
        {
            UsageByEngine = new Dictionary<NativePdhGpuEngineIdentity, double> { [identity] = 42.5 }
        };

        // Discovery receives only the exact PDH engine sample, creates a public typed binding,
        // and strips the adapter-scoped planner trigger (including its stale value and status).
        var discovered = WindowsHardwareMetricCapture.ProjectGpuEngineFields(triggerSnapshot,
            MetricSampleRequest.CatalogProbe, null, read);
        var engineBinding = Assert.Single(discovered.FieldBindings.Values,
            binding => binding.PdhGpuEngineIdentity is not null);
        Assert.Equal(identity, engineBinding.PdhGpuEngineIdentity);
        Assert.Equal(NativeMetricSnapshotSources.PdhGpuEngine, engineBinding.SourceId);
        Assert.Equal(triggerAddress, engineBinding.EffectiveSampleRequestAddress);
        Assert.DoesNotContain(discovered.FieldBindings.Values, binding => binding.RequestAddress == triggerAddress);
        Assert.DoesNotContain(discovered.Items.Keys, key => key == triggerAddress);
        Assert.DoesNotContain(discovered.Datasets.Keys, key => key == triggerAddress);

        // A field request names the public engine address; it is mapped back to its internal
        // trigger only before native planning.
        var publicRequest = MetricDeviceFields.CreateRequest(discovered, engineBinding.DeviceIdentity, [definition.Name]);
        Assert.Equal([engineBinding.RequestAddress], publicRequest.Ids);
        var addressRequest = MetricSampleRequest.ForIds([engineBinding.RequestAddress]);
        Assert.Equal([triggerAddress], WindowsHardwareMetricCapture
            .CreateCollectionRequest(addressRequest, catalog, discovered).Ids);
        var collectionRequest = WindowsHardwareMetricCapture.CreateCollectionRequest(publicRequest, catalog, discovered);
        Assert.Equal([triggerAddress], collectionRequest.Ids);
        workspace.ReplaceCatalog(catalog, now);
        var handles = NativeMetricSnapshotRequestProjection.CreateMetricHandles(collectionRequest, catalog);
        var modes = catalog.Sources.Select(source => new NativeMetricSnapshotSourceRuntimeMode(source.SourceHandle,
            1, NativeMetricSnapshotZoneMode.Normal, NativeMetricSnapshotSourceAvailability.Available)).ToArray();
        var plan = workspace.PlanCollection(handles, modes, true, now);
        Assert.Contains(plan.Sources, source => catalog.SourceIds[source.SourceHandle] == NativeMetricSnapshotSources.PdhGpuEngine
            && (source.Flags & (uint)NativeMetricSnapshotSourcePlanFlags.Selected) != 0);
        Assert.DoesNotContain(plan.Sources, source => catalog.SourceIds[source.SourceHandle] == NativeMetricSnapshotSources.NvidiaNvml
            && (source.Flags & (uint)NativeMetricSnapshotSourcePlanFlags.Selected) != 0);
        Assert.Contains(plan.Metrics, metric => metric.MetricHandle == triggerMetric.MetricHandle
            && (metric.Flags & (uint)NativeMetricSnapshotMetricPlanFlags.Selected) != 0);

        // The typed engine value comes from that same PDH read even though the adapter trigger
        // has no numeric observation. The source can therefore complete successfully without
        // an adapter-level value; the trigger never enters the public committed projection.
        var triggerWithoutValue = triggerSnapshot with
        {
            Items = new Dictionary<string, MetricValue>(StringComparer.OrdinalIgnoreCase),
            Datasets = new Dictionary<string, HardwareMetricDatasetObservation>(StringComparer.OrdinalIgnoreCase)
            {
                [triggerAddress] = new(triggerAddress, SamplingObservationStatus.Unavailable, 1, 0,
                    1, 1, 1, 1)
            }
        };
        var projected = WindowsHardwareMetricCapture.ProjectGpuEngineFields(triggerWithoutValue,
            publicRequest, discovered, read);
        var field = Assert.Single(Assert.Single(MetricDeviceFields.Read(projected, engineBinding.DeviceIdentity,
            new HashSet<string> { definition.Name })).Fields);
        Assert.Equal(SamplingObservationStatus.Current, field.Status);
        Assert.Equal(42.5, field.NumericValue);
        Assert.Equal(now, field.ObservedAt);
        Assert.Equal(42.5, projected.Items[engineBinding.RequestAddress].NumericValue);
        Assert.DoesNotContain(projected.Items.Keys, key => key == triggerAddress);
        Assert.DoesNotContain(projected.Datasets.Keys, key => key == triggerAddress);

        // The cache's request expansion must replace the internal planner seed
        // with the public typed engine dataset as well. Checking only the
        // transient projection would miss a synthetic trigger failure dataset.
        var current = new LastSuccessfulHardwareMetricSnapshot();
        var publication = current.Publish(projected, publicRequest);
        Assert.DoesNotContain(triggerAddress, publication.CommittedDatasetIds);
        Assert.DoesNotContain(triggerAddress, publication.RejectedDatasetIds);
        var cached = Assert.IsType<HardwareMetricSnapshot>(current.Read());
        Assert.DoesNotContain(cached.Datasets.Keys, key => key == triggerAddress);
        var cachedField = Assert.Single(Assert.Single(MetricDeviceFields.Read(cached,
            engineBinding.DeviceIdentity, new HashSet<string> { definition.Name })).Fields);
        Assert.Equal(SamplingObservationStatus.Current, cachedField.Status);
        Assert.Equal(42.5, cachedField.NumericValue);
        Assert.Equal(now, cachedField.ObservedAt);

        // An unrelated CPU request does not plan a PDH GPU query or touch the last engine sample.
        var unrelated = MetricSampleRequest.ForIds(["cpu.usage"]);
        var unrelatedCollection = WindowsHardwareMetricCapture.CreateCollectionRequest(unrelated, catalog, discovered);
        Assert.Equal(unrelated.Ids, unrelatedCollection.Ids);
        var unrelatedHandles = NativeMetricSnapshotRequestProjection.CreateMetricHandles(unrelatedCollection, catalog);
        var unrelatedPlan = workspace.PlanCollection(unrelatedHandles, modes, false, now.AddSeconds(1));
        Assert.DoesNotContain(unrelatedPlan.Sources, source => catalog.SourceIds[source.SourceHandle] == NativeMetricSnapshotSources.PdhGpuEngine
            && (source.Flags & (uint)NativeMetricSnapshotSourcePlanFlags.Selected) != 0);
        var preserved = WindowsHardwareMetricCapture.ProjectGpuEngineFields(projected, unrelated, discovered, null);
        Assert.Equal(42.5, preserved.Items[engineBinding.RequestAddress].NumericValue);
        Assert.Equal(now.UtcTicks, preserved.Datasets[engineBinding.RequestAddress].ObservedAtUtcTicks);

        // A failed explicitly selected source clears the public value while retaining its typed directory entry.
        var failedRead = new PdhGpuEngineUsageRead(new Dictionary<int, double>(),
            NativePdhProviderAvailability.Unavailable, null);
        var failed = WindowsHardwareMetricCapture.ProjectGpuEngineFields(triggerSnapshot,
            publicRequest, discovered, failedRead);
        var failedField = Assert.Single(Assert.Single(MetricDeviceFields.Read(failed, engineBinding.DeviceIdentity,
            new HashSet<string> { definition.Name })).Fields);
        Assert.Equal(SamplingObservationStatus.Unavailable, failedField.Status);
        Assert.Null(failedField.NumericValue);
        Assert.Null(failedField.ObservedAt);
        Assert.DoesNotContain(failed.Items.Keys, key => key == triggerAddress);
        Assert.DoesNotContain(failed.Datasets.Keys, key => key == triggerAddress);
    }

    internal static HardwareMetricSnapshot CreateSnapshot(SamplingObservationStatus status)
    {
        const string address = "memory-scope/memory_all_physical_available_bytes";
        var now = DateTimeOffset.UtcNow;
        return new(now, new("fixture", 0, false, CpuMetricObservationStatus.NotRequested, 0,0,0,0,0,"fixture",
            new(new("fixture", "NotRequested", null), null,null,null,null)),
            new(0,0,0,false,"fixture"), new(0,0,0,"fixture",false), [],
            new(SamplingObservationStatus.NotRequested,0,0,0,0,0,0,[]),
            new Dictionary<string,MetricValue> { [address] = new(address,"fixture","Memory","",1024,"B",null,null)
                { SourceId = "windows.memory" } })
        {
            FieldBindings = new Dictionary<string,MetricDeviceFieldBinding>
            {
                [address] = new("memory", MetricFieldRegistry.FindByTemplate("memory.availableBytes")!, address, "windows.memory")
            },
            Datasets = new Dictionary<string,HardwareMetricDatasetObservation>
            { [address] = new(address,status,1,now.UtcTicks,1,1,1,1) },
            WorkspaceIdentity=1, ConfigurationGeneration=1, CatalogGeneration=1, CommittedGeneration=1
        };
    }
}
