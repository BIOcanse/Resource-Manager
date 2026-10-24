using System.Reflection;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.Transactions;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.Ledger;

namespace Resource_Manager_APP.Tests;

public sealed partial class NativeTransactionJournalOwnerTests
{
    [Theory]
    [InlineData(16U, 9600UL)]
    [InlineData(8192U, 4849792UL)]
    [InlineData(65536U, 38797440UL)]
    public void ActualNativeCapacityDefinesTheSingleReusableRegion(uint records, ulong expectedBytes)
    {
        var (create, _) = CreateV5Configurations(records, 268435456, 11, 17);
        using var session = new NativeTransactionJournalSession(in create);
        using var ledger = new ResourceManagerLedgerOwner();
        ResourceManagerLedgerOwner.ComputeScratch? resource = null;
        using var workspace = new NativeTransactionJournalWorkspace(session.Capacity,
            bytes => resource = ledger.CreateScratch(bytes));
        Assert.Equal(expectedBytes, workspace.Length);
        Assert.Equal(expectedBytes, resource!.Length);
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
    }

    [Fact]
    public async Task RealSdkRecordsAndEncodeBytesSurviveIdleReclamationWithTheSameUid()
    {
        using var directory = new JournalLedgerDirectory();
        using var ledger = new ResourceManagerLedgerOwner();
        var path = Path.Combine(directory.Path, "journal.bin");
        var (create, open) = CreateV5Configurations(16, ResidentBudget, 11, 17);
        ResourceManagerLedgerOwner.ComputeScratch? resource = null;
        await using var owner = await NativeTransactionJournalOwner.OpenOrCreateAsync(
            new(path), create, open, bytes => resource = ledger.CreateScratch(bytes), CancellationToken.None);
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes); // Startup's single-use image is ordinary.
        var uid = resource!.Uid;
        var empty = await owner.ReadSnapshotAsync(CancellationToken.None);
        Assert.Empty(empty.Records);
        var input = CreatePrepare(CreateIdentity(71), empty.Header.JournalRevision, 1000);
        Assert.Equal(NativeTransactionJournalStatus.Ok, await owner.PrepareAsync(input, CancellationToken.None));
        var saved = await owner.ReadSnapshotAsync(CancellationToken.None);
        Assert.Single(saved.Records);
        Assert.Equal(71UL, saved.Records[0].Identity.ActionId);
        var image = await File.ReadAllBytesAsync(path);
        ulong allocation;
        using (var use = resource.Acquire())
        {
            allocation = use.AllocationNumber;
            var offset = (ulong)owner.Capacity.RecordCapacity * NativeTransactionJournalSession.SizeOf<NativeTransactionJournalRecord>();
            Assert.Equal(image, use.GetMemoryChunk(offset, image.Length).ToArray());
            var independent = new byte[image.Length];
            Assert.Equal(NativeTransactionJournalStatus.Ok, JournalSession(owner).Encode(independent, out var written));
            Assert.Equal((ulong)image.Length, written);
            Assert.Equal(image, independent);
        }
        ledger.SetMode(LedgerTableMode.Optimize);
        Assert.Equal(1UL, ledger.Tick().Released);
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
        var restored = await owner.ReadSnapshotAsync(CancellationToken.None);
        Assert.Equal(uid, resource.Uid);
        Assert.Equal(71UL, Assert.Single(restored.Records).Identity.ActionId);
        using (var next = resource.Acquire()) Assert.NotEqual(allocation, next.AllocationNumber);
        var second = CreatePrepare(CreateIdentity(73), restored.Header.JournalRevision, 1001);
        second.Identity.ProcessId++;
        second.Identity.TargetId++;
        Assert.Equal(NativeTransactionJournalStatus.Ok, await owner.PrepareAsync(second, CancellationToken.None));
        Assert.Equal(71UL, Assert.Single(saved.Records).Identity.ActionId); // The result is an independent copy.
        Assert.Equal(2, (await owner.ReadSnapshotAsync(CancellationToken.None)).Records.Count);
    }

    [Fact]
    public async Task RefusedRegionPrecedesCanonicalMutationAndLeavesTheDurableInputUnchanged()
    {
        using var directory = new JournalLedgerDirectory();
        using var ledger = new ResourceManagerLedgerOwner();
        var path = Path.Combine(directory.Path, "journal.bin");
        var (create, open) = CreateV5Configurations(16, ResidentBudget, 11, 17);
        JournalRefusingAcquire? resource = null;
        await using var owner = await NativeTransactionJournalOwner.OpenOrCreateAsync(new(path), create, open,
            bytes => resource = new(ledger.CreateScratch(bytes)), CancellationToken.None);
        var original = await File.ReadAllBytesAsync(path);
        var input = CreatePrepare(CreateIdentity(71), 1, 1000);
        await Assert.ThrowsAsync<IOException>(() => owner.PrepareAsync(input, CancellationToken.None));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Equal(NativeTransactionJournalOwnerState.Ready, owner.State);
        Assert.Null(owner.PersistenceFailure);
        resource!.Refuse = false;
        Assert.Empty((await owner.ReadSnapshotAsync(CancellationToken.None)).Records);
        Assert.Equal(NativeTransactionJournalStatus.Ok, await owner.PrepareAsync(input, CancellationToken.None));
        Assert.Equal(71UL, Assert.Single((await owner.ReadSnapshotAsync(CancellationToken.None)).Records).Identity.ActionId);
    }

    [Fact]
    public async Task ActualAsyncCommitKeepsTheRegionOccupiedAndBothCloseCallersWait()
    {
        using var directory = new JournalLedgerDirectory();
        using var ledger = new ResourceManagerLedgerOwner();
        var path = Path.Combine(directory.Path, "journal.bin");
        var hook = new ControlledCommitHook();
        var store = new NativeTransactionJournalFileStore(path, hook);
        var (create, open) = CreateV5Configurations(16, ResidentBudget, 11, 17);
        var owner = await NativeTransactionJournalOwner.OpenOrCreateAsync(store, create, open, ledger.CreateScratch, CancellationToken.None);
        var pause = hook.PauseNextCommit();
        var input = CreatePrepare(CreateIdentity(71), 1, 1000);
        var mutation = owner.PrepareAsync(input, CancellationToken.None);
        Task? first = null, second = null;
        try
        {
            await pause.Entered.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.False(mutation.IsCompleted);
            ledger.SetMode(LedgerTableMode.Optimize);
            Assert.Equal(0UL, ledger.Tick().Released);
            first = owner.DisposeAsync().AsTask();
            second = owner.DisposeAsync().AsTask();
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.Throws<NativeTransactionJournalOwnerLeaseException>(() => store.AcquireOwnerLease());
        }
        finally
        {
            pause.Release();
            Assert.Equal(NativeTransactionJournalStatus.Ok, await mutation.WaitAsync(TimeSpan.FromSeconds(20)));
            if (first is not null) await first.WaitAsync(TimeSpan.FromSeconds(20));
            if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(20));
            await owner.DisposeAsync();
        }
        Assert.Equal(NativeTransactionJournalOwnerState.Disposed, owner.State);
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
        var opened = await store.TryOpenAsync(open, CancellationToken.None);
        using var durable = opened.Session;
        Assert.Equal(NativeTransactionJournalStatus.Ok, opened.Status);
        Assert.NotNull(durable);
        Assert.Equal(NativeTransactionJournalStatus.Ok, durable.Get(in input.Identity, out var record));
        Assert.Equal(71UL, record.Identity.ActionId);
    }

    [Fact]
    public async Task FailedEndUseRetainsSessionAndPathUntilExplicitCloseSucceeds()
    {
        using var directory = new JournalLedgerDirectory();
        using var ledger = new ResourceManagerLedgerOwner();
        var store = new NativeTransactionJournalFileStore(Path.Combine(directory.Path, "journal.bin"));
        var (create, open) = CreateV5Configurations(16, ResidentBudget, 11, 17);
        JournalRefusingEnd? resource = null;
        var owner = await NativeTransactionJournalOwner.OpenOrCreateAsync(store, create, open,
            bytes => resource = new(ledger.CreateScratch(bytes)), CancellationToken.None);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => owner.ReadSnapshotAsync(CancellationToken.None));
            Assert.Equal(1, resource!.EndAttempts);
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.ReadSnapshotAsync(CancellationToken.None));
            Assert.Equal(1, resource.EndAttempts);
            ledger.SetMode(LedgerTableMode.Optimize);
            Assert.Equal(0UL, ledger.Tick().Released);
            await Assert.ThrowsAsync<IOException>(() => owner.DisposeAsync().AsTask());
            Assert.Equal(NativeTransactionJournalOwnerState.Disposing, owner.State);
            Assert.Equal(16U, JournalSession(owner).Capacity.RecordCapacity);
            Assert.Throws<NativeTransactionJournalOwnerLeaseException>(() => store.AcquireOwnerLease());
            resource.AllowEnd = true;
            await owner.DisposeAsync();
            Assert.Equal(3, resource.EndAttempts);
            Assert.Equal(NativeTransactionJournalOwnerState.Disposed, owner.State);
            using var nextOwner = store.AcquireOwnerLease();
        }
        finally { resource!.AllowEnd = true; await owner.DisposeAsync(); }
    }

    [Fact]
    public async Task ExternalUseBlocksNormalOwnerCloseWithoutLosingTheNativeSessionOrFileLock()
    {
        using var directory = new JournalLedgerDirectory();
        using var ledger = new ResourceManagerLedgerOwner();
        var store = new NativeTransactionJournalFileStore(Path.Combine(directory.Path, "journal.bin"));
        var (create, open) = CreateV5Configurations(16, ResidentBudget, 11, 17);
        ResourceManagerLedgerOwner.ComputeScratch? resource = null;
        var owner = await NativeTransactionJournalOwner.OpenOrCreateAsync(store, create, open,
            bytes => resource = ledger.CreateScratch(bytes), CancellationToken.None);
        try
        {
            await owner.ReadSnapshotAsync(CancellationToken.None);
            using (var use = resource!.Acquire())
            {
                var failure = await Assert.ThrowsAsync<LedgerException>(() => owner.DisposeAsync().AsTask());
                Assert.Equal(LedgerStatus.InUse, failure.Status);
                Assert.Equal(16U, JournalSession(owner).Capacity.RecordCapacity);
                Assert.Throws<NativeTransactionJournalOwnerLeaseException>(() => store.AcquireOwnerLease());
            }
            await owner.DisposeAsync();
            Assert.Equal(NativeTransactionJournalOwnerState.Disposed, owner.State);
        }
        finally { await owner.DisposeAsync(); }
    }

    [Fact]
    public async Task FailedHostInitializationKeepsOneUnpublishedRuntimeAndPreservesRecoveryFiles()
    {
        using var directory = new JournalLedgerDirectory();
        using var ledger = new ResourceManagerLedgerOwner();
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(provider.Current with { Version = 1, HostManager = HostManagerTestPlanFactory.CreatePlan() });
        var retirements = new HostManagerAuthorityRetirementManager(directory.Path,
            WindowsHostManagerAuthorityRetirementStorage.Instance);
        var applied = new HostManagerAppliedOwnershipRuntime(
            new HostManagerAppliedOwnershipDeploymentRuntime(provider, deployment), retirements, ledger.CreateScratch);
        JournalRefusingEnd? refusing = null;
        var creates = 0;
        var host = new HostManagerNativeActionTransactionRuntime(
            new HostManagerTransactionJournalDeploymentRuntime(provider, deployment), applied, retirements,
            bytes =>
            {
                creates++;
                return creates == 1 ? refusing = new(ledger.CreateScratch(bytes)) : ledger.CreateScratch(bytes);
            });
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => host.EnsureReadyAsync(CancellationToken.None));
            var pending = Assert.IsType<HostManagerTransactionJournalRuntime>(JournalField(host, "unpublishedJournal"));
            Assert.Null(JournalField(host, "journal"));
            Assert.Equal(HostManagerTransactionJournalRuntimeState.Faulted, pending.State);
            Assert.Null(pending.StartupSnapshot);
            Assert.Throws<InvalidOperationException>(() => host.CurrentJournalPlan);
            var image = await File.ReadAllBytesAsync(pending.JournalPath);
            var store = new NativeTransactionJournalFileStore(pending.JournalPath);
            Assert.Throws<NativeTransactionJournalOwnerLeaseException>(() => store.AcquireOwnerLease());
            await Assert.ThrowsAnyAsync<Exception>(() => host.EnsureReadyAsync(CancellationToken.None));
            Assert.Same(pending, JournalField(host, "unpublishedJournal"));
            Assert.Equal(1, creates);
            Assert.Equal(0, host.PendingRetiredJournalCount);
            Assert.Empty(Directory.EnumerateFiles(retirements.RetirementDirectory, "retirement-*.bin"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => pending.DisposeAsync().AsTask());
            Assert.Equal(HostManagerTransactionJournalRuntimeState.Faulted, pending.State);
            Assert.Throws<NativeTransactionJournalOwnerLeaseException>(() => store.AcquireOwnerLease());
            refusing!.AllowEnd = true;
            await host.EnsureReadyAsync(CancellationToken.None);
            Assert.Equal(2, creates);
            Assert.Null(JournalField(host, "unpublishedJournal"));
            Assert.Equal(HostManagerTransactionJournalRuntimeState.ReadyForExecution, host.JournalState);
            Assert.Equal(image, await File.ReadAllBytesAsync(pending.JournalPath));
            Assert.Empty(Directory.EnumerateFiles(retirements.RetirementDirectory, "retirement-*.bin"));
        }
        finally { if (refusing is not null) refusing.AllowEnd = true; await host.DisposeAsync(); }
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
    }

    private static object? JournalField(object owner, string name)
        => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);

    private static NativeTransactionJournalSession JournalSession(NativeTransactionJournalOwner owner)
        => Assert.IsType<NativeTransactionJournalSession>(JournalField(owner, "session"));

    private sealed class JournalRefusingAcquire(IComputeScratch inner) : IComputeScratch
    {
        public bool Refuse { get; set; } = true;
        public ulong Length => inner.Length;
        public ComputeScratchLease Acquire() => Refuse ? throw new IOException("Injected acquire refusal.") : inner.Acquire();
        public void Dispose() => inner.Dispose();
    }

    private sealed class JournalRefusingEnd(IComputeScratch inner) : IComputeScratch
    {
        public bool AllowEnd { get; set; }
        public int EndAttempts { get; private set; }
        public ulong Length => inner.Length;
        public ComputeScratchLease Acquire() => new Lease(this, inner.Acquire());
        public void Dispose() => inner.Dispose();
        private sealed class Lease(JournalRefusingEnd owner, ComputeScratchLease inner) : ComputeScratchLease
        {
            internal override ulong Length => inner.Length;
            internal override ulong AllocationNumber => inner.AllocationNumber;
            internal override bool IsLoading => inner.IsLoading;
            internal override Memory<byte> GetMemoryChunk(ulong offset, int count) => inner.GetMemoryChunk(offset, count);
            internal override void Complete() => inner.Complete();
            public override void Dispose()
            {
                owner.EndAttempts++;
                if (!owner.AllowEnd) throw new IOException("Injected endUse refusal.");
                inner.Dispose();
            }
        }
    }

    private sealed class JournalLedgerDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rm-journal-ledger-" + Guid.NewGuid().ToString("N"));
        public JournalLedgerDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            var pending = new Stack<(string Path, bool Exit)>();
            pending.Push((Path, false));
            var nodes = 0;
            while (pending.TryPop(out var item))
            {
                if (++nodes > 1024) throw new IOException("Journal fixture cleanup exceeds its bound.");
                if (item.Exit) { Directory.Delete(item.Path); continue; }
                pending.Push((item.Path, true));
                foreach (var child in Directory.EnumerateFileSystemEntries(item.Path))
                {
                    if (++nodes > 1024) throw new IOException("Journal fixture cleanup exceeds its bound.");
                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Unexpected fixture reparse point.");
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push((child, false));
                    else File.Delete(child);
                }
            }
        }
    }
}
