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
                $"block-{index}", Array.Empty<string>(), static _ => new NeverRunBlock()))
            .ToArray();
        var options = Options.Create(new BusinessBlockOptions());
        var provider = new EmptyServiceProvider();
        var logger = NullLogger<BusinessBlockController>.Instance;

        // Warm code paths and logger metadata before measuring the retained object graph.
        await using (var warm = new BusinessBlockController(provider, registrations, options, logger))
        {
            for (var index = 0; index < 128; index++)
                warm.ReportFailure("block-0", new InvalidOperationException("warm"), "warm");
        }

        var beforeController = CollectLiveBytes();
        var controllerAllocationStart = GC.GetAllocatedBytesForCurrentThread();
        await using var controller = new BusinessBlockController(provider, registrations, options, logger);
        var controllerAllocated = GC.GetAllocatedBytesForCurrentThread() - controllerAllocationStart;
        var afterController = CollectLiveBytes();
        var controllerRetained = Math.Max(0, afterController - beforeController);

        const int historyCount = 128;
        var error = new InvalidOperationException("memory probe");
        const string operation = "memory-probe";
        var beforeHistory = CollectLiveBytes();
        var historyAllocationStart = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < historyCount; index++)
        {
            controller.ReportFailure("block-0", error, operation);
        }
        var historyAllocated = GC.GetAllocatedBytesForCurrentThread() - historyAllocationStart;
        var afterHistory = CollectLiveBytes();
        var historyRetained = Math.Max(0, afterHistory - beforeHistory);

        var snapshot = controller.Read();
        Assert.Equal(128, snapshot.HistoryLimit);
        Assert.Equal(historyCount, snapshot.History.Count);
        Assert.All(snapshot.History, transition => Assert.Equal("block-0", transition.BlockId));

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
        public Task<BusinessBlockResult> RunAsync(CancellationToken stopNotification)
            => throw new InvalidOperationException("The memory test must not construct or run business blocks.");
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BusinessBlockMemoryCollection
{
    public const string Name = "Business block memory measurement";
}
