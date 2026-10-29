using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.TrustedAccess;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.TrustedAccess;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

public sealed partial class GpuLaunchSessionTests
{
    [Fact]
    public async Task GenericInvocationRequiresControlPurposeAndOperationScope()
    {
        var fixture = TrustedAccessTestFactory.Create();
        var services = new LaunchServices();
        using var host = CreateHost(fixture.Owner, services);

        var defaultKey = fixture.KeyFile.Value!;
        var resolved = await InvokeAsync(host, "gpu.resolve", defaultKey, @"D:\owned.exe");
        Assert.Equal(TrustedAccessInvocationContract.Success, resolved.Status);
        Assert.Contains("status=probe-resolved", resolved.Payload ?? string.Empty);
        Assert.Equal(1, services.ResolveCalls);

        var reportOnly = await fixture.Owner.CreateAsync(new AccessKeyCreateRequest(
            "report only", AccessKeyPurposes.Control, [AccessKeyScopes.GpuReport]), CancellationToken.None);
        var deniedResolve = await InvokeAsync(host, "gpu.resolve", reportOnly.ApiKey, @"D:\denied.exe");
        Assert.Equal(TrustedAccessInvocationContract.Forbidden, deniedResolve.Status);
        Assert.Equal(1, services.ResolveCalls);

        var resolveOnly = await fixture.Owner.CreateAsync(new AccessKeyCreateRequest(
            "resolve only", AccessKeyPurposes.Control, [AccessKeyScopes.GpuResolve]), CancellationToken.None);
        var deniedReport = await InvokeAsync(host, "gpu.report", resolveOnly.ApiKey,
            "executablePath=D:\\denied.exe\noutcome=launch-failed");
        Assert.Equal(TrustedAccessInvocationContract.Forbidden, deniedReport.Status);
        Assert.Equal(0, services.ReportCalls);

        var otherPurpose = await fixture.Owner.CreateAsync(new AccessKeyCreateRequest(
            "AI key", AccessKeyPurposes.OpenAi, [AccessKeyScopes.GpuResolve]), CancellationToken.None);
        var wrongPurpose = await InvokeAsync(host, "gpu.resolve", otherPurpose.ApiKey, @"D:\denied.exe");
        Assert.Equal(TrustedAccessInvocationContract.Unauthorized, wrongPurpose.Status);
        Assert.Equal(1, services.ResolveCalls);
    }

    [Fact]
    public async Task GenericInvocationUsesKeylessAdministratorBypassOnlyForVerifiedAdministrator()
    {
        Assert.True(WindowsLoopbackApiAdministratorReader.TryReadProcessIdentity(Environment.ProcessId, out var current));
        var fixture = TrustedAccessTestFactory.Create();
        var keys = new CountingKeyService(fixture.Owner);
        var services = new LaunchServices();
        using var host = CreateHost(keys, services);

        var emptyKey = await InvokeAsync(host, "gpu.resolve", string.Empty, @"D:\keyless.exe");
        var isAdministrator = current.Caller.IsAdministrator;
        Assert.Equal(isAdministrator ? TrustedAccessInvocationContract.Success : TrustedAccessInvocationContract.Unauthorized,
            emptyKey.Status);
        Assert.Equal(0, keys.AuthenticateCalls);
        Assert.Equal(0, keys.RevocationCalls);
        Assert.Equal(isAdministrator ? 1 : 0, services.ResolveCalls);

        var invalidKey = await InvokeAsync(host, "gpu.resolve",
            "rm_sk_" + new string('0', 64),
            @"D:\invalid-key.exe");
        Assert.Equal(TrustedAccessInvocationContract.Unauthorized, invalidKey.Status);
        Assert.Equal(1, keys.AuthenticateCalls);
        Assert.Equal(0, keys.RevocationCalls);
        Assert.Equal(isAdministrator ? 1 : 0, services.ResolveCalls);
    }

    [Fact]
    public async Task GenericInvocationRechecksKeyRevocationBeforeReporting()
    {
        var fixture = TrustedAccessTestFactory.Create();
        var key = await fixture.Owner.CreateAsync(new AccessKeyCreateRequest(
            "revocable", AccessKeyPurposes.Control, [AccessKeyScopes.GpuResolve]), CancellationToken.None);
        var services = new LaunchServices { ResolveWaitForCancellation = true };
        using var host = CreateHost(fixture.Owner, services);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var invocation = InvokeAsync(host, "gpu.resolve", key.ApiKey, @"D:\revoked.exe", deadline.Token);
        await services.ResolveStarted.Task.WaitAsync(deadline.Token);
        Assert.True(await fixture.Owner.RevokeAsync(key.Credential.Id, deadline.Token));
        var response = await invocation.WaitAsync(deadline.Token);

        Assert.Equal(TrustedAccessInvocationContract.Unauthorized, response.Status);
        Assert.Equal(0, services.ReportCalls);
    }

