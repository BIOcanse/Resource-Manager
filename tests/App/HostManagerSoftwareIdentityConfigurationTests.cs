using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

[Collection(SoftwareIdentityCatalogProcessStateCollection.Name)]
public sealed class HostManagerSoftwareIdentityConfigurationTests
{
    [Fact]
    public void DefaultProfilePublishesExactCatalogAndResolutionPlans()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan();

        Assert.Equal(59UL << 32, plan.SoftwareIdentityCatalog.ConfigurationGeneration);
        Assert.Equal(59UL << 32, plan.SoftwareIdentityResolution.ConfigurationGeneration);
        Assert.Equal("software_identity_catalog", plan.SoftwareIdentityCatalog.Build.NativeModule);
        Assert.Equal(
            "software_identity_resolution",
            plan.SoftwareIdentityResolution.Build.NativeModule);
        Assert.Equal(8192, plan.SoftwareIdentityCatalog.Recreate.Capacity.MaximumEntryCount);
        Assert.Equal(32768, plan.SoftwareIdentityCatalog.Recreate.Capacity.MaximumAliasCount);
        Assert.Equal(29, plan.SoftwareIdentityCatalog.Recreate.ProhibitedExecutableAliases.Length);
        Assert.Equal<string>(
            ["hyp", "launcher", "启动器"],
            plan.SoftwareIdentityCatalog.Recreate.LauncherTokens);
        Assert.Equal<string>(
            ["games"],
            plan.SoftwareIdentityCatalog.Recreate.ManagedChildSegments);
        Assert.Equal(120, plan.SoftwareIdentityCatalog.HotPublish.ExactTextScore);
        Assert.Equal(90, plan.SoftwareIdentityCatalog.HotPublish.IdentityMinimumScore);
        Assert.Equal(
            Enumerable.Range(1, 10).Select(static value => (uint)value),
            plan.SoftwareIdentityResolution.Recreate.SourceIds);
        Assert.Equal(
            Enumerable.Range(1, 10).Select(static value => (uint)(value * 10)),
            plan.SoftwareIdentityResolution.HotPublish.SourcePolicies
                .Select(static policy => policy.Priority));
        Assert.All(
            plan.SoftwareIdentityResolution.HotPublish.SourcePolicies,
            static policy => Assert.False(policy.StopOnUnavailable));
        Assert.NotEqual(
            plan.DeploymentDigests.SoftwareIdentityCatalog,
            plan.DeploymentDigests.SoftwareIdentityResolution);
    }

    [Fact]
    public void ProfileRejectsMissingExplicitCapacityRuleAndSourcePolicyFields()
    {
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["host_recreate"]!["software_identity_catalog"]!["capacity"]!
                .AsObject()
                .Remove("alias_index_capacity");
        }));
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["hot_publish"]!["software_identity_catalog"]!
                .AsObject()
                .Remove("root_reject_score");
        }));
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["hot_publish"]!["software_identity_resolution"]!["source_policies"]![0]!
                .AsObject()
                .Remove("stop_on_unavailable");
        }));
    }

    [Fact]
    public void ModuleDigestsChangeOnlyForTheEditedIdentityDomain()
    {
        var baseline = HostManagerTestPlanFactory.CreatePlan();
        var catalogChanged = HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["hot_publish"]!["software_identity_catalog"]!
                .AsObject()["exact_text_score"] = 121;
        });
        Assert.NotEqual(
            baseline.DeploymentDigests.SoftwareIdentityCatalog,
            catalogChanged.DeploymentDigests.SoftwareIdentityCatalog);
        Assert.Equal(
            baseline.DeploymentDigests.SoftwareIdentityResolution,
            catalogChanged.DeploymentDigests.SoftwareIdentityResolution);

        var resolutionChanged = HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["hot_publish"]!["software_identity_resolution"]!["source_policies"]![0]!
                .AsObject()["priority"] = 11;
        });
        Assert.Equal(
            baseline.DeploymentDigests.SoftwareIdentityCatalog,
            resolutionChanged.DeploymentDigests.SoftwareIdentityCatalog);
        Assert.NotEqual(
            baseline.DeploymentDigests.SoftwareIdentityResolution,
            resolutionChanged.DeploymentDigests.SoftwareIdentityResolution);
    }

    [Fact]
    public async Task OwnerSettlesBothModulesAndRejectsSameGenerationDrift()
    {
        var initial = HostManagerTestPlanFactory.CreatePlan();
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CreateRuntimePlan(1, initial));
        using var owner = new HostManagerSoftwareIdentityOwner(
            provider,
            new HostManagerSoftwareIdentityRuntime(provider, deployment),
            NullLogger<HostManagerSoftwareIdentityOwner>.Instance);
        await owner.StartAsync(CancellationToken.None);

        Assert.Equal(
            HostManagerDeploymentStatus.InSync,
            deployment.Snapshot.SoftwareIdentityCatalog.Status);
        Assert.Equal(
            HostManagerDeploymentStatus.InSync,
            deployment.Snapshot.SoftwareIdentityResolution.Status);
        using var initialLease = owner.Acquire();
        var initialCatalogDigest = initialLease.CatalogPlan.ConfigurationSha256;
        initialLease.Dispose();

        var sameGenerationDrift = HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["hot_publish"]!["software_identity_catalog"]!
                .AsObject()["exact_text_score"] = 121;
        });
        provider.Publish(CreateRuntimePlan(2, sameGenerationDrift));
        using var retained = owner.Acquire();
        Assert.Equal(initialCatalogDigest, retained.CatalogPlan.ConfigurationSha256);
        retained.Dispose();

        var nextRevision = HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["profile_revision"] = root["profile_revision"]!.GetValue<int>() + 1;
            root["hot_publish"]!["software_identity_catalog"]!
                .AsObject()["exact_text_score"] = 121;
        });
        provider.Publish(CreateRuntimePlan(3, nextRevision));
        using var replaced = owner.Acquire();
        Assert.Equal(
            nextRevision.SoftwareIdentityCatalog.ConfigurationSha256,
            replaced.CatalogPlan.ConfigurationSha256);
        Assert.Equal(
            nextRevision.SoftwareIdentityResolution.ConfigurationSha256,
            replaced.ResolutionPlan.ConfigurationSha256);
    }

    private static CompiledRuntimePlan CreateRuntimePlan(
        long version,
        CompiledHostManagerPlan hostManager)
        => CompiledRuntimePlan.Default with
        {
            Version = version,
            CompiledAt = DateTimeOffset.UtcNow,
            Reason = "software-identity-configuration-test",
            HostManager = hostManager
        };
}
