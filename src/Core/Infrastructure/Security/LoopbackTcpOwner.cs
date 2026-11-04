using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.Adaptation;

namespace ResourceManager.App.Infrastructure.Security;

// Platform/memory adapter. The native wrapper orders the entire query through
// the injected execution slot. The only long-lived payload is a scratch identity;
// each synchronous WithRegion owns and returns its own local lease and pin.
internal sealed unsafe partial class LoopbackTcpOwner : IDisposable
{
    private readonly SemaphoreSlim execution = new(1, 1);
    private readonly IComputeScratch scratch;
    private readonly uint maximumBytes;
    private int disposed;

    public LoopbackTcpOwner(ResourceManagerLedgerOwner ledger)
        : this(ledger.CreateScratch(ledger.NativePageSize), LoopbackTcpSettings.MaximumTableBytes) { }

    internal LoopbackTcpOwner(IComputeScratch scratch, uint maximumBytes)
    {
        this.scratch = scratch;
        this.maximumBytes = maximumBytes;
        ComputeScratchNames.Name(scratch, "loopback-tcp", "TCP table bytes (query high water)");
    }

    internal ulong WorkspaceBytes => scratch.Length;

    internal int Find(IPAddress client, int clientPort, IPAddress server, int serverPort)
        => Find(client, clientPort, server, serverPort, out _);

    internal int Find(IPAddress client, int clientPort, IPAddress server, int serverPort, out LookupResult result)
    {
        result = default;
        if (client.AddressFamily != server.AddressFamily || clientPort is <= 0 or > ushort.MaxValue
            || serverPort is <= 0 or > ushort.MaxValue || !OperatingSystem.IsWindows()) return 0;
        var tuple = new Connection { Client = Endpoint.Create(client, clientPort), Server = Endpoint.Create(server, serverPort) };
        var root = GCHandle.Alloc(this);
        try
        {
            var callbacks = new Host { Size = (uint)sizeof(Host), Version = 1, Context = GCHandle.ToIntPtr(root),
                Enter = &Enter, Leave = &Leave, WithRegion = &WithRegion };
            FindNative(in callbacks, in tuple, maximumBytes, out result);
            return result.Status == 0 ? checked((int)result.ProcessId) : 0;
        }
        finally { root.Free(); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint Enter(nint context)
    {
        var self = (LoopbackTcpOwner)GCHandle.FromIntPtr(context).Target!;
        try
        {
            self.execution.Wait();
            if (Volatile.Read(ref self.disposed) == 0) return 1;
            self.execution.Release();
            return 0;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Leave(nint context)
    {
        try { ((LoopbackTcpOwner)GCHandle.FromIntPtr(context).Target!).execution.Release(); }
        catch { /* No managed exception may cross the ABI. */ }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Sampled)]
    private static uint WithRegion(nint context, uint requested,
        delegate* unmanaged[Cdecl]<nint, byte*, nuint, uint> work, nint operation)
    {
        try
        {
            var self = (LoopbackTcpOwner)GCHandle.FromIntPtr(context).Target!;
            using var lease = self.scratch.Acquire();
            if (lease.Length < requested)
            {
                // Growth is done before pinning. The same UID is kept; its region
                // may have been reclaimed while idle and re-pushed by Acquire.
                if (lease.IsLoading) lease.PublishAndRetain();
                lease.Grow(requested);
            }
            uint accepted;
            using (var view = new ComputeScratchView(lease))
                accepted = work(operation, (byte*)view.AlignedRegion(0, requested, 1), checked((nuint)lease.Length));
            lease.Complete();
            return accepted;
        }
        catch { return 0; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        // Retirement prevents new admission; drain the admitted operation outside
        // any business-state lock before closing the sole workspace identity.
        execution.Wait();
        try { scratch.Dispose(); }
        finally { execution.Release(); }
        // Queued late callers still use this primitive to observe retirement.
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Endpoint
    {
        internal fixed byte Address[16];
        internal uint Scope;
        internal ushort Port, Family;
        internal static Endpoint Create(IPAddress address, int port)
        {
            var result = new Endpoint { Port = checked((ushort)port),
                Family = address.AddressFamily == AddressFamily.InterNetworkV6 ? (ushort)23 : (ushort)2,
                Scope = address.AddressFamily == AddressFamily.InterNetworkV6 ? checked((uint)address.ScopeId) : 0 };
            if (!address.TryWriteBytes(new Span<byte>(result.Address, 16), out _))
                throw new ArgumentException("Invalid TCP address.", nameof(address));
            return result;
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Connection { internal Endpoint Client, Server; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Host
    {
        internal uint Size, Version;
        internal nint Context;
        internal delegate* unmanaged[Cdecl]<nint, uint> Enter;
        internal delegate* unmanaged[Cdecl]<nint, void> Leave;
        internal delegate* unmanaged[Cdecl]<nint, uint, delegate* unmanaged[Cdecl]<nint, byte*, nuint, uint>, nint, uint> WithRegion;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct LookupResult
    {
        internal uint ProcessId, Status;
        internal ulong WorkspaceBytes;
        internal uint TableBytes, Reserved;
    }
    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_loopback_tcp_find")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void FindNative(in Host host, in Connection connection, uint maximumBytes, out LookupResult result);
}
