using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.PublicServices;
using ResourceManager.App.Domain.PublicServices.AiModels;
using ResourceManager.App.Domain.Settings;
using ResourceManager.Ledger;

namespace Resource_Manager_APP.Tests;

public sealed class PublicServiceLedgerScratchTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AcceptedModelsRemainTheSourceWhenReturningScratchFails(
        bool failComplete, bool changeCount)
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var plan = HostManagerTestPlanFactory.CreatePlan(root =>
            root["host_recreate"]!["public_service_coordinator"]![
                "model_catalog_acquisition_interval_ms"] = 1,
            publicService: new AppLocalPublicServiceSettings(true, true, true, true)).PublicServiceCoordinator;
        FailingScratch? scratch = null;
        using var workspace = new NativePublicServiceCoordinatorWorkspace(plan, 1, 1, 1,
            length => scratch = new FailingScratch(ledger.CreateScratch(length)));
        var projection = new NativePublicServiceModelProjection();
        var first = projection.Project([Model("old")], plan.HotPublish.AiProviderHandle);
        var acquisition = workspace.PlanModelAcquisition();
        workspace.PublishModels(acquisition.AttemptHandle, 1, first);
        acquisition = NextAcquisition(workspace);
        var accepted = projection.Project(changeCount ? [Model("new"), Model("other")] : [Model("new")],
            plan.HotPublish.AiProviderHandle);
        scratch!.FailNextComplete = failComplete;
        scratch.FailNextReturn = !failComplete;
        Assert.Throws<IOException>(() => workspace.PublishModels(acquisition.AttemptHandle, 2, accepted));

        Assert.Equal(2UL, workspace.ReadSnapshot().ModelGeneration);
        Assert.Same(accepted.Payloads, workspace.CaptureModels());
        Assert.Equal(accepted.Models.Length, checked((int)workspace.ReadSnapshot().ModelCount));
        Assert.False(((NativePublicServiceCoordinatorSnapshotFlags)workspace.ReadSnapshot().Flags)
            .HasFlag(NativePublicServiceCoordinatorSnapshotFlags.ModelAcquisitionRunning));
        ledger.SetMode(LedgerTableMode.Optimize);
        Assert.Equal(0UL, ledger.Tick().Released);
        var resolved = workspace.ResolveModel("new");
        Assert.Equal((uint)NativePublicServiceModelResolveStatus.Matched, resolved.Status);
        Assert.Equal("new", workspace.CaptureModels().ByPayloadHandle[resolved.PayloadHandle].Key);
        Assert.NotEqual((uint)NativePublicServiceModelResolveStatus.Matched, workspace.ResolveModel("old").Status);
        var task = workspace.EnqueueTask(plan.HotPublish.AnonymousCallerHandle, resolved.ModelHandle,
            1, 0, NativePublicServiceTaskKind.Load);
        Assert.NotEqual(0UL, task.TaskHandle);
        workspace.CancelQueuedTasks();
        Assert.Equal(1UL, ledger.Tick().Released);
        Assert.Equal((uint)NativePublicServiceModelResolveStatus.Matched, workspace.ResolveModel("new").Status);

        acquisition = NextAcquisition(workspace);
        workspace.PublishModels(acquisition.AttemptHandle, 3, first);
        Assert.Same(first.Payloads, workspace.CaptureModels());
        Assert.Equal((uint)NativePublicServiceModelResolveStatus.Matched, workspace.ResolveModel("old").Status);
    }

    private static AiModelDescriptor Model(string key) => new(
        "llm", "test", key, key, null, "gguf", "Q4_K_M", 4, 1, "1B", 4096, [], []);

    private static NativePublicServiceModelAcquisitionPlanOutput NextAcquisition(
        NativePublicServiceCoordinatorWorkspace workspace)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline)
        {
            var acquisition = workspace.PlanModelAcquisition();
            if (acquisition.Status == (uint)NativePublicServiceModelAcquisitionPlanStatus.Start)
                return acquisition;
            Assert.Equal((uint)NativePublicServiceModelAcquisitionPlanStatus.NotDue, acquisition.Status);
            Thread.Sleep(10);
        }
        throw new TimeoutException("The native model acquisition did not become due.");
    }

    [Fact]
    public void FailedEndUseRetainsTheRealLeaseUntilTheNextOperationRetries()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var plan = HostManagerTestPlanFactory.CreatePlan().PublicServiceCoordinator;
        using var workspace = new NativePublicServiceCoordinatorWorkspace(plan, 1, 1, 1,
            length => new FailingScratch(ledger.CreateScratch(length), failReturn: true));
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
        ledger.SetMode(LedgerTableMode.Optimize);
        Assert.Throws<IOException>(() => workspace.ResolveModel("unavailable"));
        Assert.Equal(34_291_719UL, ledger.ReadTable().RegisteredBytes);
        Assert.Equal(0UL, ledger.Tick().Released);
        var output = workspace.ResolveModel("unavailable");
        Assert.Equal((uint)NativePublicServiceModelResolveStatus.CatalogUnavailable, output.Status);
        Assert.Equal(1UL, ledger.Tick().Released);
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
    }

    [Fact]
    public void FailedResourceCloseCanBeRetriedWithoutDestroyingTheSession()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var plan = HostManagerTestPlanFactory.CreatePlan().PublicServiceCoordinator;
        using var workspace = new NativePublicServiceCoordinatorWorkspace(plan, 1, 1, 1,
            length => new FailingScratch(ledger.CreateScratch(length), failClose: true));
        workspace.ResolveModel("unavailable");
        Assert.Throws<IOException>(() => workspace.Dispose());
        Assert.Equal(34_291_719UL, ledger.ReadTable().RegisteredBytes);
        Assert.NotEqual(0UL, workspace.ReadSnapshot().CatalogGeneration);
        workspace.Dispose();
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
    }

    internal sealed class FailingScratch(IComputeScratch inner, bool failReturn = false, bool failClose = false)
        : IComputeScratch
    {
        private bool closeFailure = failClose;
        internal bool FailNextReturn { get; set; } = failReturn;
        internal bool FailNextComplete { get; set; }
        internal Action? BeforeReturn { get; set; }
        public ulong Length => inner.Length;
        public ComputeScratchLease Acquire() => new Reading(this, inner.Acquire());
        public void Dispose()
        {
            if (closeFailure) { closeFailure = false; throw new IOException("Injected close failure."); }
            inner.Dispose();
        }

        private sealed class Reading(FailingScratch owner, ComputeScratchLease inner) : ComputeScratchLease
        {
            internal override ulong Length => inner.Length;
            internal override ulong AllocationNumber => inner.AllocationNumber;
            internal override bool IsLoading => inner.IsLoading;
            internal override Memory<byte> GetMemoryChunk(ulong offset, int count) => inner.GetMemoryChunk(offset, count);
            internal override void Complete()
            {
                if (owner.FailNextComplete) { owner.FailNextComplete = false; throw new IOException("Injected Complete failure."); }
                inner.Complete();
            }
            public override void Dispose()
            {
                owner.BeforeReturn?.Invoke();
                if (owner.FailNextReturn) { owner.FailNextReturn = false; throw new IOException("Injected endUse failure."); }
                inner.Dispose();
            }
        }
    }
}
