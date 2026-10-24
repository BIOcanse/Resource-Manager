using ResourceManager.Ledger;
using ResourceManager.Ledger.Identity;
using Microsoft.Extensions.Logging;

namespace ResourceManager.App.Infrastructure.Adaptation;

public sealed partial class ResourceManagerLedgerOwner
{
    private const uint ComputeScratchTable = 1;
    private readonly HashSet<ComputeScratch> computeScratch = [];
    private bool closing;

    internal ulong NativePageSize => owner.NativePageSize;

    internal ComputeScratch CreateScratch(ulong length)
    {
        lock (operationGate)
        {
            ThrowIfDisposed();
            if (closing) throw new InvalidOperationException("The product memory owner is closing.");
            if (length == 0) throw new ArgumentOutOfRangeException(nameof(length));
            var scratch = new ComputeScratch(this, length, length >= owner.NativePageSize);
            computeScratch.Add(scratch);
            return scratch; // Identity only: replacement construction must not consume a slot.
        }
    }

    private ComputeScratchLease AcquireScratch(ComputeScratch scratch)
    {
        lock (operationGate)
        {
            ThrowIfDisposed();
            if (closing || scratch.Retiring || scratch.Closed)
                throw new ObjectDisposedException(nameof(ComputeScratch));
            if (!scratch.InLedger)
            {
                scratch.Managed ??= new byte[checked((int)scratch.Length)];
                var managed = new ScratchLease(this, scratch, null, scratch.Managed);
                scratch.Active++;
                return managed;
            }
            var used = owner.Use(scratch.Uid);
            LedgerLease<LedgerUid128>? lease = used.Lease;
            if (used.Status is LedgerStatus.NotInMemory or LedgerStatus.Nonexistent)
            {
                var pushed = owner.Push(LedgerTarget<LedgerUid128>.ForTable(ComputeScratchTable),
                    scratch.Uid, scratch.Length, operationWorkspace);
                lease = pushed.Lease;
                if (pushed.Status is not (LedgerStatus.NewlyAllocated or LedgerStatus.InMemory))
                {
                    lease?.Dispose();
                    throw new LedgerException(pushed.Status, "allocate compute scratch");
                }
            }
            else if (used.Status != LedgerStatus.InMemory)
            {
                lease?.Dispose();
                throw new LedgerException(used.Status, "use compute scratch");
            }
            if (lease is null) throw new InvalidOperationException("The Ledger returned no compute scratch lease.");
            try
            {
                if (lease.Length != scratch.Length)
                    throw new InvalidOperationException("The compute scratch layout does not match its backing allocation.");
                var reading = new ScratchLease(this, scratch, lease, null);
                scratch.Active++;
                return reading;
            }
            catch { lease.Dispose(); throw; }
        }
    }

    private bool RetireScratch(ComputeScratch scratch)
    {
        lock (operationGate)
        {
            if (scratch.Closed) return true;
            scratch.Retiring = true;
            CloseScratch(scratch);
            return scratch.Closed;
        }
    }

    private void CloseScratch(ComputeScratch scratch)
    {
        if (scratch.Active != 0) return;
        if (scratch.InLedger)
        {
            var status = owner.Close(scratch.Uid, operationWorkspace);
            if (status is LedgerStatus.InUse or LedgerStatus.Loading) return;
            if (status is not (LedgerStatus.Ok or LedgerStatus.Nonexistent or LedgerStatus.NotInMemory))
                throw new LedgerException(status, "close compute scratch");
        }
        scratch.Managed = null;
        scratch.Closed = true;
        computeScratch.Remove(scratch);
    }

    private void ScratchEnded(ComputeScratch scratch)
    {
        lock (operationGate)
        {
            scratch.Active--;
            if (scratch.Retiring) CloseScratch(scratch);
        }
    }

    internal sealed class ComputeScratch(ResourceManagerLedgerOwner owner, ulong length, bool inLedger)
        : IComputeScratch
    {
        internal LedgerUid128 Uid { get; } = LedgerUid128.FromBytes(Guid.NewGuid().ToByteArray());
        public ulong Length { get; } = length;
        internal bool InLedger { get; } = inLedger;
        internal byte[]? Managed;
        internal int Active;
        internal bool Retiring;
        internal bool Closed;
        internal string WorkArea = "compute-scratch";
        internal string Rows = "unspecified";
        internal void DescribeGrowth(string area, string oldRows, string rows, ulong oldBytes)
        {
            lock (owner.operationGate)
            {
                WorkArea = area;
                Rows = rows;
                if (owner.workspaceLogger is { } logger)
                    logger.LogInformation("Workspace {WorkArea} grew rows {OldRows} -> {NewRows}, bytes {OldBytes} -> {NewBytes}: actual row demand exceeded high water.",
                        area, oldRows, rows, oldBytes, Length);
                else
                    System.Diagnostics.Trace.TraceInformation("Workspace {0} grew rows {1} -> {2}, bytes {3} -> {4}: actual row demand exceeded high water.",
                        area, oldRows, rows, oldBytes, Length);
            }
        }
        public ComputeScratchLease Acquire() => owner.AcquireScratch(this);
        public void Dispose()
        {
            if (!owner.RetireScratch(this))
                throw new LedgerException(LedgerStatus.InUse, "retire compute scratch");
        }
    }

    private sealed class ScratchLease(ResourceManagerLedgerOwner owner, ComputeScratch scratch,
        LedgerLease<LedgerUid128>? native, byte[]? managed) : ComputeScratchLease
    {
        private bool disposed;
        internal override ulong Length => scratch.Length;
        internal override ulong AllocationNumber => native?.AllocationNumber ?? 0;
        internal override bool IsLoading => native?.IsLoading ?? false;
        internal override Memory<byte> GetMemoryChunk(ulong offset, int count)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return native is not null ? native.GetMemoryChunk(offset, count)
                : managed!.AsMemory(checked((int)offset), count);
        }
        internal override void Complete()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (native is { IsLoading: true }) native.PublishOnDispose();
        }
        public override void Dispose()
        {
            if (disposed) return;
            // Do not forget ownership if ending the native use throws.
            native?.Dispose();
            disposed = true;
            owner.ScratchEnded(scratch);
        }
    }
}
