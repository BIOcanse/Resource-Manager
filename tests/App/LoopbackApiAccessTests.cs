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
using ResourceManager.App.Application.PublicServices.AiGateway;
using ResourceManager.App.Application.PublicServices.AiModels;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization.FreedomPoints;
using ResourceManager.App.Domain.PublicServices.AiModels;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.App.Infrastructure.Optimization.Transactions;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class LoopbackApiAccessTests
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
            Assert.Equal("software_base_mean_utilization_bonus",
                welfare.Value!.Value.GetProperty("kind").GetString());
            Assert.Equal(70, welfare.Value.Value.GetProperty("utilization_baseline_percent").GetDouble());
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
            BackendFreedomPointPaths.SoftwareWelfare)["value"]!["utilization_baseline_percent"] = 35);
        fixture.Runtime.Publish(fixture.Runtime.Current with { HostManager = changed });
        var updated = (await fixture.Client.GetFromJsonAsync<CompiledFreedomPointTree>(
            "/api/debug/freedom-points"))!;
        Assert.Equal(4, Assert.Single(updated.EnumeratePoints(),
            point => point.Id == "required_consecutive_decisions").Value!.Value.GetInt32());
        Assert.Equal(35, Assert.Single(updated.EnumeratePoints(),
            point => point.Address == BackendFreedomPointPaths.SoftwareWelfare)
            .Value!.Value.GetProperty("utilization_baseline_percent").GetDouble());
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
        Assert.Equal("resource-manager-smart-decision-diagnostics-v1",
            second.RootElement.GetProperty("value").GetProperty("contract").GetString());
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
    public async Task OpenAiCompatibility_HasNoSeparateApiKeyGate(string method, string path, string providerPath)
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Administrator.ThrowIfCalled = true;
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
            request.Content = JsonContent.Create(new { model = "test-model" });
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(providerPath, await response.Content.ReadAsStringAsync());
        Assert.Equal(0, fixture.Administrator.Calls);
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
    public async Task OpenReadsAndModeSwitch_DoNotInspectCallerIdentity()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Administrator.ThrowIfCalled = true;
        fixture.Client.DefaultRequestHeaders.Add(LoopbackApiAccessToken.HeaderName, "not-a-token");
        using var capabilities = await fixture.Client.GetAsync("/api/runtime/capabilities");
        Assert.Equal(HttpStatusCode.OK, capabilities.StatusCode);
        using var status = await fixture.Client.GetAsync("/api/optimization/smart/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        using var mode = await fixture.Client.PostAsJsonAsync(
            "/api/optimization/smart/mode", new { mode = "normal" });
        Assert.Equal(HttpStatusCode.OK, mode.StatusCode);
        Assert.Equal("normal", fixture.Effects.Mode);
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
    [InlineData("debug")]
    public async Task AuthenticatedRoutes_AcceptEitherIdentityOrDebug(string admission)
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
    public async Task DebugSwitch_TakesEffectOnTheNextHttpRequest()
    {
        await using var fixture = await Fixture.StartAsync();
        using var before = await fixture.Client.GetAsync(Scope);
        Assert.Equal(HttpStatusCode.Unauthorized, before.StatusCode);
        fixture.Runtime.SetDebug(true);
        fixture.Administrator.ThrowIfCalled = true;
        using var during = await fixture.Client.GetAsync(Scope);
        Assert.Equal(HttpStatusCode.OK, during.StatusCode);
        fixture.Runtime.SetDebug(false);
        fixture.Administrator.ThrowIfCalled = false;
        using var after = await fixture.Client.GetAsync(Scope);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        Assert.Equal(1, fixture.Effects.ValidationCalls);
        Assert.Equal(2, fixture.Administrator.Calls);
    }

    [Fact]
    public async Task DebugBypass_DoesNotTurnInvalidPayloadsIntoSuccessfulOperations()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Runtime.SetDebug(true);
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
    [InlineData("GET", "/api/settings/dashboard", true)]
    [InlineData("POST", "/api/subscriptions/stream", true)]
    [InlineData("POST", "/api/optimization/smart/mode", true)]
    [InlineData("GET", "/api/public/v1/resources", true)]
    [InlineData("POST", "/api/public/v1/ai/compat/openai/v1/chat/completions", true)]
    [InlineData("GET", "/api/debug/metrics/capture", false)]
    [InlineData("GET", "/api/debug/history/requirements", false)]
    [InlineData("GET", "/api/debug/freedom-points", false)]
    [InlineData("GET", "/api/optimization/smart/diagnostics/decision-snapshot", false)]
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
        public int HttpRequests;
        public Exception? LastRequestError;
        public string Token => App.Services.GetRequiredService<LoopbackApiAccessToken>().Token;

        public static async Task<Fixture> StartAsync(ICpuTopologyReader? topology = null)
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
                builder.Logging.ClearProviders();
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Services.AddResourceManagerApp(["--no-native-ui"], StartupCapabilitySet.Full);
                builder.Services.RemoveAll<IHostedService>();
                var pipeline = LoopbackApiPipelineConfigurator.Compile(processIsAdministrator: true);
                pipeline.ConfigureServices(builder.Services);
                builder.Services.AddSingleton<IRuntimePlanProvider>(fixture.Runtime);
                builder.Services.AddSingleton(provider =>
                {
                    var retirements = new HostManagerAuthorityRetirementManager(
                        GpuWindowLedgerTestData.NewRoot("api-retirement"),
                        WindowsHostManagerAuthorityRetirementStorage.Instance);
                    return new HostManagerNativeActionTransactionRuntime(
                        provider.GetRequiredService<HostManagerTransactionJournalDeploymentRuntime>(),
                        new HostManagerAppliedOwnershipRuntime(
                            provider.GetRequiredService<HostManagerAppliedOwnershipDeploymentRuntime>(), retirements),
                        retirements);
                });
                builder.Services.AddSingleton<ILoopbackApiAdministratorReader>(fixture.Administrator);
                builder.Services.AddSingleton<IHostManagerSmartCoordinator>(fixture.Effects);
                builder.Services.AddSingleton<IHostManagerProcessEffectValidationScopeControl>(fixture.Effects);
                builder.Services.AddSingleton<IHostManagerMemoryCleanupValidationEvidenceControl>(fixture.Effects);
                builder.Services.AddSingleton<IMetricSampler>(fixture.Sampler);
                builder.Services.AddSingleton<ILocalAiModelService, AiModelService>();
                builder.Services.AddSingleton<IAiGatewayLocalModelPolicy, AiModelPolicy>();
                if (topology is not null)
                    builder.Services.AddSingleton(topology);
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
                foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "Config", "Runtime")))
                    File.Delete(path);
                Directory.Delete(Path.Combine(root, "Config", "Runtime"));
                Directory.Delete(Path.Combine(root, "Config"));
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
        public Task<AiModelRuntimeStatus> GetStatusAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<AiModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AiModelLoadResult> LoadAsync(AiModelLoadRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AiModelUnloadResult> UnloadAsync(AiModelUnloadRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ForwardAsync(HttpContext context, string relativePath, CancellationToken cancellationToken)
            => context.Response.WriteAsync(relativePath, cancellationToken);
    }
}
