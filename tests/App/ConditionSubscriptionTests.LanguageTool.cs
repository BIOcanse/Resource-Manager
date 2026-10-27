using System.Diagnostics;
using System.Text.Json;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

public sealed partial class ConditionSubscriptionTests
{
    [Theory]
    [InlineData("pwsh", "condition", "met")]
    [InlineData("pwsh", "condition", "invalid")]
    [InlineData("pwsh", "condition", "credential")]
    [InlineData("pwsh", "json", "met")]
    [InlineData("pwsh", "json", "invalid")]
    [InlineData("pwsh", "json", "credential")]
    [InlineData("powershell.exe", "condition", "met")]
    [InlineData("powershell.exe", "condition", "invalid")]
    [InlineData("powershell.exe", "condition", "credential")]
    [InlineData("powershell.exe", "json", "met")]
    [InlineData("powershell.exe", "json", "invalid")]
    [InlineData("powershell.exe", "json", "credential")]
    public async Task WaitToolUsesWaitForTextAndChannelForUnmodifiedHookJson(string shell, string form, string outcome)
    {
        var metrics = new MetricSource();
        await using var host = await Host.StartAsync(metrics, realClock: true);
        var start = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-File", FindWaitTool(), "-BaseUri", host.Client.BaseAddress!.AbsoluteUri,
            "-AccessKey", outcome == "credential" ? "invalid-key" : host.Client.DefaultRequestHeaders.GetValues(TrustedAccessHeaders.KeyHeader).Single() })
            start.ArgumentList.Add(argument);
        if (form == "condition")
        {
            start.ArgumentList.Add("-Condition");
            start.ArgumentList.Add(outcome == "invalid" ? "memory.free < 1GB for 1s" : "memory.free < 1GiB for 0.1s every 100ms");
        }
        else
        {
            var rule = Rule(); rule["holdSeconds"] = .1;
            if (outcome == "invalid") rule["metricId"] = "unknown";
            start.ArgumentList.Add("-HookJson"); start.ArgumentList.Add(JsonSerializer.Serialize(Hook(rule, "json-hook")));
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        try
        {
            if (outcome == "met")
            {
                await metrics.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await metrics.PublishAsync(Memory(Epoch, 950)); await Task.Delay(150); await metrics.PublishAsync(Memory(Epoch, 950));
            }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var stdout = await output; var stderr = await errors;
            if (outcome == "met")
            {
                Assert.Equal(0, process.ExitCode); Assert.Empty(stderr);
                using var frame = JsonDocument.Parse(Assert.Single(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)));
                Assert.Equal("met", frame.RootElement.GetProperty("value").GetProperty("state").GetString());
                if (form == "condition") Assert.Equal(100, frame.RootElement.GetProperty("hook").GetProperty("intervalMs").GetInt32());
                else Assert.Equal("json-hook", frame.RootElement.GetProperty("hookId").GetString());
                await UntilAsync(() => metrics.Active == 0);
            }
            else { Assert.NotEqual(0, process.ExitCode); Assert.Empty(stdout); Assert.NotEmpty(stderr); }
        }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
    }
}
