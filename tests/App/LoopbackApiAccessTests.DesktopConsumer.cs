using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.App.Infrastructure.ServiceHosting;
using ResourceManager.Shared.Runtime;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed partial class LoopbackApiAccessTests
{
    private readonly ITestOutputHelper desktopOutput;
    public LoopbackApiAccessTests(ITestOutputHelper output) => desktopOutput = output;
    [EnvironmentVariableFact("RESOURCE_MANAGER_DESKTOP_CONSUMER_TEST", "1")]
    public Task CompiledDesktopPublisherReachesTheDefaultIdentityAndStateConsumers()
        => RunDesktopConsumerAsync("desktop");

    [EnvironmentVariableFact("RESOURCE_MANAGER_DESKTOP_CONSUMER_TEST", "1")]
    public Task DefaultDesktopConsumerRejectsTheRealPublisherWithWrongCreationIdentity()
        => RunDesktopConsumerAsync("desktop-creation");

    [EnvironmentVariableFact("RESOURCE_MANAGER_DESKTOP_CONSUMER_TEST", "1")]
    public Task CompiledPublisherCanSendTheMaximumTypedDesktopPayload()
        => RunDesktopConsumerAsync("desktop-limit");

    [EnvironmentVariableFact("RESOURCE_MANAGER_DESKTOP_CONSUMER_TEST", "1")]
    public Task DesktopConsumerRejectsMoreThanTheMaximumWindowCount()
        => RunDesktopConsumerAsync("desktop-count");

    [EnvironmentVariableFact("RESOURCE_MANAGER_DESKTOP_CONSUMER_TEST", "1")]
    public Task DesktopConsumerEnforcesItsRequestByteLimit()
        => RunDesktopConsumerAsync("desktop-oversize");

    private async Task RunDesktopConsumerAsync(string mode)
    {
        var probe = Environment.GetEnvironmentVariable("RM_FRONTEND_HANDSHAKE_PROBE");
        Assert.True(File.Exists(probe), "Build FrontendSessionCredentialProbe before enabling this gate.");
        using var image = new OwnedFrontendImage(Path.GetFullPath(probe!));
        await using var fixture = await Fixture.StartAsync(actualSessionCredentials: true);
        using var identity = WindowsIdentity.GetCurrent();
        using var current = Process.GetCurrentProcess();
        Assert.True(current.SessionId > 0, "This capture test requires an interactive user session.");
        var name = "ResourceManager.Tests.Desktop." + Guid.NewGuid().ToString("N");
        using var server = WindowsLocalPipeFactory.Create(name,
            $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;{identity.User!.Value})", 4096, 4096);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var accepting = server.WaitForConnectionAsync(deadline.Token);
        using var child = Process.Start(new ProcessStartInfo(image.Path)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { name, Environment.ProcessPath!, fixture.Client.BaseAddress!.AbsoluteUri.TrimEnd('/'),
                Scope, LoopbackApiAccessToken.HeaderName, mode }
        })!;
        var errors = child.StandardError.ReadToEndAsync();
        try
        {
            await accepting;
            using var host = new FrontendSessionCredentialHostedService(new UnusedHandshakeSessions(), fixture.Tokens,
                fixture.App.Services.GetRequiredService<HostManagerRuntimeIdentity>(),
                NullLogger<FrontendSessionCredentialHostedService>.Instance);
            await host.ServeConnectionAsync(server, child.SessionId, identity.User.Value, image.Path,
                Environment.ProcessPath!, deadline.Token);
            var result = JsonSerializer.Deserialize<DesktopProbeResult>(
                (await child.StandardOutput.ReadLineAsync(deadline.Token))!)!;
            desktopOutput.WriteLine($"Published={result.Published}; HTTP={result.StatusCode}; bytes={result.RequestBytes}; windows={result.Sample.Windows.Count}; "
                + $"capture ms={result.CaptureMilliseconds}; age before send ms={result.SendAgeMilliseconds}; "
                + $"age after send ms={result.FinishedAgeMilliseconds}; publisher identity matches="
                + (child.StartTime.ToFileTimeUtc() == result.Sample.PublisherCreationFileTimeUtc)
                + $"; image matches={StringComparer.OrdinalIgnoreCase.Equals(image.Path, child.MainModule?.FileName)}");
            var boundaryPayload = mode is "desktop-limit" or "desktop-count" or "desktop-oversize";
            Assert.Equal(mode is "desktop" or "desktop-limit", result.Published);
            Assert.Equal(mode switch
                {
                    "desktop-creation" or "desktop-count" => 400,
                    "desktop-oversize" => 413,
                    _ => 200
                }, result.StatusCode);
            if (boundaryPayload)
            {
                Assert.False(result.Sample.ForegroundKnown);
                if (mode == "desktop-limit")
                {
                    Assert.Equal(InteractiveDesktopObservation.MaximumWindows, result.Sample.Windows.Count);
                    Assert.InRange(result.RequestBytes, 256 * 1024 + 1, InteractiveDesktopObservation.MaximumRequestBytes);
                }
                else if (mode == "desktop-count") Assert.Equal(InteractiveDesktopObservation.MaximumWindows + 1, result.Sample.Windows.Count);
                else Assert.True(result.RequestBytes > InteractiveDesktopObservation.MaximumRequestBytes);
            }
            else Assert.True(result.Sample.ForegroundKnown, "Enable this gate on an accessible interactive desktop.");
            Assert.NotEmpty(result.Sample.Windows);
            Assert.Equal(child.Id, result.Sample.PublisherProcessId);
            Assert.Equal(child.SessionId, result.Sample.SessionId);
            Assert.Equal(1, fixture.HttpRequests);
            Assert.Null(fixture.LastRequestError);
            Assert.Equal(0, fixture.Administrator.Calls);
            var store = fixture.App.Services.GetRequiredService<InteractiveDesktopObservationStore>();
            // Boundary cases test framing only, not synthetic process ownership.
            var expected = boundaryPayload ? Array.Empty<ProcessInstanceKey>() : result.Sample.Windows
                .Select(window => new ProcessInstanceKey(window.ProcessId, window.CreationFileTimeUtc))
                .Append(new(result.Sample.Foreground!.ProcessId, result.Sample.Foreground.CreationFileTimeUtc)).Distinct().ToArray();
            var projected = WindowsProcessRuntimeStateSnapshot.Capture(expected, store);
            desktopOutput.WriteLine($"Published={result.Published}; captured windows={result.Sample.Windows.Count}; "
                + $"complete={result.Sample.Complete}; foreground known={result.Sample.ForegroundKnown}; "
                + $"projected status={projected.Status}; projected processes={projected.Processes.Count}");
            if (!result.Published || !result.Sample.ForegroundKnown) Assert.Empty(projected.Processes);
            else
            {
                Assert.NotEmpty(projected.Processes);
                Assert.True(projected.Processes[new(result.Sample.Foreground!.ProcessId,
                    result.Sample.Foreground.CreationFileTimeUtc)].ForegroundFocused);
                foreach (var (key, fact) in projected.Processes)
                    Assert.Equal(result.Sample.Foreground!.ProcessId == key.ProcessId, fact.ForegroundFocused);
            }
            await child.StandardInput.WriteLineAsync("exit");
            await child.StandardInput.FlushAsync();
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

    private sealed record DesktopProbeResult(bool Published, InteractiveDesktopObservation Sample,
        double CaptureMilliseconds, double SendAgeMilliseconds, double FinishedAgeMilliseconds, int RequestBytes, int StatusCode);

    private sealed class OwnedFrontendImage : IDisposable
    {
        private readonly string directory;
        private readonly List<string> files = [];
        public string Path { get; }
        public OwnedFrontendImage(string source)
        {
            directory = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "ResourceManagerNativeUi"));
            Path = System.IO.Path.Combine(directory, "ResourceManager.NativeUi.exe");
            Assert.Equal(Path, NativeUiLaunchHostedService.ResolveNativeUiPath());
            Assert.False(Directory.Exists(directory), "Never overwrite an existing frontend output directory.");
            Directory.CreateDirectory(directory);
            try
            {
                foreach (var file in Directory.EnumerateFiles(System.IO.Path.GetDirectoryName(source)!))
                {
                    var destination = file.Equals(source, StringComparison.OrdinalIgnoreCase)
                        ? Path : System.IO.Path.Combine(directory, System.IO.Path.GetFileName(file));
                    if (destination.Equals(Path, StringComparison.OrdinalIgnoreCase)
                        && !file.Equals(source, StringComparison.OrdinalIgnoreCase)) continue;
                    files.Add(destination);
                    File.Copy(file, destination, overwrite: false);
                }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            foreach (var file in files) File.Delete(file);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }
}
