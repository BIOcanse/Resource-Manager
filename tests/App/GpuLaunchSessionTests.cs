using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.App.Infrastructure.ServiceHosting;
using ResourceManager.Shared.Runtime;

namespace Resource_Manager_APP.Tests;

public sealed partial class GpuLaunchSessionTests
{
    [EnvironmentVariableFact("RESOURCE_MANAGER_GPU_LAUNCH_SESSION_TEST", "1")]
    public Task NativeBrokerResolvesAndReportsWithoutADiskToken() => RunNativeAsync("valid");
    [EnvironmentVariableFact("RESOURCE_MANAGER_GPU_LAUNCH_SESSION_TEST", "1")]
    public Task NativeBrokerRejectsUnexpectedBackendBeforeSending() => RunNativeAsync("backend");
    [EnvironmentVariableFact("RESOURCE_MANAGER_GPU_LAUNCH_SESSION_TEST", "1")]
    public Task NativeBrokerRoleRejectsUnexpectedImage() => RunNativeAsync("broker");
    [EnvironmentVariableFact("RESOURCE_MANAGER_GPU_LAUNCH_SESSION_TEST", "1")]
    public Task NativeBrokerRoleRejectsUnexpectedSid() => RunNativeAsync("sid");
    [EnvironmentVariableFact("RESOURCE_MANAGER_GPU_LAUNCH_SESSION_TEST", "1")]
    public Task NativeBrokerRoleRejectsUnexpectedSession() => RunNativeAsync("session");
    [EnvironmentVariableFact("RESOURCE_MANAGER_GPU_LAUNCH_SESSION_TEST", "1")]
    public Task NativeBrokerReportRequiresTheResolvedBackendGeneration() => RunNativeAsync("generation");
    [EnvironmentVariableFact("RESOURCE_MANAGER_GPU_LAUNCH_SESSION_TEST", "1")]
    public Task NativeBrokerCannotRequestAFrontendCredential() => RunNativeAsync("frontend");
    [EnvironmentVariableFact("RESOURCE_MANAGER_GPU_LAUNCH_SESSION_TEST", "1")]
    public Task SessionHostRejectsUnknownOperationBeforeAnyHandler() => RunNativeAsync("operation");
    [EnvironmentVariableFact("RESOURCE_MANAGER_GPU_LAUNCH_SESSION_TEST", "1")]
    public Task NativeBrokerFallsBackWhenTheBackendDoesNotReply() => RunNativeAsync("timeout");
    [EnvironmentVariableFact("RESOURCE_MANAGER_GPU_LAUNCH_SESSION_TEST", "1")]
    public Task NativeBrokerRejectsAnOversizedReply() => RunNativeAsync("reply");
    [EnvironmentVariableFact("RESOURCE_MANAGER_GPU_LAUNCH_SESSION_TEST", "1")]
    public Task CorruptPolicyRejectsOneRequestAndTheSameSessionServesTheNext() => RunNativeAsync("corrupt");
    [EnvironmentVariableFact("RESOURCE_MANAGER_GPU_LAUNCH_SESSION_TEST", "1")]
    public Task MalformedUtf8RejectsOneRequestAndTheSameSessionServesTheNext() => RunNativeAsync("frame");

