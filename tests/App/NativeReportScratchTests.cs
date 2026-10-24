using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.Ledger;
using ResourceManager.Ledger.Generated;

namespace Resource_Manager_APP.Tests;

public sealed class NativeReportScratchTests
{
    [Fact]
    public void ActualRowsUseSmallScratchAndGrowEachDimensionWithoutKeepingTheOldHandle()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var resources = new List<ResourceManagerLedgerOwner.ComputeScratch>();
        using var workspace = new NativeReportCoordinatorWorkspace(
            HostManagerTestPlanFactory.CreatePlan().ReportCoordinator, 101, 102,
            length => { var resource = ledger.CreateScratch(length); resources.Add(resource); return resource; });
        Assert.Empty(resources);
        workspace.EnsureScratch(1, 2);
        Assert.Equal(1056UL, workspace.Layout.Length);
        using (var lease = workspace.AcquireScratch())
        {
            Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
            lease.Complete();
        }
        var first = Assert.Single(resources);
        workspace.EnsureScratch(20, 2);
        Assert.Throws<ObjectDisposedException>(() => first.Acquire());
        var second = resources[1];
        Assert.NotEqual(first.Uid, second.Uid);
        workspace.EnsureScratch(1, 40);
        Assert.Throws<ObjectDisposedException>(() => second.Acquire());
        Assert.Equal(20, workspace.Layout.ReportCapacity);
        Assert.Equal(40, workspace.Layout.PersistenceCapacity);
        Assert.Equal(21120UL, workspace.Layout.Length);
        var current = resources[2];
        workspace.EnsureScratch(2, 3);
        Assert.Equal(3, resources.Count);
        using (var lease = workspace.AcquireScratch()) { lease.Complete(); }
        ledger.SetMode(LedgerTableMode.Optimize);
        Assert.Equal(1UL, ledger.Tick().Released);
        using (var lease = workspace.AcquireScratch())
        {
            Assert.Equal(current.Uid, resources[2].Uid);
            lease.Complete();
        }
    }

    [Theory]
    [InlineData(1024u, 32768u, 14352384UL)]
    [InlineData(4096u, 196608u, 85721088UL)]
    [InlineData(2000000u, 6000000u, 2976000000UL)]
    public void LayoutUsesThreeActualStridesWithoutAnInt32ByteLimit(uint reports, uint persistence, ulong bytes)
    {
        var layout = new NativeReportScratchLayout(reports, persistence);
        Assert.Equal(192, Marshal.SizeOf<NativeReportOutput>());
        Assert.Equal(352, Marshal.SizeOf<NativeReportPersistenceOperation>());
        Assert.Equal(80, Marshal.SizeOf<NativeReportPersistenceFeedback>());
        Assert.Equal(192UL * reports, layout.Persistence);
        Assert.Equal(192UL * reports + 352UL * persistence, layout.Feedback);
        Assert.Equal(bytes, layout.Length);
        Assert.Equal(0UL, layout.Feedback % 8);
    }

    [Fact]
    public void FailedGrowthCanRebindThePreviousHighWaterForSmallerDemand()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var resources = new List<ResourceManagerLedgerOwner.ComputeScratch>();
        var failNext = false;
        using var workspace = new NativeReportCoordinatorWorkspace(
            HostManagerTestPlanFactory.CreatePlan().ReportCoordinator, 101, 102,
            length =>
            {
                if (failNext) { failNext = false; throw new IOException("recorded report scratch growth failure"); }
                var resource = ledger.CreateScratch(length);
                resources.Add(resource);
                return resource;
            });
        workspace.EnsureScratch(1, 10);
        var oldLayout = workspace.Layout;
        var first = Assert.Single(resources);
        failNext = true;
        Assert.Throws<IOException>(() => workspace.EnsureScratch(20, 40));
        Assert.True(first.Closed);
        Assert.Same(oldLayout, workspace.Layout);
        workspace.EnsureScratch(1, 1);
        Assert.Equal(2, resources.Count);
        Assert.Equal(oldLayout.Length, resources[1].Length);
        using var lease = workspace.AcquireScratch();
        lease.Complete();
    }

    [Fact]
    public void ConstructorIsLazyAndAllThreeColumnsUseOneRealRegion()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        using var workspace = Create(ledger.CreateScratch);
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
        using (var lease = workspace.AcquireScratch())
        {
            using var view = new ComputeScratchView(lease);
            view.Column<NativeReportOutput>(workspace.Layout.Reports, workspace.Layout.ReportCapacity)[0]
                = new() { ReportHandle = 11 };
            view.Column<NativeReportPersistenceOperation>(workspace.Layout.Persistence,
                workspace.Layout.PersistenceCapacity)[0] = new() { IdentityHandle = 12 };
            view.Column<NativeReportPersistenceFeedback>(workspace.Layout.Feedback,
                workspace.Layout.PersistenceCapacity)[0] = new() { IdentityHandle = 13 };
            Assert.Equal(workspace.Layout.Length, ledger.ReadTable().RegisteredBytes);
            ledger.SetMode(LedgerTableMode.Optimize);
            Assert.Equal(0UL, ledger.Tick().Released);
            lease.Complete();
        }
        using var next = workspace.AcquireScratch();
        using var nextView = new ComputeScratchView(next);
        Assert.Equal(11UL, nextView.Column<NativeReportOutput>(0, workspace.Layout.ReportCapacity)[0].ReportHandle);
        Assert.Equal(12UL, nextView.Column<NativeReportPersistenceOperation>(workspace.Layout.Persistence,
            workspace.Layout.PersistenceCapacity)[0].IdentityHandle);
        Assert.Equal(13UL, nextView.Column<NativeReportPersistenceFeedback>(workspace.Layout.Feedback,
            workspace.Layout.PersistenceCapacity)[0].IdentityHandle);
    }

    [Fact]
    public void IdleReallocationKeepsUidAndNativeSessionWhileIndependentResultsSurvivePoison()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        ResourceManagerLedgerOwner.ComputeScratch? resource = null;
        using var workspace = Create(length => resource = ledger.CreateScratch(length));
        var session = workspace.Session;
        var residentBytes = session.Capacity.ResidentByteCount;
        var uid = resource!.Uid;
        ulong allocation;
        NativeReportOutput[] copied;
        using (var lease = workspace.AcquireScratch())
        {
            allocation = lease.AllocationNumber;
            using var view = new ComputeScratchView(lease);
            var reports = view.Column<NativeReportOutput>(0, workspace.Layout.ReportCapacity);
            reports[0] = new() { ReportHandle = 77, CurrentValue = 12.5 };
            copied = reports[..1].ToArray();
            lease.Complete();
        }
        ledger.SetMode(LedgerTableMode.Optimize);
        Assert.Equal(1UL, ledger.Tick().Released);
        using (var next = workspace.AcquireScratch())
        {
            Assert.NotEqual(allocation, next.AllocationNumber);
            using var view = new ComputeScratchView(next);
            view.Column<byte>(0, checked((int)workspace.Layout.Length)).Fill(0xA5);
            Assert.Equal(uid, resource.Uid);
            Assert.Same(session, workspace.Session);
            Assert.Equal(residentBytes, session.Capacity.ResidentByteCount);
            next.Complete();
        }
        Assert.Equal(77UL, Assert.Single(copied).ReportHandle);
        Assert.Equal(12.5, copied[0].CurrentValue);
    }

    [Fact]
    public void CloseInUseRetainsNativeSessionUntilLeaseReturnsThenCanBeRetried()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        using var workspace = Create(ledger.CreateScratch);
        var lease = workspace.AcquireScratch();
        try
        {
            lease.Complete();
            Assert.Equal(LedgerStatus.InUse, Assert.Throws<LedgerException>(workspace.Dispose).Status);
            Assert.NotEqual(0U, workspace.Session.Capacity.ReportOutputCapacity);
            Assert.Throws<ObjectDisposedException>(() => workspace.AcquireScratch());
            Assert.Equal(workspace.Layout.Length, ledger.ReadTable().RegisteredBytes);
        }
        finally { lease.Dispose(); }
        workspace.Dispose();
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
        Assert.Throws<ObjectDisposedException>(() => _ = workspace.Session.Capacity);
    }

    [Fact]
    public void ExhaustedRealTableRejectsAllocationWithoutAnOrdinaryFallback()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var resources = new List<ResourceManagerLedgerOwner.ComputeScratch>();
        var leases = new List<ComputeScratchLease>();
        var blocked = false;
        try
        {
            // Bound discovery by the real paired generated owner, not a past table size.
            var maximumAttempts = checked((int)LedgerOwnerBuild.Identity.ResourceCapacity + 1);
            for (var index = 0; index < maximumAttempts; index++)
            {
                var resource = ledger.CreateScratch(8192);
                try
                {
                    var lease = resource.Acquire();
                    resources.Add(resource);
                    leases.Add(lease);
                    lease.Complete();
                }
                catch (LedgerException error) when (error.Status == LedgerStatus.Blocked)
                {
                    resource.Dispose();
                    blocked = true;
                    break;
                }
            }
            Assert.NotEmpty(resources);
            Assert.True(blocked);
            var registered = checked(8192UL * (ulong)resources.Count);
            using var workspace = Create(ledger.CreateScratch);
            Assert.Equal(registered, ledger.ReadTable().RegisteredBytes);
            var failure = Assert.Throws<LedgerException>(() => workspace.AcquireScratch());
            Assert.Equal(LedgerStatus.Blocked, failure.Status);
            Assert.Equal(registered, ledger.ReadTable().RegisteredBytes);
        }
        finally
        {
            foreach (var lease in leases) lease.Dispose();
            foreach (var resource in resources) resource.Dispose();
        }
    }

    [Fact]
    public void WrongLengthFactoryClosesItsCreatedHandleAndDoesNotRegisterAnything()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        ResourceManagerLedgerOwner.ComputeScratch? created = null;
        Assert.Throws<InvalidOperationException>(() => Create(length => created = ledger.CreateScratch(length + 8)));
        Assert.NotNull(created);
        Assert.Throws<ObjectDisposedException>(() => created.Acquire());
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
        using var valid = Create(ledger.CreateScratch);
        Assert.NotEqual(0U, valid.Session.Capacity.ReportOutputCapacity);
    }

    private static NativeReportCoordinatorWorkspace Create(Func<ulong, IComputeScratch> factory)
    {
        var workspace = new NativeReportCoordinatorWorkspace(
            HostManagerTestPlanFactory.CreatePlan().ReportCoordinator, 101, 102, factory);
        try { workspace.EnsureScratch(16, 32); return workspace; }
        catch { workspace.Dispose(); throw; }
    }
}
