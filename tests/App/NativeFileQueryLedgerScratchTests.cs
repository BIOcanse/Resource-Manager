using System.Buffers;
using ResourceManager.ReadWrite.Native;
using System.Runtime.CompilerServices;
using System.Text;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.Ledger;

namespace Resource_Manager_APP.Tests;

public sealed class NativeFileQueryLedgerScratchTests
{
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
    public async Task FailedSessionCloseKeepsTheQueryLeaseCallbackUntilItsRetrySucceeds()
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
        Assert.True(scratch.Closed);
        Assert.Equal(1, scratch.Returns);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
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
        internal bool FailPin, FailReturn, FailClose, Closed;
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
            internal override void Complete() => inner.Complete();
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
