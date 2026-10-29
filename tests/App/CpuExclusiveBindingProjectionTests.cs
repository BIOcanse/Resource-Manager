using ResourceManager.App.Application.Optimization.Scheduling;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Tests;

public sealed class CpuExclusiveBindingProjectionTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("tr-TR")]
    [InlineData("sv-SE")]
    public void CreateOrdersSoftwareNamesOrdinallyRegardlessOfCurrentCulture(string culture)
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
            var policies = new[]
            {
                CreatePolicy("software:z", "ä", DateTimeOffset.UnixEpoch, ["core:0"]),
                CreatePolicy("software:b", "a", DateTimeOffset.UnixEpoch, ["core:0"]),
                CreatePolicy("software:a", "A", DateTimeOffset.UnixEpoch, ["core:0"]),
                CreatePolicy("software:i", "I", DateTimeOffset.UnixEpoch, ["core:0"]),
                CreatePolicy("software:i-lower", "i", DateTimeOffset.UnixEpoch, ["core:0"])
            };
            var snapshot = CpuExclusiveBindingProjection.Create(CreateTopology(),
                new GpuPlacementPolicyDocument(GpuPlacementPolicyDocumentVersions.Current, policies, [], DateTimeOffset.UnixEpoch),
                DateTimeOffset.UnixEpoch);

            Assert.Equal(["software:a", "software:b", "software:i", "software:i-lower", "software:z"],
                snapshot.Bindings.Select(binding => binding.SoftwareId));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void CreateExpandsCcdAndMultiCoreBindingsWithoutChangingSoftwareScope()
    {
        var topology = CreateTopology();
        var policy = new GpuPlacementSoftwarePolicy(
            "software:one",
            "Example",
            GpuPlacementProviderIds.Defaults,
            GpuPlacementTargets.AutoIdleGpu,
            null,
            GpuPlacementRuntimeSwitchMethods.FutureFrameTakeover,
            null,
            true,
            DateTimeOffset.UnixEpoch,
            CpuSchedulingExcluded: false,
            MemorySchedulingExcluded: false,
            GpuSchedulingExcluded: false,
            CpuManualExclusivePositionIds: ["ccd:0", "core:2"],
            CpuManualLockedPositionIds: ["core:1", "core:3"]);

        var snapshot = CpuExclusiveBindingProjection.Create(
            topology,
            new GpuPlacementPolicyDocument(
                GpuPlacementPolicyDocumentVersions.Current,
                [policy],
                [],
                DateTimeOffset.UnixEpoch),
            DateTimeOffset.UnixEpoch);

        var binding = Assert.Single(snapshot.Bindings);
        Assert.Equal(["ccd:0", "core:2"], binding.RequestedExclusivePositionIds);
        Assert.Equal(["core:0", "core:1", "core:2"], binding.ExpandedExclusivePhysicalCoreIds);
        Assert.Equal(["core:1", "core:3"], binding.ExpandedLockedPhysicalCoreIds);
    }

    [Fact]
    public void CreateKeepsOnlyLatestPolicyAndOmitsPoliciesWithoutPositions()
    {
        var topology = CreateTopology();
        var oldPolicy = CreatePolicy("software:one", "Old", DateTimeOffset.UnixEpoch, ["core:0"]);
        var currentPolicy = CreatePolicy("software:one", "Current", DateTimeOffset.UnixEpoch.AddSeconds(1), ["core:3"]);
        var emptyPolicy = CreatePolicy("software:empty", "Empty", DateTimeOffset.UnixEpoch, []);

        var snapshot = CpuExclusiveBindingProjection.Create(
            topology,
            new GpuPlacementPolicyDocument(
                GpuPlacementPolicyDocumentVersions.Current,
                [oldPolicy, currentPolicy, emptyPolicy],
                [],
                DateTimeOffset.UnixEpoch),
            DateTimeOffset.UnixEpoch);

        var binding = Assert.Single(snapshot.Bindings);
        Assert.Equal("Current", binding.SoftwareName);
        Assert.Equal(["core:3"], binding.ExpandedExclusivePhysicalCoreIds);
    }

    private static GpuPlacementSoftwarePolicy CreatePolicy(
        string softwareId,
        string name,
        DateTimeOffset updatedAt,
        IReadOnlyList<string> positions) =>
        new(
            softwareId,
            name,
            GpuPlacementProviderIds.Defaults,
            GpuPlacementTargets.SystemDefaultGpu,
            null,
            GpuPlacementRuntimeSwitchMethods.FutureFrameTakeover,
            null,
            true,
            updatedAt,
            CpuSchedulingExcluded: false,
            MemorySchedulingExcluded: false,
            GpuSchedulingExcluded: false,
            CpuManualExclusivePositionIds: positions);

    private static CpuTopologySnapshot CreateTopology()
    {
        var cores = Enumerable.Range(0, 4)
            .Select(index => new CpuPhysicalCoreModel(
                $"core:{index}",
                index,
                index < 2 ? "ccd:0" : "ccd:1",
                0,
                100,
                null,
                [index],
                []))
            .ToArray();
        var logical = Enumerable.Range(0, 4)
            .Select(index => new CpuLogicalProcessorModel(
                index,
                0,
                index,
                $"core:{index}",
                index < 2 ? "ccd:0" : "ccd:1",
                100,
                null,
                true,
                null))
            .ToArray();
        return new CpuTopologySnapshot(
            DateTimeOffset.UnixEpoch,
            "Example CPU",
            new CpuSpecificationModel(
                "Example CPU",
                "Example",
                "Example",
                4,
                4,
                5000,
                4000,
                4096,
                32768,
                "test"),
            "test",
            "test",
            CpuTopologyAffinityTargetKinds.LogicalProcessorMask,
            CpuTopologyVisualLayoutKinds.CcdGrid,
            "test",
            4,
            4,
            2,
            false,
            [
                new CpuCcdModel("ccd:0", 0, null, [0, 1], [0, 1], "test"),
                new CpuCcdModel("ccd:1", 1, null, [2, 3], [2, 3], "test")
            ],
            cores,
            logical,
            []);
    }
}