    [Fact]
    public async Task GenericInvocationRejectsUnknownOperationsAndInvalidReportsWithoutCallingOwners()
    {
        var fixture = TrustedAccessTestFactory.Create();
        var services = new LaunchServices();
        using var host = CreateHost(fixture.Owner, services);

        var unknown = await InvokeAsync(host, "frontend.issue", fixture.KeyFile.Value!, "ignored");
        var invalidReport = await InvokeAsync(host, "gpu.report", fixture.KeyFile.Value!, "not a report");

        Assert.Equal(TrustedAccessInvocationContract.InvalidRequest, unknown.Status);
        Assert.Equal(TrustedAccessInvocationContract.InvalidRequest, invalidReport.Status);
        Assert.Equal(0, services.ResolveCalls);
        Assert.Equal(0, services.ReportCalls);
    }

    [Fact]
    public async Task GenericInvocationHoldsTheResponseUntilTheClientCloses()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var fixture = TrustedAccessTestFactory.Create();
        var services = new LaunchServices();
        using var host = CreateHost(fixture.Owner, services);
        var name = "ResourceManager.Tests.TrustedAccessInvocation." + Guid.NewGuid().ToString("N");
        using var server = WindowsLocalPipeFactory.Create(name,
            $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;{identity.User!.Value})",
            TrustedAccessInvocationContract.MaximumFrameBytes,
            TrustedAccessInvocationContract.MaximumFrameBytes);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation);
        var accepting = server.WaitForConnectionAsync(deadline.Token);
        await client.ConnectAsync(deadline.Token);
        await accepting;
        var serving = host.ServeConnectionAsync(server, deadline.Token);
        await TrustedAccessInvocationContract.WriteRequestAsync(client,
            new("gpu.resolve", fixture.KeyFile.Value!, @"D:\owned-target.exe"), deadline.Token);
        var response = await TrustedAccessInvocationContract.ReadResponseAsync(client, deadline.Token);
        Assert.Equal(TrustedAccessInvocationContract.Success, response.Status);
        await Task.Delay(100, deadline.Token);
        Assert.False(serving.IsCompleted, "The server must keep the connection until the caller closes it.");
        client.Dispose();
        await serving.WaitAsync(deadline.Token);
        Assert.Equal(1, services.ResolveCalls);
    }

    [EnvironmentVariableFact("RESOURCE_MANAGER_GPU_LAUNCH_SESSION_TEST", "1")]
    public Task NativeBrokerUsesDefaultKeyFileForResolveAndReport() => RunNativeAsync("valid");

    [EnvironmentVariableFact("RESOURCE_MANAGER_GPU_LAUNCH_SESSION_TEST", "1")]
    public Task NativeBrokerRejectsAChangedBackendGenerationBeforeReporting() => RunNativeAsync("generation");

    [EnvironmentVariableFact("RESOURCE_MANAGER_GPU_LAUNCH_SESSION_TEST", "1")]
    public Task NativeBrokerRejectsAChangedBackendImageBeforeReadingTheKey() => RunNativeAsync("backend");

    [EnvironmentVariableFact("RESOURCE_MANAGER_GPU_LAUNCH_SESSION_TEST", "1")]
    public Task NativeBrokerContinuesToUseTheBearerKeyAcrossWindowsCallerFacts() => RunNativeAsync("broker");

    [EnvironmentVariableFact("RESOURCE_MANAGER_GPU_LAUNCH_SESSION_TEST", "1")]
    public Task NativeBrokerContinuesToUseTheBearerKeyAcrossDifferentSidFacts() => RunNativeAsync("sid");

    [EnvironmentVariableFact("RESOURCE_MANAGER_GPU_LAUNCH_SESSION_TEST", "1")]
    public Task NativeBrokerContinuesToUseTheBearerKeyAcrossDifferentSessionFacts() => RunNativeAsync("session");

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
        var fixture = TrustedAccessTestFactory.Create();
        using var keyPackage = NativeTestKeyPackage.Create(fixture.KeyFile.Value!);
        var services = new LaunchServices { FailFirstResolve = mode == "corrupt" };
        using var host = CreateHost(fixture.Owner, services);
        var name = "ResourceManager.Tests.GpuLaunchSession." + Guid.NewGuid().ToString("N");
        using var pipe = WindowsLocalPipeFactory.Create(name,
            $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;{identity.User!.Value})",
            TrustedAccessInvocationContract.MaximumFrameBytes,
            TrustedAccessInvocationContract.MaximumFrameBytes);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var target = Path.Combine(Path.GetTempPath(), "owned-gpu-launch-target-" + Guid.NewGuid().ToString("N") + ".exe");
        var connections = mode switch
        {
            "valid" or "broker" or "sid" or "session" => 2,
            "corrupt" or "frame" => 3,
            _ => 1
        };
        var serving = ServeAsync();
        using var child = Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { name, mode == "backend" ? path : Environment.ProcessPath!, target, keyPackage.Root, mode },
            Environment = { ["RESOURCE_MANAGER_PACKAGE_ROOT"] = keyPackage.Root }
        })!;
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        try
        {
            await child.WaitForExitAsync(deadline.Token);
            await serving;
            Assert.Equal(0, child.ExitCode);
            Assert.Equal(string.Empty, await errors);
            Assert.Equal(mode switch
            {
                "valid" or "broker" or "sid" or "session" or "corrupt" or "frame" => "resolved-and-reported",
                "generation" => "resolved-report-rejected",
                _ => "rejected"
            }, (await output).Trim());
            var accepted = mode is "valid" or "broker" or "sid" or "session" or "generation" or "corrupt" or "frame";
            Assert.Equal(mode == "corrupt" ? 2 : accepted ? 1 : 0, services.ResolveCalls);
            Assert.Equal(mode is "valid" or "broker" or "sid" or "session" or "corrupt" or "frame" ? 1 : 0,
                services.ReportCalls);
            if (accepted) Assert.Equal(target, services.ExecutablePath);
            if (services.ReportCalls > 0)
            {
                Assert.Equal(child.Id, services.Report!.ProcessId);
                Assert.Equal(target, services.Report.ExecutablePath);
                Assert.Equal("fixture-software", services.Report.SoftwareId);
                Assert.Equal(GpuLaunchExecutionOutcomes.PassThroughStarted, services.Report.Outcome);
            }
        }
        finally
        {
            deadline.Cancel();
            if (!child.HasExited)
            {
                child.Kill();
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            try { await serving; } catch (OperationCanceledException) { }
        }

        async Task ServeAsync()
        {
            for (var index = 0; index < connections; index++)
            {
                await pipe.WaitForConnectionAsync(deadline.Token);
                try
                {
                    if (mode is "timeout" or "reply")
                    {
                        _ = await TrustedAccessInvocationContract.ReadRequestAsync(pipe, deadline.Token);
                        if (mode == "timeout") await Task.Delay(2500, deadline.Token);
                        else await pipe.WriteAsync(BitConverter.GetBytes(TrustedAccessInvocationContract.MaximumFrameBytes + 1), deadline.Token);
                        continue;
                    }
                    await host.ServeConnectionAsync(pipe, deadline.Token);
                }
                finally { if (pipe.IsConnected) pipe.Disconnect(); }
            }
        }
    }

    private static TrustedAccessInvocationHostedService CreateHost(
        ITrustedAccessKeyService keys,
        LaunchServices services)
        => new(keys, services, services, new HostManagerRuntimeIdentity(),
            NullLogger<TrustedAccessInvocationHostedService>.Instance);

    private static async Task<TrustedAccessInvocationResponse> InvokeAsync(
        TrustedAccessInvocationHostedService host,
        string operation,
        string key,
        string payload,
        CancellationToken cancellationToken = default)
    {
        var name = "ResourceManager.Tests.TrustedAccessInvocation." + Guid.NewGuid().ToString("N");
        using var identity = WindowsIdentity.GetCurrent();
        using var server = WindowsLocalPipeFactory.Create(name,
            $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;{identity.User!.Value})",
            TrustedAccessInvocationContract.MaximumFrameBytes,
            TrustedAccessInvocationContract.MaximumFrameBytes);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var accepting = server.WaitForConnectionAsync(deadline.Token);
        await client.ConnectAsync(deadline.Token);
        await accepting;
        var serving = host.ServeConnectionAsync(server, deadline.Token);
        try
        {
            await TrustedAccessInvocationContract.WriteRequestAsync(client,
                new(operation, key, payload), deadline.Token);
            return await TrustedAccessInvocationContract.ReadResponseAsync(client, deadline.Token);
        }
        finally
        {
            client.Dispose();
            await serving.WaitAsync(deadline.Token);
        }
    }

    private sealed class LaunchServices : IGpuStartupPlacementResolver, IGpuLaunchExecutionReportStore
    {
        internal int ResolveCalls;
        internal int ReportCalls;
        internal bool FailFirstResolve;
        internal bool ResolveWaitForCancellation;
        internal string? ExecutablePath;
        internal GpuLaunchExecutionReport? Report;
        internal TaskCompletionSource ResolveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<GpuStartupPlacementDecision> ResolveAsync(
            GpuStartupPlacementRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ResolveCalls);
            ExecutablePath = request.ExecutablePath;
            if (FailFirstResolve && ResolveCalls == 1)
                throw new System.Text.Json.JsonException("Corrupt startup policy fixture.");
            if (ResolveWaitForCancellation)
            {
                ResolveStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return new GpuStartupPlacementDecision(GpuStartupPlacementDecisionKinds.PassThrough,
                "probe-resolved", "owned resolver", request.ExecutablePath, "fixture-software", "fixture-process",
                GpuPlacementTargets.IntegratedGpu, null, null, null, null, []);
        }

        public Task<GpuLaunchExecutionReport> RecordAsync(GpuLaunchExecutionReport report, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ReportCalls);
            Report = report;
            return Task.FromResult(report);
        }

        public Task<IReadOnlyList<GpuLaunchExecutionReport>> GetLatestAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class CountingKeyService(ITrustedAccessKeyService inner) : ITrustedAccessKeyService
    {
        internal int AuthenticateCalls;
        internal int RevocationCalls;

        public Task<IReadOnlyList<AccessKeySummary>> ListAsync(CancellationToken cancellationToken)
            => inner.ListAsync(cancellationToken);

        public Task<AccessKeyCreated> CreateAsync(AccessKeyCreateRequest request, CancellationToken cancellationToken)
            => inner.CreateAsync(request, cancellationToken);

        public Task<AccessKeyCreated> RotateAsync(string credentialId, CancellationToken cancellationToken)
            => inner.RotateAsync(credentialId, cancellationToken);

        public Task<bool> RevokeAsync(string credentialId, CancellationToken cancellationToken)
            => inner.RevokeAsync(credentialId, cancellationToken);

        public Task<AccessKeyCreated?> SetDefaultAsync(bool enabled, CancellationToken cancellationToken)
            => inner.SetDefaultAsync(enabled, cancellationToken);

        public Task<AccessKeySummary?> AuthenticateAsync(
            string purpose,
            string? apiKey,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref AuthenticateCalls);
            return inner.AuthenticateAsync(purpose, apiKey, cancellationToken);
        }

        public Task InitializeAsync(bool recover, CancellationToken cancellationToken)
            => inner.InitializeAsync(recover, cancellationToken);

        public ValueTask<bool> ValidateCredentialAsync(string credentialId, CancellationToken cancellationToken)
            => inner.ValidateCredentialAsync(credentialId, cancellationToken);

        public CancellationToken GetRevocationToken(string credentialId)
        {
            Interlocked.Increment(ref RevocationCalls);
            return inner.GetRevocationToken(credentialId);
        }
    }

    internal sealed class NativeTestKeyPackage : IDisposable
    {
        private NativeTestKeyPackage(string root) => Root = root;
        internal string Root { get; }

        internal static NativeTestKeyPackage Create(string key)
        {
            var root = Path.Combine(Path.GetTempPath(), "rm-gpu-invocation-package-" + Guid.NewGuid().ToString("N"));
            try
            {
                TrustedAccessKeyFile.WriteAtomic(root, key);
                Assert.Equal(key, TrustedAccessKeyFile.Read(root));
                return new(root);
            }
            catch
            {
                Delete(root);
                throw;
            }
        }

        public void Dispose() => Delete(Root);

        private static void Delete(string root)
        {
            var key = TrustedAccessKeyFile.PathForPackage(root);
            if (File.Exists(key)) File.Delete(key);
            var security = Path.GetDirectoryName(key)!;
            if (Directory.Exists(security)) Directory.Delete(security);
            var userData = Directory.GetParent(security)?.FullName;
            if (userData is not null && Directory.Exists(userData)) Directory.Delete(userData);
            if (Directory.Exists(root)) Directory.Delete(root);
        }
    }
}
