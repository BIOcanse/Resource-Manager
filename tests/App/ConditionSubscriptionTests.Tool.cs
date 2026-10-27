using System.Diagnostics;
using System.Text.Json;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

public sealed partial class ConditionSubscriptionTests
{
    [Theory]
    [InlineData("pwsh", "met")]
    [InlineData("pwsh", "rejected")]
    [InlineData("pwsh", "http-error")]
    [InlineData("powershell.exe", "met")]
    [InlineData("powershell.exe", "rejected")]
    [InlineData("powershell.exe", "http-error")]
    public async Task WaitToolRunsAgainstProductionHttpHost(string shell, string outcome)
    {
        var metrics = new MetricSource();
        await using var host = await Host.StartAsync(metrics, realClock: true);
        var script = FindWaitTool();
        var start = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-File", script, "-Binding", "machine", "-MetricId",
            outcome == "rejected" ? "unknown.metric" : "memory.usage", "-Quantity", "remaining", "-Comparison", "below",
            "-Threshold", "100", "-DurationSeconds", "0.1", "-IntervalMs", "100", "-BaseUri", host.Client.BaseAddress!.AbsoluteUri,
            "-AccessKey", outcome == "http-error" ? "invalid-key" : host.Client.DefaultRequestHeaders.GetValues(TrustedAccessHeaders.KeyHeader).Single() })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            if (outcome == "met")
            {
                await metrics.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.False(process.HasExited);
                metrics.Publish(Memory(Epoch, 950));
                await Task.Delay(200);
                metrics.Publish(Memory(Epoch.AddSeconds(1), 950));
            }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var output = await stdout;
            var error = await stderr;
            if (outcome == "met")
            {
                Assert.Equal(0, process.ExitCode);
                Assert.Equal(string.Empty, error);
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                using var frame = JsonDocument.Parse(Assert.Single(lines));
                Assert.Equal("met", frame.RootElement.GetProperty("value").GetProperty("state").GetString());
                await metrics.Released.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            else
            {
                Assert.NotEqual(0, process.ExitCode);
                Assert.Equal(string.Empty, output);
                Assert.Contains(outcome == "rejected" ? "rejected" : "HTTP", error);
            }
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
        }
    }

    private static string FindWaitTool()
    {
        var path = Path.Combine(TestRepositoryPaths.FindRoot(), "scripts", "tools", "Wait-RmCondition.ps1");
        if (File.Exists(path)) return path;
        throw new FileNotFoundException("Wait-RmCondition.ps1 is missing from the test worktree.", path);
    }
}
