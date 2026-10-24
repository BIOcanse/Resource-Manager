using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ResourceManager.Shared.Runtime;

namespace ResourceManager.Launcher;

internal static class NativeUiOpenSessionClient
{
    internal static async Task OpenAsync(string expectedBackendPath, bool background, CancellationToken cancellationToken)
    {
        using var own = Process.GetCurrentProcess();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // The SCM running state can precede the first active-session pipe scan.
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var pipe = new NamedPipeClientStream(".",
            FrontendSessionCredentialContract.PipeName(checked((uint)own.SessionId)),
            PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        await pipe.ConnectAsync(deadline.Token);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid)) throw LastWin32();
        using var server = OpenProcess(0x1000, false, serverPid);
        if (server.IsInvalid || !GetProcessTimes(server, out var created, out _, out _, out _)) throw LastWin32();
        var image = new StringBuilder(32768);
        var length = checked((uint)image.Capacity);
        if (!QueryFullProcessImageNameW(server, 0, image, ref length)) throw LastWin32();
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(image.ToString()), Path.GetFullPath(expectedBackendPath)))
            throw new UnauthorizedAccessException("The UI opening pipe is not owned by the installed backend.");
        if (!OpenProcessToken(server, 0x0008, out var token)) throw LastWin32();
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            if (!identity.IsSystem) throw new UnauthorizedAccessException("The UI opening backend is not SYSTEM.");

        await pipe.WriteAsync(new byte[] { NativeUiOpenSessionContract.OpenOperation,
            background ? NativeUiOpenSessionContract.OpenBackground : NativeUiOpenSessionContract.OpenForeground }, deadline.Token);
        await pipe.FlushAsync(deadline.Token);
        var reply = new byte[1];
        await pipe.ReadExactlyAsync(reply, deadline.Token);
        if (reply[0] != NativeUiOpenSessionContract.Accepted
            || !GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var currentPid) || currentPid != serverPid
            || !GetProcessTimes(server, out var currentCreated, out _, out _, out _) || currentCreated != created)
            throw new UnauthorizedAccessException("The service did not confirm opening the Native UI.");
    }

    private static Win32Exception LastWin32() => new(Marshal.GetLastWin32Error());

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
