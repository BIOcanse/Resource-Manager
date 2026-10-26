using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using static Resource_Manager_APP.Tests.BusinessBlockTestSupport;

namespace Resource_Manager_APP.Tests;

public sealed class BusinessBlockControllerTests
{
    [Fact]
    public async Task ConcurrentTransitionsRetainTheNewestBoundedHistory()
    {
        const int blockCount = 32;
        const int retained = 4;
        for (var round = 0; round < 16; round++)
        {
            var control = new BusinessBlockController(NullLogger<BusinessBlockController>.Instance, retained);
            await Task.WhenAll(Enumerable.Range(0, blockCount).Select(index => Task.Run(() =>
                control.Start(index.ToString(), context => { context.ReportRunning(); return Task.CompletedTask; }))));
            await Until(() => control.Read().States.Values.All(state => state == BusinessBlockState.Stopped));
            var history = control.Read().History;
            Assert.Equal(Enumerable.Range(blockCount * 3 - retained + 1, retained).Select(value => (long)value),
                history.Select(item => item.Sequence));
            Assert.All(control.Read().States.Values, state => Assert.Equal(BusinessBlockState.Stopped, state));
        }
    }

    [Fact]
    public async Task ReadsDoNotConstructOrRunBlocksAndUnknownIdsFail()
    {
        var constructions = 0;
        await using var scheduler = Create([new("lazy", [], _ => { Interlocked.Increment(ref constructions); return new Probe(); })]);
        var before = scheduler.Read();
        Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState("lazy"));
        Assert.Equal(before.History, scheduler.Read().History);
        Assert.Equal(before.Decisions, scheduler.Read().Decisions);
        Assert.Equal(0, constructions);
        Assert.Throws<KeyNotFoundException>(() => scheduler.ReadState("missing"));
        Assert.Throws<KeyNotFoundException>(() => scheduler.AcquireDependency("missing", Reason()));
        Assert.Throws<KeyNotFoundException>(() => { _ = scheduler.SetDemandAsync("unknown", "missing", Reason()); });
        Assert.Throws<KeyNotFoundException>(() => { _ = scheduler.SetEnabledAsync("missing", true, Reason()); });
        Assert.Throws<KeyNotFoundException>(() => { _ = scheduler.SetFactAsync("missing", true, Reason()); });
        Assert.Empty(scheduler.Read().Demands);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task DecisionsAreBoundedPerNodeAndEveryDecisionIsLogged(int limit)
    {
        var logger = new BusinessBlockControlSystemTests.CapturedLogger<BusinessBlockScheduler>();
        await using var scheduler = Create([Register("worker", new Probe())], historyLimit: limit, logger: logger);
        for (var index = 0; index < 8; index++)
            await scheduler.SetEnabledAsync("worker", index % 2 != 0, Reason(cause: "configuration-" + index));
        var decisions = scheduler.Read().Decisions;
        Assert.Equal(limit, decisions.Count);
        Assert.Equal(8, logger.Messages.Count(value => value.StartsWith("Business block decision ", StringComparison.Ordinal)));
        if (limit > 0)
        {
            Assert.Equal([6L, 7L, 8L], decisions.Select(value => value.Sequence));
            Assert.Equal("configuration-7", decisions[^1].Reason.ConfigurationOrDependency);
        }
        Assert.All(scheduler.Read().Blocks, value => Assert.Equal(BusinessBlockState.Stopped, value.State));
    }

    [Fact]
    public async Task ControlHistoryIsBoundedAndZeroRetentionStillLogsTransitions()
    {
        foreach (var limit in new[] { 0, 3 })
        {
            var logger = new BusinessBlockControlSystemTests.CapturedLogger<BusinessBlockController>();
            var control = new BusinessBlockController(logger, limit);
            for (var index = 0; index < 4; index++)
            {
                control.Start("worker", context => { context.ReportRunning(); return Task.CompletedTask; });
                await Until(() => control.ReadState("worker") == BusinessBlockState.Stopped);
            }
            Assert.Equal(limit, control.Read().History.Count);
            Assert.Equal(12, logger.Messages.Count);
            Assert.Equal(BusinessBlockState.Stopped, control.ReadState("worker"));
        }
    }
}
