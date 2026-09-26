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
        var projection = NativeMetricSnapshotCatalogProjection.Create(
            HostManagerTestPlanFactory.CreatePlan().MetricSnapshot,
            [new(0, "GPU", "gpu-test", 0x101UL, 1, VendorNvidia, true)], [], [],
            NativeMetricSnapshotCatalogHandleMap.Empty);
        var ids = NativeMetricSnapshotRequestProjection.CreateMetricHandles(
            MetricSampleRequest.ForIdsAndAllGpuCoreMetrics([]), projection)
            .Select(handle => projection.MetricIds[handle]).ToHashSet();
        Assert.Contains("gpu.0.usage", ids);
        Assert.Contains("gpu.0.vram", ids);
        Assert.Contains("gpu.0.vramTotal", ids);
        Assert.DoesNotContain("cpu.usage", ids);
        Assert.DoesNotContain("memory.usage", ids);
    }

    [Fact]
    public void CatalogProbeProjectsEveryActiveMetricHandle()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        var projection = NativeMetricSnapshotCatalogProjection.Create(
            plan,
            [],
            [],
            [],
            NativeMetricSnapshotCatalogHandleMap.Empty);

        var handles =
            NativeMetricSnapshotRequestProjection.CreateMetricHandles(
                MetricSampleRequest.CatalogProbe,
                projection);

        Assert.NotEmpty(handles);
        Assert.Equal(
            projection.MetricHandles.Values.Distinct().Order(),
            handles);
    }

    [Fact]
    public void UnavailableRequestedMetricsProduceAnEmptyNativeIntersection()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        var projection = NativeMetricSnapshotCatalogProjection.Create(
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
        var projection = NativeMetricSnapshotCatalogProjection.Create(
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
            .Select(projection.MetricIds.GetValueOrDefault)
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
        var projection = NativeMetricSnapshotCatalogProjection.Create(
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
            .Select(projection.MetricIds.GetValueOrDefault)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("memory.total", metricIds);
        Assert.DoesNotContain("virtualMemory.total", metricIds);
        Assert.DoesNotContain("gpu.0.vramTotal", metricIds);
    }

    [Fact]
    public void StaticCatalogProjectsEveryCompiledSourceAndStableMetricIdentity()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;

        var projection = NativeMetricSnapshotCatalogProjection.Create(
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
            projection.MetricIds,
            static pair => pair.Value == "cpu.usage");
        Assert.Equal(
            projection.MetricIds.Count,
            projection.MetricHandles.Count);
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
            projection.MetricIds,
            static pair => pair.Value == "disk.total.readBytesPerSec");
        Assert.DoesNotContain(
            projection.MetricIds,
            static pair => pair.Value.StartsWith(
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
        NativeMetricSnapshotGpuCatalogIdentity[] identities =
        [
            new(1, "AMD GPU", "gpu-amd", 0x202UL, 1, VendorAmd, false),
            new(
                0,
                "NVIDIA GPU",
                "gpu-nvidia",
                0x101UL,
                1,
                VendorNvidia,
                true)
        ];

        var first = NativeMetricSnapshotCatalogProjection.Create(
            plan,
            identities,
            [],
            [],
            NativeMetricSnapshotCatalogHandleMap.Empty);
        var second = NativeMetricSnapshotCatalogProjection.Create(
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
        Assert.Contains(first.MetricIds, static pair =>
            pair.Value == "gpu.0.vram");
        Assert.DoesNotContain(first.MetricIds, static pair =>
            pair.Value == "gpu.1.vram");
        Assert.Contains(first.MetricIds, static pair =>
            pair.Value == "gpu.1.graphicsClock");
        Assert.DoesNotContain(first.MetricIds, static pair =>
            pair.Value == "gpu.0.boardPower");
        Assert.DoesNotContain(first.MetricIds, static pair =>
            pair.Value == "gpu.1.boardPower");

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
        Assert.Contains(
            RulesForMetric(first, "gpu.1.graphicsClock"),
            static row => row.SourceHandle == 9);
        WindowsGpuAdapter[] samePhysicalInventory =
        [
            new(
                9,
                "Renamed AMD GPU",
                0x1002,
                1,
                1,
                new AdapterLuid { LowPart = 0x202, HighPart = 0 },
                false,
                0,
                WindowsGpuAdapterKind.Dedicated),
            new(
                4,
                "Renamed NVIDIA GPU",
                0x10DE,
                2,
                1,
                new AdapterLuid { LowPart = 0x101, HighPart = 0 },
                false,
                0,
                WindowsGpuAdapterKind.Dedicated)
        ];
        Assert.True(WindowsHardwareMetricSampler.MatchesExactGpuInventory(
            first,
            samePhysicalInventory));
        Assert.False(WindowsHardwareMetricSampler.MatchesExactGpuInventory(
            first,
            samePhysicalInventory[1..]));
    }

    [Fact]
    public void DuplicateOrInvalidGpuIdentityIsRejectedBeforeNativeMutation()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;

        Assert.Throws<InvalidOperationException>(() =>
            NativeMetricSnapshotCatalogProjection.Create(
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
            NativeMetricSnapshotCatalogProjection.Create(
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
            NativeMetricSnapshotCatalogProjection.Create(
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

        var projection = NativeMetricSnapshotCatalogProjection.Create(
            plan,
            [],
            [],
            fans,
            NativeMetricSnapshotCatalogHandleMap.Empty);

        Assert.Contains(
            projection.MetricIds,
            static pair => pair.Value == "fan.20.speed");
        Assert.Contains(
            projection.MetricIds,
            static pair => pair.Value == "fan.30.speed");
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
            NativeMetricSnapshotCatalogProjection.Create(
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
        var first = NativeMetricSnapshotCatalogProjection.Create(
            plan,
            firstTopology,
            [],
            [],
            NativeMetricSnapshotCatalogHandleMap.Empty);
        var firstAUsage = first.MetricIds.Single(
            static pair => pair.Value == "gpu.0.usage").Key;
        var firstBUsage = first.MetricIds.Single(
            static pair => pair.Value == "gpu.1.usage").Key;

        NativeMetricSnapshotGpuCatalogIdentity[] reordered =
        [
            new(0, "GPU B", "gpu-b", 0xB2UL, 2, VendorAmd, false),
            new(1, "GPU A", "gpu-a", 0xA2UL, 2, VendorNvidia, true)
        ];
        var second = NativeMetricSnapshotCatalogProjection.Create(
            plan,
            reordered,
            [],
            [],
            first.HandleMap);

        Assert.Equal(
            firstAUsage,
            second.MetricIds.Single(
                static pair => pair.Value == "gpu.1.usage").Key);
        Assert.Equal(
            firstBUsage,
            second.MetricIds.Single(
                static pair => pair.Value == "gpu.0.usage").Key);
        Assert.NotEqual(
            first.CatalogIdentitySha256,
            second.CatalogIdentitySha256);

        NativeMetricSnapshotGpuCatalogIdentity[] inserted =
        [
            new(0, "GPU B", "gpu-b", 0xB3UL, 3, VendorAmd, false),
            new(1, "GPU A", "gpu-a", 0xA3UL, 3, VendorNvidia, true),
            new(2, "GPU C", "gpu-c", 0xC3UL, 3, VendorAmd, false)
        ];
        var third = NativeMetricSnapshotCatalogProjection.Create(
            plan,
            inserted,
            [],
            [],
            second.HandleMap);
        Assert.Equal(
            firstAUsage,
            third.MetricIds.Single(
                static pair => pair.Value == "gpu.1.usage").Key);
        Assert.Equal(
            firstBUsage,
            third.MetricIds.Single(
                static pair => pair.Value == "gpu.0.usage").Key);
    }

    private static IReadOnlyList<NativeMetricSnapshotMetricDefinitionInput>
        RulesForMetric(
            NativeMetricSnapshotCatalogProjection projection,
            string metricId)
    {
        var handle = projection.MetricIds.Single(
            pair => pair.Value == metricId).Key;
        return projection.Rules
            .Where(row => row.MetricHandle == handle)
            .ToArray();
    }

    private const uint VendorNvidia = 1;
    private const uint VendorAmd = 2;
}
