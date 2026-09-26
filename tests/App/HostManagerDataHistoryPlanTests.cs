using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerDataHistoryPlanTests
{
    [Theory]
    [InlineData("host_recreate", "smart_coordinator", "maximum_samples_per_process")]
    [InlineData("host_recreate", "smart_coordinator", "maximum_samples_per_gpu")]
    [InlineData("build_specialize", "capacity_limits", "smart_coordinator_process_sample_capacity")]
    [InlineData("build_specialize", "capacity_limits", "smart_coordinator_gpu_sample_capacity")]
    public void RetiredManualHistoryCapacitiesAreNotSilentlyAccepted(string section, string group, string field)
    {
        var error = Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
            root[section]![group]![field] = 16));
        Assert.Contains(field, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SmartCoordinatorDoesNotAllocateHiddenDataHistory()
    {
        var baseline = HostManagerTestPlanFactory.CreatePlan();
        var smaller = HostManagerTestPlanFactory.CreatePlan(root =>
            root["host_recreate"]!["smart_coordinator"]!["maximum_processes"] = 32);
        Assert.NotEqual(baseline.HostRecreate.SmartCoordinator.MaximumProcesses, smaller.HostRecreate.SmartCoordinator.MaximumProcesses);
        Assert.Empty(baseline.DataHistory.Requirements);
        Assert.Equal<HistoryRequirement>(baseline.DataHistory.Requirements, smaller.DataHistory.Requirements);
    }

    [Fact]
    public void SmartCoordinatorFeaturesDoNotCreateHistoryConsumers()
    {
        var allFeatures = HostManagerTestPlanFactory.CreatePlan();
        var processOnly = HostManagerTestPlanFactory.CreatePlan(root =>
            root["hot_publish"]!["smart_coordinator"]!["feature_flags"] =
                new System.Text.Json.Nodes.JsonArray("process_policy"));
        var adaptersOnly = HostManagerTestPlanFactory.CreatePlan(root =>
            root["hot_publish"]!["smart_coordinator"]!["feature_flags"] =
                new System.Text.Json.Nodes.JsonArray("adapter_cpu", "adapter_gpu"));

        Assert.Empty(allFeatures.DataHistory.Requirements);
        Assert.Empty(processOnly.DataHistory.Requirements);
        Assert.Empty(adaptersOnly.DataHistory.Requirements);
    }

    [Fact]
    public void TimingAndSubscriptionChangesDoNotCreateCoordinatorHistory()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["hot_publish"]!["smart_coordinator"]!["required_consecutive_decisions"] = 4;
            root["hot_publish"]!["sampling_subscription"]!["roles"]![0]!["minimum_interval_ms"] = 250;
        });
        var smart = plan.SmartCoordinator;
        var configuration = NativeSmartCoordinatorConfigurationWriter.Create(
            smart.Build, smart.Recreate, smart.HotPublish, smart.ConfigurationGeneration);
        Assert.Empty(plan.DataHistory.Requirements);
        Assert.Equal(4u, configuration.RequiredConsecutiveDecisions);
        Assert.NotEqual(HostManagerTestPlanFactory.SmartCoordinator.ConfigurationSha256, smart.ConfigurationSha256);
        using var session = new NativeSmartCoordinatorSession(in configuration);
        var larger = configuration;
        larger.Generation++;
        larger.RequiredConsecutiveDecisions++;
        Assert.Equal(NativeSmartCoordinatorStatus.Ok, session.Reconfigure(in larger));
    }

    [Theory]
    [InlineData("averaging_window_ms")]
    [InlineData("cpu_cutline_start_pressure")]
    [InlineData("cpu_cutline_full_pressure")]
    public void RetiredPressureFieldsAreRejected(string field)
    {
        var error = Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
            root["hot_publish"]!["smart_coordinator"]![field] = 1));
        Assert.Contains(field, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("cpu_adapter_policy", "free_multiplier_minimum")]
    [InlineData("cpu_adapter_policy", "free_multiplier_maximum")]
    [InlineData("gpu_adapter_policy", "free_multiplier_minimum")]
    [InlineData("gpu_adapter_policy", "free_multiplier_maximum")]
    public void RetiredAdapterPressureMultipliersAreRejected(string policy, string field)
    {
        var error = Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
            root["hot_publish"]!["smart_coordinator"]![policy]![field] = 1));
        Assert.Contains(field, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RetiredGlobalGpuPressureFallbackFeatureIsRejected()
    {
        var error = Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
            root["hot_publish"]!["smart_coordinator"]!["feature_flags"] =
                new System.Text.Json.Nodes.JsonArray(
                    "process_policy",
                    "adapter_cpu",
                    "adapter_gpu",
                    "critical_events",
                    "adapter_gpu_global_fallback")));
        Assert.Contains("adapter_gpu_global_fallback", error.Message, StringComparison.Ordinal);
    }
}
