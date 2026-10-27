using System.Reflection;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerComputeRuntimeFactTests
{
    [Theory]
    [InlineData("empty")]
    [InlineData("multiple")]
    [InlineData("start-key")]
    [InlineData("target-id")]
    [InlineData("runtime")]
    public void InvalidTargetDoesNotEraseAnotherTargetsRuntime(string invalid)
    {
        var exclusions = new List<SchedulingFactExclusion>();
        var first = CreateTarget(10, 100, invalid);
        var second = CreateTarget(20, 200);

        var result = CreateFacts([first, second], exclusions);

        Assert.Equal(new HostManagerComputeProcessIdentity(20, 200), Assert.Single(result).Key);
        var exclusion = Assert.Single(exclusions);
        Assert.Equal(invalid switch
        {
            "empty" or "multiple" => SchedulingFactExclusionReason.TargetProcessCountNotOne,
            "start-key" => SchedulingFactExclusionReason.TargetProcessIdentityMissing,
            "target-id" => SchedulingFactExclusionReason.TargetProcessIdentityMismatch,
            _ => SchedulingFactExclusionReason.TargetRuntimeStateInvalid
        }, exclusion.Reason);
        Assert.Equal(invalid == "runtime" ? SchedulingFactCode.RuntimeState : SchedulingFactCode.Identity, exclusions[0].Fact);
    }

    [Fact]
    public void DuplicateIdentityExcludesEveryOccurrenceAndKeepsUnrelatedTarget()
    {
        var exclusions = new List<SchedulingFactExclusion>();
        var result = CreateFacts([
            CreateTarget(10, 100), CreateTarget(10, 100), CreateTarget(10, 100), CreateTarget(20, 200)
        ], exclusions);

        Assert.Equal(new HostManagerComputeProcessIdentity(20, 200), Assert.Single(result).Key);
        Assert.All(exclusions, exclusion => Assert.Equal(SchedulingFactExclusionReason.DuplicateTargetProcessIdentity, exclusion.Reason));
        Assert.Equal(2, exclusions.Count);
        Assert.All(exclusions, exclusion => Assert.Equal(SchedulingFactCode.Identity, exclusion.Fact));
    }

    // The target model belongs to the coordinator. Exercise its private boundary without
    // exposing production target construction merely to provide malformed test inputs.
    private static object CreateTarget(int processId, ulong startKey, string? invalid = null)
    {
        var targetType = typeof(HostManagerSmartCoordinator).GetNestedType("HostManagerTargetInfo", BindingFlags.NonPublic)!;
        var constructor = targetType.GetConstructors().Single();
        var values = new Dictionary<string, object?>
        {
            ["TargetId"] = invalid == "target-id" ? "wrong-target" : HostManagerTargetIdentity.CreateProcessTargetId(processId, startKey),
            ["ProcessIds"] = invalid == "empty" ? Array.Empty<int>()
                : invalid == "multiple" ? new[] { processId, 99 } : new[] { processId },
            ["ProcessStartKeys"] = invalid == "start-key" ? new Dictionary<int, ulong>()
                : new Dictionary<int, ulong> { [processId] = startKey },
            ["RuntimeState"] = invalid == "runtime" ? "invalid" : HostManagerRuntimeStates.ForegroundFocused
        };
        return constructor.Invoke(constructor.GetParameters().Select(parameter =>
            values.TryGetValue(parameter.Name!, out var value) ? value
                : parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null).ToArray());
    }

    private static IReadOnlyDictionary<HostManagerComputeProcessIdentity, HostManagerComputeRuntimeFact> CreateFacts(
        object[] targets, ICollection<SchedulingFactExclusion> exclusions)
    {
        var targetArray = Array.CreateInstance(targets[0].GetType(), targets.Length);
        for (var index = 0; index < targets.Length; index++) targetArray.SetValue(targets[index], index);
        return (IReadOnlyDictionary<HostManagerComputeProcessIdentity, HostManagerComputeRuntimeFact>)
            typeof(HostManagerSmartCoordinator).GetMethod("CreateComputeRuntimeFacts", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [targetArray, exclusions])!;
    }
}
