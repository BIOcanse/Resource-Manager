using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.App.Infrastructure.Security;

public sealed class WindowsLoopbackApiAdministratorReader : ILoopbackApiAdministratorReader
{
    public bool IsAdministrator(HttpContext context)
        => TryReadCaller(context, out _, out var administrator) && administrator;

    internal static bool TryReadCaller(HttpContext context, out int processId, out bool administrator)
    {
        processId = 0;
        administrator = false;
        if (!OperatingSystem.IsWindows())
            return false;

        var connection = context.Connection;
        var clientAddress = Normalize(connection.RemoteIpAddress);
        var serverAddress = Normalize(connection.LocalIpAddress);
        if (clientAddress is null || serverAddress is null
            || !IPAddress.IsLoopback(clientAddress) || !IPAddress.IsLoopback(serverAddress))
            return false;

        var owner = FindOwner(clientAddress, connection.RemotePort, serverAddress, connection.LocalPort);
        if (owner <= 0)
            return false;

        using var process = OpenProcess(0x1000, false, owner); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process.IsInvalid || !OpenProcessToken(process, 0x000A, out var token))
            return false;
        using (token)
        {
            try
            {
                using var identity = new WindowsIdentity(token.DangerousGetHandle());
                var member = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                // Keep the process handle alive and recheck the same client socket, never the server row.
                if (FindOwner(clientAddress, connection.RemotePort, serverAddress, connection.LocalPort) != owner)
                    return false;
                processId = owner;
                administrator = member;
                return true;
            }
            catch (Exception exception) when (exception is Win32Exception or SecurityException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private static IPAddress? Normalize(IPAddress? address)
        => address?.IsIPv4MappedToIPv6 == true ? address.MapToIPv4() : address;

    private static int FindOwner(IPAddress client, int clientPort, IPAddress server, int serverPort)
    {
        if (client.AddressFamily != server.AddressFamily || clientPort <= 0 || serverPort <= 0)
            return 0;
        var ipv6 = client.AddressFamily == AddressFamily.InterNetworkV6;
        var family = ipv6 ? 23 : 2;
        var size = 0;
        const int ownerPidConnections = 4;
        const int insufficientBuffer = 122;
        if (GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, ownerPidConnections, 0) != insufficientBuffer
            || size < sizeof(uint))
            return 0;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, family, ownerPidConnections, 0) != 0)
                return 0;
            var count = Marshal.ReadInt32(buffer);
            var rowSize = ipv6 ? Marshal.SizeOf<Tcp6Row>() : Marshal.SizeOf<Tcp4Row>();
            if (count < 0 || count > (size - sizeof(uint)) / rowSize)
                return 0;
            var owner = 0;
            for (var index = 0; index < count; index++)
            {
                var address = IntPtr.Add(buffer, sizeof(uint) + index * rowSize);
                uint pid;
                if (ipv6)
                {
                    var row = Marshal.PtrToStructure<Tcp6Row>(address);
                    if (row.State != 5 || Port(row.LocalPort) != clientPort || Port(row.RemotePort) != serverPort
                        || !new IPAddress(row.LocalAddress, row.LocalScope).Equals(client)
                        || !new IPAddress(row.RemoteAddress, row.RemoteScope).Equals(server))
                        continue;
                    pid = row.ProcessId;
                }
                else
                {
                    var row = Marshal.PtrToStructure<Tcp4Row>(address);
                    if (row.State != 5 || Port(row.LocalPort) != clientPort || Port(row.RemotePort) != serverPort
                        || !new IPAddress(row.LocalAddress).Equals(client)
                        || !new IPAddress(row.RemoteAddress).Equals(server))
                        continue;
                    pid = row.ProcessId;
                }
                if (pid == 0 || pid > int.MaxValue || owner != 0)
                    return 0;
                owner = (int)pid;
            }
            return owner;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int Port(uint value) => (int)(((value & 0xff) << 8) | ((value >> 8) & 0xff));

    [DllImport("iphlpapi.dll")]
    private static extern int GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int family, int tableClass, int reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [StructLayout(LayoutKind.Sequential)]
    private struct Tcp4Row
    {
        public uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, ProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Tcp6Row
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddress;
        public uint LocalScope, LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] RemoteAddress;
        public uint RemoteScope, RemotePort, State, ProcessId;
    }
}
