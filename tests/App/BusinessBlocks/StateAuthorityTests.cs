using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Hosting;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks.State;
using SharedScheduler = ResourceManager.Shared.BusinessBlocks.BusinessBlockScheduler;
using BackendRegistry = ResourceManager.App.StateItems.StateRegistry;

namespace Resource_Manager_APP.Tests;

public sealed class StateAuthorityTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly BusinessBlockReason Reason = new(BusinessBlockRequestKind.User, "state-authority-test", "explicit", 7);

    [Fact]
    public async Task ReadFunctionReadsCurrentOwnerValueAndNotifiesWithoutCachingOrInvokingWhileUnbound()
    {
        var authority = CreateAuthority();
        var current = false;
        var reads = 0;
        var function = authority.ClaimReadFunction<bool>(BackendItems.PresentNativeSessionAvailable, () =>
        {
            reads++;
            return StateRead<bool>.FromValue(current);
        });
        Assert.Throws<InvalidOperationException>(() => authority.ClaimCell<bool>(BackendItems.PresentNativeSessionAvailable));
        Assert.False(authority.Read<bool>(BackendItems.PresentNativeSessionAvailable).Value);
        current = true;
        Assert.True(authority.Read<bool>(BackendItems.PresentNativeSessionAvailable).Value);
        var notifications = 0;
        using var subscription = authority.Subscribe(BackendItems.PresentNativeSessionAvailable, "read-function", _ =>
        { notifications++; return Task.CompletedTask; });
        var before = reads;
        await function.NotifyChangedAsync(Reason);
        Assert.Equal(before, reads);
        Assert.Equal(1, notifications);
        await function.UnbindAsync(Reason);
        Assert.Equal(NoValueReason.OwnerStopped, authority.Read<bool>(BackendItems.PresentNativeSessionAvailable).NoValueReason);
        Assert.Equal(before, reads);
        current = false;
        await function.BindAsync(Reason);
        Assert.False(authority.Read<bool>(BackendItems.PresentNativeSessionAvailable).Value);
        Assert.Equal(before + 1, reads);
    }

    [Fact]
    public void ReadFunctionReportsReadFailedWhenItsOwnerThrows()
    {
        var authority = CreateAuthority();
        authority.ClaimReadFunction<bool>(BackendItems.PresentNativeSessionAvailable, () => throw new InvalidOperationException("read"));
        Assert.Equal(NoValueReason.ReadFailed, authority.Read<bool>(BackendItems.PresentNativeSessionAvailable).NoValueReason);
        Assert.Throws<InvalidOperationException>(() => authority.ClaimReadFunction<bool>(BackendItems.ProgramShuttingDown,
            () => StateRead<bool>.FromValue(true)));
    }

    [Fact]
    public void CurrentReadFunctionDoesNotAllocateManagedMemory()
    {
        var authority = CreateAuthority();
        authority.ClaimReadFunction<bool>(BackendItems.PresentNativeSessionAvailable, static () => StateRead<bool>.FromValue(true));
        for (var index = 0; index < 4096; index++) _ = authority.Read<bool>(BackendItems.PresentNativeSessionAvailable);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var observed = 0;
        for (var index = 0; index < 100_000; index++)
            if (authority.Read<bool>(BackendItems.PresentNativeSessionAvailable).Value) observed++;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(100_000, observed);
        Assert.Equal(0L, allocated);
    }

    [Fact]
    public async Task DynamicHoldersAreUniquePerItemAndReleaseOnlyTheirOwnRegistration()
    {
        var authority = CreateAuthority();
        var cell = authority.ClaimCell<bool>(BackendItems.PresentNativeSessionAvailable);
        var calls = 0;
        Func<BusinessBlockReason, Task> reader = _ => { calls++; return Task.CompletedTask; };
        using var first = authority.Subscribe(BackendItems.PresentNativeSessionAvailable, "first", reader);
        Assert.Throws<InvalidOperationException>(() => authority.Subscribe(BackendItems.PresentNativeSessionAvailable, "first", reader));
        using var second = authority.Subscribe(BackendItems.PresentNativeSessionAvailable, "second", reader);
        await cell.PublishAsync(true, Reason);
        Assert.Equal(2, calls);
        first.Dispose();
        first.Dispose();
        await cell.PublishAsync(false, Reason);
        Assert.Equal(3, calls);
        using var replacement = authority.Subscribe(BackendItems.PresentNativeSessionAvailable, "first", reader);
        first.Dispose();
        await cell.PublishAsync(true, Reason);
        Assert.Equal(5, calls);
    }

    [Fact]
    public async Task ClaimsRequireTheDeclaredStorageAndTypeAndOnlyOneOwnerCanWriteEachKey()
    {
        var authority = CreateAuthority();
        Assert.Throws<InvalidOperationException>(() => authority.ClaimCell<bool>(BackendItems.ProgramShuttingDown));
        Assert.Throws<InvalidOperationException>(() => authority.ClaimWrite<int>(BackendItems.ProgramShuttingDown));
        var writer = authority.ClaimWrite<bool>(BackendItems.ProgramShuttingDown);
        await writer.PublishAsync(false, Reason);
        Assert.Throws<InvalidOperationException>(() => authority.ClaimWrite<bool>(BackendItems.ProgramShuttingDown));
        Assert.False(authority.Read<bool>(BackendItems.ProgramShuttingDown).Value);

        Assert.Throws<InvalidOperationException>(() => authority.ClaimWrite<bool>(BackendItems.PresentNativeSessionAvailable));
        Assert.Throws<InvalidOperationException>(() => authority.ClaimCell<int>(BackendItems.PresentNativeSessionAvailable));
        var cell = authority.ClaimCell<bool>(BackendItems.PresentNativeSessionAvailable);
        await cell.PublishAsync(true, Reason);
        Assert.Throws<InvalidOperationException>(() => authority.ClaimCell<bool>(BackendItems.PresentNativeSessionAvailable));
        Assert.True(authority.Read<bool>(BackendItems.PresentNativeSessionAvailable).Value);
        Assert.Throws<InvalidOperationException>(() => authority.Read<int>(BackendItems.PresentNativeSessionAvailable));
    }

    [Fact]
    public void UnknownItemsAndInvalidPerBlockKeysAreRejected()
    {
        var authority = CreateAuthority();
        Assert.Throws<KeyNotFoundException>(() => authority.Read<bool>(int.MaxValue));
        Assert.Throws<KeyNotFoundException>(() => authority.Read<bool>(BackendItems.HostStartPermitted));
        Assert.Throws<KeyNotFoundException>(() => authority.Read<bool>(new(BackendItems.HostStartPermitted, int.MaxValue)));
        Assert.Throws<KeyNotFoundException>(() => authority.Read<bool>(new(BackendItems.PresentNativeSessionAvailable, BackendUids.PresentFrameEtwSource)));
        Assert.Equal(NoValueReason.NotPublished,
            authority.Read<bool>(new(BackendItems.HostStartPermitted, BackendUids.PresentFrameEtwSource)).NoValueReason);
    }

    [Fact]
    public async Task ReferenceUnbindPublishesOwnerStoppedUntilTheOwnerBindsItsCurrentValueAgain()
    {
        var authority = CreateAuthority();
        Assert.Equal(NoValueReason.NotPublished, authority.Read<bool>(BackendItems.PresentNativeSessionAvailable).NoValueReason);
        var cell = authority.ClaimCell<bool>(BackendItems.PresentNativeSessionAvailable);
        Assert.Equal(NoValueReason.NotPublished, authority.Read<bool>(BackendItems.PresentNativeSessionAvailable).NoValueReason);
        await cell.PublishAsync(true, Reason);
        var previous = authority.Read<bool>(BackendItems.PresentNativeSessionAvailable);
        await cell.UnbindAsync(Reason);
        var stopped = authority.Read<bool>(BackendItems.PresentNativeSessionAvailable);
        Assert.False(stopped.HasValue);
        Assert.Equal(NoValueReason.OwnerStopped, stopped.NoValueReason);
        Assert.Throws<InvalidOperationException>(() => stopped.Value);
        Assert.True(previous.Value);

        await cell.PublishAsync(false, Reason);
        Assert.Equal(NoValueReason.OwnerStopped, authority.Read<bool>(BackendItems.PresentNativeSessionAvailable).NoValueReason);
        await cell.BindAsync(Reason);
        Assert.False(authority.Read<bool>(BackendItems.PresentNativeSessionAvailable).Value);
        await cell.PublishNoValueAsync(NoValueReason.ReadFailed, Reason);
        Assert.Equal(NoValueReason.ReadFailed, authority.Read<bool>(BackendItems.PresentNativeSessionAvailable).NoValueReason);
    }

    [Fact]
    public async Task CurrentBooleanAndNoValueReadsAllocateNoManagedMemory()
    {
        var authority = CreateAuthority();
        await authority.ClaimCell<bool>(BackendItems.PresentNativeSessionAvailable).PublishAsync(true, Reason);
        var present = new StateItemKey(BackendItems.PresentNativeSessionAvailable);
        var unpublished = new StateItemKey(BackendItems.ProgramShuttingDown);
        var total = 0;
        for (var index = 0; index < 4096; index++)
        {
            if (authority.Read<bool>(present).GetValueOrDefault(false)) total++;
            if (authority.Read<bool>(unpublished).NoValueReason == NoValueReason.NotPublished) total++;
        }
        _ = GC.GetAllocatedBytesForCurrentThread();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 100_000; index++)
        {
            if (authority.Read<bool>(present).GetValueOrDefault(false)) total++;
            if (authority.Read<bool>(unpublished).NoValueReason == NoValueReason.NotPublished) total++;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(208_192, total);
        Assert.Equal(0L, allocated);
    }

    [Fact]
    public async Task RuleReadOfAnUndeclaredItemFaultsThePublishRoundWithoutStartingAnyBlock()
    {
        var authority = CreateAuthority();
        var cell = authority.ClaimCell<bool>(BackendItems.PresentNativeSessionAvailable);
        var block = new HoldingBlock();
        var rule = new BusinessBlockDecisionRegistration(BackendUids.Program, [BackendItems.PresentNativeSessionAvailable],
            input => input.Read<bool>(BackendItems.VidMmNativeSessionAvailable).GetValueOrDefault(false)
                ? [BackendUids.PresentFrameEtwSource] : []);
        await using var scheduler = CreateSingleBlockScheduler(authority, block, rule);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cell.PublishAsync(true, Reason).WaitAsync(Timeout));
        Assert.Contains("undeclared", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(BackendItems.VidMmNativeSessionAvailable.ToString(), error.Message, StringComparison.Ordinal);
        Assert.Equal(0, block.Runs);
        Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState(BackendUids.PresentFrameEtwSource));
    }

    [Theory]
    [InlineData(NoValueReason.NotPublished)]
    [InlineData(NoValueReason.OwnerStopped)]
    [InlineData(NoValueReason.ReadFailed)]
    public async Task RuleExplicitlyTreatsEachNoValueReasonAsFalse(NoValueReason reason)
    {
        var authority = CreateAuthority();
        var cell = authority.ClaimCell<bool>(BackendItems.PresentNativeSessionAvailable);
        var block = new HoldingBlock();
        var rule = new BusinessBlockDecisionRegistration(BackendUids.Program, [BackendItems.PresentNativeSessionAvailable],
            input => input.Read<bool>(BackendItems.PresentNativeSessionAvailable).GetValueOrDefault(false)
                ? [BackendUids.PresentFrameEtwSource] : []);
        await using var scheduler = CreateSingleBlockScheduler(authority, block, rule);
        try
        {
            await cell.PublishNoValueAsync(reason, Reason).WaitAsync(Timeout);
            Assert.Equal(0, block.Runs);
            await cell.PublishAsync(true, Reason).WaitAsync(Timeout);
            await Until(() => scheduler.ReadState(BackendUids.PresentFrameEtwSource) == BusinessBlockState.Running);
            await cell.PublishNoValueAsync(reason, Reason).WaitAsync(Timeout);
            await Until(() => scheduler.ReadState(BackendUids.PresentFrameEtwSource) == BusinessBlockState.Stopped);
            Assert.Equal(reason, authority.Read<bool>(BackendItems.PresentNativeSessionAvailable).NoValueReason);
            Assert.Equal(1, block.Runs);
        }
        finally { await Stop(scheduler); }
    }

    [Fact]
    public async Task CustomRuleDoesNotReadOrReactToUnrelatedAvailability()
    {
        var authority = CreateAuthority();
        var cell = authority.ClaimCell<bool>(BackendItems.PresentNativeSessionAvailable);
        await cell.PublishAsync(true, Reason);
        var block = new HoldingBlock();
        var executionKey = new StateItemKey(BackendItems.HostStartPermitted, BackendUids.PresentFrameEtwSource);
        var permit = authority.ClaimCell<bool>(executionKey);
        var rule = new BusinessBlockDecisionRegistration(BackendUids.Program, [executionKey],
            input => input.Read<bool>(executionKey).GetValueOrDefault(false) ? [BackendUids.PresentFrameEtwSource] : []);
        await using var scheduler = CreateSingleBlockScheduler(authority, block, rule);
        try
        {
            await permit.PublishAsync(true, Reason).WaitAsync(Timeout);
            await Until(() => scheduler.ReadState(BackendUids.PresentFrameEtwSource) == BusinessBlockState.Running);
            await cell.PublishAsync(false, Reason).WaitAsync(Timeout);
            // Force an actual completed decision after the unrelated availability publication.
            await permit.PublishAsync(true, Reason).WaitAsync(Timeout);
            Assert.Equal(BusinessBlockState.Running, scheduler.ReadState(BackendUids.PresentFrameEtwSource));
            Assert.Equal(1, block.Runs);
            Assert.DoesNotContain(scheduler.Read().CurrentDecisions.Single().Items,
                item => item.ItemUid == BackendItems.PresentNativeSessionAvailable);
            Assert.DoesNotContain(scheduler.Read().History, transition => transition.To == BusinessBlockState.Stopping);
        }
        finally { await Stop(scheduler); }
    }

    private static StateAuthority CreateAuthority() => new(BackendRegistry.Items, BackendRegistry.Blocks);
    private static TestBlockScheduler CreateSingleBlockScheduler(StateAuthority authority, IBusinessBlock block,
        BusinessBlockDecisionRegistration rule)
        => new([new TestBlockRegistration(BackendUids.PresentFrameEtwSource, [], _ => block)], authority,
            historyLimit: 64, logger: NullLogger.Instance, decisions: [rule]);
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!predicate()) await Task.Delay(1, timeout.Token);
    }
    private static async Task Stop(SharedScheduler scheduler)
    {
        await scheduler.ShutdownAsync(Reason).WaitAsync(Timeout);
        await Until(() => scheduler.Read().Blocks.All(block => block.State == BusinessBlockState.Stopped));
    }
    private sealed class HoldingBlock : IBusinessBlock
    {
        private int runs;
        internal int Runs => Volatile.Read(ref runs);
        public async Task RunAsync(BusinessBlockRunContext context)
        {
            Interlocked.Increment(ref runs);
            context.ReportRunning();
            try { await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, context.StopNotification); }
            catch (OperationCanceledException) when (context.StopNotification.IsCancellationRequested) { }
        }
    }
}
