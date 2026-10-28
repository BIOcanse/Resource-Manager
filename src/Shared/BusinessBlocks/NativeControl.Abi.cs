using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ResourceManager.Shared.BusinessBlocks.State;

namespace ResourceManager.Shared.BusinessBlocks;

public sealed partial class NativeControl
{
    private const string Library = "ResourceManager.NativeCore";
    private static ItemKey Key(StateItemKey key) => new() { Item = checked((uint)key.ItemUid), Block = checked((uint)(key.BlockUid ?? 0)) };
    internal static void Check(int result)
    {
        if (result == 0) return;
        throw result switch
        {
            1 => new ArgumentException("Invalid controller argument."),
            2 => new InvalidOperationException("Controller ABI mismatch."),
            3 => new InvalidOperationException("Controller unavailable."),
            4 => new InvalidOperationException("Controller has no data."),
            5 => new ArgumentException("The controller output buffer is too small."),
            6 => new InvalidOperationException("Stale native frame."),
            7 => new OutOfMemoryException("The native controller could not allocate its fixed tables."),
            8 => new InvalidOperationException("Native PDH error."),
            20 => new InvalidOperationException("The item already has an owner."),
            21 => new KeyNotFoundException("Undeclared state item."),
            22 => new InvalidOperationException("State item type mismatch."),
            24 => new ArgumentException("Host runnable UIDs do not exactly match the compiled control tree."),
            25 => new ArgumentOutOfRangeException(nameof(result), "Invalid controller creation limits."),
            26 => new InvalidOperationException("A state subscription already uses this holder."),
            27 => new InvalidOperationException("The bounded reader allocator is full."),
            28 => new InvalidOperationException("The bounded completion table is full."),
            29 => new InvalidOperationException("Invalid state write capability."),
            30 => new InvalidOperationException("State item storage mismatch."),
            31 => new InvalidOperationException("The controller is not active."),
            34 => new ArgumentException("The update reason exceeds its bounded capacity."),
            35 => new InvalidOperationException("The program is not shutting down."),
            36 => new InvalidOperationException("Runs have not all returned."),
            37 => new KeyNotFoundException("Unknown business block UID."),
            38 => new ArgumentException("Invalid reader holder."),
            _ => new InvalidOperationException($"Unknown native controller result code {result}.")
        };
    }
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct Config
    {
        public uint StructSize, Profile, WorkerCount, HistoryCapacity,
            CompletionCapacity, ReaderCapacity, ReaderBytes, WorkerArenaBytes, ReasonTextCapacity, ShutdownItemUid;
        public uint* HostBlocks;
        public uint HostBlockCount;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct ItemKey { public uint Item, Block; }
    [StructLayout(LayoutKind.Sequential)] internal struct FunctionRead { public uint Kind, Value; }
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct ReadFunction
    {
        public nint Context;
        public delegate* unmanaged[Cdecl]<nint, FunctionRead*, void> Read;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct ReasonView { public uint Kind; public byte* Owner; public nuint OwnerLength; public byte* Cause; public nuint CauseLength; public ulong PlanVersion; }
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct HostCallbacks
    {
        public nint Context;
        public delegate* unmanaged[Cdecl]<nint, uint, ulong, void> Start;
        public delegate* unmanaged[Cdecl]<nint, uint, ulong, void> Stop;
        public delegate* unmanaged[Cdecl]<nint, ulong, void> Complete;
        public delegate* unmanaged[Cdecl]<nint, uint, byte*, nuint, void> Log;
        public delegate* unmanaged[Cdecl]<nint, ulong, ItemKey, ReasonView*, void> ItemChanged;
    }
    internal sealed class ControlHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal ControlHandle(nint value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle() => Destroy(handle) == 0;
    }
    [DllImport(Library, EntryPoint = "rm_control_abi_version", CallingConvention = CallingConvention.Cdecl)] internal static extern uint AbiVersion();
    [DllImport(Library, EntryPoint = "rm_control_profile_host_blocks", CallingConvention = CallingConvention.Cdecl)] internal static unsafe extern int ProfileHostBlocksNative(uint profile, uint* buffer, nuint capacity, out nuint needed);
    [DllImport(Library, EntryPoint = "rm_control_create", CallingConvention = CallingConvention.Cdecl)] internal static extern int Create(in Config config, in HostCallbacks callbacks, out nint handle);
    [DllImport(Library, EntryPoint = "rm_control_activate", CallingConvention = CallingConvention.Cdecl)] internal static extern int ActivateNative(ControlHandle handle);
    [DllImport(Library, EntryPoint = "rm_control_destroy", CallingConvention = CallingConvention.Cdecl)] internal static extern int Destroy(nint handle);
    [DllImport(Library, EntryPoint = "rm_control_claim_write", CallingConvention = CallingConvention.Cdecl)] internal static extern int ClaimWrite(ControlHandle handle, in ItemKey key, out nint claim);
    [DllImport(Library, EntryPoint = "rm_control_claim_cell", CallingConvention = CallingConvention.Cdecl)] internal static extern int ClaimCell(ControlHandle handle, in ItemKey key, out nint claim);
    [DllImport(Library, EntryPoint = "rm_control_claim_read_function", CallingConvention = CallingConvention.Cdecl)] internal static extern int ClaimReadFunctionNative(ControlHandle handle, in ItemKey key, in ReadFunction read, out nint claim);
    [DllImport(Library, EntryPoint = "rm_control_notify_changed", CallingConvention = CallingConvention.Cdecl)] internal static unsafe extern int NotifyChangedNative(ControlHandle handle, nint claim, ReasonView* reason, ulong token);
    [DllImport(Library, EntryPoint = "rm_control_read", CallingConvention = CallingConvention.Cdecl)] internal static extern int ReadNative(ControlHandle handle, in ItemKey key, out uint read);
    [DllImport(Library, EntryPoint = "rm_control_read_presence", CallingConvention = CallingConvention.Cdecl)] internal static extern int ReadPresence(ControlHandle handle, in ItemKey key, out uint presence);
    [DllImport(Library, EntryPoint = "rm_control_read_state", CallingConvention = CallingConvention.Cdecl)] internal static extern int ReadStateNative(ControlHandle handle, uint block, out uint state);
    [DllImport(Library, EntryPoint = "rm_control_publish", CallingConvention = CallingConvention.Cdecl)] internal static unsafe extern int Publish(ControlHandle handle, nint claim, uint read, ReasonView* reason, ulong token);
    [DllImport(Library, EntryPoint = "rm_control_publish_presence", CallingConvention = CallingConvention.Cdecl)] internal static unsafe extern int PublishPresenceNative(ControlHandle handle, nint claim, uint read, ReasonView* reason, ulong token);
    [DllImport(Library, EntryPoint = "rm_control_bind", CallingConvention = CallingConvention.Cdecl)] internal static unsafe extern int BindNative(ControlHandle handle, nint claim, ReasonView* reason, ulong token);
    [DllImport(Library, EntryPoint = "rm_control_unbind", CallingConvention = CallingConvention.Cdecl)] internal static unsafe extern int UnbindNative(ControlHandle handle, nint claim, ReasonView* reason, ulong token);
    [DllImport(Library, EntryPoint = "rm_control_register_reader", CallingConvention = CallingConvention.Cdecl)] internal static unsafe extern int RegisterReader(ControlHandle handle, in ItemKey key, byte* text, uint length, out ulong id);
    [DllImport(Library, EntryPoint = "rm_control_unregister_reader", CallingConvention = CallingConvention.Cdecl)] internal static extern int UnregisterReader(ControlHandle handle, ulong id);
    [DllImport(Library, EntryPoint = "rm_control_run_running", CallingConvention = CallingConvention.Cdecl)] internal static extern int RunRunning(ControlHandle handle, uint block, ulong run);
    [DllImport(Library, EntryPoint = "rm_control_run_returned", CallingConvention = CallingConvention.Cdecl)] internal static extern int RunReturned(ControlHandle handle, uint block, ulong run);
    [DllImport(Library, EntryPoint = "rm_control_shutdown", CallingConvention = CallingConvention.Cdecl)] internal static unsafe extern int Shutdown(ControlHandle handle, ReasonView* reason, ulong token);
    [DllImport(Library, EntryPoint = "rm_control_snapshot", CallingConvention = CallingConvention.Cdecl)] internal static unsafe extern int Snapshot(ControlHandle handle, byte* buffer, nuint length, out nuint needed);
}
