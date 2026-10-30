using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ResourceManager.Shared.Overlay;

// Native mirror: src/Core/Native/PerformanceOverlay/OverlayProtocol.h.
public static class PerformanceOverlaySharedMemory
{
    public const uint Magic = 0x564F4D52; // "RMOV" in little endian.
    public const uint Version = 2;
    public const int HeaderBytes = 88;
    public const int MaximumPixelBytes = 64 * 1024 * 1024;
    // Handshake reports every present using only the header. Disabled also retires
    // a never-published section, so its reader cannot keep it alive after Dispose.
    public const int Handshake = 0, Enabled = 1, Disabled = -1;

    static PerformanceOverlaySharedMemory()
    {
        if (Marshal.SizeOf<PerformanceOverlayHeader>() != HeaderBytes
            || Marshal.OffsetOf<PerformanceOverlayHeader>(nameof(PerformanceOverlayHeader.Generation)).ToInt32() != 16
            || Marshal.OffsetOf<PerformanceOverlayHeader>(nameof(PerformanceOverlayHeader.SwapChainWidth)).ToInt32() != 72
            || Marshal.OffsetOf<PerformanceOverlayHeader>(nameof(PerformanceOverlayHeader.PublisherProcessId)).ToInt32() != 80)
            throw new TypeLoadException("The performance overlay shared header layout is incompatible.");
    }

    public static string SectionName(int processId, long creationFileTimeUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(creationFileTimeUtc);
        return $"Local\\ResourceManager.PerformanceOverlay.{processId:X8}.{creationFileTimeUtc:X16}";
    }

    // A producer creates the section with this protected DACL; the DLL only opens it.
    public static string SectionSddl(SecurityIdentifier interactiveUserSid)
    {
        ArgumentNullException.ThrowIfNull(interactiveUserSid);
        if (!interactiveUserSid.IsAccountSid())
            throw new ArgumentException("An account SID is required.", nameof(interactiveUserSid));
        return $"D:P(A;;GA;;;SY)(A;;GA;;;{interactiveUserSid.Value})";
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct PerformanceOverlayHeader
{
    public uint Magic;
    public uint Version;
    public uint HeaderBytes;
    public uint PixelCapacity;
    // Producer: increment to odd before changing the bitmap, publish the next even value last.
    public long Generation;
    // DLL outputs. All 64-bit counters use atomic reads/writes.
    public long ConsumedGeneration;
    public long FrameCounter;
    public long DrawSuccessCounter;
    // Producer's selected target HWND. Zero accepts any window (useful for a single-window test).
    public ulong TargetWindow;
    public uint Width;
    public uint Height;
    public uint Stride;
    public int Enabled;
    public int SwapChainWidth;
    public int SwapChainHeight;
    // Reader opens a SYNCHRONIZE process handle once and drops its view on publisher exit.
    public uint PublisherProcessId;
}
