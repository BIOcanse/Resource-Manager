using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.App.Infrastructure.ServiceHosting;
using ResourceManager.Shared.Runtime;

namespace Resource_Manager_APP.Tests;

public sealed partial class LoopbackApiAccessTests
{
    [EnvironmentVariableFact("RESOURCE_MANAGER_FRONTEND_HANDSHAKE_TEST", "1")]
    public Task CompiledFrontendAcquiresCredentialAndCallsProtectedHttp() => RunFrontendHandshakeAsync("valid");
    [EnvironmentVariableFact("RESOURCE_MANAGER_FRONTEND_HANDSHAKE_TEST", "1")]
    public Task CompiledFrontendRejectsUnexpectedBackendBeforeHandshake() => RunFrontendHandshakeAsync("backend");
    [EnvironmentVariableFact("RESOURCE_MANAGER_FRONTEND_HANDSHAKE_TEST", "1")]
    public Task CredentialHostRejectsUnexpectedFrontendWithoutIssuingHttpAccess() => RunFrontendHandshakeAsync("frontend");

    private static async Task RunFrontendHandshakeAsync(string mode)
    {
        var probe = Environment.GetEnvironmentVariable("RM_FRONTEND_HANDSHAKE_PROBE");
        Assert.True(File.Exists(probe), "Build FrontendSessionCredentialProbe before enabling this gate.");
        var probePath = Path.GetFullPath(probe!);
        await using var fixture = await Fixture.StartAsync(actualSessionCredentials: true);
        using var identity = WindowsIdentity.GetCurrent();
        var name = "ResourceManager.Tests.FrontendHandshake." + Guid.NewGuid().ToString("N");
        using var server = WindowsLocalPipeFactory.Create(name,
            $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;{identity.User!.Value})", 4096, 4096);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var accepting = server.WaitForConnectionAsync(deadline.Token);
        using var child = Process.Start(new ProcessStartInfo(probePath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { name, mode == "backend" ? probePath : Environment.ProcessPath!,
                fixture.Client.BaseAddress!.AbsoluteUri.TrimEnd('/'), Scope, LoopbackApiAccessToken.HeaderName }
        })!;
        var errors = child.StandardError.ReadToEndAsync();
        try
        {
            await accepting;
            using var host = new FrontendSessionCredentialHostedService(new UnusedHandshakeSessions(), fixture.Tokens,
                fixture.App.Services.GetRequiredService<HostManagerRuntimeIdentity>(),
                NullLogger<FrontendSessionCredentialHostedService>.Instance);
            if (mode == "backend")
                Assert.Equal(0, await server.ReadAsync(new byte[1], deadline.Token));
            else if (mode == "frontend")
            {
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.RespondAsync(server,
                    child.SessionId, identity.User.Value, Environment.ProcessPath!, deadline.Token));
                server.Disconnect();
            }
            else
                await host.ServeConnectionAsync(server, child.SessionId, identity.User.Value, probePath,
                    Environment.ProcessPath!, deadline.Token);

            Assert.Equal(mode == "valid" ? "200" : "rejected",
                await child.StandardOutput.ReadLineAsync(deadline.Token));
            Assert.Equal(mode == "valid" ? 1 : 0, fixture.Effects.ValidationCalls);
            Assert.Equal(mode == "valid" ? 1 : 0, fixture.HttpRequests);
            Assert.Equal(0, fixture.Administrator.Calls);
            Assert.Null(fixture.LastRequestError);
            if (mode == "valid") { await child.StandardInput.WriteLineAsync("exit"); await child.StandardInput.FlushAsync(); }
            await child.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, child.ExitCode);
            Assert.Equal(string.Empty, await errors);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.StandardInput.Close();
                try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { child.Kill(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            }
        }
    }

    private sealed class UnusedHandshakeSessions : IInteractiveUserSessionBroker
    {
        public IReadOnlyList<uint> GetActiveSessionIds() => throw new NotSupportedException();
        public NativeUiProcessPresence GetNativeUiProcessPresence(uint sessionId, string path) => throw new NotSupportedException();
        public uint LaunchNativeUi(uint sessionId, string path, string args) => throw new NotSupportedException();
    }
}
