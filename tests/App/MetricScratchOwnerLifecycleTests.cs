using static Resource_Manager_APP.Tests.MetricSnapshotOwnerTestFixture;
using MetricScratchRecording = Resource_Manager_APP.Tests.NativeMetricSnapshotAbiTests.MetricScratchRecording;
using System.Reflection;
using System.Runtime.CompilerServices;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native;

namespace Resource_Manager_APP.Tests;

public sealed class MetricScratchOwnerLifecycleTests
{
    [Fact]
    public async Task FirstHolderCreatesLastReturnClosesAndLaterAcquireUsesCurrentPlan()
    {
        var regions = new List<MetricScratchRecording>();
        using var fixture = new Fixture(Topology([]), (length, alignment) =>
        { var region = new MetricScratchRecording(length, alignment); regions.Add(region); return region; });
        fixture.ReleaseWorkspace();
        Assert.Equal(0UL, fixture.Owner.CurrentWorkspaceIdentity);
        Assert.False(fixture.Owner.TryAcquire(out _));
        Assert.Equal(1, regions[0].Retires);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fixture.Owner.AcquireAsync(cancelled.Token));
        Assert.Single(regions);

        var first = fixture.Owner.Acquire();
        var second = fixture.Owner.Acquire();
        var identity = first.WorkspaceIdentity;
        Assert.Same(first.Workspace, second.Workspace);
        first.Dispose();
        Assert.Equal(0, regions[1].Retires);
        second.Dispose();
        second.Dispose();
        Assert.Equal(1, regions[1].Retires);
        Assert.Equal(0UL, fixture.Owner.CurrentWorkspaceIdentity);

