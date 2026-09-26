using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerSamplingAndPortableConfigurationTests
{
    [Fact]
    public void DefaultProfilePublishesSevenExactSamplingRolesAndOnePortableRegistryModule()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan();

        Assert.Equal(42, plan.SchemaVersion);
        Assert.Equal(59, plan.ProfileRevision);
        Assert.Equal(
            (59UL << 32) | NativeSamplingSubscriptionAbi.Version,
            plan.SamplingSubscription.ConfigurationGeneration);
        Assert.Equal(59UL << 32, plan.PortableSoftwareRegistry.ConfigurationGeneration);
        Assert.Equal(
            [1, 2, 3, 4, 5, 6, 7],
            plan.SamplingSubscription.Build.RoleCapacityLimits.Select(static role => role.RoleId));
        Assert.Equal(
            [1, 2, 3, 4, 5, 6, 7],
            plan.SamplingSubscription.Recreate.Roles.Select(static role => role.RoleId));
        Assert.Equal(
            [1, 2, 3, 4, 5, 6, 7],
            plan.SamplingSubscription.HotPublish.Roles.Select(static role => role.RoleId));
        Assert.All(plan.SamplingSubscription.HotPublish.Roles, static role =>
        {
            Assert.True(role.DefaultIntervalMilliseconds >= role.MinimumIntervalMilliseconds);
            Assert.True(role.ActiveTtlMilliseconds > role.DefaultIntervalMilliseconds);
        });
        Assert.Equal(
            [2000L, 2000L, 5000L, 2000L, 4000L, 2000L, 2000L],
            plan.SamplingSubscription.HotPublish.Roles
                .Select(static role => role.FreshnessGraceMilliseconds));
        Assert.Equal(
            "sampling_subscription",
            plan.SamplingSubscription.Build.NativeModule);
        Assert.Equal(
            "software_identity_portable_registry",
            plan.PortableSoftwareRegistry.Build.NativeModule);
        Assert.Equal(
            8192,
            plan.PortableSoftwareRegistry.Recreate.Capacity.RegistrationIndexCapacity);
        Assert.Equal(
            16384,
            plan.PortableSoftwareRegistry.Recreate.Capacity.PathIndexCapacity);
    }

    [Fact]
    public void SamplingProfileHasNoSelectableDispatchMode()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan(planEpoch: 7);

        Assert.Equal(
            (59UL << 32) | NativeSamplingSubscriptionAbi.Version,
            plan.SamplingSubscription.ConfigurationGeneration);
        Assert.All(plan.SamplingSubscription.HotPublish.Roles, static role =>
            Assert.True(role.IsPublished));
    }

    [Fact]
    public void SamplingProfileRejectsMissingRoleAndRetiredDispatchFields()
    {
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["hot_publish"]!["sampling_subscription"]!["roles"]!
                .AsArray()
                .RemoveAt(5);
        }));

        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
        {
            var role = root["hot_publish"]!["sampling_subscription"]!["roles"]![0]!
                .AsObject();
            role["smooth_realtime_enabled"] = false;
        }));

        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["hot_publish"]!["sampling_subscription"]!["roles"]![2]!
                .AsObject()["maximum_due_items_per_plan"] = 1;
        }));
    }

    [Fact]
    public void PortableProfileRejectsInvalidIndexCapacityAndMissingHotField()
    {
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["host_recreate"]!["portable_software_registry"]!["capacity"]!
                .AsObject()["path_index_capacity"] = 8193;
        }));

        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["hot_publish"]!["portable_software_registry"]!
                .AsObject()
                .Remove("resident_byte_budget");
        }));
    }

    [Fact]
    public void SamplingSemanticDriftDoesNotRewritePortableRegistryDigestAtSameGeneration()
    {
        var baseline = HostManagerTestPlanFactory.CreatePlan();
        var changed = HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["hot_publish"]!["sampling_subscription"]!["roles"]![0]!
                .AsObject()["freshness_grace_ms"] = 2001;
        });

        Assert.NotEqual(
            baseline.SamplingSubscription.ConfigurationSha256,
            changed.SamplingSubscription.ConfigurationSha256);
        Assert.NotEqual(
            baseline.DeploymentDigests.SamplingSubscription,
            changed.DeploymentDigests.SamplingSubscription);
        Assert.Equal(
            baseline.PortableSoftwareRegistry.ConfigurationSha256,
            changed.PortableSoftwareRegistry.ConfigurationSha256);
        Assert.Equal(
            baseline.DeploymentDigests.PortableSoftwareRegistry,
            changed.DeploymentDigests.PortableSoftwareRegistry);
    }

    [Fact]
    public void DeploymentTracksSamplingAndPortableRegistryAsIndependentExactAttempts()
    {
        var hostPlan = HostManagerTestPlanFactory.CreatePlan();
        var state = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(state);
        provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = 1,
            HostManager = hostPlan
        });
        var sampling = new HostManagerSamplingSubscriptionRuntime(provider, state);
        var portable = new HostManagerPortableSoftwareRegistryRuntime(provider, state);

        sampling.CompleteSucceeded(sampling.BeginInitialCreate(hostPlan));
        portable.CompleteSucceeded(portable.BeginInitialCreate(hostPlan));

        Assert.Equal(
            HostManagerDeploymentStatus.InSync,
            state.Snapshot.SamplingSubscription.Status);
        Assert.Equal(
            HostManagerDeploymentStatus.InSync,
            state.Snapshot.PortableSoftwareRegistry.Status);
        Assert.NotEqual(
            state.Snapshot.SamplingSubscription.LastSettledAttemptId,
            state.Snapshot.PortableSoftwareRegistry.LastSettledAttemptId);
    }
}
