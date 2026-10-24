using ResourceManager.App.Application.DiskUsage;
using ResourceManager.App.Domain.DiskUsage;
using ResourceManager.App.Infrastructure.DiskUsage;
using ResourceManager.Ledger;
using ResourceManager.Ledger.Generated;
using ResourceManager.Ledger.Identity;
using Microsoft.Extensions.Logging;

namespace ResourceManager.App.Infrastructure.Adaptation;

/// <summary>The product's single owned-region instance. OS working sets and
/// allocator bootstrap metadata never enter its payload table.</summary>
public sealed partial class ResourceManagerLedgerOwner : IDiskUsageTreePayloadAllocator, IDisposable
{
    private const uint TreeTable = 0;
    // Structural operations share explicit scratch. Callbacks use only noticeGate.
    private readonly object operationGate = new();
    private readonly object noticeGate = new();
    private readonly Dictionary<LedgerUid128, Payload> payloads = [];
    private readonly LedgerOwner<LedgerUid128> owner;
    private readonly LedgerWorkspace operationWorkspace;
    private readonly LedgerWorkspace tickWorkspace;
    private readonly LedgerWorkspace destroyWorkspace;
    private LedgerSubscription<LedgerUid128>? subscription;
    private bool disposed;
    private readonly ILogger<ResourceManagerLedgerOwner>? workspaceLogger;

    public ResourceManagerLedgerOwner()
        : this(Path.Combine(AppContext.BaseDirectory, "ResourceManager.Ledger.Native.dll")) { }

    public ResourceManagerLedgerOwner(ILogger<ResourceManagerLedgerOwner> logger) : this()
        => workspaceLogger = logger;

    public IReadOnlyList<ResourceManagerWorkspaceCapacity> ReadWorkspaceCapacities()
    {
        lock (operationGate)
        {
            ThrowIfDisposed();
            return computeScratch.Select(static scratch => new ResourceManagerWorkspaceCapacity(
                scratch.Uid.ToString()!, scratch.WorkArea, scratch.Rows, scratch.Length, scratch.InLedger)).ToArray();
        }
    }

