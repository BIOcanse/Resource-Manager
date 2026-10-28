using System.Buffers;
using ResourceManager.ReadWrite.Native;
using System.Runtime.CompilerServices;
using System.Text;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.Ledger;
using ResourceManager.App.Domain.RuntimeSpecialization;
using Microsoft.Extensions.Logging.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed class NativeFileQueryLedgerScratchTests
{
    [Fact]
    public async Task OwnerAllocatesOnlyWhileQueriesAreHeldAndClosesOnFinalReturn()
    {
        using var memory = new ResourceManagerLedgerOwner();
        var resources = new List<FaultingScratch>();
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = 1,
            HostManager = HostManagerTestPlanFactory.CreatePlan(root =>
                root["host_recreate"]!["file_query"]!["query_session_count"] = 2)
        });
        using var owner = new HostManagerFileQueryOwner(provider,
            new HostManagerFileQueryRuntime(provider, deployment),
            NullLogger<HostManagerFileQueryOwner>.Instance, (length, alignment) =>
            {
                var scratch = new FaultingScratch(FileQueryScratch.Create(memory.CreateScratch, length, alignment));
                resources.Add(scratch);
                return scratch;
            });
        Assert.Empty(resources);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await owner.AcquireAsync(cancelled.Token));
        Assert.Empty(resources);
        var first = await owner.AcquireAsync(CancellationToken.None);
        var second = await owner.AcquireAsync(CancellationToken.None);
        Assert.Equal(2, resources.Count);
        first.MarkReusable(); second.MarkReusable();
        first.Dispose();
        Assert.All(resources, scratch => Assert.False(scratch.Closed));
        second.Dispose(); second.Dispose();
        Assert.All(resources, scratch => Assert.True(scratch.Closed));
        using var reopened = await owner.AcquireAsync(CancellationToken.None);
        Assert.Equal(4, resources.Count);
    }

    [Fact]
    public async Task DefaultPoolConstructsOnlyMetadataAndRegistersScratchOnlyForBorrowedSessions()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var resources = new List<ResourceManagerLedgerOwner.ComputeScratch>();
        var plan = HostManagerTestPlanFactory.CreatePlan().FileQuery;
        using var workspace = new NativeFileQueryWorkspace(plan, (length, alignment) =>
            FileQueryScratch.Create(bytes =>
            {
                var resource = owner.CreateScratch(bytes);
                resources.Add(resource);
                return resource;
            }, length, alignment));
        Assert.Equal(8, resources.Count);
        Assert.All(resources, resource => Assert.Equal(8_784_695UL, resource.Length));
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        NativeFileQuerySession session;
        using (var query = await workspace.AcquireAsync(CancellationToken.None))
        {
            session = query.Session;
            Assert.Equal(8_784_695UL, owner.ReadTable().RegisteredBytes);
            Assert.Equal(8_785_144UL, session.Capacity.ResidentByteCount);
            query.MarkReusable();
        }
        Assert.Equal(NativeFileQueryStatus.Ok, session.Snapshot(out var idle));
        Assert.Equal(456UL, idle.ResidentByteCount);
        // Eight former eager work areas contained 70,277,504 bytes. Only
        // 8 * 456 native metadata bytes are constructed before the first query.
    }

    [Fact]
    public async Task QueryPinsRealScratchThenTickReclaimsAndNextQueryRefillsTheSameIdentity()
    {
        using var owner = new ResourceManagerLedgerOwner();
        ResourceManagerLedgerOwner.ComputeScratch? resource = null;
        using var workspace = NativeFileQueryTestWorkspaceFactory.Create((length, alignment) =>
            FileQueryScratch.Create(bytes => resource = owner.CreateScratch(bytes), length, alignment));
        Assert.NotNull(resource);
        var uid = resource.Uid;
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        owner.SetMode(LedgerTableMode.Optimize);
        ulong allocation;
        NativeFileQuerySession firstSession;
        using (var first = await workspace.AcquireAsync(CancellationToken.None))
        {
            firstSession = first.Session;
            Assert.Equal(resource.Length, owner.ReadTable().RegisteredBytes);
            Assert.Equal(0UL, owner.Tick().Released); // The real native columns are pinned.
            Assert.Equal("ledger", Begin(workspace, first.Session, "ledger"));
            Assert.Equal(NativeFileQueryStatus.Ok, first.Session.Snapshot(out var active));
            Assert.Equal(6U, active.QueryByteCount);
            Reset(workspace, first.Session);
            first.MarkReusable();
        }
        using (var inspection = resource.Acquire()) allocation = inspection.AllocationNumber;
        Assert.Equal(1UL, owner.Tick().Released);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        using (var second = await workspace.AcquireAsync(CancellationToken.None))
        {
            Assert.Same(firstSession, second.Session);
            Assert.Equal(uid, resource.Uid);
            Assert.Equal(NativeFileQueryStatus.Ok, second.Session.Snapshot(out var clean));
            Assert.Equal((uint)NativeFileQueryPhase.Empty, clean.Phase);
            Assert.Equal(0U, clean.QueryByteCount);
            Assert.Equal(0U, clean.UniqueCandidateCount);
            Assert.Equal("refill", Begin(workspace, second.Session, "refill"));
            Reset(workspace, second.Session);
            second.MarkReusable();
        }
        using (var inspection = resource.Acquire()) Assert.NotEqual(allocation, inspection.AllocationNumber);
        workspace.Dispose();
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
    }

    [Fact]
    public async Task CancelledAcquisitionNeverAllocatesAndAbandonedQueryReturnsLoadingStorage()
    {
        using var owner = new ResourceManagerLedgerOwner();
        using var workspace = NativeFileQueryTestWorkspaceFactory.Create((length, alignment) =>
            FileQueryScratch.Create(owner.CreateScratch, length, alignment));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await workspace.AcquireAsync(cancellation.Token));
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        using (var query = await workspace.AcquireAsync(CancellationToken.None))
        {
            Assert.Equal("abandon", Begin(workspace, query.Session, "abandon"));
            Assert.NotEqual(0UL, owner.ReadTable().RegisteredBytes);
        }
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
    }

    [Fact]
    public async Task FailedAcquisitionReturnStaysOwnedAndTheNextBorrowRetriesIt()
    {
        using var owner = new ResourceManagerLedgerOwner();
        FaultingScratch? scratch = null;
        using var workspace = NativeFileQueryTestWorkspaceFactory.Create((length, alignment) =>
            scratch = new FaultingScratch(FileQueryScratch.Create(owner.CreateScratch, length, alignment))
                { FailPin = true, FailReturn = true });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await workspace.AcquireAsync(CancellationToken.None));
        Assert.Equal("injected return failure", error.Message);
        Assert.NotEqual(0UL, owner.ReadTable().RegisteredBytes);
        owner.SetMode(LedgerTableMode.Optimize);
        Assert.Equal(0UL, owner.Tick().Released);
        using (var query = await workspace.AcquireAsync(CancellationToken.None))
        {
            Assert.Equal(1, scratch!.Returns);
            Assert.Equal("retry", Begin(workspace, query.Session, "retry"));
            Reset(workspace, query.Session);
            query.MarkReusable();
        }
        Assert.Equal(2, scratch!.Returns);
        workspace.Dispose();
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
    }

    [Fact]
    public async Task FailedSessionCloseStaysOwnedByThePoolForAnExplicitDisposeRetry()
    {
        using var owner = new ResourceManagerLedgerOwner();
        FaultingScratch? scratch = null;
        using var workspace = NativeFileQueryTestWorkspaceFactory.Create((length, alignment) =>
            scratch = new FaultingScratch(FileQueryScratch.Create(owner.CreateScratch, length, alignment)));
        using var query = await workspace.AcquireAsync(CancellationToken.None);
        scratch!.FailClose = true;
        Assert.Throws<InvalidOperationException>(() => query.Dispose());
        Assert.Equal(1, scratch.Returns); // The use ended; only resource close remains.
        query.Dispose();
        Assert.False(scratch.Closed); // The lease callback transferred ownership once.
        workspace.Dispose();
        Assert.True(scratch.Closed);
        Assert.Equal(1, scratch.Returns);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedQueryReturnRestoresThePermitAndTheNextBorrowRetriesWithoutTheOldLease(bool failComplete)
    {
        using var owner = new ResourceManagerLedgerOwner();
        FaultingScratch? scratch = null;
        using var workspace = NativeFileQueryTestWorkspaceFactory.Create((length, alignment) =>
            scratch = new FaultingScratch(FileQueryScratch.Create(owner.CreateScratch, length, alignment)));
        var first = await workspace.AcquireAsync(CancellationToken.None);
        first.MarkReusable();
        if (failComplete) scratch!.FailComplete = true;
        else scratch!.FailReturn = true;
        Assert.Throws<InvalidOperationException>(() => first.Dispose());
        Assert.Equal(0, scratch.Returns);
        owner.SetMode(LedgerTableMode.Optimize);
        Assert.Equal(0UL, owner.Tick().Released); // The failed use is still counted.

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using (var next = await workspace.AcquireAsync(timeout.Token))
        {
            Assert.Same(first.Session, next.Session);
            Assert.Equal(1, scratch.Returns);
            first.Dispose(); // Must not detach or return the new borrow.
            Assert.Equal(1, scratch.Returns);
            using var cancelledWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                using var unexpected = await workspace.AcquireAsync(cancelledWait.Token);
            });
            Assert.Equal("recovered", Begin(workspace, next.Session, "recovered"));
            Reset(workspace, next.Session);
            next.MarkReusable();
        }
        Assert.Equal(2, scratch.Returns);
        workspace.Dispose();
        Assert.True(scratch.Closed);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedReturnAfterWorkspaceShutdownStaysOwnedForTheNextDispose(bool failComplete)
    {
        using var owner = new ResourceManagerLedgerOwner();
        FaultingScratch? scratch = null;
        using var workspace = NativeFileQueryTestWorkspaceFactory.Create((length, alignment) =>
            scratch = new FaultingScratch(FileQueryScratch.Create(owner.CreateScratch, length, alignment)));
        var query = await workspace.AcquireAsync(CancellationToken.None);
        query.MarkReusable();
        workspace.Dispose();
        if (failComplete) scratch!.FailComplete = true;
        else scratch!.FailReturn = true;
        Assert.Throws<InvalidOperationException>(() => query.Dispose());
        Assert.Equal(0, scratch.Returns);
        Assert.False(scratch.Closed);
        query.Dispose();
        workspace.Dispose();
        Assert.Equal(1, scratch.Returns);
        Assert.True(scratch.Closed);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerReleasesItsHolderEvenWhenTheInnerLeaseReturnFails(bool failComplete)
    {
        using var memoryOwner = new ResourceManagerLedgerOwner();
        var resources = new List<FaultingScratch>();
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        Publish(1);
        using var owner = new HostManagerFileQueryOwner(provider,
            new HostManagerFileQueryRuntime(provider, deployment),
            NullLogger<HostManagerFileQueryOwner>.Instance,
            (length, alignment) =>
            {
                var scratch = new FaultingScratch(FileQueryScratch.Create(memoryOwner.CreateScratch, length, alignment));
                resources.Add(scratch);
                return scratch;
            });
        var query = await owner.AcquireAsync(CancellationToken.None);
        query.MarkReusable();
        if (failComplete) resources[0].FailComplete = true;
        else resources[0].FailReturn = true;
        Assert.Throws<InvalidOperationException>(() => query.Dispose());
        Publish(2); // Retirement retries the session return and closes its scratch.
        Assert.True(resources[0].Closed);
        Assert.Equal(1, resources[0].Returns);
        query.Dispose();
        Assert.Equal(1, resources[0].Returns);
        owner.Dispose();
        Assert.All(resources, scratch => Assert.True(scratch.Closed));
        Assert.Equal(0UL, memoryOwner.ReadTable().RegisteredBytes);

        void Publish(int revision) => provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = revision,
            HostManager = HostManagerTestPlanFactory.CreatePlan(root =>
            {
                root["profile_revision"] = root["profile_revision"]!.GetValue<int>() + revision;
                root["host_recreate"]!["file_query"]!["query_session_count"] = 1;
            })
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetiredPoolCloseFailureFromTheLastLeaseIsRetriedByItsOwner(bool disposeOwnerFirst)
    {
        using var memoryOwner = new ResourceManagerLedgerOwner();
        var resources = new List<FaultingScratch>();
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        Publish(1);
        using var owner = new HostManagerFileQueryOwner(provider,
            new HostManagerFileQueryRuntime(provider, deployment),
            NullLogger<HostManagerFileQueryOwner>.Instance,
            (length, alignment) =>
            {
                var scratch = new FaultingScratch(FileQueryScratch.Create(memoryOwner.CreateScratch, length, alignment));
                resources.Add(scratch);
                return scratch;
            });
        var query = await owner.AcquireAsync(CancellationToken.None);
        query.MarkReusable();
        if (disposeOwnerFirst) owner.Dispose();
        else Publish(2);
        resources[0].FailClose = true;
        Assert.Throws<InvalidOperationException>(() => query.Dispose());
        Assert.Equal(1, resources[0].Returns);
        Assert.False(resources[0].Closed);
        query.Dispose(); // No second holder release or session return.
        Assert.False(resources[0].Closed);
        owner.Dispose();
        Assert.True(resources[0].Closed);
        owner.Dispose();
        Assert.All(resources, scratch => Assert.True(scratch.Closed));
        Assert.Equal(0UL, memoryOwner.ReadTable().RegisteredBytes);

        void Publish(int revision) => provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = revision,
            HostManager = HostManagerTestPlanFactory.CreatePlan(root =>
            {
                root["profile_revision"] = root["profile_revision"]!.GetValue<int>() + revision;
                root["host_recreate"]!["file_query"]!["query_session_count"] = 1;
            })
        });
    }

    [Fact]
    public async Task OutstandingRetiredPoolsAllowPlanReplacementAndKeepEveryBorrowedHolderOwned()
    {
        using var memoryOwner = new ResourceManagerLedgerOwner();
        var resources = new List<FaultingScratch>();
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        Publish(1);
        using var owner = new HostManagerFileQueryOwner(provider,
            new HostManagerFileQueryRuntime(provider, deployment),
            NullLogger<HostManagerFileQueryOwner>.Instance,
            (length, alignment) =>
            {
                var scratch = new FaultingScratch(FileQueryScratch.Create(memoryOwner.CreateScratch, length, alignment));
                resources.Add(scratch);
                return scratch;
            });
        var first = await owner.AcquireAsync(CancellationToken.None);
        first.MarkReusable();
        Publish(2);
        var current = await owner.AcquireAsync(CancellationToken.None);
        current.MarkReusable();
        Publish(3);
        Assert.Equal(3, resources.Count); // Every active borrower retains its own retired pool.
        Assert.False(resources[0].Closed);
        first.Dispose();
        Assert.True(resources[0].Closed);
        Publish(4);
        Assert.Equal(4, resources.Count);
        owner.Dispose(); // The prior current is still leased.
        Assert.False(resources[1].Closed);
        current.Dispose();
        owner.Dispose();
        Assert.All(resources, scratch => Assert.True(scratch.Closed));
        Assert.Equal(0UL, memoryOwner.ReadTable().RegisteredBytes);

        void Publish(int revision) => provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = revision,
            HostManager = HostManagerTestPlanFactory.CreatePlan(root =>
            {
                root["profile_revision"] = root["profile_revision"]!.GetValue<int>() + revision;
                root["host_recreate"]!["file_query"]!["query_session_count"] = 1;
            })
        });
    }

    [Fact]
    public void FailedWorkspaceCloseRetainsTheSessionForAnExplicitDisposeRetry()
    {
        using var owner = new ResourceManagerLedgerOwner();
        FaultingScratch? scratch = null;
        using var workspace = NativeFileQueryTestWorkspaceFactory.Create((length, alignment) =>
            scratch = new FaultingScratch(FileQueryScratch.Create(owner.CreateScratch, length, alignment))
                { FailClose = true });
        Assert.Throws<InvalidOperationException>(() => workspace.Dispose());
        Assert.False(scratch!.Closed);
        workspace.Dispose();
        Assert.True(scratch.Closed);
    }

    private sealed class FaultingScratch(INativeFileQueryScratch inner) : INativeFileQueryScratch
    {
        internal bool FailPin, FailReturn, FailComplete, FailClose, Closed;
        internal int Returns;
        public ulong Length => inner.Length;
        public NativeFileQueryScratchLease Acquire() => new Lease(this, inner.Acquire());
        public void Dispose()
        {
            if (FailClose) { FailClose = false; throw new InvalidOperationException("injected close failure"); }
            inner.Dispose();
            Closed = true;
        }
        private sealed class Lease(FaultingScratch owner, NativeFileQueryScratchLease inner) : NativeFileQueryScratchLease
        {
            private bool disposed;
            internal override ulong Length => inner.Length;
            internal override MemoryHandle Pin()
            {
                if (owner.FailPin) { owner.FailPin = false; throw new InvalidOperationException("injected pin failure"); }
                return inner.Pin();
            }
            internal override void Complete()
            {
                if (owner.FailComplete) { owner.FailComplete = false; throw new InvalidOperationException("injected complete failure"); }
                inner.Complete();
            }
            public override void Dispose()
            {
                if (disposed) return;
                if (owner.FailReturn) { owner.FailReturn = false; throw new InvalidOperationException("injected return failure"); }
                inner.Dispose();
                disposed = true;
                owner.Returns++;
            }
        }
    }

    private static string Begin(NativeFileQueryWorkspace workspace, NativeFileQuerySession session, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var sources = new NativeFileQuerySourcePlan[3];
        var plan = new byte[131072];
        var input = new NativeFileQueryBeginInput
        {
            AbiVersion = NativeFileQueryAbi.Version,
            StructSize = checked((uint)Unsafe.SizeOf<NativeFileQueryBeginInput>()),
            ConfigurationGeneration = session.ConfigurationGeneration,
            OperationEpoch = workspace.NextEpoch(), QueryEpoch = workspace.NextEpoch(),
            QueryByteCount = checked((uint)bytes.Length), RequestedResultCount = 20,
            ValidMask = (ulong)NativeFileQueryBeginValidity.Required
        };
        Assert.Equal(NativeFileQueryStatus.Ok, session.Begin(in input, bytes, sources, plan, out var output));
        return Encoding.UTF8.GetString(plan, checked((int)output.NormalizedQueryOffset), checked((int)output.NormalizedQueryLength));
    }

    private static void Reset(NativeFileQueryWorkspace workspace, NativeFileQuerySession session)
    {
        var input = new NativeFileQueryResetInput
        {
            AbiVersion = NativeFileQueryAbi.Version,
            StructSize = checked((uint)Unsafe.SizeOf<NativeFileQueryResetInput>()),
            ConfigurationGeneration = session.ConfigurationGeneration,
            OperationEpoch = workspace.NextEpoch(), ValidMask = (ulong)NativeFileQueryResetValidity.Required
        };
        Assert.Equal(NativeFileQueryStatus.Ok, session.Reset(in input));
    }
}
