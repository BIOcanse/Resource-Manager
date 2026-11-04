using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

/// <summary>Rows the native registry holds right now: registrations and paths a snapshot emits, and dirty paths a persistence plan emits.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativePortableSoftwareRegistryWorkspaceRows
{
    public uint StructSize;
    public uint RegistrationCount;
    public uint PathCount;
    public uint DirtyPathCount;
    public ulong ResidentByteCount;
}

internal sealed partial class NativePortableSoftwareRegistrySession
{
    /// <summary>Changes only when an import, observation, root confirmation, missing mark or feedback mutates the registry.</summary>
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven,
        "portable registry mutation")]
    internal unsafe NativePortableSoftwareRegistryWorkspaceRows WorkspaceRows
    {
        get
        {
            lock (sync)
            {
                NativePortableSoftwareRegistryWorkspaceRows result = default;
                var size = checked((uint)Unsafe.SizeOf<NativePortableSoftwareRegistryWorkspaceRows>());
                var status = (NativePortableSoftwareRegistryStatus)NativeMethods.QueryWorkspaceRows(RequireHandle(), &result, size);
                if (status != NativePortableSoftwareRegistryStatus.Ok || result.StructSize != size)
                    throw new InvalidOperationException(
                        $"Native portable software registry row query failed with {status}, size {result.StructSize}.");
                return result;
            }
        }
    }

    private static partial class NativeMethods
    {
        [LibraryImport(LibraryName, EntryPoint = "rm_software_identity_portable_registry_query_workspace_rows")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int QueryWorkspaceRows(IntPtr handle,
            NativePortableSoftwareRegistryWorkspaceRows* rows, uint outputSize);
    }
}
