using System.Collections.Concurrent;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Optimization.Reports;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerRuntimePublicationTests
{
    [Fact]
    public void RuntimePlanProvider_UsesDeploymentStateAsItsOnlyCurrentOwner()
    {
        var state = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(state);
        var plan = CreateRuntimePlan(Compile(1), 1);

        provider.Publish(plan);

        Assert.Same(plan, provider.Current);
        var diagnostics = state.CaptureDiagnostics();
        Assert.Equal(1UL, diagnostics.PublicationSequence);
        Assert.Equal(plan.Version, diagnostics.RuntimePlanVersion);
        Assert.Equal(plan.Version, diagnostics.Deployment.RuntimePlanVersion);
        Assert.Equal(plan.HostManager.PlanEpoch, diagnostics.PlanEpoch);
        Assert.Equal(plan.HostManager.PlanEpoch, diagnostics.Deployment.PublishedPlanEpoch);
        Assert.Throws<InvalidOperationException>(() => provider.Publish(plan));
    }

    [Fact]
    public async Task DiagnosticsCapture_NeverTearsPlanAndDeploymentPublication()
    {
        var state = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(state);
        var first = Compile(1);
        provider.Publish(CreateRuntimePlan(first, 1));
        var failures = new ConcurrentQueue<string>();
        using var start = new ManualResetEventSlim(false);

        var writer = Task.Run(() =>
        {
            start.Wait();
            for (var version = 2L; version <= 250; version++)
            {
                provider.Publish(CreateRuntimePlan(first with { PlanEpoch = checked((ulong)version) }, version));
                Thread.Yield();
            }
        });
        var readers = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                while (!writer.IsCompleted)
                {
                    ValidateAtomicCut(state.CaptureDiagnostics(), failures);
                }
                ValidateAtomicCut(state.CaptureDiagnostics(), failures);
            }))
            .ToArray();

        start.Set();
        await Task.WhenAll(readers.Append(writer));

        Assert.Empty(failures);
        Assert.Equal(250L, provider.Current.Version);
        Assert.Equal(250UL, state.Snapshot.PublicationSequence);
    }

    [Fact]
    public async Task PublicationLease_HoldsOneImmutablePlanUntilTheCycleReleasesIt()
    {
        var state = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(state);
        var first = CreateRuntimePlan(Compile(1), 1);
        var second = CreateRuntimePlan(
            first.HostManager with { PlanEpoch = 2 },
            2);
        provider.Publish(first);

        using var lease = provider.AcquirePublicationLease();
        Assert.Same(first, lease.Plan);
        Assert.Equal(1UL, lease.PublicationSequence);

        using var publishStarted = new ManualResetEventSlim(false);
        var publish = Task.Run(() =>
        {
            publishStarted.Set();
            provider.Publish(second);
        });
        publishStarted.Wait();
        await Task.Delay(50);

        Assert.False(publish.IsCompleted);
        Assert.Same(first, provider.Current);
        Assert.Same(first, lease.Plan);

        lease.Dispose();
        await publish.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(second, provider.Current);
        Assert.Equal(2UL, state.Snapshot.PublicationSequence);
    }

    [Fact]
    public void Publish_IsolatesSubscriberFailuresAndReportsCommittedIdentity()
    {
        var state = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(state);
        var plan = CreateRuntimePlan(Compile(1), 1);
        var deliveries = new List<string>();
        provider.Published += _ => deliveries.Add("first");
        provider.Published += _ => throw new InvalidOperationException("subscriber-failed");
        provider.Published += _ => deliveries.Add("third");

        var result = provider.Publish(plan);

        Assert.Same(plan, provider.Current);
        Assert.Same(plan, result.Plan);
        Assert.Equal(1UL, result.PublicationSequence);
        Assert.Equal(["first", "third"], deliveries);
        var failure = Assert.Single(result.DeliveryFailures);
        Assert.Equal(typeof(InvalidOperationException).FullName, failure.ExceptionType);
        Assert.Equal("subscriber-failed", failure.Message);
    }

    [Fact]
    public void ReportCoordinatorAttemptFailsClosedOnStaleSuccessAndSettlesOnce()
    {
        var state = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(state);
        var first = CreateRuntimePlan(Compile(1), 1);
        var second = CreateRuntimePlan(
            first.HostManager with { PlanEpoch = 2 },
            2);
        provider.Publish(first);
        var runtime = new HostManagerReportCoordinatorRuntime(state);
        var token = runtime.BeginInitialCreate(first.HostManager);
        var attempt = new ReportCoordinatorDeploymentAttempt(runtime, token);

        provider.Publish(second);

        Assert.Throws<InvalidOperationException>(() =>
            attempt.CompleteSucceeded());
        Assert.True(attempt.SettlementAttempted);
        Assert.Throws<InvalidOperationException>(() =>
            attempt.CompleteFailed("must-not-settle-twice"));
    }

    [Fact]
    public void ReportCoordinatorFailureSettlementRecordsSupersededFailure()
    {
        var state = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(state);
        var first = CreateRuntimePlan(Compile(1), 1);
        var second = CreateRuntimePlan(
            first.HostManager with { PlanEpoch = 2 },
            2);
        provider.Publish(first);
        var runtime = new HostManagerReportCoordinatorRuntime(state);
        var token = runtime.BeginInitialCreate(first.HostManager);

        provider.Publish(second);

        Assert.Equal(
            HostManagerDeploymentAttemptSettlement.Failed,
            runtime.CompleteFailed(token, "superseded-before-failure"));
        var failure = Assert.IsType<HostManagerModuleFailureSnapshot>(
            state.Snapshot.ReportCoordinator.LastFailure);
        Assert.False(failure.Active);
        Assert.Equal(HostManagerFailureResolution.Superseded, failure.Resolution);
    }

    private static void ValidateAtomicCut(
        HostManagerDeploymentDiagnosticsSnapshot diagnostics,
        ConcurrentQueue<string> failures)
    {
        if (diagnostics.RuntimePlanVersion != diagnostics.Deployment.RuntimePlanVersion
            || diagnostics.PublicationSequence != diagnostics.Deployment.PublicationSequence
            || diagnostics.PlanEpoch != diagnostics.Deployment.PublishedPlanEpoch
            || Modules(diagnostics.Deployment).Any(module =>
                module.DesiredPublicationSequence != diagnostics.PublicationSequence
                || module.DesiredPlanEpoch != diagnostics.PlanEpoch))
        {
            failures.Enqueue(
                $"torn:{diagnostics.RuntimePlanVersion}:{diagnostics.PublicationSequence}:{diagnostics.PlanEpoch}");
        }
    }

    private static CompiledRuntimePlan CreateRuntimePlan(
        CompiledHostManagerPlan hostPlan,
        long version)
        => CompiledRuntimePlan.Default with
        {
            Version = version,
            CompiledAt = DateTimeOffset.UtcNow,
            Reason = $"publication-{version}",
            HostManager = hostPlan
        };

    private static CompiledHostManagerPlan Compile(ulong epoch)
    {
        using var stream = typeof(StrictHostManagerProfileLoader).Assembly
            .GetManifestResourceStream("ResourceManager.Configuration.HostManager.default.json")
            ?? throw new InvalidOperationException("Embedded Host Manager profile was not found.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var loaded = StrictHostManagerProfileLoader.LoadBytes(buffer.ToArray(), "test/profile.json");
        return new HostManagerPlanCompiler().Compile(
            loaded,
            HostManagerTestPlanFactory.CreateSettingsInput(AppSettingsDefaults.Create().Performance),
            epoch,
            HostManagerTestPlanFactory.CreateCpuTopology(1),
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)));
    }

    private static IReadOnlyList<HostManagerModuleDeploymentSnapshot> Modules(
        HostManagerDeploymentSnapshot snapshot)
        =>
        [
            snapshot.SharedResources,
            snapshot.ResourceScheduler,
            snapshot.AdapterPrivateResourceLedger,
            snapshot.SmartCoordinator,
            snapshot.MemoryCleanup,
            snapshot.PlacementCoordinator,
            snapshot.TransactionJournal,
            snapshot.ProcessPolicyExecutor,
            snapshot.PdhCollector
        ];
}
