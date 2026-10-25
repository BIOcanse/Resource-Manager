namespace Resource_Manager_APP.Tests;

public sealed class CpuResidencyWindowCompletionTests
{
    [Fact]
    public void BoundaryDeliveryOrderDoesNotMoveTheWindowBeforeAnyProcessorsFirstBoundary()
    {
        var window = new CpuResidencyWindowCompletion([0, 1], windowQpc: 50);
        window.Observe(0, 200);
        window.Observe(1, 100);
        window.Observe(0, 249);
        window.Observe(1, 249);
        Assert.False(window.Complete.IsCompleted);
        window.Observe(0, 250);
        window.Observe(1, 250);
        Assert.True(window.Complete.Result);
    }

    [Fact]
    public void EveryLogicalProcessorMustEstablishItsFirstBoundary()
    {
        var window = new CpuResidencyWindowCompletion([2, 7, 9], windowQpc: 10);

        window.Observe(2, 100);
        window.Observe(7, 110);

        Assert.False(window.Boundaries.IsCompleted);
        Assert.False(window.Complete.IsCompleted);

        window.Observe(9, 120);

        Assert.True(window.Boundaries.IsCompletedSuccessfully);
        Assert.True(window.Boundaries.Result);
        Assert.False(window.Complete.IsCompleted);
    }

    [Fact]
    public void FullWindowStartsAtTheLastFirstBoundaryAndEveryProcessorMustReachItsEnd()
    {
        var window = new CpuResidencyWindowCompletion([0, 1], windowQpc: 50);

        window.Observe(0, 100);
        window.Observe(1, 200);

        Assert.True(window.Boundaries.IsCompletedSuccessfully);
        Assert.True(window.Boundaries.Result);

        window.Observe(0, 249);
        window.Observe(1, 249);
        Assert.False(window.Complete.IsCompleted);

        window.Observe(0, 250);
        Assert.False(window.Complete.IsCompleted);

        window.Observe(1, 250);

        Assert.True(window.Complete.IsCompletedSuccessfully);
        Assert.True(window.Complete.Result);
    }

    [Fact]
    public void NoContextSwitchDataLeavesBothWaitsIncomplete()
    {
        var window = new CpuResidencyWindowCompletion([0, 1], windowQpc: 10);

        Assert.False(window.Boundaries.IsCompleted);
        Assert.False(window.Complete.IsCompleted);
    }

    [Fact]
    public void MissingLogicalProcessorEndBoundaryLeavesWindowIncomplete()
    {
        var window = new CpuResidencyWindowCompletion([0, 1, 2], windowQpc: 10);

        window.Observe(0, 10);
        window.Observe(1, 20);
        window.Observe(2, 30);
        window.Observe(0, 40);
        window.Observe(1, 40);

        Assert.True(window.Boundaries.IsCompletedSuccessfully);
        Assert.True(window.Boundaries.Result);
        Assert.False(window.Complete.IsCompleted);
    }

    [Fact]
    public void DuplicateAndUnknownLogicalProcessorsCannotSupplyMissingCoverage()
    {
        var window = new CpuResidencyWindowCompletion([0, 1], windowQpc: 10);

        window.Observe(0, 10);
        window.Observe(0, 30);
        window.Observe(99, 1000);

        Assert.False(window.Boundaries.IsCompleted);
        Assert.False(window.Complete.IsCompleted);

        window.Observe(1, 100);
        window.Observe(99, 110);
        window.Observe(0, 109);
        Assert.False(window.Complete.IsCompleted);

        window.Observe(0, 110);
        window.Observe(0, 111);
        window.Observe(99, 200);
        Assert.False(window.Complete.IsCompleted);

        window.Observe(1, 110);

        Assert.True(window.Complete.IsCompletedSuccessfully);
        Assert.True(window.Complete.Result);
    }

    [Fact]
    public void InvalidationCompletesUnresolvedWaitsAsFalse()
    {
        var window = new CpuResidencyWindowCompletion([0, 1], windowQpc: 10);
        window.Observe(0, 100);

        window.Invalidate();

        Assert.True(window.Boundaries.IsCompletedSuccessfully);
        Assert.False(window.Boundaries.Result);
        Assert.True(window.Complete.IsCompletedSuccessfully);
        Assert.False(window.Complete.Result);

        window.Observe(1, 110);
        Assert.False(window.Boundaries.Result);
        Assert.False(window.Complete.Result);
    }

    [Fact]
    public void InvalidationDuringWindowLeavesBoundarySuccessAndCompletesWindowAsFalse()
    {
        var window = new CpuResidencyWindowCompletion([0, 1], windowQpc: 10);
        window.Observe(0, 100);
        window.Observe(1, 105);

        window.Invalidate();

        Assert.True(window.Boundaries.IsCompletedSuccessfully);
        Assert.True(window.Boundaries.Result);
        Assert.True(window.Complete.IsCompletedSuccessfully);
        Assert.False(window.Complete.Result);

        window.Observe(0, 115);
        window.Observe(1, 115);
        Assert.False(window.Complete.Result);
    }

    [Fact]
    public void CompletedWindowIgnoresSubsequentEvents()
    {
        var window = new CpuResidencyWindowCompletion([0, 1], windowQpc: 10);
        window.Observe(0, 100);
        window.Observe(1, 105);
        window.Observe(0, 115);
        window.Observe(1, 115);

        var boundaries = window.Boundaries;
        var complete = window.Complete;
        window.Observe(0, 200);
        window.Observe(1, 200);
        window.Observe(99, 200);

        Assert.Same(boundaries, window.Boundaries);
        Assert.Same(complete, window.Complete);
        Assert.True(boundaries.IsCompletedSuccessfully);
        Assert.True(boundaries.Result);
        Assert.True(complete.IsCompletedSuccessfully);
        Assert.True(complete.Result);
    }
}
