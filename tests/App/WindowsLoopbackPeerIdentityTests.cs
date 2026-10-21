using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ResourceManager.App.Infrastructure.Security;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsLoopbackPeerIdentityTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("127.0.0.1", true)]
    public async Task ActualConnectionIdentifiesServerChildRatherThanTheLocalClient(string address, bool dualStack = false)
    {
        var image = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var command = "$ErrorActionPreference='Stop'; $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Parse('"
            + (dualStack ? "::" : address) + "'),0); " + (dualStack ? "$listener.Server.DualMode=$true; " : "")
            + "$listener.Start(); [Console]::Out.WriteLine($listener.LocalEndpoint.Port); "
            + "$client=$listener.AcceptTcpClient(); [Console]::Out.WriteLine('connected'); "
            + "try { $null=[Console]::In.ReadLine() } finally { $client.Dispose(); $listener.Stop() }";
        var start = new ProcessStartInfo(image)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) }
        };
        using var child = Process.Start(start)!;
        _ = child.SafeHandle;
        var errors = child.StandardError.ReadToEndAsync();
        try
        {
            var port = int.Parse((await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)))!);
            var ip = IPAddress.Parse(address);
            using var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(ip, port).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("connected", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(WindowsLoopbackApiAdministratorReader.TryReadConnectedPeer(socket, out var peer));
            Assert.Equal(child.Id, peer.Caller.ProcessId);
            Assert.NotEqual(Environment.ProcessId, peer.Caller.ProcessId);
            Assert.Equal(child.StartTime.ToUniversalTime().Ticks, peer.Caller.ProcessCreatedUtcTicks);
            Assert.Equal(image, peer.ExecutablePath, ignoreCase: true);
        }
        finally
        {
            await child.StandardInput.WriteLineAsync("finish");
            child.StandardInput.Close();
            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException) { child.Kill(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        }
        Assert.Equal(0, child.ExitCode);
        Assert.Equal("", await errors);
    }
}