    private static async Task RunNativeAsync(string mode)
    {
        var probe = Environment.GetEnvironmentVariable("RM_GPU_LAUNCH_SESSION_PROBE");
        Assert.True(File.Exists(probe), "Build GpuLaunchSessionProbe.cpp before enabling this gate.");
        var path = Path.GetFullPath(probe!);
        using var identity = WindowsIdentity.GetCurrent();
        using var current = Process.GetCurrentProcess();
        var spy = new LaunchServices { FailFirstResolve = mode == "corrupt" };
        using var provider = new ServiceCollection().AddSingleton<IGpuStartupPlacementResolver>(spy)
            .AddSingleton<IGpuLaunchExecutionReportStore>(spy).BuildServiceProvider();
        // These two scoped operations must never issue a frontend's all-API credential.
        var tokens = new LoopbackApiAccessToken(_ => throw new InvalidOperationException("Unexpected credential issuance."), (_, _) => false);
        using var host = new FrontendSessionCredentialHostedService(new UnusedSessions(), tokens,
            new HostManagerRuntimeIdentity(), NullLogger<FrontendSessionCredentialHostedService>.Instance, provider);
        var name = "ResourceManager.Tests.GpuLaunchSession." + Guid.NewGuid().ToString("N");
        using var pipe = WindowsLocalPipeFactory.Create(name,
            $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;{identity.User!.Value})", 4096, 4096);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var target = Path.Combine(Path.GetTempPath(), "owned-gpu-launch-target-" + Guid.NewGuid().ToString("N") + ".exe");
        var serving = ServeAsync();
        using var child = Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { name, mode == "backend" ? path : Environment.ProcessPath!, target, Path.GetTempPath(), mode }
        })!;
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        try
        {
            await child.WaitForExitAsync(deadline.Token);
            await serving;
            Assert.Equal(0, child.ExitCode);
            Assert.Equal(string.Empty, await errors);
            Assert.Equal(mode switch { "valid" or "corrupt" or "frame" => "resolved-and-reported", "generation" => "resolved-report-rejected", _ => "rejected" },
                (await output).Trim());
            var accepted = mode is "valid" or "generation" or "corrupt" or "frame";
            Assert.Equal(mode == "corrupt" ? 2 : accepted ? 1 : 0, spy.ResolveCalls);
            Assert.Equal(mode is "valid" or "corrupt" or "frame" ? 1 : 0, spy.ReportCalls);
            if (accepted) Assert.Equal(target, spy.ExecutablePath);
            if (mode is "valid" or "corrupt" or "frame")
            {
                Assert.Equal(child.Id, spy.Report!.ProcessId);
                Assert.Equal(target, spy.Report.ExecutablePath);
                Assert.Equal("fixture-software", spy.Report.SoftwareId);
                Assert.Equal(GpuLaunchExecutionOutcomes.PassThroughStarted, spy.Report.Outcome);
            }
        }
        finally
        {
            deadline.Cancel();
            if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            try { await serving; } catch (OperationCanceledException) { }
        }

        async Task ServeAsync()
        {
            for (var index = 0; index < (mode is "corrupt" or "frame" ? 3 : mode is "valid" or "generation" ? 2 : 1); index++)
            {
                await pipe.WaitForConnectionAsync(deadline.Token);
                try
                {
                    if (mode is "timeout" or "reply")
                    {
                        var operation = new byte[1];
                        await pipe.ReadExactlyAsync(operation, deadline.Token);
                        Assert.Equal(GpuLaunchSessionContract.ResolveOperation, operation[0]);
                        Assert.Equal(target, await GpuLaunchSessionContract.ReadAsync(pipe, deadline.Token));
                        if (mode == "timeout") await Task.Delay(2500, deadline.Token);
                        else await pipe.WriteAsync(BitConverter.GetBytes(GpuLaunchSessionContract.MaximumPayloadBytes + 1), deadline.Token);
                        continue;
                    }
                    await host.ServeConnectionAsync(pipe, mode == "session" ? current.SessionId + 1 : current.SessionId,
                        mode == "sid" ? "S-1-0-0" : identity.User!.Value, Environment.ProcessPath!,
                        mode == "broker" ? Environment.ProcessPath! : path, deadline.Token);
                }
                finally { if (pipe.IsConnected) pipe.Disconnect(); }
            }
        }
    }

    [Fact]
    public async Task SessionKeepsTheReplyUntilTheClientReadsAndCloses()
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var current = Process.GetCurrentProcess();
        var spy = new LaunchServices();
        using var provider = new ServiceCollection().AddSingleton<IGpuStartupPlacementResolver>(spy)
            .AddSingleton<IGpuLaunchExecutionReportStore>(spy).BuildServiceProvider();
        var tokens = new LoopbackApiAccessToken(_ => throw new InvalidOperationException("Unexpected credential issuance."), (_, _) => false);
        using var host = new FrontendSessionCredentialHostedService(new UnusedSessions(), tokens,
            new HostManagerRuntimeIdentity(), NullLogger<FrontendSessionCredentialHostedService>.Instance, provider);
        var name = "ResourceManager.Tests.GpuLaunchRead." + Guid.NewGuid().ToString("N");
        using var server = WindowsLocalPipeFactory.Create(name,
            $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;{identity.User!.Value})", 4096, 4096);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation);
        var accepting = server.WaitForConnectionAsync(deadline.Token);
        await client.ConnectAsync(deadline.Token);
        await accepting;
        var serving = host.ServeConnectionAsync(server, current.SessionId, identity.User.Value,
            Environment.ProcessPath!, Environment.ProcessPath!, deadline.Token);
        try
        {
            await client.WriteAsync(new byte[] { GpuLaunchSessionContract.ResolveOperation }, deadline.Token);
            await GpuLaunchSessionContract.WriteAsync(client, @"D:\owned-target.exe", deadline.Token);
            await Task.Delay(200, deadline.Token); // Deliberately leave the complete response unread.
            Assert.False(serving.IsCompleted, "The server disconnected before the client consumed its response.");
            Assert.Contains("status=probe-resolved", await GpuLaunchSessionContract.ReadAsync(client, deadline.Token));
            Assert.Equal(1, spy.ResolveCalls);
        }
        finally
        {
            client.Dispose();
            await serving;
        }
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(65537)]
    public async Task FrameRejectsInvalidLengthBeforeBodyRead(int length)
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(length));
        await Assert.ThrowsAsync<InvalidDataException>(() => GpuLaunchSessionContract.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task FramePreservesUnicodeAndRejectsInvalidUtf8()
    {
        const string text = "D:\\软件\\台式机.exe";
        using var stream = new MemoryStream();
        await GpuLaunchSessionContract.WriteAsync(stream, text, CancellationToken.None);
        stream.Position = 0;
        Assert.Equal(text, await GpuLaunchSessionContract.ReadAsync(stream, CancellationToken.None));
        using var invalid = new MemoryStream([1, 0, 0, 0, 255]);
        await Assert.ThrowsAsync<InvalidDataException>(() => GpuLaunchSessionContract.ReadAsync(invalid, CancellationToken.None));
    }

    private sealed class LaunchServices : IGpuStartupPlacementResolver, IGpuLaunchExecutionReportStore
    {
        internal int ResolveCalls, ReportCalls;
        internal bool FailFirstResolve;
        internal string? ExecutablePath;
        internal GpuLaunchExecutionReport? Report;
        public Task<GpuStartupPlacementDecision> ResolveAsync(GpuStartupPlacementRequest request, CancellationToken cancellationToken)
        {
            ResolveCalls++; ExecutablePath = request.ExecutablePath;
            if (FailFirstResolve && ResolveCalls == 1) throw new System.Text.Json.JsonException("Corrupt startup policy fixture.");
            return Task.FromResult(new GpuStartupPlacementDecision(GpuStartupPlacementDecisionKinds.PassThrough,
                "probe-resolved", "owned resolver", request.ExecutablePath, "fixture-software", "fixture-process",
                "IntegratedGpu", null, null, null, null, []));
        }
        public Task<GpuLaunchExecutionReport> RecordAsync(GpuLaunchExecutionReport report, CancellationToken cancellationToken)
        { ReportCalls++; Report = report; return Task.FromResult(report); }
        public Task<IReadOnlyList<GpuLaunchExecutionReport>> GetLatestAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
    private sealed class UnusedSessions : IInteractiveUserSessionBroker
    {
        public IReadOnlyList<uint> GetActiveSessionIds() => throw new NotSupportedException();
        public NativeUiProcessPresence GetNativeUiProcessPresence(uint sessionId, string path) => throw new NotSupportedException();
        public uint LaunchNativeUi(uint sessionId, string path, string args) => throw new NotSupportedException();
    }
}
