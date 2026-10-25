using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Application.PublicServices.AiGateway;
using ResourceManager.App.Application.PublicServices;
using ResourceManager.App.Application.PublicServices.AiModels;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization.FreedomPoints;
using ResourceManager.App.Domain.PublicServices.AiModels;
using ResourceManager.App.Domain.PublicServices;
using ResourceManager.App.Domain.PublicServices.AiGateway;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.App.Infrastructure.Optimization.Transactions;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.PublicServices.AiGateway;
using ResourceManager.App.Application.Indexing;

namespace Resource_Manager_APP.Tests;

public sealed partial class LoopbackApiAccessTests
{
    private const string Scope = "/api/optimization/smart/validation/process-effect-scope";

    [Fact]
    public async Task FreedomPointsReadsTheCurrentCompiledTreeWithoutSamplingOrCompiling()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Client.DefaultRequestHeaders.Add(LoopbackApiAccessToken.HeaderName, fixture.Token);
        fixture.Administrator.ThrowIfCalled = true;
        var plan = HostManagerTestPlanFactory.CreatePlan();
        fixture.Runtime.Publish(fixture.Runtime.Current with { HostManager = plan });
        for (var read = 0; read < 10; read++)
        {
            using var response = await fixture.Client.GetAsync("/api/debug/freedom-points");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var tree = (await response.Content.ReadFromJsonAsync<CompiledFreedomPointTree>())!;
            Assert.Equal(plan.FreedomPoints.DeclarationSha256, tree.DeclarationSha256);
            Assert.Equal(plan.FreedomPoints.EnumeratePoints().Select(point => (point.Address, point.Status)),
                tree.EnumeratePoints().Select(point => (point.Address, point.Status)));
            var welfare = Assert.Single(tree.EnumeratePoints(), point =>
                point.Address == BackendFreedomPointPaths.SoftwareWelfare);
            Assert.Equal("active", welfare.Status);
            Assert.Equal("free_capacity_welfare_bonus",
                welfare.Value!.Value.GetProperty("kind").GetString());
            Assert.Equal(100, welfare.Value.Value.GetProperty("base_score").GetDouble());
            var pending = Assert.Single(tree.EnumeratePoints(), point => point.Id == "score_algorithm");
            Assert.Equal("pending", pending.Status);
            Assert.NotEmpty(pending.PendingReason!);
            Assert.Null(pending.Value);
            Assert.Same(plan, fixture.Runtime.Current.HostManager);
        }

