using Microsoft.Extensions.Logging;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Adaptation;

internal readonly record struct NativeWorkspaceCapacityDescription(string Rows, ulong Bytes);

internal interface INativeWorkspaceCapacitySource
{
    NativeWorkspaceCapacityDescription ReadWorkspaceCapacity();
    void SetGrowthObserver(Action<NativeWorkspaceGrowthEvent>? observer);
}

// Scratch factories expose this optional diagnostic capability independently of
// allocating or acquiring any scratch. Native authority remains outside Ledger.
internal interface IWorkspaceCapacityRegistry
{
    IDisposable RegisterNativeWorkspace(string workArea, Func<NativeWorkspaceCapacityDescription> read);
    void LogNativeGrowth(NativeWorkspaceGrowthEvent growth);
}

public sealed partial class ResourceManagerLedgerOwner : IWorkspaceCapacityRegistry
{
    private readonly HashSet<NativeWorkspaceRegistration> nativeWorkspaces = [];

    void IWorkspaceCapacityRegistry.LogNativeGrowth(NativeWorkspaceGrowthEvent growth)
        => workspaceLogger?.LogInformation(new EventId(8101, "NativeWorkspaceGrowth"),
            "Native workspace {Owner} grew capacity {OldCapacity} -> {NewCapacity} ({CapacityUnit}): {Reason}.",
            growth.Owner, growth.OldCapacity, growth.NewCapacity, growth.Unit, growth.Reason);

    IDisposable IWorkspaceCapacityRegistry.RegisterNativeWorkspace(
        string workArea, Func<NativeWorkspaceCapacityDescription> read)
    {
        lock (operationGate)
        {
            ThrowIfDisposed();
            if (closing) throw new InvalidOperationException("The product memory owner is closing.");
            var registration = new NativeWorkspaceRegistration(this, workArea, read);
            nativeWorkspaces.Add(registration);
            return registration;
        }
    }

    private sealed class NativeWorkspaceRegistration(ResourceManagerLedgerOwner owner,
        string workArea, Func<NativeWorkspaceCapacityDescription> read) : IDisposable
    {
        private readonly object gate = new();
        private readonly string uid = Guid.NewGuid().ToString("N");
        private bool disposed;

        internal ResourceManagerWorkspaceCapacity? Read()
        {
            lock (gate)
            {
                if (disposed) return null;
                var capacity = read();
                return new(uid, workArea, capacity.Rows, capacity.Bytes, InLedger: false);
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
            }
            lock (owner.operationGate) owner.nativeWorkspaces.Remove(this);
        }
    }
}
