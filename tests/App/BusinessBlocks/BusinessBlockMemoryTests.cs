using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using SharedScheduler = ResourceManager.Shared.BusinessBlocks.BusinessBlockScheduler;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

[Collection(BusinessBlockMemoryCollection.Name)]
public sealed class BusinessBlockMemoryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ReportsControllerAndDefaultHistoryMemoryCost()
    {
        const int registrationCount = 40;
        var registrations = Enumerable.Range(0, registrationCount)
            .Select(index => BusinessBlockTestSupport.Register(index + 101, new NeverRunBlock()))
            .ToArray();
        var logger = NullLogger<SharedScheduler>.Instance;

        // Warm code paths and logger metadata before measuring the retained object graph.
        await using (var warm = BusinessBlockTestSupport.Create(registrations))
        {
            var warmGate = warm.Authority.ClaimWrite<bool>(BusinessBlockTestSupport.GateKey(101));
            for (var index = 0; index < 128; index++)
                await warmGate.PublishAsync(index % 2 == 0, new(BusinessBlockRequestKind.Configuration, "test", "warm", 1));
        }

        var beforeController = CollectLiveBytes();
        var controllerAllocationStart = GC.GetTotalAllocatedBytes();
        await using var controller = BusinessBlockTestSupport.Create(registrations);
        var enabled101 = controller.Authority.ClaimWrite<bool>(BusinessBlockTestSupport.GateKey(101));
        var controllerAllocated = GC.GetTotalAllocatedBytes() - controllerAllocationStart;
        var afterController = CollectLiveBytes();
        var controllerRetained = Math.Max(0, afterController - beforeController);

        const int historyCount = 128;
        const string operation = "memory-probe";
        var beforeHistory = CollectLiveBytes();
        var historyAllocationStart = GC.GetTotalAllocatedBytes();
        for (var index = 0; index < historyCount; index++)
        {
            await enabled101.PublishAsync(index % 2 == 0, new(BusinessBlockRequestKind.Configuration, "test", operation, 1));
        }
        var historyAllocated = GC.GetTotalAllocatedBytes() - historyAllocationStart;
        var afterHistory = CollectLiveBytes();
        var historyRetained = Math.Max(0, afterHistory - beforeHistory);

        var snapshot = controller.Read();
        Assert.Equal(128, snapshot.HistoryLimit);
        Assert.Equal(historyCount, snapshot.Decisions.Count);
        Assert.All(snapshot.Decisions, decision => Assert.Equal("test", decision.Reason.Owner));

        output.WriteLine($"Controller: {registrationCount} lazy registrations, history limit {snapshot.HistoryLimit}.");
        output.WriteLine($"Controller construction: {controllerAllocated:N0} B allocated; approximately {controllerRetained:N0} B retained ({controllerRetained / (double)registrationCount:N1} B per registered block).");
        output.WriteLine($"Filling history: {historyAllocated:N0} B allocated for {historyCount} transitions; approximately {historyRetained:N0} B retained ({historyRetained / (double)historyCount:N1} B per transition).");

        Assert.InRange(controllerAllocated, 0, 2 * 1024 * 1024);
        Assert.InRange(controllerRetained, 0, 2 * 1024 * 1024);
        Assert.InRange(historyAllocated, 0, 2 * 1024 * 1024);
        Assert.InRange(historyRetained, 0, 512 * 1024);
    }

    private static long CollectLiveBytes() => GC.GetTotalMemory(forceFullCollection: true);

    private sealed class NeverRunBlock : IBusinessBlock
    {
        public Task RunAsync(BusinessBlockRunContext context)
            => throw new InvalidOperationException("The memory test must not construct or run business blocks.");
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BusinessBlockMemoryCollection
{
    public const string Name = "Business block memory measurement";
}
