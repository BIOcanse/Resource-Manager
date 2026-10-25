using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ResourceManager.Shared.Runtime.TrustedAccess;
using ResourceManager.Shared.ServiceHosting;

namespace ResourceManager.Launcher;

internal static class TrustedSessionLaunchClient
{
    internal static async Task<uint> LaunchAsync(string expectedBackendPath, SessionLaunchRequest command,
        string key, string failureMessage, CancellationToken cancellationToken)
    {
        try { return await LaunchCoreAsync(expectedBackendPath, command, key, cancellationToken); }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or System.Text.Json.JsonException or OperationCanceledException)
        {
            // The frontend supplies its already translated launch failure. Native protocol details stay diagnostic.
            throw new InvalidOperationException(failureMessage, error);
        }
    }

    private static async Task<uint> LaunchCoreAsync(string expectedBackendPath, SessionLaunchRequest command,
        string key, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var pipe = new NamedPipeClientStream(".", TrustedAccessInvocationContract.PipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        await pipe.ConnectAsync(deadline.Token);
        // SCM's LocalSystem contract and the actual pipe server process are checked before any key byte.
        var state = WindowsServiceRegistration.Read(WindowsServiceRegistration.ProductServiceName)
            ?? throw new UnauthorizedAccessException("The installed service is unavailable.");
        WindowsServiceRegistration.RequireExpectedBinary(state, expectedBackendPath);
        if (!state.Running || !GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid)
            || serverPid != state.ProcessId)
            throw new UnauthorizedAccessException("The invocation pipe is not owned by the installed service.");
        using var process = OpenProcess(0x1000, false, serverPid);
        if (process.IsInvalid || !GetProcessTimes(process, out var created, out _, out _, out _)) throw LastWin32();
        var image = new StringBuilder(32768);
        var length = checked((uint)image.Capacity);
        if (!QueryFullProcessImageNameW(process, 0, image, ref length)) throw LastWin32();
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(image.ToString()), Path.GetFullPath(expectedBackendPath)))
            throw new UnauthorizedAccessException("The invocation service image does not match.");

        await TrustedAccessInvocationContract.WriteRequestAsync(pipe,
            new(TrustedAccessInvocationContract.SessionLaunchOperation, key, SessionLaunchContract.Encode(command)), deadline.Token);
        var response = await TrustedAccessInvocationContract.ReadResponseAsync(pipe, deadline.Token);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var currentPid) || currentPid != serverPid
            || !GetProcessTimes(process, out var currentCreated, out _, out _, out _) || currentCreated != created)
            throw new UnauthorizedAccessException("The invocation service process changed.");
        if (response.Status != TrustedAccessInvocationContract.Success || response.Payload is null)
            throw new InvalidOperationException("The session launch was rejected: " + response.Status);
        return SessionLaunchContract.DecodeResult(response.Payload).ProcessId;
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
}
