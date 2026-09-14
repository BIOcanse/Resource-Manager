using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Monitoring;

namespace Resource_Manager_APP.Tests;

public sealed class DataHistoryTests
{
    [Fact]
    public void CompilerTakesMaximumPerItemWithoutKnowingConsumerAlgorithms()
    {
        var plan = CompiledDataHistoryPlan.Compile([
            new("cpu", 3), new("CPU", 5), new("cpu", 8), new("memory", 2)]);
        Assert.Equal(2, plan.Requirements.Length);
        Assert.Equal(8, plan.GetRetainedRounds("CPU"));
        Assert.Equal(2, plan.GetRetainedRounds("memory"));
        Assert.Equal(1, plan.GetRetainedRounds("undemanded"));
        var reduced = CompiledDataHistoryPlan.Compile([new("cpu", 3), new("cpu", 5)]);
        Assert.Equal(5, reduced.GetRetainedRounds("cpu"));
        Assert.Equal([nameof(HistoryRequirement.DataItem), nameof(HistoryRequirement.RetainedRounds)],
            typeof(HistoryRequirement).GetProperties().Select(static property => property.Name));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CompilerRejectsNonpositiveRoundRequirements(int rounds)
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            CompiledDataHistoryPlan.Compile([new("cpu", rounds)]));

    [Fact]
    public void BufferRetainsNullAndEqualValuesAsSeparateRounds()
    {
        var history = new DataHistoryBuffer<double?>(4);
        history.Append(5);
        history.Append(5);
        history.Append(null);
        history.Append(0);
        var original = history.Read();
        Assert.Equal(new double?[] { 5, 5, null, 0 }, original);
        history.Append(9);
        Assert.Equal(new double?[] { 5, null, 0, 9 }, history.Read());
        Assert.Equal(new double?[] { 5, 5, null, 0 }, original);
        Assert.Equal(4, history.Count);
    }

    [Fact]
    public void BufferResizePreservesOnlyTheRealTailAndNeverPadsGrowth()
    {
        var history = new DataHistoryBuffer<string>(3);
        foreach (var value in new[] { "a", "b", "c", "d" }) history.Append(value);
        history.Resize(2);
        Assert.Equal<string>(["c", "d"], history.Read());
        history.Resize(5);
        Assert.Equal(2, history.Count);
        history.Append("e");
        Assert.Equal<string>(["c", "d", "e"], history.Read());
        Assert.Equal(5, history.RetainedRounds);
    }
}
