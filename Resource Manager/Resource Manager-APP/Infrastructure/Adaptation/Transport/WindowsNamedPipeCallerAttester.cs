using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Adaptation.Transport;

internal sealed class WindowsNamedPipeCallerAttester : IAdapterTransportCallerAttester
{
    private const int MaximumWindowsPathLength = 32_768;

    public TrustedAdapterCallerIdentity Attest(
        NamedPipeServerStream connectedPipe,
        ulong hostInstanceId,
        ulong transportConnectionId)
    {
        ArgumentNullException.ThrowIfNull(connectedPipe);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }
        if (!connectedPipe.IsConnected)
        {
            throw new InvalidOperationException("The adapter pipe is not connected.");
        }
        if (hostInstanceId == 0 || transportConnectionId == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(hostInstanceId),
                "Host and transport identities must be nonzero.");
        }

        var pipeHandle = connectedPipe.SafePipeHandle;
        var processId = GetPipeClientProcessId(pipeHandle);
        var pipeSessionId = GetPipeClientSessionId(pipeHandle);
        var token = ReadImpersonatedToken(pipeHandle);
        if (!WindowsAdapterTransportNativeMethods.ProcessIdToSessionId(
                processId,
                out var processSessionId))
        {
            throw LastWin32();
        }
        if (pipeSessionId != processSessionId || pipeSessionId != token.SessionId)
        {
            throw new UnauthorizedAccessException(
                "Named-pipe, process and token sessions do not match.");
        }

        var process = ReadProcessIdentity(processId);
        if (GetPipeClientProcessId(pipeHandle) != processId
            || GetPipeClientSessionId(pipeHandle) != pipeSessionId)
        {
            throw new UnauthorizedAccessException(
                "The named-pipe client changed during caller attestation.");
        }

        return new TrustedAdapterCallerIdentity(
            hostInstanceId,
            transportConnectionId,
            checked((int)processId),
            process.CreatedUtcTicks,
            token.WindowsSid,
            checked((int)pipeSessionId),
            token.AuthenticationIdLuid,
            token.IntegrityLevelRid,
            token.IsElevated,
            process.CanonicalExecutablePath,
            process.FileIdentity,
            NativeAdapterInstanceLeaseSession.MonotonicNow());
    }

    private static uint GetPipeClientProcessId(SafePipeHandle pipeHandle)
    {
        if (!WindowsAdapterTransportNativeMethods.GetNamedPipeClientProcessId(
                pipeHandle,
                out var processId)
            || processId == 0)
        {
            throw LastWin32();
        }
        return processId;
    }

    private static uint GetPipeClientSessionId(SafePipeHandle pipeHandle)
    {
        if (!WindowsAdapterTransportNativeMethods.GetNamedPipeClientSessionId(
                pipeHandle,
                out var sessionId))
        {
            throw LastWin32();
        }
        return sessionId;
    }

    private static TokenFacts ReadImpersonatedToken(SafePipeHandle pipeHandle)
    {
        if (!WindowsAdapterTransportNativeMethods.ImpersonateNamedPipeClient(pipeHandle))
        {
            throw LastWin32();
        }
        try
        {
            if (!WindowsAdapterTransportNativeMethods.OpenThreadToken(
                    WindowsAdapterTransportNativeMethods.GetCurrentThread(),
                    WindowsAdapterTransportNativeMethods.TokenQuery,
                    openAsSelf: true,
                    out var tokenHandle))
            {
                throw LastWin32();
            }
            using (tokenHandle)
            {
                var sid = ReadTokenSid(
                    tokenHandle,
                    WindowsAdapterTransportNativeMethods.TokenInformationClass.TokenUser);
                var integritySid = ReadTokenSid(
                    tokenHandle,
                    WindowsAdapterTransportNativeMethods.TokenInformationClass.TokenIntegrityLevel);
                var statistics = ReadTokenStructure<WindowsAdapterTransportNativeMethods.TokenStatistics>(
                    tokenHandle,
                    WindowsAdapterTransportNativeMethods.TokenInformationClass.TokenStatistics);
                var sessionId = ReadTokenUInt32(
                    tokenHandle,
                    WindowsAdapterTransportNativeMethods.TokenInformationClass.TokenSessionId);
                var elevation = ReadTokenUInt32(
                    tokenHandle,
                    WindowsAdapterTransportNativeMethods.TokenInformationClass.TokenElevation);
                return new TokenFacts(
                    sid.Value,
                    sessionId,
                    statistics.AuthenticationId.ToUInt64(),
                    ReadIntegrityRid(integritySid),
                    elevation != 0);
            }
        }
        finally
        {
            if (!WindowsAdapterTransportNativeMethods.RevertToSelf())
            {
                throw LastWin32();
            }
        }
    }

    internal static ProcessFacts ReadProcessIdentity(uint processId)
    {
        using var process = WindowsAdapterTransportNativeMethods.OpenProcess(
            WindowsAdapterTransportNativeMethods.ProcessQueryLimitedInformation,
            inheritHandle: false,
            processId);
        if (process.IsInvalid)
        {
            throw LastWin32();
        }
        var firstPath = QueryProcessPath(process);
        var createdUtcTicks = QueryProcessCreatedUtcTicks(process);
        using var image = WindowsAdapterTransportNativeMethods.CreateFileW(
            firstPath,
            WindowsAdapterTransportNativeMethods.FileReadAttributes,
            WindowsAdapterTransportNativeMethods.FileShareRead
                | WindowsAdapterTransportNativeMethods.FileShareWrite
                | WindowsAdapterTransportNativeMethods.FileShareDelete,
            IntPtr.Zero,
            WindowsAdapterTransportNativeMethods.OpenExisting,
            flagsAndAttributes: 0,
            IntPtr.Zero);
        if (image.IsInvalid)
        {
            throw LastWin32();
        }
        var canonicalPath = QueryFinalPath(image);
        if (!string.Equals(
                NormalizeWindowsPath(firstPath),
                canonicalPath,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                NormalizeWindowsPath(QueryProcessPath(process)),
                canonicalPath,
                StringComparison.OrdinalIgnoreCase)
            || QueryProcessCreatedUtcTicks(process) != createdUtcTicks)
        {
            throw new UnauthorizedAccessException(
                "The adapter process image changed during caller attestation.");
        }
        if (!WindowsAdapterTransportNativeMethods.GetFileInformationByHandleEx(
                image,
                WindowsAdapterTransportNativeMethods.FileInformationClass.FileIdInfo,
                out var fileId,
                checked((uint)Marshal.SizeOf<WindowsAdapterTransportNativeMethods.FileIdInfo>())))
        {
            throw LastWin32();
        }
        return new ProcessFacts(
            canonicalPath,
            createdUtcTicks,
            new AdapterExecutableFileIdentity(
                fileId.VolumeSerialNumber,
                fileId.FileIdHigh,
                fileId.FileIdLow));
    }

    private static string QueryProcessPath(SafeProcessHandle process)
    {
        var path = new StringBuilder(MaximumWindowsPathLength);
        var length = checked((uint)path.Capacity);
        if (!WindowsAdapterTransportNativeMethods.QueryFullProcessImageNameW(
                process,
                flags: 0,
                path,
                ref length))
        {
            throw LastWin32();
        }
        return path.ToString(0, checked((int)length));
    }

    private static long QueryProcessCreatedUtcTicks(SafeProcessHandle process)
    {
        if (!WindowsAdapterTransportNativeMethods.GetProcessTimes(
                process,
                out var creationTime,
                out _,
                out _,
                out _))
        {
            throw LastWin32();
        }
        return DateTime.FromFileTimeUtc(creationTime.ToInt64()).Ticks;
    }

    private static string QueryFinalPath(SafeFileHandle image)
    {
        var capacity = 1024;
        while (capacity <= MaximumWindowsPathLength)
        {
            var path = new StringBuilder(capacity);
            var length = WindowsAdapterTransportNativeMethods.GetFinalPathNameByHandleW(
                image,
                path,
                checked((uint)path.Capacity),
                flags: 0);
            if (length == 0)
            {
                throw LastWin32();
            }
            if (length < path.Capacity)
            {
                return NormalizeWindowsPath(path.ToString());
            }
            capacity = checked((int)length + 1);
        }
        throw new PathTooLongException("The adapter executable path is too long.");
    }

    private static string NormalizeWindowsPath(string path)
    {
        var normalized = path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)
            ? $"\\\\{path[8..]}"
            : path.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase)
                ? path[4..]
                : path;
        return Path.GetFullPath(normalized).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
    }

    private static SecurityIdentifier ReadTokenSid(
        SafeAccessTokenHandle tokenHandle,
        WindowsAdapterTransportNativeMethods.TokenInformationClass informationClass)
    {
        using var information = ReadTokenInformation(tokenHandle, informationClass);
        var sidPointer = Marshal.ReadIntPtr(information.DangerousGetHandle());
        if (sidPointer == IntPtr.Zero)
        {
            throw new UnauthorizedAccessException("The adapter token SID is missing.");
        }
        return new SecurityIdentifier(sidPointer);
    }

    private static T ReadTokenStructure<T>(
        SafeAccessTokenHandle tokenHandle,
        WindowsAdapterTransportNativeMethods.TokenInformationClass informationClass)
        where T : struct
    {
        using var information = ReadTokenInformation(tokenHandle, informationClass);
        return Marshal.PtrToStructure<T>(information.DangerousGetHandle());
    }

    private static uint ReadTokenUInt32(
        SafeAccessTokenHandle tokenHandle,
        WindowsAdapterTransportNativeMethods.TokenInformationClass informationClass)
    {
        using var information = new SafeHGlobalBuffer(sizeof(uint));
        if (!WindowsAdapterTransportNativeMethods.GetTokenInformation(
                tokenHandle,
                informationClass,
                information.DangerousGetHandle(),
                sizeof(uint),
                out var returnedLength)
            || returnedLength != sizeof(uint))
        {
            throw LastWin32();
        }
        return unchecked((uint)Marshal.ReadInt32(information.DangerousGetHandle()));
    }

    private static SafeHGlobalBuffer ReadTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        WindowsAdapterTransportNativeMethods.TokenInformationClass informationClass)
    {
        _ = WindowsAdapterTransportNativeMethods.GetTokenInformation(
            tokenHandle,
            informationClass,
            IntPtr.Zero,
            0,
            out var length);
        var error = Marshal.GetLastWin32Error();
        if (length == 0
            || error != checked((int)WindowsAdapterTransportNativeMethods.ErrorInsufficientBuffer))
        {
            throw new Win32Exception(error);
        }
        var buffer = new SafeHGlobalBuffer(checked((int)length));
        if (!WindowsAdapterTransportNativeMethods.GetTokenInformation(
                tokenHandle,
                informationClass,
                buffer.DangerousGetHandle(),
                length,
                out _))
        {
            var failure = LastWin32();
            buffer.Dispose();
            throw failure;
        }
        return buffer;
    }

    private static uint ReadIntegrityRid(SecurityIdentifier integritySid)
    {
        var bytes = new byte[integritySid.BinaryLength];
        integritySid.GetBinaryForm(bytes, 0);
        if (bytes.Length < 12 || bytes[1] == 0)
        {
            throw new UnauthorizedAccessException("The adapter integrity SID is invalid.");
        }
        return BitConverter.ToUInt32(bytes, bytes.Length - sizeof(uint));
    }

    private static Win32Exception LastWin32()
        => new(Marshal.GetLastWin32Error());

    private sealed class SafeHGlobalBuffer : SafeHandle
    {
        internal SafeHGlobalBuffer(int size)
            : base(IntPtr.Zero, ownsHandle: true)
        {
            SetHandle(Marshal.AllocHGlobal(size));
        }

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle()
        {
            Marshal.FreeHGlobal(handle);
            return true;
        }
    }

    private sealed record TokenFacts(
        string WindowsSid,
        uint SessionId,
        ulong AuthenticationIdLuid,
        uint IntegrityLevelRid,
        bool IsElevated);

    internal sealed record ProcessFacts(
        string CanonicalExecutablePath,
        long CreatedUtcTicks,
        AdapterExecutableFileIdentity FileIdentity);
}
