using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Hosting;

namespace Resource_Manager_APP.Tests;

public sealed class BusinessBlockConfigurationTests
{
    [Fact]
    public void HostConfigurationOverridesDefaultsAndCanDisableHistorySamplingAndDeadline()
    {
        using var host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BusinessBlocks:HistoryLimit"] = "0",
                ["BusinessBlocks:MetricsInterval"] = null,
                ["BusinessBlocks:Policy:StopDeadline"] = null,
                ["BusinessBlocks:Policy:OnTimeout"] = "ContinueWaiting",
                ["BusinessBlocks:Policy:OnFailure"] = "StopDependents"
            }))
            .ConfigureServices(services => services.AddBusinessBlockHostedService<ConfiguredService>())
            .Build();

        var facts = host.Services.GetRequiredService<IBusinessBlockQuery>().Read();
        Assert.Equal(0, facts.HistoryLimit);
        Assert.Null(facts.MetricsInterval);
        var block = Assert.Single(facts.Blocks);
        Assert.Null(block.Policy.StopDeadline);
        Assert.Equal(BusinessBlockTimeoutPolicy.ContinueWaiting, block.Policy.OnTimeout);
        Assert.Equal(BusinessBlockFailurePolicy.StopDependents, block.Policy.OnFailure);
        Assert.Equal(BusinessBlockState.Stopped, block.State);
    }

    private sealed class ConfiguredService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
