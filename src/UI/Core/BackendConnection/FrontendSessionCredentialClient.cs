using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ResourceManager.Shared.Runtime;

namespace ResourceManager.NativeUi;

internal static class FrontendSessionCredentialClient
{
    internal static async Task<string?> AcquireAsync(string expectedBackendPath, CancellationToken cancellationToken,
        string? testPipeName = null)
        => (await AcquireSessionAsync(expectedBackendPath, cancellationToken, testPipeName))?.AccessToken;

    internal static async Task<FrontendSessionCredentialReply?> AcquireSessionAsync(
        string expectedBackendPath, CancellationToken cancellationToken, string? testPipeName = null)
    {
        using var own = Process.GetCurrentProcess();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var pipe = new NamedPipeClientStream(".",
                testPipeName ?? FrontendSessionCredentialContract.PipeName(checked((uint)own.SessionId)),
                PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
            await pipe.ConnectAsync(deadline.Token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid)) return null;
            using var server = OpenProcess(0x1000, false, serverPid);
            if (server.IsInvalid || !GetProcessTimes(server, out var created, out _, out _, out _)) return null;
            var image = new StringBuilder(32768);
            var length = checked((uint)image.Capacity);
            if (!QueryFullProcessImageNameW(server, 0, image, ref length)
                || !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(image.ToString()), Path.GetFullPath(expectedBackendPath)))
                return null;
            await pipe.WriteAsync(new byte[] { FrontendSessionCredentialContract.ProtocolVersion }, deadline.Token);
            await pipe.FlushAsync(deadline.Token);
            var reply = await FrontendSessionCredentialContract.ReadAsync(pipe, deadline.Token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var currentPid) || currentPid != serverPid
                || !GetProcessTimes(server, out var currentCreated, out _, out _, out _) || currentCreated != created
                || reply.ProductId != FrontendSessionCredentialContract.ProductId
                || reply.ProtocolVersion != FrontendSessionCredentialContract.ProtocolVersion || reply.ServiceInstanceId == 0
                || reply.ServiceProcessId != serverPid || reply.ServiceProcessCreatedUtcTicks != DateTime.FromFileTimeUtc(created).Ticks
                || reply.UiProcessId != own.Id || reply.UiProcessCreatedUtcTicks != own.StartTime.ToUniversalTime().Ticks
                || reply.WindowsSessionId != own.SessionId || reply.AccessToken is null
                || reply.AccessToken.Length != FrontendSessionCredentialContract.AccessTokenHexLength
                || !reply.AccessToken.All(char.IsAsciiHexDigit)) return null;
            return reply;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception) { return null; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
    }

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
