using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
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
            .Select(index => new BusinessBlockRegistration(
                index + 101, Array.Empty<int>(), static _ => new NeverRunBlock()))
            .ToArray();
        var options = Options.Create(new BusinessBlockOptions());
        var provider = new EmptyServiceProvider();
        var logger = NullLogger<BusinessBlockScheduler>.Instance;

        // Warm code paths and logger metadata before measuring the retained object graph.
        await using (var warm = new BusinessBlockScheduler(provider, registrations, options, logger))
        {
            for (var index = 0; index < 128; index++)
                await warm.SetEnabledAsync(101, index % 2 == 0, new(BusinessBlockRequestKind.Configuration, "test", "warm", 1));
        }

        var beforeController = CollectLiveBytes();
        var controllerAllocationStart = GC.GetTotalAllocatedBytes();
        await using var controller = new BusinessBlockScheduler(provider, registrations, options, logger);
        var controllerAllocated = GC.GetTotalAllocatedBytes() - controllerAllocationStart;
        var afterController = CollectLiveBytes();
        var controllerRetained = Math.Max(0, afterController - beforeController);

        const int historyCount = 128;
        const string operation = "memory-probe";
        var beforeHistory = CollectLiveBytes();
        var historyAllocationStart = GC.GetTotalAllocatedBytes();
        for (var index = 0; index < historyCount; index++)
        {
            await controller.SetEnabledAsync(101, index % 2 == 0, new(BusinessBlockRequestKind.Configuration, "test", operation, 1));
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

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

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
