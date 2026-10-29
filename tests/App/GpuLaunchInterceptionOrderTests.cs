using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class GpuLaunchInterceptionOrderTests
{
    [Fact]
    public async Task PublicationStartsReconciliationOnceAndOnlyChangedMatchingInputReconcilesAgain()
    {
        using var files = new StartupModuleTestCatalog();
        var store = SeedRules();
        var software = new SoftwareView();
        var plans = new RuntimePlanProvider(new HostManagerDeploymentState());
        var reconciler = Create(files, store, software, new ModuleStore(), new FailureLog(), plans: plans);
        await reconciler.StartAsync(default);
        try
        {
            Assert.False(plans.Current.HostManager.IsPublished);
            await reconciler.PublicationWork.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(0, software.Reads);
            AssertRulesUnchanged(store);

            var plan = Plan();
            plans.Publish(plan);
            await reconciler.PublicationWork.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(1, software.Reads);
            Assert.Equal(4, store.WriteRequests.Count);
            foreach (var view in Views)
            {
                Assert.Empty(store.GetImage(view, "module.exe").Rules);
                Assert.Empty(store.GetImage(view, "gpu.exe").Rules);
            }

            plans.Publish(plan with { Version = 2, AutomaticUpdatesEnabled = !plan.AutomaticUpdatesEnabled });
            await reconciler.PublicationWork.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(1, software.Reads);
            Assert.Equal(4, store.WriteRequests.Count);

            var changed = Plan(3, 4);
            Assert.NotEqual(plan.HostManager.SoftwareIdentityCatalog.ConfigurationSha256,
                changed.HostManager.SoftwareIdentityCatalog.ConfigurationSha256);
            plans.Publish(changed);
            await reconciler.PublicationWork.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(2, software.Reads);
        }
        finally { await reconciler.StopAsync(default); }
    }

    [Fact]
    public async Task PublicationDuringReconciliationQueuesOneLatestPassAndExplicitCommandsShareTheSerialGate()
    {
        using var files = new StartupModuleTestCatalog();
        var software = new SoftwareView();
        var plans = new RuntimePlanProvider(new HostManagerDeploymentState());
        var availability = new GatedAvailability();
        var reconciler = Create(files, SeedRules(), software, new ModuleStore(), new FailureLog(),
            plans: plans, availability: availability);
        await reconciler.StartAsync(default);
        try
        {
            var first = Plan();
            plans.Publish(first);
            await availability.Entered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            plans.Publish(first with { Version = 2 });
            var changed = Plan(3, 4);
            plans.Publish(changed);
            plans.Publish(changed with { Version = 4 });
            var explicitCommand = reconciler.ReconcileAsync(default);
            Assert.False(explicitCommand.IsCompleted);
            Assert.Equal(1, availability.Calls);
            availability.Release.TrySetResult();
            await explicitCommand.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await reconciler.PublicationWork.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(3, software.Reads); // initial input, latest publication, explicit command
            Assert.Equal(3, availability.Calls);
            Assert.Equal(1, availability.MaximumActive);
        }
        finally { availability.Release.TrySetResult(); await reconciler.StopAsync(default); }
    }

    [Fact]
    public async Task LateStartCatchesCurrentPublicationAndStopRejectsCapturedCallbacksAndJoinsAcceptedWork()
    {
        using var files = new StartupModuleTestCatalog();
        var store = SeedRules();
        var software = new SoftwareView();
        var plans = new RuntimePlanProvider(new HostManagerDeploymentState());
        plans.Publish(Plan());
        var availability = new GatedAvailability();
        var reconciler = Create(files, store, software, new ModuleStore(), new FailureLog(),
            plans: plans, availability: availability);
        await reconciler.StartAsync(default);
        var oldCallback = Assert.IsType<Action<CompiledRuntimePlan>>(PublicationCallback(plans));
        Task? stop = null;
        try
        {
            await availability.Entered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            stop = reconciler.StopAsync(default);
            await availability.Canceled.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.False(stop.IsCompleted); // accepted work deliberately ignores cancellation until released
            Assert.Same(stop, reconciler.StopAsync(default));
            Assert.Throws<InvalidOperationException>(() => { _ = reconciler.StartAsync(default); });
            Assert.Null(PublicationCallback(plans));
            var changed = Plan(2, 4);
            plans.Publish(changed);
            oldCallback(changed);
            availability.Release.TrySetResult();
            await stop.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(0, software.Reads);
            AssertRulesUnchanged(store);

            await reconciler.StartAsync(default);
            await reconciler.PublicationWork.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(1, software.Reads);
            oldCallback(Plan(3, 5));
            await reconciler.PublicationWork.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(1, software.Reads);
        }
        finally
        {
            availability.Release.TrySetResult();
            if (stop is not null) await stop;
            await reconciler.StopAsync(default);
        }
    }

    [Theory]
    [InlineData("software")]
    [InlineData("settings")]
    [InlineData("selection")]
    [InlineData("availability")]
    [InlineData("policy")]
    [InlineData("cancellation")]
    public async Task FailedSelectionKeepsEveryOwnedRuleAndReportsTheOriginalFailure(string stage)
    {
        using var files = new StartupModuleTestCatalog();
        var store = SeedRules();
        Exception failure = stage == "cancellation" ? new OperationCanceledException("unrequested cancellation")
            : new IOException("selection failed");
        var software = new SoftwareView(stage is "software" or "cancellation" ? failure : null);
        var log = new FailureLog();
        var reconciler = Create(files, store, software,
            new ModuleStore(stage == "settings" ? failure : null, stage == "selection"), log,
            stage == "availability" ? failure : null, stage == "policy" ? failure : null);

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => reconciler.ReconcileAsync(default));
        if (stage != "selection") Assert.Same(failure, thrown);
        Assert.Contains(log.Errors, error => ReferenceEquals(error, thrown));
        AssertRulesUnchanged(store);
    }

    [Fact]
    public async Task FailedAutomaticAttemptIsReportedWithoutRetryingTheSamePublishedInput()
    {
        using var files = new StartupModuleTestCatalog();
        var store = SeedRules();
        var failure = new IOException("software unavailable");
        var software = new SoftwareView(failure);
        var log = new FailureLog();
        var plans = new RuntimePlanProvider(new HostManagerDeploymentState());
        var reconciler = Create(files, store, software, new ModuleStore(), log, plans: plans);
        await reconciler.StartAsync(default);
        try
        {
            var plan = Plan();
            plans.Publish(plan);
            await reconciler.PublicationWork.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            plans.Publish(plan with { Version = 2 });
            await reconciler.PublicationWork.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(1, software.Reads);
            Assert.Same(failure, Assert.Single(log.Errors));
            plans.Publish(Plan(3, 4));
            await reconciler.PublicationWork.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(2, software.Reads);
            Assert.Equal(2, log.Errors.Count);
            await Assert.ThrowsAsync<IOException>(() => reconciler.ReconcileAsync(default));
            Assert.Equal(3, software.Reads);
            Assert.Equal(3, log.Errors.Count);
            AssertRulesUnchanged(store);
        }
        finally { await reconciler.StopAsync(default); }
    }

    private static CompiledRuntimePlan Plan(long version = 1, int minimumKeyLength = 3)
        => CompiledRuntimePlan.Default with { Version = version, HostManager = HostManagerTestPlanFactory.CreatePlan(
            root => root["hot_publish"]!["software_identity_catalog"]!["minimum_contains_key_length"] = minimumKeyLength,
            planEpoch: (ulong)version) };
    private static object? PublicationCallback(RuntimePlanProvider plans) => typeof(RuntimePlanProvider)
        .GetField("Published", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plans);

    private static readonly RegistryView[] Views = [RegistryView.Registry64, RegistryView.Registry32];
    private static IfeoOwnedRuleCleanupCoordinatorTests.FakeOwnedRuleStore SeedRules()
    {
        var store = new IfeoOwnedRuleCleanupCoordinatorTests.FakeOwnedRuleStore();
        foreach (var view in Views)
            foreach (var image in new[] { "module.exe", "gpu.exe" })
                store.SetImage(view, new IfeoImageSnapshot(image, true, true,
                    [new IfeoRuleSnapshot("owned", WindowsIfeoGpuLaunchInterceptionRegistry.OwnerValue, @"C:\Apps\" + image)]));
        return store;
    }
    private static void AssertRulesUnchanged(IfeoOwnedRuleCleanupCoordinatorTests.FakeOwnedRuleStore store)
    {
        Assert.Empty(store.WriteRequests);
        foreach (var view in Views)
            foreach (var image in new[] { "module.exe", "gpu.exe" })
            {
                var remaining = store.GetImage(view, image);
                Assert.True(remaining.ParentUseFilterOwned);
                Assert.Equal(@"C:\Apps\" + image, Assert.Single(remaining.Rules).NormalizedFilterPath);
            }
    }
    private static GpuLaunchInterceptionReconciler Create(StartupModuleTestCatalog files,
        IfeoOwnedRuleCleanupCoordinatorTests.FakeOwnedRuleStore store, SoftwareView software,
        ModuleStore modules, FailureLog log, Exception? availabilityFailure = null, Exception? policyFailure = null,
        RuntimePlanProvider? plans = null, IGpuSchedulingAvailability? availability = null)
        => new(new PolicyStore(policyFailure), modules, files.Catalog, software,
            new WindowsIfeoGpuLaunchInterceptionRegistry(@"C:\missing-broker.exe", store),
            availability ?? new Availability(availabilityFailure), log, plans ?? new RuntimePlanProvider(new HostManagerDeploymentState()));

    private sealed class ModuleStore(Exception? failure = null, bool invalid = false) : IStartupModuleSettingsStore
    {
        public Task<StartupModuleSettingsDocument> GetAsync(CancellationToken token) => failure is null
            ? Task.FromResult(new StartupModuleSettingsDocument(1, invalid ? null! : []))
            : Task.FromException<StartupModuleSettingsDocument>(failure);
        public Task<StartupModuleSelection> SaveAsync(StartupModuleSelection selection, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class SoftwareView(Exception? failure = null) : ISoftwareRegistryView
    {
        private int reads;
        internal int Reads => Volatile.Read(ref reads);
        public Task<IReadOnlyList<SoftwareRecord>> GetSoftwareAsync(CancellationToken token)
        {
            Interlocked.Increment(ref reads);
            return failure is null ? Task.FromResult<IReadOnlyList<SoftwareRecord>>([])
                : Task.FromException<IReadOnlyList<SoftwareRecord>>(failure);
        }
        public Task<IReadOnlyList<SoftwareRecord>> RefreshSoftwareAsync(CancellationToken token) => GetSoftwareAsync(token);
    }
    private sealed class Availability(Exception? failure) : IGpuSchedulingAvailability
    {
        public ValueTask<GpuSchedulingAvailability> EvaluateAsync(CancellationToken token) => failure is null
            ? ValueTask.FromResult(new GpuSchedulingAvailability(true, null))
            : ValueTask.FromException<GpuSchedulingAvailability>(failure);
    }
    private sealed class PolicyStore(Exception? failure) : IGpuPlacementPolicyStore
    {
        public Task<GpuPlacementPolicyDocument> GetAsync(CancellationToken token) => failure is null
            ? Task.FromResult(GpuPlacementPolicyDocument.Empty)
            : Task.FromException<GpuPlacementPolicyDocument>(failure);
        public Task<GpuPlacementSoftwarePolicy> GetOrCreateSoftwarePolicyAsync(string id, string name, string? kind, CancellationToken token) => throw new NotSupportedException();
        public Task<GpuPlacementSoftwarePolicy> SaveSoftwarePolicyAsync(GpuPlacementSoftwarePolicy policy, CancellationToken token) => throw new NotSupportedException();
        public Task<GpuPlacementProcessPolicy> SaveProcessPolicyAsync(GpuPlacementProcessPolicy policy, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class GatedAvailability : IGpuSchedulingAvailability
    {
        private int calls;
        private int active;
        private int maximumActive;
        internal int Calls => Volatile.Read(ref calls);
        internal int MaximumActive => Volatile.Read(ref maximumActive);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<GpuSchedulingAvailability> EvaluateAsync(CancellationToken token)
        {
            var call = Interlocked.Increment(ref calls);
            var currentActive = Interlocked.Increment(ref active);
            Interlocked.Exchange(ref maximumActive, Math.Max(MaximumActive, currentActive));
            try
            {
                if (call == 1)
                {
                    using var cancellation = token.Register(() => Canceled.TrySetResult());
                    Entered.TrySetResult();
                    await Release.Task;
                    token.ThrowIfCancellationRequested();
                }
                return new(true, null);
            }
            finally { Interlocked.Decrement(ref active); }
        }
    }
    private sealed class FailureLog : ILogger<GpuLaunchInterceptionReconciler>
    {
        internal List<Exception> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        { if (level >= LogLevel.Warning && error is not null) Errors.Add(error); }
    }
}
