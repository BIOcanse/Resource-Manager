using System.Diagnostics;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace Resource_Manager_APP.Tests;

public sealed partial class FanCurveReceiptTests
{
    [EnvironmentVariableFact("RESOURCE_MANAGER_FAN_CORE_CONSUMER_TEST", "1")]
    public async Task CompiledCoreRestorationExceptionKeepsRetryAndOtherFansRunning()
    {
        await using var helper = new CoreChild(restoreFault: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = helper.Client;
        Assert.Equal(2, client.Describe().Count);
        var writer = new FanControlWriter(client);
        var capability = new ControlCapability("fan.curve", "Curve", ControlValueKinds.Curve, true);
        var setting = new ControlSetting("fan.curve", Curve: [new(60, 40), new(95, 100)],
            CurveExecution: ControlFanCurveExecutions.Software);
        await client.PushTemperaturesAsync(new Dictionary<string, FanTemperatureSample>
            { ["cpu"] = new(60, DateTime.UtcNow.Ticks) }, deadline.Token);
        var other = Target with { Id = "fan:oem1" };
        Assert.Equal(ControlApplyStatuses.Applied, (await writer.WriteAsync(Target, capability, setting, deadline.Token)).Status);
        Assert.Equal(ControlApplyStatuses.Applied, (await writer.WriteAsync(other, capability, setting, deadline.Token)).Status);
        var retry = Stopwatch.StartNew();
        while (true)
        {
            var read = (await client.SendAsync("read", null, deadline.Token))!.Value.GetProperty("fans");
            if (read[0].GetProperty("automaticControl").GetBoolean()
                && !read[1].GetProperty("automaticControl").GetBoolean()) break;
            Assert.True(retry.Elapsed < TimeSpan.FromSeconds(3), "The release exception stopped the loop instead of retrying and continuing the other fan.");
            await client.PushTemperaturesAsync(new Dictionary<string, FanTemperatureSample>
                { ["cpu"] = new(60, DateTime.UtcNow.Ticks) }, deadline.Token);
            await Task.Delay(100, deadline.Token);
        }
        Assert.Null(await client.ReadCurveAsync(0, deadline.Token));
        Assert.Equal(setting.Curve, await client.ReadCurveAsync(1, deadline.Token));
        await client.PushTemperaturesAsync(new Dictionary<string, FanTemperatureSample>
            { ["cpu"] = new(100, DateTime.UtcNow.Ticks) }, deadline.Token);
        var nextWrite = Stopwatch.StartNew();
        while (true)
        {
            var read = (await client.SendAsync("read", null, deadline.Token))!.Value.GetProperty("fans");
            if (read[1].GetProperty("dutyPercent").GetDouble() == 100) break;
            Assert.True(nextWrite.Elapsed < TimeSpan.FromSeconds(2), "The other fan stopped receiving new temperature-driven writes.");
            await Task.Delay(100, deadline.Token);
        }
        Assert.True(await writer.RestoreAsync(other, capability, new HashSet<string>(), deadline.Token, setting));
    }

    [EnvironmentVariableFact("RESOURCE_MANAGER_FAN_CORE_CONSUMER_TEST", "1")]
    public Task CompiledCoreRestoreAllRetainsAFailedFanAndContinuesTheOtherFan()
        => RunRestorationProbeAsync("--restore-all-check");

    [EnvironmentVariableFact("RESOURCE_MANAGER_FAN_CORE_CONSUMER_TEST", "1")]
    public Task CompiledEngineStopAllRetainsAFailedFanAndContinuesTheOtherFan()
        => RunRestorationProbeAsync("--engine-stop-all-check");

    private static async Task RunRestorationProbeAsync(string mode)
    {
        var path = Environment.GetEnvironmentVariable("RM_FAN_CORE_PROTOCOL_PROBE");
        Assert.True(File.Exists(path), "Build the owned in-memory core fixture first.");
        var start = new ProcessStartInfo(path!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(mode);
        start.Environment["RM_FAN_CORE_PROBE_FAIL_RESTORE"] = "0";
        start.Environment["RM_FAN_CORE_PROBE_DEFER_OPEN"] = "0";
        using var probe = Process.Start(start)!;
        var error = probe.StandardError.ReadToEndAsync();
        await probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(probe.ExitCode == 0, $"Owned restoration probe exited {probe.ExitCode}: {await error}");
    }

    [EnvironmentVariableFact("RESOURCE_MANAGER_FAN_CORE_CONSUMER_TEST", "1")]
    public Task CompiledCoreRedetectionRetriesFailedDiscoveryWithoutRestartingTheHelper()
        => RunRedetectionAsync(1);

    [EnvironmentVariableFact("RESOURCE_MANAGER_FAN_CORE_CONSUMER_TEST", "1")]
    public Task CompiledCoreRepeatedFailedRedetectionUpdatesItsReasonBeforeLaterRecovery()
        => RunRedetectionAsync(2);

    private static async Task RunRedetectionAsync(int deferredOpens)
    {
        await using var helper = new CoreChild(deferredOpens);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = helper.Client;
        Assert.Empty(client.Describe());
        var initialReason = client.UnavailableReason;
        Assert.NotNull(initialReason);
        Assert.False(client.NeedsTemperatures());
        var processId = helper.ProcessId;
        var unchanged = await client.SendAsync("describe", null, deadline.Token);
        Assert.False(unchanged!.Value.GetProperty("available").GetBoolean());
        Assert.Empty(unchanged.Value.GetProperty("fans").EnumerateArray());

        if (deferredOpens == 2)
        {
            client.ResetDetection();
            Assert.Empty(client.Describe());
            Assert.NotNull(client.UnavailableReason);
            Assert.NotEqual(initialReason, client.UnavailableReason);
            Assert.False(client.NeedsTemperatures());
            Assert.Equal(processId, helper.ProcessId);
        }
        client.ResetDetection();
        Assert.True(Assert.Single(client.Describe()).SupportsSoftwareCurve);
        Assert.Null(client.UnavailableReason);
        Assert.False(client.NeedsTemperatures());
        Assert.Equal(processId, helper.ProcessId);
        Assert.False(helper.HasExited);
        var refreshed = await client.SendAsync("describe", null, deadline.Token);
        Assert.True(refreshed!.Value.GetProperty("available").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, refreshed.Value.GetProperty("reason").ValueKind);
        Assert.True(Assert.Single(client.Describe()).SupportsSoftwareCurve);
        // The deferred fixture exits unsuccessfully if detection drove any hardware.
    }

    [EnvironmentVariableFact("RESOURCE_MANAGER_FAN_CORE_CONSUMER_TEST", "1")]
    public Task CompiledCoreReceiptsAndManualRestorationUseTheProductionProcessChannel()
        => RunCoreConsumerAsync("manual");

    [EnvironmentVariableFact("RESOURCE_MANAGER_FAN_CORE_CONSUMER_TEST", "1")]
    public Task CompiledCoreLosesTemperatureAndStopsTheSoftwarePlanWithoutReturningACachedCurve()
        => RunCoreConsumerAsync("temperature");

    [EnvironmentVariableFact("RESOURCE_MANAGER_FAN_CORE_CONSUMER_TEST", "1")]
    public Task CompiledCoreRestoresAnActiveSoftwareOverrideWhenTheHostClosesInput()
        => RunCoreConsumerAsync("eof");

    private static async Task RunCoreConsumerAsync(string finish)
    {
        await using var helper = new CoreChild();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var client = helper.Client;
        var fan = Assert.Single(client.Describe());
        Assert.True(fan.SupportsSoftwareCurve);
        Assert.Null(fan.FirmwareCurve);
        await client.PushTemperaturesAsync(new Dictionary<string, FanTemperatureSample>
        {
            ["cpu"] = new(60, DateTime.UtcNow.Ticks)
        }, deadline.Token);
        var setting = new ControlSetting("fan.curve", Curve: [new(60, 35), new(120, 100)],
            CurveExecution: ControlFanCurveExecutions.Software);
        var capability = new ControlCapability("fan.curve", "Curve", ControlValueKinds.Curve, true);
        var writer = new FanControlWriter(client);
        var outcome = await writer.WriteAsync(Target, capability, setting, deadline.Token);
        Assert.Equal(ControlApplyStatuses.Applied, outcome.Status);
        Assert.Equal(setting.Curve, await client.ReadCurveAsync(0, deadline.Token));
        // Accepting the plan precedes the next actual engine tick. Keep the normal
        // host feed active until the injected backend confirms its first drive.
        var firstDrive = Stopwatch.StartNew();
        while (true)
        {
            var manual = await client.SendAsync("read", null, deadline.Token);
            if (!manual!.Value.GetProperty("fans")[0].GetProperty("automaticControl").GetBoolean()) break;
            Assert.True(firstDrive.Elapsed < TimeSpan.FromSeconds(5), "The actual engine never drove the injected backend.");
            await client.PushTemperaturesAsync(new Dictionary<string, FanTemperatureSample>
            {
                ["cpu"] = new(60, DateTime.UtcNow.Ticks)
            }, deadline.Token);
            await Task.Delay(100, deadline.Token);
        }
        var sameOwner = helper.ProcessId;
        client.ResetDetection();
        Assert.True(Assert.Single(client.Describe()).SupportsSoftwareCurve);
        Assert.Equal(sameOwner, helper.ProcessId);
        Assert.Equal(setting.Curve, await client.ReadCurveAsync(0, deadline.Token));
        var stillActive = await client.SendAsync("read", null, deadline.Token);
        Assert.False(stillActive!.Value.GetProperty("fans")[0].GetProperty("automaticControl").GetBoolean());
        if (finish == "eof")
        {
            Assert.False(helper.HasExited);
            return; // CoreChild closes stdin; exit 0 requires core cleanup to restore this active override.
        }

        if (finish == "temperature")
        {
            // Keep the actual default freshness and blind timeout. No new samples.
            var timer = Stopwatch.StartNew();
            while (true)
            {
                var reading = await client.SendAsync("read", null, deadline.Token);
                if (reading!.Value.GetProperty("fans")[0].GetProperty("automaticControl").GetBoolean()) break;
                Assert.True(timer.Elapsed < TimeSpan.FromSeconds(14), "The core did not hand back after losing temperature.");
                await Task.Delay(100, deadline.Token);
            }
        }
        else
        {
            Assert.True(await writer.RestoreAsync(Target, capability, new HashSet<string>(), deadline.Token, setting));
        }
        Assert.Null(await client.ReadCurveAsync(0, deadline.Token));
        var after = await client.SendAsync("read", null, deadline.Token);
        Assert.True(after!.Value.GetProperty("fans")[0].GetProperty("automaticControl").GetBoolean());
        Assert.False(helper.HasExited);
    }

    private sealed class CoreChild : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "rm-fan-core-consumer-" + Guid.NewGuid().ToString("N"));
        private readonly string helperDirectory;
        private Process? observer;
        internal FanControlCoreClient Client { get; }
        internal bool HasExited => observer!.HasExited;
        internal int ProcessId => observer!.Id;
        internal CoreChild(int deferredOpens = 0, bool restoreFault = false)
        {
            var probePath = Environment.GetEnvironmentVariable("RM_FAN_CORE_PROTOCOL_PROBE");
            Assert.False(string.IsNullOrWhiteSpace(probePath), "Build the owned in-memory core fixture first.");
            Assert.True(File.Exists(probePath), "Missing owned core fixture.");
            helperDirectory = Directory.CreateDirectory(Path.Combine(root, "Dependencies", "fan-control-core")).FullName;
            File.WriteAllText(Path.Combine(helperDirectory, "FanControlCore.exe"), "factory placeholder");
            Process? Start(ProcessStartInfo original)
            {
                original.FileName = probePath!;
                original.Environment["RM_FAN_CORE_PROBE_DEFER_OPEN"] = deferredOpens.ToString(System.Globalization.CultureInfo.InvariantCulture);
                original.Environment["RM_FAN_CORE_PROBE_FAIL_RESTORE"] = restoreFault ? "1" : "0";
                var child = Process.Start(original)!;
                observer = Process.GetProcessById(child.Id);
                _ = observer.SafeHandle;
                return child;
            }
            Client = new FanControlCoreClient(new EnvironmentInfo(Path.Combine(root, "ResourceManager-APP")), Start);
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            if (observer is not null)
            {
                try
                {
                    await observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Equal(0, observer.ExitCode); // EOF restored the fake backend and exited normally.
                }
                finally { observer.Dispose(); }
            }
            File.Delete(Path.Combine(helperDirectory, "FanControlCore.exe"));
            Directory.Delete(helperDirectory);
            Directory.Delete(Path.Combine(root, "Dependencies"));
            Directory.Delete(root);
        }
    }
}
