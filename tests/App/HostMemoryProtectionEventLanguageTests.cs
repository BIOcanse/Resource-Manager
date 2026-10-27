using ResourceManager.App.Application.Monitoring;

namespace Resource_Manager_APP.Tests;

public sealed class HostMemoryProtectionEventLanguageTests
{
    [Theory]
    [InlineData("active")]
    [InlineData("inactive")]
    [InlineData("normal-running-point-created")]
    [InlineData("threshold-reached")]
    [InlineData("below-threshold")]
    [InlineData("action-started")]
    [InlineData("action")]
    [InlineData("reserve-remaining-changed")]
    [InlineData("reserve-error")]
    public async Task VocabularyEventsExpandWithoutMetricEvaluationOrIdentityLookups(string name)
    {
        var vocabulary = MonitoringConditionVocabulary.Create(null);
        Assert.Contains(name, vocabulary.Events);
        var hook = await MonitoringConditionLanguage.ExpandAsync("memory-protection " + name, "event", vocabulary,
            _ => throw new InvalidOperationException("event hooks never resolve process identities"));
        Assert.Equal("memory-protection." + name, hook.Event);
        Assert.Null(hook.Rule);
        Assert.Equal(0, hook.IntervalMs);
    }

    [Theory]
    [InlineData("memory-protection missing")]
    [InlineData("memory-protection action for 1s")]
    [InlineData("memory-protection action every 1s")]
    [InlineData("memory-protection action top 5")]
    public async Task EventGrammarRejectsUnknownEventsAndMetricClauses(string condition)
        => await Assert.ThrowsAsync<MonitoringConditionParseException>(() => MonitoringConditionLanguage.ExpandAsync(condition,
            "bad", MonitoringConditionVocabulary.Create(null), _ => Task.FromResult<string?>(null)));
}
