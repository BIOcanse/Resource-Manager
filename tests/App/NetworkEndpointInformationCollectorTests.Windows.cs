using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using ResourceManager.App.Infrastructure.DeviceTopology;

namespace Resource_Manager_APP.Tests;

public sealed partial class NetworkEndpointInformationCollectorTests
{
    [Fact]
    public async Task ActualOwnedProcessLeaseRejectsExitedHandleEvenWhenExitCodeIsStillActiveNumber()
    {
        var executable=Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe");
        var start=new ProcessStartInfo(executable) { UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,
            RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true };
        start.ArgumentList.Add("-NoProfile");start.ArgumentList.Add("-NonInteractive");start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("[Console]::WriteLine('endpoint-ready'); [Console]::ReadLine() | Out-Null; exit 259");
        using var child=Process.Start(start)!;
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try {
            Assert.Equal("endpoint-ready",await child.StandardOutput.ReadLineAsync(timeout.Token));
            using var lease=WindowsNetworkEndpointProcessLease.Open((uint)child.Id);
            Assert.NotNull(lease);Assert.True(lease.TryReadCreationFileTime(out var before));Assert.True(before>0);
            await child.StandardInput.WriteLineAsync();await child.StandardInput.FlushAsync();
            await child.WaitForExitAsync(timeout.Token);Assert.Equal(259,child.ExitCode);
            Assert.False(lease.TryReadCreationFileTime(out var after));Assert.Equal(0UL,after);
        }
        finally { if(!child.HasExited) { child.Kill();await child.WaitForExitAsync(); } }
    }

    [Fact]
    public async Task ActualWindowsOwnedLoopbackTcpAndUdpBindingsCarryExactCurrentProcessCreationIdentity()
    {
        Assert.True(OperatingSystem.IsWindows());
        using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        var port=((IPEndPoint)listener.LocalEndpoint).Port;
        using var client=new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback,port);
        using var accepted=await listener.AcceptTcpClientAsync();
        using var udp4=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);
        udp4.Bind(new IPEndPoint(IPAddress.Loopback,0));
        using var udp6=new Socket(AddressFamily.InterNetworkV6,SocketType.Dgram,ProtocolType.Udp);
        udp6.Bind(new IPEndPoint(IPAddress.IPv6Loopback,0));
        using var process=Process.GetCurrentProcess();
        var created=process.StartTime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture);
        var result=new WindowsNetworkEndpointInformationReader(new WindowsNetworkEndpointTableApi()).Read();
        Assert.NotNull(result.TcpIpv4);Assert.NotNull(result.TcpIpv6);Assert.NotNull(result.UdpIpv4);Assert.NotNull(result.UdpIpv6);
        var listening=Assert.Single(result.TcpIpv4, row=>row.Local.Address=="127.0.0.1"&&row.Local.Port==port&&row.StateRaw==2);
        Assert.Null(listening.Remote);Assert.Equal((uint)Environment.ProcessId,listening.OwningProcessIdReported);
        Assert.Equal(created,listening.ProcessIdentity!.CreationWindowsFileTime100Ns);
        Assert.Contains(result.TcpIpv4,row=>row.Local.Port==port&&row.StateRaw==5&&row.Remote?.Address=="127.0.0.1"&&row.ProcessIdentity?.CreationWindowsFileTime100Ns==created);
        Assert.Contains(result.UdpIpv4,row=>row.Local.Address=="127.0.0.1"&&row.Local.Port==((IPEndPoint)udp4.LocalEndPoint!).Port&&row.ProcessIdentity?.CreationWindowsFileTime100Ns==created);
        Assert.Contains(result.UdpIpv6,row=>row.Local.Address=="::1"&&row.Local.Port==((IPEndPoint)udp6.LocalEndPoint!).Port&&row.ProcessIdentity?.CreationWindowsFileTime100Ns==created);
    }
}
