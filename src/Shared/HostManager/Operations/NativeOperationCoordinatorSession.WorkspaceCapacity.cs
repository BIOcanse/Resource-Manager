using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal static partial class NativeWorkspaceCapacityAbi
{
    internal const uint Version = 0x0002_0000;

    internal static void EnsureVersion()
    {
        var actual = GetAbiVersion();
        if (actual != Version)
            throw new InvalidOperationException($"Native workspace capacity ABI mismatch: expected 0x{Version:X8}, actual 0x{actual:X8}.");
    }

    internal static void Validate(int status, uint structSize, uint expectedSize, ulong bytes)
    {
        if (status != 0 || structSize != expectedSize || bytes == 0)
            throw new InvalidOperationException($"Native workspace capacity query failed with status {status}, size {structSize}, bytes {bytes}.");
    }

    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_workspace_capacity_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetAbiVersion();
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeOperationCoordinatorWorkspaceCapacity
{
    public uint StructSize;
    public uint OperationCapacity;
    public uint StagingCapacity;
    public uint ActionCapacity;
    public uint OperationIndexCapacity;
    public uint DomainIndexCapacity;
    public ulong ResidentByteCount;
    public NativeWorkspaceGrowthLog Growth;
}

internal sealed partial class NativeOperationCoordinatorSession
{
    private readonly NativeWorkspaceGrowthObserver growthObserver = new();

    internal void SetGrowthObserver(Action<NativeWorkspaceGrowthEvent>? observer)
    {
        lock (sync)
        {
            growthObserver.Observer = observer;
            ObserveGrowth();
        }
    }

    private void ObserveGrowth()
    {
        if (growthObserver.Observer is not null) growthObserver.Read(WorkspaceCapacity.Growth);
    }
    internal unsafe NativeOperationCoordinatorWorkspaceCapacity WorkspaceCapacity
    {
        get
        {
            lock (sync)
            {
                NativeWorkspaceCapacityAbi.EnsureVersion();
                NativeOperationCoordinatorWorkspaceCapacity result = default;
                var status = NativeMethods.QueryWorkspaceCapacity(RequireHandle(), &result,
                    checked((uint)Unsafe.SizeOf<NativeOperationCoordinatorWorkspaceCapacity>()));
                NativeWorkspaceCapacityAbi.Validate(status, result.StructSize, 296, result.ResidentByteCount);
                return result;
            }
        }
    }

    private static partial class NativeMethods
    {
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_operation_coordinator_query_workspace_capacity")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int QueryWorkspaceCapacity(IntPtr handle,
            NativeOperationCoordinatorWorkspaceCapacity* capacity, uint outputSize);
    }
}

