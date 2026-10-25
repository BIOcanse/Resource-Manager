using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Windows.Metrics;
using ResourceManager.ReadWrite.Windows.Metrics.Capture;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;

namespace Resource_Manager_APP.Tests;

public sealed class NpuBusyAttemptTimeTests
{
    [Fact]
    public async Task MaximumKeepsItsObservationTimeAndTheCurrentAttemptTimeThroughPublication()
    {
        var recording = new NpuEngineRecordedResources();
        recording.EngineCounterSteps[0] = 500_000;
        recording.EngineCounterSteps[1] = 1_500_000;
        using var owner = new WindowsHardwareMetricOwner(WindowsMetricConfiguration.Default,
            recording.Create(), new(), unavailableCooldown: TimeSpan.Zero);
        using var http = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(http, []));
        var catalog = await core.DiscoverAsync();
        var fields = NpuEngineOwnerIntegrationTests.Fields(catalog)
            .AddRange(NpuEngineOwnerIntegrationTests.BusyFields(catalog));
        await core.CollectAsync(fields);
        await core.CollectAsync(fields);
        var captured = owner.ReadCachedSnapshot()!;
        var observed = captured.CapturedAt.AddSeconds(-1);
        var attempt = captured.CapturedAt;
        var snapshot = captured with
        {
            Datasets = captured.Datasets.ToDictionary(static pair => pair.Key, pair =>
                pair.Value with
                {
                    ObservedAtUtcTicks = observed.UtcTicks,
                    LastSuccessAtUtcTicks = observed.UtcTicks,
                    LastAttemptAtUtcTicks = attempt.UtcTicks
                })
        };
        var projected = WindowsHardwareMetricCapture.ProjectNpuDeviceBusyFields(snapshot, MetricSampleRequest.All);
        var binding = Assert.Single(projected.FieldBindings.Values,
            static row => row.Field.Code == MetricFieldCode.NpuDeviceEngineBusyPercent);
        Assert.Equal(150d, projected.Items[binding.RequestAddress].NumericValue);
        Assert.Equal(observed, projected.Items[binding.RequestAddress].ObservedAt);
        using var current = new LastSuccessfulHardwareMetricSnapshot();
        current.ConfigureHistory([new(binding.RequestAddress, 2)]);
        current.Publish(projected, MetricSampleRequest.ForIds([binding.RequestAddress]));
        var published = current.Read()!;
        foreach (var id in binding.RequestAliases.Prepend(binding.RequestAddress))
        {
            var observation = published.Datasets[id];
            Assert.Equal(observed.UtcTicks, observation.ObservedAtUtcTicks);
            Assert.Equal(observed.UtcTicks, observation.LastSuccessAtUtcTicks);
            Assert.Equal(attempt.UtcTicks, observation.LastAttemptAtUtcTicks);
        }
        Assert.Equal(attempt, Assert.Single(published.History[binding.RequestAddress]).ObservedAt);
    }
}