        var next = HostManagerTestPlanFactory.CreatePlan(root =>
            root["profile_revision"] = root["profile_revision"]!.GetValue<int>() + 1, planEpoch: 2);
        fixture.Slots.Provider.Publish(fixture.Slots.Provider.Current with { Version = 2, HostManager = next });
        Assert.Equal(2, regions.Count);
        using var reopened = fixture.Owner.Acquire();
        Assert.NotEqual(identity, reopened.WorkspaceIdentity);
        Assert.Equal(next.MetricSnapshot.HotPublish.ConfigurationGeneration, reopened.Workspace.Configuration.Generation);
    }

    [Fact]
    public void MultipleAlreadyRetiredHoldersKeepTheirOwnCloseFailuresAndBlockAnotherReplacement()
    {
        var regions = new List<MetricScratchRecording>();
        using var fixture = new Fixture(Topology([]), (length, alignment) =>
        { var region = new MetricScratchRecording(length, alignment); regions.Add(region); return region; });
        var first = fixture.Owner.Acquire();
        Replace(1);
        var second = fixture.Owner.Acquire();
        Replace(2);
        regions[0].FailRetire = true; regions[1].FailRetire = true;
        Assert.Same(regions[0].RetireFailure, Assert.Throws<InvalidOperationException>(() => first.Dispose()));
        Assert.Same(regions[1].RetireFailure, Assert.Throws<InvalidOperationException>(() => second.Dispose()));
        // Reference counts were already released once; retrying the lease itself cannot decrement again.
        first.Dispose(); second.Dispose();
        var current = fixture.Owner.CurrentWorkspaceIdentity;
        fixture.Topology = Topology([new(3, "pending-third", 1)]);
        fixture.Owner.RequestTopologyRefresh();
        var both = Assert.Throws<AggregateException>(() => fixture.Owner.RefreshTopologyIfRequested());
        Assert.Contains(regions[0].RetireFailure, both.InnerExceptions);
        Assert.Contains(regions[1].RetireFailure, both.InnerExceptions);
        Assert.Equal(3, regions.Count);
        Assert.Equal(current, fixture.Owner.CurrentWorkspaceIdentity);
        regions[0].FailRetire = false;
        Assert.Same(regions[1].RetireFailure,
            Assert.Throws<InvalidOperationException>(() => fixture.Owner.RefreshTopologyIfRequested()));
        Assert.Equal(1, regions[0].Retires);
        Assert.Equal(0, regions[1].Retires);
        Assert.Equal(3, regions.Count);
        regions[1].FailRetire = false;
        fixture.Owner.RefreshTopologyIfRequested();
        Assert.Equal(4, regions.Count);
        Assert.NotEqual(current, fixture.Owner.CurrentWorkspaceIdentity);
        fixture.ReleaseWorkspace(); fixture.Owner.Dispose(); fixture.ReleaseWorkspace(); fixture.Owner.Dispose();
        Assert.All(regions, region => Assert.Equal(1, region.Retires));

        void Replace(int ordinal)
        {
            fixture.Topology = Topology([new(ordinal, $"recorded-{ordinal}", 1)]);
            fixture.Owner.RequestTopologyRefresh();
            fixture.Owner.RefreshTopologyIfRequested();
        }
    }

    [Fact]
    public void LastExternalLeaseCanFailEndUseAfterOwnerDisposeAndTheSameOwnerRetriesIt()
    {
        MetricScratchRecording? region = null;
        using var fixture = new Fixture(Topology([]), (length, alignment) => region = new(length, alignment));
        var lease = fixture.Owner.Acquire();
        OpenCompletion(lease.Workspace);
        region!.FailReturn = true;
        fixture.ReleaseWorkspace(); fixture.Owner.Dispose(); // The external lease is still live; no forced drain.
        Assert.Equal(1, region.Returns); // Only the completed Plan has returned.
        Assert.Same(region.ReturnFailure, Assert.Throws<InvalidOperationException>(() => lease.Dispose()));
        Assert.Equal(region.Pins, region.Unpins);
        Assert.Equal(0, region.Retires);
        Assert.Same(region.ReturnFailure, Assert.Throws<InvalidOperationException>(() => fixture.Owner.Dispose()));
        region.FailReturn = false;
        fixture.ReleaseWorkspace(); fixture.Owner.Dispose(); fixture.ReleaseWorkspace(); fixture.Owner.Dispose(); lease.Dispose();
        Assert.Equal(2, region.Returns);
        Assert.Equal(2, region.Unpins);
        Assert.Equal(1, region.Retires);
    }

    [Fact]
    public void BothDirectoryReleaseFailuresStayVisibleAndOnlyUnreleasedLoansAreRetried()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        var path = Path.Combine(Path.GetTempPath(), $"metric-close-directories-{Guid.NewGuid():N}.bin");
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled, path);
        var closed = 0;
        var holder = new HostManagerMetricSnapshotOwner.WorkspaceHolder(1, workspace, Topology([]), _ => closed++);
        var intel = new DirectoryLoan("intel directory release");
        var npu = new DirectoryLoan("npu directory release");
        typeof(HostManagerMetricSnapshotOwner.WorkspaceHolder)
            .GetField("intelFanDirectoryLease", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(holder, intel);
        typeof(HostManagerMetricSnapshotOwner.WorkspaceHolder)
            .GetField("npuEngineDirectoryLease", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(holder, npu);
        var both = Assert.Throws<AggregateException>(() => holder.Retire());
        Assert.Same(intel.Failure, both.InnerExceptions[0]);
        Assert.Same(npu.Failure, both.InnerExceptions[1]);
        Assert.Same(both, holder.CloseFailure);
        Assert.Equal(0, closed);
        intel.Fail = false;
        Assert.Same(npu.Failure, Assert.Throws<InvalidOperationException>(() => holder.CloseIfUnused()));
        Assert.Same(both, holder.CloseFailure);
        Assert.Equal(2, intel.Attempts); Assert.Equal(2, npu.Attempts);
        npu.Fail = false;
        holder.CloseIfUnused(); holder.CloseIfUnused();
        Assert.Equal(2, intel.Attempts); Assert.Equal(3, npu.Attempts);
        Assert.Null(holder.CloseFailure); Assert.Equal(1, closed);
    }

    [Fact]
    public void FailedInitialDirectoryReleaseRetiresUnpublishedCandidateAndRetainsEveryCloseObligation()
    {
        var regions = new List<MetricScratchRecording>();
        using var fixture = new Fixture(Topology([]), (length, alignment) =>
        {
            var region = new MetricScratchRecording(length, alignment) { FailRetire = regions.Count == 1 };
            regions.Add(region); return region;
        });
        var before = fixture.Owner.CurrentWorkspaceIdentity;
        var intel = new DirectoryLoan("initial Intel directory reference");
        var npu = new DirectoryLoan("initial NPU directory reference");
        var topology = Topology([]) with { IgclFanDirectoryReference = intel, NpuEngineDirectoryReference = npu };
        var method = typeof(HostManagerMetricSnapshotOwner).GetMethod("CreateWorkspace", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var invocation = Assert.Throws<TargetInvocationException>(() => method.Invoke(fixture.Owner,
            [HostManagerTestPlanFactory.CreatePlan().MetricSnapshot, topology]));
        var failures = Assert.IsType<AggregateException>(invocation.InnerException);
        var original = Assert.IsType<AggregateException>(failures.InnerExceptions[0]);
        Assert.Same(intel.Failure, original.InnerExceptions[0]);
        Assert.Same(npu.Failure, original.InnerExceptions[1]);
        Assert.Same(regions[1].RetireFailure, failures.InnerExceptions[1]);
        Assert.Equal(before, fixture.Owner.CurrentWorkspaceIdentity);
        Assert.Equal(2, regions.Count);
        Assert.Equal(0, regions[1].Retires);
        Assert.Equal(1, intel.Attempts); Assert.Equal(1, npu.Attempts);
        fixture.Owner.RequestTopologyRefresh();
        Assert.Same(regions[1].RetireFailure,
            Assert.Throws<InvalidOperationException>(() => fixture.Owner.RefreshTopologyIfRequested()));
        Assert.Equal(2, regions.Count); // Another replacement cannot hide the failed candidate.
        intel.Fail = false; npu.Fail = false; regions[1].FailRetire = false;
        fixture.ReleaseWorkspace(); fixture.Owner.Dispose(); fixture.ReleaseWorkspace(); fixture.Owner.Dispose();
        Assert.Equal(2, intel.Attempts); Assert.Equal(2, npu.Attempts);
        Assert.All(regions, region => Assert.Equal(1, region.Retires));
    }

    private static void OpenCompletion(NativeMetricSnapshotWorkspace workspace)
    {
        var catalog = workspace.ReadCatalog();
        var modes = catalog.Sources.Select(source => new NativeMetricSnapshotSourceRuntimeState(source.SourceHandle,
            1, NativeMetricSnapshotSourceAvailability.Available)).ToArray();
        var at = DateTimeOffset.UtcNow;
        var plan = workspace.PlanCollection([catalog.MetricHandles.Values.First()], modes, false, at);
        var metric = plan.Metrics.First(row => (row.Flags & (uint)NativeMetricSnapshotMetricPlanFlags.Selected) != 0);
        var source = plan.Sources.Single(row => row.SourceHandle == metric.SourceHandle);
        var header = new NativeMetricSnapshotCompletionHeader
        {
            AbiVersion = NativeMetricSnapshotAbi.Version,
            StructSize = checked((uint)Unsafe.SizeOf<NativeMetricSnapshotCompletionHeader>()),
            ConfigurationGeneration = workspace.Configuration.Generation, CatalogGeneration = plan.Header.CatalogGeneration,
            OperationEpoch = plan.Header.OperationEpoch + 1, PlanEpoch = plan.Header.PlanEpoch,
            SourceHandle = source.SourceHandle, SourceIncarnation = 1, SourceObservationSequence = 1,
            CapabilityGeneration = source.CapabilityGeneration, PlanTokenFingerprint = source.PlanTokenFingerprint,
            CommandAtMilliseconds = checked((ulong)at.ToUnixTimeMilliseconds()),
            CapturedAtMilliseconds = checked((ulong)at.ToUnixTimeMilliseconds()),
            RequestedRuleCount = source.RequestedRuleCount, Status = (uint)NativeMetricSnapshotSourceStatus.Unavailable
        };
        Assert.Equal(NativeMetricSnapshotStatus.Ok, workspace.Execute(session => session.BeginCompletion(in header)));
    }

    private sealed class DirectoryLoan(string message) : IDisposable
    {
        internal readonly InvalidOperationException Failure = new(message);
        internal bool Fail = true;
        internal int Attempts;
        public void Dispose() { Attempts++; if (Fail) throw Failure; }
    }
}
