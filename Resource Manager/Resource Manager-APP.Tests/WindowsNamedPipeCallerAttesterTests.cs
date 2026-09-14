using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using ResourceManager.App.Infrastructure.Adaptation.Transport;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsNamedPipeCallerAttesterTests
{
    [Fact]
    public async Task ConnectedPipeProjectsOsAttestedCallerIdentity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var pipeName = $"HostManager.Tests.Adapter.{Guid.NewGuid():N}";
        using var server = WindowsLocalPipeFactory.Create(
            pipeName,
            CurrentUserPipeSddl(),
            inputBufferSize: 4096,
            outputBufferSize: 4096);
        using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        var accepting = server.WaitForConnectionAsync();
        await client.ConnectAsync(5000);
        await accepting;
        await client.WriteAsync(new byte[] { 0x01 });
        await client.FlushAsync();
        Assert.Equal(0x01, server.ReadByte());

        var host = new HostManagerRuntimeIdentity();
        var caller = new WindowsNamedPipeCallerAttester().Attest(
            server,
            host.InstanceId,
            transportConnectionId: 51);
        using var current = Process.GetCurrentProcess();

        Assert.Equal(current.Id, caller.ProcessId);
        Assert.Equal(current.StartTime.ToUniversalTime().Ticks, caller.ProcessCreatedUtcTicks);
        Assert.Equal(current.SessionId, caller.WindowsSessionId);
        Assert.Equal(WindowsIdentity.GetCurrent().User!.Value, caller.WindowsSid);
        Assert.NotEqual(0UL, caller.AuthenticationIdLuid);
        Assert.NotEqual(0U, caller.IntegrityLevelRid);
        Assert.Equal(Path.GetFullPath(Environment.ProcessPath!), caller.CanonicalExecutablePath);
        Assert.NotEqual(default, caller.ExecutableFileIdentity);
    }

    [Fact]
    public void PipeFactoryEnforcesFirstInstance()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var pipeName = $"HostManager.Tests.Adapter.First.{Guid.NewGuid():N}";
        using var first = WindowsLocalPipeFactory.Create(
            pipeName,
            CurrentUserPipeSddl(),
            inputBufferSize: 4096,
            outputBufferSize: 4096);

        Assert.Throws<Win32Exception>(() =>
            WindowsLocalPipeFactory.Create(
                pipeName,
                CurrentUserPipeSddl(),
                inputBufferSize: 4096,
                outputBufferSize: 4096));
    }

    private static string CurrentUserPipeSddl()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The current Windows identity has no SID.");
        return $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;{sid})";
    }
}