    public ResourceManagerLedgerOwner(string nativeLibraryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeLibraryPath);
        var opened = LedgerOwner<LedgerUid128>.Open(nativeLibraryPath, LedgerOwnerBuild.Identity);
        LedgerWorkspace? operation = null, tick = null, destroy = null;
        LedgerSubscription<LedgerUid128>? notices = null;
        try
        {
            // Allocate shutdown scratch first so a later construction failure
            // can close the partially constructed instance without another allocation.
            destroy = opened.CreateDestroyWorkspace();
            operation = opened.CreateOperationWorkspace();
            tick = opened.CreateTickWorkspace();
            notices = opened.Subscribe(TreeTable, OnReleased);
            owner = opened;
            operationWorkspace = operation; tickWorkspace = tick; destroyWorkspace = destroy;
            subscription = notices;
        }
        catch
        {
            notices?.Dispose();
            // No product payload or reader can have escaped this constructor.
            var status = opened.Destroy(destroy);
            if (status != LedgerStatus.Ok) throw new LedgerException(status, "construct product owner cleanup");
            destroy?.Dispose(); tick?.Dispose(); operation?.Dispose();
            throw;
        }
    }

    public IDiskUsageTreePayload Import(DiskUsageTree source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var layout = DiskUsageTreeRegionLayout.Create(source, cancellationToken);
        lock (operationGate)
        {
            ThrowIfDisposed();
            if (closing) throw new InvalidOperationException("The product memory owner is closing.");
            cancellationToken.ThrowIfCancellationRequested();
            // A tiny published cache does not justify a page, slot, index and
            // SDK leases. Copy only live columns, never the builder's capacity.
            if (layout.Length < owner.NativePageSize)
                return DiskUsageTreeStore.ImportManagedPayload(source, layout, cancellationToken);
            var uid = LedgerUid128.FromBytes(Guid.NewGuid().ToByteArray());
            var pushed = owner.Push(LedgerTarget<LedgerUid128>.ForTable(TreeTable), uid,
                layout.Length, operationWorkspace);
            if (pushed.Status != LedgerStatus.NewlyAllocated || pushed.Lease is not { IsLoading: true } loading)
            {
                pushed.Lease?.Dispose();
                throw new LedgerException(pushed.Status, "allocate disk usage tree");
            }
            var allocation = loading.AllocationNumber;
            try
            {
                layout.Write(source, new Region(loading), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                loading.PublishOnDispose();
                var ended = loading.EndUseAsync().GetAwaiter().GetResult();
                if (ended.Status != LedgerStatus.Completed)
                    throw new LedgerException(ended.Status, "publish disk usage tree");
                cancellationToken.ThrowIfCancellationRequested();
                // Verify the published allocation through a fresh counted use.
                var used = owner.Use(uid);
                using var publication = used.Lease;
                if (used.Status != LedgerStatus.InMemory || publication is null ||
                    publication.AllocationNumber != allocation || publication.Length != layout.Length)
                    throw new LedgerException(used.Status, "verify disk usage tree publication");
                var payload = new Payload(this, uid, allocation, layout);
                lock (noticeGate) payloads.Add(uid, payload);
                return payload;
            }
            catch
            {
                // Loading is discarded unless it was completely published.
                loading.Dispose();
                var closed = owner.Close(uid, operationWorkspace);
                if (closed is not (LedgerStatus.Ok or LedgerStatus.Nonexistent or LedgerStatus.NotInMemory))
                    throw new LedgerException(closed, "discard failed tree import");
                throw;
            }
        }
    }

    public void SetMode(LedgerTableMode mode)
    {
        lock (operationGate)
        {
            ThrowIfDisposed();
            for (var table = TreeTable; table <= ComputeScratchTable; table++)
            {
                var status = owner.SetMode(table, mode);
                if (status != LedgerStatus.Ok) throw new LedgerException(status, "set product memory mode");
            }
        }
    }

    public LedgerTickResult Tick()
    {
        lock (operationGate)
        {
            ThrowIfDisposed();
            CloseRetired();
            return owner.Tick(tickWorkspace);
        }
    }

    public LedgerTableView ReadTable()
    {
        lock (operationGate)
        {
            ThrowIfDisposed();
            var tree = owner.GetTableView(TreeTable);
            var scratch = owner.GetTableView(ComputeScratchTable);
            return new(checked(tree.RegisteredBytes + scratch.RegisteredBytes),
                checked(tree.SystemPageBytes + scratch.SystemPageBytes));
        }
    }

    private IDiskUsageTreePayloadReadLease? TryAcquire(Payload payload)
    {
        lock (operationGate)
        {
            if (disposed || closing || payload.IsRetiring || payload.IsReleased) return null;
            var used = owner.Use(payload.Uid);
            if (used.Status != LedgerStatus.InMemory || used.Lease is not { } lease) return null;
            if (lease.AllocationNumber != payload.AllocationNumber || lease.Length != payload.Layout.Length)
            { lease.Dispose(); return null; }
            try { return new Reader(payload, lease); }
            catch { lease.Dispose(); throw; }
        }
    }

    private void Retire(Payload payload)
    {
        lock (operationGate)
        {
            if (disposed || payload.IsReleased) return;
            payload.IsRetiring = true;
            ClosePayload(payload);
        }
    }

    private void ClosePayload(Payload payload)
    {
        var status = owner.Close(payload.Uid, operationWorkspace);
        if (status is LedgerStatus.InUse or LedgerStatus.Loading) return;
        if (status is not (LedgerStatus.Ok or LedgerStatus.Nonexistent or LedgerStatus.NotInMemory))
            throw new LedgerException(status, "close retired disk usage tree");
        // Nonexistent can occur after an earlier Tick notice. The native callback
        // performs only the same identity-matched metadata retirement.
        MarkReleased(payload.Uid, payload.AllocationNumber);
    }

    private void ReaderEnded(Payload payload)
    {
        lock (operationGate)
            if (!disposed && payload.IsRetiring && !payload.IsReleased) ClosePayload(payload);
    }

    private void CloseRetired()
    {
        Payload[] retired;
        lock (noticeGate) retired = payloads.Values.Where(static p => p.IsRetiring).ToArray();
        foreach (var payload in retired) ClosePayload(payload);
        foreach (var scratch in computeScratch.Where(static item => item.Retiring).ToArray()) CloseScratch(scratch);
    }

    private void OnReleased(LedgerNotice<LedgerUid128> notice)
        => MarkReleased(notice.Uid, notice.AllocationNumber);

    private void MarkReleased(LedgerUid128 uid, ulong allocationNumber)
    {
        Payload? payload;
        lock (noticeGate)
        {
            if (!payloads.TryGetValue(uid, out payload) || payload.AllocationNumber != allocationNumber) return;
            payloads.Remove(uid);
        }
        // No ledger entry, Close, lease return or unsubscription in a notice.
        payload.MarkReleased();
    }

    public LedgerStatus TryClose()
    {
        lock (operationGate)
        {
            if (disposed) return LedgerStatus.Ok;
            closing = true;
            foreach (var scratch in computeScratch.ToArray())
            {
                scratch.Retiring = true;
                CloseScratch(scratch);
            }
            Payload[] remaining;
            lock (noticeGate) remaining = payloads.Values.ToArray();
            foreach (var payload in remaining)
            {
                payload.IsRetiring = true;
                ClosePayload(payload);
            }
            lock (noticeGate)
                if (payloads.Count != 0) return LedgerStatus.InUse;
            if (computeScratch.Count != 0) return LedgerStatus.InUse;
            // Every region has safely closed. No payload access or pin remains.
            // Native destruction may still refuse; retain the owner and scratch.
            subscription?.Dispose();
            subscription = null;
            LedgerStatus status;
            try
            {
                status = owner.Destroy(destroyWorkspace);
            }
            finally
            {
                // Destroy also restores admission when it throws. Such an owner
                // remains live and must continue receiving allocation notices.
                if (!owner.IsDestroyed) subscription = owner.Subscribe(TreeTable, OnReleased);
            }
            if (status != LedgerStatus.Ok) return status;
            disposed = true;
            destroyWorkspace.Dispose(); tickWorkspace.Dispose(); operationWorkspace.Dispose();
            return LedgerStatus.Ok;
        }
    }

    public void Dispose()
    {
        var status = TryClose();
        if (status != LedgerStatus.Ok) throw new LedgerException(status, "dispose product owner");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    private sealed class Payload(ResourceManagerLedgerOwner owner, LedgerUid128 uid,
        ulong allocationNumber, DiskUsageTreeRegionLayout layout) : IDiskUsageTreePayload
    {
        private int released;
        public LedgerUid128 Uid { get; } = uid;
        public ulong AllocationNumber { get; } = allocationNumber;
        public DiskUsageTreeRegionLayout Layout { get; } = layout;
        public bool IsRetiring { get; set; }
        public bool IsReleased => Volatile.Read(ref released) != 0;
        public event Action? Released;
        public IDiskUsageTreePayloadReadLease? TryAcquire() => owner.TryAcquire(this);
        public void Retire() => owner.Retire(this);
        public void MarkReleased() { if (Interlocked.Exchange(ref released, 1) == 0) Released?.Invoke(); }
        public void Ended() => owner.ReaderEnded(this);
    }

    private sealed class Region(LedgerLease<LedgerUid128> lease) : IDiskUsageTreeRegion
    {
        public ulong Length => lease.Length;
        public Span<byte> GetSpanChunk(ulong offset, int length) => lease.GetSpanChunk(offset, length);
    }

    private sealed class Reader : IDiskUsageTreePayloadReadLease
    {
        private LedgerLease<LedgerUid128>? lease;
        private readonly Payload payload;
        private readonly DiskUsageTree tree;
        public Reader(Payload payload, LedgerLease<LedgerUid128> lease)
        {
            this.payload = payload; this.lease = lease;
            tree = payload.Layout.Open(new Region(lease));
        }
        public DiskUsageTree Tree => lease is not null ? tree : throw new ObjectDisposedException(nameof(Reader));
        public void Dispose()
        {
            var ended = Interlocked.Exchange(ref lease, null);
            if (ended is null) return;
            ended.Dispose();
            payload.Ended();
        }
    }
}
