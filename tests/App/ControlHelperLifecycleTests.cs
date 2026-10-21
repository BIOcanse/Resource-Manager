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
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task DisposalDuringPipeWriteFinishesCommandAndDeliversEof(bool fan, bool notification)
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rm-helper-writing-" + Guid.NewGuid().ToString("N"))).FullName;
        var dependencies = Directory.CreateDirectory(Path.Combine(root, "Dependencies")).FullName;
        var helperDirectory = Directory.CreateDirectory(Path.Combine(dependencies, fan ? "fan-control-core" : "hardware-bridge")).FullName;
        var placeholder = Path.Combine(helperDirectory, fan ? "FanControlCore.exe" : "ResourceManager.HardwareBridge.exe");
        File.WriteAllText(placeholder, "test-only factory placeholder");
        var started = Path.Combine(root, "started");
        var release = Path.Combine(root, "release");
        var finished = Path.Combine(root, "finished");
        var received = Path.Combine(root, "received");
        var description = notification ? """
            $request=[Console]::In.ReadLine() | ConvertFrom-Json
            if ($request.op -ne 'describe') { throw 'Expected capability discovery' }
            [Console]::Out.WriteLine((@{id=$request.id; ok=$true; available=$true; temperatureNotifications=$true; fans=@()} | ConvertTo-Json -Compress))
            [Console]::Out.Flush()
            """ : "";
        var script = $$"""
            $ErrorActionPreference='Stop'
            {{description}}
            [IO.File]::WriteAllText('{{started}}', 'waiting for input')
            $deadline=[DateTime]::UtcNow.AddSeconds(20)
            while (-not [IO.File]::Exists('{{release}}') -and [DateTime]::UtcNow -lt $deadline) { [Threading.Thread]::Sleep(10) }
            $line=[Console]::In.ReadLine()
            $request=$line | ConvertFrom-Json
            if ($request.op -eq 'temp') {
                if (-not $request.notification) { throw 'Expected one-way notification' }
                $name=@($request.samples.PSObject.Properties.Name)[0]
                if ($request.samples.$name -ne 49 -or $request.observedAtUtcTicks.$name -le 0) { throw 'Invalid temperature sample' }
                [IO.File]::WriteAllText('{{received}}', $name.Length.ToString())
            } else {
                [Console]::Out.WriteLine((@{id=$request.id; ok=$true; received=$request.padding.Length} | ConvertTo-Json -Compress))
                [Console]::Out.Flush()
            }
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
        IDisposable client;
        Func<Task<JsonElement?>> send;
        if (fan)
        {
            var value = new FanControlCoreClient(environment, Start); client = value;
            send = async () =>
            {
                if (!notification) return await value.SendAsync("owned-test", arguments, CancellationToken.None);
                Assert.Empty(value.Describe()); // Use the actual capability response to enable one-way input.
                var samples = new Dictionary<string, FanTemperatureSample>
                { [new string('x', 64 * 1024)] = new(49, DateTimeOffset.UtcNow.UtcTicks) };
                await value.PushTemperaturesAsync(samples, CancellationToken.None);
                return null;
            };
        }
        else
        {
            var value = new HardwareBridgeClient(environment, Start); client = value;
            send = () => value.SendAsync("owned-test", arguments, CancellationToken.None);
        }
        Task<JsonElement?>? pending = null;
        try
        {
            pending = send();
            await WaitForFile(started);
            Assert.False(pending.IsCompleted); // The reader cannot yet drain the full JSON line.
            client.Dispose();
            File.WriteAllText(release, "release");
            var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            if (!notification)
            {
                Assert.True(response!.Value.GetProperty("ok").GetBoolean());
                Assert.Equal(64 * 1024, response.Value.GetProperty("received").GetInt32());
            }
            await observer!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(File.Exists(finished));
            if (notification) Assert.Equal((64 * 1024).ToString(), File.ReadAllText(received));
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
            foreach (var file in new[] { started, release, finished, received, placeholder }) File.Delete(file);
            foreach (var directory in new[] { helperDirectory, dependencies, contentRoot, root }) Directory.Delete(directory);
        }
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task StartedCommandFinishesAfterCallerCancellationOrClientDisposal(bool fan, bool disposeDuringCommand)
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-helper-lifecycle-" + Guid.NewGuid().ToString("N"));
        var helperDirectory = Directory.CreateDirectory(Path.Combine(root, "Dependencies", fan ? "fan-control-core" : "hardware-bridge")).FullName;
        File.WriteAllText(Path.Combine(helperDirectory, fan ? "FanControlCore.exe" : "ResourceManager.HardwareBridge.exe"), "test-only factory placeholder");
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
        IDisposable client;
        Func<CancellationToken, Task<JsonElement?>> send;
        if (fan)
        {
            var value = new FanControlCoreClient(environment, Start); client = value;
            send = token => value.SendAsync("owned-test", null, token);
        }
        else
        {
            var value = new HardwareBridgeClient(environment, Start); client = value;
            send = token => value.SendAsync("owned-test", null, token);
        }
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
