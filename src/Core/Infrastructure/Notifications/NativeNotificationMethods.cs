using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.Notifications;

internal static unsafe partial class NativeNotificationMethods
{
    private const string Library = "ResourceManager.NativeCore";
    [StructLayout(LayoutKind.Sequential)] internal struct Config { internal nuint MaximumItems, MaximumSubscribers; }
    [StructLayout(LayoutKind.Sequential)] internal struct Meta { internal long Sequence; internal uint Flags; }
    [StructLayout(LayoutKind.Sequential)] internal struct Store { internal int Version; internal long NextSequence; internal nuint Count; }
    [StructLayout(LayoutKind.Sequential)] internal struct Callbacks
    {
        internal delegate* unmanaged[Cdecl]<nint, void> Release;
        internal delegate* unmanaged[Cdecl]<nint, Meta> Describe;
        internal delegate* unmanaged[Cdecl]<nint, Store*, int> Load;
        internal delegate* unmanaged[Cdecl]<nint, nuint, nint> LoadedItem;
        internal delegate* unmanaged[Cdecl]<nint, void> LoadedEnd;
        internal delegate* unmanaged[Cdecl]<nint, long, nint> CreateItem;
        internal delegate* unmanaged[Cdecl]<nint, long, nint*, nuint, nint, int> Save;
        internal delegate* unmanaged[Cdecl]<nint, nint*, nuint, nint*, nuint, int> Deliver;
        internal delegate* unmanaged[Cdecl]<nint, uint, int> LogError;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Projection
    {
        internal delegate* unmanaged[Cdecl]<nint, nuint, uint> ScopeAt;
        internal delegate* unmanaged[Cdecl]<nint, nuint, void> Select;
    }
    [LibraryImport(Library, EntryPoint = "rm_notifications_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint Create(in Config config, in Callbacks callbacks);
    [LibraryImport(Library, EntryPoint = "rm_notifications_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Destroy(nint owner);
    [LibraryImport(Library, EntryPoint = "rm_notifications_read")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Read(NotificationOwner.OwnerHandle owner, nint context, uint persist);
    [LibraryImport(Library, EntryPoint = "rm_notifications_post")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Post(NotificationOwner.OwnerHandle owner, nint context, uint persist, nuint capacity, uint flags);
    [LibraryImport(Library, EntryPoint = "rm_notifications_subscribe")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Subscribe(NotificationOwner.OwnerHandle owner, nint context, uint persist, nint reader);
    [LibraryImport(Library, EntryPoint = "rm_notifications_unsubscribe")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Unsubscribe(nint owner, nint reader);
    [LibraryImport(Library, EntryPoint = "rm_notifications_owned_bytes")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nuint OwnedBytes(NotificationOwner.OwnerHandle owner);
    [LibraryImport(Library, EntryPoint = "rm_notifications_scope")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint Scope(char* kind, nuint length);
    internal static uint Scope(string? kind)
    {
        fixed (char* pointer = kind) return Scope(pointer, (nuint)(kind?.Length ?? 0));
    }
    [LibraryImport(Library, EntryPoint = "rm_notifications_project")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nuint Project(nint context, nuint count, uint access, in Projection callbacks);
    [LibraryImport(Library, EntryPoint = "rm_notifications_sequences_equal")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint SequencesEqual(nint context, nuint left, nuint right,
        delegate* unmanaged[Cdecl]<nint, uint, nuint, long> sequence);
}
