using System.Buffers;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.Ledger;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Devices.Display;

namespace Resource_Manager_APP.Tests;

public sealed class DisplayNormalizationLedgerTests
{
    [Fact]
    public void CheckpointImportBorrowsAndReturnsTheSameExternalNormalizationResource()
    {
        using var owner = new ResourceManagerLedgerOwner();
        owner.SetMode(LedgerTableMode.Optimize);
        var configuration = HostManagerDisplayCoordinatorOwner.CreateConfiguration(HostManagerTestPlanFactory.CreatePlan().DisplayCoordinator);
        NativeDisplayCoordinatorPersistenceImage? image = null;
        using (var source = new NativeDisplayCoordinatorWorkspace(configuration,
            new NativeDisplayCoordinatorSession(in configuration),
            static () => new DeviceTopologyDisplayPathSnapshot([Path()], null),
            new NativeDisplayCoordinatorPersistenceCallbacks(_ => null, (_, saved) => image = saved)))
            source.Refresh([]);
        Assert.NotNull(image);
        ProbedScratch? scratch = null;
        var session = new NativeDisplayCoordinatorSession(in configuration,
            length => scratch = new(new DisplayNormalizationScratch(owner.CreateScratch(length)),
                () => Assert.Equal(0UL, owner.Tick().Released)));
        using var imported = new NativeDisplayCoordinatorWorkspace(configuration, session,
            static () => new DeviceTopologyDisplayPathSnapshot([], null),
            new NativeDisplayCoordinatorPersistenceCallbacks(_ => image, (_, _) => { }));
        Assert.Equal(1, scratch!.Acquired); Assert.Equal(1, scratch.Returned);
        Assert.Single(imported.ReadCurrent().Current.DisplayPaths);
        Assert.Equal(1UL, owner.Tick().Released);
        Assert.Single(imported.ReadCurrent().Current.DisplayPaths);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
    }

    [Fact]
    public void ActualNormalizationUsesOneLazyIdentityPerSessionAndReclaimsBetweenRefreshes()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var configuration = HostManagerDisplayCoordinatorOwner.CreateConfiguration(HostManagerTestPlanFactory.CreatePlan().DisplayCoordinator);
        ResourceManagerLedgerOwner.ComputeScratch? identity = null;
        ProbedScratch? scratch = null;
        var session = new NativeDisplayCoordinatorSession(in configuration, length =>
        {
            identity = owner.CreateScratch(length);
            return scratch = new(new DisplayNormalizationScratch(identity), () =>
            {
                Assert.Equal(length, owner.ReadTable().RegisteredBytes);
                Assert.Equal(0UL, owner.Tick().Released);
            });
        });
        using var workspace = new NativeDisplayCoordinatorWorkspace(configuration, session,
            static () => new DeviceTopologyDisplayPathSnapshot([Path()], null));
        using var ordinary = new NativeDisplayCoordinatorSession(in configuration);
        Assert.Equal(ordinary.Capacity.ResidentByteCount - configuration.MaximumTextByteCount, session.Capacity.ResidentByteCount);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        Assert.Equal(1_048_576UL, identity!.Length);
        var uid = identity.Uid;
        owner.SetMode(LedgerTableMode.Optimize);
        Assert.Single(workspace.Refresh([]).DisplayPaths);
        Assert.Equal(6, scratch!.Acquired); Assert.Equal(scratch.Acquired, scratch.Returned);
        ulong first;
        using (var use = identity.Acquire()) first = use.AllocationNumber;
        Assert.Equal(1UL, owner.Tick().Released);
        Assert.Single(workspace.ReadCurrent().Current.DisplayPaths);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        workspace.Refresh([]);
        Assert.Equal(uid, identity.Uid);
        using (var use = identity.Acquire()) Assert.NotEqual(first, use.AllocationNumber);
        Assert.Equal(12, scratch.Acquired); Assert.Equal(scratch.Acquired, scratch.Returned);
        workspace.Dispose();
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
    }

    [Fact]
    public void FailedRetiredCloseKeepsNativeSessionAndLedgerResourceForExplicitRetry()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var configuration = HostManagerDisplayCoordinatorOwner.CreateConfiguration(HostManagerTestPlanFactory.CreatePlan().DisplayCoordinator);
        ProbedScratch? scratch = null;
        var session = new NativeDisplayCoordinatorSession(in configuration,
            length => scratch = new(new DisplayNormalizationScratch(owner.CreateScratch(length)), static () => { }));
        var workspace = new NativeDisplayCoordinatorWorkspace(configuration, session,
            static () => new DeviceTopologyDisplayPathSnapshot([Path()], null));
        workspace.Refresh([]);
        var holder = new HostManagerDisplayCoordinatorOwner.WorkspaceHolder(workspace);
        holder.AddReference();
        holder.Retire();
        Assert.False(holder.TryClose());
        scratch!.CloseFailures = 1;
        Assert.Throws<IOException>(() => holder.Release());
        Assert.Equal(NativeDisplayCoordinatorStatus.Ok, session.Snapshot(out _));
        Assert.NotEqual(0UL, owner.ReadTable().RegisteredBytes);
        Assert.True(holder.TryClose());
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        Assert.True(holder.TryClose());
    }

    private static DeviceTopologyDisplayPath Path() => new(
        AdapterHighPart: 1, AdapterLowPart: 2, SourceId: 3, TargetId: 4, OutputTechnology: 10,
        ConnectorInstance: 5, MonitorFriendlyName: "Test display", MonitorDevicePath: @"DISPLAY\TEST\1",
        Width: 2560, Height: 1440, RefreshRateNumerator: 165000, RefreshRateDenominator: 1000,
        Active: true, TargetAvailable: true, AdapterDevicePath: @"PCI\VEN_10DE",
        SourceDeviceName: @"\\.\DISPLAY1", PositionX: 0, PositionY: 0);

    private sealed class ProbedScratch(INativeDisplayNormalizationScratch inner, Action duringPin) : INativeDisplayNormalizationScratch
    {
        private readonly Action probe = duringPin;
        internal int Acquired, Returned, CloseFailures;
        public ulong Length => inner.Length;
        public NativeDisplayNormalizationLease Acquire() { Acquired++; return new Lease(this, inner.Acquire()); }
        public void Dispose()
        {
            if (CloseFailures > 0) { CloseFailures--; throw new IOException("recorded normalization Close failure"); }
            inner.Dispose();
        }
        private sealed class Lease(ProbedScratch owner, NativeDisplayNormalizationLease inner) : NativeDisplayNormalizationLease
        {
            internal override MemoryHandle Pin() { owner.probe(); return inner.Pin(); }
            internal override void Complete() => inner.Complete();
            public override void Dispose() { inner.Dispose(); owner.Returned++; }
        }
    }
}
