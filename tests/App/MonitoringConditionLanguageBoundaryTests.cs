using System.Globalization;
using ResourceManager.App.Application.Monitoring;

namespace Resource_Manager_APP.Tests;

public sealed class MonitoringConditionLanguageBoundaryTests
{
    [Theory]
    [InlineData("  memory.free < 1KiB for 1ms every 1ms top 0  ", 1024d, .001d, 1)]
    [InlineData("memory.free < 0.5MiB for 0.1ms every 0.001s", 524288d, .0001d, 1)]
    public async Task SpacesAndListedUnitScalesHaveExactExpansion(string text, double threshold, double hold, int interval)
    {
        var hook = await ExpandAsync(text);
        Assert.Equal(threshold, hook.Rule.Threshold); Assert.Equal(hold, hook.Rule.HoldSeconds); Assert.Equal(interval, hook.IntervalMs);
    }

    [Theory]
    [InlineData("Memory.free < 1GiB for 1s", "unknown name")]
    [InlineData("memory.free == 1GiB for 1s", "unsupported comparison")]
    [InlineData("memory.free < 1gib for 1s", "unsupported unit")]
    [InlineData("memory.free < .5GiB for 1s", "decimal digits")]
    [InlineData("memory.free < 1GiB for", "ASCII space")]
    [InlineData("memory.free < 1GiB for ", "decimal digits")]
    [InlineData("memory.free < 1GiB for 0.00000001s", "hold time")]
    [InlineData("memory.free < 1GiB for 9999999999999s", "hold time")]
    [InlineData("memory.free < 1GiB for 1s every 2147483648ms", "frequency")]
    [InlineData("memory.free < 1GiB for 1s top 2147483648", "owner count")]
    [InlineData("memory.free < 1GiB for 1s every 1s every 1s", "repeated clause")]
    [InlineData("memory.free < 1GiB for 1s top 1 top 1", "repeated clause")]
    [InlineData("memory.free\t< 1GiB for 1s", "one line")]
    [InlineData("software abc private < 1B for 1s", "quoted identity")]
    [InlineData("software \"abc\\", "unterminated")]
    [InlineData("process 2147483648:1 private < 1B for 1s", "positive Int32")]
    [InlineData("process 1:18446744073709551616 private < 1B for 1s", "positive UInt64")]
    [InlineData("process 1:2 private < 1B for 1s top 1", "no top-owner")]
    [InlineData("device \"missing\" gpu.0.vram < 1B for 1s", "unknown name")]
    public async Task EveryErrorFamilyHasAnExplicitReason(string text, string reason)
    {
        var error = (await Assert.ThrowsAsync<MonitoringConditionParseException>(() => ExpandAsync(text)));
        Assert.Contains(reason, error.Reason); Assert.InRange(error.Position, 0, text.Length);
    }

    [Fact]
    public async Task NumericOverflowAndLengthAreBoundedAndNeverPartiallyAccepted()
    {
        var tooLong = new string(' ', 4097);
        Assert.Equal(4096, (await Assert.ThrowsAsync<MonitoringConditionParseException>(() => ExpandAsync(tooLong))).Position);
        var huge = "memory.free < " + new string('9', 310) + "B for 1s";
        Assert.Equal(14, (await Assert.ThrowsAsync<MonitoringConditionParseException>(() => ExpandAsync(huge))).Position);
        var converted = "memory.free < " + new string('9', 308) + "GiB for 1s";
        Assert.Contains("converted", (await Assert.ThrowsAsync<MonitoringConditionParseException>(() => ExpandAsync(converted))).Reason);
        var strict = "memory.free > " + double.MaxValue.ToString("F0", CultureInfo.InvariantCulture) + "B for 1s";
        Assert.Contains("strict threshold", (await Assert.ThrowsAsync<MonitoringConditionParseException>(() => ExpandAsync(strict))).Reason);
        var time = "memory.free < 1B for " + new string('9', 30) + "s";
        Assert.Contains("time exceeds", (await Assert.ThrowsAsync<MonitoringConditionParseException>(() => ExpandAsync(time))).Reason);
    }

    private static Task<ExpandedMonitoringHook> ExpandAsync(string text) => MonitoringConditionLanguage.ExpandAsync(text, "test", MonitoringConditionVocabulary.Create(null), _ => Task.FromResult<string?>(null));
}
