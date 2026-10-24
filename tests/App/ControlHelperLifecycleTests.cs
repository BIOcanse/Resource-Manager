using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace Resource_Manager_APP.Tests;

public sealed partial class ControlHelperLifecycleTests
{
    [Fact]
    public async Task DisposalDuringPipeWriteFinishesCommandAndDeliversEof()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rm-helper-writing-" + Guid.NewGuid().ToString("N"))).FullName;
        var dependencies = Directory.CreateDirectory(Path.Combine(root, "Dependencies")).FullName;
        var helperDirectory = Directory.CreateDirectory(Path.Combine(dependencies, "hardware-bridge")).FullName;
        var placeholder = Path.Combine(helperDirectory, "ResourceManager.HardwareBridge.exe");
        File.WriteAllText(placeholder, "test-only factory placeholder");
        var started = Path.Combine(root, "started");
        var release = Path.Combine(root, "release");
        var finished = Path.Combine(root, "finished");
        var script = $$"""
            $ErrorActionPreference='Stop'
            [IO.File]::WriteAllText('{{started}}', 'waiting for input')
            $deadline=[DateTime]::UtcNow.AddSeconds(20)
            while (-not [IO.File]::Exists('{{release}}') -and [DateTime]::UtcNow -lt $deadline) { [Threading.Thread]::Sleep(10) }
            $line=[Console]::In.ReadLine()
            $request=$line | ConvertFrom-Json
            [Console]::Out.WriteLine((@{id=$request.id; ok=$true; received=$request.padding.Length} | ConvertTo-Json -Compress))
            [Console]::Out.Flush()
            if ($null -ne [Console]::In.ReadLine()) { throw 'Unexpected extra command' }
            [IO.File]::WriteAllText('{{finished}}', 'input closed')
            """;
        Process? helper = null;
        Process? observer = null;
        Process? Start(ProcessStartInfo ignored)
        {
            _ = ignored;
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive"); info.ArgumentList.Add("-EncodedCommand");
            info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            helper = Process.Start(info)!;
            observer = Process.GetProcessById(helper.Id);
            _ = observer.SafeHandle;
            return helper;
        }
        var contentRoot = Directory.CreateDirectory(Path.Combine(root, "ResourceManager-APP")).FullName;
        var environment = new EnvironmentInfo(contentRoot);
        var arguments = new Dictionary<string, object?> { ["padding"] = new string('x', 64 * 1024) };
        var client = new HardwareBridgeClient(environment, Start);
        Func<Task<JsonElement?>> send = () => client.SendAsync("owned-test", arguments, CancellationToken.None);
        Task<JsonElement?>? pending = null;
        try
        {
            pending = send();
            await WaitForFile(started);
            Assert.False(pending.IsCompleted); // The reader cannot yet drain the full JSON line.
            client.Dispose();
            File.WriteAllText(release, "release");
            var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(response!.Value.GetProperty("ok").GetBoolean());
            Assert.Equal(64 * 1024, response.Value.GetProperty("received").GetInt32());
            await observer!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(File.Exists(finished));
            Assert.Equal(0, observer.ExitCode);
        }
        finally
        {
            File.WriteAllText(release, "release");
            client.Dispose();
            if (pending is not null)
            {
                try { await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception) { } // Cleanup still owns only this injected test process.
            }
            if (observer is not null && !observer.HasExited)
            {
                try { helper!.StandardInput.Close(); } catch (InvalidOperationException) { }
                try { await observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { observer.Kill(); await observer.WaitForExitAsync(); }
            }
            observer?.Dispose();
            helper?.Dispose();
            foreach (var file in new[] { started, release, finished, placeholder }) File.Delete(file);
            foreach (var directory in new[] { helperDirectory, dependencies, contentRoot, root }) Directory.Delete(directory);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartedCommandFinishesAfterCallerCancellationOrClientDisposal(bool disposeDuringCommand)
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-helper-lifecycle-" + Guid.NewGuid().ToString("N"));
        var helperDirectory = Directory.CreateDirectory(Path.Combine(root, "Dependencies", "hardware-bridge")).FullName;
        File.WriteAllText(Path.Combine(helperDirectory, "ResourceManager.HardwareBridge.exe"), "test-only factory placeholder");
        var started = Path.Combine(root, "started"); var release = Path.Combine(root, "release"); var finished = Path.Combine(root, "finished");
        var script = $$"""
            $ErrorActionPreference='Stop'
            $line=[Console]::In.ReadLine()
            $request=$line | ConvertFrom-Json
            [IO.File]::WriteAllText('{{started}}', 'started')
            $deadline=[DateTime]::UtcNow.AddSeconds(20)
            while (-not [IO.File]::Exists('{{release}}') -and [DateTime]::UtcNow -lt $deadline) { [Threading.Thread]::Sleep(10) }
            [Console]::Out.WriteLine((@{id=$request.id; ok=$true} | ConvertTo-Json -Compress))
            [Console]::Out.Flush()
            while ($null -ne [Console]::In.ReadLine()) { }
            [IO.File]::WriteAllText('{{finished}}', 'finished')
            """;
        Process? child = null;
        var launches = 0;
        Process? Start(ProcessStartInfo ignored)
        {
            _ = ignored;
            launches++;
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive"); info.ArgumentList.Add("-EncodedCommand");
            info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            var startedProcess = Process.Start(info)!;
            child = Process.GetProcessById(startedProcess.Id);
            _ = child.SafeHandle; // Pin the independent observer before the helper exits.
            return startedProcess;
        }
        var environment = new EnvironmentInfo(Directory.CreateDirectory(Path.Combine(root, "ResourceManager-APP")).FullName);
        var client = new HardwareBridgeClient(environment, Start);
        Func<CancellationToken, Task<JsonElement?>> send = token => client.SendAsync("owned-test", null, token);
        using var cancellation = new CancellationTokenSource();
        try
        {
            var pending = send(cancellation.Token);
            await WaitForFile(started);
            if (disposeDuringCommand) client.Dispose(); else cancellation.Cancel();
            await Task.Delay(TimeSpan.FromMilliseconds(2500)); // Exceeds the former forced-kill deadline.
            Assert.False(child!.HasExited);
            Assert.False(pending.IsCompleted);
            Assert.Equal(1, launches);
            File.WriteAllText(release, "release");
            var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(response!.Value.GetProperty("ok").GetBoolean());
            client.Dispose();
            await WaitForFile(finished);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, child.ExitCode);
        }
        finally
        {
            File.WriteAllText(release, "release"); client.Dispose();
            if (child is not null)
            {
                if (!child.HasExited) await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                child.Dispose();
            }
            Directory.Delete(root, true);
        }
    }
    private static async Task WaitForFile(string path)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(path)) await Task.Delay(10, deadline.Token);
    }
    private sealed class EnvironmentInfo(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "OwnedControlHelperTest";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
