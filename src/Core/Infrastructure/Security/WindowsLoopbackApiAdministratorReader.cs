using System.Net;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.App.Infrastructure.Security;

public sealed partial class WindowsLoopbackApiAdministratorReader : ILoopbackApiAdministratorReader
{
    public bool IsAdministrator(HttpContext context)
        => TryReadCaller(context, out var caller, out var administrator)
            && caller != Environment.ProcessId && administrator;

    internal static bool TryReadCaller(HttpContext context, out int processId, out bool administrator)
    {
        var found = TryReadCallerIdentity(context, out var caller);
        processId = found ? caller.ProcessId : 0;
        administrator = found && caller.IsAdministrator;
        return found;
    }

    internal static bool TryReadCallerIdentity(HttpContext context, out LoopbackApiCaller caller)
    {
        if (!TryReadConnectedCaller(context, out var peer))
        {
            caller = null!;
            return false;
        }
        caller = peer.Caller;
        return true;
    }

    internal static bool TryReadConnectedCaller(HttpContext context, out LoopbackApiProcess peer)
    {
        peer = null!;
        if (!OperatingSystem.IsWindows())
            return false;

        var connection = context.Connection;
        var clientAddress = Normalize(connection.RemoteIpAddress);
        var serverAddress = Normalize(connection.LocalIpAddress);
        if (clientAddress is null || serverAddress is null
            || !IPAddress.IsLoopback(clientAddress) || !IPAddress.IsLoopback(serverAddress))
            return false;

        var connections = context.RequestServices.GetService<LoopbackTcpOwner>();
        if (connections is null) return false;
        var owner = connections.Find(clientAddress, connection.RemotePort, serverAddress, connection.LocalPort);
        return TryOpenProcessIdentity(owner, out peer, out var token,
            () => connections.Find(clientAddress, connection.RemotePort, serverAddress, connection.LocalPort) == owner)
            && DisposeToken(token);

        static bool DisposeToken(SafeAccessTokenHandle? token)
        {
            token?.Dispose();
            return token is not null;
        }
    }

    internal static bool TryOpenCallerToken(HttpContext context, out LoopbackApiCaller caller,
        [NotNullWhen(true)] out SafeAccessTokenHandle? callerToken)
    {
        caller = null!;
        callerToken = null;
        var connection = context.Connection;
        return connection.RemoteIpAddress is { } client && connection.LocalIpAddress is { } server
            && context.RequestServices.GetService<LoopbackTcpOwner>() is { } connections
            && TryOpenCallerToken(connections, client, connection.RemotePort, server, connection.LocalPort,
                out caller, out callerToken);
    }

    // Platform proof shared by Kestrel and the native listener. The tuple is
    // supplied by their actual socket owner, never by HTTP or caller metadata.
    internal static bool TryOpenCallerToken(LoopbackTcpOwner connections,
        IPAddress client, int clientPort, IPAddress server, int serverPort,
        out LoopbackApiCaller caller, [NotNullWhen(true)] out SafeAccessTokenHandle? callerToken)
    {
        caller = null!;
        callerToken = null;
        if (!OperatingSystem.IsWindows())
            return false;

        var clientAddress = Normalize(client)!;
        var serverAddress = Normalize(server)!;
        if (!IPAddress.IsLoopback(clientAddress) || !IPAddress.IsLoopback(serverAddress))
            return false;

        var owner = connections.Find(clientAddress, clientPort, serverAddress, serverPort);
        if (owner <= 0)
            return false;

        if (!TryOpenProcessIdentity(owner, out var identity, out var token,
            () => connections.Find(clientAddress, clientPort, serverAddress, serverPort) == owner))
            return false;
        caller = identity.Caller;
        callerToken = token;
        return true;
    }

    private static IPAddress? Normalize(IPAddress? address)
        => address?.IsIPv4MappedToIPv6 == true ? address.MapToIPv4() : address;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);


}

internal sealed record LoopbackApiCaller(int ProcessId, long ProcessCreatedUtcTicks,
    string WindowsSid, int WindowsSessionId, bool IsAdministrator);
