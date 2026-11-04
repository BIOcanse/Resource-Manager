using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ResourceManager.Shared.Runtime;

namespace ResourceManager.App.Infrastructure.NativeCore;

// Pending registration in the EXISTING NativeCore ABI and actual L/H/R input.
// R supplies a locally leased STABLE native copy, never live external W. T and
// the full original selection tuple are checked before and after each read.
// This bridge owns no current, selection, permission, callback registration,
// timer, pin or lease. All pointers and callbacks end before the call returns.
internal static unsafe partial class NativeDesktopObservation
{
    private const string LibraryName = "ResourceManager.NativeCore";

    [StructLayout(LayoutKind.Sequential)]
    internal struct State { internal long LastObservedAtUtcTicks; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Publisher
    {
        internal int SessionId;
        internal int ProcessId;
        internal long CreationFileTimeUtc;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Window
    {
        internal int ProcessId;
        internal uint HasVisibleWindow;
        internal uint HasBackgroundWindow;
        internal long CreationFileTimeUtc;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Frame
    {
        internal Publisher Publisher;
        internal long ObservedAtUtcTicks;
        internal uint Complete;
        internal uint ForegroundKnown;
        internal uint ForegroundPresent;
        internal uint WindowsPresent;
        internal Window Foreground;
        internal uint WindowCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Windows
    {
        internal Window* Values;
        internal uint Count;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Limits { internal uint MaximumWindows; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Process
    {
        internal int ProcessId;
        internal int SessionId;
        internal long CreationFileTimeUtc;
    }

    // Internal ABI codes, mapped by the original consumer to its existing
    // constants. These are not additional external wire values.
    internal enum RuntimeState : uint
    { Unavailable, ForegroundFocused, ForegroundUnfocused, BackgroundWindow, BackgroundProcess }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Fact
    {
        internal uint Verified;
        internal uint Available;
        internal RuntimeState RuntimeState;
        internal uint ForegroundFocused;
        internal uint HasVisibleWindow;
        internal uint HasBackgroundWindow;
    }

    // Original gate covers the whole call. commit rechecks full selection and
    // transfers trusted T before returning 1; notify is the original no-throw
    // native publication signal. Neither callback may retain these pointers.
    internal static bool Publish(ref State state, Publisher selected, bool publisherVerified,
        Frame* stableFrame, Window* stableRows, uint rowCount, long nowUtcTicks,
        nint context, delegate* unmanaged[Cdecl]<nint, uint> commit,
        delegate* unmanaged[Cdecl]<nint, void> notify)
    {
        if (stableFrame == null || stableFrame->WindowCount != rowCount ||
            rowCount > InteractiveDesktopObservation.MaximumWindows ||
            (rowCount != 0 && stableRows == null) || commit == null || notify == null) return false;
        var windows = new Windows { Values = stableRows, Count = rowCount };
        var limits = new Limits { MaximumWindows = InteractiveDesktopObservation.MaximumWindows };
        return PublishNative(ref state, in selected, publisherVerified ? 1u : 0u,
            stableFrame, in windows, nowUtcTicks, in limits, context, commit, notify) == 1;
    }

    // Original connection stops ingress first. retire invalidates T and removes
    // exactly its own selection, returning 0 for stale/repeated end. The host
    // drains in-flight callbacks/borrows before releasing callback context/maps.
    internal static bool End(nint context, delegate* unmanaged[Cdecl]<nint, uint> retire,
        delegate* unmanaged[Cdecl]<nint, void> notify)
        => retire != null && notify != null && EndNative(context, retire, notify) == 1;

    // Pure projection only: not a grant or the final scoped I/P ReadCurrent.
    // Original verified process/session facts remain the caller's responsibility.
    internal static Fact ReadCurrent(Process process, Frame* stableFrame, Window* stableRows,
        uint rowCount, long nowUtcTicks)
    {
        if (rowCount > InteractiveDesktopObservation.MaximumWindows ||
            (stableFrame != null && stableFrame->WindowCount != rowCount) ||
            (rowCount != 0 && stableRows == null)) return default;
        var windows = new Windows { Values = stableRows, Count = rowCount };
        ReadNative(in process, stableFrame, in windows, nowUtcTicks, out var fact);
        return fact;
    }

    [LibraryImport(LibraryName, EntryPoint = "rm_desktop_observation_publish")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint PublishNative(ref State state, in Publisher selected, uint publisherVerified,
        Frame* frame, in Windows windows, long nowUtcTicks, in Limits limits, nint context,
        delegate* unmanaged[Cdecl]<nint, uint> commit, delegate* unmanaged[Cdecl]<nint, void> notify);

    [LibraryImport(LibraryName, EntryPoint = "rm_desktop_observation_end")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint EndNative(nint context, delegate* unmanaged[Cdecl]<nint, uint> retire,
        delegate* unmanaged[Cdecl]<nint, void> notify);

    [LibraryImport(LibraryName, EntryPoint = "rm_desktop_observation_read")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void ReadNative(in Process process, Frame* frame, in Windows windows,
        long nowUtcTicks, out Fact output);
}
