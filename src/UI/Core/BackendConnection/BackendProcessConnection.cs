using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ResourceManager.Shared.ServiceHosting;

namespace ResourceManager.NativeUi;

internal sealed record BackendProcessIdentity(int ProcessId, long CreatedUtcTicks, string ExecutablePath);

// Verify the connected server socket before HttpClient can send credentials or an administrator operation.
internal static class BackendProcessConnection
{
    internal static BackendProcessIdentity? CaptureInstalledServiceIdentity(string expectedExecutablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedExecutablePath);
        var executablePath = Path.GetFullPath(expectedExecutablePath);
        var first = WindowsServiceRegistration.Read(WindowsServiceRegistration.ProductServiceName);
        if (first is null || !first.Running || first.ProcessId is 0 or > int.MaxValue)
        {
            return null;
        }
        WindowsServiceRegistration.RequireExpectedBinary(first, executablePath);

        var processId = checked((int)first.ProcessId);
        using var process = Process.GetProcessById(processId);
        var createdUtcTicks = process.StartTime.ToUniversalTime().Ticks;
        if (!VerifyProcess(processId, createdUtcTicks, executablePath))
        {
            return null;
        }

        var current = WindowsServiceRegistration.Read(WindowsServiceRegistration.ProductServiceName);
        if (current is null || !current.Running || current.ProcessId != first.ProcessId)
        {
            return null;
        }
        WindowsServiceRegistration.RequireExpectedBinary(current, executablePath);
        if (!VerifyProcess(processId, createdUtcTicks, executablePath))
        {
            return null;
        }

        return new BackendProcessIdentity(processId, createdUtcTicks, executablePath);
    }

    internal static SocketsHttpHandler CreateHandler(Func<BackendProcessIdentity?> expected)
        => new()
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectCallback = async (context, cancellationToken) =>
            {
                if (expected() is not { } identity || !IsLoopbackHost(context.DnsEndPoint.Host))
                    throw new UnauthorizedAccessException("The backend identity is unavailable.");
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, context.DnsEndPoint.Port, cancellationToken);
                    if (!VerifyConnectedServer(socket, identity))
                        throw new UnauthorizedAccessException("The connected server is not the attested backend.");
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };

    internal static bool IsLoopbackHost(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1";

    internal static bool VerifyProcess(int processId, long createdUtcTicks, string executablePath)
    {
        using var process = OpenProcess(0x1000, false, processId);
        if (process.IsInvalid || !GetProcessTimes(process, out var created, out _, out _, out _)
            || DateTime.FromFileTimeUtc(created).Ticks != createdUtcTicks) return false;
        var image = new StringBuilder(32768);
        var length = checked((uint)image.Capacity);
        return QueryFullProcessImageNameW(process, 0, image, ref length)
            && StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(image.ToString()), Path.GetFullPath(executablePath));
    }

    internal static bool VerifyConnectedServer(Socket socket, BackendProcessIdentity expected)
    {
        if (socket.LocalEndPoint is not IPEndPoint client || socket.RemoteEndPoint is not IPEndPoint server
            || !IPAddress.IsLoopback(client.Address) || !IPAddress.IsLoopback(server.Address)
            || client.AddressFamily != AddressFamily.InterNetwork || server.AddressFamily != AddressFamily.InterNetwork)
            return false;
        return FindServer(client, server) == expected.ProcessId
            && VerifyProcess(expected.ProcessId, expected.CreatedUtcTicks, expected.ExecutablePath)
            && FindServer(client, server) == expected.ProcessId;
    }

    private static int FindServer(IPEndPoint client, IPEndPoint server)
    {
        var size = 0;
        if (GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 4, 0) != 122
            || size < sizeof(uint) || size > 64 * 1024 * 1024) return 0;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var capacity = size;
            if (GetExtendedTcpTable(buffer, ref size, false, 2, 4, 0) != 0 || size > capacity) return 0;
            var count = Marshal.ReadInt32(buffer);
            var stride = Marshal.SizeOf<Tcp4Row>();
            if (count < 0 || count > (size - sizeof(uint)) / stride) return 0;
            var found = 0;
            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<Tcp4Row>(IntPtr.Add(buffer, sizeof(uint) + index * stride));
                if (row.State != 5 || Port(row.LocalPort) != server.Port || Port(row.RemotePort) != client.Port
                    || !new IPAddress(row.LocalAddress).Equals(server.Address)
                    || !new IPAddress(row.RemoteAddress).Equals(client.Address)) continue;
                if (found != 0 || row.ProcessId == 0 || row.ProcessId > int.MaxValue) return 0;
                found = checked((int)row.ProcessId);
            }
            return found;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static int Port(uint value) => (int)(((value & 0xff) << 8) | ((value >> 8) & 0xff));
    [StructLayout(LayoutKind.Sequential)]
    private struct Tcp4Row { internal uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, ProcessId; }
    [DllImport("iphlpapi.dll")]
    private static extern int GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int family, int tableClass, int reserved);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);
}
