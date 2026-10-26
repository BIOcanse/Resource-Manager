using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.PublicServices.AiModels;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.PublicServices.AiModels;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.PublicServices;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

[Collection(SoftwareIdentityCatalogProcessStateCollection.Name)]
public sealed class HostedPlanCallbackLifetimeTests
{
    [Fact]
    public async Task FileQueryRejectsCapturedPlanCallbackAfterStop()
    {
        var deployment = new HostManagerDeploymentState();
        var provider = CreateProvider(deployment);
        using var memory = new ResourceManagerLedgerOwner();
        using var owner = new HostManagerFileQueryOwner(provider,
            new HostManagerFileQueryRuntime(provider, deployment),
            NullLogger<HostManagerFileQueryOwner>.Instance, memory);
        await AssertCapturedCallbackIsRetiredAsync(owner, provider,
            () => deployment.Snapshot.FileQuery, Replacement());
    }

    [Fact]
    public async Task SoftwareIdentityRejectsCapturedPlanCallbackAfterStop()
    {
        var deployment = new HostManagerDeploymentState();
        var provider = CreateProvider(deployment);
        using var owner = new HostManagerSoftwareIdentityOwner(provider,
            new HostManagerSoftwareIdentityRuntime(provider, deployment),
            NullLogger<HostManagerSoftwareIdentityOwner>.Instance);
        await AssertCapturedCallbackIsRetiredAsync(owner, provider,
            () => deployment.Snapshot.SoftwareIdentityCatalog, Replacement());
    }

    [Fact]
    public async Task MetricSnapshotRejectsCapturedPlanCallbackAfterStop()
    {
        using var fixture = new MetricSnapshotOwnerTestFixture.Fixture(
            MetricSnapshotOwnerTestFixture.Topology([]));
        // Reopen after the blocker is registered so the original Publish really
        // captures this owner's callback behind the blocked subscriber.
        await fixture.Owner.StopAsync(CancellationToken.None);
        await AssertCapturedCallbackIsRetiredAsync(fixture.Owner, fixture.Slots.Provider,
            () => fixture.Slots.Deployment.Snapshot.MetricSnapshot, Replacement());
    }

    [Fact]
    public Task PublicServiceRejectsCapturedPlanCallbackAfterStop()
        => CheckPublicServiceCallbackAsync(disposeAfterStop: false);

    [Fact]
    public Task PublicServiceRejectsCapturedPlanCallbackAfterStopAndDispose()
        => CheckPublicServiceCallbackAsync(disposeAfterStop: true);

    private static async Task CheckPublicServiceCallbackAsync(bool disposeAfterStop)
    {
        var deployment = new HostManagerDeploymentState();
        var enabled = new AppLocalPublicServiceSettings(true, true, true, true);
        var provider = CreateProvider(deployment, enabled);
        using var memory = new ResourceManagerLedgerOwner();
        using var owner = new HostManagerPublicServiceCoordinatorOwner(provider,
            new HostManagerPublicServiceCoordinatorRuntime(provider, deployment),
            new HostManagerRuntimeIdentity(), new UnusedModelProvider(),
            NullLogger<HostManagerPublicServiceCoordinatorOwner>.Instance, memory);
        await AssertCapturedCallbackIsRetiredAsync(owner, provider,
            () => deployment.Snapshot.PublicServiceCoordinator, Replacement(enabled),
            disposeAfterStop ? owner.Dispose : null);
    }

    private static RuntimePlanProvider CreateProvider(HostManagerDeploymentState deployment,
        AppLocalPublicServiceSettings? publicService = null)
    {
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = 1,
            HostManager = HostManagerTestPlanFactory.CreatePlan(publicService: publicService)
        });
        return provider;
    }

    private static CompiledHostManagerPlan Replacement(AppLocalPublicServiceSettings? publicService = null)
        => HostManagerTestPlanFactory.CreatePlan(root =>
            root["profile_revision"] = root["profile_revision"]!.GetValue<int>() + 1,
            planEpoch: 2, publicService: publicService);

    private static async Task AssertCapturedCallbackIsRetiredAsync(IHostedService owner,
        RuntimePlanProvider provider, Func<HostManagerModuleDeploymentSnapshot> readModule,
        CompiledHostManagerPlan replacement, Action? afterStop = null)
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Block(CompiledRuntimePlan _)
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)), "Captured publication was not released.");
        }
        provider.Published += Block;
        Task<RuntimePlanPublicationResult>? publication = null;
        try
        {
            await owner.StartAsync(CancellationToken.None);
            var before = readModule();
            publication = Task.Run(() => provider.Publish(provider.Current with
            {
                Version = provider.Current.Version + 1,
                HostManager = replacement
            }));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await owner.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            afterStop?.Invoke();
            release.Set();
            var delivered = await publication.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(delivered.DeliveryFailures);
            var after = readModule();
            Assert.Equal(before.LastSettledAttemptId, after.LastSettledAttemptId);
            Assert.Equal(before.AppliedPublicationSequence, after.AppliedPublicationSequence);
            Assert.Equal(before.AppliedPlanEpoch, after.AppliedPlanEpoch);
        }
        finally
        {
            release.Set();
            if (publication is not null) await publication.WaitAsync(TimeSpan.FromSeconds(5));
            provider.Published -= Block;
            await owner.StopAsync(CancellationToken.None);
        }
    }

    private sealed class UnusedModelProvider : IAiModelRuntimeProvider
    {
        public Task<AiModelRuntimeStatus> GetStatusAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("No model work was requested.");
        public Task<IReadOnlyList<AiModelDescriptor>> ListModelsIfRunningAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("No model work was requested.");
        public Task<AiModelLoadResult> LoadAsync(AiModelLoadRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("No model work was requested.");
        public Task<AiModelUnloadResult> UnloadAsync(AiModelUnloadRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("No model work was requested.");
        public Task ForwardAsync(HttpContext context, string relativePath, CancellationToken cancellationToken)
            => throw new InvalidOperationException("No model work was requested.");
    }
}