        var changed = HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["profile_revision"] = root["profile_revision"]!.GetValue<int>() + 1;
            root["hot_publish"]!["smart_coordinator"]!["required_consecutive_decisions"] = 4;
        }, editFreedomPoints: root => FreedomPointTestFactory.Point(root,
            BackendFreedomPointPaths.SoftwareWelfare)["value"]!["base_score"] = 35);
        fixture.Runtime.Publish(fixture.Runtime.Current with { HostManager = changed });
        var updated = (await fixture.Client.GetFromJsonAsync<CompiledFreedomPointTree>(
            "/api/debug/freedom-points"))!;
        Assert.Equal(4, Assert.Single(updated.EnumeratePoints(),
            point => point.Id == "required_consecutive_decisions").Value!.Value.GetInt32());
        Assert.Equal(35, Assert.Single(updated.EnumeratePoints(),
            point => point.Address == BackendFreedomPointPaths.SoftwareWelfare)
            .Value!.Value.GetProperty("base_score").GetDouble());
        Assert.Same(changed, fixture.Runtime.Current.HostManager);
        Assert.Equal(11, fixture.HttpRequests);
        Assert.Equal(0, fixture.Sampler.Captures);
        Assert.Equal(0, fixture.Administrator.Calls);
    }

    [Fact]
    public async Task HistoryRequirementsReadsCurrentCompiledPlanWithoutSampling()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Client.DefaultRequestHeaders.Add(LoopbackApiAccessToken.HeaderName, fixture.Token);
        fixture.Administrator.ThrowIfCalled = true;
        var plan = HostManagerTestPlanFactory.CreatePlan();
        fixture.Runtime.Publish(fixture.Runtime.Current with { HostManager = plan });
        for (var read = 0; read < 10; read++)
        {
            using var response = await fixture.Client.GetAsync("/api/debug/history/requirements");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var requirements = document.RootElement.GetProperty("requirements").Deserialize<HistoryRequirement[]>(
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.Equal<HistoryRequirement>(plan.DataHistory.Requirements, requirements);
            var consumer = Assert.Single(document.RootElement.GetProperty("consumers").EnumerateArray());
            Assert.Equal("smart_coordinator", consumer.GetProperty("module").GetString());
            Assert.Equal<HistoryRequirement>(plan.SmartCoordinator.DataHistory.Requirements,
                consumer.GetProperty("requirements").Deserialize<HistoryRequirement[]>(
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
        }

        var changed = HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["profile_revision"] = root["profile_revision"]!.GetValue<int>() + 1;
            root["hot_publish"]!["smart_coordinator"]!["required_consecutive_decisions"] = 4;
        });
        fixture.Runtime.Publish(fixture.Runtime.Current with { HostManager = changed });
        using var updated = await fixture.Client.GetAsync("/api/debug/history/requirements");
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var changedDocument = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
        var changedRequirements = changedDocument.RootElement.GetProperty("requirements").Deserialize<HistoryRequirement[]>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Empty(changedRequirements);
        Assert.Equal<HistoryRequirement>(changed.DataHistory.Requirements, changedRequirements);
        Assert.Equal(11, fixture.HttpRequests);
        Assert.Equal(0, fixture.Sampler.Captures);
        Assert.Equal(0, fixture.Administrator.Calls);
    }

    [Fact]
    public async Task BusinessBlocksDiagnosticReadIsAuthenticatedAndDoesNotStartHostedBlocks()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Client.DefaultRequestHeaders.Add(LoopbackApiAccessToken.HeaderName, fixture.Token);
        fixture.Administrator.ThrowIfCalled = true;
        var query = fixture.App.Services.GetRequiredService<IBusinessBlockQuery>();
        var before = query.Read();
        var metricsBefore = before.Blocks.Select(block => new
        {
            block.Id,
            block.Metrics.SampledAt,
            block.Metrics.TotalCpuTime,
            block.Metrics.TotalAllocatedBytes,
            ActiveThreadCount = block.Metrics.ActiveThreads.Count
        }).ToArray();

        using var response = await fixture.Client.GetAsync("/api/debug/business-blocks");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var payloadBlocks = payload.RootElement.GetProperty("blocks").EnumerateArray().ToArray();
        var after = query.Read();
        Assert.Equal(before.History.ToArray(), after.History.ToArray());
        Assert.Equal(after.History.Count, payload.RootElement.GetProperty("history").GetArrayLength());
        Assert.Equal(before.Blocks.Select(block => block.Id).OrderBy(id => id, StringComparer.Ordinal),
            after.Blocks.Select(block => block.Id).OrderBy(id => id, StringComparer.Ordinal));
        Assert.All(after.Blocks, static block =>
        {
            Assert.Equal(BusinessBlockState.Stopped, block.State);
            Assert.False(block.ExecutionOutstanding);
        });
        Assert.Equal(metricsBefore, after.Blocks.Select(block => new
        {
            block.Id,
            block.Metrics.SampledAt,
            block.Metrics.TotalCpuTime,
            block.Metrics.TotalAllocatedBytes,
            ActiveThreadCount = block.Metrics.ActiveThreads.Count
        }).ToArray());
        Assert.Equal(after.Blocks.Select(block => block.Id).OrderBy(id => id, StringComparer.Ordinal),
            payloadBlocks.Select(block => block.GetProperty("id").GetString()!)
                .OrderBy(id => id, StringComparer.Ordinal));
        Assert.All(payloadBlocks, static block =>
        {
            Assert.Equal((int)BusinessBlockState.Stopped, block.GetProperty("state").GetInt32());
            Assert.False(block.GetProperty("executionOutstanding").GetBoolean());
        });
        Assert.Equal(0, fixture.Administrator.Calls);
        Assert.Equal(0, fixture.Sampler.Captures);
    }

    [Fact]
    public async Task DecisionSubscription_RequiresAuthentication()
    {
        await using var fixture = await Fixture.StartAsync();
        using var response = await fixture.Client.GetAsync(
            "/api/optimization/smart/diagnostics/decision-snapshot/subscribe");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, fixture.Sampler.Captures);
    }

    [Fact]
    public async Task DecisionSubscription_PushesTwoSnapshotsWithoutClientPollingOrSampling()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Administrator.ThrowIfCalled = true;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/api/optimization/smart/diagnostics/decision-snapshot/subscribe");
        request.Headers.Add("X-Resource-Manager-Token", fixture.Token);
        using var response = await fixture.Client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"Subscription returned {response.StatusCode}: {fixture.LastRequestError}");
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
        using var first = JsonDocument.Parse((await stream.ReadLineAsync(deadline.Token))!);
        using var second = JsonDocument.Parse((await stream.ReadLineAsync(deadline.Token))!);
        Assert.Equal("decision-snapshot", first.RootElement.GetProperty("subscriptionId").GetString());
        Assert.Equal("resource-manager-smart-decision-diagnostics-v2",
            second.RootElement.GetProperty("value").GetProperty("contract").GetString());
        Assert.Equal("0", second.RootElement.GetProperty("value")
            .GetProperty("schedulingAuthority").GetProperty("roundNumber").GetString());
        Assert.Equal("unavailable", second.RootElement.GetProperty("value")
            .GetProperty("schedulingAuthority").GetProperty("availability").GetString());
        var firstTime = first.RootElement.GetProperty("value").GetProperty("capturedAtUtc").GetDateTimeOffset();
        var secondTime = second.RootElement.GetProperty("value").GetProperty("capturedAtUtc").GetDateTimeOffset();
        Assert.True(secondTime > firstTime);
        Assert.Equal(1, fixture.HttpRequests);
        Assert.Equal(0, fixture.Sampler.Captures);
        Assert.Equal(0, fixture.Administrator.Calls);
    }

    [Theory]
    [InlineData("GET", "/api/public/v1/ai/compat/openai/v1/models", "/v1/models")]
    [InlineData("POST", "/api/public/v1/ai/compat/openai/v1/chat/completions", "/v1/chat/completions")]
    [InlineData("POST", "/api/public/v1/ai/compat/openai/v1/embeddings", "/v1/embeddings")]
    [InlineData("POST", "/api/public/v1/ai/compat/openai/v1/responses", "/v1/responses")]
    [InlineData("POST", "/api/public/v1/ai/compat/anthropic/v1/messages", "/v1/messages")]
    public async Task AiCompatibilityRequiresActiveKeyForItsProtocol(string method, string path, string providerPath)
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Administrator.ThrowIfCalled = true;
        var profile = path.Contains("/anthropic/", StringComparison.Ordinal)
            ? AiGatewayCompatibilityProfiles.Anthropic : AiGatewayCompatibilityProfiles.OpenAi;
        var key = await fixture.Credentials.CreateAsync(new("HTTP client", profile), CancellationToken.None);
        using var missing = await fixture.Client.SendAsync(CreateAiRequest(method, path, profile, null));
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        using var wrong = await fixture.Client.SendAsync(CreateAiRequest(method, path, profile, "invalid"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var other = await fixture.Credentials.CreateAsync(new("Other protocol", profile == "openai" ? "anthropic" : "openai"), CancellationToken.None);
        using var mismatched = await fixture.Client.SendAsync(CreateAiRequest(method, path, profile, other.ApiKey));
        Assert.Equal(HttpStatusCode.Unauthorized, mismatched.StatusCode);
        Assert.Equal(0, fixture.Models.ForwardCalls);
        using var request = CreateAiRequest(method, path, profile, key.ApiKey);
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(providerPath, await response.Content.ReadAsStringAsync());
        Assert.True(await fixture.Credentials.RevokeAsync(key.Credential.Id, CancellationToken.None));
        using var revoked = await fixture.Client.SendAsync(CreateAiRequest(method, path, profile, key.ApiKey));
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.Equal(1, fixture.Models.ForwardCalls);
        Assert.Equal(5, fixture.PublicAccess.Evaluations);
        Assert.Equal(5, fixture.PublicAccess.Completions);
        Assert.Equal(0, fixture.Administrator.Calls);
    }

    [Theory]
    [InlineData(503, "service-disabled")]
    [InlineData(403, "loopback-only")]
    [InlineData(429, "rate-limited")]
    public async Task PublicAccessRejectionDoesNotEnterAiHandler(int status, string reason)
    {
        await using var fixture = await Fixture.StartAsync();
        var key = await fixture.Credentials.CreateAsync(new("client", "openai"), CancellationToken.None);
        fixture.PublicAccess.Rejection = new(false, status, reason, 0);
        using var response = await fixture.Client.SendAsync(CreateAiRequest("POST",
            "/api/public/v1/ai/compat/openai/v1/chat/completions", "openai", key.ApiKey));
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Contains(reason, await response.Content.ReadAsStringAsync());
        Assert.Equal(0, fixture.Models.ForwardCalls);
        Assert.Equal(1, fixture.PublicAccess.Evaluations);
        Assert.Equal(0, fixture.PublicAccess.Completions);
    }

    [Fact]
    public async Task PublicRequestCompletesAfterInvalidBodyAndProviderFailure()
    {
        await using var fixture = await Fixture.StartAsync();
        var key = await fixture.Credentials.CreateAsync(new("client", "openai"), CancellationToken.None);
        const string path = "/api/public/v1/ai/compat/openai/v1/chat/completions";
        using var invalid = CreateAiRequest("POST", path, "openai", key.ApiKey);
        invalid.Content = new StringContent("invalid JSON");
        using var rejected = await fixture.Client.SendAsync(invalid);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        fixture.Models.FailForward = true;
        using var failure = await fixture.Client.SendAsync(CreateAiRequest("POST", path, "openai", key.ApiKey));
        Assert.Equal(HttpStatusCode.BadGateway, failure.StatusCode);
        Assert.Equal(2, fixture.PublicAccess.Completions);
    }

    private static HttpRequestMessage CreateAiRequest(string method, string path, string profile, string? key)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (key is not null)
            request.Headers.Add(profile == "anthropic" ? "x-api-key" : "Authorization",
                profile == "anthropic" ? key : "Bearer " + key);
        if (method == "POST") request.Content = JsonContent.Create(new { model = "test-model" });
        return request;
    }

    [Fact]
    public async Task OpenSubscription_PushesSuccessiveCurrentValuesFromOneAnonymousRequest()
    {
        await using var topology = new CpuTopologyProviderTestFixture();
        await using var fixture = await Fixture.StartAsync(topology.Reader);
        fixture.Administrator.ThrowIfCalled = true;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        {
            Content = JsonContent.Create(new
            {
                version = 1,
                subscriptions = new[] { new { id = "cpu", path = "/api/cpu/topology/subscribe?intervalMs=100" } }
            })
        };
        var responseTask = fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        try
        {
            while (topology.ReadDemand().SourceCount != 1)
                await Task.Delay(10, deadline.Token);
            await topology.Reader.StartAsync(CancellationToken.None);
            Assert.Equal(1, await topology.Sampler.WaitForCaptureAsync());
            topology.Sampler.Complete(new InvalidOperationException("No current sample."));
            using var response = await responseTask;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
            using var empty = JsonDocument.Parse((await stream.ReadLineAsync(deadline.Token))!);
            Assert.Equal(JsonValueKind.Null, empty.RootElement.GetProperty("value").ValueKind);
            Assert.Equal(2, await topology.Sampler.WaitForCaptureAsync());
            topology.Sampler.Complete(CpuTopologyProviderTestFixture.CreateSnapshot(34));
            using var value = JsonDocument.Parse((await stream.ReadLineAsync(deadline.Token))!);
            Assert.Equal(34, value.RootElement.GetProperty("value").GetProperty("logicalProcessors")[0]
                .GetProperty("usagePercent").GetDouble());
            Assert.Equal(1, fixture.HttpRequests);
            Assert.Equal(0, fixture.Administrator.Calls);
        }
        finally
        {
            await deadline.CancelAsync();
        }
    }

    [Fact]
    public async Task OpenReadsDoNotInspectIdentityButModeChangesRequireIt()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Administrator.ThrowIfCalled = true;
        fixture.Client.DefaultRequestHeaders.Add(LoopbackApiAccessToken.HeaderName, "not-a-token");
        using var capabilities = await fixture.Client.GetAsync("/api/runtime/capabilities");
        Assert.Equal(HttpStatusCode.OK, capabilities.StatusCode);
        using var status = await fixture.Client.GetAsync("/api/optimization/smart/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(0, fixture.Administrator.Calls);
        fixture.Administrator.ThrowIfCalled = false;
        using var mode = await fixture.Client.PostAsJsonAsync(
            "/api/optimization/smart/mode", new { mode = "smart" });
        Assert.Equal(HttpStatusCode.Unauthorized, mode.StatusCode);
        Assert.NotEqual("smart", fixture.Effects.Mode);
    }

    [Theory]
    [InlineData("Origin", "https://untrusted.example")]
    [InlineData("Origin", "null")]
    [InlineData("Host", "untrusted.example")]
    public async Task BrowserRequestsCannotBypassHostOrOriginBoundary(string header, string value)
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Client.DefaultRequestHeaders.Add(LoopbackApiAccessToken.HeaderName, fixture.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/optimization/smart/run-once");
        request.Headers.TryAddWithoutValidation(header, value);
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, fixture.Administrator.Calls);
    }

    [Theory]
    [InlineData("GET", Scope)]
    [InlineData("POST", Scope + "/open")]
    [InlineData("POST", Scope + "/close")]
    [InlineData("GET", "/api/optimization/smart/validation/memory-cleanup-evidence")]
    [InlineData("PATCH", "/api/settings/app")]
    [InlineData("POST", "/api/system/processes/terminate")]
    [InlineData("POST", "/api/public/v1/sqlite/databases/test/execute")]
    [InlineData("GET", "/api/debug/metrics/capture")]
    [InlineData("GET", "/api/debug/history/requirements")]
    [InlineData("GET", "/api/debug/freedom-points")]
    [InlineData("GET", "/api/debug/business-blocks")]
    [InlineData("GET", "/api/index/files/search?query=private")]
    [InlineData("GET", "/api/index/software/test")]
    [InlineData("GET", "/api/public/v1/index/files/search?query=private")]
    [InlineData("GET", "/api/public/v1/index/software/test")]
    public async Task AuthenticatedRoutes_RejectOrdinaryAndForgedCallers(string method, string path)
    {
        await using var fixture = await Fixture.StartAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add(LoopbackApiAccessToken.HeaderName, "wrong-instance-token");
        request.Headers.Add("X-Is-Administrator", "true");
        request.Headers.Add("X-Windows-Sid", "S-1-5-18");
        request.Headers.Add("X-Is-Frontend", "true");
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("frontend-or-administrator-required", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, fixture.Administrator.Calls);
        Assert.Equal(0, fixture.Effects.ValidationCalls);
        Assert.Equal(0, fixture.Sampler.Captures);
    }

    [Theory]
    [InlineData("frontend")]
    [InlineData("administrator")]
    public async Task AuthenticatedRoutesAcceptFrontendOrAdministrator(string admission)
    {
        await using var fixture = await Fixture.StartAsync();
        if (admission == "frontend")
            fixture.Client.DefaultRequestHeaders.Add(LoopbackApiAccessToken.HeaderName, fixture.Token);
        fixture.Administrator.Allowed = admission == "administrator";
        fixture.Administrator.ThrowIfCalled = admission != "administrator";
        fixture.Runtime.SetDebug(admission == "debug");

        using var read = await fixture.Client.GetAsync(Scope);
        using var open = await fixture.Client.PostAsJsonAsync(Scope + "/open", new
        {
            runNonce = Guid.NewGuid(), jobName = "test-only", expiresAt = DateTimeOffset.UtcNow,
            releaseToken = "test-only", allowedProcesses = Array.Empty<object>()
        });
        using var close = await fixture.Client.PostAsJsonAsync(Scope + "/close", new
        {
            scopeId = Guid.NewGuid(), runNonce = Guid.NewGuid(), releaseToken = "test-only"
        });
        using var evidence = await fixture.Client.GetAsync(
            "/api/optimization/smart/validation/memory-cleanup-evidence");
        using var capture = await fixture.Client.GetAsync("/api/debug/metrics/capture");
        foreach (var response in new[] { read, open, close, evidence, capture })
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4, fixture.Effects.ValidationCalls);
        Assert.Equal(1, fixture.Sampler.Captures);
        Assert.Equal(admission == "administrator" ? 5 : 0, fixture.Administrator.Calls);
    }

    [Fact]
    public async Task DebugSwitchDoesNotBypassAuthentication()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Runtime.SetDebug(true);
        using var anonymous = await fixture.Client.GetAsync(Scope);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        fixture.Client.DefaultRequestHeaders.Add(LoopbackApiAccessToken.HeaderName, fixture.Token);
        fixture.Administrator.ThrowIfCalled = true;
        using var authorized = await fixture.Client.GetAsync(Scope);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        Assert.Equal(1, fixture.Effects.ValidationCalls);
        Assert.Equal(1, fixture.Administrator.Calls);
    }

    [Fact]
    public async Task AuthenticatedDiagnosticsStillRejectInvalidPayloads()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Runtime.SetDebug(true);
        fixture.Client.DefaultRequestHeaders.Add(LoopbackApiAccessToken.HeaderName, fixture.Token);
        fixture.Administrator.ThrowIfCalled = true;
        using var response = await fixture.Client.PostAsJsonAsync(
            Scope + "/close", new { scopeId = "not-a-guid" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, fixture.Effects.ValidationCalls);
        Assert.Equal(0, fixture.Administrator.Calls);
    }

    [Theory]
    [InlineData("GET", "/api/metrics/catalog", true)]
    [InlineData("GET", "/api/metrics/snapshot", true)]
    [InlineData("GET", "/api/settings/app", true)]
    [InlineData("POST", "/api/subscriptions/stream", true)]
    [InlineData("POST", "/api/optimization/smart/mode", false)]
    [InlineData("POST", "/api/optimization/smart/run-once", false)]
    [InlineData("GET", "/api/debug/amd-smu", false)]
    [InlineData("GET", "/api/debug/gpu-control", false)]
    [InlineData("GET", "/api/public/v1/resources", true)]
    [InlineData("GET", "/api/index/files/search", false)]
    [InlineData("GET", "/api/index/software/{softwareId}", false)]
    [InlineData("GET", "/api/public/v1/index/files/search", false)]
    [InlineData("GET", "/api/public/v1/index/software/{softwareId}", false)]
    [InlineData("POST", "/api/public/v1/ai/compat/openai/v1/chat/completions", true)]
    [InlineData("GET", "/api/debug/metrics/capture", false)]
    [InlineData("GET", "/api/debug/history/requirements", false)]
    [InlineData("GET", "/api/debug/freedom-points", false)]
    [InlineData("GET", "/api/debug/business-blocks", false)]
    [InlineData("GET", "/api/optimization/smart/diagnostics/decision-snapshot/subscribe", false)]
    [InlineData("GET", Scope, false)]
    [InlineData("PUT", "/api/settings/app", false)]
    [InlineData("PATCH", "/api/settings/app", false)]
    [InlineData("POST", "/api/components/{id}/install", false)]
    [InlineData("POST", "/api/system/processes/terminate", false)]
    [InlineData("POST", "/api/public/v1/sqlite/databases/{databaseId}/execute", false)]
    public async Task ProductionRoutes_DeclareTheirGroup(string method, string path, bool open)
    {
        await using var fixture = await Fixture.StartAsync();
        var endpoint = Assert.Single(((IEndpointRouteBuilder)fixture.App).DataSources
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText == path
                && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));
        Assert.Equal(open, endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "rm-api-" + Guid.NewGuid().ToString("N"));
        public WebApplication App { get; private set; } = null!;
        public HttpClient Client { get; private set; } = null!;
        public RuntimePlan Runtime { get; } = new();
        public AdministratorReader Administrator { get; } = new();
        public EffectRecorder Effects { get; } = new();
        public Sampler Sampler { get; } = new();
        public PublicAccessPolicy PublicAccess { get; } = new();
        public AiGatewayCredentialService Credentials { get; } = new(new CredentialStore());
        public AiModelService Models { get; } = new();
        public LoopbackApiAccessToken Tokens { get; private set; } = new(_ => true, (_, _) => true);
        public int HttpRequests;
        public Exception? LastRequestError;
        private string? token;
        public string Token => token ??= Tokens.Issue(new TrustedAdapterCallerIdentity(
            1, 1, 7, 1, "test-sid", 1, 1, 1, false, Path.GetFullPath("fixture.exe"), default, 1));

        public static async Task<Fixture> StartAsync(ICpuTopologyReader? topology = null, ISoftwareFileIndex? fileIndex = null,
            bool actualSessionCredentials = false)
        {
            Assert.True(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RESOURCE_MANAGER_PACKAGE_ROOT")));
            var fixture = new Fixture();
            Directory.CreateDirectory(fixture.root);
            try
            {
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions
                {
                    ContentRootPath = fixture.root, EnvironmentName = Environments.Production
                });
                if (actualSessionCredentials) fixture.Tokens = new LoopbackApiAccessToken(builder.Environment);
                builder.Logging.ClearProviders();
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Services.AddResourceManagerApp(["--no-native-ui"], StartupCapabilitySet.Full);
                builder.Services.RemoveAll<IHostedService>();
                var pipeline = LoopbackApiPipelineConfigurator.Compile(processIsAdministrator: true);
                pipeline.ConfigureServices(builder.Services);
                builder.Services.RemoveAll<IHostedService>();
                builder.Services.AddSingleton(fixture.Tokens);
                builder.Services.AddSingleton<IRuntimePlanProvider>(fixture.Runtime);
                builder.Services.AddSingleton(provider =>
                {
                    var retirements = new HostManagerAuthorityRetirementManager(
                        GpuWindowLedgerTestData.NewRoot("api-retirement"),
                        WindowsHostManagerAuthorityRetirementStorage.Instance);
                    return new HostManagerNativeActionTransactionRuntime(
                        provider.GetRequiredService<HostManagerTransactionJournalDeploymentRuntime>(),
                        new HostManagerAppliedOwnershipRuntime(
                            provider.GetRequiredService<HostManagerAppliedOwnershipDeploymentRuntime>(), retirements,
                            provider.GetRequiredService<ResourceManager.App.Infrastructure.Adaptation.ResourceManagerSelfLocalResourceManager>().CreateScratch),
                        retirements, provider.GetRequiredService<ResourceManager.App.Infrastructure.Adaptation.ResourceManagerSelfLocalResourceManager>().CreateScratch);
                });
                builder.Services.AddSingleton<ILoopbackApiAdministratorReader>(fixture.Administrator);
                builder.Services.AddSingleton<IHostManagerSmartCoordinator>(fixture.Effects);
                builder.Services.AddSingleton<IHostManagerProcessEffectValidationScopeControl>(fixture.Effects);
                builder.Services.AddSingleton<IHostManagerMemoryCleanupValidationEvidenceControl>(fixture.Effects);
                builder.Services.AddSingleton<IMetricSampler>(fixture.Sampler);
                builder.Services.AddSingleton<ILocalAiModelService>(fixture.Models);
                builder.Services.AddSingleton<IAiGatewayLocalModelPolicy, AiModelPolicy>();
                builder.Services.AddSingleton<IAiGatewayCredentialService>(fixture.Credentials);
                builder.Services.AddSingleton<ILocalPublicServiceAccessPolicy>(fixture.PublicAccess);
                if (topology is not null)
                    builder.Services.AddSingleton(topology);
                if (fileIndex is not null)
                    builder.Services.AddSingleton(fileIndex);
                fixture.App = builder.Build();
                fixture.App.Use(async (context, next) =>
                {
                    Interlocked.Increment(ref fixture.HttpRequests);
                    try { await next(context); }
                    catch (Exception error) { fixture.LastRequestError = error; throw; }
                });
                pipeline.ConfigureApplication(fixture.App);
                fixture.App.MapResourceManagerEndpoints(pipeline.Mode, StartupCapabilitySet.Full);
                await fixture.App.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
                fixture.Client = new HttpClient
                {
                    BaseAddress = new Uri(Assert.Single(fixture.App.Services.GetRequiredService<IServer>()
                        .Features.Get<IServerAddressesFeature>()!.Addresses)),
                    Timeout = TimeSpan.FromSeconds(10)
                };
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Client?.Dispose();
            if (App is not null)
            {
                await App.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
                await App.DisposeAsync();
            }
            // Only this fixture's temporary token directory is removed.
            if (Directory.Exists(root))
            {
                if (Directory.Exists(Path.Combine(root, "Config", "Runtime")))
                {
                    foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "Config", "Runtime"))) File.Delete(path);
                    Directory.Delete(Path.Combine(root, "Config", "Runtime"));
                }
                if (Directory.Exists(Path.Combine(root, "Config"))) Directory.Delete(Path.Combine(root, "Config"));
                Directory.Delete(root);
            }
        }
    }

    private sealed class RuntimePlan : IRuntimePlanProvider
    {
        public CompiledRuntimePlan Current { get; private set; } = CompiledRuntimePlan.Default;
        public void SetDebug(bool enabled) => Publish(Current with
        {
            Diagnostics = Current.Diagnostics with { DebugModeEnabled = enabled }
        });
        public RuntimePlanPublicationLease AcquirePublicationLease()
            => RuntimePlanPublicationLease.CreateUntracked(Current, 0);
        public RuntimePlanPublicationResult Publish(CompiledRuntimePlan plan)
        {
            Current = plan;
            return new(plan, 0, []);
        }
    }

    private sealed class AdministratorReader : ILoopbackApiAdministratorReader
    {
        public int Calls { get; private set; }
        public bool Allowed { get; set; }
        public bool ThrowIfCalled { get; set; }
        public bool IsAdministrator(HttpContext context)
        {
            Calls++;
            Assert.False(ThrowIfCalled, "The request must not inspect the caller.");
            return Allowed;
        }
    }

    private sealed class Sampler : IMetricSampler
    {
        public int Captures { get; private set; }
        public Task<HardwareMetricSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
            => Task.FromResult<HardwareMetricSnapshot>(null!);
        public Task<HardwareMetricSnapshot> GetSnapshotAsync(MetricSampleRequest request, CancellationToken cancellationToken)
            => GetSnapshotAsync(cancellationToken);
        public Task<HardwareMetricSnapshot> CaptureSnapshotAsync(MetricSampleRequest request, CancellationToken cancellationToken)
        {
            Captures++;
            return GetSnapshotAsync(cancellationToken);
        }
    }

    private sealed class EffectRecorder : IHostManagerSmartCoordinator,
        IHostManagerProcessEffectValidationScopeControl, IHostManagerMemoryCleanupValidationEvidenceControl
    {
        public int ValidationCalls { get; private set; }
        public string? Mode { get; private set; }
        public Task<HostManagerSmartCoordinatorStatus> GetStatusAsync(CancellationToken cancellationToken)
            => Task.FromResult<HostManagerSmartCoordinatorStatus>(null!);
        public Task<HostManagerRollbackStateDocument> GetStateAsync(CancellationToken cancellationToken)
            => Task.FromResult<HostManagerRollbackStateDocument>(null!);
        public Task<HostManagerSmartCoordinatorStatus> SetModeAsync(string mode, CancellationToken cancellationToken)
        {
            Mode = mode;
            return GetStatusAsync(cancellationToken);
        }
        public Task<HostManagerSmartCoordinatorStatus> RunOnceAsync(CancellationToken cancellationToken)
            => GetStatusAsync(cancellationToken);
        public Task<HostManagerSmartCoordinatorStatus> RestoreNormalModeAsync(CancellationToken cancellationToken)
            => GetStatusAsync(cancellationToken);
        public Task<HostManagerProcessEffectValidationScopeStatus> GetProcessEffectValidationScopeAsync(CancellationToken cancellationToken)
        {
            ValidationCalls++;
            return Task.FromResult<HostManagerProcessEffectValidationScopeStatus>(null!);
        }
        public Task<HostManagerProcessEffectValidationScopeStatus> OpenProcessEffectValidationScopeAsync(
            HostManagerProcessEffectValidationScopeOpenRequest request, CancellationToken cancellationToken)
            => GetProcessEffectValidationScopeAsync(cancellationToken);
        public Task<HostManagerProcessEffectValidationScopeStatus> CloseProcessEffectValidationScopeAsync(
            HostManagerProcessEffectValidationScopeCloseRequest request, CancellationToken cancellationToken)
            => GetProcessEffectValidationScopeAsync(cancellationToken);
        public Task<HostManagerMemoryCleanupValidationEvidenceSnapshot> GetMemoryCleanupValidationEvidenceAsync(CancellationToken cancellationToken)
        {
            ValidationCalls++;
            return Task.FromResult<HostManagerMemoryCleanupValidationEvidenceSnapshot>(null!);
        }
    }

    private sealed class AiModelPolicy : IAiGatewayLocalModelPolicy
    {
        public Task<bool> IsAllowedAsync(string model, CancellationToken cancellationToken)
            => Task.FromResult(model == "test-model");
    }

    private sealed class AiModelService : ILocalAiModelService
    {
        public int ForwardCalls { get; private set; }
        public bool FailForward { get; set; }
        public Task<AiModelRuntimeStatus> GetStatusAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<AiModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AiModelLoadResult> LoadAsync(AiModelLoadRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AiModelUnloadResult> UnloadAsync(AiModelUnloadRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ForwardAsync(HttpContext context, string relativePath, CancellationToken cancellationToken)
        {
            ForwardCalls++;
            if (FailForward) throw new HttpRequestException("Simulated provider failure.");
            return context.Response.WriteAsync(relativePath, cancellationToken);
        }
    }

    private sealed class CredentialStore : IAiGatewayCredentialStore
    {
        private readonly List<StoredAiGatewayCredential> values = [];
        public Task<IReadOnlyList<StoredAiGatewayCredential>> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<StoredAiGatewayCredential>>(values.ToArray());
        public Task AddAsync(StoredAiGatewayCredential credential, CancellationToken cancellationToken)
        { values.Add(credential); return Task.CompletedTask; }
        public Task<bool> DeleteAsync(string credentialId, CancellationToken cancellationToken)
            => Task.FromResult(values.RemoveAll(item => item.Id == credentialId) > 0);
    }

    private sealed class PublicAccessPolicy : ILocalPublicServiceAccessPolicy
    {
        private readonly HashSet<ulong> pending = [];
        public int Evaluations { get; private set; }
        public int Completions { get; private set; }
        public LocalPublicServiceAccessDecision? Rejection { get; set; }
        public Task<LocalPublicServiceAccessDecision> EvaluateAsync(HttpContext context, CancellationToken cancellationToken)
        {
            var handle = checked((ulong)++Evaluations);
            if (Rejection is not null) return Task.FromResult(Rejection);
            Assert.True(pending.Add(handle));
            return Task.FromResult(new LocalPublicServiceAccessDecision(true, 200, "allowed", handle));
        }
        public ValueTask CompleteAsync(ulong completionHandle, CancellationToken cancellationToken)
        {
            Assert.True(pending.Remove(completionHandle));
            Completions++;
            return ValueTask.CompletedTask;
        }
    }
}
