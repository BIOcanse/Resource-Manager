using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Security;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Theory]
    [InlineData("disconnect")]
    [InlineData("delete")]
    [InlineData("revoke")]
    [InlineData("keyless")]
    [InlineData("stopped")]
    public async Task CpuLeaseHttpUsesExistingSubscriptionDisconnectAndKeyRevocationLifetime(string end)
    {
        const int pid = 53201;
        const ulong birth = 133900000000000201;
        var topology = CreateAutomaticPlacementTopology();
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(pid, birth, "probe", 80, 20, 5),
            policyExecutionEnabled: false, automaticMemoryCleanupEnabled: false,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
            cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology), automaticPlacementTopology: topology);
        RecordCpuLeaseBindings(fixture);
        if (end != "stopped") BeginCpuLeaseRunForManualCycles(fixture);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(options => ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options.SerializerOptions));
        var credential = TrustedAccessEndpointTestAuthentication.Add(builder.Services, "machine.write");
        builder.Services.AddSingleton<ILoopbackApiAdministratorReader>(new CpuLeaseFixtureAdministrator());
        builder.Services.AddSingleton<IRuntimeCpuLeaseService>(fixture.Coordinator);
        builder.Services.AddSingleton<IRuntimeCpuLeaseRegistry>(fixture.CpuLeases);
        await using var app = TrustedAccessEndpointTestAuthentication.Build(builder);
        ResourceManagerEndpointRouteBuilderExtensions.MapCpuLeaseEndpoints(app);
        await app.StartAsync();
        try
        {
            using var client = TrustedAccessEndpointTestAuthentication.Client(app);
            if (end == "keyless") client.DefaultRequestHeaders.Remove(ResourceManager.Shared.Runtime.TrustedAccess.TrustedAccessHeaders.KeyHeader);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/cpu/leases")
            { Content = JsonContent.Create(new RuntimeCpuLeaseRequest(new(ProcessId: pid, ProcessStartKey: birth), ["core:0"], true, false)) };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
            using var line = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
            var id = line.RootElement.GetProperty("leaseId").GetString()!;
            Assert.Equal(end == "stopped" ? "unavailable" : "pending", line.RootElement.GetProperty("state").GetString());
            if (end == "stopped")
            {
                Assert.Equal("scheduler-stopped", line.RootElement.GetProperty("reasonCode").GetString());
                BeginCpuLeaseRunForManualCycles(fixture);
            }
            Assert.Empty(fixture.StateStore.Current.AppliedPlacements);
            await fixture.Coordinator.RunOnceAsync(timeout.Token);
            using var currentResponse = await client.GetAsync("/api/cpu/leases/" + id, timeout.Token);
            currentResponse.EnsureSuccessStatusCode();
            var current = await currentResponse.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
            Assert.Equal("observed", current.GetProperty("state").GetString());
            Assert.Equal(birth.ToString(System.Globalization.CultureInfo.InvariantCulture), current.GetProperty("effects")[0].GetProperty("processStartKey").GetString());
            Assert.True(current.GetProperty("effects")[0].GetProperty("changedByManager").GetBoolean());
            if (end == "delete")
            {
                using var deleted = await client.DeleteAsync("/api/cpu/leases/" + id, timeout.Token);
                Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            }
            else if (end == "revoke") await credential.Owner.RevokeAsync(credential.CredentialId, timeout.Token);
            else response.Dispose();
            var ended = false;
            for (var iteration = 0; iteration < 200; iteration++)
            {
                if (await fixture.Coordinator.ReadCpuLeaseAsync(id, timeout.Token) is null) { ended = true; break; }
                await Task.Delay(10, timeout.Token);
            }
            Assert.True(ended, "The existing request lifetime must withdraw the lease.");
        }
        finally { await app.StopAsync(); }
    }

    private sealed class CpuLeaseFixtureAdministrator : ILoopbackApiAdministratorReader
    {
        public bool IsAdministrator(Microsoft.AspNetCore.Http.HttpContext context) => true;
    }
}
