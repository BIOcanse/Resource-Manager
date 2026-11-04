using System.Buffers;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.App.Infrastructure.Adaptation.Transport;

internal static partial class WindowsAdapterTransportNativeMethods
{
    // This is an attested read, not a process-identity cache. Each caller still
    // verifies the current process and connection; only the output buffer is reused.
    internal static unsafe bool TryQueryProcessPath(SafeProcessHandle process, out string path)
    {
        const int maximumWindowsPathLength = 32_768;
        var buffer = ArrayPool<char>.Shared.Rent(maximumWindowsPathLength);
        try
        {
            var length = (uint)maximumWindowsPathLength;
            fixed (char* pointer = buffer)
            {
                if (!QueryFullProcessImageNameW(process, 0, pointer, ref length))
                {
                    path = string.Empty;
                    return false;
                }
            }
            path = new string(buffer, 0, checked((int)length));
            return true;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }
}
