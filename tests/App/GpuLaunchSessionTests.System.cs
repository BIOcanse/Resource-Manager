using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.App.Infrastructure.ServiceHosting;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed partial class GpuLaunchSessionTests
{
    private readonly ITestOutputHelper systemOutput;
    public GpuLaunchSessionTests(ITestOutputHelper output) => systemOutput = output;

    [EnvironmentVariableFact("RESOURCE_MANAGER_SYSTEM_GPU_SESSION_TEST", "1")]
    public Task StandardUserNativeBrokerResolvesAndReportsToSystemSession()
        => RunSystemNativeAsync("valid");

    [EnvironmentVariableFact("RESOURCE_MANAGER_SYSTEM_GPU_SESSION_TEST", "1")]
    public Task StandardUserNativeBrokerRejectsChangedSystemBackendCreationIdentity()
        => RunSystemNativeAsync("generation");

    private async Task RunSystemNativeAsync(string mode)
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var current = Process.GetCurrentProcess();
        Assert.True(identity.IsSystem, "Use only the finite owned SYSTEM test helper.");
        Assert.Equal(0, current.SessionId);
        WindowsBackendProcessIdentityAccess.InitializeForCurrentSystemProcess();
        var session = uint.Parse(Environment.GetEnvironmentVariable("RM_SYSTEM_TARGET_SESSION")!);
        var sid = new SecurityIdentifier(Environment.GetEnvironmentVariable("RM_SYSTEM_TARGET_SID")!).Value;
        Assert.NotEqual(identity.User!.Value, sid);
        var launcher = new WindowsInteractiveUserSessionBroker();
        Assert.Contains(session, launcher.GetActiveSessionIds());
        Assert.Equal(sid, launcher.GetUserSid(session));
        var path = Environment.GetEnvironmentVariable("RM_SYSTEM_GPU_SESSION_PROBE")!;
        Assert.True(File.Exists(path), "Build the GUI-subsystem GpuLaunchSessionProbe first.");
        path = Path.GetFullPath(path);
        var spy = new LaunchServices();
        using var provider = new ServiceCollection().AddSingleton<IGpuStartupPlacementResolver>(spy)
            .AddSingleton<IGpuLaunchExecutionReportStore>(spy).BuildServiceProvider();
        var tokens = new LoopbackApiAccessToken(_ => throw new InvalidOperationException("No frontend credential may be issued."),
            (_, _) => false);
        using var host = new FrontendSessionCredentialHostedService(new UnusedSessions(), tokens,
            new HostManagerRuntimeIdentity(), new SystemGpuLogger(systemOutput), provider);
        var name = "ResourceManager.Tests.SystemGpuLaunch." + Guid.NewGuid().ToString("N");
        using var pipe = WindowsLocalPipeFactory.Create(name, $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;{sid})", 4096, 4096);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var accepting = pipe.WaitForConnectionAsync(deadline.Token);
        var target = Path.Combine(Path.GetTempPath(), "owned-system-gpu-target-" + Guid.NewGuid().ToString("N") + ".exe");
        var arguments = string.Join(" ", new[] { name, Environment.ProcessPath!, target, Path.GetDirectoryName(path)!, mode }
            .Select(value => $"\"{value}\""));
        // Do not answer until the owned child's handle and token are captured;
        // the client cannot finish while waiting for its first response.
        using var child = Process.GetProcessById(checked((int)launcher.LaunchNativeUi(session, path, arguments)));
        _ = child.SafeHandle;
        try
        {
            Assert.True(WindowsLoopbackApiAdministratorReader.TryReadProcessIdentity(child.Id, out var caller));
            Assert.Equal(sid, caller.Caller.WindowsSid);
            Assert.Equal(checked((int)session), caller.Caller.WindowsSessionId);
            Assert.False(caller.Caller.IsAdministrator);
            Assert.Equal(path, caller.ExecutablePath, ignoreCase: true);
            await Task.WhenAny(accepting, child.WaitForExitAsync(deadline.Token)).WaitAsync(deadline.Token);
            Assert.False(child.HasExited, child.HasExited ? $"Native client exited before its first connection: {child.ExitCode}" : null);
            await accepting;
            await host.ServeConnectionAsync(pipe, checked((int)session), sid,
                Environment.ProcessPath!, path, deadline.Token);
            systemOutput.WriteLine($"First request: resolves={spy.ResolveCalls}; exited={child.HasExited}.");
            var next = pipe.WaitForConnectionAsync(deadline.Token);
            await Task.WhenAny(next, child.WaitForExitAsync(deadline.Token)).WaitAsync(deadline.Token);
            Assert.False(child.HasExited, child.HasExited ? $"Native client exited before report connection: {child.ExitCode}" : null);
            await next;
            await host.ServeConnectionAsync(pipe, checked((int)session), sid,
                Environment.ProcessPath!, path, deadline.Token);
            await child.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, child.ExitCode);
            Assert.Equal(1, spy.ResolveCalls);
            Assert.Equal(target, spy.ExecutablePath);
            Assert.Equal(mode == "valid" ? 1 : 0, spy.ReportCalls);
            if (mode == "valid")
            {
                Assert.Equal(child.Id, spy.Report!.ProcessId);
                Assert.Equal(target, spy.Report.ExecutablePath);
                Assert.Equal("fixture-software", spy.Report.SoftwareId);
                Assert.Equal(GpuLaunchExecutionOutcomes.PassThroughStarted, spy.Report.Outcome);
            }
            systemOutput.WriteLine($"SYSTEM backend session={current.SessionId}; standard native broker session={caller.Caller.WindowsSessionId}; "
                + $"mode={mode}; resolves={spy.ResolveCalls}; reports={spy.ReportCalls}; exit={child.ExitCode}.");
        }
        finally
        {
            deadline.Cancel();
            pipe.Dispose();
            if (!child.HasExited)
            {
                try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { child.Kill(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            }
        }
    }

    private sealed class SystemGpuLogger(ITestOutputHelper output) : ILogger<FrontendSessionCredentialHostedService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (error is not null) output.WriteLine($"Session request: {error.GetType().Name}: {error.Message}");
        }
    }
}
