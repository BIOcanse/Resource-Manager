using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Infrastructure.Adaptation.Transport;

namespace ResourceManager.App.Infrastructure.Security;

public sealed partial class WindowsLoopbackApiAdministratorReader
{
    internal static bool TryReadConnectedPeer(Socket socket, out LoopbackApiProcess identity)
    {
        identity = null!;
        if (!OperatingSystem.IsWindows() || socket.LocalEndPoint is not IPEndPoint local
            || socket.RemoteEndPoint is not IPEndPoint remote)
            return false;
        var localAddress = Normalize(local.Address)!;
        var remoteAddress = Normalize(remote.Address)!;
        if (!IPAddress.IsLoopback(localAddress) || !IPAddress.IsLoopback(remoteAddress)) return false;
        // Find the server's connected row, not its listener or our client row.
        var owner = FindOwner(remoteAddress, remote.Port, localAddress, local.Port);
        if (!TryOpenProcessIdentity(owner, out identity, out var token,
            () => FindOwner(remoteAddress, remote.Port, localAddress, local.Port) == owner)) return false;
        token.Dispose();
        return true;
    }

    internal static bool TryReadProcessIdentity(int processId, out LoopbackApiProcess identity)
    {
        if (!TryOpenProcessIdentity(processId, out identity, out var token)) return false;
        token.Dispose();
        return true;
    }

    private static bool TryOpenProcessIdentity(int processId, out LoopbackApiProcess identity,
        out SafeAccessTokenHandle processToken, Func<bool>? confirmConnection = null)
    {
        identity = null!;
        processToken = SafeAccessTokenHandle.InvalidHandle;
        if (!OperatingSystem.IsWindows() || processId <= 0) return false;
        using var process = OpenProcess(0x1000, false, processId);
        if (process.IsInvalid || !OpenProcessToken(process, 0x000A, out var token)) return false;
        var accepted = false;
        try
        {
            using var windowsIdentity = new WindowsIdentity(token.DangerousGetHandle());
            var image = new StringBuilder(32768);
            var length = checked((uint)image.Capacity);
            if (windowsIdentity.User is not { } sid
                || !WindowsAdapterTransportNativeMethods.ProcessIdToSessionId(checked((uint)processId), out var session)
                || !WindowsAdapterTransportNativeMethods.GetProcessTimes(process, out var created, out _, out _, out _)
                || !WindowsAdapterTransportNativeMethods.QueryFullProcessImageNameW(process, 0, image, ref length)
                || confirmConnection is not null && !confirmConnection())
                return false;
            identity = new(new(processId, DateTime.FromFileTimeUtc(created.ToInt64()).Ticks,
                sid.Value, checked((int)session), new WindowsPrincipal(windowsIdentity).IsInRole(WindowsBuiltInRole.Administrator)),
                Path.GetFullPath(image.ToString()));
            processToken = token;
            accepted = true;
            return true;
        }
        catch (Exception error) when (error is Win32Exception or SecurityException or UnauthorizedAccessException
            or ArgumentException or IOException or NotSupportedException)
        { return false; }
        finally { if (!accepted) token.Dispose(); }
    }
}

internal sealed record LoopbackApiProcess(LoopbackApiCaller Caller, string ExecutablePath);
