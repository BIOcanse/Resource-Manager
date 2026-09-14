using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.PublicServices.AiModels;
using ResourceManager.App.Domain.PublicServices.AiModels;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.PublicServices;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerPublicServiceCoordinatorOwnerTests
{
    [Fact]
    public async Task StartupDoesNotProbeTheModelProviderUntilCatalogIsRequested()
    {
        await using var fixture = await Fixture.CreateAsync();
        await Task.Delay(100);
        Assert.Equal(0, fixture.Provider.CatalogCallCount);

        await Assert.ThrowsAsync<AiModelRuntimeUnavailableException>(
            () => fixture.Owner.ListModelsAsync(CancellationToken.None));
        await WaitForCatalogAsync(fixture.Owner);

        Assert.Equal(1, fixture.Provider.CatalogCallCount);
    }

    [Fact]
    public async Task AccessPolicyUsesNativeRoutingAndExactCompletion()
    {
        await using var fixture = await Fixture.CreateAsync();
        var allowed = CreateContext(
            "/api/public/v1/index/status",
            HttpMethods.Get,
            IPAddress.Loopback);

        var decision = await fixture.Owner.EvaluateAsync(
            allowed,
            CancellationToken.None);
        Assert.True(
            decision.Allowed,
            $"Expected the loopback GET route to be admitted, but received {decision.StatusCode} ({decision.Reason}).");
        Assert.NotEqual(0UL, decision.CompletionHandle);
        await fixture.Owner.CompleteAsync(
            decision.CompletionHandle,
            CancellationToken.None);

        var wrongMethod = await fixture.Owner.EvaluateAsync(
            CreateContext(
                "/api/public/v1/index/status",
                HttpMethods.Post,
                IPAddress.Loopback),
            CancellationToken.None);
        Assert.False(wrongMethod.Allowed);
        Assert.Equal(StatusCodes.Status405MethodNotAllowed, wrongMethod.StatusCode);

        var remote = await fixture.Owner.EvaluateAsync(
            CreateContext(
                "/api/public/v1/index/status",
                HttpMethods.Get,
                IPAddress.Parse("192.0.2.1")),
            CancellationToken.None);
        Assert.False(remote.Allowed);
        Assert.Equal(StatusCodes.Status403Forbidden, remote.StatusCode);
    }

    [Fact]
    public async Task ModelLoadRunsThroughNativeCatalogAndTaskQueue()
    {
        await using var fixture = await Fixture.CreateAsync();
        await WaitForCatalogAsync(fixture.Owner);

        var result = await fixture.Owner.LoadAsync(
            new AiModelLoadRequest(
                "community/open-model",
                4096,
                null,
                null,
                null),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("community/open-model", result.InstanceId);
        Assert.Equal(1, fixture.Provider.LoadCallCount);
        Assert.Equal("community/open-model", fixture.Provider.LastLoadedModel);
        Assert.True(await fixture.Owner.IsAllowedAsync(
            "community/open-model",
            CancellationToken.None));
    }

    [Fact]
    public async Task TransientProviderFailureIsRetriedOnlyByTheNativeQueue()
    {
        await using var fixture = await Fixture.CreateAsync(loadFailures: 1);
        await WaitForCatalogAsync(fixture.Owner);

        var result = await fixture.Owner.LoadAsync(
            new AiModelLoadRequest(
                "community/open-model",
                4096,
                null,
                null,
                null),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("community/open-model", result.InstanceId);
        Assert.Equal(2, fixture.Provider.LoadCallCount);
    }

    [Fact]
    public async Task RetryStopsAtTheExplicitNativeAttemptLimit()
    {
        await using var fixture = await Fixture.CreateAsync(loadFailures: int.MaxValue);
        await WaitForCatalogAsync(fixture.Owner);

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await fixture.Owner.LoadAsync(
                new AiModelLoadRequest(
                    "community/open-model",
                    4096,
                    null,
                    null,
                    null),
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(3, fixture.Provider.LoadCallCount);
    }

    [Fact]
    public async Task ProviderEffectPastDeadlineIsNeverExecutedTwice()
    {
        await using var fixture = await Fixture.CreateAsync(
            taskTimeoutMilliseconds: 50,
            holdFirstLoadIgnoringCancellation: true);
        await WaitForCatalogAsync(fixture.Owner);

        var load = fixture.Owner.LoadAsync(
            new AiModelLoadRequest(
                "community/open-model",
                4096,
                null,
                null,
                null),
            CancellationToken.None);
        await fixture.Provider.WaitForLoadCallsAsync(1);
        await Task.Delay(200);
        Assert.Equal(1, fixture.Provider.LoadCallCount);

        fixture.Provider.ReleaseFirstLoad();
        var result = await load.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("community/open-model", result.InstanceId);
        Assert.Equal(1, fixture.Provider.LoadCallCount);
    }

    [Fact]
    public async Task StopCancelsRunningAndQueuedOperationsThenSupportsRestart()
    {
        await using var fixture = await Fixture.CreateAsync(cancelFirstLoad: true);
        await WaitForCatalogAsync(fixture.Owner);

        var first = fixture.Owner.LoadAsync(
            new AiModelLoadRequest(
                "community/open-model",
                4096,
                null,
                null,
                null),
            CancellationToken.None);
        var second = fixture.Owner.LoadAsync(
            new AiModelLoadRequest(
                "community/open-model",
                4096,
                null,
                null,
                null),
            CancellationToken.None);
        await fixture.Provider.WaitForLoadCallsAsync(1);

        await fixture.Owner.StopAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(
            first.IsCompleted,
            $"Running operation remained {first.Status} after Host Manager stop.");
        Assert.True(
            second.IsCompleted,
            $"Queued operation remained {second.Status} after Host Manager stop; provider calls={fixture.Provider.LoadCallCount}.");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await first.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, fixture.Provider.LoadCallCount);

        await fixture.Owner.StartAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForCatalogAsync(fixture.Owner);
        var restarted = await fixture.Owner.LoadAsync(
            new AiModelLoadRequest(
                "community/open-model",
                4096,
                null,
                null,
                null),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("community/open-model", restarted.InstanceId);
        Assert.Equal(2, fixture.Provider.LoadCallCount);
    }

    [Fact]
    public async Task ExpiredRequestCompletionReleasesWorkspaceForReplacement()
    {
        await using var fixture = await Fixture.CreateAsync(
            requestTimeoutMilliseconds: 20);
        var decision = await fixture.Owner.EvaluateAsync(
            CreateContext(
                "/api/public/v1/index/status",
                HttpMethods.Get,
                IPAddress.Loopback),
            CancellationToken.None);
        Assert.True(
            decision.Allowed,
            $"Expected the expiring loopback GET route to be admitted, but received {decision.StatusCode} ({decision.Reason}).");

        await Task.Delay(50);
        await fixture.Owner.CompleteAsync(
            decision.CompletionHandle,
            CancellationToken.None);
        await fixture.Owner.StopAsync(CancellationToken.None);
        fixture.PublishPlan(root =>
        {
            root["profile_revision"] = root["profile_revision"]!.GetValue<int>() + 1;
            root["host_recreate"]!["public_service_coordinator"]![
                "request_timeout_ms"] = 25;
        });
        await fixture.Owner.StartAsync(CancellationToken.None);

        var afterReplacement = await fixture.Owner.EvaluateAsync(
            CreateContext(
                "/api/public/v1/index/status",
                HttpMethods.Get,
                IPAddress.Loopback),
            CancellationToken.None);
        Assert.True(
            afterReplacement.Allowed,
            $"Expected the replacement workspace to admit the loopback GET route, but received {afterReplacement.StatusCode} ({afterReplacement.Reason}).");
        await fixture.Owner.CompleteAsync(
            afterReplacement.CompletionHandle,
            CancellationToken.None);
    }

    private static async Task WaitForCatalogAsync(
        HostManagerPublicServiceCoordinatorOwner owner)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var models = await owner.ListModelsAsync(CancellationToken.None);
                if (models.Count != 0)
                {
                    return;
                }
            }
            catch (AiModelRuntimeUnavailableException)
            {
            }
            await Task.Delay(10);
        }
        throw new TimeoutException("The native model catalog did not become usable.");
    }

    private static DefaultHttpContext CreateContext(
        string path,
        string method,
        IPAddress remoteAddress)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;
        context.Connection.RemoteIpAddress = remoteAddress;
        return context;
    }

    private sealed class Fixture(
        HostManagerPublicServiceCoordinatorOwner owner,
        RecordingModelProvider provider,
        RuntimePlanProvider planProvider) : IAsyncDisposable
    {
        internal HostManagerPublicServiceCoordinatorOwner Owner { get; } = owner;

        internal RecordingModelProvider Provider { get; } = provider;

        internal static async Task<Fixture> CreateAsync(
            int loadFailures = 0,
            int taskTimeoutMilliseconds = 30_000,
            int requestTimeoutMilliseconds = 30_000,
            bool holdFirstLoadIgnoringCancellation = false,
            bool cancelFirstLoad = false)
        {
            var hostPlan = HostManagerTestPlanFactory.CreatePlan(
                root =>
                {
                    var recreate = root["host_recreate"]![
                        "public_service_coordinator"]!;
                    recreate["maximum_concurrent_model_tasks"] = 1;
                    recreate["retry_delay_ms"] = 10;
                    recreate["task_timeout_ms"] = taskTimeoutMilliseconds;
                    recreate["request_timeout_ms"] = requestTimeoutMilliseconds;
                },
                publicService: EnabledPublicServiceSettings);
            Assert.True(
                hostPlan.IsPublished,
                DescribeUnpublishedPlanSections(hostPlan));
            var deployment = new HostManagerDeploymentState();
            var planProvider = new RuntimePlanProvider(deployment);
            planProvider.Publish(CompiledRuntimePlan.Default with
            {
                Version = 1,
                CompiledAt = DateTimeOffset.UtcNow,
                Reason = "public-service-owner-test",
                HostManager = hostPlan
            });
            var provider = new RecordingModelProvider(
                loadFailures,
                holdFirstLoadIgnoringCancellation,
                cancelFirstLoad);
            var owner = new HostManagerPublicServiceCoordinatorOwner(
                planProvider,
                new HostManagerPublicServiceCoordinatorRuntime(
                    planProvider,
                    deployment),
                new HostManagerRuntimeIdentity(),
                provider,
                NullLogger<HostManagerPublicServiceCoordinatorOwner>.Instance);
            await owner.StartAsync(CancellationToken.None);
            return new Fixture(owner, provider, planProvider);
        }

        internal void PublishPlan(Action<JsonObject> mutate)
        {
            var plan = HostManagerTestPlanFactory.CreatePlan(
                mutate,
                publicService: EnabledPublicServiceSettings);
            planProvider.Publish(CompiledRuntimePlan.Default with
            {
                Version = planProvider.Current.Version + 1,
                CompiledAt = DateTimeOffset.UtcNow,
                Reason = "public-service-owner-replacement-test",
                HostManager = plan
            });
        }

        private static string DescribeUnpublishedPlanSections(
            CompiledHostManagerPlan plan)
        {
            var sections = new List<string>();
            AddIfFalse(sections, "schema", plan.SchemaVersion == 22);
            AddIfFalse(sections, "profile-revision", plan.ProfileRevision > 0);
            AddIfFalse(sections, "plan-epoch", plan.PlanEpoch > 0);
            AddIfFalse(sections, "binding", plan.BindingProvenance.IsPublished);
            AddIfFalse(sections, "build", plan.BuildSpecialize.IsPublished);
            AddIfFalse(sections, "digests", plan.DeploymentDigests.IsPublished);
            AddIfFalse(sections, "smart", plan.SmartCoordinator.IsPublished);
            AddIfFalse(sections, "sampling", plan.SamplingSubscription.IsPublished);
            AddIfFalse(sections, "portable", plan.PortableSoftwareRegistry.IsPublished);
            AddIfFalse(sections, "identity-catalog", plan.SoftwareIdentityCatalog.IsPublished);
            AddIfFalse(sections, "identity-resolution", plan.SoftwareIdentityResolution.IsPublished);
            AddIfFalse(sections, "report", plan.ReportCoordinator.IsPublished);
            AddIfFalse(sections, "file-query", plan.FileQuery.IsPublished);
            AddIfFalse(
                sections,
                "public-service-build",
                plan.PublicServiceCoordinator.Build.IsPublished);
            AddIfFalse(
                sections,
                "public-service-recreate",
                plan.PublicServiceCoordinator.Recreate.IsPublished);
            AddIfFalse(
                sections,
                "public-service-hot",
                plan.PublicServiceCoordinator.HotPublish.IsPublished);
            AddIfFalse(
                sections,
                "public-service",
                plan.PublicServiceCoordinator.IsPublished);
            return sections.Count == 0
                ? "The compiled Host Manager plan failed an unlisted publication invariant."
                : $"Unpublished Host Manager sections: {string.Join(", ", sections)}.";
        }

        private static void AddIfFalse(
            ICollection<string> sections,
            string name,
            bool condition)
        {
            if (!condition)
            {
                sections.Add(name);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Owner.StopAsync(CancellationToken.None);
            Owner.Dispose();
        }

        private static AppLocalPublicServiceSettings EnabledPublicServiceSettings { get; }
            = new(
                Enabled: true,
                FileIndexEnabled: true,
                DatabaseServiceEnabled: true,
                AiModelCatalogEnabled: true);
    }

    private sealed class RecordingModelProvider(
        int loadFailures,
        bool holdFirstLoadIgnoringCancellation,
        bool cancelFirstLoad) : IAiModelRuntimeProvider
    {
        private readonly TaskCompletionSource firstLoadStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource firstLoadRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int catalogCallCount;
        private int loadCallCount;
        private int remainingLoadFailures = loadFailures;
        private string? lastLoadedModel;

        internal int CatalogCallCount => Volatile.Read(ref catalogCallCount);

        internal int LoadCallCount => Volatile.Read(ref loadCallCount);

        internal string? LastLoadedModel => Volatile.Read(ref lastLoadedModel);

        internal async Task WaitForLoadCallsAsync(int count)
        {
            if (LoadCallCount >= count)
            {
                return;
            }
            await firstLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(LoadCallCount >= count);
        }

        internal void ReleaseFirstLoad()
            => firstLoadRelease.TrySetResult();

        public Task<AiModelRuntimeStatus> GetStatusAsync(
            CancellationToken cancellationToken)
            => Task.FromResult(new AiModelRuntimeStatus(
                "test",
                "http://127.0.0.1:1",
                true,
                true,
                false,
                null));

        public Task<IReadOnlyList<AiModelDescriptor>> ListModelsIfRunningAsync(
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref catalogCallCount);
            return Task.FromResult<IReadOnlyList<AiModelDescriptor>>(
            [
                new AiModelDescriptor(
                    "llm",
                    "community",
                    "community/open-model",
                    "Open Model",
                    null,
                    "gguf",
                    "Q4_K_M",
                    4,
                    1,
                    "1B",
                    4096,
                    [],
                    [])
            ]);
        }

        public async Task<AiModelLoadResult> LoadAsync(
            AiModelLoadRequest request,
            CancellationToken cancellationToken)
        {
            Volatile.Write(ref lastLoadedModel, request.Model);
            var call = Interlocked.Increment(ref loadCallCount);
            if (call == 1)
            {
                firstLoadStarted.TrySetResult();
                if (holdFirstLoadIgnoringCancellation)
                {
                    await firstLoadRelease.Task.ConfigureAwait(false);
                }
                else if (cancelFirstLoad)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            if (Interlocked.Decrement(ref remainingLoadFailures) >= 0)
            {
                throw new HttpRequestException(
                    "Transient provider failure.",
                    null,
                    HttpStatusCode.ServiceUnavailable);
            }
            return new AiModelLoadResult(
                "llm",
                request.Model,
                0,
                "loaded");
        }

        public Task<AiModelUnloadResult> UnloadAsync(
            AiModelUnloadRequest request,
            CancellationToken cancellationToken)
            => Task.FromResult(new AiModelUnloadResult(request.InstanceId));

        public Task ForwardAsync(
            HttpContext context,
            string relativePath,
            CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
