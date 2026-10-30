using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class EtwCpuCoreResidencyLiveSmokeTests
{
    [EnvironmentVariableFact("RESOURCE_MANAGER_RUN_LIVE_ETW_TESTS", "1")]
    public async Task RealKernelSessionPublishesACompleteDurationWindowWhenExplicitlyEnabled()
    {
        Assert.True(OperatingSystem.IsWindows());
        Assert.True(TraceEventSession.IsElevated() == true, "Live kernel ETW smoke requires elevation.");
        var topologySampler = new WindowsCpuTopologyReader(new EmptyOverrideStore());
        var expectedTopology = topologySampler.CaptureTopology();
        using var sampling = new HostManagerSamplingSubscriptionTestFixture(
            HostManagerTestPlanFactory.CreatePlan(cpuTopology: expectedTopology));
        var plan = sampling.Provider.Current.HostManager;
        var configuration = KernelEtwNativeSettings.CreateConfiguration(
            plan.KernelEtwLimits ?? throw new InvalidOperationException("Native kernel limits are unpublished."),
            plan.CpuCoreResidency,
            checked((uint)plan.CpuCoreResidency.PhysicalCoreByLogicalProcessor.Count));
        var regions = new BoundedKernelEtwRegionProvider(checked((nuint)configuration.ByteCap));
        var factory = new NativeKernelEtwSessionRuntimeFactory(regions, () => configuration);
        using var broker = new KernelEtwSessionBroker(factory, NullLogger<KernelEtwSessionBroker>.Instance);
        using var reader = new EtwCpuCoreResidencyReader(
            broker,
            sampling.Provider,
            sampling.Owner,
            NullLogger<EtwCpuCoreResidencyReader>.Instance);
        await reader.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var values = reader.SubscribeAsync(
                    "test.live-etw",
                    TimeSpan.FromMilliseconds(250),
                    timeout.Token)
                .GetAsyncEnumerator();
            CpuCoreResidencySnapshot? complete = null;
            while (await values.MoveNextAsync())
            {
                if (values.Current is CpuCoreResidencySnapshot snapshot)
                {
                    complete = snapshot;
                    break;
                }
            }

            Assert.NotNull(complete);
            Assert.True(complete.SessionGeneration > 0);
            Assert.True(complete.MeasuredThrough > complete.MeasuredFrom);
            Assert.NotEmpty(complete.Processes);
            Assert.True(complete.Processes.Sum(static process => process.ExecutionTimeMilliseconds) > 0);
            var expectedLogicalProcessorIds = expectedTopology.LogicalProcessors
                .Select(static logical => logical.Id)
                .ToHashSet();
            Assert.All(
                complete.Processes,
                process =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(process.ProcessInstanceId));
                    Assert.All(
                        process.LogicalProcessors,
                        logical => Assert.Contains(logical.LogicalProcessorId, expectedLogicalProcessorIds));
                });
            var brokerSnapshot = broker.GetSnapshot();
            Assert.True(brokerSnapshot.IsRunning);
            Assert.Equal(0, brokerSnapshot.EventsLost);
        }
        finally
        {
            await reader.StopAsync(CancellationToken.None);
            broker.Dispose();
        }
        Assert.Equal(0UL, regions.GetUsage().CurrentBytes);
    }

    private sealed class EmptyOverrideStore : ICpuCorePerformanceOverrideStore
    {
        public CpuPerformanceOverrides LoadConfiguration(string cpuName) => new(LoadScores(cpuName), null);

        public void SaveBaselineRatio(double ratio) => throw new NotSupportedException();

        public void ResetBaselineRatio() => throw new NotSupportedException();

        public IReadOnlyDictionary<int, double> LoadScores(string cpuName)
            => new Dictionary<int, double>();

        public CpuCorePerformanceOverrideResult Save(CpuCorePerformanceOverrideRequest request)
            => throw new NotSupportedException();

        public CpuCorePerformanceOverrideResult Reset(string cpuName)
            => throw new NotSupportedException();
    }
}
