using System.Runtime.InteropServices;
using ResourceManager.ReadWrite.Windows.Frames;
using ResourceManager.Shared.Overlay;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

/// <summary>
/// Reads the output size the injected overlay module already writes into its shared section on every hooked
/// Present (OverlayProtocol.h swapChainWidth/Height). Null when the target has no section (injected mode not
/// running), the header does not match this protocol, or the module has not seen a Present yet.
/// </summary>
internal interface IInjectedOverlaySizeReader
{
    FrameOutputResolution? Read(int processId, long processStartKey, DateTimeOffset now);
}

/// <summary>
/// Opens the section read-only for one read and maps only its header. The section is created by the overlay
/// host in the user's session with a DACL that grants SYSTEM; a reader in another session (the service) names
/// it through that session's BaseNamedObjects.
/// </summary>
internal sealed class InjectedOverlaySectionSizeReader : IInjectedOverlaySizeReader
{
    private const uint FileMapRead = 0x0004;
    private const string LocalPrefix = "Local\\";

    public FrameOutputResolution? Read(int processId, long processStartKey, DateTimeOffset now)
    {
        if (processId <= 0 || processStartKey <= 0) return null;
        var name = PerformanceOverlaySharedMemory.SectionName(processId, processStartKey);
        if (!ProcessIdToSessionId((uint)processId, out var targetSession)) return null;
        var mapping = targetSession == CurrentSession ? OpenFileMappingW(FileMapRead, false, name)
            : OpenSessionSection(targetSession, name[LocalPrefix.Length..]);
        if (mapping == 0) return null;
        try
        {
            var view = MapViewOfFile(mapping, FileMapRead, 0, 0, (nuint)PerformanceOverlaySharedMemory.HeaderBytes);
            if (view == 0) return null;
            try
            {
                var header = Marshal.PtrToStructure<PerformanceOverlayHeader>(view);
                if (header.Magic != PerformanceOverlaySharedMemory.Magic
                    || header.Version != PerformanceOverlaySharedMemory.Version
                    || header.HeaderBytes != PerformanceOverlaySharedMemory.HeaderBytes
                    || header.Enabled == PerformanceOverlaySharedMemory.Disabled || header.FrameCounter <= 0
                    || header.SwapChainWidth <= 0 || header.SwapChainHeight <= 0) return null;
                return new FrameOutputResolution(FrameOutputResolutionSource.InjectedSharedSection,
                    header.SwapChainWidth, header.SwapChainHeight, now);
            }
            finally { UnmapViewOfFile(view); }
        }
        finally { CloseHandle(mapping); }
    }

    private static uint CurrentSession { get; } = ProcessIdToSessionId((uint)Environment.ProcessId, out var session) ? session : uint.MaxValue;

    // OpenFileMapping supports Local/Global names, not another session's Local namespace. The
    // service opens the already-existing section by its absolute Object Manager name, read-only.
    private static unsafe nint OpenSessionSection(uint session, string leaf)
    {
        var path = $"\\Sessions\\{session}\\BaseNamedObjects\\{leaf}";
        fixed (char* characters = path)
        {
            var unicode = new UnicodeString { Length = checked((ushort)(path.Length * sizeof(char))),
                MaximumLength = checked((ushort)((path.Length + 1) * sizeof(char))), Buffer = (nint)characters };
            var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>(), ObjectName = (nint)(&unicode),
                Attributes = 0x40 }; // OBJ_CASE_INSENSITIVE
            return NtOpenSection(out var section, FileMapRead, ref attributes) >= 0 ? section : 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { internal ushort Length, MaximumLength; internal nint Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        internal int Length;
        internal nint RootDirectory, ObjectName;
        internal uint Attributes;
        internal nint SecurityDescriptor, SecurityQualityOfService;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtOpenSection(out nint section, uint desiredAccess, ref ObjectAttributes attributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint OpenFileMappingW(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint MapViewOfFile(nint mapping, uint desiredAccess, uint offsetHigh, uint offsetLow, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnmapViewOfFile(nint view);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
