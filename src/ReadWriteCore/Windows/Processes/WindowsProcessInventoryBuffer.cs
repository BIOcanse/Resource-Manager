using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Windows.Processes;

internal interface IWindowsProcessInventoryBufferApi
{
    int Query(IntPtr buffer, uint length, out uint returned);
    IntPtr Resize(IntPtr buffer, int length);
    void Free(IntPtr buffer);
}

/// <summary>One capture owner's kernel-table workspace. Borrowed spans last until
/// the next read/release; the owner's capture gate covers both query and parsing.</summary>
internal sealed class WindowsProcessInventoryBuffer : IDisposable
{
    private readonly WindowsProcessDetailsOptions options;
    private readonly IWindowsProcessInventoryBufferApi api;
    private IntPtr buffer;
    private bool disposed;
    internal int CapacityBytes { get; private set; }

    internal WindowsProcessInventoryBuffer(WindowsProcessDetailsOptions? options = null,
        IWindowsProcessInventoryBufferApi? api = null)
    {
        this.options = options ?? WindowsProcessDetailsOptions.Default;
        this.api = api ?? NativeApi.Instance;
    }

    internal unsafe bool TryRead(out ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        data = default;
        if (CapacityBytes == 0) Grow(options.InitialSystemInformationBufferBytes);
        for (var attempt = 0; attempt < options.MaximumSystemInformationAttempts; attempt++)
        {
            var status = api.Query(buffer, checked((uint)CapacityBytes), out var returned);
            if (status == 0)
            {
                if (returned == 0 || returned > CapacityBytes) return false;
                data = new ReadOnlySpan<byte>(buffer.ToPointer(), checked((int)returned));
                return true;
            }
            if (status is not (unchecked((int)0xC0000004) or unchecked((int)0xC0000023))) return false;
            var next = Math.Max((long)returned, (long)CapacityBytes * 2);
            if (attempt + 1 == options.MaximumSystemInformationAttempts
                || next > options.MaximumSystemInformationBufferBytes || next <= CapacityBytes) return false;
            Grow(checked((int)next));
        }
        return false;
    }

    private void Grow(int bytes)
    {
        if (bytes <= 0 || bytes > options.MaximumSystemInformationBufferBytes)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        var next = api.Resize(buffer, bytes);
        if (next == IntPtr.Zero) throw new OutOfMemoryException("Cannot allocate the process inventory workspace.");
        buffer = next;
        CapacityBytes = bytes;
    }

    internal void Release()
    {
        if (buffer != IntPtr.Zero) api.Free(buffer);
        buffer = IntPtr.Zero;
        CapacityBytes = 0;
    }

    public void Dispose()
    {
        if (disposed) return;
        Release();
        disposed = true;
    }

    private sealed class NativeApi : IWindowsProcessInventoryBufferApi
    {
        internal static readonly NativeApi Instance = new();
        public int Query(IntPtr buffer, uint length, out uint returned)
            => NtQuerySystemInformation(5, buffer, length, out returned);
        public IntPtr Resize(IntPtr buffer, int length) => Marshal.ReAllocHGlobal(buffer, new IntPtr(length));
        public void Free(IntPtr buffer) => Marshal.FreeHGlobal(buffer);
        [DllImport("ntdll.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int NtQuerySystemInformation(uint informationClass, IntPtr information, uint length, out uint returned);
    }
}
