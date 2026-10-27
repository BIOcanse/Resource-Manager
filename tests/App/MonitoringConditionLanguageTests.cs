using ResourceManager.App.Application.Monitoring;

namespace Resource_Manager_APP.Tests;

public sealed class MonitoringConditionLanguageTests
{
    [Theory]
    [InlineData("memory.free < 1GiB for 60s", "machine", "memory.usage", "remaining", "below", 1073741824d, 60d, 1000, 0)]
    [InlineData("commit >= 95% for 60s top 8", "machine", "virtualMemory.usage", "percentOfTotal", "atOrAbove", 95d, 60d, 1000, 8)]
    [InlineData("memory.used >= 512MiB for 500ms every 2s top 3", "machine", "memory.usage", "value", "atOrAbove", 536870912d, .5d, 2000, 3)]
    [InlineData("cpu < 25% for 1s every 100ms", "machine", "cpu.usage", "value", "below", 25d, 1d, 100, 0)]
    [InlineData("cpu < 25% for 1s every 1.001s", "machine", "cpu.usage", "value", "below", 25d, 1d, 1001, 0)]
    public async Task GrammarExpandsMachineConditions(string text, string binding, string metric, string quantity, string comparison,
        double threshold, double hold, int interval, int owners)
    {
        var expanded = await MonitoringConditionLanguage.ExpandAsync(text, "test", MonitoringConditionVocabulary.Create(null), _ => Task.FromResult<string?>(null));
        Assert.Equal("test", expanded.Id); Assert.Equal(interval, expanded.IntervalMs);
        Assert.Equal(binding, expanded.Rule.Binding); Assert.Equal(metric, expanded.Rule.MetricId);
        Assert.Equal(quantity, expanded.Rule.Quantity); Assert.Equal(comparison, expanded.Rule.Comparison);
        Assert.Equal(threshold, expanded.Rule.Threshold); Assert.Equal(hold, expanded.Rule.HoldSeconds);
        Assert.Equal(owners, expanded.Rule.TopOwners);
    }

    [Fact]
    public async Task ProcessExampleCapturesThePublishedStartKeyAndUsesOnlyLowLevelComparison()
    {
        var result = await MonitoringConditionLanguage.ExpandAsync("process 1234 private > 4GiB for 30s every 2s", "process",
            MonitoringConditionVocabulary.Create(null), pid => Task.FromResult<string?>(pid == 1234 ? "133700000000000000" : null));
        Assert.Equal(1234, result.Rule.ProcessId); Assert.Equal("133700000000000000", result.Rule.StartKey);
        Assert.Equal("process_all_private_commit_actual", result.Rule.MetricId);
        Assert.Equal("atOrAbove", result.Rule.Comparison);
        Assert.Equal(Math.BitIncrement(4294967296d), result.Rule.Threshold);
        Assert.Equal(2000, result.IntervalMs);
    }

    [Fact]
    public async Task ExplicitProcessIdentityNeedsNoLookupAndQuotedSoftwareIdsAreLossless()
    {
        var process = await MonitoringConditionLanguage.ExpandAsync("process 1234:5678 private >= 1B for 1s", "p",
            MonitoringConditionVocabulary.Create(null), _ => throw new InvalidOperationException("must not resolve"));
        Assert.Equal("5678", process.Rule.StartKey);
        var software = await MonitoringConditionLanguage.ExpandAsync("software \"software:a b\\\"c\\\\d\" private-resident < 1.5GiB for 1s", "s",
            MonitoringConditionVocabulary.Create(null), _ => Task.FromResult<string?>(null));
        Assert.Equal("software:a b\"c\\d", software.Rule.SoftwareId);
        Assert.Equal(1610612736d, software.Rule.Threshold);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("unknown < 1GiB for 1s", 0)]
    [InlineData("memory.free <= 1GiB for 1s", 12)]
    [InlineData("memory.free < 1GB for 1s", 15)]
    [InlineData("memory.free < 1% for 1s", 15)]
    [InlineData("memory.free < -1GiB for 1s", 14)]
    [InlineData("memory.free < 1e3GiB for 1s", 15)]
    [InlineData("memory.free < 1.GiB for 1s", 16)]
    [InlineData("memory.free < 1GiB 60s", 19)]
    [InlineData("memory.free < 1GiB for 0s", 23)]
    [InlineData("memory.free < 1GiB for 1m", 24)]
    [InlineData("memory.free < 1GiB for 1s every 0ms", 32)]
    [InlineData("memory.free < 1GiB for 1s every 0.5ms", 32)]
    [InlineData("memory.free < 1GiB for 1s top -1", 30)]
    [InlineData("memory.free < 1GiB for 1s top 1.5", 31)]
    [InlineData("memory.free < 1GiB for 1s top 1 every 1s", 32)]
    [InlineData("memory.free < 1GiB for 1s junk", 26)]
    [InlineData("memory.free < 1GiB for 1s\n", 25)]
    [InlineData("process 0 private < 1B for 1s", 8)]
    [InlineData("process 1234:0 private < 1B for 1s", 13)]
    [InlineData("software \"\" private < 1B for 1s", 9)]
    [InlineData("software \"abc private < 1B for 1s", 9)]
    [InlineData("software \"a\\qb\" private < 1B for 1s", 12)]
    [InlineData("software \" a \" private < 1B for 1s", 9)]
    [InlineData("process 1234abc private < 1B for 1s", 12)]
    public async Task InvalidInputReportsExactZeroBasedPositionAndReason(string text, int position)
    {
        var error = await Assert.ThrowsAsync<MonitoringConditionParseException>(() => MonitoringConditionLanguage.ExpandAsync(text,
            "test", MonitoringConditionVocabulary.Create(null), _ => Task.FromResult<string?>(null)));
        Assert.Equal(position, error.Position);
        Assert.False(string.IsNullOrWhiteSpace(error.Reason));
    }

    [Fact]
    public async Task MissingFreshProcessIdentityDoesNotGuess()
    {
        var error = await Assert.ThrowsAsync<MonitoringConditionParseException>(() => MonitoringConditionLanguage.ExpandAsync(
            "process 1234 private > 4GiB for 30s every 2s", "p", MonitoringConditionVocabulary.Create(null), _ => Task.FromResult<string?>(null)));
        Assert.Equal(8, error.Position); Assert.Contains("start key", error.Reason);
    }

    [Fact]
    public async Task DeviceNameComesFromTheSamePublishedVocabularyAndKeepsItsExactIdentity()
    {
        var vocabulary = MonitoringConditionVocabulary.Create(StorageMetricSnapshotTestData.CommittedSnapshot());
        var name = vocabulary.Names.First(item => item.Binding == "device" && item.Unit == "B");
        var result = await MonitoringConditionLanguage.ExpandAsync($"device \"{name.DeviceIdentity}\" {name.Name} >= 1B for 1s", "device", vocabulary, _ => Task.FromResult<string?>(null));
        Assert.Equal(name.MetricId, result.Rule.MetricId); Assert.Equal(name.DeviceIdentity, result.Rule.DeviceIdentity);
        Assert.Equal("value", result.Rule.Quantity); Assert.Equal(0, result.Rule.TopOwners);
    }
}
