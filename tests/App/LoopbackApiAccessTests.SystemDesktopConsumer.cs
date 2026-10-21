using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.App.Infrastructure.ServiceHosting;

namespace Resource_Manager_APP.Tests;

public sealed partial class LoopbackApiAccessTests
{
    [EnvironmentVariableFact("RESOURCE_MANAGER_SYSTEM_DESKTOP_TEST", "1")]
    public Task SystemSessionLaunchPublishesUserDesktopWithTheDefaultConsumers()
        => RunSystemDesktopConsumerAsync("desktop");

    [EnvironmentVariableFact("RESOURCE_MANAGER_SYSTEM_DESKTOP_TEST", "1")]
    public Task SystemSessionRejectsWrongFrontendCreationIdentityFromTheRealUser()
        => RunSystemDesktopConsumerAsync("desktop-creation");

    private async Task RunSystemDesktopConsumerAsync(string mode)
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var current = Process.GetCurrentProcess();
        Assert.True(identity.IsSystem, "Run only through the finite owned SYSTEM test helper.");
        Assert.Equal(0, current.SessionId);
        WindowsBackendProcessIdentityAccess.InitializeForCurrentSystemProcess();
        var session = uint.Parse(Environment.GetEnvironmentVariable("RM_SYSTEM_TARGET_SESSION")!);
        var expectedSid = new SecurityIdentifier(Environment.GetEnvironmentVariable("RM_SYSTEM_TARGET_SID")!).Value;
        Assert.NotEqual(identity.User!.Value, expectedSid);
        var broker = new WindowsInteractiveUserSessionBroker();
        Assert.Contains(session, broker.GetActiveSessionIds());
        Assert.Equal(expectedSid, broker.GetUserSid(session));
        var probe = Environment.GetEnvironmentVariable("RM_SYSTEM_FRONTEND_PROBE");
        Assert.True(File.Exists(probe), "Build the no-console FrontendDesktopSessionProbe first.");
        using var image = new OwnedFrontendImage(Path.GetFullPath(probe!));
        await using var fixture = await Fixture.StartAsync(actualSessionCredentials: true);
        var name = "ResourceManager.Tests.SystemDesktop." + Guid.NewGuid().ToString("N");
        var descriptor = $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;{expectedSid})";
        using var credentials = WindowsLocalPipeFactory.Create(name, descriptor, 4096, 4096);
        using var diagnostics = WindowsLocalPipeFactory.Create(name + ".report", descriptor, 4096, 4096);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var acceptCredentials = credentials.WaitForConnectionAsync(deadline.Token);
        var acceptDiagnostics = diagnostics.WaitForConnectionAsync(deadline.Token);
        var arguments = string.Join(" ", new[] { name, Environment.ProcessPath!,
            fixture.Client.BaseAddress!.AbsoluteUri.TrimEnd('/'), Scope, LoopbackApiAccessToken.HeaderName,
            mode, name + ".report" }.Select(value => $"\"{value}\""));
        // The existing user-shell gate must reject a changed SID before creating any process.
        Assert.Throws<UnauthorizedAccessException>(() => broker.LaunchUserShell(session, identity.User.Value,
            image.Path, arguments));
        using var child = Process.GetProcessById(checked((int)(mode == "desktop"
            ? broker.LaunchNativeUi(session, image.Path, arguments)
            : broker.LaunchUserShell(session, expectedSid, image.Path, arguments))));
        _ = child.SafeHandle;
        try
        {
            Assert.Equal(checked((int)session), child.SessionId);
            Assert.Equal(image.Path, child.MainModule!.FileName, ignoreCase: true);
            Assert.Equal(NativeUiProcessPresence.Expected, broker.GetNativeUiProcessPresence(session, image.Path));
            var connected = Task.WhenAll(acceptDiagnostics, acceptCredentials);
            var exited = child.WaitForExitAsync(deadline.Token);
            await Task.WhenAny(connected, exited).WaitAsync(deadline.Token);
            Assert.False(child.HasExited, child.HasExited ? $"Owned frontend exited before connecting: {child.ExitCode}" : null);
            await connected;
            using var host = new FrontendSessionCredentialHostedService(new UnusedHandshakeSessions(), fixture.Tokens,
                fixture.App.Services.GetRequiredService<HostManagerRuntimeIdentity>(),
                new DesktopCredentialLogger(desktopOutput));
            await host.ServeConnectionAsync(credentials, checked((int)session), expectedSid, image.Path,
                Environment.ProcessPath!, deadline.Token);
            using var reader = new StreamReader(diagnostics, leaveOpen: true);
            using var writer = new StreamWriter(diagnostics, leaveOpen: true) { AutoFlush = true };
            var report = await reader.ReadLineAsync(deadline.Token);
            Assert.True(report?.StartsWith('{') == true, "Compiled frontend credential result: " + report);
            var result = JsonSerializer.Deserialize<DesktopProbeResult>(report!)!;
            using var reportData = JsonDocument.Parse(report!);
            var rights = reportData.RootElement.GetProperty("BackendRights");
            Assert.False(rights.GetProperty("FrontendIsSystem").GetBoolean());
            Assert.False(rights.GetProperty("FrontendIsAdministrator").GetBoolean());
            foreach (var property in new[] { "TerminationDenied", "VmReadDenied", "VmWriteDenied", "DaclWriteDenied" })
                Assert.True(rights.GetProperty(property).GetBoolean(), property);
            desktopOutput.WriteLine("Standard frontend: terminate, VM read/write and DACL write remain denied.");
            desktopOutput.WriteLine($"backend SYSTEM={identity.IsSystem}, session={current.SessionId}; "
                + $"frontend session={result.Sample.SessionId}; HTTP={result.StatusCode}; "
                + $"windows={result.Sample.Windows.Count}; foreground known={result.Sample.ForegroundKnown}");
            Assert.Equal(mode == "desktop", result.Published);
            Assert.Equal(mode == "desktop" ? 200 : 400, result.StatusCode);
            Assert.Equal(child.Id, result.Sample.PublisherProcessId);
            Assert.Equal(child.SessionId, result.Sample.SessionId);
            Assert.True(result.Sample.ForegroundKnown);
            Assert.NotEmpty(result.Sample.Windows);
            Assert.Equal(1, fixture.HttpRequests);
            Assert.Null(fixture.LastRequestError);
            Assert.Equal(0, fixture.Administrator.Calls);
            var foreground = result.Sample.Foreground!;
            var expected = result.Sample.Windows.Select(window => new ProcessInstanceKey(window.ProcessId, window.CreationFileTimeUtc))
                .Append(new(foreground.ProcessId, foreground.CreationFileTimeUtc)).Distinct().ToArray();
            var projected = WindowsProcessRuntimeStateSnapshot.Capture(expected,
                fixture.App.Services.GetRequiredService<InteractiveDesktopObservationStore>());
            if (mode == "desktop")
            {
                Assert.True(projected.Processes[new(foreground.ProcessId, foreground.CreationFileTimeUtc)].ForegroundFocused);
                foreach (var (key, fact) in projected.Processes)
                    Assert.Equal(foreground.ProcessId == key.ProcessId, fact.ForegroundFocused);
            }
            else Assert.Empty(projected.Processes);
            desktopOutput.WriteLine($"projected status={projected.Status}; processes={projected.Processes.Count}");
            await writer.WriteLineAsync("exit");
            await child.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, child.ExitCode);
        }
        finally
        {
            diagnostics.Dispose();
            if (!child.HasExited)
            {
                try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { child.Kill(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            }
        }
    }

    private sealed class DesktopCredentialLogger(Xunit.Abstractions.ITestOutputHelper output) : ILogger<FrontendSessionCredentialHostedService>
    {
        private int count;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null && Interlocked.Increment(ref count) <= 8)
                output.WriteLine($"Credential service: {exception.GetType().Name}: {exception.Message}");
        }
    }
}
