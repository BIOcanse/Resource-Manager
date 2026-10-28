using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Http;
using ResourceManager.App.Application.PublicServices.AiModels;
using ResourceManager.App.Domain.PublicServices.AiModels;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.PublicServices;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerPublicServiceCoordinatorOwnerTests
{
    private static readonly TimeSpan PublicHoldBound = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task IdlePublicPreparationAndPlanPublicationDoNotOpenNativeWorkspace()
    {
        await using var fixture = await Fixture.CreateAsync(start: false);
        Assert.Empty(fixture.Scratches);
        Assert.Empty(fixture.Lifetimes.Events);
        fixture.PublishPlan(ChangeRequestTimeout);
        await fixture.Owner.InitializeAsync(CancellationToken.None);
        await fixture.Owner.GetStatusAsync(CancellationToken.None);
        Assert.Empty(fixture.Lifetimes.Events);
        Assert.Empty(fixture.Scratches);
        Assert.Equal(0UL, fixture.Ledger.ReadTable().RegisteredBytes);
        Assert.Equal(0, fixture.Provider.CatalogCallCount);
    }

    [Theory]
    [InlineData("catalog")]
    [InlineData("capabilities")]
    [InlineData("models")]
    [InlineData("model-policy")]
    [InlineData("rejected-request")]
    [InlineData("disabled-request")]
    public async Task StoppedFinitePublicCallsCloseNativeWorkspaceBeforeReturning(string operation)
    {
        await using var fixture = await Fixture.CreateAsync(start: false, enabled: operation != "disabled-request");
        var entered = Stopwatch.GetTimestamp();
        switch (operation)
        {
            case "catalog": await fixture.Owner.GetCatalogAsync(CancellationToken.None); break;
            case "capabilities": await fixture.Owner.ListCapabilitiesAsync(CancellationToken.None); break;
            case "models":
                await Assert.ThrowsAsync<AiModelRuntimeUnavailableException>(
                    () => fixture.Owner.ListModelsAsync(CancellationToken.None));
                break;
            case "model-policy":
                await Assert.ThrowsAsync<AiModelRuntimeUnavailableException>(
                    () => fixture.Owner.IsAllowedAsync("community/open-model", CancellationToken.None));
                break;
            case "rejected-request":
                var rejected = await fixture.Owner.EvaluateAsync(CreateContext(
                    "/api/public/v1/index/status", HttpMethods.Post, IPAddress.Loopback), CancellationToken.None);
                Assert.False(rejected.Allowed);
                Assert.Equal(0UL, rejected.CompletionHandle);
                break;
            case "disabled-request":
                var disabled = await fixture.Owner.EvaluateAsync(CreateContext(
                    "/api/public/v1/index/status", HttpMethods.Get, IPAddress.Loopback), CancellationToken.None);
                Assert.False(disabled.Allowed);
                Assert.Equal("service-disabled", disabled.Reason);
                Assert.Equal(0UL, disabled.CompletionHandle);
                break;
        }
        var returned = Stopwatch.GetTimestamp();
        Assert.Equal(new[] { "open", "close" }, fixture.Lifetimes.Events.Select(item => item.Kind));
        Assert.All(fixture.Lifetimes.Events, item => Assert.InRange(item.Timestamp, entered, returned));
        Assert.Equal(0, fixture.Lifetimes.Active);
        Assert.Equal(0UL, fixture.Ledger.ReadTable().RegisteredBytes);
        Assert.Throws<ObjectDisposedException>(() => fixture.Lifetimes.LastCreated.ReadSnapshot());
    }

    [Fact]
    public async Task PublicRunKeepsOneWorkspaceUntilStopAndDoesNotAcquireModelCatalog()
    {
        await using var fixture = await Fixture.CreateAsync();
        var workspace = fixture.Lifetimes.LastCreated;
        Assert.Equal(1, fixture.Lifetimes.Active);
        await fixture.Owner.StartAsync(CancellationToken.None);
        await fixture.Owner.GetCatalogAsync(CancellationToken.None);
        await fixture.Owner.ListCapabilitiesAsync(CancellationToken.None);
        Assert.Same(workspace, fixture.Lifetimes.LastCreated);
        Assert.Equal("open", Assert.Single(fixture.Lifetimes.Events).Kind);
        Assert.Equal(0, fixture.Provider.CatalogCallCount);
        var stopping = Stopwatch.GetTimestamp();
        await fixture.Owner.StopAsync(CancellationToken.None).WaitAsync(PublicHoldBound);
        var stopped = Stopwatch.GetTimestamp();
        Assert.Equal(0, fixture.Lifetimes.Active);
        Assert.InRange(fixture.Lifetimes.Events.Single(item => item.Kind == "close").Timestamp, stopping, stopped);
        Assert.Throws<ObjectDisposedException>(() => workspace.ReadSnapshot());
    }

    [Fact]
    public async Task PublicRunAndOverlappingFiniteCallCloseOnlyAfterLastReturn()
    {
        await using var fixture = await Fixture.CreateAsync();
        await WaitForCatalogAsync(fixture.Owner);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fixture.Scratches[0].BeforeReturn = () =>
        {
            entered.Set();
            Assert.True(release.Wait(PublicHoldBound));
        };
        var call = Task.Run(() => fixture.Owner.IsAllowedAsync("community/open-model", CancellationToken.None));
        try
        {
            Assert.True(entered.Wait(PublicHoldBound));
            await fixture.Owner.StopAsync(CancellationToken.None).WaitAsync(PublicHoldBound);
            Assert.Equal(1, fixture.Lifetimes.Active);
            Assert.Equal("open", Assert.Single(fixture.Lifetimes.Events).Kind);
        }
        finally
        {
            fixture.Scratches[0].BeforeReturn = null;
            release.Set();
        }
        Assert.True(await call.WaitAsync(PublicHoldBound));
        Assert.Equal(0, fixture.Lifetimes.Active);
        Assert.Equal(new[] { "open", "close" }, fixture.Lifetimes.Events.Select(item => item.Kind));
        Assert.Equal(0UL, fixture.Ledger.ReadTable().RegisteredBytes);
    }

    [Fact]
    public async Task PublicRequestCompletionOwnsExactWorkspaceAcrossStop()
    {
        await using var fixture = await Fixture.CreateAsync();
        var decision = await AdmitIndexAsync(fixture);
        var workspace = fixture.Lifetimes.LastCreated;
        await fixture.Owner.StopAsync(CancellationToken.None).WaitAsync(PublicHoldBound);
        Assert.Equal(1, fixture.Lifetimes.Active);
        Assert.Equal(1U, workspace.ReadSnapshot().RequestCount);
        Assert.True(fixture.Owner.HasPendingTasks);
        var completing = Stopwatch.GetTimestamp();
        await fixture.Owner.CompleteAsync(decision.CompletionHandle, CancellationToken.None);
        var completed = Stopwatch.GetTimestamp();
        Assert.Equal(0, fixture.Lifetimes.Active);
        Assert.False(fixture.Owner.HasPendingTasks);
        Assert.InRange(fixture.Lifetimes.Events.Single(item => item.Kind == "close").Timestamp, completing, completed);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Owner.CompleteAsync(decision.CompletionHandle, CancellationToken.None));
        Assert.Equal(2, fixture.Lifetimes.Events.Length);
    }

    [Fact]
    public async Task PublicHeldPlanRebuildClosesPreviousWorkspaceAndIdlePlanWaitsForUse()
    {
        await using var fixture = await Fixture.CreateAsync();
        var previous = fixture.Lifetimes.LastCreated;
        fixture.PublishPlan(ChangeRequestTimeout);
        Assert.NotSame(previous, fixture.Lifetimes.LastCreated);
        Assert.Throws<ObjectDisposedException>(() => previous.ReadSnapshot());
        Assert.Equal(1, fixture.Lifetimes.Active);
        await fixture.Owner.StopAsync(CancellationToken.None).WaitAsync(PublicHoldBound);
        var eventCount = fixture.Lifetimes.Events.Length;
        fixture.PublishPlan(root =>
            root["host_recreate"]!["public_service_coordinator"]!["request_timeout_ms"] = 32_000);
        Assert.Equal(eventCount, fixture.Lifetimes.Events.Length);
        await fixture.Owner.GetCatalogAsync(CancellationToken.None);
        Assert.Equal(eventCount + 2, fixture.Lifetimes.Events.Length);
        Assert.Equal(0, fixture.Lifetimes.Active);
    }

    [Fact]
    public async Task StoppedPublicPlanRebuildWhileFiniteCallIsHeldClosesBothWorkspacesOnReturn()
    {
        await using var fixture = await Fixture.CreateAsync();
        await WaitForCatalogAsync(fixture.Owner);
        var previous = fixture.Lifetimes.LastCreated;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fixture.Scratches[0].BeforeReturn = () =>
        {
            entered.Set();
            Assert.True(release.Wait(PublicHoldBound));
        };
        var call = Task.Run(() => fixture.Owner.IsAllowedAsync("community/open-model", CancellationToken.None));
        var releasing = 0L;
        try
        {
            Assert.True(entered.Wait(PublicHoldBound));
            await fixture.Owner.StopAsync(CancellationToken.None).WaitAsync(PublicHoldBound);
            Assert.Equal(1, fixture.Lifetimes.Active);
            fixture.PublishPlan(ChangeRequestTimeout);
            Assert.NotSame(previous, fixture.Lifetimes.LastCreated);
            Assert.Equal(2, fixture.Lifetimes.Active);
            Assert.Equal(new[] { "open", "open" }, fixture.Lifetimes.Events.Select(item => item.Kind));
            Assert.Equal(1U, previous.ReadSnapshot().ModelCount);
        }
        finally
        {
            fixture.Scratches[0].BeforeReturn = null;
            releasing = Stopwatch.GetTimestamp();
            release.Set();
        }
        Assert.True(await call.WaitAsync(PublicHoldBound));
        var returned = Stopwatch.GetTimestamp();
        Assert.Equal(0, fixture.Lifetimes.Active);
        Assert.Equal(2, fixture.Lifetimes.Events.Count(item => item.Kind == "open"));
        Assert.Equal(2, fixture.Lifetimes.Events.Count(item => item.Kind == "close"));
        Assert.All(fixture.Lifetimes.Events.Where(item => item.Kind == "close"),
            item => Assert.InRange(item.Timestamp, releasing, returned));
        Assert.Throws<ObjectDisposedException>(() => previous.ReadSnapshot());
        Assert.Throws<ObjectDisposedException>(() => fixture.Lifetimes.LastCreated.ReadSnapshot());
        Assert.Equal(0UL, fixture.Ledger.ReadTable().RegisteredBytes);
    }

    [Fact]
    public async Task PublicDisableAndLastReleaseDiscardRateWindowBeforeEnable()
    {
        await using var fixture = await Fixture.CreateAsync(configurePlan: ConfigureSingleRequestWindow);
        var accepted = await AdmitIndexAsync(fixture);
        await fixture.Owner.CompleteAsync(accepted.CompletionHandle, CancellationToken.None);
        var limited = await fixture.Owner.EvaluateAsync(CreateContext(
            "/api/public/v1/index/status", HttpMethods.Get, IPAddress.Loopback), CancellationToken.None);
        Assert.False(limited.Allowed);
        Assert.Equal("rate-limited", limited.Reason);
        fixture.PublishPlan(_ => { }, enabled: false);
        await fixture.Owner.StopAsync(CancellationToken.None).WaitAsync(PublicHoldBound);
        Assert.Equal(0, fixture.Lifetimes.Active);
        fixture.PublishPlan(_ => { }, enabled: true);
        await fixture.Owner.StartAsync(CancellationToken.None);
        var fresh = await AdmitIndexAsync(fixture);
        await fixture.Owner.CompleteAsync(fresh.CompletionHandle, CancellationToken.None);
    }

    [Fact]
    public async Task PublicDisableRetainsAdmittedRequestAndItsRateWindowUntilCompletion()
    {
        await using var fixture = await Fixture.CreateAsync(configurePlan: ConfigureSingleRequestWindow);
        var accepted = await AdmitIndexAsync(fixture);
        var workspace = fixture.Lifetimes.LastCreated;
        fixture.PublishPlan(_ => { }, enabled: false);
        await fixture.Owner.StopAsync(CancellationToken.None).WaitAsync(PublicHoldBound);
        Assert.Equal(1, fixture.Lifetimes.Active);
        Assert.Same(workspace, fixture.Lifetimes.LastCreated);
        Assert.Equal(1U, workspace.ReadSnapshot().RequestCount);
        Assert.Equal(1U, workspace.ReadSnapshot().RateBucketCount);
        var disabled = await fixture.Owner.EvaluateAsync(CreateContext(
            "/api/public/v1/index/status", HttpMethods.Get, IPAddress.Loopback), CancellationToken.None);
        Assert.False(disabled.Allowed);
        Assert.Equal("service-disabled", disabled.Reason);
        Assert.Equal(1U, workspace.ReadSnapshot().RateBucketCount);
        await fixture.Owner.CompleteAsync(accepted.CompletionHandle, CancellationToken.None);
        Assert.Equal(0, fixture.Lifetimes.Active);
        fixture.PublishPlan(_ => { }, enabled: true);
        await fixture.Owner.StartAsync(CancellationToken.None);
        var fresh = await AdmitIndexAsync(fixture);
        await fixture.Owner.CompleteAsync(fresh.CompletionHandle, CancellationToken.None);
    }

    [Fact]
    public async Task AdmittedStoppedPublicLoadTransfersRequestHoldThroughNativeTaskSettlementAfterDisable()
    {
        await using var fixture = await Fixture.CreateAsync(holdFirstLoadIgnoringCancellation: true);
        await WaitForCatalogAsync(fixture.Owner);
        var admitted = await AdmitIndexAsync(fixture);
        var workspace = fixture.Lifetimes.LastCreated;
        await fixture.Owner.StopAsync(CancellationToken.None).WaitAsync(PublicHoldBound);
        // Admit while enabled, then retain this task and its workspace across disable.
        var load = fixture.Owner.LoadAsync(NewLoadRequest(), CancellationToken.None);
        fixture.PublishPlan(_ => { }, enabled: false);
        await fixture.Owner.CompleteAsync(admitted.CompletionHandle, CancellationToken.None);
        Assert.Equal(1, fixture.Lifetimes.Active);
        Assert.Equal(1U, workspace.ReadSnapshot().QueuedTaskCount);
        Assert.False(load.IsCompleted);
        Assert.Equal(0, fixture.Provider.LoadCallCount);
        await fixture.Owner.StartAsync(CancellationToken.None);
        try
        {
            await fixture.Provider.WaitForLoadCallsAsync(1);
            Assert.Same(workspace, fixture.Lifetimes.LastCreated);
            Assert.Equal(1, fixture.Lifetimes.Active);
        }
        finally { fixture.Provider.ReleaseFirstLoad(); }
        Assert.Equal("community/open-model", (await load.WaitAsync(PublicHoldBound)).InstanceId);
        await fixture.Owner.StopAsync(CancellationToken.None).WaitAsync(PublicHoldBound);
        Assert.Equal(0, fixture.Lifetimes.Active);
        Assert.Equal(0UL, fixture.Ledger.ReadTable().RegisteredBytes);
    }

    [Fact]
    public async Task CanceledPublicLoadWaiterKeepsNativeTaskUntilItsEffectSettles()
    {
        await using var fixture = await Fixture.CreateAsync(holdFirstLoadIgnoringCancellation: true);
        await WaitForCatalogAsync(fixture.Owner);
        var admitted = await AdmitIndexAsync(fixture);
        var workspace = fixture.Lifetimes.LastCreated;
        await fixture.Owner.StopAsync(CancellationToken.None).WaitAsync(PublicHoldBound);
        using var waiterCancellation = new CancellationTokenSource();
        var waiter = fixture.Owner.LoadAsync(NewLoadRequest(), waiterCancellation.Token);
        fixture.PublishPlan(_ => { }, enabled: false);
        waiterCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        await fixture.Owner.CompleteAsync(admitted.CompletionHandle, CancellationToken.None);
        Assert.True(fixture.Owner.HasPendingTasks);
        Assert.Equal(1U, workspace.ReadSnapshot().QueuedTaskCount);
        Assert.Equal(1, fixture.Lifetimes.Active);
        await fixture.Owner.StartAsync(CancellationToken.None);
        await fixture.Provider.WaitForLoadCallsAsync(1);
        var stopping = fixture.Owner.StopAsync(CancellationToken.None);
        try
        {
            Assert.False(stopping.IsCompleted);
            Assert.Equal(1, fixture.Lifetimes.Active);
            Assert.Equal(1U, workspace.ReadSnapshot().RunningTaskCount);
        }
        finally { fixture.Provider.ReleaseFirstLoad(); }
        await stopping.WaitAsync(PublicHoldBound);
        Assert.False(fixture.Owner.HasPendingTasks);
        Assert.Equal(1, fixture.Provider.LoadCallCount);
        Assert.Equal(0, fixture.Lifetimes.Active);
        Assert.Throws<ObjectDisposedException>(() => workspace.ReadSnapshot());
    }

    [Fact]
    public async Task CanceledQueuedPublicTaskClearsPendingFactWhenLastScratchCloseRequiresRetry()
    {
        await using var fixture = await Fixture.CreateAsync(failWorkspaceClose: true);
        await WaitForCatalogAsync(fixture.Owner);
        var admitted = await AdmitIndexAsync(fixture);
        var workspace = fixture.Lifetimes.LastCreated;
        await fixture.Owner.StopAsync(CancellationToken.None).WaitAsync(PublicHoldBound);
        var load = fixture.Owner.LoadAsync(NewLoadRequest(), CancellationToken.None);
        fixture.PublishPlan(_ => { }, enabled: false);
        await fixture.Owner.CompleteAsync(admitted.CompletionHandle, CancellationToken.None);
        Assert.Equal(1U, workspace.ReadSnapshot().QueuedTaskCount);
        Assert.True(fixture.Owner.HasPendingTasks);
        await Assert.ThrowsAsync<IOException>(() => fixture.Owner.DisposeAsync().AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        Assert.False(fixture.Owner.HasPendingTasks);
        Assert.Equal(0U, workspace.ReadSnapshot().QueuedTaskCount);
        Assert.Equal(0U, workspace.ReadSnapshot().RunningTaskCount);
        Assert.Equal(0, fixture.Provider.LoadCallCount);
        Assert.Equal(1, fixture.Lifetimes.Active);
        Assert.Equal("open", Assert.Single(fixture.Lifetimes.Events).Kind);
        var retrying = Stopwatch.GetTimestamp();
        await fixture.Owner.DisposeAsync().AsTask().WaitAsync(PublicHoldBound);
        var disposed = Stopwatch.GetTimestamp();
        Assert.Equal(new[] { "open", "close" }, fixture.Lifetimes.Events.Select(item => item.Kind));
        Assert.InRange(fixture.Lifetimes.Events.Single(item => item.Kind == "close").Timestamp, retrying, disposed);
        Assert.Equal(0, fixture.Lifetimes.Active);
        Assert.False(fixture.Owner.HasPendingTasks);
        Assert.Throws<ObjectDisposedException>(() => workspace.ReadSnapshot());
        await fixture.Owner.DisposeAsync();
        Assert.Equal(2, fixture.Lifetimes.Events.Length);
    }

    [Fact]
    public async Task CompletedPublicRequestClearsPendingFactWhenLastScratchCloseRequiresRetry()
    {
        await using var fixture = await Fixture.CreateAsync(start: false, failWorkspaceClose: true);
        var admitted = await AdmitIndexAsync(fixture);
        var workspace = fixture.Lifetimes.LastCreated;
        Assert.True(fixture.Owner.HasPendingTasks);
        await Assert.ThrowsAsync<IOException>(async () =>
            await fixture.Owner.CompleteAsync(admitted.CompletionHandle, CancellationToken.None));
        Assert.False(fixture.Owner.HasPendingTasks);
        Assert.Equal(0U, workspace.ReadSnapshot().RequestCount);
        Assert.Equal(1, fixture.Lifetimes.Active);
        Assert.Equal("open", Assert.Single(fixture.Lifetimes.Events).Kind);
        var disposing = Stopwatch.GetTimestamp();
        await fixture.Owner.DisposeAsync().AsTask().WaitAsync(PublicHoldBound);
        var disposed = Stopwatch.GetTimestamp();
        Assert.Equal(new[] { "open", "close" }, fixture.Lifetimes.Events.Select(item => item.Kind));
        Assert.InRange(fixture.Lifetimes.Events.Single(item => item.Kind == "close").Timestamp, disposing, disposed);
        Assert.Equal(0, fixture.Lifetimes.Active);
        Assert.False(fixture.Owner.HasPendingTasks);
        Assert.Throws<ObjectDisposedException>(() => workspace.ReadSnapshot());
        await fixture.Owner.DisposeAsync();
        Assert.Equal(2, fixture.Lifetimes.Events.Length);
    }

    [Fact]
    public async Task PublicAsyncDisposalWaitsForAdmittedRequestAndAllowsItsExactCompletion()
    {
        await using var fixture = await Fixture.CreateAsync(start: false);
        var admitted = await AdmitIndexAsync(fixture);
        var workspace = fixture.Lifetimes.LastCreated;
        var closing = fixture.Owner.DisposeAsync().AsTask();
        var completing = 0L;
        try
        {
            Assert.False(closing.IsCompleted);
            Assert.Equal(1, fixture.Lifetimes.Active);
            Assert.Equal("open", Assert.Single(fixture.Lifetimes.Events).Kind);
            Assert.Equal(1U, workspace.ReadSnapshot().RequestCount);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Owner.EvaluateAsync(CreateContext(
                "/api/public/v1/index/status", HttpMethods.Get, IPAddress.Loopback), CancellationToken.None));
            Assert.False(closing.IsCompleted);
        }
        finally
        {
            completing = Stopwatch.GetTimestamp();
            await fixture.Owner.CompleteAsync(admitted.CompletionHandle, CancellationToken.None);
        }
        await closing.WaitAsync(PublicHoldBound);
        var closed = Stopwatch.GetTimestamp();
        Assert.Equal(new[] { "open", "close" }, fixture.Lifetimes.Events.Select(item => item.Kind));
        Assert.InRange(fixture.Lifetimes.Events.Single(item => item.Kind == "close").Timestamp, completing, closed);
        Assert.Equal(0, fixture.Lifetimes.Active);
        Assert.False(fixture.Owner.HasPendingTasks);
        Assert.Throws<ObjectDisposedException>(() => workspace.ReadSnapshot());
        await fixture.Owner.DisposeAsync();
        Assert.Equal(2, fixture.Lifetimes.Events.Length);
    }

    [Fact]
    public async Task PublicAsyncDisposalWaitsForInflightProviderEffectBeforeClosingWorkspace()
    {
        await using var fixture = await Fixture.CreateAsync(holdFirstLoadIgnoringCancellation: true);
        await WaitForCatalogAsync(fixture.Owner);
        var load = fixture.Owner.LoadAsync(NewLoadRequest(), CancellationToken.None);
        await fixture.Provider.WaitForLoadCallsAsync(1);
        var closing = fixture.Owner.DisposeAsync().AsTask();
        try
        {
            Assert.False(closing.IsCompleted);
            Assert.Equal(1, fixture.Lifetimes.Active);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Owner.GetCatalogAsync(CancellationToken.None));
        }
        finally { fixture.Provider.ReleaseFirstLoad(); }
        Assert.Equal("community/open-model", (await load.WaitAsync(PublicHoldBound)).InstanceId);
        await closing.WaitAsync(PublicHoldBound);
        Assert.Equal(0, fixture.Lifetimes.Active);
        Assert.Equal(0UL, fixture.Ledger.ReadTable().RegisteredBytes);
    }

    [Fact]
    public void PublicWorkspaceCloseTimelineRecordsOnlySuccessfulNativeCloseOnce()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var lifetimes = new PublicWorkspaceLifetimes();
        using var workspace = lifetimes.Create(HostManagerTestPlanFactory.CreatePlan().PublicServiceCoordinator,
            1, 1, 1, length => new PublicServiceLedgerScratchTests.FailingScratch(ledger.CreateScratch(length), failClose: true));
        workspace.ResolveModel("unavailable");
        Assert.Throws<IOException>(workspace.Dispose);
        Assert.Equal("open", Assert.Single(lifetimes.Events).Kind);
        Assert.Equal(1, lifetimes.Active);
        Assert.NotEqual(0UL, workspace.ReadSnapshot().CatalogGeneration);
        workspace.Dispose();
        workspace.Dispose();
        Assert.Equal(new[] { "open", "close" }, lifetimes.Events.Select(item => item.Kind));
        Assert.Equal(0, lifetimes.Active);
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
        Assert.Throws<ObjectDisposedException>(() => workspace.ReadSnapshot());
    }

    private static async Task<ResourceManager.App.Domain.PublicServices.LocalPublicServiceAccessDecision> AdmitIndexAsync(Fixture fixture)
    {
        var decision = await fixture.Owner.EvaluateAsync(CreateContext(
            "/api/public/v1/index/status", HttpMethods.Get, IPAddress.Loopback), CancellationToken.None);
        Assert.True(decision.Allowed, $"Expected an admitted request; received {decision.StatusCode} ({decision.Reason}).");
        return decision;
    }

    private static AiModelLoadRequest NewLoadRequest() => new("community/open-model", 4096, null, null, null);

    private static void ChangeRequestTimeout(System.Text.Json.Nodes.JsonObject root)
        => root["host_recreate"]!["public_service_coordinator"]!["request_timeout_ms"] = 31_000;

    private static void ConfigureSingleRequestWindow(System.Text.Json.Nodes.JsonObject root)
    {
        var recreate = root["host_recreate"]!["public_service_coordinator"]!;
        recreate["maximum_requests_per_rate_window"] = 1;
        recreate["rate_window_ms"] = 60_000;
    }

    private sealed class PublicWorkspaceLifetimes
    {
        private readonly object gate = new();
        private readonly List<(long Timestamp, string Kind)> events = [];
        private int active;
        internal (long Timestamp, string Kind)[] Events { get { lock (gate) return events.ToArray(); } }
        internal int Active { get { lock (gate) return active; } }
        internal NativePublicServiceCoordinatorWorkspace LastCreated { get; private set; } = null!;

        internal NativePublicServiceCoordinatorWorkspace Create(CompiledHostManagerPublicServiceCoordinatorPlan plan,
            ulong hostInstanceId, ulong sessionIncarnation, ulong catalogGeneration, Func<ulong, IComputeScratch> createScratch)
        {
            var workspace = new NativePublicServiceCoordinatorWorkspace(plan, hostInstanceId, sessionIncarnation,
                catalogGeneration, createScratch, onClosed: () =>
                {
                    lock (gate)
                    {
                        active--;
                        events.Add((Stopwatch.GetTimestamp(), "close"));
                    }
                });
            lock (gate)
            {
                LastCreated = workspace;
                active++;
                events.Add((Stopwatch.GetTimestamp(), "open"));
            }
            return workspace;
        }
    }
}
