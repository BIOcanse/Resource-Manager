using System.Diagnostics;
using System.Reflection;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

public sealed partial class ConditionSubscriptionTests
{
    [Fact]
    public async Task LiveProcessAndSoftwareHooksShareOriginalWorkerAndReleaseOnRealProcessExit()
    {
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-Command", "[Console]::In.ReadLine() | Out-Null" }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start)!;
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var catalog = new WindowsResourceBreakdownSamplerLifecycleTests.VersionedProcessAttributionCatalogProvider();
        using var sampler = new WindowsResourceBreakdownSampler(null!, catalog, new StaticResourceResidualBreakdownProvider(),
            null!, null!, subscriptions.Provider, subscriptions.Owner, new PdhProcessGpuReader(),
            new ChildInventory(child),
            new WindowsProcessBasicMetricReader());
        var tracker = (NativeItemSamplingSubscriptionTracker<ResourceBreakdownSampleRequest>)typeof(WindowsResourceBreakdownSampler)
            .GetField("subscriptions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sampler)!;
        await sampler.StartAsync(CancellationToken.None);
        try
        {
            await using var host = await Host.StartAsync(new MetricSource(), sampler, realClock: true);
            var rule = ObjectRule("process", child.Id, child.StartTime.ToUniversalTime().ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture));
            rule["metricId"] = "process_all_private_commit_actual"; rule["threshold"] = 0d; rule["holdSeconds"] = .2;
            await using var processStream = await host.OpenAsync([Hook(rule)]);
            var met = await ReadStateAsync(processStream, "met");
            Assert.True(met.GetProperty("reading").GetDouble() > 0);
            var current = sampler.ReadLatest(new SchedulingProcessFactRequest(SchedulingProcessMetricMask.None,
                new(SamplingObservationStatus.NotRequested, 0, 0, 0, 0, 0, 0, [])))!;
            var softwareId = Assert.Single(current.AttributionProcesses, row => row.ProcessId == child.Id).SoftwareId;
            var software = ObjectRule("software");
            software["softwareId"] = softwareId; software["metricId"] = "process_all_private_commit_actual";
            software["threshold"] = 0d; software["holdSeconds"] = .2; software["topOwners"] = 1;
            await using var softwareStream = await host.OpenAsync([Hook(software, "live-software")]);
            var softwareMet = await ReadStateAsync(softwareStream, "met");
            Assert.Equal(child.Id, Assert.Single(softwareMet.GetProperty("owners").EnumerateArray()).GetProperty("processId").GetInt32());
            await child.StandardInput.WriteLineAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("bound-process-ended", (await ReadStateAsync(processStream, "objectEnded")).GetProperty("reason").GetString());
            Assert.Equal("bound-software-ended", (await ReadStateAsync(softwareStream, "objectEnded")).GetProperty("reason").GetString());
            await UntilAsync(() => tracker.CaptureManagedSourceState() == (0, 0));
        }
        finally
        {
            if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
            await sampler.StopAsync(CancellationToken.None);
        }
    }

    private sealed class ChildInventory(Process child) : IWindowsProcessInventoryReader
    {
        public Process[] GetProcesses() => child.HasExited ? [] : [Process.GetProcessById(child.Id)];
    }

    private static async Task<System.Text.Json.JsonElement> ReadStateAsync(Stream stream, string state)
    {
        for (var count = 0; count < 8; count++)
        {
            var value = await stream.ReadAsync();
            if (value.GetProperty("state").GetString() == state) return value;
            Assert.Equal("unavailable", value.GetProperty("state").GetString());
        }
        throw new InvalidOperationException("The expected hook state was not delivered.");
    }
}
