using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

/// <summary>Publication-scoped references; records and paths never outlive the synchronous consumer.</summary>
internal static unsafe class KernelEtwNativeNotifications
{
    private const string Library = "ResourceManager.NativeCore";

    /// <summary>One reader's place in the native notification stream.</summary>
    internal struct Cursor
    {
        // Version of the bank last read; every output's publication advances it.
        public ulong Publication;
        // Stream position after the last notification read.
        public ulong Next;
    }

    // `missed` is true when notifications were published in banks superseded before this
    // reader woke, or the stream restarted. Superseded banks without notifications miss nothing.
    internal static bool Read(NativeKernelEtwSession session, ref Cursor cursor, Action<View, bool> consume)
    {
        using var owner = session.AcquireReadLease();
        var status = Acquire(owner.OwnerHandle, out var view, out var next, out var token);
        if (status == 3) return false;
        KernelEtwNativeReaders.Check(status, "acquire notification publication");
        try
        {
            if (next == cursor.Publication) return false;
            if (view.Count != 0 && view.Records == 0)
                throw new InvalidDataException("Native kernel ETW notification rows are absent.");
            consume(view, cursor.Publication != 0 && view.First != cursor.Next);
            cursor = new() { Publication = next, Next = view.First + view.Count };
            return true;
        }
        finally { KernelEtwNativeReaders.Check(Release(owner.OwnerHandle, token), "release notification publication"); }
    }

    internal static ReadOnlySpan<Notification> Records(View view)
        => new((void*)view.Records, checked((int)view.Count));

    [StructLayout(LayoutKind.Sequential)]
    internal struct View
    {
        public ulong EventsLost;
        public uint Count, Reserved;
        public nint Records;
        public ulong First;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Notification
    {
        public uint Kind, Flags;
        public long Qpc, TickMilliseconds;
        public uint ProcessId, ThreadId;
        public ulong Key, Object, Base, Size;
        public nint Path;
        public uint PathLength, Reserved;
        public long TimeFileTime;
        public uint ParentProcessId, Reserved2;
    }

    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_acquire_notifications", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Acquire(nint owner, out View view, out ulong sequence, out ulong token);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_release", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Release(nint owner, ulong token);
}
