using ResourceManager.ReadWrite.Vendors.Intel.Msr;
using ResourceManager.ReadWrite.Vendors.Amd.Smu;
using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Windows.Devices.Battery;
using ResourceManager.ReadWrite.Windows.Devices.Npu;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class NativeMetricSnapshotCatalogProjectionTests
{
    [Fact]
    public void GpuOnlyWildcardProjectsActualGpuHandlesAndCapacity()
    {
        var projection = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
            HostManagerTestPlanFactory.CreatePlan().MetricSnapshot,
            [new(0, "GPU", "gpu-test", 0x101UL, 1, VendorNvidia, true)], [], [],
            NativeMetricSnapshotCatalogHandleMap.Empty);
        var ids = NativeMetricSnapshotRequestProjection.CreateMetricHandles(
            MetricSampleRequest.ForIdsAndAllGpuCoreMetrics([]), projection)
            .Select(handle => projection.MetricBindings[handle].LegacyMetricId).ToHashSet();
        Assert.Contains("gpu.0.usage", ids);
        Assert.Contains("gpu.0.vram", ids);
        Assert.Contains("gpu.0.vramTotal", ids);
        Assert.DoesNotContain("cpu.usage", ids);
        Assert.DoesNotContain("memory.usage", ids);
    }

    [Fact]
    public void CatalogProbeDoesNotRequestNumericHandles()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        var projection = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
            plan,
            [],
            [],
            [],
            NativeMetricSnapshotCatalogHandleMap.Empty);

        var handles =
            NativeMetricSnapshotRequestProjection.CreateMetricHandles(
                MetricSampleRequest.CatalogProbe,
                projection);

        Assert.Empty(handles);
    }

    [Fact]
    public void UnavailableRequestedMetricsProduceAnEmptyNativeIntersection()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        var projection = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
            plan,
            [],
            [],
            [],
            NativeMetricSnapshotCatalogHandleMap.Empty);

        var handles =
            NativeMetricSnapshotRequestProjection.CreateMetricHandles(
                MetricSampleRequest.ForIds(
                ["gpu.99.usage", "removed.metric"]),
                projection);

        Assert.Empty(handles);
    }

    [Fact]
    public void CompositeUsageRequestsIncludeTheirInternalCapacityMetrics()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        var projection = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
            plan,
            [
                new(
                    0,
                    "NVIDIA GPU",
                    "gpu-nvidia",
                    0x101UL,
                    1,
                    VendorNvidia,
                    true),
                new(
                    1,
                    "AMD GPU",
                    "gpu-amd",
                    0x202UL,
                    1,
                    VendorAmd,
                    true)
            ],
            [],
            [],
            NativeMetricSnapshotCatalogHandleMap.Empty);

        var handles =
            NativeMetricSnapshotRequestProjection.CreateMetricHandles(
                MetricSampleRequest.ForIds(
                    [
                        "memory.usage",
                        "virtualMemory.usage",
                        "gpu.0.vram",
                        "gpu.1.vram"
                    ]),
                projection);
        var metricIds = handles
            .Select(handle => projection.MetricBindings[handle].LegacyMetricId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("memory.usage", metricIds);
        Assert.Contains("memory.total", metricIds);
        Assert.Contains("virtualMemory.usage", metricIds);
        Assert.Contains("virtualMemory.total", metricIds);
        Assert.Contains("gpu.0.vram", metricIds);
        Assert.Contains("gpu.0.vramTotal", metricIds);
        Assert.Contains("gpu.1.vram", metricIds);
        Assert.Contains("gpu.1.vramTotal", metricIds);
    }

    [Fact]
    public void UnrelatedRequestsDoNotIncludeInternalCapacityMetrics()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        var projection = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
            plan,
            [
                new(
                    0,
                    "NVIDIA GPU",
                    "gpu-nvidia",
                    0x101UL,
                    1,
                    VendorNvidia,
                    true)
            ],
            [],
            [],
            NativeMetricSnapshotCatalogHandleMap.Empty);

        var handles =
            NativeMetricSnapshotRequestProjection.CreateMetricHandles(
                MetricSampleRequest.ForIds(
                    [
                        "memory.percent",
                        "virtualMemory.percent",
                        "gpu.0.vramPercent"
                    ]),
                projection);
        var metricIds = handles
            .Select(handle => projection.MetricBindings[handle].LegacyMetricId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("memory.total", metricIds);
        Assert.DoesNotContain("virtualMemory.total", metricIds);
        Assert.DoesNotContain("gpu.0.vramTotal", metricIds);
    }

    [Fact]
    public void StaticCatalogProjectsEveryCompiledSourceAndStableMetricIdentity()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;

        var projection = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
            plan,
            [],
            [],
            [],
            NativeMetricSnapshotCatalogHandleMap.Empty);

        Assert.Equal(
            plan.HotPublish.SourcePolicies.Length,
            projection.Sources.Length);
        Assert.NotEmpty(projection.Rules);
        Assert.Equal(
            projection.Sources.Select(static row => row.SourceHandle)
                .Order(),
            projection.Sources.Select(static row => row.SourceHandle));
        Assert.Equal(
            projection.Rules.Select(static row => row.MetricHandle),
            projection.Rules.Select(static row => row.MetricHandle).Order());
        Assert.Contains(
            projection.MetricBindings.Values,
            static binding => binding.LegacyMetricId == "cpu.usage");
        Assert.Equal(
            projection.MetricIds.Count,
            projection.MetricHandles.Values.Distinct().Count());
        Assert.Equal(
            projection.Sources.Length,
            projection.SourceIds.Count);
        Assert.Equal(
            projection.Sources.Length,
            projection.SourceHandles.Count);
        Assert.Equal(
            projection.Rules.Length,
            projection.RuleBindings.Count);
        foreach (var (metricHandle, metricId) in projection.MetricIds)
        {
            Assert.Equal(
                metricHandle,
                projection.MetricHandles[metricId]);
        }
        foreach (var source in plan.HotPublish.SourcePolicies)
        {
            Assert.Equal(
                source.SourceId,
                projection.SourceIds[source.SourceHandle]);
            Assert.Equal(
                source.SourceHandle,
                projection.SourceHandles[source.SourceId]);
        }
        foreach (var rule in projection.Rules)
        {
            var binding = projection.RuleBindings[rule.RuleHandle];
            Assert.Equal(rule.MetricHandle, binding.MetricHandle);
            Assert.Equal(rule.SourceHandle, binding.SourceHandle);
            Assert.Equal(rule.ScopeHandle, binding.ScopeHandle);
            Assert.Equal(rule.MetricKind, binding.MetricKind);
            Assert.Equal(rule.ValueKind, binding.ValueKind);
            Assert.Equal(rule.CapabilityMask, binding.CapabilityMask);
            Assert.Equal(
                projection.MetricIds[rule.MetricHandle],
                binding.MetricId);
            Assert.Equal(
                projection.SourceIds[rule.SourceHandle],
                binding.SourceId);
        }
        Assert.Contains(
            projection.MetricBindings.Values,
            static binding => binding.LegacyMetricId == "disk.total.readBytesPerSec");
        Assert.DoesNotContain(
            projection.MetricBindings.Values,
            static binding => binding.LegacyMetricId.StartsWith(
                "gpu.",
                StringComparison.Ordinal));

        foreach (var metric in projection.Rules.GroupBy(
            static row => row.MetricHandle))
        {
            var canonical = metric.First();
            Assert.All(metric, row =>
            {
                Assert.Equal(canonical.Flags, row.Flags);
                Assert.Equal(canonical.ScopeHandle, row.ScopeHandle);
                Assert.Equal(canonical.MetricKind, row.MetricKind);
                Assert.Equal(canonical.ScopeKind, row.ScopeKind);
                Assert.Equal(canonical.ValueKind, row.ValueKind);
                Assert.Equal(
                    canonical.MinimumValueBits,
                    row.MinimumValueBits);
                Assert.Equal(
                    canonical.MaximumValueBits,
                    row.MaximumValueBits);
            });
        }
    }

    [Fact]
    public void ExactGpuIdentitiesExpandVendorAndDedicatedRulesDeterministically()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        var nvidia = NvidiaFanFixture.Adapter(0, 0x101, NvidiaFanFixture.ParentA) with { Name = "NVIDIA GPU" };
        var amd = NvidiaFanFixture.Adapter(1, 0x202, @"PCI\VEN_1002&DEV_2484\AMD") with
        {
            Name = "AMD GPU", VendorId = 0x1002, Kind = WindowsGpuAdapterKind.Integrated,
            PhysicalIdentities = [NvidiaFanFixture.Adapter(1, 0x202, @"PCI\VEN_1002&DEV_2484\AMD").PhysicalIdentities[0]
                with { VendorId = 0x1002 }]
        };
        WindowsGpuAdapter[] recordedInventory = [amd, nvidia];
        var captured = ResourceManager.ReadWrite.Windows.Metrics.Topology.WindowsHardwareMetricTopologySource.CaptureGpuIdentities(
            new(SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks, 2, 0, 0, 0, recordedInventory));
        Assert.Null(captured.FailureCode);
        var identities = captured.Rows;

        var first = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
            plan,
            identities,
            [],
            [],
            NativeMetricSnapshotCatalogHandleMap.Empty);
        var second = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
            plan,
            identities.Reverse().ToArray(),
            [],
            [],
            NativeMetricSnapshotCatalogHandleMap.Empty);

        Assert.Equal(
            first.NativeRowFingerprint,
            second.NativeRowFingerprint);
        Assert.Equal(
            first.CatalogIdentitySha256,
            second.CatalogIdentitySha256);
        Assert.Equal(
            "NVIDIA GPU",
            first.GpuScopeBindings.Values.Single(
                static value => value.DisplayIndex == 0).DisplayName);
        Assert.Equal(
            "AMD GPU",
            first.GpuScopeBindings.Values.Single(
                static value => value.DisplayIndex == 1).DisplayName);
        Assert.Equal(
            MemoryMarshal.AsBytes(first.Sources.AsSpan()).ToArray(),
            MemoryMarshal.AsBytes(second.Sources.AsSpan()).ToArray());
        Assert.Equal(
            MemoryMarshal.AsBytes(first.Rules.AsSpan()).ToArray(),
            MemoryMarshal.AsBytes(second.Rules.AsSpan()).ToArray());
        Assert.Equal(
            first.MetricIds.OrderBy(static pair => pair.Key).ToArray(),
            second.MetricIds.OrderBy(static pair => pair.Key).ToArray());
        Assert.Contains("gpu.0.vram", first.LegacyMetricHandles.Keys);
        Assert.DoesNotContain("gpu.1.vram", first.LegacyMetricHandles.Keys);
        Assert.Contains("gpu.1.graphicsClock", first.LegacyMetricHandles.Keys);
        Assert.DoesNotContain("gpu.0.boardPower", first.LegacyMetricHandles.Keys);
        Assert.Contains("gpu.1.boardPower", first.LegacyMetricHandles.Keys);

        var nvidiaScope = first.GpuScopeBindings.Single(
            static pair => pair.Value.DisplayIndex == 0).Key;
        var amdScope = first.GpuScopeBindings.Single(
            static pair => pair.Value.DisplayIndex == 1).Key;
        Assert.All(
            RulesForMetric(first, "gpu.0.usage"),
            row => Assert.Equal(nvidiaScope, row.ScopeHandle));
        Assert.All(
            RulesForMetric(first, "gpu.1.usage"),
            row => Assert.Equal(amdScope, row.ScopeHandle));
        Assert.Contains(
            RulesForMetric(first, "gpu.0.usage"),
            static row => row.SourceHandle == 7);
        Assert.DoesNotContain(
            RulesForMetric(first, "gpu.1.usage"),
            static row => row.SourceHandle == 7);
        var clocks = first.MetricBindings.Values.Where(binding => binding.DisplayIndex == 1
            && binding.FieldCode == MetricFieldCode.GpuGraphicsClock).ToArray();
        Assert.Equal(2, clocks.Length);
        Assert.Equal(2, clocks.Select(binding => binding.MetricHandle).Distinct().Count());
        Assert.Contains(clocks, static binding => binding.SourceId == "amd.adlx");
        Assert.Contains(clocks, static binding => binding.SourceId == "amd.smu");
        var samePhysicalInventory = recordedInventory.Reverse()
            .Select(static adapter => adapter with { Name = "Renamed " + adapter.Name }).ToArray();
        Assert.True(WindowsHardwareMetricSampler.MatchesExactGpuInventory(
            first,
            samePhysicalInventory));
        Assert.False(WindowsHardwareMetricSampler.MatchesExactGpuInventory(
            first,
            samePhysicalInventory[1..]));
        Assert.False(WindowsHardwareMetricSampler.MatchesExactGpuInventory(first,
            samePhysicalInventory.Select(static adapter => adapter with { Index = adapter.Index + 4 }).ToArray()));
    }

    [Fact]
    public void DuplicateOrInvalidGpuIdentityIsRejectedBeforeNativeMutation()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;

        Assert.Throws<InvalidOperationException>(() =>
            NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
                plan,
                [
                    new(
                        0,
                        "GPU A",
                        "gpu-a",
                        1,
                        1,
                        VendorNvidia,
                        true),
                    new(
                        0,
                        "GPU B",
                        "gpu-b",
                        2,
                        1,
                        VendorAmd,
                        true)
                ],
                [],
                [],
                NativeMetricSnapshotCatalogHandleMap.Empty));
        Assert.Throws<InvalidOperationException>(() =>
            NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
                plan,
                [
                    new(
                        0,
                        "GPU A",
                        "gpu-a",
                        0,
                        1,
                        VendorNvidia,
                        true)
                ],
                [],
                [],
                NativeMetricSnapshotCatalogHandleMap.Empty));
        Assert.Throws<InvalidOperationException>(() =>
            NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
                plan,
                [new(0, "GPU A", "gpu-a", 1, 1, 3, true)],
                [],
                [],
                NativeMetricSnapshotCatalogHandleMap.Empty));
    }

    [Fact]
    public void DynamicInstanceLimitIsCountBasedAndSparseOrdinalsArePreserved()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        NativeMetricSnapshotOrdinalCatalogIdentity[] fans =
        [
            new(20, "fan-a", 1),
            new(30, "fan-b", 1)
        ];

        var projection = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
            plan,
            [],
            [],
            fans,
            NativeMetricSnapshotCatalogHandleMap.Empty);

        Assert.Contains(
            projection.MetricBindings.Values,
            static binding => binding.LegacyMetricId == "fan.20.speed");
        Assert.Contains(
            projection.MetricBindings.Values,
            static binding => binding.LegacyMetricId == "fan.30.speed");
    }

    [Fact]
    public void SameMetricIdWithDifferentNativeIdentityIsRejectedBeforePInvoke()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        var group = plan.HotPublish.RuleTemplates
            .GroupBy(static template => template.MetricIdTemplate)
            .First(static templates => templates.Count() > 1);
        var target = group.Skip(1).First();
        var changedTemplates = plan.HotPublish.RuleTemplates
            .Select(template =>
                string.Equals(
                    template.TemplateId,
                    target.TemplateId,
                    StringComparison.Ordinal)
                    ? template with
                    {
                        MinimumValueBits =
                            template.MinimumValueBits == 0 ? 1UL : 0UL
                    }
                    : template)
            .ToImmutableArray();
        var changedPlan = plan with
        {
            HotPublish = plan.HotPublish with
            {
                RuleTemplates = changedTemplates
            }
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
                changedPlan,
                [],
                [],
                [],
                NativeMetricSnapshotCatalogHandleMap.Empty));
        Assert.Contains(
            "conflicting native identity",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StableHandlesSurvivePresentationReorderAndUnrelatedInsertion()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        NativeMetricSnapshotGpuCatalogIdentity[] firstTopology =
        [
            new(0, "GPU A", "gpu-a", 0xA1UL, 1, VendorNvidia, true),
            new(1, "GPU B", "gpu-b", 0xB1UL, 1, VendorAmd, false)
        ];
        var first = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
            plan,
            firstTopology,
            [],
            [],
            NativeMetricSnapshotCatalogHandleMap.Empty);
        var firstAUsage = first.MetricHandles["gpu.0.usage"];
        var firstBUsage = first.MetricHandles["gpu.1.usage"];

        NativeMetricSnapshotGpuCatalogIdentity[] reordered =
        [
            new(0, "GPU B", "gpu-b", 0xB2UL, 2, VendorAmd, false),
            new(1, "GPU A", "gpu-a", 0xA2UL, 2, VendorNvidia, true)
        ];
        var second = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
            plan,
            reordered,
            [],
            [],
            first.HandleMap);

        Assert.Equal(
            firstAUsage,
            second.MetricHandles["gpu.1.usage"]);
        Assert.Equal(
            firstBUsage,
            second.MetricHandles["gpu.0.usage"]);
        Assert.NotEqual(
            first.CatalogIdentitySha256,
            second.CatalogIdentitySha256);

        NativeMetricSnapshotGpuCatalogIdentity[] inserted =
        [
            new(0, "GPU B", "gpu-b", 0xB3UL, 3, VendorAmd, false),
            new(1, "GPU A", "gpu-a", 0xA3UL, 3, VendorNvidia, true),
            new(2, "GPU C", "gpu-c", 0xC3UL, 3, VendorAmd, false)
        ];
        var third = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
            plan,
            inserted,
            [],
            [],
            second.HandleMap);
        Assert.Equal(
            firstAUsage,
            third.MetricHandles["gpu.1.usage"]);
        Assert.Equal(
            firstBUsage,
            third.MetricHandles["gpu.0.usage"]);
    }

    private static IReadOnlyList<NativeMetricSnapshotMetricDefinitionInput>
        RulesForMetric(
            NativeMetricSnapshotCatalogProjection projection,
            string metricId)
    {
        var handle = projection.MetricHandles[metricId];
        return projection.Rules
            .Where(row => row.MetricHandle == handle)
            .ToArray();
    }

    private const uint VendorNvidia = 1;
    private const uint VendorAmd = 2;
    [Fact]
    public void ActualCatalogUsageAndReadAllocationsDoNotDependOnConfiguredUpperBounds()
    {
        var configuration = ResourceManager.ReadWrite.Windows.Metrics.Configuration.WindowsMetricConfiguration.Default;
        var original = configuration.Configuration;
        original.MaximumMetricCount = 12288;
        original.MaximumRequestedCount = 12288;
        original.MaximumObservationCount = 12288;
        original.MaximumPlanMetricCount = 12288;
        original.MaximumMetricPlanCount = 12288;
        var intermediate = configuration.Configuration;
        intermediate.MaximumMetricCount = 12800;
        intermediate.MaximumRequestedCount = 12800;
        intermediate.MaximumObservationCount = 12800;
        intermediate.MaximumPlanMetricCount = 12800;
        intermediate.MaximumMetricPlanCount = 12800;
        // The upper bounds remain validation ceilings; the same real catalog
        // must consume the same native work area and managed read-result bytes.
        using var before = new NativeMetricSnapshotWorkspace(original,
            configuration.CatalogConfiguration with { MaximumMetricCount = 12288 });
        using var after = new NativeMetricSnapshotWorkspace(intermediate,
            configuration.CatalogConfiguration with { MaximumMetricCount = 12800 });
        Assert.Equal(0x000D_0000U, before.Configuration.AbiVersion); Assert.Equal(before.Configuration.AbiVersion, after.Configuration.AbiVersion);
        Assert.Equal(12288U, before.Capacity.MetricCapacity); Assert.Equal(12800U, after.Capacity.MetricCapacity);
        Assert.Equal(before.Capacity.ResidentByteCount, after.Capacity.ResidentByteCount);
        Assert.Equal(before.Capacity.ScratchByteCount, after.Capacity.ScratchByteCount);
        var emptyResidentBytes = before.Capacity.ResidentByteCount;
        var catalog = NativeMetricSnapshotCatalogProjection.Create(configuration.CatalogConfiguration,
            [new(0, "Recorded GPU", "recorded-gpu", 0xA1, 1, VendorNvidia, true)], [], [], NativeMetricSnapshotCatalogHandleMap.Empty);
        var capturedAt = DateTimeOffset.UtcNow;
        before.ReplaceCatalog(catalog, capturedAt);
        after.ReplaceCatalog(catalog, capturedAt);
        Assert.True(before.Capacity.ResidentByteCount > emptyResidentBytes);
        Assert.Equal(before.Capacity.ResidentByteCount, after.Capacity.ResidentByteCount);
        Assert.Equal(before.Capacity.ScratchByteCount, after.Capacity.ScratchByteCount);
        var beforeFrame = before.ReadCommitted();
        var afterFrame = after.ReadCommitted();
        Assert.Equal((beforeFrame.Sources.Length, beforeFrame.Metrics.Length,
                beforeFrame.GpuInventory.Length, beforeFrame.Rules.Length),
            (afterFrame.Sources.Length, afterFrame.Metrics.Length,
                afterFrame.GpuInventory.Length, afterFrame.Rules.Length));
        // Warm complete batches on both paths, then compare their allocation
        // floors. A one-off runtime allocation must not masquerade as a
        // capacity-dependent cost; a per-read cost still appears in every batch.
        _ = ReadAllocations(before);
        _ = ReadAllocations(after);
        var beforeAllocated = long.MaxValue;
        var afterAllocated = long.MaxValue;
        for (var batch = 0; batch < 3; batch++)
        {
            beforeAllocated = Math.Min(beforeAllocated, ReadAllocations(before));
            afterAllocated = Math.Min(afterAllocated, ReadAllocations(after));
        }
        Assert.Equal(beforeAllocated, afterAllocated);

        static long ReadAllocations(NativeMetricSnapshotWorkspace workspace)
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var iteration = 0; iteration < 32; iteration++) GC.KeepAlive(workspace.ReadCommitted());
            return GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        }
        Assert.Equal(67_108_864UL, before.Configuration.ResidentByteBudget); Assert.Equal(before.Configuration.ResidentByteBudget, after.Configuration.ResidentByteBudget);
        Assert.True(checked(before.Capacity.ResidentByteCount + before.Capacity.ScratchByteCount) <= before.Configuration.ResidentByteBudget);
        Assert.True(checked(after.Capacity.ResidentByteCount + after.Capacity.ScratchByteCount) <= after.Configuration.ResidentByteBudget);
        Assert.Equal(before.Configuration.MaximumRuleCount, after.Configuration.MaximumRuleCount);
        Assert.Equal(before.Configuration.MaximumSourceCount, after.Configuration.MaximumSourceCount);
        Assert.Equal(before.Configuration.MetricIndexCapacity, after.Configuration.MetricIndexCapacity);
        Assert.Equal(before.Configuration.RuleIndexCapacity, after.Configuration.RuleIndexCapacity);
    }
    [Fact]
    public void MaximumApplicableDirectoriesIncludingNpu64FitOneRealNativeCatalog()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        var config = NativeMetricSnapshotHostConfiguration.CreateCatalogConfiguration(plan);
        var templates = config.RuleTemplates;
        int Bound(uint scope, string? source = null)
            => checked((int)Assert.Single(templates.Where(row => row.ScopeExpansion == scope
                && (source is null || row.SourceId == source)).Select(row => row.MaximumInstanceCount).Distinct()));

        // Catalog-capacity envelope, not a claim that one physical machine has
        // every CPU/MSR/disk capability simultaneously. No reader is invoked.
        var gpus = Enumerable.Range(0, checked((int)config.MaximumGpuAdapterCount))
            .Select(i => new NativeMetricSnapshotGpuCatalogIdentity(i, "Recorded maximum GPU",
                @"PCI\VEN_10DE&DEV_2684\CAPACITY_" + i, (ulong)i + 1, 1, VendorNvidia, true)).ToArray();
        var disks = Enumerable.Range(0, Bound(5)).Select(i =>
            new NativeMetricSnapshotOrdinalCatalogIdentity(i, "recorded-storage|" + i, 1)
                { SupportsNvmeHealth = true, SupportsAtaSmart = true }).ToArray();
        var fans = new List<NativeMetricSnapshotOrdinalCatalogIdentity>();
        for (var i = 0; i < Bound(6, NativeMetricSnapshotSources.HardwareMonitorWmi); i++)
            fans.Add(new(i, "recorded-wmi-fan|" + i, 1));
        var nvmlCount = Bound(6, NativeMetricSnapshotSources.NvidiaNvml);
        Assert.Equal(NvidiaNvmlReader.MaxFanCount, nvmlCount);
        Assert.Equal(16, gpus.Length);
        for (var i = 0; i < nvmlCount; i++)
        {
            var fan = new NvidiaNvmlFanIdentity(gpus[i / NvidiaNvmlReader.MaxFansPerDevice].ExactIdentityKey,
                (uint)(i % NvidiaNvmlReader.MaxFansPerDevice));
            fans.Add(new(fans.Count, fan.ExactIdentityKey, 1) { NvmlFan = fan });
        }
        // Original OEM route has exactly two role identities, not 64 invented roles.
        var oem = WindowsNotebookFanProjection.CreateCatalogIdentities(fans.Count, 1);
        Assert.Equal(2, oem.Count);
        Assert.True(oem.Count <= Bound(6, NativeMetricSnapshotSources.NotebookOemFan));
        fans.AddRange(oem);
        var intelFanDirectoryId = Guid.NewGuid();
        for (var i = 0; i < Bound(6, NativeMetricSnapshotSources.IntelGcl); i++)
        {
            var fan = new ResourceManager.ReadWrite.Vendors.Intel.IntelGclFanIdentity(gpus[0].ExactIdentityKey, intelFanDirectoryId, (uint)i);
            fans.Add(new(fans.Count, fan.ExactIdentityKey, 1) { IgclFan = fan });
        }
        using var engines = new NpuEngineRecordedResources.Directory(Bound(17));
        var batteries = Enumerable.Range(0, Bound(8)).Select(i => new BatteryCatalogIdentity(i,
            @"\\?\RECORDED-BATTERY-" + i, "capacity-battery-" + i,
            new BatteryInformation(i % 2 == 0 ? 0U : 0x40000000U, "LION", 100, 100, 1))).ToArray();
        var numa = Enumerable.Range(0, Bound(9)).Select(i => new NumaMemoryCatalogIdentity((ushort)i)).ToArray();
        var interfaces = Enumerable.Range(0, Bound(14)).Select(i =>
            new NetworkInterfaceCatalogIdentity(i, new Guid(i + 1, 1, 2, new byte[8]), (ulong)i + 1, (uint)i + 1)).ToArray();
        var npus = Enumerable.Range(0, Bound(16)).Select(i => new NpuMemoryCatalogIdentity(@"ACPI\RECORDED_NPU\CAPACITY_" + i)).ToArray();
        var cpus = new List<IntelMsrCatalogIdentity>();
        AddCpus(IntelMsrTargetKind.PhysicalCore, 10);
        AddCpus(IntelMsrTargetKind.Package, 11);
        AddCpus(IntelMsrTargetKind.LogicalProcessor, 12);
        AddCpus(IntelMsrTargetKind.PhysicalDie, 13);
        AddCpus(IntelMsrTargetKind.Platform, 15);
        var amdTarget = new AmdCcdCpuTarget("recorded-single-amd-package", 0x1A, 0, 0xB00F00, [new(0, 0)]);
        var amdFormat = AmdCcdTemperatureFormat.Resolve(amdTarget.Family, amdTarget.Model)!;
        var amdCcds = Enumerable.Range(0, Bound(13, NativeMetricSnapshotSources.AmdSmn))
            .Select(index => new AmdCcdCatalogIdentity(amdTarget, index, amdFormat.RegisterAddress(index))).ToArray();

        using var workspace = new NativeMetricSnapshotWorkspace(
            NativeMetricSnapshotHostConfiguration.CreateConfiguration(plan), config);
        Assert.Equal(13_504U, workspace.Capacity.MetricCapacity);
        Assert.Equal(16384U, workspace.Capacity.RuleCapacity);
        Assert.True(checked(workspace.Capacity.ResidentByteCount + workspace.Capacity.ScratchByteCount) <= plan.Recreate.Capacity.ResidentByteBudget);
        var projection = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(plan, gpus, disks, fans,
            workspace.CreateCatalogHandleMap(), cpuVendor: MetricVendor.All, batteries: batteries,
            numaNodes: numa, intelCpus: cpus, networkInterfaces: interfaces, npuMemoryIdentities: npus, npuEngineIdentities: engines.Identities, npuEngineDirectory: engines,
            amdCcdIdentities: amdCcds);
        Assert.Equal(256, projection.NpuEngineIdentities.Length);
        // Each engine retains its raw counter and ratio; each NPU also has one
        // device busy field. Capacity includes the newly declared node/thread fields.
        Assert.Equal(2 * engines.Identities.Count + Bound(16),
            projection.RuleBindings.Values.Count(row => row.SourceId == NativeMetricSnapshotSources.WindowsNpuEngine));
        Assert.Equal(Bound(16), projection.RuleBindings.Values.Count(row => row.FieldCode == MetricFieldCode.NpuDeviceEngineBusyPercent));
        Assert.Equal(Bound(9), projection.RuleBindings.Values.Count(row => row.FieldCode == MetricFieldCode.NumaNodeCapacityMiB));
        Assert.Equal(Bound(12), projection.RuleBindings.Values.Count(row => row.FieldCode == MetricFieldCode.IntelEffectiveFrequencyMhz));
        Assert.Equal(16, projection.SystemFanIdentities.Count(row => row.IgclFan is not null));
        Assert.Equal(64, projection.NpuMemoryIdentities.Length);
        Assert.Equal(256, projection.RuleBindings.Values.Count(row => row.SourceId == NativeMetricSnapshotSources.WindowsNpuMemory));
        Assert.Equal(2432, projection.RuleBindings.Values.Count(row => row.SourceId == NativeMetricSnapshotSources.WindowsNetworkInterface));
        Assert.Equal(1696, projection.RuleBindings.Values.Count(row => row.SourceId is
            NativeMetricSnapshotSources.WindowsStorageSensors or NativeMetricSnapshotSources.WindowsDiskPerformance
            or NativeMetricSnapshotSources.WindowsNvmeHealth or NativeMetricSnapshotSources.WindowsAtaSmart));
        Assert.Equal(660, projection.RuleBindings.Values.Count(row => row.Field?.Resource == MetricResource.Fan));
        Assert.Equal(16, projection.RuleBindings.Values.Count(row => row.FieldCode == MetricFieldCode.AmdCcdTdieTemperature));
        var addedNodeThreadAndDeviceFields = Bound(9) + Bound(12) + Bound(16) + amdCcds.Length;
        Assert.Equal(12103 + addedNodeThreadAndDeviceFields + gpus.Length, projection.Rules.Length);
        var addedAdlxFields = gpus.Count(gpu => (gpu.VendorMask & VendorAmd) != 0) * 2;
        Assert.Equal(12069 + addedNodeThreadAndDeviceFields + gpus.Length + addedAdlxFields, projection.MetricBindings.Count);
        // These are the original native replacement and native header, not a
        // managed row-count-only assertion or a replacement workspace stub.
        var header = workspace.ReplaceCatalog(projection, DateTimeOffset.UtcNow);
        Assert.Equal((uint)NativeMetricSnapshotPhase.CatalogReady, header.Phase);
        Assert.Equal((uint)projection.Sources.Length, header.SourceCount);
        Assert.Equal((uint)projection.Rules.Length, header.RuleCount);
        Assert.Equal((uint)projection.MetricBindings.Count, header.MetricCount);
        Assert.Equal(projection.CatalogIdentitySha256, workspace.ReadCatalog().CatalogIdentitySha256);

        void AddCpus(IntelMsrTargetKind kind, uint scope)
        {
            var maximum = Bound(scope, NativeMetricSnapshotSources.IntelMsr);
            for (var i = 0; i < maximum; i++)
                cpus.Add(new IntelMsrCatalogIdentity(i, "recorded-intel-capacity|" + kind + "|" + i,
                    new CpuProcessorAddress((ushort)(i / 64), (byte)(i % 64)), 0x406E0, (uint)i, kind, true, true)
                {
                    SupportsPerformanceFeedback = true,
                    IntelProcessorBaseFrequencyDeclaredMhz = 1000,
                    RaplProfile = new IntelRaplProfile(IntelRaplDomains.Package | IntelRaplDomains.Pp0
                        | IntelRaplDomains.Pp1 | IntelRaplDomains.Dram)
                    { PowerInformationDomains = IntelRaplDomains.Package | IntelRaplDomains.Dram, DramPowerUsesRaplUnits = true },
                    ThermalStatusCapabilities = IntelThermalStatusCapabilities.Raw | IntelThermalStatusCapabilities.Thermal
                        | IntelThermalStatusCapabilities.Power | IntelThermalStatusCapabilities.Current | IntelThermalStatusCapabilities.CrossDomain
                });
        }
    }

}
