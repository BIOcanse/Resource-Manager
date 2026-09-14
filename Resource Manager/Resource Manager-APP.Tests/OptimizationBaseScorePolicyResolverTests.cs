using ResourceManager.App.Application.Optimization.Scoring;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Software;

namespace Resource_Manager_APP.Tests;

public sealed class OptimizationBaseScorePolicyResolverTests
{
    [Fact]
    public void ResolveBaseScore_UsesSoftwareOverrideAsProcessDefault()
    {
        var resolver = new OptimizationBaseScorePolicyResolver(Document(
            softwarePolicies:
            [
                SoftwarePolicy("software-a", 72, processOverrideAllowed: true)
            ]));

        var score = resolver.ResolveBaseScore(
            "software-a",
            SoftwareKinds.Managed,
            "helper",
            null);

        Assert.Equal(72, score);
    }

    [Fact]
    public void ResolveBaseScore_UsesProcessOverrideBeforeSoftwareDefault()
    {
        const string path = @"C:\Apps\Game\helper.exe";
        var resolver = new OptimizationBaseScorePolicyResolver(Document(
            softwarePolicies:
            [
                SoftwarePolicy("software-a", 72, processOverrideAllowed: true)
            ],
            processPolicies:
            [
                ProcessPolicy(
                    "software-a",
                    OptimizationBaseScorePolicyResolver.CreateProcessKey("helper", path),
                    38,
                    inherit: false)
            ]));

        var score = resolver.ResolveBaseScore(
            "software-a",
            SoftwareKinds.Managed,
            "helper",
            path);

        Assert.Equal(38, score);
    }

    [Fact]
    public void ResolveBaseScore_IgnoresProcessOverrideWhenProcessInherits()
    {
        var resolver = new OptimizationBaseScorePolicyResolver(Document(
            softwarePolicies:
            [
                SoftwarePolicy("software-a", 72, processOverrideAllowed: true)
            ],
            processPolicies:
            [
                ProcessPolicy(
                    "software-a",
                    OptimizationBaseScorePolicyResolver.CreateProcessKey("helper", null),
                    38,
                    inherit: true)
            ]));

        var score = resolver.ResolveBaseScore(
            "software-a",
            SoftwareKinds.Managed,
            "helper",
            null);

        Assert.Equal(72, score);
    }

    [Fact]
    public void ResolveBaseScore_PairsExactGameAndOtherDefaultsWithoutOverrides()
    {
        var resolver = new OptimizationBaseScorePolicyResolver(GpuPlacementPolicyDocument.Empty);

        Assert.Equal(95, resolver.ResolveBaseScore(
            "software-a", SoftwareKinds.Game, "same-process", null));
        Assert.Equal(35, resolver.ResolveBaseScore(
            "software-a", SoftwareKinds.Other, "same-process", null));
    }

    private static GpuPlacementPolicyDocument Document(
        IReadOnlyList<GpuPlacementSoftwarePolicy>? softwarePolicies = null,
        IReadOnlyList<GpuPlacementProcessPolicy>? processPolicies = null)
    {
        return new GpuPlacementPolicyDocument(
            GpuPlacementPolicyDocumentVersions.Current,
            softwarePolicies ?? [],
            processPolicies ?? [],
            DateTimeOffset.UnixEpoch);
    }

    private static GpuPlacementSoftwarePolicy SoftwarePolicy(
        string softwareId,
        double? baseScoreOverride,
        bool processOverrideAllowed)
    {
        return GpuPlacementPolicyDefaults.CreateSoftwarePolicy(softwareId, softwareId) with
        {
            BaseScoreOverride = baseScoreOverride,
            ProcessOverrideAllowed = processOverrideAllowed
        };
    }

    private static GpuPlacementProcessPolicy ProcessPolicy(
        string softwareId,
        string processKey,
        double? baseScoreOverride,
        bool inherit)
    {
        return new GpuPlacementProcessPolicy(
            softwareId,
            processKey,
            "helper",
            null,
            inherit,
            GpuPlacementPolicyModes.Inherit,
            GpuPlacementRiskLevels.Low,
            GpuPlacementProviderIds.Defaults,
            GpuPlacementTargets.AutoIdleGpu,
            GpuPlacementExplicitSelectionModes.DefaultSkip,
            baseScoreOverride,
            DateTimeOffset.UnixEpoch);
    }
}
