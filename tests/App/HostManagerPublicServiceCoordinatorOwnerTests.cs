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
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.Ledger;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerPublicServiceCoordinatorOwnerTests
{
    [Fact]
    public async Task PreparedOwnerAdmitsAndSettlesRequestWithoutStartingWorkers()
    {
        await using var fixture = await Fixture.CreateAsync(start: false);
        var changes = new List<bool>();
        fixture.Owner.PendingTasksChanged += () => changes.Add(fixture.Owner.HasPendingTasks);
        Assert.False(fixture.Owner.HasPendingTasks);
        var decision = await fixture.Owner.EvaluateAsync(CreateContext(
            "/api/public/v1/index/status", HttpMethods.Get, IPAddress.Loopback), CancellationToken.None);
        Assert.True(decision.Allowed);
        Assert.True(fixture.Owner.HasPendingTasks);
        await fixture.Owner.StopAsync(CancellationToken.None);
        Assert.True(fixture.Owner.HasPendingTasks);
        await fixture.Owner.CompleteAsync(decision.CompletionHandle, CancellationToken.None);
        Assert.False(fixture.Owner.HasPendingTasks);
        Assert.Equal(new[] { true, false }, changes);
        Assert.Equal(0, fixture.Provider.CatalogCallCount);
    }

    [Fact]
    public async Task InferenceDemandAcquiresCatalogAndUnknownModelsRemainDistinct()
    {
        await using var fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<AiModelRuntimeUnavailableException>(() => fixture.Owner.IsAllowedAsync("community/open-model", default));
        await fixture.Provider.WaitForCatalogCallsAsync(1);
        await WaitForCatalogAsync(fixture.Owner);
        Assert.True(await fixture.Owner.IsAllowedAsync("community/open-model", default));
        Assert.False(await fixture.Owner.IsAllowedAsync("unknown-model", default));
    }

    [Fact]
    public async Task CatalogRefreshUsesNativeDueTimeWithoutAnotherDemand()
    {
        await using var fixture = await Fixture.CreateAsync(catalogIntervalMilliseconds: 50);
        await WaitForCatalogAsync(fixture.Owner);
        await fixture.Provider.WaitForCatalogCallsAsync(2);
        Assert.True(await fixture.Owner.IsAllowedAsync("community/open-model", default));
    }

    [Fact]
    public async Task CatalogDeadlineDoesNotFaultOtherCapabilitiesAndLateResultCannotPublish()
    {
        await using var fixture = await Fixture.CreateAsync(catalogTimeoutMilliseconds: 50);
        var late = new TaskCompletionSource<IReadOnlyList<AiModelDescriptor>>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Provider.FirstCatalog = late.Task;
        await Assert.ThrowsAsync<AiModelRuntimeUnavailableException>(() => fixture.Owner.ListModelsAsync(default));
        await fixture.Provider.WaitForCatalogCallsAsync(2);
        await WaitForCatalogAsync(fixture.Owner);
        late.SetResult([]);
        await Task.Delay(50);
        Assert.True(await fixture.Owner.IsAllowedAsync("community/open-model", default));
        var decision = await fixture.Owner.EvaluateAsync(CreateContext("/api/public/v1/index/status", HttpMethods.Get, IPAddress.Loopback), default);
        Assert.True(decision.Allowed);
        await fixture.Owner.CompleteAsync(decision.CompletionHandle, default);
    }
    [Fact]
    public async Task ReplacementWaitsForTheSinglePendingWorkspaceToFinishItsLastOperation()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var returning = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        fixture.Scratches[0].BeforeReturn = () =>
        {
            returning.Set();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(5)));
        };
        var resolving = Task.Run(() => fixture.Owner.IsAllowedAsync(
            "community/open-model", CancellationToken.None));
        try
        {
            Assert.True(returning.Wait(TimeSpan.FromSeconds(5)));
            PublishReplacement(31_000);
            Assert.Equal(2, fixture.Scratches.Count);
            PublishReplacement(32_000);
            Assert.Equal(2, fixture.Scratches.Count);
        }
        finally
        {
            fixture.Scratches[0].BeforeReturn = null;
            resume.Set();
        }
        await Assert.ThrowsAsync<AiModelRuntimeUnavailableException>(() => resolving.WaitAsync(TimeSpan.FromSeconds(5)));
        await WaitForCatalogAsync(fixture.Owner);
        PublishReplacement(33_000);
        Assert.Equal(3, fixture.Scratches.Count);

        void PublishReplacement(int timeout) => fixture.PublishPlan(root =>
        {
            root["profile_revision"] = root["profile_revision"]!.GetValue<int>() + timeout;
            root["host_recreate"]!["public_service_coordinator"]!["request_timeout_ms"] = timeout;
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedCatalogIsListedAndUsedAfterItsWorkAreaReturnFails(bool failComplete)
    {
        await using var fixture = await Fixture.CreateAsync(
            failCatalogReturn: !failComplete, failCatalogComplete: failComplete);
        await WaitForCatalogAsync(fixture.Owner);
        var models = await fixture.Owner.ListModelsAsync(CancellationToken.None);
        Assert.Equal("community/open-model", Assert.Single(models).Key);
        Assert.True(await fixture.Owner.IsAllowedAsync("community/open-model", CancellationToken.None));
        Assert.Equal(1, fixture.Provider.CatalogCallCount);
        var result = await fixture.Owner.LoadAsync(new AiModelLoadRequest(
            "community/open-model", 4096, null, null, null), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("community/open-model", result.InstanceId);
        Assert.Equal(1, fixture.Provider.LoadCallCount);
    }

    [Fact]
    public async Task StartupDoesNotProbeTheModelProviderUntilCatalogIsRequested()
    {
        await using var fixture = await Fixture.CreateAsync();
        await Task.Delay(100);
        Assert.Equal(0, fixture.Provider.CatalogCallCount);
        Assert.Equal(0UL, fixture.Ledger.ReadTable().RegisteredBytes);

        await Assert.ThrowsAsync<AiModelRuntimeUnavailableException>(
            () => fixture.Owner.ListModelsAsync(CancellationToken.None));
        await WaitForCatalogAsync(fixture.Owner);

        Assert.Equal(1, fixture.Provider.CatalogCallCount);
    }

    [Fact]
    public async Task ModelWorkAreaIsReturnedToLedgerAndRebuiltAfterTick()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(0UL, fixture.Ledger.ReadTable().RegisteredBytes);
        await WaitForCatalogAsync(fixture.Owner);
        Assert.True(await fixture.Owner.IsAllowedAsync("community/open-model", CancellationToken.None));
        Assert.True(fixture.Ledger.ReadTable().RegisteredBytes > 0);
        fixture.Ledger.SetMode(LedgerTableMode.Optimize);
        Assert.Equal(1UL, fixture.Ledger.Tick().Released);
        Assert.Equal(0UL, fixture.Ledger.ReadTable().RegisteredBytes);
        Assert.True(await fixture.Owner.IsAllowedAsync("community/open-model", CancellationToken.None));
        Assert.Equal(1, fixture.Provider.CatalogCallCount);
        Assert.True(fixture.Ledger.ReadTable().RegisteredBytes > 0);
        Assert.Equal(1UL, fixture.Ledger.Tick().Released);
    }

    [Fact]
    public async Task InflightTaskSurvivesReclamationOfModelWorkArea()
    {
        await using var fixture = await Fixture.CreateAsync(holdFirstLoadIgnoringCancellation: true);
        await WaitForCatalogAsync(fixture.Owner);
        var loading = fixture.Owner.LoadAsync(new AiModelLoadRequest(
            "community/open-model", 4096, null, null, null), CancellationToken.None);
        try
        {
            await fixture.Provider.WaitForLoadCallsAsync(1);
            fixture.Ledger.SetMode(LedgerTableMode.Optimize);
            Assert.Equal(1UL, fixture.Ledger.Tick().Released);
        }
        finally { fixture.Provider.ReleaseFirstLoad(); }
        var result = await loading.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("community/open-model", result.InstanceId);
        Assert.Equal(1, fixture.Provider.LoadCallCount);
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
    public async Task StopDrainsRunningOperationAndPreservesQueuedOperationForRestart()
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
        Assert.False(second.IsCompleted);
        Assert.True(fixture.Owner.HasPendingTasks);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, fixture.Provider.LoadCallCount);

        await fixture.Owner.StartAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("community/open-model",
            (await second.WaitAsync(TimeSpan.FromSeconds(5))).InstanceId);
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
        Assert.Equal(3, fixture.Provider.LoadCallCount);
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
        RuntimePlanProvider planProvider,
        ResourceManagerLedgerOwner ledger,
        List<PublicServiceLedgerScratchTests.FailingScratch> scratches,
        PublicWorkspaceLifetimes lifetimes,
        Action<JsonObject>? configurePlan,
        bool enabled) : IAsyncDisposable
    {
        private bool serviceEnabled = enabled;
        internal PublicWorkspaceLifetimes Lifetimes { get; } = lifetimes;
        internal ResourceManagerLedgerOwner Ledger { get; } = ledger;
        internal HostManagerPublicServiceCoordinatorOwner Owner { get; } = owner;

        internal RecordingModelProvider Provider { get; } = provider;
        internal List<PublicServiceLedgerScratchTests.FailingScratch> Scratches { get; } = scratches;

        internal static async Task<Fixture> CreateAsync(
            int loadFailures = 0,
            int taskTimeoutMilliseconds = 30_000,
            int requestTimeoutMilliseconds = 30_000,
            bool holdFirstLoadIgnoringCancellation = false,
            bool cancelFirstLoad = false,
            bool failCatalogReturn = false,
            bool failCatalogComplete = false,
            int catalogTimeoutMilliseconds = 10_000,
            int catalogIntervalMilliseconds = 15_000,
            bool start = true,
            bool enabled = true,
            Action<JsonObject>? configurePlan = null,
            bool failWorkspaceClose = false)
        {
            Action<JsonObject> configure = root =>
                {
                    var recreate = root["host_recreate"]![
                        "public_service_coordinator"]!;
                    recreate["maximum_concurrent_model_tasks"] = 1;
                    recreate["retry_delay_ms"] = 10;
                    recreate["task_timeout_ms"] = taskTimeoutMilliseconds;
                    recreate["request_timeout_ms"] = requestTimeoutMilliseconds;
                    recreate["model_catalog_acquisition_timeout_ms"] = catalogTimeoutMilliseconds;
                    recreate["model_catalog_acquisition_interval_ms"] = catalogIntervalMilliseconds;
                    configurePlan?.Invoke(root);
                };
            var hostPlan = HostManagerTestPlanFactory.CreatePlan(
                configure,
                publicService: EnabledPublicServiceSettings with { Enabled = enabled });
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
            var ledger = new ResourceManagerLedgerOwner();
            var scratches = new List<PublicServiceLedgerScratchTests.FailingScratch>();
            var lifetimes = new PublicWorkspaceLifetimes();
            var owner = new HostManagerPublicServiceCoordinatorOwner(
                planProvider,
                new HostManagerPublicServiceCoordinatorRuntime(
                    planProvider,
                    deployment),
                new HostManagerRuntimeIdentity(),
                provider,
                NullLogger<HostManagerPublicServiceCoordinatorOwner>.Instance,
                length =>
                {
                    var scratch = new PublicServiceLedgerScratchTests.FailingScratch(
                        ledger.CreateScratch(length), failReturn: failCatalogReturn,
                        failClose: failWorkspaceClose)
                        { FailNextComplete = failCatalogComplete };
                    scratches.Add(scratch);
                    return scratch;
                },
                lifetimes.Create);
            if (start) await owner.StartAsync(CancellationToken.None);
            else await owner.InitializeAsync(CancellationToken.None);
            return new Fixture(owner, provider, planProvider, ledger, scratches, lifetimes, configure, enabled);
        }

        internal void PublishPlan(Action<JsonObject> mutate, bool? enabled = null)
        {
            if (enabled is { } desired) serviceEnabled = desired;
            var plan = HostManagerTestPlanFactory.CreatePlan(
                root =>
                {
                    configurePlan?.Invoke(root);
                    mutate(root);
                },
                publicService: EnabledPublicServiceSettings with { Enabled = serviceEnabled });
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
            Provider.ReleaseFirstLoad();
            await Owner.StopAsync(CancellationToken.None).WaitAsync(PublicHoldBound);
            await Owner.DisposeAsync().AsTask().WaitAsync(PublicHoldBound);
            Ledger.Dispose();
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
        internal Task<IReadOnlyList<AiModelDescriptor>>? FirstCatalog { get; set; }

        internal async Task WaitForCatalogCallsAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (CatalogCallCount < count) await Task.Delay(10, timeout.Token);
        }
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
            if (Interlocked.Increment(ref catalogCallCount) == 1 && FirstCatalog is { } pending) return pending;
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
