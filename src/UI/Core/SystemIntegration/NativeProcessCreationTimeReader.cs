using System.Runtime.InteropServices;

namespace ResourceManager.NativeUi.SystemIntegration;

internal enum ProcessCreationTimeReadStatus
{
    Success,
    Unavailable,
    Exited
}

internal readonly record struct ProcessCreationTimeReadResult(
    ProcessCreationTimeReadStatus Status,
    long? CreationFileTimeUtc,
    int? Win32Error,
    Exception? Error,
    bool HandleCloseFailed);

internal interface IProcessCreationTimeNativeCalls
{
    IntPtr OpenProcess(uint desiredAccess, uint processId);
    bool TryGetCreationFileTime(IntPtr processHandle, out long creationFileTimeUtc);
    bool CloseHandle(IntPtr handle);
    int GetLastError();
}

internal static class NativeProcessCreationTimeReader
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorNotFound = 1168;

    internal static ProcessCreationTimeReadResult Read(
        int processId,
        IProcessCreationTimeNativeCalls nativeCalls)
    {
        ArgumentNullException.ThrowIfNull(nativeCalls);
        IntPtr handle;
        try
        {
            handle = nativeCalls.OpenProcess(ProcessQueryLimitedInformation, checked((uint)processId));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(ProcessCreationTimeReadStatus.Unavailable, null, null, exception, false);
        }

        if (handle == IntPtr.Zero)
        {
            var error = TryGetLastError(nativeCalls, out var errorReadException);
            return new(IsProcessGone(error) ? ProcessCreationTimeReadStatus.Exited : ProcessCreationTimeReadStatus.Unavailable,
                null, error, errorReadException, false);
        }

        var status = ProcessCreationTimeReadStatus.Unavailable;
        long? creationTime = null;
        int? failureCode = null;
        Exception? failure = null;
        var closeFailed = false;
        try
        {
            if (nativeCalls.TryGetCreationFileTime(handle, out var observedCreationTime))
            {
                status = ProcessCreationTimeReadStatus.Success;
                creationTime = observedCreationTime;
            }
            else
            {
                failureCode = TryGetLastError(nativeCalls, out failure);
                if (IsProcessGone(failureCode))
                    status = ProcessCreationTimeReadStatus.Exited;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            failure = exception;
        }
        finally
        {
            try
            {
                if (!nativeCalls.CloseHandle(handle))
                {
                    closeFailed = true;
                    failureCode = TryGetLastError(nativeCalls, out var closeErrorException) ?? failureCode;
                    failure ??= closeErrorException;
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                closeFailed = true;
                failure ??= exception;
            }
        }

        if (closeFailed && status == ProcessCreationTimeReadStatus.Success)
        {
            status = ProcessCreationTimeReadStatus.Unavailable;
            creationTime = null;
        }
        return new(status, creationTime, failureCode, failure, closeFailed);
    }

    private static int? TryGetLastError(IProcessCreationTimeNativeCalls nativeCalls, out Exception? error)
    {
        try
        {
            error = null;
            return nativeCalls.GetLastError();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            error = exception;
            return null;
        }
    }

    private static bool IsProcessGone(int? error) =>
        error is ErrorInvalidParameter or ErrorNotFound;
}

internal sealed class WindowsProcessCreationTimeNativeCalls : IProcessCreationTimeNativeCalls
{
    internal static WindowsProcessCreationTimeNativeCalls Instance { get; } = new();

    private WindowsProcessCreationTimeNativeCalls() { }

    public IntPtr OpenProcess(uint desiredAccess, uint processId) =>
        NativeOpenProcess(desiredAccess, false, processId);

    public bool TryGetCreationFileTime(IntPtr processHandle, out long creationFileTimeUtc)
    {
        if (!NativeGetProcessTimes(processHandle, out var creationTime, out _, out _, out _))
        {
            creationFileTimeUtc = 0;
            return false;
        }
        creationFileTimeUtc = creationTime.ToInt64();
        return true;
    }

    public bool CloseHandle(IntPtr handle) => NativeCloseHandle(handle);
    public int GetLastError() => Marshal.GetLastWin32Error();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;

        public readonly long ToInt64() => ((long)HighDateTime << 32) | LowDateTime;
    }

    [DllImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    private static extern IntPtr NativeOpenProcess(uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "GetProcessTimes", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeGetProcessTimes(IntPtr processHandle, out NativeFileTime creationTime,
        out NativeFileTime exitTime, out NativeFileTime kernelTime, out NativeFileTime userTime);
    [DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeCloseHandle(IntPtr handle);
}
