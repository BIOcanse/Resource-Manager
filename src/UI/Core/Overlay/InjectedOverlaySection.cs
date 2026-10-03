using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Security.Principal;
using ResourceManager.Shared.Overlay;

namespace ResourceManager.NativeUi.Overlay;

internal unsafe sealed class InjectedOverlaySection : IDisposable
{
    private readonly nint mapping;
    private readonly nint view;
    private readonly PerformanceOverlayHeader* header;
    private bool disposed;

    public InjectedOverlaySection(OverlayTargetId id, nint targetWindow)
    {
        var name = PerformanceOverlaySharedMemory.SectionName(id.ProcessId,
            checked((long)id.ProcessStartKey));
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The interactive account SID is unavailable.");
        var sddl = PerformanceOverlaySharedMemory.SectionSddl(user);
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        bool existing;
        try
        {
            var security = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor
            };
            var bytes = (ulong)PerformanceOverlaySharedMemory.HeaderBytes
                + PerformanceOverlaySharedMemory.MaximumPixelBytes;
            mapping = CreateFileMapping(new nint(-1), ref security, 4,
                (uint)(bytes >> 32), (uint)bytes, name);
            if (mapping == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            existing = Marshal.GetLastWin32Error() == 183;
        }
        finally { LocalFree(descriptor); }

        view = MapViewOfFile(mapping, 0x0002 | 0x0004, 0, 0, 0);
        if (view == 0)
        {
            CloseHandle(mapping);
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        header = (PerformanceOverlayHeader*)view;
        if (existing)
        {
            if (header->Magic != PerformanceOverlaySharedMemory.Magic
                || header->Version != PerformanceOverlaySharedMemory.Version
                || header->HeaderBytes != PerformanceOverlaySharedMemory.HeaderBytes
                || header->PixelCapacity != PerformanceOverlaySharedMemory.MaximumPixelBytes)
            {
                UnmapViewOfFile(view);
                CloseHandle(mapping);
                throw new InvalidDataException("The existing overlay section has an incompatible header.");
            }
            Interlocked.Exchange(ref header->Enabled, 0);
            SetTargetWindow(targetWindow);
            return;
        }
        *header = new PerformanceOverlayHeader
        {
            Magic = PerformanceOverlaySharedMemory.Magic,
            Version = PerformanceOverlaySharedMemory.Version,
            HeaderBytes = PerformanceOverlaySharedMemory.HeaderBytes,
            PixelCapacity = PerformanceOverlaySharedMemory.MaximumPixelBytes,
            TargetWindow = checked((ulong)targetWindow)
        };
    }

    public Size SwapChainSize => new(
        Volatile.Read(ref header->SwapChainWidth),
        Volatile.Read(ref header->SwapChainHeight));

    internal long Generation => Volatile.Read(ref header->Generation);
    internal bool Enabled => Volatile.Read(ref header->Enabled) != 0;
    internal long ConsumedGeneration => Volatile.Read(ref header->ConsumedGeneration);
    internal long DrawSuccessCounter => Volatile.Read(ref header->DrawSuccessCounter);

    public void SetTargetWindow(nint window)
    {
        var value = checked((ulong)window);
        if (header->TargetWindow == value) return;
        header->TargetWindow = value;
        Interlocked.Exchange(ref header->SwapChainWidth, 0);
        Interlocked.Exchange(ref header->SwapChainHeight, 0);
    }

    public void Publish(OverlayImage image)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (image.Width <= 0 || image.Height <= 0 || image.Width > 8192 || image.Height > 8192
            || image.Pixels.Length != checked(image.Stride * image.Height)
            || image.Pixels.Length > PerformanceOverlaySharedMemory.MaximumPixelBytes)
            throw new ArgumentOutOfRangeException(nameof(image));

        var nextOdd = checked((Volatile.Read(ref header->Generation) & ~1L) + 1);
        Interlocked.Exchange(ref header->Generation, nextOdd);
        header->Width = (uint)image.Width;
        header->Height = (uint)image.Height;
        header->Stride = (uint)image.Stride;
        Marshal.Copy(image.Pixels, 0, view + PerformanceOverlaySharedMemory.HeaderBytes,
            image.Pixels.Length);
        Interlocked.Exchange(ref header->Generation, nextOdd + 1);
        Interlocked.Exchange(ref header->Enabled, 1);
    }

    public void Disable()
    {
        if (!disposed) Interlocked.Exchange(ref header->Enabled, 0);
    }

    public void Dispose()
    {
        if (disposed) return;
        Disable();
        disposed = true;
        UnmapViewOfFile(view);
        CloseHandle(mapping);
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes
    { public int Length; public nint Descriptor; public int InheritHandle; }
    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string sddl, uint revision, out nint descriptor, nint size);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileMappingW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern nint CreateFileMapping(nint file, ref SecurityAttributes security,
        uint protection, uint highSize, uint lowSize, string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint MapViewOfFile(
        nint mapping, uint access, uint highOffset, uint lowOffset, nuint bytes);
    [DllImport("kernel32.dll")] private static extern bool UnmapViewOfFile(nint view);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
